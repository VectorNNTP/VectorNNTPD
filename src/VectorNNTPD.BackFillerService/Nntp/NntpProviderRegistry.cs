using System.Collections.Concurrent;
using VectorNNTPD.BackFillerService.Accounts;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Configuration;

namespace VectorNNTPD.BackFillerService.Nntp
{
    /// <summary>
    /// Owns one <see cref="NntpSessionPool"/> per configured provider with MaxSessions &gt; 0.
    /// Eagerly establishes MaxSessions NNTP slots and publishes usable capacity.
    /// </summary>
    /// <remarks>
    /// Pools are keyed by backbone with ordinal ignore-case comparison.
    /// <see cref="StartAsync"/> fills pools from the current catalog.
    /// <see cref="ApplySnapshotAsync"/> keeps a pool whose provider record is unchanged, rebinds a max-sessions shrink, and otherwise replaces the pool.
    /// </remarks>
    internal sealed class NntpProviderRegistry : IHostedService, IAsyncDisposable
    {
        /// <summary>Catalog consulted by <see cref="TryGetPool"/> and <see cref="PublishCapacity"/>.</summary>
        private readonly IBackFillerProviderCatalog _catalog;

        /// <summary>Transport passed to every pool this registry creates.</summary>
        private readonly INntpTransportFactory _transport;

        /// <summary>Session options passed to every pool this registry creates.</summary>
        private readonly NntpSessionOptions _options;

        /// <summary>
        /// Absolute budget for one <see cref="StopAsync"/> or <see cref="DisposeAsync"/>.
        /// Captured once as an <see cref="NntpShutdownDeadline"/> and shared by every pool in that call.
        /// </summary>
        private readonly TimeSpan _shutdownGrace;

        /// <summary>
        /// Application-wide establishment concurrency gate shared by every pool this registry creates.
        /// </summary>
        private readonly ProviderSessionEstablishmentGate _establishment;

        /// <summary>Logger for capacity publication and consumer-reconcile failures.</summary>
        private readonly ILogger<NntpProviderRegistry> _logger;

        /// <summary>Snapshot replaced by <see cref="PublishCapacity"/> and cleared on dispose.</summary>
        private readonly BackboneUsableCapacityState _capacity;

        /// <summary>Test-supplied reconciler. When null, <see cref="Consumers"/> resolves one from <see cref="_services"/>.</summary>
        private readonly IArticleWorkConsumerReconciliation? _consumers;

        /// <summary>Host provider used to resolve consumers after construction. Null when tests pass <see cref="_consumers"/> directly.</summary>
        private readonly IServiceProvider? _services;

        /// <summary>Live pools keyed by backbone, ordinal ignore-case.</summary>
        private readonly ConcurrentDictionary<string, NntpSessionPool> _pools = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Serializes pool-map edits, catalog publication, and the provider list read for capacity.</summary>
        private readonly object _gate = new();

        /// <summary>Zero while the registry accepts work. Set to 1 by the first <see cref="DisposeCoreAsync"/>.</summary>
        private int _disposed;

        /// <summary>
        /// Initializes the registry with <see cref="NntpSessionOptions.Default"/> and <see cref="BackFillerShutdownRuntimeOptions.GracePeriod"/>.
        /// </summary>
        /// <param name="catalog">Current provider snapshot.</param>
        /// <param name="transport">NNTP transport factory.</param>
        /// <param name="runtime">Validated runtime snapshot.</param>
        /// <param name="establishment">Application-wide provider-session establishment gate.</param>
        /// <param name="logger">Registry logger.</param>
        /// <param name="capacity">Usable-capacity publisher.</param>
        /// <param name="services">
        /// Host service provider used to resolve consumers after construction.
        /// Avoids a constructor cycle with <see cref="IArticleWorkConsumerReconciliation"/>.
        /// </param>
        internal NntpProviderRegistry(
            IBackFillerProviderCatalog catalog,
            INntpTransportFactory transport,
            BackFillerRuntimeOptions runtime,
            ProviderSessionEstablishmentGate establishment,
            ILogger<NntpProviderRegistry> logger,
            BackboneUsableCapacityState capacity,
            IServiceProvider? services = null)
            : this(
                catalog,
                transport,
                NntpSessionOptions.Default with
                {
                    TcpSendBufferBytes = runtime.Nntp.SendBufferBytes,
                    TcpReceiveBufferBytes = runtime.Nntp.ReceiveBufferBytes,
                    TcpKeepAlive = runtime.TcpKeepAlive,
                },
                runtime.Shutdown.GracePeriod,
                establishment,
                logger,
                capacity,
                consumers: null,
                services)
        {
        }

        /// <summary>Initializes the registry with explicit session options (tests).</summary>
        /// <param name="catalog">Current provider snapshot.</param>
        /// <param name="transport">NNTP transport factory.</param>
        /// <param name="options">Per-session NNTP options.</param>
        /// <param name="shutdownGrace">
        /// Absolute budget for one <see cref="StopAsync"/> or <see cref="DisposeAsync"/>.
        /// Captured once and shared by every pool's lease wait and QUIT. Replacement drain does not use it as a second deadline.
        /// </param>
        /// <param name="establishment">
        /// Establishment gate shared by every pool this registry creates. When null, this
        /// constructor allocates a private gate at
        /// <see cref="Configuration.BackFillerNntpOptions.DefaultMaxConcurrentSessionEstablishments"/>.
        /// </param>
        /// <remarks>
        /// The BackFiller host does not call this constructor. <c>AddBackFillerHosting</c> uses the
        /// runtime overload and injects the single <see cref="ProviderSessionEstablishmentGate"/>
        /// singleton, so every production pool shares that gate.
        /// The private gate exists so existing test calls can keep their positional
        /// <see cref="TimeSpan"/> argument. It bounds only the pools of this registry. A second
        /// registry constructed the same way has its own gate and can establish in parallel.
        /// That does not add a second gate to the host process, which constructs one registry.
        /// </remarks>
        /// <param name="logger">Registry logger.</param>
        /// <param name="capacity">Usable-capacity publisher. Created when omitted.</param>
        /// <param name="consumers">Optional consumer reconciler for tests.</param>
        /// <param name="services">Optional host service provider for deferred consumer resolve.</param>
        internal NntpProviderRegistry(
            IBackFillerProviderCatalog catalog,
            INntpTransportFactory transport,
            NntpSessionOptions options,
            TimeSpan shutdownGrace,
            ILogger<NntpProviderRegistry> logger,
            BackboneUsableCapacityState? capacity = null,
            IArticleWorkConsumerReconciliation? consumers = null,
            IServiceProvider? services = null,
            ProviderSessionEstablishmentGate? establishment = null)
            : this(
                catalog,
                transport,
                options,
                shutdownGrace,
                establishment
                ?? new ProviderSessionEstablishmentGate(
                    Configuration.BackFillerNntpOptions.DefaultMaxConcurrentSessionEstablishments),
                logger,
                capacity,
                consumers,
                services)
        {
        }

        /// <summary>Initializes the registry with an explicit shared establishment gate.</summary>
        /// <param name="catalog">Current provider snapshot.</param>
        /// <param name="transport">NNTP transport factory.</param>
        /// <param name="options">Per-session NNTP options.</param>
        /// <param name="shutdownGrace">
        /// Absolute budget for one <see cref="StopAsync"/> or <see cref="DisposeAsync"/>.
        /// Captured once and shared by every pool's lease wait and QUIT. Replacement drain does not use it as a second deadline.
        /// </param>
        /// <param name="establishment">Application-wide establishment gate shared by every pool.</param>
        /// <param name="logger">Registry logger.</param>
        /// <param name="capacity">Usable-capacity publisher. Created when omitted.</param>
        /// <param name="consumers">Optional consumer reconciler for tests.</param>
        /// <param name="services">Optional host service provider for deferred consumer resolve.</param>
        private NntpProviderRegistry(
            IBackFillerProviderCatalog catalog,
            INntpTransportFactory transport,
            NntpSessionOptions options,
            TimeSpan shutdownGrace,
            ProviderSessionEstablishmentGate establishment,
            ILogger<NntpProviderRegistry> logger,
            BackboneUsableCapacityState? capacity = null,
            IArticleWorkConsumerReconciliation? consumers = null,
            IServiceProvider? services = null)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            ArgumentNullException.ThrowIfNull(transport);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(establishment);
            ArgumentNullException.ThrowIfNull(logger);
            _catalog = catalog;
            _transport = transport;
            _options = options;
            _shutdownGrace = shutdownGrace;
            _establishment = establishment;
            _logger = logger;
            _capacity = capacity ?? new BackboneUsableCapacityState();
            _consumers = consumers;
            _services = services;
        }

        /// <summary>
        /// Gets the reconciler passed at construction, or resolves one from <see cref="_services"/> when that argument was omitted.
        /// </summary>
        /// <remarks>The service lookup is not cached. The result is null when neither source has a reconciler.</remarks>
        private IArticleWorkConsumerReconciliation? Consumers =>
            _consumers ?? _services?.GetService<IArticleWorkConsumerReconciliation>();

        /// <summary>Gets the usable-capacity snapshot published by this registry.</summary>
        private BackboneUsableCapacityState Capacity => _capacity;

        /// <summary>
        /// Returns the live pool for <paramref name="backbone"/> when it matches the catalog provider, creating that pool on first use.
        /// </summary>
        /// <param name="backbone">Backbone key, compared ordinal-ignore-case.</param>
        /// <param name="pool">The matching pool when this method returns <see langword="true"/>; otherwise null.</param>
        /// <returns>
        /// <see langword="false"/> when the registry is disposed, the catalog has no such provider,
        /// <see cref="BackFillerProviderDefinition.MaxSessions"/> is not positive, or a pool is already stored whose bound snapshot is not equal to the catalog provider.
        /// </returns>
        /// <exception cref="ArgumentException"><paramref name="backbone"/> is null or whitespace.</exception>
        /// <remarks>
        /// A mismatched pool is left in <see cref="_pools"/>. <see cref="ApplySnapshotAsync"/> is the path that replaces it.
        /// </remarks>
        internal bool TryGetPool(string backbone, out NntpSessionPool pool)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(backbone);
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    pool = null!;
                    return false;
                }

                if (!_catalog.TryGetProvider(backbone, out var provider) || provider.MaxSessions <= 0)
                {
                    pool = null!;
                    return false;
                }

                if (_pools.TryGetValue(backbone, out pool!))
                {
                    if (pool.Provider == provider)
                    {
                        return true;
                    }

                    pool = null!;
                    return false;
                }

                pool = CreatePool(provider);
                _pools[provider.Backbone] = pool;
                return true;
            }
        }

        /// <summary>
        /// Publishes <paramref name="providers"/> and reconciles pools. Unchanged providers keep their pool.
        /// A MaxSessions-only shrink keeps the pool and retires consumers above the new limit first.
        /// A MaxSessions-only increase keeps the pool, restores acquire permits, and connects the added slots.
        /// Consumers for removed or replaced providers are retired before NNTP teardown.
        /// </summary>
        /// <param name="providers">
        /// Desired provider set. When <see cref="_catalog"/> is a <see cref="ProviderConfigurationCatalog"/>, it is published under <see cref="_gate"/> before the pool map changes.
        /// Other catalog implementations are not updated.
        /// </param>
        /// <param name="cancellationToken">
        /// Cancels consumer retirement, pool shrink, pool drain, and warming connects.
        /// Cancellation after a replacement pool has been inserted leaves that pool in the map.
        /// </param>
        /// <remarks>
        /// A disposed registry returns without changing pools. After shrinks, in-place growth, and removals, new and replacement pools are connected, capacity is published, and a running consumer reconciler is asked to reconcile.
        /// Consumer retirement is skipped when <see cref="Consumers"/> is missing or not running.
        /// Each replaced pool waits for its own leases on <paramref name="cancellationToken"/> and then uses one retirement grace for its sessions.
        /// That grace is not the deadline captured by <see cref="StopAsync"/> or <see cref="DisposeAsync"/>, and pools replaced by the same snapshot do not share it, because one pool's lease wait must not expire another pool's QUIT budget.
        /// </remarks>
        internal async Task ApplySnapshotAsync(
            IReadOnlyList<BackFillerProviderDefinition> providers,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(providers);
            var retiring = new List<NntpSessionPool>();
            var shrinking = new List<NntpSessionPool>();
            var growing = new List<NntpSessionPool>();
            var warming = new List<NntpSessionPool>();
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    return;
                }

                if (_catalog is ProviderConfigurationCatalog live)
                {
                    live.Publish(providers);
                }

                var desired = new Dictionary<string, BackFillerProviderDefinition>(StringComparer.OrdinalIgnoreCase);
                foreach (var provider in providers)
                {
                    desired[provider.Backbone] = provider;
                }

                foreach (var pair in _pools.ToArray())
                {
                    if (!desired.TryGetValue(pair.Key, out var next) || next.MaxSessions <= 0)
                    {
                        if (_pools.TryRemove(pair.Key, out var removed))
                        {
                            Unhook(removed);
                            retiring.Add(removed);
                        }

                        continue;
                    }

                    if (pair.Value.Provider == next)
                    {
                        continue;
                    }

                    if (next.IsMaxSessionsShrinkOf(pair.Value.Provider))
                    {
                        pair.Value.BindProvider(next);
                        shrinking.Add(pair.Value);
                        continue;
                    }

                    if (next.IsMaxSessionsGrowthOf(pair.Value.Provider))
                    {
                        pair.Value.BindProvider(next);
                        growing.Add(pair.Value);
                        continue;
                    }

                    if (_pools.TryRemove(pair.Key, out var replaced))
                    {
                        Unhook(replaced);
                        retiring.Add(replaced);
                    }

                    if (next.MaxSessions > 0)
                    {
                        var created = CreatePool(next);
                        _pools[next.Backbone] = created;
                        warming.Add(created);
                    }
                }

                foreach (var provider in desired.Values)
                {
                    if (provider.MaxSessions <= 0 || _pools.ContainsKey(provider.Backbone))
                    {
                        continue;
                    }

                    var created = CreatePool(provider);
                    _pools[provider.Backbone] = created;
                    warming.Add(created);
                }
            }

            if (Consumers is { IsRunning: true } retiringConsumers)
            {
                foreach (var pool in shrinking)
                {
                    await retiringConsumers.RetireCapacityAsync(pool.Backbone, pool.Provider.MaxSessions, cancellationToken)
                        .ConfigureAwait(false);
                }

                foreach (var pool in retiring)
                {
                    await retiringConsumers.RetireCapacityAsync(pool.Backbone, 0, cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var pool in shrinking)
            {
                await pool.ShrinkToBoundAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var pool in growing)
            {
                pool.GrowToBound();
                await pool.EnsureDesiredSessionsAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var pool in retiring)
            {
                await pool.DrainAndDisposeAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            foreach (var pool in warming)
            {
                await pool.EnsureDesiredSessionsAsync(cancellationToken).ConfigureAwait(false);
            }

            PublishCapacity();
            if (Consumers is { IsRunning: true } reconcile)
            {
                await reconcile.ReconcileAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Opens a pool for every catalog provider with a positive <see cref="BackFillerProviderDefinition.MaxSessions"/> and connects that pool's desired sessions.
        /// </summary>
        /// <param name="cancellationToken">
        /// Cancels session establishment. Pools already created stay registered. Capacity is published only when the loop finishes.
        /// </param>
        /// <remarks>
        /// Does not reconcile article-work consumers.
        /// A catalog provider whose stored pool snapshot does not match is skipped because <see cref="TryGetPool"/> returns false.
        /// </remarks>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            NntpLogMessages.UpstreamSocketOptions(
                _logger,
                _options.TcpSendBufferBytes,
                _options.TcpReceiveBufferBytes);
            foreach (var provider in _catalog.Providers)
            {
                if (provider.MaxSessions <= 0)
                {
                    continue;
                }

                if (!TryGetPool(provider.Backbone, out var pool))
                {
                    continue;
                }

                await pool.EnsureDesiredSessionsAsync(cancellationToken).ConfigureAwait(false);
            }

            PublishCapacity();
        }

        /// <summary>Drains and disposes every pool under one shared grace budget.</summary>
        /// <param name="cancellationToken">Ends the drain wait and an in-flight QUIT. The grace timer is the deadline captured below, not a second clock.</param>
        /// <remarks>
        /// One <see cref="NntpShutdownDeadline"/> is captured here and passed to every pool.
        /// Pools are not given a fresh <see cref="BackFillerShutdownRuntimeOptions.GracePeriod"/> or a fresh per-session QUIT allowance.
        /// When the deadline expires, a session that has not started QUIT aborts its transport.
        /// Pools run with <see cref="NntpShutdownWork.Parallelism"/> so one slow pool does not keep the others from starting.
        /// </remarks>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            using var deadline = NntpShutdownDeadline.FromBudget(_shutdownGrace);
            await DisposeCoreAsync(cancellationToken, deadline).ConfigureAwait(false);
        }

        /// <summary>Drains and disposes every pool, using only <see cref="_shutdownGrace"/> as the shutdown budget.</summary>
        /// <remarks>
        /// The host cancellation token is not observed; <see cref="StopAsync"/> is the path that links caller cancellation.
        /// The deadline is captured once and covers every pool's lease wait and QUIT.
        /// A second call returns immediately from <see cref="DisposeCoreAsync"/>.
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            using var deadline = NntpShutdownDeadline.FromBudget(_shutdownGrace);
            await DisposeCoreAsync(CancellationToken.None, deadline).ConfigureAwait(false);
        }

        /// <summary>Creates a pool for <paramref name="provider"/> and subscribes to its active-session changes.</summary>
        /// <param name="provider">Provider definition stored on the new pool.</param>
        /// <returns>The hooked pool. The caller inserts it into <see cref="_pools"/>.</returns>
        private NntpSessionPool CreatePool(BackFillerProviderDefinition provider)
        {
            var pool = new NntpSessionPool(provider, _options, _transport, _logger, _establishment, _shutdownGrace);
            pool.ActiveSessionCountChanged += OnPoolActiveSessionCountChanged;
            return pool;
        }

        /// <summary>Removes the active-session handler from <paramref name="pool"/> before the pool is drained.</summary>
        /// <param name="pool">Pool that is leaving the registry.</param>
        private void Unhook(NntpSessionPool pool)
        {
            pool.ActiveSessionCountChanged -= OnPoolActiveSessionCountChanged;
        }

        /// <summary>
        /// Publishes capacity and, when consumers are running, starts a reconcile that is not tied to the caller's cancellation token.
        /// </summary>
        /// <remarks>
        /// Returns immediately when the registry is disposed or consumers are not running.
        /// The reconcile task is not observed here; its failures are logged by <see cref="ReconcileConsumersObservedAsync"/>.
        /// </remarks>
        private void OnPoolActiveSessionCountChanged()
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            PublishCapacity();
            if (Consumers is not { IsRunning: true })
            {
                return;
            }

            _ = ReconcileConsumersObservedAsync();
        }

        /// <summary>Runs <see cref="IArticleWorkConsumerReconciliation.ReconcileAsync"/> with <see cref="CancellationToken.None"/>.</summary>
        /// <remarks>
        /// Exceptions are logged through <see cref="NntpLogMessages.SessionReplenishFailed"/> with backbone text <c>consumers</c> and are not rethrown.
        /// </remarks>
        private async Task ReconcileConsumersObservedAsync()
        {
            try
            {
                await Consumers!.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                NntpLogMessages.SessionReplenishFailed(_logger, "consumers", ex.Message);
            }
            // codeql[cs/catch-of-all-exceptions] An observed reconcile task must log every failure, including a failure of that log.
            catch (Exception ex)
            {
                try
                {
                    NntpLogMessages.SessionReplenishFailed(_logger, "consumers", ex.Message);
                }
                // codeql[cs/catch-of-all-exceptions] An observed reconcile task must log every failure, including a failure of that log.
                catch (Exception logFailure)
                {
                    try
                    {
                        Serilog.Debugging.SelfLog.WriteLine(
                            "NNTP consumer reconcile failure log failed: {0}",
                            logFailure);
                    }
                    // codeql[cs/catch-of-all-exceptions] An observed reconcile task must log every failure, including a failure of that log.
                    catch (Exception)
                    {
                        // The reconcile task is unobserved. Its failure must not become an unhandled task exception.
                    }
                }
            }
        }

        /// <summary>
        /// Publishes one usable-session count per catalog provider and logs how many of those counts are positive.
        /// </summary>
        /// <remarks>
        /// Providers with <see cref="BackFillerProviderDefinition.MaxSessions"/> below 1, and providers with no pool, are published as zero.
        /// The count is <see cref="NntpSessionPool.ActiveSessionCount"/>, not the configured maximum.
        /// </remarks>
        private void PublishCapacity()
        {
            var snapshot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<BackFillerProviderDefinition> providers;
            lock (_gate)
            {
                providers = _catalog.Providers;
                foreach (var provider in providers)
                {
                    if (provider.MaxSessions <= 0)
                    {
                        snapshot[provider.Backbone] = 0;
                        continue;
                    }

                    snapshot[provider.Backbone] = _pools.TryGetValue(provider.Backbone, out var pool)
                        ? pool.ActiveSessionCount
                        : 0;
                }
            }

            _capacity.PublishSnapshot(snapshot);
            NntpLogMessages.UsableCapacityPublished(_logger, snapshot.Count(static pair => pair.Value > 0));
        }

        /// <summary>
        /// Marks the registry disposed, drains each pool under <paramref name="deadline"/>, and publishes an empty capacity snapshot.
        /// </summary>
        /// <param name="cancellationToken">
        /// Passed to each <see cref="NntpSessionPool.DrainAndDisposeAsync(CancellationToken, NntpShutdownDeadline)"/>.
        /// A cancelled wait still retires that pool's remaining sessions.
        /// </param>
        /// <param name="deadline">The instant captured by <see cref="StopAsync"/> or <see cref="DisposeAsync"/>. Not recreated per pool.</param>
        /// <remarks>
        /// The first caller clears <see cref="_pools"/>. A concurrent or later call returns without draining again.
        /// Pools are unhooked before any drain so active-session notifications cannot re-enter the registry during teardown.
        /// A pool fault does not skip the other pools. After every pool has been attempted, those faults are thrown and the empty capacity snapshot is still published.
        /// </remarks>
        private async Task DisposeCoreAsync(CancellationToken cancellationToken, NntpShutdownDeadline deadline)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            List<NntpSessionPool> pools;
            lock (_gate)
            {
                pools = [.. _pools.Values];
                _pools.Clear();
            }

            foreach (var pool in pools)
            {
                Unhook(pool);
            }

            try
            {
                await NntpShutdownWork.RunBoundedAsync(
                    pools,
                    pool => pool.DrainAndDisposeAsync(cancellationToken, deadline)).ConfigureAwait(false);
            }
            finally
            {
                _capacity.PublishSnapshot(new Dictionary<string, int>());
            }
        }
    }
}

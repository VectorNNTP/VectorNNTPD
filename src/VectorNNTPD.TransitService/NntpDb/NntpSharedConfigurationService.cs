using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.TransitService.Configuration;

namespace VectorNNTPD.TransitService.NntpDb
{
    /// <summary>
    /// Loads <c>nntpsharedconfig</c> during startup and refreshes it on this service's own loop.
    /// </summary>
    /// <remarks>
    /// The initial load fails startup. A later refresh failure keeps the last known-good snapshot.
    /// One refresh runs at a time. This service does not query on an article path.
    /// </remarks>
    internal sealed class NntpSharedConfigurationService : INntpSharedConfigurationCatalogue, INntpArticlePolicySource, IApplicationService, IAsyncDisposable
    {
        /// <summary>Refresh interval after a successful initial load. Owned by this service, not by a shared scheduler.</summary>
        public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

        private readonly NntpDbService _nntpDb;
        private readonly IOptions<NntpdOptions> _options;
        private readonly IOptions<RabbitMqOptions> _rabbitMq;
        private readonly ILogger<NntpSharedConfigurationService> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _interval;
        private readonly CancellationTokenSource _runCts = new();
        private PublishedSnapshot? _current;
        private Task? _execution;
        private int _started;
        private int _refreshing;
        private int _disposed;

        /// <summary>Initializes a new instance of the <see cref="NntpSharedConfigurationService"/> class.</summary>
        /// <param name="nntpDb">Started database service.</param>
        /// <param name="options">Live NNTPD options. ACME, DNS, and Cloudflare credential fields are replaced from each published snapshot.</param>
        /// <param name="rabbitMq">Live RabbitMQ options. Username and password are replaced from each published snapshot.</param>
        /// <param name="logger">Lifecycle logger.</param>
        public NntpSharedConfigurationService(
            NntpDbService nntpDb,
            IOptions<NntpdOptions> options,
            IOptions<RabbitMqOptions> rabbitMq,
            ILogger<NntpSharedConfigurationService> logger)
            : this(nntpDb, options, logger, TimeProvider.System, RefreshInterval, rabbitMq)
        {
        }

        /// <summary>Initializes a new instance with an explicit clock and interval (tests).</summary>
        /// <param name="nntpDb">Started database service.</param>
        /// <param name="options">Live NNTPD options. ACME, DNS, and Cloudflare credential fields are replaced from each published snapshot.</param>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="timeProvider">Clock used by the refresh delay.</param>
        /// <param name="interval">Delay between refreshes.</param>
        /// <param name="rabbitMq">Live RabbitMQ options. Username and password are replaced from each published snapshot.</param>
        internal NntpSharedConfigurationService(
            NntpDbService nntpDb,
            IOptions<NntpdOptions> options,
            ILogger<NntpSharedConfigurationService> logger,
            TimeProvider timeProvider,
            TimeSpan interval,
            IOptions<RabbitMqOptions>? rabbitMq = null)
        {
            ArgumentNullException.ThrowIfNull(nntpDb);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
            _nntpDb = nntpDb;
            _options = options;
            _rabbitMq = rabbitMq ?? Options.Create(new RabbitMqOptions());
            _logger = logger;
            _timeProvider = timeProvider;
            _interval = interval;
        }

        /// <inheritdoc />
        public string Name => "NntpSharedConfiguration";

        /// <inheritdoc />
        public Task? Execution => _execution;

        /// <inheritdoc />
        public bool TryGetArticlePolicy(out int maxArticleBytes, out string siteName)
        {
            var snapshot = Volatile.Read(ref _current);
            if (snapshot is null)
            {
                maxArticleBytes = 0;
                siteName = string.Empty;
                return false;
            }

            maxArticleBytes = snapshot.Value.MaxArticleBytes;
            siteName = snapshot.Value.SiteName;
            return true;
        }

        /// <inheritdoc />
        public NntpSharedConfiguration Current
        {
            get
            {
                var snapshot = Volatile.Read(ref _current);
                return snapshot?.Value ?? throw new InvalidOperationException("nntpsharedconfig has not been published.");
            }
        }

        private sealed class PublishedSnapshot
        {
            internal PublishedSnapshot(NntpSharedConfiguration value) => Value = value;

            internal NntpSharedConfiguration Value { get; }
        }

        /// <summary>Gets whether a snapshot has been published (tests).</summary>
        internal bool HasSnapshot => Volatile.Read(ref _current) is not null;

        /// <summary>Gets whether a refresh is currently running (tests).</summary>
        internal bool IsRefreshing => Volatile.Read(ref _refreshing) == 1;

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            NntpSharedConfigurationLogMessages.InitialLoadStarted(_logger);
            try
            {
                var snapshot = await LoadAsync(cancellationToken).ConfigureAwait(false);
                Publish(snapshot);
                NntpSharedConfigurationLogMessages.RefreshLoopStarted(_logger, _interval);
                _execution = RunAsync(_runCts.Token);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                {
                    NntpSharedConfigurationLogMessages.InitialLoadFailed(_logger, ex);
                }

                Interlocked.Exchange(ref _started, 0);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _runCts.CancelAsync().ConfigureAwait(false);
            var execution = _execution;
            if (execution is null)
            {
                return;
            }

            try
            {
                await execution.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await _runCts.CancelAsync().ConfigureAwait(false);
            _runCts.Dispose();
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(_interval, _timeProvider, cancellationToken).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await TryRefreshAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }

            NntpSharedConfigurationLogMessages.RefreshLoopStopped(_logger);
        }

        private async Task TryRefreshAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
            {
                return;
            }

            try
            {
                var snapshot = await LoadAsync(cancellationToken).ConfigureAwait(false);
                Publish(snapshot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                NntpSharedConfigurationLogMessages.RefreshFailed(_logger, ex);
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        }

        private async Task<NntpSharedConfiguration> LoadAsync(CancellationToken cancellationToken)
        {
            await using var session = await _nntpDb.OpenAsync(cancellationToken).ConfigureAwait(false);
            return await NntpSharedConfigurationReader.ReadAsync(session, cancellationToken).ConfigureAwait(false);
        }

        private void Publish(NntpSharedConfiguration snapshot)
        {
            var options = _options.Value;
            if (options.ServerId is { } serverId && ServerIdRules.IsInRange(serverId))
            {
                var prefix = string.IsNullOrWhiteSpace(options.HostApplicationPrefix)
                    ? NntpdOptions.ApplicationPrefix
                    : options.HostApplicationPrefix;
                var fqdn = snapshot.RequireApplicationFqdn(prefix, serverId);
                if (options.IsTlsListenerEnabled)
                {
                    DnsZoneCoverage.RequireIdentitiesInDnsZone(
                        CertificateIdentities.ForFqdn(fqdn, options.IncludeNewsHostnameInCertificate),
                        snapshot.DnsSuffix);
                }
            }

            snapshot.CopyAcmeDnsTo(options);
            snapshot.CopyRabbitMqCredentialsTo(_rabbitMq.Value);
            Volatile.Write(ref _current, new PublishedSnapshot(snapshot));
            NntpSharedConfigurationLogMessages.SnapshotPublished(_logger, snapshot.MaxArticleBytes, snapshot.SiteName);
        }
    }
}

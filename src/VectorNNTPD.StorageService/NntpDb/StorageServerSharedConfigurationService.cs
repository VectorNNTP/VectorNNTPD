using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.StorageService.Configuration;

namespace VectorNNTPD.StorageService.NntpDb
{

    /// <summary>
    /// Loads <c>nntpsharedconfig</c> during startup and refreshes it on this service's own loop.
    /// </summary>
    /// <remarks>
    /// The initial load fails startup. A later refresh failure keeps the last known-good snapshot.
    /// This service does not create schema or run migrations, and it does not read
    /// <c>maxartsize</c>, <c>sitename</c>, or <c>prometheusurl</c>. The listener's runtime snapshot is
    /// published once from the initial row. Refreshes update the live ACME and RabbitMQ options.
    /// </remarks>
    internal sealed class StorageServerSharedConfigurationService : INntpSharedConfigurationCatalogue, IApplicationService
    {
        /// <summary>Refresh interval after a successful initial load.</summary>
        public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

        private readonly NntpDbService _nntpDb;
        private readonly IOptions<StorageServerOptions> _options;
        private readonly IOptions<AcmeCloudflareOptions> _acme;
        private readonly IOptions<RabbitMqOptions> _rabbitMq;
        private readonly StorageServerRuntimeOptionsSource _runtime;
        private readonly ILogger<StorageServerSharedConfigurationService> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _interval;
        private readonly CancellationTokenSource _runCts = new();
        private PublishedSnapshot? _current;
        private Task? _execution;
        private int _started;

        /// <summary>Initializes a new instance of the <see cref="StorageServerSharedConfigurationService"/> class.</summary>
        /// <param name="nntpDb">Started database service.</param>
        /// <param name="options">Live StorageServer options. Shared fields are replaced from each snapshot.</param>
        /// <param name="acme">Live ACME and Cloudflare options. Shared fields are replaced from each snapshot.</param>
        /// <param name="rabbitMq">Live RabbitMQ options. Username and password are replaced from each snapshot.</param>
        /// <param name="runtime">Receives the runtime snapshot from the initial load.</param>
        /// <param name="logger">Lifecycle logger.</param>
        public StorageServerSharedConfigurationService(
            NntpDbService nntpDb,
            IOptions<StorageServerOptions> options,
            IOptions<AcmeCloudflareOptions> acme,
            IOptions<RabbitMqOptions> rabbitMq,
            StorageServerRuntimeOptionsSource runtime,
            ILogger<StorageServerSharedConfigurationService> logger)
            : this(nntpDb, options, acme, rabbitMq, runtime, logger, TimeProvider.System, RefreshInterval)
        {
        }

        /// <summary>Initializes a new instance with an explicit clock and interval (tests).</summary>
        /// <param name="nntpDb">Started database service.</param>
        /// <param name="options">Live StorageServer options.</param>
        /// <param name="acme">Live ACME and Cloudflare options.</param>
        /// <param name="rabbitMq">Live RabbitMQ options.</param>
        /// <param name="runtime">Receives the runtime snapshot from the initial load.</param>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="timeProvider">Clock used by the refresh delay.</param>
        /// <param name="interval">Delay between refreshes.</param>
        internal StorageServerSharedConfigurationService(
            NntpDbService nntpDb,
            IOptions<StorageServerOptions> options,
            IOptions<AcmeCloudflareOptions> acme,
            IOptions<RabbitMqOptions> rabbitMq,
            StorageServerRuntimeOptionsSource runtime,
            ILogger<StorageServerSharedConfigurationService> logger,
            TimeProvider timeProvider,
            TimeSpan interval)
        {
            ArgumentNullException.ThrowIfNull(nntpDb);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(acme);
            ArgumentNullException.ThrowIfNull(rabbitMq);
            ArgumentNullException.ThrowIfNull(runtime);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
            _nntpDb = nntpDb;
            _options = options;
            _acme = acme;
            _rabbitMq = rabbitMq;
            _runtime = runtime;
            _logger = logger;
            _timeProvider = timeProvider;
            _interval = interval;
        }

        /// <inheritdoc />
        public string Name => "NntpSharedConfiguration";

        /// <inheritdoc />
        public Task? Execution => _execution;

        /// <inheritdoc />
        public NntpSharedConfiguration Current =>
            Volatile.Read(ref _current)?.Value
            ?? throw new InvalidOperationException("nntpsharedconfig has not been published.");

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            StorageServerSharedConfigurationLogMessages.InitialLoadStarted(_logger);
            try
            {
                var snapshot = await LoadAsync(cancellationToken).ConfigureAwait(false);
                Publish(snapshot, publishRuntime: true);
                StorageServerSharedConfigurationLogMessages.RefreshLoopStarted(_logger, _interval);
                _execution = RunAsync(_runCts.Token);
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                {
                    StorageServerSharedConfigurationLogMessages.InitialLoadFailed(_logger, ex);
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
                // WaitAsync throws OCE when the caller cancels this wait, or when the execution task
                // itself completed canceled. RunAsync swallows its own loop cancellation and completes
                // successfully after CancelAsync above, so an OCE here is normally the caller's stop
                // budget. Propagate unexpected cancellation from a canceled execution task.
                await execution.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                ExpectedException.Ignore(ex);
            }
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
                ExpectedException.Ignore(ex);
            }

            StorageServerSharedConfigurationLogMessages.RefreshLoopStopped(_logger);
        }

        private async Task TryRefreshAsync(CancellationToken cancellationToken)
        {
            try
            {
                var snapshot = await LoadAsync(cancellationToken).ConfigureAwait(false);
                Publish(snapshot, publishRuntime: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                StorageServerSharedConfigurationLogMessages.RefreshFailed(_logger, ex);
            }
        }

        private async Task<NntpSharedConfiguration> LoadAsync(CancellationToken cancellationToken)
        {
            await using var session = await _nntpDb.OpenAsync(cancellationToken).ConfigureAwait(false);
            return await StorageServerSharedConfigurationReader.ReadAsync(session, cancellationToken).ConfigureAwait(false);
        }

        private void Publish(NntpSharedConfiguration snapshot, bool publishRuntime)
        {
            var options = _options.Value;
            if (options.ServerId is not { } serverId || !ServerIdRules.IsInRange(serverId))
            {
                throw new InvalidOperationException("StorageServer:ServerId must be published before nntpsharedconfig.");
            }

            var fqdn = snapshot.RequireApplicationFqdn(StorageServerOptions.ApplicationPrefix, serverId);
            DnsZoneCoverage.RequireIdentitiesInDnsZone(
                CertificateIdentities.ForFqdn(fqdn, includeNewsHostname: false),
                snapshot.DnsSuffix);

            options.AcmeDirectoryUrl = snapshot.AcmeDirectoryUrl;
            options.AcmeRenewalThresholdDays = snapshot.AcmeRenewalThresholdDays;
            options.CloudFlareZoneId = snapshot.CloudFlareZoneId;
            options.DnsSuffix = snapshot.DnsSuffix;

            var acme = _acme.Value;
            snapshot.CopyAcmeDnsTo(acme);
            acme.Fqdn = fqdn;
            acme.IncludeNewsHostnameInCertificate = false;
            snapshot.CopyRabbitMqCredentialsTo(_rabbitMq.Value);
            Volatile.Write(ref _current, new PublishedSnapshot(snapshot));

            if (publishRuntime)
            {
                _runtime.Publish(StorageServerRuntimeOptionsFactory.Create(options, acme, AppContext.BaseDirectory));
            }

            StorageServerSharedConfigurationLogMessages.SnapshotPublished(_logger, fqdn);
        }

        private sealed class PublishedSnapshot
        {
            internal PublishedSnapshot(NntpSharedConfiguration value) => Value = value;

            internal NntpSharedConfiguration Value { get; }
        }
    }
}

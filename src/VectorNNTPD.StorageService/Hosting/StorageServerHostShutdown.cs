using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Core;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Logging;

namespace VectorNNTPD.StorageService.Hosting
{

    /// <summary>
    /// Ensures application shutdown is initiated exactly once and coordinates with the generic host lifetime.
    /// </summary>
    public sealed class StorageServerHostShutdown
    {
        private readonly ApplicationLifecycle _lifecycle;
        private readonly IHostApplicationLifetime _hostApplicationLifetime;
        private readonly IOptions<StorageServerOptions> _options;
        private readonly ILogger<StorageServerHostShutdown> _logger;
        private readonly object _sync = new();
        private Task? _shutdownTask;
        private int _shutdownRequested;

        /// <summary>
        /// Initializes a new instance of the <see cref="StorageServerHostShutdown"/> class.
        /// </summary>
        public StorageServerHostShutdown(
            ApplicationLifecycle lifecycle,
            IHostApplicationLifetime hostApplicationLifetime,
            IOptions<StorageServerOptions> options,
            ILogger<StorageServerHostShutdown> logger)
        {
            ArgumentNullException.ThrowIfNull(lifecycle);
            ArgumentNullException.ThrowIfNull(hostApplicationLifetime);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);

            _lifecycle = lifecycle;
            _hostApplicationLifetime = hostApplicationLifetime;
            _options = options;
            _logger = logger;
        }

        /// <summary>
        /// Requests a single graceful application shutdown. Subsequent calls await the same operation.
        /// </summary>
        public Task RequestShutdownAsync(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (_shutdownTask is not null)
                {
                    return _shutdownTask;
                }

                if (Interlocked.Exchange(ref _shutdownRequested, 1) == 0)
                {
                    HostingLogMessages.ShutdownRequestedOnce(_logger, _options.Value.ApplicationName);
                }

                _shutdownTask = ShutdownCoreAsync(cancellationToken);
                return _shutdownTask;
            }
        }

        /// <summary>
        /// Stops the generic host after an unexpected application-service termination.
        /// </summary>
        public void NotifyUnexpectedTermination()
        {
            if (!_options.Value.StopHostOnUnexpectedServiceTermination)
            {
                HostingLogMessages.UnexpectedTerminationOptionDisabled(
                    _logger,
                    nameof(StorageServerOptions.StopHostOnUnexpectedServiceTermination));
                return;
            }

            HostingLogMessages.RequestingHostStop(_logger);
            _hostApplicationLifetime.StopApplication();
        }

        private async Task ShutdownCoreAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _lifecycle.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HostingLogMessages.ShutdownCompletedWithFailure(_logger, ex);
                throw;
            }
        }
    }
}

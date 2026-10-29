using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.ReaderService.Logging;

namespace VectorNNTPD.ReaderService.Hosting
{
    /// <summary>
    /// Ensures application shutdown is initiated exactly once and coordinates with the generic host lifetime.
    /// </summary>
    public sealed class NntpdHostLifetime
    {
        private readonly ApplicationLifecycle _lifecycle;
        private readonly IHostApplicationLifetime _hostApplicationLifetime;
        private readonly IOptions<NntpdOptions> _options;
        private readonly ILogger<NntpdHostLifetime> _logger;
        private readonly object _sync = new();
        private Task? _shutdownTask;
        private int _shutdownRequested;

        /// <summary>
        /// Initializes a new instance of the <see cref="NntpdHostLifetime"/> class.
        /// </summary>
        public NntpdHostLifetime(
            ApplicationLifecycle lifecycle,
            IHostApplicationLifetime hostApplicationLifetime,
            IOptions<NntpdOptions> options,
            ILogger<NntpdHostLifetime> logger)
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
        /// <param name="cancellationToken">Host-provided shutdown token.</param>
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
                    nameof(NntpdOptions.StopHostOnUnexpectedServiceTermination));
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
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                HostingLogMessages.ShutdownCompletedWithFailure(_logger, ex);
                throw;
            }
        }
    }
}

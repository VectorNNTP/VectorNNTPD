using VectorNNTPD.Common.Networking.Listeners;

namespace VectorNNTPD.BackFillerService.Listener
{
    /// <summary>
    /// Maps shared accept-failure observations onto <see cref="CacheListenerLogMessages"/>.
    /// </summary>
    internal sealed class CacheListenerAcceptFailureObserver : IListenAcceptFailureObserver
    {
        private readonly ILogger _logger;

        /// <summary>Initializes the observer with the cache listener logger.</summary>
        internal CacheListenerAcceptFailureObserver(ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(logger);
            _logger = logger;
        }

        /// <inheritdoc />
        public void OnAcceptFailed(string socketError, int consecutiveFailures, int maxFailures) =>
            CacheListenerLogMessages.AcceptFailed(_logger, socketError, consecutiveFailures, maxFailures);

        /// <inheritdoc />
        public void OnAcceptRecovered(int recoveredAfterFailures) =>
            CacheListenerLogMessages.AcceptRecovered(_logger, recoveredAfterFailures);

        /// <inheritdoc />
        public void OnAcceptLoopStopping(int consecutiveFailures, string socketError) =>
            CacheListenerLogMessages.AcceptLoopStopping(_logger, consecutiveFailures, socketError);
    }
}

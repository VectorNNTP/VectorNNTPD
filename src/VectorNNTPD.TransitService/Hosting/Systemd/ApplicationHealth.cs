using VectorNNTPD.Common.Core;

namespace VectorNNTPD.TransitService.Hosting.Systemd
{
    /// <summary>
    /// Lifecycle-based application health used for systemd watchdog decisions.
    /// </summary>
    public sealed class ApplicationHealth : IApplicationHealth
    {
        private readonly ApplicationLifecycle _lifecycle;

        /// <summary>
        /// Initializes a new instance of the <see cref="ApplicationHealth"/> class.
        /// </summary>
        public ApplicationHealth(ApplicationLifecycle lifecycle)
        {
            ArgumentNullException.ThrowIfNull(lifecycle);
            _lifecycle = lifecycle;
        }

        /// <inheritdoc />
        public bool IsHealthyForWatchdog =>
            _lifecycle is { State: ApplicationState.Running, UnexpectedTermination.IsCompleted: false, ShutdownRequested: false };
    }
}

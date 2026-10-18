using VectorNNTPD.BackFillerService.Core;
using VectorNNTPD.BackFillerService.Hosting.Systemd;

namespace VectorNNTPD.BackFillerService.Hosting
{
    /// <summary>
    /// Integrates <see cref="ApplicationServiceManager"/> with the .NET Generic Host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Startup runs during <see cref="StartAsync"/> so a failed application-service
    /// initialization prevents the host from reporting successful start.
    /// </para>
    /// <para>
    /// The constructor subscribes to <see cref="ApplicationServiceManager.UnexpectedServiceTermination"/>
    /// and does not remove that subscription. The handler marks <see cref="BackFillerApplicationHealth"/>
    /// unhealthy for the watchdog and calls <see cref="IHostApplicationLifetime.StopApplication"/>.
    /// Faults and non-fault completions are handled the same way.
    /// </para>
    /// </remarks>
    internal sealed class BackFillerApplicationHostedService : IHostedService
    {
        /// <summary>Application-service start and stop coordinator.</summary>
        private readonly ApplicationServiceManager _manager;

        /// <summary>Host lifetime stopped after an unexpected service execution end.</summary>
        private readonly IHostApplicationLifetime _lifetime;

        /// <summary>Watchdog health marked failed after an unexpected service execution end.</summary>
        private readonly BackFillerApplicationHealth _health;

        /// <summary>
        /// Subscribes to unexpected service termination and retains the host lifetime and health flags.
        /// </summary>
        /// <param name="manager">Coordinator started and stopped with this hosted service.</param>
        /// <param name="lifetime">Lifetime stopped when a watched service execution ends.</param>
        /// <param name="health">Health flags updated when a watched service execution ends.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="manager"/>, <paramref name="lifetime"/>, or <paramref name="health"/> is <see langword="null"/>.
        /// </exception>
        public BackFillerApplicationHostedService(
            ApplicationServiceManager manager,
            IHostApplicationLifetime lifetime,
            BackFillerApplicationHealth health)
        {
            ArgumentNullException.ThrowIfNull(manager);
            ArgumentNullException.ThrowIfNull(lifetime);
            ArgumentNullException.ThrowIfNull(health);
            _manager = manager;
            _lifetime = lifetime;
            _health = health;
            _manager.UnexpectedServiceTermination += OnUnexpectedServiceTermination;
        }

        /// <summary>Starts application services through <see cref="ApplicationServiceManager.StartAsync"/>.</summary>
        /// <param name="cancellationToken">Forwarded to <see cref="ApplicationServiceManager.StartAsync"/>.</param>
        /// <returns>The task returned by <see cref="ApplicationServiceManager.StartAsync"/>.</returns>
        /// <remarks>
        /// Exceptions from the manager, including <see cref="InvalidOperationException"/>,
        /// <see cref="OperationCanceledException"/>, and service start failures, propagate and fail host start.
        /// </remarks>
        public Task StartAsync(CancellationToken cancellationToken) => _manager.StartAsync(cancellationToken);

        /// <summary>Stops application services through <see cref="ApplicationServiceManager.StopAsync"/>.</summary>
        /// <param name="cancellationToken">Forwarded to <see cref="ApplicationServiceManager.StopAsync"/>.</param>
        /// <returns>The task returned by <see cref="ApplicationServiceManager.StopAsync"/>.</returns>
        /// <remarks>
        /// <see cref="InvalidOperationException"/>, gate-wait cancellation, and <see cref="AggregateException"/>
        /// from the manager propagate. This method does not send systemd notifications.
        /// </remarks>
        public Task StopAsync(CancellationToken cancellationToken) => _manager.StopAsync(cancellationToken);

        /// <summary>Marks watchdog health failed and requests host shutdown.</summary>
        /// <param name="sender">Event source. Not inspected.</param>
        /// <param name="args">Termination details. Not inspected; fault and non-fault endings both stop the host.</param>
        /// <remarks>Runs on the <see cref="ApplicationServiceManager"/> execution-watch task.</remarks>
        private void OnUnexpectedServiceTermination(object? sender, UnexpectedServiceTerminationEventArgs args)
        {
            _health.MarkUnexpectedTermination();
            _lifetime.StopApplication();
        }
    }
}

using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Logging;
using Microsoft.Extensions.Options;

namespace VectorNNTPD.BackFillerService.Hosting.Systemd
{
    /// <summary>
    /// Sends systemd watchdog keep-alives while the application is healthy.
    /// </summary>
    /// <remarks>
    /// The watchdog is disabled unless systemd configured <c>WATCHDOG_USEC</c> for this process and
    /// <see cref="BackFillerSystemdOptions.EnableWatchdog"/> is <see langword="true"/>. Unhealthy ticks
    /// are skipped and the loop keeps running; the loop ends when shutdown begins.
    /// </remarks>
    internal sealed class SystemdWatchdogService : BackgroundService
    {
        /// <summary>Platform snapshot consulted by <see cref="ShouldActivate"/> and for the deadline.</summary>
        private readonly ISystemdRuntime _runtime;

        /// <summary>Bridge used for <c>WATCHDOG=1</c>.</summary>
        private readonly ISystemdNotifyBridge _notify;

        /// <summary>Health checked before each keep-alive. An unhealthy result skips that tick and leaves the loop running.</summary>
        private readonly IApplicationHealth _health;

        /// <summary>Lifetime whose stopping token is linked with the execute loop.</summary>
        private readonly IHostApplicationLifetime _hostLifetime;

        /// <summary>Options supplying <see cref="BackFillerSystemdOptions.EnableWatchdog"/> and the interval fraction.</summary>
        private readonly IOptions<BackFillerOptions> _options;

        /// <summary>Logger for activation, skipped keep-alives, failures, and shutdown.</summary>
        private readonly ILogger<SystemdWatchdogService> _logger;

        /// <summary>
        /// Heartbeat interval computed when the loop activates. Stays at <see cref="TimeSpan.Zero"/> when activation is declined,
        /// and is not cleared when the loop later stops.
        /// </summary>
        private TimeSpan _interval;

        /// <summary>Non-zero while the watchdog loop is considered active.</summary>
        private int _active;

        /// <summary>
        /// Retains runtime, notify, health, and options used when the host starts this service.
        /// </summary>
        /// <param name="runtime">Platform snapshot. Supplies the watchdog deadline when activation succeeds.</param>
        /// <param name="notify">Bridge that sends watchdog keep-alives.</param>
        /// <param name="health">Health consulted on each tick.</param>
        /// <param name="hostLifetime">Lifetime linked into the execute loop so host stopping cancels the timer.</param>
        /// <param name="options">BackFiller options whose systemd section enables the watchdog and selects the fraction.</param>
        /// <param name="logger">Logger for watchdog lifecycle events.</param>
        /// <exception cref="ArgumentNullException">Thrown when any argument is <see langword="null"/>.</exception>
        public SystemdWatchdogService(
            ISystemdRuntime runtime,
            ISystemdNotifyBridge notify,
            IApplicationHealth health,
            IHostApplicationLifetime hostLifetime,
            IOptions<BackFillerOptions> options,
            ILogger<SystemdWatchdogService> logger)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            ArgumentNullException.ThrowIfNull(notify);
            ArgumentNullException.ThrowIfNull(health);
            ArgumentNullException.ThrowIfNull(hostLifetime);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);

            _runtime = runtime;
            _notify = notify;
            _health = health;
            _hostLifetime = hostLifetime;
            _options = options;
            _logger = logger;
        }

        /// <summary>Gets a value indicating whether the watchdog loop is active.</summary>
        /// <value>
        /// <see langword="true"/> after activation succeeds, until <see cref="StopAsync"/>, loop exit, or a loop failure clears it.
        /// </value>
        internal bool IsActive => Volatile.Read(ref _active) != 0;

        /// <summary>Gets the heartbeat interval last computed for this service.</summary>
        /// <value>
        /// <see cref="TimeSpan.Zero"/> until <see cref="StartAsync"/> activates the loop.
        /// After activation, the calculated interval remains, including after the loop stops.
        /// </value>
        internal TimeSpan HeartbeatInterval => _interval;

        /// <summary>Activates the keep-alive loop when systemd and options allow it.</summary>
        /// <param name="cancellationToken">
        /// Forwarded to <see cref="BackgroundService.StartAsync"/> only when the loop activates. Otherwise ignored.
        /// </param>
        /// <returns>
        /// A completed task when the watchdog stays disabled. Otherwise the task from
        /// <see cref="BackgroundService.StartAsync"/>.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when activation checks passed and <see cref="ISystemdRuntime.WatchdogTimeout"/> is then <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the systemd deadline is shorter than <see cref="SystemdWatchdogInterval.MinimumTimeout"/>.
        /// </exception>
        /// <remarks>
        /// A disabled watchdog does not call <see cref="BackgroundService.StartAsync"/>, so <see cref="ExecuteAsync"/> is not started.
        /// </remarks>
        public override Task StartAsync(CancellationToken cancellationToken)
        {
            if (!ShouldActivate(out var reason))
            {
                SystemdLogMessages.WatchdogRemainDisabled(_logger, reason);
                return Task.CompletedTask;
            }

            var timeout = _runtime.WatchdogTimeout
                ?? throw new InvalidOperationException("Watchdog timeout unexpectedly unavailable.");

            _interval = SystemdWatchdogInterval.Calculate(
                timeout,
                _options.Value.Systemd.WatchdogIntervalFraction);

            Interlocked.Exchange(ref _active, 1);
            SystemdLogMessages.WatchdogActivated(_logger, timeout, _interval);

            return base.StartAsync(cancellationToken);
        }

        /// <summary>Sends watchdog keep-alives until shutdown, while skipping ticks that are not healthy.</summary>
        /// <param name="stoppingToken">
        /// Host stop token. Linked with <see cref="IHostApplicationLifetime.ApplicationStopping"/>.
        /// </param>
        /// <returns>A task that completes when the loop exits. An inactive service completes immediately.</returns>
        /// <remarks>
        /// <para>
        /// Each tick checks <see cref="IApplicationHealth.IsHealthyForWatchdog"/>. An unhealthy result is logged
        /// and does not stop the loop. Exceptions from <see cref="ISystemdNotifyBridge.NotifyWatchdog"/> are logged
        /// and do not stop the loop.
        /// </para>
        /// <para>
        /// <see cref="OperationCanceledException"/> after the linked token is canceled is logged as deactivation and swallowed.
        /// Any other exception is logged, clears <see cref="IsActive"/>, calls
        /// <see cref="IHostApplicationLifetime.StopApplication"/>, and is rethrown.
        /// The active flag is cleared again when the method leaves.
        /// </para>
        /// </remarks>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (Volatile.Read(ref _active) == 0)
            {
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken,
                _hostLifetime.ApplicationStopping);

            try
            {
                using var timer = new PeriodicTimer(_interval);
                while (await timer.WaitForNextTickAsync(linked.Token).ConfigureAwait(false))
                {
                    if (!_health.IsHealthyForWatchdog)
                    {
                        SystemdLogMessages.WatchdogKeepAliveSkippedUnhealthy(_logger);
                        continue;
                    }

                    try
                    {
                        _notify.NotifyWatchdog();
                        SystemdLogMessages.WatchdogKeepAliveSent(_logger);
                    }
                    // codeql[cs/catch-of-all-exceptions] A watchdog notify failure must be logged and must not stop the process.
                    catch (Exception ex)
                    {
                        // Unlike NNTPD, notify failures must not stop BackFiller. Missing
                        // keep-alives still trip systemd when WatchdogSec is configured.
                        SystemdLogMessages.WatchdogNotificationFailed(_logger, ex);
                    }
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                SystemdLogMessages.WatchdogDeactivated(_logger);
            }
            catch (Exception ex)
            {
                SystemdLogMessages.WatchdogLoopFailed(_logger, ex);
                Interlocked.Exchange(ref _active, 0);
                _hostLifetime.StopApplication();
                throw;
            }
            finally
            {
                Interlocked.Exchange(ref _active, 0);
            }
        }

        /// <summary>Clears the active flag, logs stopping, and stops the background loop.</summary>
        /// <param name="cancellationToken">Forwarded to <see cref="BackgroundService.StopAsync"/>.</param>
        /// <returns>The task returned by <see cref="BackgroundService.StopAsync"/>.</returns>
        /// <remarks>The stopping log is written even when <see cref="StartAsync"/> left the watchdog inactive.</remarks>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _active, 0);
            SystemdLogMessages.WatchdogStopping(_logger);
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Decides whether watchdog heartbeats should run for this process.</summary>
        /// <param name="reason">
        /// Diagnostic phrase for the disabled log, or <c>ok</c> when activation is allowed.
        /// Assigned in check order: <c>EnableWatchdog is false</c>, <c>non-Linux platform</c>,
        /// <c>systemd did not configure WATCHDOG_USEC for this process</c>,
        /// <c>systemd notify is not enabled</c>, then <c>ok</c>.
        /// </param>
        /// <returns>
        /// <see langword="true"/> only when <see cref="BackFillerSystemdOptions.EnableWatchdog"/> is true,
        /// <see cref="ISystemdRuntime.IsLinux"/> is true, a watchdog deadline is configured,
        /// and <see cref="ISystemdNotifyBridge.IsEnabled"/> is true.
        /// </returns>
        private bool ShouldActivate(out string reason)
        {
            if (!_options.Value.Systemd.EnableWatchdog)
            {
                reason = "EnableWatchdog is false";
                return false;
            }

            if (!_runtime.IsLinux)
            {
                reason = "non-Linux platform";
                return false;
            }

            if (!_runtime.IsWatchdogConfigured || _runtime.WatchdogTimeout is null)
            {
                reason = "systemd did not configure WATCHDOG_USEC for this process";
                return false;
            }

            if (!_notify.IsEnabled)
            {
                reason = "systemd notify is not enabled";
                return false;
            }

            reason = "ok";
            return true;
        }
    }
}

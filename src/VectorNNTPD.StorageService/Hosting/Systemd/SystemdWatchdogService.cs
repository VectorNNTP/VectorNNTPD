using Microsoft.Extensions.Options;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Logging;

namespace VectorNNTPD.StorageService.Hosting.Systemd
{

    /// <summary>
    /// Sends systemd watchdog keep-alives while the application is healthy.
    /// </summary>
    public sealed class SystemdWatchdogService : BackgroundService
    {
        private readonly ISystemdRuntime _runtime;
        private readonly ISystemdNotifyBridge _notify;
        private readonly IApplicationHealth _health;
        private readonly IHostApplicationLifetime _hostLifetime;
        private readonly IOptions<StorageServerOptions> _options;
        private readonly ILogger<SystemdWatchdogService> _logger;
        private TimeSpan _interval;
        private int _active;

        /// <summary>
        /// Initializes a new instance of the <see cref="SystemdWatchdogService"/> class.
        /// </summary>
        public SystemdWatchdogService(
            ISystemdRuntime runtime,
            ISystemdNotifyBridge notify,
            IApplicationHealth health,
            IHostApplicationLifetime hostLifetime,
            IOptions<StorageServerOptions> options,
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
        public bool IsActive => Volatile.Read(ref _active) != 0;

        /// <summary>Gets the heartbeat interval when active; otherwise <see cref="TimeSpan.Zero"/>.</summary>
        public TimeSpan HeartbeatInterval => _interval;

        /// <inheritdoc />
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

        /// <inheritdoc />
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
                    catch (Exception ex)
                    {
                        SystemdLogMessages.WatchdogNotificationFailed(_logger, ex);
                        throw;
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

        /// <inheritdoc />
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _active, 0);
            SystemdLogMessages.WatchdogStopping(_logger);
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }

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

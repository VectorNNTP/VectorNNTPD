using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Core;
using VectorNNTPD.StorageService.Configuration;

namespace VectorNNTPD.StorageService.Storage.SegmentPools
{
    /// <summary>
    /// Supervises one independent <see cref="SpoolEvictionMonitor"/> per configured spool directory.
    /// Eight configured directories produce eight independently scheduled monitor loops.
    /// </summary>
    internal sealed class SpoolEvictionMonitorService : IApplicationService
    {
        private readonly SegmentPoolCatalog _catalog;
        private readonly SpoolCapacityRegistry _registry;
        private readonly BlockDeviceMonitorService _monitor;
        private readonly IServiceProvider _services;
        private readonly IOptions<StorageServerOptions> _options;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<SpoolEvictionMonitorService> _logger;
        private readonly CancellationTokenSource _runCts = new();
        private readonly List<SpoolEvictionMonitor> _monitors = [];
        private Task? _execution;
        private int _started;

        /// <summary>Initializes the supervisor.</summary>
        public SpoolEvictionMonitorService(
            SegmentPoolCatalog catalog,
            SpoolCapacityRegistry registry,
            BlockDeviceMonitorService monitor,
            IServiceProvider services,
            IOptions<StorageServerOptions> options,
            ILoggerFactory loggerFactory,
            ILogger<SpoolEvictionMonitorService> logger)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(monitor);
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(loggerFactory);
            ArgumentNullException.ThrowIfNull(logger);
            _catalog = catalog;
            _registry = registry;
            _monitor = monitor;
            _services = services;
            _options = options;
            _loggerFactory = loggerFactory;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "SpoolEvictionMonitors";

        /// <inheritdoc />
        public Task? Execution => _execution;

        /// <summary>Independent monitors created for the current catalog.</summary>
        internal IReadOnlyList<SpoolEvictionMonitor> Monitors => _monitors;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return Task.CompletedTask;
            }

            _monitors.AddRange(
                _catalog.Devices.SelectMany(device =>
                    device.PoolPaths.Select(path =>
                        new SpoolEvictionMonitor(
                            path,
                            device.DeviceId,
                            _registry,
                            _monitor,
                            _services,
                            _options,
                            _loggerFactory.CreateLogger<SpoolEvictionMonitor>()))));

            SpoolEvictionMonitorServiceLogMessages.SupervisorStarted(_logger, _monitors.Count);
            _monitor.CapacitySamplePublished += OnCapacitySample;
            _registry.RefreshFromObservations();
            foreach (var monitor in _monitors)
            {
                monitor.Start(_runCts.Token);
            }

            _execution = Task.WhenAll(_monitors.Select(static m => m.Execution ?? Task.CompletedTask));
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _monitor.CapacitySamplePublished -= OnCapacitySample;
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
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                ExpectedException.Ignore(ex);
            }

            SpoolEvictionMonitorServiceLogMessages.SupervisorStopped(_logger, _monitors.Count);
        }

        private void OnCapacitySample()
        {
            _registry.RefreshFromObservations();
            foreach (var monitor in _monitors)
            {
                monitor.NotifySample();
            }
        }
    }

    /// <summary>Source-generated eviction-monitor supervisor logs.</summary>
    internal static partial class SpoolEvictionMonitorServiceLogMessages
    {
        [LoggerMessage(
            EventId = 5840,
            Level = LogLevel.Information,
            Message = "Spool eviction monitor supervisor started monitorCount={MonitorCount}")]
        public static partial void SupervisorStarted(ILogger logger, int MonitorCount);

        [LoggerMessage(
            EventId = 5841,
            Level = LogLevel.Information,
            Message = "Spool eviction monitor supervisor stopped monitorCount={MonitorCount}")]
        public static partial void SupervisorStopped(ILogger logger, int MonitorCount);
    }
}

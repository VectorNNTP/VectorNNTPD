using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Core;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Storage.ArticleIndex;

namespace VectorNNTPD.StorageService.Storage.SegmentPools
{
    /// <summary>
    /// Independent High→Low LFU eviction loop for one configured spool directory.
    /// Never selects victims from another spool. Non-fatal failures are logged and the loop continues
    /// after a bounded backoff so one monitor cannot terminate the others.
    /// </summary>
    internal sealed class SpoolEvictionMonitor
    {
        private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(5);

        private readonly string _spoolDirectory;
        private readonly string _deviceId;
        private readonly SpoolCapacityRegistry _registry;
        private readonly IServiceProvider _services;
        private readonly StorageServerOptions _options;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _wake = new(0, 1);
        private Task? _loop;
        private int _started;
        private int _evictionRunning;

        /// <summary>Initializes one per-spool monitor.</summary>
        public SpoolEvictionMonitor(
            string spoolDirectory,
            string deviceId,
            SpoolCapacityRegistry registry,
            BlockDeviceMonitorService monitor,
            IServiceProvider services,
            IOptions<StorageServerOptions> options,
            ILogger logger)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(spoolDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(monitor);
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);
            _ = monitor;
            _spoolDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(spoolDirectory));
            _deviceId = deviceId;
            _registry = registry;
            _services = services;
            _options = options.Value;
            _logger = logger;
        }

        /// <summary>Canonical spool root this monitor owns.</summary>
        internal string SpoolDirectory => _spoolDirectory;

        /// <summary>Physical device identity for shared-volume awareness.</summary>
        internal string DeviceId => _deviceId;

        /// <summary>Background loop task when started.</summary>
        internal Task? Execution => _loop;

        /// <summary>Starts the independent monitor loop.</summary>
        internal void Start(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            SpoolEvictionMonitorLogMessages.MonitorStarted(_logger, _spoolDirectory, _deviceId);
            _loop = RunAsync(cancellationToken);
            NotifySample();
        }

        /// <summary>Signals that a new capacity sample may require eviction evaluation.</summary>
        internal void NotifySample()
        {
            try
            {
                if (_wake.CurrentCount == 0)
                {
                    _wake.Release();
                }
            }
            catch (SemaphoreFullException)
            {
                // Already signaled.
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
                        await EvaluateAndEvictAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                    {
                        SpoolEvictionMonitorLogMessages.MonitorFailed(_logger, _spoolDirectory, ex.GetType().Name);
                        try
                        {
                            await Task.Delay(FailureBackoff, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                }
            }
            finally
            {
                _registry.SetEvictionActive(_spoolDirectory, false);
                SpoolEvictionMonitorLogMessages.MonitorStopped(_logger, _spoolDirectory);
            }
        }

        private async Task EvaluateAndEvictAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _evictionRunning, 1, 0) != 0)
            {
                return;
            }

            try
            {
                _registry.RefreshFromObservations();
                if (!_registry.TryGet(_spoolDirectory, out var state) || !state.EvictionRequired)
                {
                    return;
                }

                var index = _services.GetService<IArticleIndex>();
                if (index is null || !index.IsReady)
                {
                    return;
                }

                var pools = _options.SegmentPools ?? new SegmentPoolsOptions();
                SpoolEvictionMonitorLogMessages.EvictionStarted(
                    _logger,
                    _spoolDirectory,
                    state.UsedPercent,
                    pools.HighPercent,
                    pools.LowPercent);

                _registry.SetEvictionActive(_spoolDirectory, true);
                var beforeAvailable = state.AvailableBytes;
                try
                {
                    await index.EvictUntilBelowHighAsync(_spoolDirectory, cancellationToken).ConfigureAwait(false);
                    _registry.RefreshFromObservations();
                    if (_registry.TryGet(_spoolDirectory, out var after))
                    {
                        var reclaimed = after.AvailableBytes > beforeAvailable
                            ? after.AvailableBytes - beforeAvailable
                            : 0;
                        if (after.UsedPercent > pools.LowPercent)
                        {
                            SpoolEvictionMonitorLogMessages.EvictionInsufficient(
                                _logger,
                                _spoolDirectory,
                                after.UsedPercent,
                                pools.LowPercent);
                        }
                        else
                        {
                            SpoolEvictionMonitorLogMessages.EvictionCompleted(
                                _logger,
                                _spoolDirectory,
                                after.UsedPercent,
                                reclaimed);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                {
                    SpoolEvictionMonitorLogMessages.EvictionFailed(_logger, _spoolDirectory, ex.GetType().Name);
                }
                finally
                {
                    _registry.SetEvictionActive(_spoolDirectory, false);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _evictionRunning, 0);
            }
        }
    }

    /// <summary>Source-generated per-spool eviction monitor logs.</summary>
    internal static partial class SpoolEvictionMonitorLogMessages
    {
        [LoggerMessage(
            EventId = 5830,
            Level = LogLevel.Information,
            Message = "Spool eviction monitor started spool={Spool} device={DeviceId}")]
        public static partial void MonitorStarted(ILogger logger, string Spool, string DeviceId);

        [LoggerMessage(
            EventId = 5831,
            Level = LogLevel.Information,
            Message = "Spool eviction monitor stopped spool={Spool}")]
        public static partial void MonitorStopped(ILogger logger, string Spool);

        [LoggerMessage(
            EventId = 5832,
            Level = LogLevel.Warning,
            Message = "Spool eviction monitor failed spool={Spool} failure={Failure}")]
        public static partial void MonitorFailed(ILogger logger, string Spool, string Failure);

        [LoggerMessage(
            EventId = 5833,
            Level = LogLevel.Information,
            Message = "Spool LFU eviction started spool={Spool} usedPercent={UsedPercent} highPercent={HighPercent} lowPercent={LowPercent}")]
        public static partial void EvictionStarted(
            ILogger logger,
            string Spool,
            int UsedPercent,
            int HighPercent,
            int LowPercent);

        [LoggerMessage(
            EventId = 5834,
            Level = LogLevel.Information,
            Message = "Spool LFU eviction completed spool={Spool} usedPercent={UsedPercent} reclaimedBytes={ReclaimedBytes}")]
        public static partial void EvictionCompleted(ILogger logger, string Spool, int UsedPercent, long ReclaimedBytes);

        [LoggerMessage(
            EventId = 5835,
            Level = LogLevel.Warning,
            Message = "Spool LFU eviction ended without reaching low-water spool={Spool} usedPercent={UsedPercent} lowPercent={LowPercent}")]
        public static partial void EvictionInsufficient(ILogger logger, string Spool, int UsedPercent, int LowPercent);

        [LoggerMessage(
            EventId = 5836,
            Level = LogLevel.Warning,
            Message = "Spool LFU eviction failed spool={Spool} failure={Failure}")]
        public static partial void EvictionFailed(ILogger logger, string Spool, string Failure);
    }
}

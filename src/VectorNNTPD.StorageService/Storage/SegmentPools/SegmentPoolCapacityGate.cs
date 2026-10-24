using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Storage.SpoolPersistence;

namespace VectorNNTPD.StorageService.Storage.SegmentPools
{
    /// <summary>
    /// Global TAKETHIS admission gate based on per-spool placement eligibility.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NNTP admission and persistence placement are distinct: admission requires at least one
    /// valid, fresh, non-critical destination; placement (in <c>SpoolSelector</c>) picks the
    /// eligible spool with the greatest available bytes.
    /// </para>
    /// <para>
    /// Admission recovers as soon as any destination becomes eligible. It does not wait for every
    /// spool to fall to <see cref="SegmentPoolsOptions.LowPercent"/>. Per-spool High→Low eviction
    /// is owned by independent <see cref="SpoolEvictionMonitor"/> loops.
    /// </para>
    /// </remarks>
    internal sealed class SegmentPoolCapacityGate
    {
        private readonly SpoolCapacityRegistry _registry;
        private readonly BlockDeviceMonitorService _monitor;
        private readonly ILogger _logger;
        private int _admissionBlocked = 1;
        private int _loggedUnknown;
        private int _loggedCritical;

        /// <summary>Initializes the gate.</summary>
        public SegmentPoolCapacityGate(
            SpoolCapacityRegistry registry,
            BlockDeviceMonitorService monitor,
            IOptions<StorageServerOptions> options,
            ILogger<SegmentPoolCapacityGate> logger)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(monitor);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);
            _ = options;
            _registry = registry;
            _monitor = monitor;
            _logger = logger;
            _monitor.CapacitySamplePublished += OnCapacitySampleUpdated;
            OnCapacitySampleUpdated();
        }

        /// <summary>
        /// Returns whether TAKETHIS may admit into the in-memory queue because at least one
        /// spool is valid, fresh, and at or below CriticalPercent.
        /// </summary>
        internal bool AllowsAdmission()
        {
            _registry.RefreshFromObservations();
            if (_registry.HasEligibleDestination())
            {
                if (Interlocked.Exchange(ref _admissionBlocked, 0) == 1)
                {
                    SegmentPoolCapacityLogMessages.AdmissionRecovered(_logger);
                    Interlocked.Exchange(ref _loggedUnknown, 0);
                    Interlocked.Exchange(ref _loggedCritical, 0);
                }

                return true;
            }

            Interlocked.Exchange(ref _admissionBlocked, 1);
            var states = _registry.SnapshotAll();
            var anyValid = false;
            foreach (var state in states)
            {
                if (state.MeasurementValid)
                {
                    anyValid = true;
                    break;
                }
            }

            if (!anyValid)
            {
                if (Interlocked.Exchange(ref _loggedUnknown, 1) == 0)
                {
                    SegmentPoolCapacityLogMessages.AdmissionBlockedUnknown(_logger);
                }
            }
            else if (Interlocked.Exchange(ref _loggedCritical, 1) == 0)
            {
                SegmentPoolCapacityLogMessages.AdmissionBlockedCritical(_logger);
            }

            return false;
        }

        /// <summary>Refreshes registry state after a monitor publish.</summary>
        internal void OnCapacitySampleUpdated()
        {
            _registry.RefreshFromObservations();
            if (_registry.HasEligibleDestination())
            {
                Interlocked.Exchange(ref _admissionBlocked, 0);
            }
            else
            {
                Interlocked.Exchange(ref _admissionBlocked, 1);
            }
        }

        /// <summary>Resolves used percent for one spool path from the monitor snapshot.</summary>
        internal static bool TryGetUsedPercent(
            SpoolObservationSnapshot observations,
            string spoolDirectory,
            out int usedPercent)
        {
            usedPercent = 0;
            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(spoolDirectory));
            if (!observations.Space.TryGetValue(canonical, out var space) || space.TotalBytes <= 0)
            {
                return false;
            }

            usedPercent = SpoolSelector.UsedPercent(space.TotalBytes, space.AvailableBytes);
            return true;
        }
    }

    /// <summary>Source-generated capacity-gate logs.</summary>
    internal static partial class SegmentPoolCapacityLogMessages
    {
        [LoggerMessage(
            EventId = 5820,
            Level = LogLevel.Warning,
            Message = "Segment-pool TAKETHIS admission blocked: no valid fresh filesystem capacity sample on any spool")]
        public static partial void AdmissionBlockedUnknown(ILogger logger);

        [LoggerMessage(
            EventId = 5821,
            Level = LogLevel.Warning,
            Message = "Segment-pool TAKETHIS admission blocked: no spool at or below CriticalPercent")]
        public static partial void AdmissionBlockedCritical(ILogger logger);

        [LoggerMessage(
            EventId = 5822,
            Level = LogLevel.Information,
            Message = "Segment-pool TAKETHIS admission recovered: at least one eligible spool")]
        public static partial void AdmissionRecovered(ILogger logger);
    }
}

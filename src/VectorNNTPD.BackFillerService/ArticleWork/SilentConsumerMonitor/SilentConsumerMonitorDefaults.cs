namespace VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor
{
    /// <summary>
    /// Internal candidate defaults for silent-consumer detection.
    /// Not production-validated paging thresholds; not exposed as JSON hierarchy.
    /// </summary>
    internal static class SilentConsumerMonitorDefaults
    {
        /// <summary>Seconds between monitor cycles.</summary>
        internal const int PollIntervalSeconds = 10;

        /// <summary>Consecutive Management failures before monitor-health is degraded.</summary>
        internal const int DegradedAfterConsecutiveFailures = 3;

        /// <summary>Fallback Management HTTP timeout when <c>RabbitMQ:Management:RequestTimeoutSeconds</c> is unset.</summary>
        internal const int ManagementHttpTimeoutSeconds = 5;

        /// <summary>Processing-stall (in-flight) shared-queue windows and floors.</summary>
        internal static SilentConsumerConditionThresholds ProcessingStall { get; } = new()
        {
            OpenWindow = TimeSpan.FromSeconds(90),
            ResolveWindow = TimeSpan.FromSeconds(20),
            UnackedFloor = 1,
            AckRateEpsilon = 0.05,
        };

        /// <summary>Dispatch-stall (queued-before-active) shared-queue windows and floors.</summary>
        internal static SilentConsumerConditionThresholds DispatchStall { get; } = new()
        {
            OpenWindow = TimeSpan.FromSeconds(90),
            ResolveWindow = TimeSpan.FromSeconds(20),
            ReadyFloor = 1,
            UnackedFloor = 1,
            AckRateEpsilon = 0.05,
            DeliverRateEpsilon = 0.05,
        };

        /// <summary>Consumer-loss shared-queue windows and floors.</summary>
        internal static SilentConsumerConditionThresholds ConsumerLoss { get; } = new()
        {
            OpenWindow = TimeSpan.FromSeconds(45),
            ResolveWindow = TimeSpan.FromSeconds(20),
            ReadyFloor = 1,
        };

        /// <summary>Bounded sample history capacity from the longest open window.</summary>
        internal static int SampleHistoryCapacity =>
            Math.Clamp((int)(ProcessingStall.OpenWindow.TotalSeconds / PollIntervalSeconds) + 8, 8, 512);
    }

    /// <summary>Internal thresholds for one silent-consumer condition.</summary>
    internal sealed class SilentConsumerConditionThresholds
    {
        /// <summary>Sustained-active duration before opening.</summary>
        public TimeSpan OpenWindow { get; init; } = TimeSpan.FromSeconds(90);

        /// <summary>Sustained-clear duration before resolving.</summary>
        public TimeSpan ResolveWindow { get; init; } = TimeSpan.FromSeconds(20);

        /// <summary>Minimum unacknowledged messages.</summary>
        public long UnackedFloor { get; init; }

        /// <summary>Minimum ready messages.</summary>
        public long ReadyFloor { get; init; }

        /// <summary>Ack rate epsilon (msg/s).</summary>
        public double AckRateEpsilon { get; init; } = 0.05;

        /// <summary>Deliver-get rate epsilon (msg/s).</summary>
        public double DeliverRateEpsilon { get; init; } = 0.05;
    }
}

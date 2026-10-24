using VectorNNTPD.StorageService.Storage.SegmentPools;

namespace VectorNNTPD.StorageService.Storage.SpoolPersistence
{
    /// <summary>
    /// Wakes persistence workers that are holding an article because no spool was eligible.
    /// Combined with the bounded <c>IneligibleRetryMilliseconds</c> poll so a notify between
    /// eligibility check and wait subscription cannot be lost when the wait task is captured first.
    /// </summary>
    internal sealed class SpoolCapacityWaitSignal
    {
        private readonly object _gate = new();
        private TaskCompletionSource _pulse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Subscribes to filesystem capacity publishes for the process lifetime.</summary>
        public SpoolCapacityWaitSignal(BlockDeviceMonitorService monitor)
        {
            ArgumentNullException.ThrowIfNull(monitor);
            monitor.CapacitySamplePublished += Notify;
        }

        /// <summary>
        /// Captures the current wait task. Call before attempting placement so a concurrent
        /// <see cref="Notify"/> completes this task even if it races the attempt.
        /// </summary>
        internal Task CaptureWait()
        {
            lock (_gate)
            {
                return _pulse.Task;
            }
        }

        /// <summary>Signals that capacity may have recovered (fresh sample, eviction reclaim, or desaturation).</summary>
        internal void Notify()
        {
            TaskCompletionSource previous;
            lock (_gate)
            {
                previous = _pulse;
                _pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            previous.TrySetResult();
        }
    }
}

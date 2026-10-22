using Microsoft.Extensions.Logging;

namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>Source-generated segment-pool validation and block-device monitor logs.</summary>
    internal static partial class SegmentPoolLogMessages
    {
        [LoggerMessage(
            EventId = 5600,
            Level = LogLevel.Error,
            Message = "Segment pool validation failed for {Path}: {Failure}")]
        public static partial void ValidationFailed(ILogger logger, string path, string failure);

        [LoggerMessage(
            EventId = 5601,
            Level = LogLevel.Information,
            Message = "Segment pool {Path} validated on devices {Devices}")]
        public static partial void ValidationSucceeded(ILogger logger, string path, string devices);

        [LoggerMessage(
            EventId = 5610,
            Level = LogLevel.Trace,
            Message = "Block device sample device={DeviceId} pools={Pools} readBytesPerSecond={ReadBytesPerSecond} writeBytesPerSecond={WriteBytesPerSecond} readIops={ReadIops} writeIops={WriteIops} totalIops={TotalIops} readLatencyMilliseconds={ReadLatencyMilliseconds} writeLatencyMilliseconds={WriteLatencyMilliseconds} activeTimePercent={ActiveTimePercent} averageQueueDepth={AverageQueueDepth} currentQueueDepth={CurrentQueueDepth} intervalMilliseconds={IntervalMilliseconds} counterResets={CounterResets}")]
        public static partial void DeviceSample(
            ILogger logger,
            string deviceId,
            string pools,
            string readBytesPerSecond,
            string writeBytesPerSecond,
            string readIops,
            string writeIops,
            string totalIops,
            string readLatencyMilliseconds,
            string writeLatencyMilliseconds,
            string activeTimePercent,
            string averageQueueDepth,
            string currentQueueDepth,
            long intervalMilliseconds,
            string counterResets);

        [LoggerMessage(
            EventId = 5611,
            Level = LogLevel.Warning,
            Message = "Block device counters unavailable for {DeviceId}: {Error}")]
        public static partial void CountersUnavailable(ILogger logger, string deviceId, string error);

        [LoggerMessage(
            EventId = 5612,
            Level = LogLevel.Debug,
            Message = "Block device sample skipped for {DeviceId}: {Reason}")]
        public static partial void SampleSkipped(ILogger logger, string deviceId, string reason);
    }
}

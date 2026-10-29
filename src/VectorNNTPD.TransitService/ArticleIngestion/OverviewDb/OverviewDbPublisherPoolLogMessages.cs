namespace VectorNNTPD.TransitService.ArticleIngestion.OverviewDb
{
    /// <summary>Source-generated OverviewDB publisher-pool log messages.</summary>
    internal static partial class OverviewDbPublisherPoolLogMessages
    {
        [LoggerMessage(
            EventId = 2110,
            Level = LogLevel.Information,
            Message = "OverviewDB publisher pool starting (min={MinWorkers}, max={MaxWorkers}, publishConcurrency={PublishConcurrency})")]
        public static partial void PoolStarting(
            ILogger logger,
            int MinWorkers,
            int MaxWorkers,
            int PublishConcurrency);

        [LoggerMessage(
            EventId = 2111,
            Level = LogLevel.Information,
            Message = "OverviewDB publisher pool stopped (workers={Workers})")]
        public static partial void PoolStopped(ILogger logger, int Workers);

        [LoggerMessage(
            EventId = 2112,
            Level = LogLevel.Information,
            Message = "OverviewDB publisher pool scaled up to {Workers} (pressure={Pressure:F3})")]
        public static partial void ScaledUp(ILogger logger, int Workers, double Pressure);

        [LoggerMessage(
            EventId = 2113,
            Level = LogLevel.Information,
            Message = "OverviewDB publisher pool scaled down to {Workers} (pressure={Pressure:F3})")]
        public static partial void ScaledDown(ILogger logger, int Workers, double Pressure);

        [LoggerMessage(
            EventId = 2114,
            Level = LogLevel.Error,
            Message = "OverviewDB publisher worker faulted")]
        public static partial void WorkerFaulted(ILogger logger, Exception exception);
    }
}

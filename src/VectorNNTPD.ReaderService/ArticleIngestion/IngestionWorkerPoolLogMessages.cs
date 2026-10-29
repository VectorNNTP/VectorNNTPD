namespace VectorNNTPD.ReaderService.ArticleIngestion
{
    /// <summary>Source-generated ingestion worker-pool log messages.</summary>
    internal static partial class IngestionWorkerPoolLogMessages
    {
        [LoggerMessage(
            EventId = 2010,
            Level = LogLevel.Information,
            Message = "Ingestion worker pool starting (min={MinWorkers}, max={MaxWorkers}, publishConcurrency={PublishConcurrency})")]
        public static partial void PoolStarting(
            ILogger logger,
            int MinWorkers,
            int MaxWorkers,
            int PublishConcurrency);

        [LoggerMessage(
            EventId = 2011,
            Level = LogLevel.Information,
            Message = "Ingestion worker pool scaled up to {WorkerCount} (pressure={Pressure:F3})")]
        public static partial void ScaledUp(ILogger logger, int WorkerCount, double Pressure);

        [LoggerMessage(
            EventId = 2012,
            Level = LogLevel.Information,
            Message = "Ingestion worker pool scaled down to {WorkerCount} (pressure={Pressure:F3})")]
        public static partial void ScaledDown(ILogger logger, int WorkerCount, double Pressure);

        [LoggerMessage(
            EventId = 2013,
            Level = LogLevel.Warning,
            Message = "Ingestion worker faulted; remaining workers continue")]
        public static partial void WorkerFaulted(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2014,
            Level = LogLevel.Information,
            Message = "Ingestion worker pool stopped (workers={WorkerCount})")]
        public static partial void PoolStopped(ILogger logger, int WorkerCount);
    }
}

namespace VectorNNTPD.TransitService.ArticleIngestion
{
    /// <summary>
    /// One sample of Transit ingestion-queue pressure used by the worker-pool scaler.
    /// </summary>
    /// <param name="Pressure">
    /// Combined pressure in <c>[0, 1]</c>: the greater of byte utilisation and a
    /// waiting-producer signal (<c>1</c> when any producer waits for byte budget).
    /// </param>
    /// <param name="QueuedBytes">Current reserved payload bytes in the ingestion queue.</param>
    /// <param name="MemoryLimitBytes">Configured Transit queue byte budget.</param>
    /// <param name="ArticleCount">Current queued article count.</param>
    /// <param name="WaitingProducers">Producers blocked on the byte budget.</param>
    public readonly record struct IngestionPressureSnapshot(
        double Pressure,
        long QueuedBytes,
        long MemoryLimitBytes,
        int ArticleCount,
        int WaitingProducers)
    {
        /// <summary>Builds a snapshot from the live ingestion queue.</summary>
        public static IngestionPressureSnapshot FromQueue(IArticleIngestionQueue queue)
        {
            ArgumentNullException.ThrowIfNull(queue);
            var limit = queue.MemoryLimitBytes;
            var bytes = queue.QueuedBytes;
            var waiting = queue.WaitingProducerCount;
            var utilisation = limit <= 0 ? 0d : Math.Clamp(bytes / (double)limit, 0d, 1d);
            var waitingSignal = waiting > 0 ? 1d : 0d;
            return new IngestionPressureSnapshot(
                Math.Max(utilisation, waitingSignal),
                bytes,
                limit,
                queue.Count,
                waiting);
        }
    }
}

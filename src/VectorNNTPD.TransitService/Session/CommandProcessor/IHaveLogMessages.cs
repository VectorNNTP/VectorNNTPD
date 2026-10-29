using VectorNNTPD.TransitService.ArticleIngestion;

namespace VectorNNTPD.TransitService.Session.CommandProcessor
{
    /// <summary>Source-generated IHAVE receive/queue diagnostics. Does not log bodies or headers.</summary>
    internal static partial class IHaveLogMessages
    {
        [LoggerMessage(
            EventId = 1700,
            Level = LogLevel.Information,
            Message = "[{Client}] IHAVE {MessageId} size={Size} pipeReads={PipeReads} receiveMs={ReceiveMs:F1} queued={Queued}")]
        public static partial void Received(
            ILogger logger,
            string Client,
            string MessageId,
            int Size,
            int PipeReads,
            double ReceiveMs,
            bool Queued);

        [LoggerMessage(
            EventId = 1710,
            Level = LogLevel.Warning,
            Message = "IHAVE deferred: TransitQueueMemoryLimit exhausted (queued {QueuedBytes}/{MemoryLimit} bytes, {Count} articles)")]
        public static partial void QueueBudgetExhausted(
            ILogger logger,
            long QueuedBytes,
            long MemoryLimit,
            int Count);
    }

    /// <summary>Rate-limits IHAVE queue-budget exhaustion warnings.</summary>
    internal static class IHaveAdmissionLog
    {
        internal static TimeSpan WarningInterval { get; set; } = TimeSpan.FromSeconds(5);

        private static long _nextWarningTicks;

        /// <summary>Emits at most one budget-exhausted warning per <see cref="WarningInterval"/>.</summary>
        internal static void BudgetExhausted(ILogger logger, IArticleIngestionQueue queue)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(queue);

            var now = DateTime.UtcNow.Ticks;
            var next = Volatile.Read(ref _nextWarningTicks);
            if (now < next)
            {
                return;
            }

            var until = now + WarningInterval.Ticks;
            if (Interlocked.CompareExchange(ref _nextWarningTicks, until, next) != next)
            {
                return;
            }

            IHaveLogMessages.QueueBudgetExhausted(
                logger,
                queue.QueuedBytes,
                queue.MemoryLimitBytes,
                queue.Count);
        }

        /// <summary>Resets rate-limit state for tests.</summary>
        internal static void ResetForTests()
        {
            Volatile.Write(ref _nextWarningTicks, 0);
            WarningInterval = TimeSpan.FromSeconds(5);
        }
    }
}

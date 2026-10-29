namespace VectorNNTPD.ReaderService.RabbitMq.ArticleWork
{
    /// <summary>Fixed article-work RPC deadlines. These are not configuration keys.</summary>
    internal static class ArticleWorkRpcTiming
    {
        /// <summary>
        /// Maximum total Backfill Scheduler lookup duration measured from request start.
        /// Also the per-attempt wait budget (remaining time until this deadline).
        /// </summary>
        internal static readonly TimeSpan LookupDeadline = TimeSpan.FromSeconds(5);
    }
}

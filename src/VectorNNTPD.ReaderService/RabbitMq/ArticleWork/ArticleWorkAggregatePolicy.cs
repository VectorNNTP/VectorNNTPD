namespace VectorNNTPD.ReaderService.RabbitMq.ArticleWork
{
    /// <summary>
    /// NNTPD policy for sequential Backfill Scheduler attempts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// BackFiller v1 outcomes classify a single work item. The scheduler publishes at most
    /// one ArticleWork request at a time and never fans out concurrently.
    /// </para>
    /// <para>
    /// Attempt policy:
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <see cref="ArticleWorkOutcome.Success"/> completes the logical lookup immediately.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <see cref="ArticleWorkOutcome.ArticleNotFound"/> is a definitive, lookup-terminal
    /// miss (provider <c>430</c>). BackFiller ACKs the delivery. NNTPD must not publish
    /// another request to a different backbone, requeue, or invent a replacement work item.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <see cref="ArticleWorkOutcome.InvalidArticle"/> is definitive for the selected
    /// backbone. BackFiller ACKs the delivery. NNTPD may publish a NEW request to another
    /// eligible backbone (unusable article on one provider is not proof of absence elsewhere).
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <see cref="ArticleWorkOutcome.InvalidRequest"/> is also attempt-terminal for the
    /// selected backbone (protocol rejection from that destination); another eligible
    /// backbone may still be attempted.
    /// </description>
    /// </item>
    /// </list>
    /// </para>
    /// <para>
    /// In-flight attempts cannot be cancelled on the broker. A per-attempt wait that
    /// expires without a definitive wire outcome ends the logical lookup without starting
    /// another backbone (avoids duplicate upstream retrieval).
    /// </para>
    /// </remarks>
    internal static class ArticleWorkAggregatePolicy
    {
        /// <summary>Returns whether this outcome completes the whole logical lookup successfully.</summary>
        internal static bool IsLookupSuccess(ArticleWorkOutcome outcome) =>
            outcome == ArticleWorkOutcome.Success;

        /// <summary>
        /// Returns whether this wire outcome ends the current backbone attempt
        /// (success or definitive absence/rejection).
        /// </summary>
        internal static bool IsAttemptTerminal(ArticleWorkOutcome outcome) =>
            outcome is ArticleWorkOutcome.Success
                or ArticleWorkOutcome.ArticleNotFound
                or ArticleWorkOutcome.InvalidArticle
                or ArticleWorkOutcome.InvalidRequest;

        /// <summary>
        /// Returns whether the scheduler may try another eligible backbone after this outcome.
        /// </summary>
        /// <remarks>
        /// <see cref="ArticleWorkOutcome.ArticleNotFound"/> is intentionally omitted: a definitive
        /// provider <c>430</c> ends the entire logical lookup.
        /// </remarks>
        internal static bool ShouldTryNextBackbone(ArticleWorkOutcome outcome) =>
            outcome is ArticleWorkOutcome.InvalidArticle
                or ArticleWorkOutcome.InvalidRequest;

        /// <summary>Legacy name: only Success completed the old parallel aggregate.</summary>
        internal static bool IsAggregateTerminal(ArticleWorkOutcome outcome) =>
            IsLookupSuccess(outcome);
    }
}

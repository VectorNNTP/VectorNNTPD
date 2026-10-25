namespace VectorNNTPD.BackFillerService.ArticleWork
{
    /// <summary>
    /// Article-work classification used for disposition planning.
    /// </summary>
    /// <remarks>
    /// Terminal ACK + correlated reply outcomes are <see cref="Success"/>, <see cref="ArticleNotFound"/>,
    /// <see cref="InvalidArticle"/>, <see cref="InvalidRequest"/> (when replyable),
    /// <see cref="ProviderFailure"/>, <see cref="UnexpectedFailure"/>, and <see cref="RetentionRejected"/>.
    /// <see cref="Cancelled"/> is the cooperative cancel/shutdown path: NACK requeue while the delivery
    /// limit allows, without a terminal RPC reply. See <see cref="ArticleWorkDispositionPlanner"/>.
    /// </remarks>
    internal enum ArticleWorkOutcome
    {
        /// <summary>Article recovered and retained for NNTP delivery. ACK + Success reply.</summary>
        Success = 0,

        /// <summary>
        /// Provider reported a definitive miss (NNTP 430 or ARTICLE 451). ACK + ArticleNotFound reply.
        /// </summary>
        ArticleNotFound = 1,

        /// <summary>
        /// Provider ARTICLE is permanently unusable (parse/yEnc failure, size ceiling, Message-ID mismatch).
        /// Terminal: publish response and ACK without requeue.
        /// </summary>
        InvalidArticle = 2,

        /// <summary>Malformed protocol payload or AMQP metadata. ACK; publish when replyable.</summary>
        InvalidRequest = 3,

        /// <summary>
        /// Provider or infrastructure failure for this lookup. ACK + ProviderFailure reply (not NACK-requeued).
        /// </summary>
        ProviderFailure = 4,

        /// <summary>
        /// Processing cancelled or shut down before a terminal reply. NACK requeue while retries remain; no reply.
        /// </summary>
        Cancelled = 5,

        /// <summary>
        /// Unexpected handler or pipeline failure for this delivery. ACK + UnexpectedFailure reply (not NACK-requeued).
        /// </summary>
        UnexpectedFailure = 6,

        /// <summary>
        /// Retrieval succeeded, but retention could not admit the payload.
        /// Distinct from <see cref="ArticleNotFound"/> and <see cref="ProviderFailure"/>.
        /// ACK + RetentionRejected reply (not NACK-requeued).
        /// </summary>
        RetentionRejected = 7,
    }
}

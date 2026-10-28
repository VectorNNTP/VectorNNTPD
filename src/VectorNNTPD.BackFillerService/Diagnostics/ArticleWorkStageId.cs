namespace VectorNNTPD.BackFillerService.Diagnostics
{
    /// <summary>
    /// Named stages recorded by <see cref="ArticleWorkStageProfiler"/>.
    /// Nested stages are documented on each member; totals must not be summed blindly.
    /// </summary>
    internal enum ArticleWorkStageId
    {
        /// <summary>
        /// <see cref="ArticleWork.ArticleWorkDeliveryPipeline.ProcessAsync"/> wall from entry through reply
        /// publish attempt (settlement excluded). Inclusive of handler and reply publish.
        /// </summary>
        PipelineTotal = 0,

        /// <summary>
        /// <see cref="ArticleWork.ProviderArticleWorkHandler.HandleAsync"/> wall. Nested inside
        /// <see cref="PipelineTotal"/>. Inclusive of retrieve, parse-in-callback, and retain.
        /// </summary>
        HandlerTotal = 1,

        /// <summary>
        /// <see cref="Nntp.NntpSessionPool.AcquireAsync"/> wait for a session lease. Nested inside
        /// <see cref="HandlerTotal"/>. Exclusive of ARTICLE exchange.
        /// </summary>
        SessionAcquire = 2,

        /// <summary>
        /// Wait for <see cref="Nntp.NntpProviderSession"/> busy gate before ARTICLE. Nested inside
        /// <see cref="NntpDownload"/>.
        /// </summary>
        SessionBusyWait = 3,

        /// <summary>
        /// ARTICLE command write through status-line read. Nested inside <see cref="NntpDownload"/>.
        /// Exclusive of payload receive.
        /// </summary>
        ArticleCommandToStatus = 4,

        /// <summary>
        /// Multiline ARTICLE body receive including the synchronous payload consumer callback
        /// (canonical parse). Nested inside <see cref="NntpDownload"/>. Inclusive of
        /// <see cref="ParseCanonical"/>.
        /// </summary>
        ArticlePayloadReceive = 5,

        /// <summary>
        /// Canonical parse / materialize inside the payload callback. Nested inside
        /// <see cref="ArticlePayloadReceive"/> (do not add to parent).
        /// </summary>
        ParseCanonical = 6,

        /// <summary>
        /// <see cref="Nntp.NntpProviderSession.DownloadArticleAsync"/> wall after the lease is held.
        /// Nested inside <see cref="HandlerTotal"/>. Inclusive of busy wait, command, payload, parse.
        /// Exclusive of <see cref="SessionAcquire"/>.
        /// </summary>
        NntpDownload = 7,

        /// <summary>
        /// <see cref="Retention.IArticleRetentionAuthority.RetainCanonical"/> wall. Nested inside
        /// <see cref="HandlerTotal"/>. Exclusive of NNTP.
        /// </summary>
        Retain = 8,

        /// <summary>
        /// Confirmed reply publication attempt (<see cref="ArticleWork.ArticleWorkDeliveryPipeline"/>
        /// → <see cref="ArticleWork.ArticleWorkResponsePublisher.PublishAsync"/>). Nested inside
        /// <see cref="PipelineTotal"/>. Inclusive of serialize, publish-gate wait, and confirm.
        /// Exclusive of handler.
        /// </summary>
        ReplyPublish = 9,

        /// <summary>
        /// <see cref="ArticleWork.ArticleWorkResponseWireProtocol.SerializeV1"/> before the publish gate.
        /// Nested inside <see cref="ReplyPublish"/>. Exclusive of gate wait and confirm.
        /// </summary>
        ReplySerialize = 10,

        /// <summary>
        /// Wait for a <see cref="ArticleWork.ArticleWorkResponsePublisher"/> <c>_publishGate</c> permit
        /// (capacity <see cref="ArticleWork.ArticleWorkResponsePublisher.MaxConcurrentConfirmedPublications"/>).
        /// Nested inside <see cref="ReplyPublish"/>. Exclusive of serialize and of work performed while holding a permit.
        /// </summary>
        ReplyPublishGateWait = 11,

        /// <summary>
        /// Work while holding a <c>_publishGate</c> permit: channel ensure + confirmed publication
        /// (RabbitMQ.Client <c>BasicPublishAsync</c> with publisher confirmation tracking — confirm
        /// completion is not separately observable). Nested inside <see cref="ReplyPublish"/>.
        /// Exclusive of gate wait. Multiple operations may hold this interval concurrently up to the gate capacity.
        /// </summary>
        ReplyPublishConfirm = 12,
    }
}

namespace VectorNNTPD.BackFillerService.ArticleWork
{
    /// <summary>
    /// Source-generated Article Work consumer and response-publisher logs.
    /// Never includes payloads or credentials.
    /// </summary>
    internal static partial class ArticleWorkLogMessages
    {
        /// <summary>
        /// Writes an Information event after a consumption session gets a current connection, and before it opens a channel.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="backbone">Provider backbone of the session being started.</param>
        /// <param name="queue">Provider queue the session will consume.</param>
        /// <param name="generation">Connection generation the channel is opened against.</param>
        [LoggerMessage(
            EventId = 5300,
            Level = LogLevel.Information,
            Message = "Article Work consumer starting backbone={Backbone} queue={Queue} generation={Generation}")]
        internal static partial void ConsumerStarting(ILogger logger, string backbone, string queue, long generation);

        /// <summary>
        /// Writes an Information event after consume registration succeeds and the session is running.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="backbone">Provider backbone of the running session.</param>
        /// <param name="queue">Provider queue being consumed.</param>
        /// <param name="generation">Connection generation of the consumption channel.</param>
        /// <param name="consumerTag">Consumer tag returned by registration.</param>
        [LoggerMessage(
            EventId = 5301,
            Level = LogLevel.Information,
            Message = "Article Work consumer running backbone={Backbone} queue={Queue} generation={Generation} consumerTag={ConsumerTag}")]
        internal static partial void ConsumerRunning(ILogger logger, string backbone, string queue, long generation, string consumerTag);

        /// <summary>
        /// Writes an Information event when retirement has moved a session to retiring and the session was not yet stopped.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="backbone">Provider backbone of the retiring session.</param>
        /// <param name="generation">Connection generation captured when the session started. Unchanged by retirement.</param>
        [LoggerMessage(
            EventId = 5302,
            Level = LogLevel.Information,
            Message = "Article Work consumer retiring backbone={Backbone} generation={Generation}")]
        internal static partial void ConsumerRetiring(ILogger logger, string backbone, long generation);

        /// <summary>
        /// Writes an Information event after the consumption channel is disposed and the session state is stopped.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="backbone">Provider backbone of the stopped session.</param>
        /// <param name="generation">Connection generation captured when the session started.</param>
        [LoggerMessage(
            EventId = 5303,
            Level = LogLevel.Information,
            Message = "Article Work consumer stopped backbone={Backbone} generation={Generation}")]
        internal static partial void ConsumerStopped(ILogger logger, string backbone, long generation);

        /// <summary>
        /// Writes a Warning event after the pipeline returns <see cref="ArticleWorkOutcome.InvalidRequest"/> for a delivery.
        /// The reason is produced during the initial bounded parse; the message body is not reparsed for diagnostics.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="backbone">Provider backbone of the session that processed the delivery.</param>
        /// <param name="generation">Delivery connection generation.</param>
        /// <param name="deliveryTag">Channel-scoped delivery tag.</param>
        /// <param name="reason">
        /// Bounded failure reason from the pipeline's parser. Examples: "RabbitMQ article-work payload exceeds WorkRequestMaxPayloadBytes",
        /// "Invalid JSON", "Missing required field MessageId". This method logs the text and does not throw.
        /// </param>
        [LoggerMessage(
            EventId = 5304,
            Level = LogLevel.Warning,
            Message = "Article Work request rejected backbone={Backbone} generation={Generation} deliveryTag={DeliveryTag} reason={Reason}")]
        internal static partial void RequestRejected(ILogger logger, string backbone, long generation, ulong deliveryTag, string reason);

        /// <summary>
        /// Writes a Warning event when active processing finds the session channel missing, closed, or on a different generation, and skips settlement.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="backbone">Provider backbone of the session.</param>
        /// <param name="generation">Delivery connection generation that was not settled.</param>
        /// <param name="deliveryTag">Channel-scoped delivery tag that was not settled.</param>
        [LoggerMessage(
            EventId = 5305,
            Level = LogLevel.Warning,
            Message = "Article Work settlement skipped on stale channel backbone={Backbone} generation={Generation} deliveryTag={DeliveryTag}")]
        internal static partial void StaleSettlementSkipped(ILogger logger, string backbone, long generation, ulong deliveryTag);

        /// <summary>
        /// Writes an Error event when session start fails, before the exception propagates.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="backbone">Provider backbone that failed to start.</param>
        /// <param name="queue">Provider queue that failed to start.</param>
        /// <param name="reason">
        /// Caller-supplied failure text. The session passes <see cref="Exception.Message"/>.
        /// This method logs that text and does not throw.
        /// </param>
        [LoggerMessage(
            EventId = 5306,
            Level = LogLevel.Error,
            Message = "Article Work consumer failed to start backbone={Backbone} queue={Queue}: {Reason}")]
        internal static partial void ConsumerStartFailed(ILogger logger, string backbone, string queue, string reason);

        /// <summary>
        /// Writes an Error event when a connection-replacement rebuild, a capacity-triggered reconciling, or the shutdown wait for that rebuild fails.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="reason">
        /// Caller-supplied failure text. Callers pass <see cref="Exception.Message"/> and continue or rethrow themselves.
        /// This method logs that text and does not throw.
        /// </param>
        [LoggerMessage(
            EventId = 5307,
            Level = LogLevel.Error,
            Message = "Article Work consumers failed to rebuild after connection replacement: {Reason}")]
        internal static partial void ConsumerReplaceFailed(ILogger logger, string reason);

        /// <summary>
        /// Writes an Information event during retirement, after the retiring event, with the shutdown snapshot captured at session construction.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="backbone">Provider backbone being retired.</param>
        /// <param name="generation">Connection generation captured when the session started.</param>
        /// <param name="drainQueuedWork">Captured policy for admitting queued work during retirement.</param>
        /// <param name="finishActiveArticles">Captured policy for letting active work finish during retirement.</param>
        /// <param name="graceSeconds">
        /// Whole seconds of the captured grace value. The session logs this value and does not start a timer from it.
        /// </param>
        [LoggerMessage(
            EventId = 5308,
            Level = LogLevel.Information,
            Message = "Article Work consumer shutdown policy backbone={Backbone} generation={Generation} drainQueuedWork={DrainQueuedWork} finishActiveArticles={FinishActiveArticles} graceSeconds={GraceSeconds}")]
        internal static partial void ConsumerShutdownPolicy(
            ILogger logger,
            string backbone,
            long generation,
            bool drainQueuedWork,
            bool finishActiveArticles,
            int graceSeconds);

        /// <summary>
        /// Writes a Warning event when the retirement shutdown token fires, before queued and active work are cancelled.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="backbone">Provider backbone whose shutdown grace callback ran.</param>
        /// <param name="generation">Connection generation captured when the session started.</param>
        [LoggerMessage(
            EventId = 5309,
            Level = LogLevel.Warning,
            Message = "Article Work consumer shutdown grace expired backbone={Backbone} generation={Generation}")]
        internal static partial void ConsumerShutdownGraceExpired(ILogger logger, string backbone, long generation);

        /// <summary>
        /// Writes an Information event when a publish-channel rebuild has a current connection and before the channel is created.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="generation">Connection generation the new publication channel is opened against.</param>
        [LoggerMessage(
            EventId = 5310,
            Level = LogLevel.Information,
            Message = "Article Work response publisher starting generation={Generation}")]
        internal static partial void PublisherStarting(ILogger logger, long generation);

        /// <summary>
        /// Writes an Information event after a startup installs a publication channel and the publisher state is running.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="generation">Installed publish-channel generation, or zero when no channel is installed.</param>
        [LoggerMessage(
            EventId = 5311,
            Level = LogLevel.Information,
            Message = "Article Work response publisher running generation={Generation}")]
        internal static partial void PublisherRunning(ILogger logger, long generation);

        /// <summary>
        /// Writes an Information event at the start of disposal, after the publisher enters retiring and before replacement work is awaited.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="generation">Installed publish-channel generation at retirement, or zero when no channel is installed.</param>
        [LoggerMessage(
            EventId = 5312,
            Level = LogLevel.Information,
            Message = "Article Work response publisher retiring generation={Generation}")]
        internal static partial void PublisherRetiring(ILogger logger, long generation);

        /// <summary>
        /// Writes an Information event after the publication channel is cleared and the publisher state is stopped.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="generation">
        /// Channel generation after disposal. The publisher clears the channel before this call, so the value is zero.
        /// </param>
        [LoggerMessage(
            EventId = 5313,
            Level = LogLevel.Information,
            Message = "Article Work response publisher stopped generation={Generation}")]
        internal static partial void PublisherStopped(ILogger logger, long generation);

        /// <summary>
        /// Writes an Error event when the publisher startup fails, before the exception propagates and start is allowed to retry.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="reason">
        /// Caller-supplied failure text. The publisher passes <see cref="Exception.Message"/>.
        /// This method logs that text and does not throw.
        /// </param>
        [LoggerMessage(
            EventId = 5314,
            Level = LogLevel.Error,
            Message = "Article Work response publisher failed to start: {Reason}")]
        internal static partial void PublisherStartFailed(ILogger logger, string reason);

        /// <summary>
        /// Writes an Error event when a connection-replacement channel rebuild fails, or when disposal's wait for that rebuild fails.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="reason">
        /// Caller-supplied failure text. Callers pass <see cref="Exception.Message"/>.
        /// The rebuild path logs and continues; disposal logs and continues. This method does not throw.
        /// </param>
        [LoggerMessage(
            EventId = 5315,
            Level = LogLevel.Error,
            Message = "Article Work response publisher failed to rebuild after connection replacement: {Reason}")]
        internal static partial void PublisherReplaceFailed(ILogger logger, string reason);

        /// <summary>
        /// Writes a Warning event when publication fails for a reason other than <see cref="OperationCanceledException"/>, before that exception propagates.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="generation">
        /// Publish-channel generation captured for the attempt, or zero when failure happens before a channel generation is captured.
        /// </param>
        /// <param name="requestId">
        /// Caller-supplied request identity text. The publisher passes the <c>D</c> format, or <c>(none)</c> when the intent has no request id.
        /// </param>
        /// <param name="outcome">
        /// Caller-supplied outcome text. The publisher passes the outcome's <c>ToString()</c> name, not the wire name.
        /// </param>
        /// <param name="reason">
        /// Caller-supplied failure text. The publisher passes <see cref="Exception.Message"/> and then rethrows.
        /// This method logs that text and does not throw.
        /// </param>
        [LoggerMessage(
            EventId = 5316,
            Level = LogLevel.Warning,
            Message = "Article Work response publication failed generation={Generation} requestId={RequestId} outcome={Outcome}: {Reason}")]
        internal static partial void PublicationFailed(
            ILogger logger,
            long generation,
            string requestId,
            string outcome,
            string reason);

        /// <summary>
        /// Writes a Debug event after the broker confirms a response, and the publication generation and channel are still current.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="generation">Publish-channel generation that was confirmed.</param>
        /// <param name="requestId">
        /// Caller-supplied request identity text. The publisher passes the <c>D</c> format, or <c>(none)</c> when the intent has no request id.
        /// </param>
        /// <param name="correlationId">AMQP correlation id echoed on the confirmed publication.</param>
        /// <param name="outcome">Wire outcome name supplied by the caller.</param>
        [LoggerMessage(
            EventId = 5317,
            Level = LogLevel.Debug,
            Message = "Article Work response published and confirmed generation={Generation} requestId={RequestId} correlationId={CorrelationId} outcome={Outcome}")]
        internal static partial void PublicationConfirmed(
            ILogger logger,
            long generation,
            string requestId,
            string correlationId,
            string outcome);

        /// <summary>
        /// Writes an Information event at the end of a reconciled pass, including a pass that starts no sessions because topology is not ready.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="sessionCount">Tracked consume sessions after the pass. This is not the count started by that pass.</param>
        [LoggerMessage(
            EventId = 5318,
            Level = LogLevel.Information,
            Message = "Article Work consumer reconcile completed sessions={SessionCount}")]
        internal static partial void ConsumerReconcileCompleted(ILogger logger, int sessionCount);

        /// <summary>
        /// Writes an Information event after one backbone's provider exchange, quorum queue, and binding are declared.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="backbone">Backbone whose topology declaration succeeded.</param>
        /// <param name="queue">Provider entity name that was declared.</param>
        [LoggerMessage(
            EventId = 5319,
            Level = LogLevel.Information,
            Message = "Article Work provider topology declared backbone={Backbone} queue={Queue}")]
        internal static partial void ProviderTopologyDeclared(ILogger logger, string backbone, string queue);

        /// <summary>
        /// Writes an Error event when provider topology cannot be declared because the connection is not ready, channel creation fails, or that backbone's declaration fails.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="backbone">Backbone whose declaration did not succeed.</param>
        /// <param name="queue">Provider entity name that was not left ready.</param>
        /// <param name="reason">
        /// Caller-supplied failure text. The service passes <see cref="Exception.Message"/> or a fixed not-ready message.
        /// <see cref="OperationCanceledException"/> is not logged here. This method does not throw.
        /// </param>
        [LoggerMessage(
            EventId = 5320,
            Level = LogLevel.Error,
            Message = "Article Work provider topology declaration failed backbone={Backbone} queue={Queue}: {Reason}")]
        internal static partial void ProviderTopologyDeclareFailed(
            ILogger logger,
            string backbone,
            string queue,
            string reason);

        /// <summary>
        /// Writes the single Information event for one processed ARTICLE after validation or a terminal outcome.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="messageId">Request Message-ID, or <c>(none)</c> when it was not resolved.</param>
        /// <param name="backbone">Request backbone, or <c>(none)</c> when it was not resolved.</param>
        /// <param name="outcome"><c>Found</c>, <c>NotFound</c>, <c>ValidationFailed</c>, or <c>Failed</c>.</param>
        /// <param name="reason">Validation failure reason. Empty when <paramref name="outcome"/> is not <c>ValidationFailed</c>.</param>
        /// <param name="elapsed">
        /// Elapsed seconds from the ARTICLE send. Zero when ARTICLE was not sent.
        /// The message renders this value with three fractional digits (<c>0.000</c>).
        /// </param>
        [LoggerMessage(
            EventId = 5321,
            Level = LogLevel.Information,
            Message = "Article processed messageId={MessageId} backbone={Backbone} outcome={Outcome} reason={Reason} elapsed={Elapsed:0.000} s")]
        internal static partial void ArticleProcessed(
            ILogger logger,
            string messageId,
            string backbone,
            string outcome,
            string reason,
            double elapsed);

        /// <summary>
        /// Writes an Error event when the periodic reconcile loop will retry after backoff.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Reason">Exception message, or a stable reason when topology for a desired consumer is not ready. The loop stays on the same task.</param>
        [LoggerMessage(
            EventId = 5322,
            Level = LogLevel.Error,
            Message = "Article Work reconcile loop failed and will retry: {Reason}")]
        internal static partial void ConsumerReconcileFailed(ILogger logger, string Reason);
    }
}

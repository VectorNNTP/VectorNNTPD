using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.NntpDb;
using VectorNNTPD.TransitService.History;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Session.Framing;
using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.Session.Commands
{
    /// <summary>
    /// IHAVE command as defined by RFC 3977, Section 6.3.2.
    /// </summary>
    /// <remarks>
    /// Not pipelined. Serial two-stage exchange: HistoryDB peek → non-blocking
    /// Transit queue probe → 335/435/436 → raw article receive (frame terminator,
    /// own stuffed wire) → destuff + <c>ArticleRecordFactory</c> → non-blocking
    /// <see cref="IArticleIngestionQueue.TryAdmit"/> → 235/436/437. IHAVE never
    /// waits for queue memory. TAKETHIS is not used and is not modified. Common
    /// owns article parse/materialize. IHAVE-specific behaviour is History peek,
    /// non-blocking probe/admit, and 335/235/435/436/437 timing. After CanonicalV1,
    /// the connection's Receive article-type mask and newsgroup expression are applied.
    /// The command
    /// Message-ID is used for History and is not matched against the article
    /// Message-ID (RFC 3977 §6.3.2 permits a mismatch). A History hit before
    /// <c>335</c> is <c>436 Transfer not possible; try again later</c> when the
    /// connection's <c>DeferOnDuplicate</c> is true, and <c>435 Article not wanted</c>
    /// otherwise. Neither response reads an article. TAKETHIS does not read that flag.
    /// Incomplete articles that cannot become CanonicalV1 are rejected with 437 after 335.
    /// </remarks>
    internal static class IHave
    {
        private static ILogger Logger => NntpCommandLoggers.For(typeof(IHave));

        /// <summary>Handles <c>IHAVE</c> (RFC 3977 §6.3.2).</summary>
        public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(Logger, context, "IHAVE", ExecuteAsync, cancellationToken);

        private static async ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
        {
            var messageId = context.ArgumentMemory;
            var history = context.Session.HistoryDb;
            var peek = history is null
                ? HistoryLookupResult.Unseen
                : await history.PeekAsync(messageId, cancellationToken).ConfigureAwait(false);

            if (peek == HistoryLookupResult.Seen)
            {
                var defer = Check.DeferOnDuplicate(context.Session);
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        defer ? NntpResponses.IhaveTryLater : NntpResponses.IhaveNotWanted,
                        defer ? NntpResponseStatus.IhaveTryLater : NntpResponseStatus.IhaveNotWanted,
                        cancellationToken)
                    .ConfigureAwait(false);
                context.CompletionDetail = defer ? "deferred duplicate" : "not wanted";
                return;
            }

            if (peek == HistoryLookupResult.Unavailable)
            {
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.IhaveTryLater,
                        NntpResponseStatus.IhaveTryLater,
                        cancellationToken)
                    .ConfigureAwait(false);
                context.CompletionDetail = "try later";
                return;
            }

            var queue = context.Session.ArticleIngestion;
            if (!queue.TryProbeCapacity())
            {
                IHaveAdmissionLog.BudgetExhausted(Logger, queue);
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.IhaveTryLater,
                        NntpResponseStatus.IhaveTryLater,
                        cancellationToken)
                    .ConfigureAwait(false);
                context.CompletionDetail = "queue full";
                return;
            }

            await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.IhaveSendArticle,
                        NntpResponseStatus.IhaveSendArticle,
                        cancellationToken)
                .ConfigureAwait(false);

            NntpArticlePolicyCapture.Capture(
                context.Session,
                queue.MaxArticleBytes,
                out var maxArticleBytes,
                out var siteNameUtf8);
            IHaveArticleReadResult read;
            try
            {
                read = await IHaveArticleReader
                    .ReadAsync(context.Connection.Input, maxArticleBytes, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested
                || context.Connection.ConnectionClosed.IsCancellationRequested)
            {
                return;
            }

            if (read.Status == NntpMultilineReadStatus.Incomplete)
            {
                return;
            }

            if (read.Status == NntpMultilineReadStatus.TooLarge)
            {
                const string tooLarge = "rejected too large";
                IngressNewsEvents.TryWriteRejected(
                    context.Session,
                    CommandMessageId(messageId),
                    437,
                    IngressNewsReasons.ArticleTooLarge,
                    read.Payload.Length);
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.IhaveRejected,
                        NntpResponseStatus.IhaveRejected,
                        cancellationToken)
                    .ConfigureAwait(false);
                context.CompletionDetail = tooLarge;
                return;
            }

            var created = siteNameUtf8 is null
                ? ArticleRecordIngress.TryCreateFromStuffedWire(
                    context.Session.ArticleParser,
                    read.Payload,
                    maxArticleBytes)
                : ArticleRecordIngress.TryCreateFromStuffedWire(
                    context.Session.ArticleParser,
                    read.Payload,
                    maxArticleBytes,
                    siteNameUtf8);
            if (!created.IsAccepted)
            {
                var recordReject = created.ParseFailure != VectorNNTPD.Common.Articles.Parsing.NntpArticleParseFailureCode.None
                    ? "rejected article record " + created.ParseFailure
                    : "rejected article record " + created.MaterializeFailure;
                IngressNewsEvents.TryWriteRejected(
                    context.Session,
                    CommandMessageId(messageId),
                    437,
                    IngressNewsReasons.ForArticleRecord(in created),
                    read.Payload.Length);
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.IhaveRejected,
                        NntpResponseStatus.IhaveRejected,
                        cancellationToken)
                    .ConfigureAwait(false);
                context.CompletionDetail = recordReject;
                return;
            }

            if (TransitReceivePolicy.TryReject(
                    context.Session.Authorization.TransitPeerPolicy,
                    created.Record,
                    out var policyReject))
            {
                IngressNewsEvents.TryWriteRejected(
                    context.Session,
                    CommandMessageId(messageId),
                    437,
                    IngressNewsReasons.ForExistingRejectDetail(policyReject),
                    created.Record.ArtSize);
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.IhaveRejected,
                        NntpResponseStatus.IhaveRejected,
                        cancellationToken)
                    .ConfigureAwait(false);
                context.CompletionDetail = policyReject;
                return;
            }

            var messageIdText = System.Text.Encoding.ASCII.GetString(messageId.Span);
            var inbound = ArticleRecordIngress.CreateQueued(
                messageIdText,
                created.Record,
                context.Session.ClientIdentity,
                DateTimeOffset.UtcNow,
                InboundArticleProducer.IHave,
                IngressNewsEvents.SnapshotInboundFeed(context.Session));

            if (IngressNewsDisposition.IsUncarriedWantTrashRejection(
                    inbound,
                    context.Session.Transit,
                    context.Session.NewsgroupCatalogue,
                    out var uncarriedReason))
            {
                IngressNewsEvents.TryWriteRejected(
                    context.Session,
                    messageIdText,
                    437,
                    uncarriedReason,
                    inbound.Payload.Length);
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.IhaveRejected,
                        NntpResponseStatus.IhaveRejected,
                        cancellationToken)
                    .ConfigureAwait(false);
                context.CompletionDetail = IngressNewsEvents.NewsgroupNotCarried;
                return;
            }

            var enqueue = queue.TryAdmit(inbound);
            if (enqueue == ArticleEnqueueResult.Rejected)
            {
                const string budget = "rejected exceeds queue budget";
                IngressNewsEvents.TryWriteRejected(
                    context.Session,
                    messageIdText,
                    437,
                    IngressNewsReasons.QueueCapacityExceeded,
                    inbound.Payload.Length);
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.IhaveRejected,
                        NntpResponseStatus.IhaveRejected,
                        cancellationToken)
                    .ConfigureAwait(false);
                context.CompletionDetail = budget;
                return;
            }

            if (enqueue != ArticleEnqueueResult.Accepted)
            {
                if (enqueue == ArticleEnqueueResult.Full)
                {
                    IHaveAdmissionLog.BudgetExhausted(Logger, queue);
                }

                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.IhaveTransferFailed,
                        NntpResponseStatus.IhaveTransferFailed,
                        cancellationToken)
                    .ConfigureAwait(false);
                context.CompletionDetail = enqueue == ArticleEnqueueResult.Full
                    ? "queue full"
                    : "queue unavailable";
                return;
            }

            history?.Remember(messageId);
            var logger = Logger;
            if (logger.IsEnabled(LogLevel.Information))
            {
                var client = NntpCommandLogFormat.Client(context.Session);
                IHaveLogMessages.Received(
                    logger,
                    client,
                    messageIdText,
                    read.Metrics.ArticleSize,
                    read.Metrics.PipeReads,
                    read.Metrics.ReceiveElapsed.TotalMilliseconds,
                    Queued: true);
            }

            await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.IhaveTransferredOk,
                        NntpResponseStatus.IhaveTransferredOk,
                        cancellationToken)
                .ConfigureAwait(false);
            context.CompletionDetail = "accepted";
        }

        private static string CommandMessageId(ReadOnlyMemory<byte> messageId) =>
            System.Text.Encoding.ASCII.GetString(messageId.Span);
    }
}

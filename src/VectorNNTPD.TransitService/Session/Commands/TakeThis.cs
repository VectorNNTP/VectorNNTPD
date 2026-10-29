using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.NntpDb;
using VectorNNTPD.TransitService.Authentication;
using VectorNNTPD.TransitService.Diagnostics;
using VectorNNTPD.TransitService.History;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Session.Framing;
using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.Session.Commands
{
    /// <summary>
    /// TAKETHIS command as defined by RFC 4644, Section 2.5.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Syntax: <c>TAKETHIS message-id</c> followed immediately by an NNTP multiline article.
    /// Responses: <c>239 message-id</c> (accepted into ingestion) or <c>439 message-id</c>
    /// (permanent reject). Temporary infrastructure failure uses <c>400</c> and closes the connection.
    /// </para>
    /// <para>
    /// STREAM path: the session RX task starts HistoryDB peek at the Message-ID, frames
    /// the article with <see cref="IHaveArticleReader"/> (one owned stuffed-wire buffer,
    /// terminator omitted, no destuff on RX), attaches that buffer to
    /// <see cref="TakeThisPipeline"/>, and returns. Peek and destuff/ArticleRecord
    /// run concurrently per occupied slot. Enqueue / Remember / ordered 239/439 stay
    /// on the emit gate so publication remains command-ordered. Depth bounds how
    /// many owned articles stay in flight. TAKETHIS responses flush immediately
    /// (no coalesce batch). ArticleRecord construction failure is a permanent
    /// <c>439</c>. After CanonicalV1, the connection's Receive article-type mask and
    /// newsgroup expression are applied. CHECK does not use them.
    /// </para>
    /// <para>
    /// MODE READER fallback still destuffs via <see cref="NntpMultilineDataReader"/> and
    /// overlaps HistoryDB peek with that receive.
    /// </para>
    /// </remarks>
    internal static class TakeThis
    {
        private static ILogger Logger => NntpCommandLoggers.For(typeof(TakeThis));

        /// <summary>Handles <c>TAKETHIS</c> (RFC 4644 §2.5) on the serial (non-pipeline) path.</summary>
        public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(Logger, context, "TAKETHIS", ExecuteAsync, cancellationToken);

        /// <summary>HistoryDB peek without miss reservation (same contract as CHECK and IHAVE).</summary>
        internal static ValueTask<HistoryLookupResult> PeekAsync(
            NntpSession session,
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken)
        {
            if (session.HistoryDb is not { } history)
            {
                return new ValueTask<HistoryLookupResult>(HistoryLookupResult.Unseen);
            }

            return history.PeekAsync(messageId, cancellationToken);
        }

        internal static void WriteCompletion(
            ILogger logger,
            NntpSession session,
            long startedTimestamp,
            string? detail = null,
            string? statusLine = null) =>
            NntpCommandExecution.WriteCompletion(
                logger,
                session,
                "TAKETHIS",
                System.Diagnostics.Stopwatch.GetElapsedTime(startedTimestamp),
                detail,
                statusLine);

        internal static async ValueTask FailTemporaryAsync(
            NntpSession session,
            NntpResponseWriter response,
            CancellationToken cancellationToken)
        {
            try
            {
                await response
                    .WriteLineAsync(NntpResponses.ServiceTemporarilyUnavailable, cancellationToken)
                    .ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (
                cancellationToken.IsCancellationRequested
                || session.Connection.ConnectionClosed.IsCancellationRequested
                || ex is ObjectDisposedException or InvalidOperationException)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }

            session.RequestClose();
            CommandLogMessages.TakeThisTemporaryFailure(
                Logger,
                NntpCommandLogFormat.Client(session));
        }

        private static async ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
        {
            var probe = context.Session.FeedProbe;
            probe?.RecordCommand();
            context.Session.SetActivityState(FeedSessionState.Receiving);
            var messageIdBytes = context.ArgumentSpan.ToArray();
            var lookup = PeekAsync(context.Session, messageIdBytes, cancellationToken);
            var queue = context.Session.ArticleIngestion;

            NntpArticlePolicyCapture.Capture(
                context.Session,
                queue.MaxArticleBytes,
                out var maxArticleBytes,
                out var siteNameUtf8);
            NntpMultilineReadStatus status;
            ReadOnlyMemory<byte> payload;
            var receiveStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                if (context.PreReadArticle is { } preRead)
                {
                    status = preRead.Status;
                    payload = preRead.Payload;
                }
                else if (context.Session.ReceiveStrategy == NntpReceiveStrategy.StreamDataPlane)
                {
                    var read = await IHaveArticleReader
                        .ReadAsync(context.Connection.Input, maxArticleBytes, cancellationToken)
                        .ConfigureAwait(false);
                    status = read.Status;
                    payload = read.Payload;
                }
                else
                {
                    var article = await NntpMultilineDataReader
                        .ReadArticleAsync(context.Connection.Input, maxArticleBytes, cancellationToken)
                        .ConfigureAwait(false);
                    status = article.Status;
                    payload = article.Payload;
                }
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested
                || context.Connection.ConnectionClosed.IsCancellationRequested)
            {
                return;
            }

            if (status == NntpMultilineReadStatus.Incomplete)
            {
                return;
            }

            probe?.RecordArticleReceived(
                payload.Length,
                System.Diagnostics.Stopwatch.GetTimestamp() - receiveStart);
            context.Session.RecordPeerArticleReceived(payload.Length);
            context.Session.SetActivityState(FeedSessionState.WaitingHistory);

            HistoryLookupResult peek;
            var peekStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                peek = await lookup.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested
                || context.Connection.ConnectionClosed.IsCancellationRequested)
            {
                return;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception)
            {
                peek = HistoryLookupResult.Unavailable;
            }

            probe?.RecordHistory(peek, System.Diagnostics.Stopwatch.GetTimestamp() - peekStart);

            if (status == NntpMultilineReadStatus.TooLarge)
            {
                const string tooLarge = "rejected too large";
                IngressNewsEvents.TryWriteRejected(
                    context.Session,
                    System.Text.Encoding.ASCII.GetString(messageIdBytes),
                    439,
                    IngressNewsReasons.ArticleTooLarge,
                    payload.Length);
                await EnqueueTransferReplyAsync(context, rejected: true, cancellationToken)
                    .ConfigureAwait(false);
                context.CompletionDetail = tooLarge;
                return;
            }

            if (peek == HistoryLookupResult.Unavailable || !queue.IsAccepting)
            {
                await FailTemporaryAsync(context.Session, context.Response, cancellationToken).ConfigureAwait(false);
                NntpCommandReply.TryNote(context, Logger, NntpResponseStatus.ServiceTemporarilyUnavailable);
                context.CompletionDetail = "temporary failure";
                return;
            }

            if (peek == HistoryLookupResult.Seen)
            {
                context.Session.SetActivityState(FeedSessionState.Completing);
                var seenStart = System.Diagnostics.Stopwatch.GetTimestamp();
                await EnqueueTransferReplyAsync(context, rejected: false, cancellationToken)
                    .ConfigureAwait(false);
                probe?.RecordArticleCompleted(
                    duplicate: true,
                    System.Diagnostics.Stopwatch.GetTimestamp() - seenStart);
                context.Session.SetActivityState(FeedSessionState.Idle);
                context.CompletionDetail = "accepted duplicate";
                return;
            }

            var messageId = System.Text.Encoding.ASCII.GetString(messageIdBytes);
            var stuffed = context.Session.ReceiveStrategy == NntpReceiveStrategy.StreamDataPlane
                && context.PreReadArticle is null;
            if (!TryCreateQueuedRecord(
                    context.Session,
                    payload,
                    stuffed,
                    messageId,
                    maxArticleBytes,
                    siteNameUtf8,
                    out var inbound,
                    out var recordReject,
                    out var canonicalSize))
            {
                IngressNewsEvents.TryWriteRejected(
                    context.Session,
                    messageId,
                    439,
                    IngressNewsReasons.ForExistingRejectDetail(recordReject),
                    canonicalSize != 0 ? canonicalSize : payload.Length);
                await EnqueueTransferReplyAsync(context, rejected: true, cancellationToken)
                    .ConfigureAwait(false);
                context.CompletionDetail = recordReject;
                return;
            }

            if (IngressNewsDisposition.IsUncarriedWantTrashRejection(
                    inbound,
                    context.Session.Transit,
                    context.Session.NewsgroupCatalogue,
                    out var uncarriedReason))
            {
                IngressNewsEvents.TryWriteRejected(
                    context.Session,
                    messageId,
                    439,
                    uncarriedReason,
                    inbound.Payload.Length);
                await EnqueueTransferReplyAsync(context, rejected: true, cancellationToken)
                    .ConfigureAwait(false);
                context.CompletionDetail = IngressNewsEvents.NewsgroupNotCarried;
                return;
            }

            ArticleEnqueueResult enqueue;
            try
            {
                context.Session.SetActivityState(FeedSessionState.WaitingQueue);
                var queueStart = System.Diagnostics.Stopwatch.GetTimestamp();
                enqueue = await queue.EnqueueAsync(inbound, cancellationToken).ConfigureAwait(false);
                probe?.RecordQueue(enqueue, System.Diagnostics.Stopwatch.GetTimestamp() - queueStart, payload.Length);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested
                || context.Connection.ConnectionClosed.IsCancellationRequested)
            {
                return;
            }

            if (enqueue == ArticleEnqueueResult.Unavailable)
            {
                await FailTemporaryAsync(context.Session, context.Response, cancellationToken).ConfigureAwait(false);
                NntpCommandReply.TryNote(context, Logger, NntpResponseStatus.ServiceTemporarilyUnavailable);
                context.CompletionDetail = "temporary failure";
                return;
            }

            if (enqueue == ArticleEnqueueResult.Rejected)
            {
                const string budget = "rejected exceeds queue budget";
                IngressNewsEvents.TryWriteRejected(
                    context.Session,
                    messageId,
                    439,
                    IngressNewsReasons.QueueCapacityExceeded,
                    inbound.Payload.Length);
                await EnqueueTransferReplyAsync(context, rejected: true, cancellationToken)
                    .ConfigureAwait(false);
                context.CompletionDetail = budget;
                return;
            }

            context.Session.HistoryDb?.Remember(messageIdBytes);
            context.Session.SetActivityState(FeedSessionState.Completing);
            var doneStart = System.Diagnostics.Stopwatch.GetTimestamp();
            await EnqueueTransferReplyAsync(context, rejected: false, cancellationToken)
                .ConfigureAwait(false);
            probe?.RecordArticleCompleted(
                duplicate: false,
                System.Diagnostics.Stopwatch.GetTimestamp() - doneStart);
            context.Session.SetActivityState(FeedSessionState.Idle);
            context.CompletionDetail = "accepted";
        }

        private static ValueTask EnqueueTransferReplyAsync(
            NntpCommandContext context,
            bool rejected,
            CancellationToken cancellationToken)
        {
            var prefix = rejected
                ? NntpResponses.TransferRejectedPrefix
                : NntpResponses.ArticleTransferredOkPrefix;
            var owned = NntpResponseCompose.Concat(
                prefix.Span,
                context.ArgumentSpan,
                NntpResponses.Crlf.Span);
            if (Logger.IsEnabled(LogLevel.Debug))
            {
                context.StatusLine ??= rejected
                    ? NntpCommandStatusText.FormatTakeThisRejected(context.ArgumentSpan)
                    : NntpCommandStatusText.FormatTakeThisAccepted(context.ArgumentSpan);
            }

            return context.Response.EnqueueLineImmediateAsync(owned, cancellationToken);
        }

        /// <summary>
        /// Destuffs when required, builds <see cref="ArticleRecord"/>, and returns a queue item
        /// that references ArtData. Does not enqueue.
        /// </summary>
        internal static bool TryCreateQueuedRecord(
            NntpSession session,
            ReadOnlyMemory<byte> payload,
            bool stuffed,
            string messageId,
            out InboundArticle inbound,
            out string rejectDetail,
            out int canonicalSize)
            => TryCreateQueuedRecord(
                session,
                payload,
                stuffed,
                messageId,
                session.ArticleIngestion.MaxArticleBytes,
                siteNameUtf8: null,
                out inbound,
                out rejectDetail,
                out canonicalSize);

        internal static bool TryCreateQueuedRecord(
            NntpSession session,
            ReadOnlyMemory<byte> payload,
            bool stuffed,
            string messageId,
            int maxArticleBytes,
            byte[]? siteNameUtf8,
            out InboundArticle inbound,
            out string rejectDetail,
            out int canonicalSize)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
            canonicalSize = 0;

            ArticleRecordCreateResult created;
            if (stuffed)
            {
                created = siteNameUtf8 is null
                    ? ArticleRecordIngress.TryCreateFromStuffedWire(
                        session.ArticleParser,
                        payload,
                        maxArticleBytes)
                    : ArticleRecordIngress.TryCreateFromStuffedWire(
                        session.ArticleParser,
                        payload,
                        maxArticleBytes,
                        siteNameUtf8);
            }
            else if (siteNameUtf8 is null)
            {
                created = ArticleRecordIngress.TryCreateFromDestuffed(
                    session.ArticleParser,
                    payload,
                    maxArticleBytes,
                    ArticlePathCanonicalizer.OrganizationalTrackerHost);
            }
            else
            {
                created = ArticleRecordIngress.TryCreateFromDestuffed(
                    session.ArticleParser,
                    payload,
                    maxArticleBytes,
                    siteNameUtf8);
            }

            if (!created.IsAccepted)
            {
                inbound = null!;
                rejectDetail = created.ParseFailure != NntpArticleParseFailureCode.None
                    ? "rejected article record " + created.ParseFailure
                    : "rejected article record " + created.MaterializeFailure;
                return false;
            }

            canonicalSize = created.Record.ArtSize;
            if (!ArticleTypeAccessPolicy.CanPostArticleType(session, created.Record.ArtType))
            {
                inbound = null!;
                rejectDetail = TransitReceivePolicy.ArticleTypeRejectDetail;
                return false;
            }

            if (TransitReceivePolicy.TryReject(session.Authorization.TransitPeerPolicy, created.Record, out rejectDetail))
            {
                inbound = null!;
                return false;
            }

            inbound = ArticleRecordIngress.CreateQueued(
                messageId,
                created.Record,
                session.ClientIdentity,
                DateTimeOffset.UtcNow,
                InboundArticleProducer.TakeThis,
                IngressNewsEvents.SnapshotInboundFeed(session));
            rejectDetail = string.Empty;
            return true;
        }
    }
}

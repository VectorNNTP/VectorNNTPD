using System.Text;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.History;
using VectorNNTPD.ReaderService.Moderation;
using VectorNNTPD.ReaderService.Authentication;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.PostFilter.Quota;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Session.Commands.Posting;
using VectorNNTPD.ReaderService.Session.Framing;

namespace VectorNNTPD.ReaderService.Session.Commands
{
    /// <summary>
    /// POST command as defined by RFC 3977, Section 6.3.1.
    /// </summary>
    /// <remarks>
    /// Not pipelined. First-stage <c>440</c> does not consume an article. After <c>340</c>
    /// the article is streamed from the session PipeReader into one destuffed output
    /// buffer (client headers destuffed write-through, server-owned headers at the
    /// header/body boundary for ordinary injection, body destuffed). Unapproved
    /// moderated proto-articles are submitted through <see cref="IModerationSubmissionService"/>
    /// without Injection-Info/Injection-Date and without History Peek / TryAdmit / Remember.
    /// Authorized moderator reinjection follows the ordinary injection path. History Peek,
    /// <see cref="IArticleIngestionQueue.TryAdmit"/>, Remember, and <c>240</c> stay
    /// after the terminator and after injection authorization. Downstream spool/worker
    /// processing is not awaited.
    /// </remarks>
    internal static class Post
    {
        private static ILogger Logger => NntpCommandLoggers.For(typeof(Post));

        /// <summary>Handles <c>POST</c>.</summary>
        public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(Logger, context, "POST", ExecuteAsync, cancellationToken);

        private static async ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
        {
            if (!context.Session.Authorization.PostingPermitted)
            {
                await WriteStatusAsync(
                        context,
                        NntpResponses.PostingProhibited,
                        NntpResponseStatus.PostingProhibited,
                        "posting not permitted",
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.PostSendArticle,
                    NntpResponseStatus.PostSendArticle,
                    cancellationToken)
                .ConfigureAwait(false);

            NntpArticlePolicyCapture.Capture(
                context.Session,
                context.Session.MaxArticleSize,
                out var maxArticleBytes,
                out var siteNameUtf8);
            StreamingPostReadResult read;
            try
            {
                read = await StreamingPostArticleReader
                    .ReadAsync(
                        context.Connection.Input,
                        new StreamingPostReadOptions
                        {
                            MaxArticleSize = maxArticleBytes,
                            Time = context.Session.Time,
                            NewsgroupPolicy = context.Session.NewsgroupPostingPolicy,
                            InjectionIdentity = context.Session.InjectionIdentity,
                            ClientIdentity = context.Session.ClientIdentity,
                            MailComplaintsTo = context.Session.MailComplaintsTo,
                            TraceProtector = context.Session.PostingTraceProtector,
                            ControlCancelPermitted = context.Session.Authorization.ControlCancelPermitted,
                            AuthenticatedUsername = context.Session.Authentication.IsAuthenticated
                                ? context.Session.Authentication.Username
                                : null,
                            ModeratorAuthorization = context.Session.CaptureModeratorAuthorization(),
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested
                || context.Connection.ConnectionClosed.IsCancellationRequested)
            {
                return;
            }

            if (read.Status == StreamingPostReadStatus.Incomplete)
            {
                return;
            }

            if (read.Status != StreamingPostReadStatus.Completed)
            {
                var failure = read.Status == StreamingPostReadStatus.TooLarge
                    ? new PostingFailure(PostingFailureCategory.ArticleTooLarge, "max article size exceeded")
                    : read.Failure;
                await RejectAsync(
                        context,
                        failure,
                        read.MessageId,
                        FormatGroups(read.Newsgroups),
                        read.DestuffedSize,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (read.Disposition == StreamingPostDisposition.SubmitForModeration)
            {
                await SubmitForModerationAsync(context, read, cancellationToken).ConfigureAwait(false);
                return;
            }

            var messageIdBytes = Encoding.ASCII.GetBytes(read.MessageId!);
            var history = context.Session.HistoryDb;
            if (history is not null)
            {
                var peek = await history.PeekAsync(messageIdBytes, cancellationToken).ConfigureAwait(false);
                if (peek == HistoryLookupResult.Seen)
                {
                    await RejectAsync(
                            context,
                            new PostingFailure(PostingFailureCategory.DuplicateArticle, "Message-ID already known"),
                            read.MessageId,
                            FormatGroups(read.Newsgroups),
                            read.DestuffedSize,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (peek == HistoryLookupResult.Unavailable)
                {
                    await RejectAsync(
                            context,
                            new PostingFailure(PostingFailureCategory.PersistenceFailure, "history unavailable"),
                            read.MessageId,
                            FormatGroups(read.Newsgroups),
                            read.DestuffedSize,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
            }

            if (cancellationToken.IsCancellationRequested
                || context.Connection.ConnectionClosed.IsCancellationRequested)
            {
                return;
            }

            var created = siteNameUtf8 is null
                ? ArticleRecordIngress.TryCreateFromDestuffed(
                    context.Session.ArticleParser,
                    read.Wire)
                : ArticleRecordIngress.TryCreateFromDestuffed(
                    context.Session.ArticleParser,
                    read.Wire,
                    maxArticleBytes,
                    siteNameUtf8);
            if (!created.IsAccepted)
            {
                await RejectAsync(
                        context,
                        MapArticleRecordFailure(in created),
                        read.MessageId,
                        FormatGroups(read.Newsgroups),
                        read.Wire.Length,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (!ArticleTypeAccessPolicy.CanPostArticleType(context.Session, created.Record.ArtType))
            {
                await RejectAsync(
                        context,
                        new PostingFailure(PostingFailureCategory.PolicyRejected, "arttype-capability"),
                        read.MessageId,
                        FormatGroups(read.Newsgroups),
                        created.Record.ArtSize,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var filterRequest = new PostFilterRequest(
                created.Record,
                context.Session.Authentication.Username,
                context.Session.ClientIdentity,
                context.Session.AccountPolicy,
                read.InjectionUtc,
                read.Newsgroups,
                context.Session.Time.GetUtcNow(),
                context.Session.InjectionIdentity);

            PostFilterResult filter;
            try
            {
                filter = await context.Session.PostFilter
                    .EvaluateAsync(filterRequest, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested
                || context.Connection.ConnectionClosed.IsCancellationRequested)
            {
                return;
            }

            if (filter.Decision == PostFilterDecision.Reject)
            {
                EnqueueRejectionEvidence(
                    context,
                    PostFilterRejectionEvidence.FromEvaluation(
                        filterRequest,
                        filter,
                        context.Session.Time.GetUtcNow()));
                await RejectAsync(
                        context,
                        new PostingFailure(PostingFailureCategory.PolicyRejected, filter.Reason),
                        read.MessageId,
                        FormatGroups(read.Newsgroups),
                        created.Record.ArtSize,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var lease = filter.Lease;
            if (cancellationToken.IsCancellationRequested
                || context.Connection.ConnectionClosed.IsCancellationRequested)
            {
                await ReleaseLeaseAsync(context, lease).ConfigureAwait(false);
                return;
            }

            var inbound = ArticleRecordIngress.CreateQueued(
                read.MessageId!,
                created.Record,
                context.Session.ClientIdentity,
                read.InjectionUtc,
                InboundArticleProducer.Post,
                IngressNewsEvents.SnapshotInboundFeed(context.Session));

            var enqueue = context.Session.ArticleIngestion.TryAdmit(inbound);
            if (enqueue != ArticleEnqueueResult.Accepted)
            {
                await ReleaseLeaseAsync(context, lease).ConfigureAwait(false);
                EnqueueRejectionEvidence(
                    context,
                    PostFilterRejectionEvidence.FromAdmissionFailure(
                        filterRequest,
                        filter,
                        enqueue.ToString(),
                        context.Session.Time.GetUtcNow()));
                await RejectAsync(
                        context,
                        new PostingFailure(PostingFailureCategory.PersistenceFailure, enqueue.ToString()),
                        read.MessageId,
                        FormatGroups(read.Newsgroups),
                        inbound.Payload.Length,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await CommitLeaseAsync(context, lease, read.MessageId!).ConfigureAwait(false);

            history?.Remember(messageIdBytes);
            var logger = Logger;
            if (logger.IsEnabled(LogLevel.Information))
            {
                var client = NntpCommandLogFormat.Client(context.Session);
                var groups = FormatGroups(read.Newsgroups);
                PostLogMessages.Accepted(
                    logger,
                    client,
                    read.MessageId!,
                    groups,
                    read.Wire.Length);
            }

            await WriteStatusAsync(
                    context,
                    NntpResponses.ArticleReceivedOk,
                    NntpResponseStatus.ArticleReceivedOk,
                    "accepted",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private static void EnqueueRejectionEvidence(
            NntpCommandContext context,
            PostFilterRejectionEvidence evidence)
        {
            if (context.Session.PostFilterEvidence.TryEnqueue(evidence))
            {
                return;
            }

            PostFilterLogMessages.EvidenceQueueFull(Logger, evidence.Stage, evidence.Reason);
        }

        private static async ValueTask ReleaseLeaseAsync(NntpCommandContext context, PostFilterLease? lease)
        {
            if (lease is null)
            {
                return;
            }

            var status = await lease
                .ReleaseAsync(context.Session.Time.GetUtcNow(), CancellationToken.None)
                .ConfigureAwait(false);
            PostFilterLogMessages.Released(
                Logger,
                status,
                lease.AccountName,
                lease.Token);
        }

        private static async ValueTask CommitLeaseAsync(
            NntpCommandContext context,
            PostFilterLease? lease,
            string messageId)
        {
            if (lease is null)
            {
                return;
            }

            var status = await lease
                .CommitAsync(context.Session.Time.GetUtcNow(), CancellationToken.None)
                .ConfigureAwait(false);
            if (status == PostFilterQuotaCommitStatus.Committed)
            {
                return;
            }

            if (status == PostFilterQuotaCommitStatus.Noop)
            {
                context.Session.PostFilterMetrics.RecordCommitNoop();
                PostFilterLogMessages.CommitNoop(
                    Logger,
                    lease.AccountName,
                    lease.Token,
                    messageId);
                return;
            }

            context.Session.PostFilterMetrics.RecordCommitUnavailable();
            PostFilterLogMessages.CommitUnavailable(
                Logger,
                lease.AccountName,
                lease.Token,
                messageId);
        }

        private static async ValueTask SubmitForModerationAsync(
            NntpCommandContext context,
            StreamingPostReadResult read,
            CancellationToken cancellationToken)
        {
            var submission = new ModerationSubmission
            {
                ProtoArticle = ArticleWireReconstructor.RestuffArticle(read.Wire.Span, includeTerminator: false),
                MessageId = read.MessageId ?? string.Empty,
                Newsgroups = read.Newsgroups,
                TargetModeratedGroup = read.TargetModeratedGroup ?? string.Empty,
                ModeratorAddress = read.ModeratorAddress ?? string.Empty,
                AuthenticatedUsername = context.Session.Authentication.IsAuthenticated
                    ? context.Session.Authentication.Username
                    : null,
                Sender = context.Session.ClientIdentity,
            };

            ModerationSubmissionResult result;
            try
            {
                result = await context.Session.ModerationSubmission
                    .SubmitAsync(submission, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested
                || context.Connection.ConnectionClosed.IsCancellationRequested)
            {
                return;
            }

            if (result.Status != ModerationSubmissionStatus.Accepted)
            {
                await RejectAsync(
                        context,
                        new PostingFailure(
                            PostingFailureCategory.ModerationForwardingFailed,
                            result.Detail),
                        read.MessageId,
                        FormatGroups(read.Newsgroups),
                        read.DestuffedSize,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var logger = Logger;
            if (logger.IsEnabled(LogLevel.Information))
            {
                var client = NntpCommandLogFormat.Client(context.Session);
                var groups = FormatGroups(read.Newsgroups);
                PostLogMessages.SubmittedForModeration(
                    logger,
                    client,
                    read.MessageId ?? "-",
                    groups,
                    read.TargetModeratedGroup ?? "-",
                    read.ModeratorAddress ?? "-",
                    context.Session.Authentication.Username ?? "-",
                    read.DestuffedSize);
            }

            IngressNewsEvents.TryWriteModerated(context.Session, read.MessageId, read.DestuffedSize);
            await WriteStatusAsync(
                    context,
                    NntpResponses.ArticleReceivedOk,
                    NntpResponseStatus.ArticleReceivedOk,
                    "accepted for moderation",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private static async ValueTask RejectAsync(
            NntpCommandContext context,
            PostingFailure failure,
            string? messageId,
            string? newsgroups,
            int size,
            CancellationToken cancellationToken)
        {
            var logger = Logger;
            if (logger.IsEnabled(LogLevel.Information))
            {
                var client = NntpCommandLogFormat.Client(context.Session);
                PostLogMessages.Rejected(
                    logger,
                    client,
                    failure.Category,
                    messageId ?? "-",
                    newsgroups ?? "-",
                    size,
                    failure.Detail);
            }
            IngressNewsEvents.TryWriteRejected(
                context.Session,
                messageId ?? "-",
                441,
                IngressNewsReasons.ForPostingFailure(failure),
                size);
            await WriteFailedAsync(context, cancellationToken).ConfigureAwait(false);
        }

        private static ValueTask WriteFailedAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
            WriteStatusAsync(
                context,
                NntpResponses.PostingFailed,
                NntpResponseStatus.PostingFailed,
                "posting failed",
                cancellationToken);

        private static ValueTask WriteStatusAsync(
            NntpCommandContext context,
            ReadOnlyMemory<byte> wire,
            string statusLine,
            string detail,
            CancellationToken cancellationToken)
        {
            context.StatusLine = statusLine;
            context.CompletionDetail = detail;
            return context.Response.WriteLineAsync(wire, cancellationToken);
        }

        private static string FormatGroups(string[] groups) =>
            groups.Length == 0 ? "-" : string.Join(',', groups);

        private static PostingFailure MapArticleRecordFailure(in ArticleRecordCreateResult created)
        {
            if (created.ParseFailure != NntpArticleParseFailureCode.None)
            {
                return new PostingFailure(MapParseFailure(created.ParseFailure), created.ParseFailure.ToString());
            }

            return new PostingFailure(PostingFailureCategory.InvalidPath, created.MaterializeFailure.ToString());
        }

        private static PostingFailureCategory MapParseFailure(NntpArticleParseFailureCode code) =>
            code switch
            {
                NntpArticleParseFailureCode.ArticleTooLarge => PostingFailureCategory.ArticleTooLarge,
                NntpArticleParseFailureCode.MalformedHeader
                    or NntpArticleParseFailureCode.MalformedHeaderContinuation
                    or NntpArticleParseFailureCode.MissingHeaderBodySeparator
                    or NntpArticleParseFailureCode.ContainsNul
                    or NntpArticleParseFailureCode.ContainsIllegalControlByte => PostingFailureCategory.MalformedHeader,
                NntpArticleParseFailureCode.MissingMessageId
                    or NntpArticleParseFailureCode.MissingNewsgroups
                    or NntpArticleParseFailureCode.EmptyArticle => PostingFailureCategory.MissingRequiredHeader,
                NntpArticleParseFailureCode.DuplicateMessageId
                    or NntpArticleParseFailureCode.DuplicateNewsgroups
                    or NntpArticleParseFailureCode.DuplicatePath => PostingFailureCategory.DuplicateHeader,
                NntpArticleParseFailureCode.InvalidMessageId => PostingFailureCategory.InvalidMessageId,
                NntpArticleParseFailureCode.MissingOrInvalidDate => PostingFailureCategory.InvalidDate,
                NntpArticleParseFailureCode.InvalidNewsgroups => PostingFailureCategory.InvalidNewsgroups,
                NntpArticleParseFailureCode.InvalidPath => PostingFailureCategory.InvalidPath,
                _ => PostingFailureCategory.PolicyRejected,
            };
    }
}

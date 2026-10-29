using VectorNNTPD.ReaderService.History;
using VectorNNTPD.ReaderService.Session.CommandProcessor;

namespace VectorNNTPD.ReaderService.Session.Commands
{
    /// <summary>
    /// CHECK command as defined by RFC 4644, Section 2.4.
    /// </summary>
    /// <remarks>
    /// Advises whether the server wants an article (STREAMING) using HistoryDB
    /// <see cref="IHistoryDb.PeekAsync"/> (no miss reservation):
    /// local memory hit and Redis hit are <see cref="HistoryLookupResult.Seen"/> and do not
    /// record the identifier. A double miss is 238. Redis infrastructure failure returns 431
    /// and is never treated as a 238 miss. A <see cref="HistoryLookupResult.Seen"/> result is
    /// 431 when the connection's <c>TransitPeerPolicy.DeferOnDuplicate</c> is true, and 438
    /// otherwise. TAKETHIS does not read that flag.
    /// The Message-ID is copied from session scratch into one owned wire buffer; it is not
    /// converted to a string for response construction. A completed local-hit lookup
    /// writes that CHECK reply without an extra async state machine.
    /// Consecutive authorized CHECK commands may overlap Redis lookups in the per-session
    /// <see cref="CheckPipeline"/> (fixed depth <see cref="CheckPipeline.Depth"/>).
    /// Every response passes through the pipeline emit gate and is enqueued in send order.
    /// A slot stays occupied until the writer accepts the line.
    /// </remarks>
    internal static class Check
    {
        private static ILogger Logger => NntpCommandLoggers.For(typeof(Check));

        /// <summary>Handles <c>CHECK</c> when dispatched serially (gates / non-pipeline fallback).</summary>
        public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(Logger, context, "CHECK", ExecuteAsync, cancellationToken);

        /// <summary>
        /// Looks up <paramref name="messageId"/> without composing a response or recording a miss.
        /// </summary>
        internal static ValueTask<HistoryLookupResult> LookupAsync(
            NntpSession session,
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(session);
            if (session.HistoryDb is not { } history)
            {
                return new ValueTask<HistoryLookupResult>(HistoryLookupResult.Unseen);
            }

            return history.PeekAsync(messageId, cancellationToken);
        }

        /// <summary>Composes the RFC 4644 CHECK wire line for <paramref name="result"/>.</summary>
        /// <param name="result">History lookup outcome. <see cref="HistoryLookupResult.Seen"/> is not rewritten.</param>
        /// <param name="messageId">Message-ID octets copied into the response.</param>
        /// <param name="deferOnDuplicate">
        /// Connection <c>TransitPeerPolicy.DeferOnDuplicate</c>. When <see langword="true"/>, a seen
        /// identifier uses the existing 431 line. Misses and infrastructure failures ignore it.
        /// </param>
        internal static ReadOnlyMemory<byte> Compose(
            HistoryLookupResult result,
            ReadOnlySpan<byte> messageId,
            bool deferOnDuplicate)
        {
            ReadOnlyMemory<byte> prefix;
            ReadOnlyMemory<byte> suffix;
            switch (result)
            {
                case HistoryLookupResult.Seen when deferOnDuplicate:
                    prefix = NntpResponses.CheckTryLaterPrefix;
                    suffix = NntpResponses.CheckTryLaterSuffix;
                    break;
                case HistoryLookupResult.Seen:
                    prefix = NntpResponses.CheckNotWantedPrefix;
                    suffix = NntpResponses.CheckNotWantedSuffix;
                    break;
                case HistoryLookupResult.Unavailable:
                    prefix = NntpResponses.CheckTryLaterPrefix;
                    suffix = NntpResponses.CheckTryLaterSuffix;
                    break;
                default:
                    prefix = NntpResponses.CheckPrefix;
                    suffix = NntpResponses.CheckSuffix;
                    break;
            }

            return NntpResponseCompose.Concat(prefix.Span, messageId, suffix.Span);
        }

        private static ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
        {
            var messageId = context.ArgumentMemory;
            var lookup = LookupAsync(context.Session, messageId, cancellationToken);
            if (lookup.IsCompletedSuccessfully)
            {
                return WriteResultAsync(context, lookup.Result, messageId, cancellationToken);
            }

            return AwaitLookupAndWriteAsync(context, lookup, messageId, cancellationToken);
        }

        private static async ValueTask AwaitLookupAndWriteAsync(
            NntpCommandContext context,
            ValueTask<HistoryLookupResult> lookup,
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken)
        {
            var result = await lookup.ConfigureAwait(false);
            await WriteResultAsync(context, result, messageId, cancellationToken).ConfigureAwait(false);
        }

        private static ValueTask WriteResultAsync(
            NntpCommandContext context,
            HistoryLookupResult result,
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken)
        {
            var deferOnDuplicate = DeferOnDuplicate(context.Session);
            var owned = Compose(result, messageId.Span, deferOnDuplicate);
            if (Logger.IsEnabled(LogLevel.Debug))
            {
                context.StatusLine ??= NntpCommandStatusText.FormatCheck(result, messageId.Span, deferOnDuplicate);
            }

            return context.Response.WriteLineAsync(owned, cancellationToken);
        }

        /// <summary>
        /// Reads the connection's captured Receive <c>DeferOnDuplicate</c> flag.
        /// A session without a peer policy does not defer.
        /// </summary>
        internal static bool DeferOnDuplicate(NntpSession session) =>
            session.Authorization.TransitPeerPolicy is { DeferOnDuplicate: true };
    }
}

using VectorNNTPD.ReaderService.Newsgroups;
using VectorNNTPD.ReaderService.Session.CommandProcessor;

namespace VectorNNTPD.ReaderService.Session.Commands
{
    /// <summary>
    /// LIST command as defined by RFC 3977, Section 7.6.1 (see also RFC 6048).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implemented keywords: default/ACTIVE, COUNTS, and NEWSGROUPS (catalogue
    /// snapshot), plus static OVERVIEW.FMT and HEADERS field lists. LIST ACTIVE
    /// and LIST COUNTS status is the stored <c>y</c>/<c>n</c>/<c>m</c>/<c>x</c>/<c>j</c>
    /// octet; the RFC 6048 <c>=&lt;newsgroup&gt;</c> form is not supported. LIST
    /// COUNTS estimated counts reuse the GROUP watermark estimate
    /// (RFC 3977 §6.1.1 / RFC 6048 §2.2). The command path does not query MySQL.
    /// </para>
    /// <para>
    /// LIST OVERVIEW.FMT and LIST HEADERS write immortal static 215 multiline
    /// responses (RFC 3977 §8.4 / §8.6). They do not read articles or the
    /// catalogue. LIST HEADERS MSGID and RANGE return the same list because HDR
    /// forms are not distinguished. LIST MOTD remains recognized without stored
    /// information and returns 503. Unsupported keywords (ACTIVE.TIMES,
    /// DISTRIB.PATS, DISTRIBUTIONS, MODERATORS, SUBSCRIPTIONS) are not registered
    /// and resolve as unknown variants.
    /// </para>
    /// </remarks>
    internal static class List
    {
        private static ILogger Logger => NntpCommandLoggers.For(typeof(List));

        /// <summary>Handles <c>LIST</c>.</summary>
        public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(Logger, context, DisplayName(context), ExecuteAsync, cancellationToken);

        private static string DisplayName(NntpCommandContext context) =>
            DefaultNntpCommandCatalog.DisplayName(context.Command.Verb, context.Command.Qualifier);

        private static ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
        {
            return context.Command.Qualifier switch
            {
                NntpVerb.None or NntpVerb.Active => WriteActiveAsync(context, cancellationToken),
                NntpVerb.Counts => WriteCountsAsync(context, cancellationToken),
                NntpVerb.Newsgroups => WriteNewsgroupsAsync(context, cancellationToken),
                NntpVerb.OverviewFmt => WriteOverviewFmtAsync(context, cancellationToken),
                NntpVerb.Headers => WriteHeadersAsync(context, cancellationToken),
                NntpVerb.Motd => WriteDataItemNotStoredAsync(context, cancellationToken),
                _ => NntpCommandNotImplemented.HandleAsync(context, Logger, cancellationToken),
            };
        }

        private static ValueTask WriteOverviewFmtAsync(
            NntpCommandContext context,
            CancellationToken cancellationToken) =>
            NntpCommandReply.WriteAsync(
                context,
                Logger,
                NntpResponses.ListOverviewFmtComplete,
                NntpResponseStatus.ListOverviewFmtFollows,
                cancellationToken);

        private static ValueTask WriteHeadersAsync(
            NntpCommandContext context,
            CancellationToken cancellationToken) =>
            NntpCommandReply.WriteAsync(
                context,
                Logger,
                NntpResponses.ListHeadersComplete,
                NntpResponseStatus.ListHeadersFollows,
                cancellationToken);

        private static ValueTask WriteDataItemNotStoredAsync(
            NntpCommandContext context,
            CancellationToken cancellationToken) =>
            NntpCommandReply.WriteAsync(
                context,
                Logger,
                NntpResponses.ListDataItemNotStored,
                NntpResponseStatus.ListDataItemNotStored,
                cancellationToken);

        private static ValueTask WriteActiveAsync(NntpCommandContext context, CancellationToken cancellationToken)
        {
            var snapshot = Capture(context);
            if (snapshot is null)
            {
                return NntpCommandNotImplemented.HandleAsync(context, Logger, cancellationToken);
            }

            var wildmat = context.ArgumentMemory;
            if (wildmat.IsEmpty)
            {
                return NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    snapshot.ListActiveComplete,
                    NntpResponseStatus.ListOfNewsgroupsFollows,
                    cancellationToken);
            }

            return WriteFilteredAsync(context, snapshot, wildmat, ListLineKind.Active, cancellationToken);
        }

        private static ValueTask WriteCountsAsync(NntpCommandContext context, CancellationToken cancellationToken)
        {
            var snapshot = Capture(context);
            if (snapshot is null)
            {
                return NntpCommandNotImplemented.HandleAsync(context, Logger, cancellationToken);
            }

            var wildmat = context.ArgumentMemory;
            if (wildmat.IsEmpty)
            {
                return NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    snapshot.ListCountsComplete,
                    NntpResponseStatus.ListOfNewsgroupsFollows,
                    cancellationToken);
            }

            return WriteFilteredAsync(context, snapshot, wildmat, ListLineKind.Counts, cancellationToken);
        }

        private static ValueTask WriteNewsgroupsAsync(NntpCommandContext context, CancellationToken cancellationToken)
        {
            var snapshot = Capture(context);
            if (snapshot is null)
            {
                return NntpCommandNotImplemented.HandleAsync(context, Logger, cancellationToken);
            }

            var wildmat = context.ArgumentMemory;
            if (wildmat.IsEmpty)
            {
                return NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    snapshot.ListNewsgroupsComplete,
                    NntpResponseStatus.ListOfNewsgroupsFollows,
                    cancellationToken);
            }

            return WriteFilteredAsync(context, snapshot, wildmat, ListLineKind.Newsgroups, cancellationToken);
        }

        private static async ValueTask WriteFilteredAsync(
            NntpCommandContext context,
            NewsgroupSnapshot snapshot,
            ReadOnlyMemory<byte> pattern,
            ListLineKind kind,
            CancellationToken cancellationToken)
        {
            NntpCommandReply.TryNote(context, Logger, NntpResponseStatus.ListOfNewsgroupsFollows);
            await context.Response
                .WriteLineAsync(NntpResponses.ListOfNewsgroupsFollows, cancellationToken)
                .ConfigureAwait(false);

            var groups = snapshot.Groups;
            for (var i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                if (!NntpWildmat.IsMatchValidated(group.GroupNameBytes.Span, pattern.Span))
                {
                    continue;
                }

                var line = kind switch
                {
                    ListLineKind.Active => group.ListActiveLine,
                    ListLineKind.Counts => group.ListCountsLine,
                    _ => group.ListNewsgroupsLine,
                };
                await context.Response.WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
            }

            await context.Response.WriteMultilineEndAsync(cancellationToken).ConfigureAwait(false);
        }

        private static NewsgroupSnapshot? Capture(NntpCommandContext context) =>
            context.Session.NewsgroupCatalogue?.Current;

        private enum ListLineKind
        {
            Active,
            Counts,
            Newsgroups,
        }
    }
}

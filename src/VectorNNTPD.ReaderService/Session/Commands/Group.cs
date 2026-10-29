using VectorNNTPD.ReaderService.Newsgroups;
using VectorNNTPD.ReaderService.Session.CommandProcessor;

namespace VectorNNTPD.ReaderService.Session.Commands
{
    /// <summary>
    /// GROUP command as defined by RFC 3977, Section 6.1.1.
    /// </summary>
    /// <remarks>
    /// Group existence and water marks come from one captured
    /// <see cref="NewsgroupSnapshot"/>. The command path does not query MySQL.
    /// Article-number sequences are not invented from the water marks.
    /// </remarks>
    internal static class Group
    {
        private static ILogger Logger => NntpCommandLoggers.For(typeof(Group));

        /// <summary>Handles <c>GROUP</c>.</summary>
        public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(Logger, context, "GROUP", ExecuteAsync, cancellationToken);

        private static ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
        {
            var snapshot = context.Session.NewsgroupCatalogue?.Current;
            if (snapshot is null)
            {
                return NntpCommandNotImplemented.HandleAsync(context, Logger, cancellationToken);
            }

            if (!snapshot.TryGet(context.ArgumentSpan, out var group))
            {
                return NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.NoSuchNewsgroup,
                    NntpResponseStatus.NoSuchNewsgroup,
                    cancellationToken);
            }

            context.Session.SelectGroup(group.GroupNameBytes);
            return NntpCommandReply.WriteAsync(
                context,
                Logger,
                group.GroupSelectedLine,
                NntpResponseStatus.GroupSelected,
                cancellationToken);
        }
    }
}

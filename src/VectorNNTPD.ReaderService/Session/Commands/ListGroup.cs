using VectorNNTPD.ReaderService.Session.CommandProcessor;

namespace VectorNNTPD.ReaderService.Session.Commands
{
    /// <summary>
    /// LISTGROUP command as defined by RFC 3977, Section 6.1.2.
    /// </summary>
    /// <remarks>
    /// Lists article numbers in a newsgroup. Deliberate placeholder until an article-number
    /// source exists. Do not invent article numbers from catalogue water marks.
    /// </remarks>
    internal static class ListGroup
    {
        private static ILogger Logger => NntpCommandLoggers.For(typeof(ListGroup));

        /// <summary>Handles <c>LISTGROUP</c>.</summary>
        public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(
                Logger,
                context,
                "LISTGROUP",
                static (ctx, ct) => NntpCommandNotImplemented.HandleAsync(ctx, Logger, ct),
                cancellationToken,
                successDetail: "not implemented");
    }
}

using VectorNNTPD.TransitService.Session.CommandProcessor;

namespace VectorNNTPD.TransitService.Session.Commands
{
    /// <summary>
    /// NEXT command as defined by RFC 3977, Section 6.1.4.
    /// </summary>
    /// <remarks>
    /// Moves the current article pointer forward. Deliberate placeholder until article selection state is implemented.
    /// </remarks>
    internal static class Next
    {
        private static ILogger Logger => NntpCommandLoggers.For(typeof(Next));

        /// <summary>Handles <c>NEXT</c>.</summary>
        public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(
                Logger,
                context,
                "NEXT",
                static (ctx, ct) => NntpCommandNotImplemented.HandleAsync(ctx, Logger, ct),
                cancellationToken,
                successDetail: "not implemented");
    }
}

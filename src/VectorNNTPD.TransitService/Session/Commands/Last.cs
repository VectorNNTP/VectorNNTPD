using VectorNNTPD.TransitService.Session.CommandProcessor;

namespace VectorNNTPD.TransitService.Session.Commands
{
    /// <summary>
    /// LAST command as defined by RFC 3977, Section 6.1.3.
    /// </summary>
    /// <remarks>
    /// Moves the current article pointer backward. Deliberate placeholder until article selection state is implemented.
    /// </remarks>
    internal static class Last
    {
        private static ILogger Logger => NntpCommandLoggers.For(typeof(Last));

        /// <summary>Handles <c>LAST</c>.</summary>
        public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(
                Logger,
                context,
                "LAST",
                static (ctx, ct) => NntpCommandNotImplemented.HandleAsync(ctx, Logger, ct),
                cancellationToken,
                successDetail: "not implemented");
    }
}

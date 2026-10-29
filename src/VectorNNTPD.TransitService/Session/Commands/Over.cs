using VectorNNTPD.TransitService.Session.CommandProcessor;

namespace VectorNNTPD.TransitService.Session.Commands
{
    /// <summary>
    /// OVER command as defined by RFC 3977, Section 8.3.
    /// </summary>
    /// <remarks>
    /// Returns overview information for articles. Deliberate placeholder until overview database is implemented.
    /// </remarks>
    internal static class Over
    {
        private static ILogger Logger => NntpCommandLoggers.For(typeof(Over));

        /// <summary>Handles <c>OVER</c>.</summary>
        public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(
                Logger,
                context,
                "OVER",
                static (ctx, ct) => NntpCommandNotImplemented.HandleAsync(ctx, Logger, ct),
                cancellationToken,
                successDetail: "not implemented");
    }
}

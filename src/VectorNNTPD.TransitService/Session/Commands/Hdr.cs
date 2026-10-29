using VectorNNTPD.TransitService.Session.CommandProcessor;

namespace VectorNNTPD.TransitService.Session.Commands
{
    /// <summary>
    /// HDR command as defined by RFC 3977, Section 8.5.
    /// </summary>
    /// <remarks>
    /// Returns a specific header field for articles. Deliberate placeholder until header/overview access is implemented.
    /// </remarks>
    internal static class Hdr
    {
        private static ILogger Logger => NntpCommandLoggers.For(typeof(Hdr));

        /// <summary>Handles <c>HDR</c>.</summary>
        public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(
                Logger,
                context,
                "HDR",
                static (ctx, ct) => NntpCommandNotImplemented.HandleAsync(ctx, Logger, ct),
                cancellationToken,
                successDetail: "not implemented");
    }
}

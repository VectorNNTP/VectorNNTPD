namespace VectorNNTPD.TransitService.Session.CommandProcessor
{
    /// <summary>Shared placeholder response for commands not yet implemented.</summary>
    internal static class NntpCommandNotImplemented
    {
        /// <summary>Returns <c>500 Command not implemented</c> (RFC 3977 for unsupported optional commands).</summary>
        public static ValueTask HandleAsync(
            NntpCommandContext context,
            ILogger logger,
            CancellationToken cancellationToken) =>
            NntpCommandReply.WriteAsync(
                context,
                logger,
                NntpResponses.CommandNotImplemented,
                NntpResponseStatus.CommandNotImplemented,
                cancellationToken);
    }
}

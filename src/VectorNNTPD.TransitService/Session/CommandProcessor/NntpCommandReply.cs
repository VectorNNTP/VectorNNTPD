namespace VectorNNTPD.TransitService.Session.CommandProcessor
{
    /// <summary>
    /// Writes existing wire bytes and, only when Debug command logging is enabled, records
    /// the independently authored semantic status line on the command context.
    /// </summary>
    internal static class NntpCommandReply
    {
        /// <summary>
        /// Records <paramref name="statusLine"/> when Debug is enabled and no status is set yet.
        /// Immortal strings are stored by reference; nothing is allocated when Debug is off.
        /// </summary>
        internal static void TryNote(NntpCommandContext context, ILogger logger, string statusLine)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentException.ThrowIfNullOrWhiteSpace(statusLine);
            if (context.StatusLine is not null || !logger.IsEnabled(LogLevel.Debug))
            {
                return;
            }

            context.StatusLine = statusLine;
        }

        /// <summary>Writes <paramref name="wire"/> and notes <paramref name="statusLine"/> if Debug is on.</summary>
        internal static ValueTask WriteAsync(
            NntpCommandContext context,
            ILogger logger,
            ReadOnlyMemory<byte> wire,
            string statusLine,
            CancellationToken cancellationToken = default)
        {
            TryNote(context, logger, statusLine);
            return context.Response.WriteLineAsync(wire, cancellationToken);
        }

        /// <summary>
        /// Enqueues <paramref name="wire"/> immediately and notes <paramref name="statusLine"/> if Debug is on.
        /// </summary>
        internal static ValueTask EnqueueImmediateAsync(
            NntpCommandContext context,
            ILogger logger,
            ReadOnlyMemory<byte> wire,
            string statusLine,
            CancellationToken cancellationToken = default)
        {
            TryNote(context, logger, statusLine);
            return context.Response.EnqueueLineImmediateAsync(wire, cancellationToken);
        }
    }
}

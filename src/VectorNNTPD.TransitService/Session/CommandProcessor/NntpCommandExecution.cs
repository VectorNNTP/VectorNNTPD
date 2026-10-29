using System.Diagnostics;

namespace VectorNNTPD.TransitService.Session.CommandProcessor
{
    /// <summary>
    /// Times command-handler execution and emits exactly one TX completion record owned by the command module.
    /// </summary>
    /// <remarks>
    /// Timing covers handler work only (not socket receive wait). Completion is always written from
    /// <c>finally</c> so cancellation and failures still produce one TX line.
    /// </remarks>
    internal static class NntpCommandExecution
    {
        /// <summary>
        /// Runs <paramref name="execute"/> under a monotonic stopwatch and logs
        /// <c>[client] TX: COMMAND [status] executed in 0.000s</c> exactly once when
        /// the handler produced an NNTP status line; otherwise the historical no-status
        /// completion format is retained.
        /// </summary>
        public static async ValueTask RunAsync(
            ILogger logger,
            NntpCommandContext context,
            string command,
            Func<NntpCommandContext, CancellationToken, ValueTask> execute,
            CancellationToken cancellationToken,
            string? successDetail = null)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentException.ThrowIfNullOrWhiteSpace(command);
            ArgumentNullException.ThrowIfNull(execute);

            var started = Stopwatch.GetTimestamp();
            string? detail = successDetail;
            var completedBefore = context.Connection.IsCompleted;
            try
            {
                await execute(context, cancellationToken).ConfigureAwait(false);
                if (context.CompletionDetail is not null)
                {
                    // Handler-owned detail (e.g. QUIT peer disconnect, STARTTLS cipher, COMPRESS).
                    detail = context.CompletionDetail;
                }
                else if (!completedBefore && context.Connection.IsCompleted)
                {
                    detail = "failed";
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                detail = "cancelled";
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                detail = "failed";
                CommandLogMessages.CommandFailed(
                    logger,
                    ex,
                    NntpCommandLogFormat.Client(context.Session),
                    command);
                throw;
            }
            finally
            {
                WriteCompletion(
                    logger,
                    context.Session,
                    command,
                    Stopwatch.GetElapsedTime(started),
                    detail,
                    context.StatusLine);
            }
        }

        /// <summary>
        /// Emits a TX completion record for gate/reject paths that never enter a command handler body.
        /// </summary>
        /// <remarks>
        /// Uses the command module's logger category (not <see cref="NntpCommandDispatcher"/>).
        /// </remarks>
        public static void WriteCompletion(
            ILogger logger,
            NntpSession session,
            string command,
            TimeSpan elapsed,
            string? detail = null,
            string? statusLine = null)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(session);
            ArgumentException.ThrowIfNullOrWhiteSpace(command);

            if (!logger.IsEnabled(LogLevel.Debug)
                || NntpCommandLogFormat.SuppressHotPathCommandLog(command))
            {
                return;
            }

            var client = NntpCommandLogFormat.Client(session);
            var seconds = elapsed.TotalSeconds;
            if (statusLine is not null)
            {
                if (detail is null)
                {
                    CommandLogMessages.CommandTxWithStatus(logger, client, command, statusLine, seconds);
                }
                else
                {
                    CommandLogMessages.CommandTxWithStatusAndDetail(
                        logger,
                        client,
                        command,
                        statusLine,
                        seconds,
                        detail);
                }

                return;
            }

            if (detail is null)
            {
                CommandLogMessages.CommandTx(logger, client, command, seconds);
            }
            else
            {
                CommandLogMessages.CommandTxWithDetail(logger, client, command, seconds, detail);
            }
        }
    }
}

using System.Net.Sockets;
using VectorNNTPD.TransitService.Session.CommandProcessor;

namespace VectorNNTPD.TransitService.Session.Commands
{
    /// <summary>
    /// QUIT command as defined by RFC 3977, Section 5.4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Syntax: <c>QUIT</c> (no arguments). Returns <c>205 Connection closing</c> then ends the session.
    /// Trailing arguments are a syntax error (<c>501</c>) and do not close the connection.
    /// </para>
    /// <para>
    /// Real clients often close the TCP socket immediately after sending QUIT, before observing 205.
    /// After a valid QUIT is accepted, peer disappearance during response write/delivery or close is
    /// treated as a normal termination race — not an unexpected application failure. Unrelated
    /// transport failures are still logged and rethrown.
    /// </para>
    /// </remarks>
    internal static class Quit
    {
        private static ILogger Logger => NntpCommandLoggers.For(typeof(Quit));

        /// <summary>Handles <c>QUIT</c> (RFC 3977 §5.4).</summary>
        public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(Logger, context, "QUIT", ExecuteAsync, cancellationToken);

        private static async ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
        {
            // Valid QUIT accepted: after this point the session must become terminal and no further
            // NNTP commands/responses should be processed (aside from finishing this 205 write).
            context.ExpectConnectionComplete = true;
            var responseWritten = false;
            var idleVersionBefore205 = context.Connection.OutboundIdleVersion;
            try
            {
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.ConnectionClosing,
                        NntpResponseStatus.ConnectionClosing,
                        cancellationToken)
                    .ConfigureAwait(false);
                responseWritten = true;

                // Prefer observing send-pump idle so 205 has left the outbound pipe before teardown,
                // matching STARTTLS/COMPRESS delivery waits. Peer-gone during this wait is expected.
                if (context.Connection is { IsCompleted: false, ConnectionClosed.IsCancellationRequested: false })
                {
                    await context.Connection
                        .WaitForOutboundDeliveryAsync(idleVersionBefore205, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (context.Connection.ConnectionClosed.IsCancellationRequested)
            {
                // Connection became terminal (often peer FIN/RST) while writing or waiting for 205.
                context.CompletionDetail = "peer disconnected";
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (NntpPeerDisconnect.IsPeerDisconnect(ex, context.Connection))
            {
                context.CompletionDetail = "peer disconnected";
                CommandLogMessages.QuitPeerDisconnected(
                    Logger,
                    ex,
                    NntpCommandLogFormat.Client(context.Session));
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                // TEMP DIAG — remove after capture
                CommandLogMessages.QuitUncaught(
                    Logger,
                    ex,
                    NntpCommandLogFormat.Client(context.Session),
                    ex.GetType().FullName,
                    (ex as SocketException)?.SocketErrorCode
                    ?? (ex.InnerException as SocketException)?.SocketErrorCode);
                throw;
            }

            // Always terminate after a valid QUIT — even when the peer vanished mid-205.
            context.Session.RequestClose();

            // Do not claim a clean delivery when we never successfully wrote the status line.
            if (!responseWritten && context.CompletionDetail is null)
            {
                context.CompletionDetail = "peer disconnected";
            }

            // StatusLine is normally noted only when Debug command logging is enabled; ensure TX
            // completion can still show 205 after a successful write.
            if (responseWritten)
            {
                context.StatusLine ??= NntpResponseStatus.ConnectionClosing;
            }
        }
    }
}

using System.IO.Pipelines;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Transport;
using VectorNNTPD.ReaderService.Session.CommandProcessor;

namespace VectorNNTPD.ReaderService.Session.Commands
{
    /// <summary>
    /// STARTTLS command as defined by RFC 4642, Section 2.2 (updated by RFC 8143).
    /// </summary>
    /// <remarks>
    /// Negotiates TLS on an existing plaintext NNTP connection. Transport owns the handshake and
    /// quiescence; this module owns command sequencing (discard pipelined input, pause reads before
    /// <c>382</c>, wait for outbound delivery, upgrade) and the TX completion record.
    /// </remarks>
    internal static class StartTls
    {
        private static ILogger Logger => NntpCommandLoggers.For(typeof(StartTls));

        /// <summary>Handles <c>STARTTLS</c>.</summary>
        public static ValueTask HandleAsync(
            NntpCommandContext context,
            ITlsCertificateContextProvider? certificateProvider,
            CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(
                Logger,
                context,
                "STARTTLS",
                (ctx, ct) => ExecuteAsync(ctx, certificateProvider, ct),
                cancellationToken);

        private static async ValueTask ExecuteAsync(
            NntpCommandContext context,
            ITlsCertificateContextProvider? certificateProvider,
            CancellationToken cancellationToken)
        {
            if (context.Connection.IsTls)
            {
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.TlsAlreadyActive,
                        NntpResponseStatus.TlsAlreadyActive,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            // RFC 8054 §2.2.2: MUST reply 502 to STARTTLS while a compression layer is already active.
            if (context.Connection.IsCompressed)
            {
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.DeflateAlreadyActive,
                        NntpResponseStatus.DeflateAlreadyActive,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (certificateProvider is null)
            {
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.TlsProviderUnavailable,
                        NntpResponseStatus.TlsProviderUnavailable,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            // RFC 8143 §4: discard any NNTP command pipelined between STARTTLS and TLS negotiation.
            DiscardBufferedApplicationInput(context.Connection.Input);

            // Claim read ownership BEFORE 382 can reach the peer. Writes remain allowed while reads
            // are paused, so ClientHello completions stash for PrefixedStream instead of Input.
            await context.Connection.PauseReadsAsync(cancellationToken).ConfigureAwait(false);

            var idleVersionBefore382 = context.Connection.OutboundIdleVersion;
            await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.ContinueWithTls,
                        NntpResponseStatus.ContinueWithTls,
                        cancellationToken)
                .ConfigureAwait(false);

            await context.Connection
                .WaitForOutboundDeliveryAsync(idleVersionBefore382, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await context.Connection.UpgradeToTlsAsync(certificateProvider, cancellationToken).ConfigureAwait(false);
                if (context.Connection.TryGetNegotiatedTlsParameters(out var tlsVersion, out var cipher))
                {
                    context.CompletionDetail = TlsNegotiationLogging.FormatDetail(tlsVersion, cipher);
                }
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                CommandLogMessages.StartTlsHandshakeFailed(
                    Logger,
                    ex,
                    NntpCommandLogFormat.Client(context.Session));
                context.Session.RequestClose();
                // Handshake failures complete the connection; do not rethrow into the dispatcher error path.
                // Precondition failures leave the connection usable — rethrow so the dispatcher can respond.
                if (context.Connection.IsCompleted)
                {
                    return;
                }

                throw;
            }
        }

        /// <summary>
        /// Consumes any octets already buffered in application <see cref="PipeReader"/> without waiting for more.
        /// </summary>
        private static void DiscardBufferedApplicationInput(PipeReader input)
        {
            while (input.TryRead(out var result))
            {
                var isCompleted = result.IsCompleted;
                var isEmpty = result.Buffer.IsEmpty;
                input.AdvanceTo(result.Buffer.End);
                if (isEmpty || isCompleted)
                {
                    break;
                }
            }
        }
    }
}

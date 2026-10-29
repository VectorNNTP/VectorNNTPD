using System.IO.Pipelines;
using VectorNNTPD.ReaderService.Session.CommandProcessor;

namespace VectorNNTPD.ReaderService.Session.Commands
{
    /// <summary>
    /// COMPRESS command as defined by RFC 8054, Section 2.2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Activates the NNTP compression layer using the mandatory DEFLATE algorithm (RFC 8054 §2.2 / §4).
    /// The command MUST NOT be pipelined (RFC 8054 §2.2.1). Success reply <c>206</c> is the final
    /// uncompressed NNTP response; compression takes effect immediately after its CRLF.
    /// </para>
    /// <para>
    /// Transport owns raw DEFLATE wrapping and quiescence via <c>UpgradeToDeflateAsync</c>. This module
    /// owns algorithm validation, capability-related session sequencing (discard pipelined input, pause
    /// reads before <c>206</c>, wait for outbound delivery, upgrade), and the TX completion record.
    /// Layering follows RFC 8054 §2.2.2: application → DEFLATE → (TLS) → network.
    /// </para>
    /// </remarks>
    internal static class Compress
    {
        private static ILogger Logger => NntpCommandLoggers.For(typeof(Compress));

        /// <summary>Handles <c>COMPRESS</c> (algorithm argument validated per RFC 8054).</summary>
        public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(
                Logger,
                context,
                "COMPRESS",
                ExecuteAsync,
                cancellationToken);

        private static async ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
        {
            if (context.Connection.IsCompressed)
            {
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.CompressionAlreadyActive,
                        NntpResponseStatus.CompressionAlreadyActive,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            // RFC 8054 §5.3: algorithm names are case-sensitive. Parser already rejected
            // lowercase/illegal octets (501). This is the semantic DEFLATE check — no string.
            if (!context.ArgumentSpan.SequenceEqual("DEFLATE"u8))
            {
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.CompressionAlgorithmNotSupported,
                        NntpResponseStatus.CompressionAlgorithmNotSupported,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            // Client MUST NOT pipeline (RFC 8054 §2.2.1). Discard any already-buffered application
            // octets so they cannot be misinterpreted after the compression layer activates.
            DiscardBufferedApplicationInput(context.Connection.Input);

            // Claim read ownership BEFORE 206 can reach the peer so the first compressed octets
            // cannot enter application Input as plaintext.
            try
            {
                await context.Connection.PauseReadsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                CommandLogMessages.CompressRefusedBeforeActivation(
                    Logger,
                    ex,
                    NntpCommandLogFormat.Client(context.Session));
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.UnableToActivateCompression,
                        NntpResponseStatus.UnableToActivateCompression,
                        cancellationToken)
                    .ConfigureAwait(false);
                context.CompletionDetail = "failed";
                return;
            }

            var idleVersionBefore206 = context.Connection.OutboundIdleVersion;
            await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.CompressionActive,
                        NntpResponseStatus.CompressionActive,
                        cancellationToken)
                .ConfigureAwait(false);

            await context.Connection
                .WaitForOutboundDeliveryAsync(idleVersionBefore206, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await context.Connection.UpgradeToDeflateAsync(cancellationToken).ConfigureAwait(false);
                context.CompletionDetail = "DEFLATE active";
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                // 206 already committed the transition. Do not write another NNTP status — the peer
                // expects compressed traffic. Terminate so we never leave a half-compressed session.
                CommandLogMessages.CompressActivationFailed(
                    Logger,
                    ex,
                    NntpCommandLogFormat.Client(context.Session));
                if (!context.Connection.IsCompleted)
                {
                    await context.Connection.CompleteAsync(ex).ConfigureAwait(false);
                }

                context.Session.RequestClose();
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

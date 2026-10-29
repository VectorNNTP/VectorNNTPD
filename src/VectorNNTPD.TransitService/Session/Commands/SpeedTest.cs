using System.Diagnostics;
using System.Globalization;
using System.Text;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Session.SpeedTest;

namespace VectorNNTPD.TransitService.Session.Commands
{
    /// <summary>
    /// VectorNNTP private extension: <c>SPEEDTEST identifier</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Syntax: <c>SPEEDTEST identifier</c>. <c>identifier</c> is one NNTP token and must be an
    /// existing Transit dictionary key (exact ordinal match). It is not <c>PeerName</c>, a
    /// hostname, IP, port, URL, or socket endpoint. Multi-token display names are not accepted.
    /// </para>
    /// <para>
    /// Access: transit-authorized sessions only (dispatcher <c>480</c>/<c>502</c>). When the
    /// session was identified as a named Transit peer, the argument must match that name.
    /// </para>
    /// <para>
    /// V1 measures TX on the current NNTP session only (this host writes a synthetic multiline
    /// payload to the connected client). Outbound Transit initiation is not implemented; the
    /// command never connects to <c>ConnectTo</c> and never reports a measurement that was not
    /// performed. RX, RTT, and packet loss are <c>NOT-MEASURED</c>.
    /// </para>
    /// <para>
    /// Responses use private-extension <c>x9x</c> codes (RFC 3977 §3.2): <c>290</c> starts the
    /// TX payload multiline; <c>291</c> is followed by machine-readable result fields.
    /// Unknown peers use the project <c>502</c> convention. The command is serial (not pipelined)
    /// and does not use article ingestion, HistoryDB, or Redis.
    /// </para>
    /// </remarks>
    internal static class SpeedTest
    {
        private static ILogger Logger => NntpCommandLoggers.For(typeof(SpeedTest));

        /// <summary>Handles <c>SPEEDTEST</c>.</summary>
        public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
            NntpCommandExecution.RunAsync(Logger, context, "SPEEDTEST", ExecuteAsync, cancellationToken);

        private static async ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
        {
            var coordinator = context.Session.SpeedTest;
            if (coordinator is null)
            {
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.SpeedTestNotSupported,
                        NntpResponseStatus.SpeedTestNotSupported,
                        cancellationToken)
                    .ConfigureAwait(false);
                context.CompletionDetail = "not supported";
                return;
            }

            var peerToken = context.ArgumentMemory;
            if (!coordinator.TryResolvePeer(peerToken.Span, out var peer) || peer is null)
            {
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.SpeedTestUnknownPeer,
                        NntpResponseStatus.SpeedTestUnknownPeer,
                        cancellationToken)
                    .ConfigureAwait(false);
                CommandLogMessages.SpeedTestRejected(
                    Logger,
                    NntpCommandLogFormat.Client(context.Session),
                    FormatPeerForLog(peerToken.Span),
                    string.Empty,
                    "unknown peer");
                context.CompletionDetail = "unknown peer";
                return;
            }

            var sessionPeer = context.Session.Authorization.TransitPeerName;
            if (sessionPeer is not null
                && !SpeedTestCoordinator.NameEquals(sessionPeer, peerToken.Span))
            {
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.SpeedTestPeerMismatch,
                        NntpResponseStatus.SpeedTestPeerMismatch,
                        cancellationToken)
                    .ConfigureAwait(false);
                CommandLogMessages.SpeedTestRejected(
                    Logger,
                    NntpCommandLogFormat.Client(context.Session),
                    peer.Identifier,
                    peer.PeerName,
                    "peer mismatch");
                context.CompletionDetail = "peer mismatch";
                return;
            }

            if (!coordinator.TryAcquire(peer.Identifier, out var lease) || lease is null)
            {
                await NntpCommandReply.WriteAsync(
                        context,
                        Logger,
                        NntpResponses.SpeedTestBusy,
                        NntpResponseStatus.SpeedTestBusy,
                        cancellationToken)
                    .ConfigureAwait(false);
                CommandLogMessages.SpeedTestRejected(
                    Logger,
                    NntpCommandLogFormat.Client(context.Session),
                    peer.Identifier,
                    peer.PeerName,
                    "busy");
                context.CompletionDetail = "busy";
                return;
            }

            using (lease)
            using (var workCts = CancellationTokenSource.CreateLinkedTokenSource(
                       cancellationToken,
                       context.Session.Connection.ConnectionClosed))
            {
                CommandLogMessages.SpeedTestStarted(
                    Logger,
                    NntpCommandLogFormat.Client(context.Session),
                    peer.Identifier,
                    peer.PeerName);

                var ready = NntpResponseCompose.Concat(
                    NntpResponses.SpeedTestReadyPrefix.Span,
                    peerToken.Span,
                    NntpResponses.SpeedTestReadySuffix.Span);
                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    context.StatusLine ??=
                        "290 SPEEDTEST " + System.Text.Encoding.ASCII.GetString(peerToken.Span) + " TX";
                }

                await context.Response.WriteLineAsync(ready, workCts.Token).ConfigureAwait(false);

                if (!ReferenceEquals(context.Response.UnderlyingOutput, context.Session.Connection.Output))
                {
                    throw new InvalidOperationException(
                        "SPEEDTEST direct path must use the existing session TX PipeWriter.");
                }

                long bytes;
                TimeSpan elapsed;
                long directFlushes;
                try
                {
                    (bytes, elapsed, directFlushes) = await WritePayloadAsync(
                            context.Response,
                            lease.Limits,
                            workCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested
                    || context.Session.Connection.ConnectionClosed.IsCancellationRequested)
                {
                    CommandLogMessages.SpeedTestCancelled(
                        Logger,
                        NntpCommandLogFormat.Client(context.Session),
                        peer.Identifier,
                        peer.PeerName,
                        "cancelled");
                    context.CompletionDetail = "cancelled";
                    return;
                }

                var result = FormatResult(peer.Identifier, peer.PeerName, bytes, elapsed, directFlushes);
                await context.Response.WriteLineAsync(result, workCts.Token).ConfigureAwait(false);

                CommandLogMessages.SpeedTestCompleted(
                    Logger,
                    NntpCommandLogFormat.Client(context.Session),
                    peer.Identifier,
                    peer.PeerName,
                    bytes,
                    elapsed.TotalMilliseconds);
                context.CompletionDetail = "tx complete";
            }
        }

        internal static async ValueTask<(long Bytes, TimeSpan Elapsed, long DirectFlushes)> WritePayloadAsync(
            NntpResponseWriter response,
            Configuration.SpeedTestLimits limits,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(response);
            await using var pipeLease = await response.AcquireDirectTxPipeAsync(cancellationToken).ConfigureAwait(false);
            var started = Stopwatch.GetTimestamp();
            long bytes = 0;
            while (bytes < limits.MaxBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Stopwatch.GetElapsedTime(started) >= limits.MaxDuration)
                {
                    break;
                }

                var slice = SpeedTestPayload.Take(limits.MaxBytes - bytes);
                if (slice.IsEmpty)
                {
                    break;
                }

                await pipeLease.WriteAndFlushAsync(slice, cancellationToken).ConfigureAwait(false);
                bytes += slice.Length;
            }

            await pipeLease.WriteAndFlushAsync(NntpResponses.MultilineTerminator, cancellationToken).ConfigureAwait(false);
            return (bytes, Stopwatch.GetElapsedTime(started), pipeLease.FlushCount);
        }

        /// <summary>Builds the <c>291</c> multiline result (status + fields + terminator).</summary>
        internal static byte[] FormatResult(
            string identifier,
            string peerName,
            long bytes,
            TimeSpan elapsed,
            long directPipeFlushes = 0)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
            ArgumentException.ThrowIfNullOrWhiteSpace(peerName);
            var durationMs = Math.Max(0, elapsed.TotalMilliseconds);
            var seconds = elapsed.TotalSeconds;
            var measured = bytes > 0 && seconds > 0;
            var mbps = measured
                ? (bytes * 8d / seconds / 1_000_000d).ToString("F3", CultureInfo.InvariantCulture)
                : "NOT-MEASURED";
            var gbit = measured
                ? (bytes * 8d / seconds / 1_000_000_000d).ToString("F6", CultureInfo.InvariantCulture)
                : "NOT-MEASURED";

            var text =
                "291 SPEEDTEST COMPLETE\r\n" +
                "PEER=" + identifier + "\r\n" +
                "PEERNAME=" + peerName + "\r\n" +
                "IMPLEMENTATION=VectorNNTP\r\n" +
                "DIRECTION=TX\r\n" +
                "BYTES=" + bytes.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "DURATION_MS=" + durationMs.ToString("F3", CultureInfo.InvariantCulture) + "\r\n" +
                "THROUGHPUT_MBPS=" + mbps + "\r\n" +
                "THROUGHPUT_GBIT=" + gbit + "\r\n" +
                "RTT_US=NOT-MEASURED\r\n" +
                "RX=NOT-MEASURED\r\n" +
                "OUTBOUND=NOT-AVAILABLE\r\n" +
                "PATH=DIRECT-PIPE\r\n" +
                "DIRECT_PIPE_FLUSHES=" + directPipeFlushes.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "COMPLETE\r\n" +
                ".\r\n";
            return Encoding.UTF8.GetBytes(text);
        }

        private static string FormatPeerForLog(ReadOnlySpan<byte> peerToken)
        {
            var unsafeControl = false;
            for (var i = 0; i < peerToken.Length; i++)
            {
                if (peerToken[i] < 0x20 || peerToken[i] > 0x7E)
                {
                    unsafeControl = true;
                    break;
                }
            }

            return unsafeControl ? "<non-ascii-peer>" : Encoding.ASCII.GetString(peerToken);
        }
    }
}

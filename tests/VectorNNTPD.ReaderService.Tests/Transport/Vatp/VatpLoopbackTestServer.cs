using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Transport.ArticleTransfer;

namespace VectorNNTPD.ReaderService.Tests.Transport.Vatp;

internal sealed class VatpLoopbackTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly ConcurrentDictionary<ArticleId, RegisteredArticle> _articles = new();
    private readonly CancellationTokenSource _cts = new();
    private int _activeSessions;
    private Task? _acceptLoop;

    private VatpLoopbackTestServer(TcpListener listener, X509Certificate2 certificate, int port)
    {
        _listener = listener;
        _certificate = certificate;
        Port = port;
        Host = "backfiller.test";
    }

    public int Port { get; }

    public string Host { get; }

    public int ActiveSessions => Volatile.Read(ref _activeSessions);

    /// <summary>DATA prefix used by accepted-byte probe transfer modes.</summary>
    internal const int ProbePayloadBytes = 64;

    internal TransferMode Mode { get; set; } = TransferMode.Complete;

    /// <summary>OPEN StreamIds observed on this listener (client-assigned).</summary>
    public ConcurrentBag<uint> ObservedOpenStreamIds { get; } = new();

    /// <summary>
    /// When set, after a stream terminalizes (END or CANCEL) the server emits late/spurious
    /// frames for the finished StreamId and for a never-allocated StreamId.
    /// </summary>
    public bool EmitSpuriousFramesAfterTerminal { get; set; }

    public static VatpLoopbackTestServer Start(X509Certificate2? certificate = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        certificate ??= VatpTestCertificates.CreateServerCertificate();
        var server = new VatpLoopbackTestServer(listener, certificate, port);
        server._acceptLoop = server.AcceptLoopAsync(server._cts.Token);
        return server;
    }

    public void Register(
        ArticleRecord record,
        NntpArticleHeaderName selectedDateHeaderName,
        TransferMode? modeOverride = null,
        TaskCompletionSource? heldStarted = null)
    {
        _articles[record.ArtId] = new RegisteredArticle(record, selectedDateHeaderName, modeOverride, heldStarted);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
            }
        }

        _cts.Dispose();
        _certificate.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _ = ex.GetType(); // observed: accept loop canceled during server stop
                break;
            }

            _ = RunSessionAsync(tcp, cancellationToken);
        }
    }

    private async Task RunSessionAsync(TcpClient tcp, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeSessions);
        try
        {
            using var tcpDisposable = tcp;
            // codeql[cs/local-not-disposed] SslStream ownership transfers to the VATP/TLS session fixture for connection lifetime.
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                },
                cancellationToken).ConfigureAwait(false);
            var session = new Session(
                ssl,
                _articles,
                () => Mode,
                ObservedOpenStreamIds,
                () => EmitSpuriousFramesAfterTerminal,
                cancellationToken);
            await session.RunAsync().ConfigureAwait(false);
            await ssl.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
        }
        catch (IOException ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
        }
        finally
        {
            Interlocked.Decrement(ref _activeSessions);
        }
    }

    internal enum TransferMode
    {
        Complete,
        FinWithoutEnd,
        DisconnectMidData,
        DisconnectMidMeta,
        DisconnectAfterFinBeforeEnd,

        /// <summary>
        /// Writes META + all DATA frames + END as one contiguous socket write so the
        /// client must parse multiple VATP frames from coalesced TCP/TLS reads.
        /// </summary>
        CoalesceOutboundFrames,

        /// <summary>
        /// Writes each outbound transfer byte in 1-byte socket writes to force
        /// partial-frame reconstruction across many reads.
        /// </summary>
        TinyOutboundWrites,

        /// <summary>
        /// After META, sends a DATA frame whose declared payload exceeds the negotiated
        /// maxFramePayload (must be rejected by the client).
        /// </summary>
        OversizedDataFrame,

        /// <summary>
        /// Sends META + one DATA chunk, then holds the stream open until CANCEL/disconnect
        /// so concurrent-cancellation tests can cancel mid-transfer deterministically.
        /// </summary>
        HoldAfterFirstData,

        /// <summary>
        /// After accepting OPEN, writes a META frame on StreamId 0 (parser must reject).
        /// </summary>
        MetaOnConnectionStreamId,

        /// <summary>Sends a META payload the client must reject before any DATA.</summary>
        InvalidMeta,

        /// <summary>Sends a short DATA frame with FIN so the client copies it and then fails.</summary>
        ShortDataFin,

        /// <summary>Sends one DATA chunk, then a later DATA frame the client rejects before copying.</summary>
        DataThenSizeReject,

        /// <summary>Sends one DATA chunk, then a stream FAIL.</summary>
        DataThenRemoteFail,

        /// <summary>Sends a full body whose META ArtHash does not match, then END.</summary>
        CorruptArtHash,
    }

    private readonly record struct RegisteredArticle(
        ArticleRecord Record,
        NntpArticleHeaderName SelectedDateHeaderName,
        TransferMode? ModeOverride,
        TaskCompletionSource? HeldStarted);

    private sealed class Session(
        SslStream stream,
        ConcurrentDictionary<ArticleId, RegisteredArticle> articles,
        Func<TransferMode> mode,
        ConcurrentBag<uint> observedOpenStreamIds,
        Func<bool> emitSpuriousAfterTerminal,
        CancellationToken cancellationToken)
    {
        private readonly Dictionary<uint, SendStream> _streams = new();
        private readonly ArticleTransferLimits _limits = ArticleTransferLimits.Default;
        private uint _maxFramePayload = VatpProtocol.DefaultMaxFramePayload;
        private bool _clientHelloComplete;

        // Never allocated by monotonic NNTPD client ids starting at 1.
        private const uint NeverAllocatedStreamId = 0x7FFFFFFEu;

        public async Task RunAsync()
        {
            var buffer = ArrayPool<byte>.Shared.Rent(VatpProtocol.HeaderLengthBytes + (int)VatpProtocol.DefaultMaxFramePayload);
            var buffered = 0;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(buffered, buffer.Length - buffered), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }

                    buffered += read;
                    var consumed = 0;
                    while (consumed < buffered)
                    {
                        var sequence = new ReadOnlySequence<byte>(buffer, consumed, buffered - consumed);
                        var parsed = VatpFrameParser.ParseOneFrame(in sequence, _maxFramePayload);
                        if (parsed.Status == VatpFrameParseStatus.Incomplete)
                        {
                            break;
                        }

                        if (parsed.Status == VatpFrameParseStatus.Invalid)
                        {
                            return;
                        }

                        consumed += (int)parsed.ConsumedBytes;
                        if (!await HandleFrameAsync(parsed.Frame!.Value).ConfigureAwait(false))
                        {
                            return;
                        }
                    }

                    if (consumed > 0)
                    {
                        buffer.AsSpan(consumed, buffered - consumed).CopyTo(buffer);
                        buffered -= consumed;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private async Task<bool> HandleFrameAsync(VatpParsedFrame frame)
        {
            switch (frame.Header.Type)
            {
                case VatpFrameType.Hello:
                    return await HandleHelloAsync(frame).ConfigureAwait(false);
                case VatpFrameType.Open:
                    return await HandleOpenAsync(frame).ConfigureAwait(false);
                case VatpFrameType.Window:
                    await HandleWindowAsync(frame).ConfigureAwait(false);
                    return true;
                case VatpFrameType.Cancel:
                    if (emitSpuriousAfterTerminal())
                    {
                        await WriteSpuriousAfterTerminalAsync(frame.Header.StreamId).ConfigureAwait(false);
                    }

                    _streams.Remove(frame.Header.StreamId);
                    return true;
                default:
                    return true;
            }
        }

        private async Task<bool> HandleHelloAsync(VatpParsedFrame frame)
        {
            if (_clientHelloComplete
                || !VatpHello.TryDecode(frame.Payload, out var hello, out _))
            {
                await WriteFrameAsync(VatpFrameEncoder.EncodeFail(VatpProtocol.ConnectionStreamId, VatpErrorCode.InvalidHello))
                    .ConfigureAwait(false);
                return false;
            }

            _maxFramePayload = Math.Min(hello.MaxFramePayload, _limits.DefaultMaxFramePayload);
            _clientHelloComplete = true;
            await WriteFrameAsync(VatpFrameEncoder.EncodeHello(_maxFramePayload)).ConfigureAwait(false);
            return true;
        }

        private async Task<bool> HandleOpenAsync(VatpParsedFrame frame)
        {
            if (!VatpOpenPayload.TryDecode(frame.Payload, out var open, out _))
            {
                await WriteFrameAsync(VatpFrameEncoder.EncodeFail(frame.Header.StreamId, VatpErrorCode.InvalidFrameLength))
                    .ConfigureAwait(false);
                return true;
            }

            if (!articles.TryGetValue(open.ArticleId, out var registered))
            {
                await WriteFrameAsync(VatpFrameEncoder.EncodeFail(frame.Header.StreamId, VatpErrorCode.OpenRejected))
                    .ConfigureAwait(false);
                return true;
            }

            var record = registered.Record;
            var meta = ArticleCanonicalTransferMeta.FromRecord(in record, registered.SelectedDateHeaderName);
            var metaBytes = VatpMetaCodec.Encode(in meta);
            var transferMode = registered.ModeOverride ?? mode();
            observedOpenStreamIds.Add(frame.Header.StreamId);

            if (transferMode == TransferMode.MetaOnConnectionStreamId)
            {
                var header = new byte[VatpProtocol.HeaderLengthBytes];
                VatpFrameHeader.Create(
                        VatpFrameType.Meta,
                        VatpProtocol.ConnectionStreamId,
                        (uint)metaBytes.Length,
                        flags: 0)
                    .WriteTo(header);
                await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(metaBytes, cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (transferMode == TransferMode.DisconnectMidMeta)
            {
                var metaFrame = VatpFrameEncoder.EncodeMeta(frame.Header.StreamId, metaBytes);
                // Write only the 16-byte header so META never becomes usable.
                await stream.WriteAsync(metaFrame.Header, cancellationToken).ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
                return false;
            }

            if (transferMode == TransferMode.InvalidMeta)
            {
                var invalid = new byte[VatpProtocol.MetaPayloadLength];
                await WriteFrameAsync(VatpFrameEncoder.EncodeMeta(frame.Header.StreamId, invalid)).ConfigureAwait(false);
                return true;
            }

            if (transferMode == TransferMode.CorruptArtHash)
            {
                var corrupt = new ArticleCanonicalTransferMeta(
                    meta.ArtHash ^ 1UL,
                    meta.ArtLines,
                    meta.ArtSize,
                    meta.SelectedDateHeaderName,
                    meta.Fields);
                metaBytes = VatpMetaCodec.Encode(in corrupt);
            }

            if (transferMode == TransferMode.ShortDataFin)
            {
                await WriteFrameAsync(VatpFrameEncoder.EncodeMeta(frame.Header.StreamId, metaBytes)).ConfigureAwait(false);
                await WriteProbeDataAsync(frame.Header.StreamId, record.ArtData.ToArray(), fin: true).ConfigureAwait(false);
                return true;
            }

            if (transferMode == TransferMode.DataThenRemoteFail)
            {
                var artData = record.ArtData.ToArray();
                await WriteFrameAsync(VatpFrameEncoder.EncodeMeta(frame.Header.StreamId, metaBytes)).ConfigureAwait(false);
                await WriteProbeDataAsync(frame.Header.StreamId, artData, fin: false).ConfigureAwait(false);
                await WriteFrameAsync(VatpFrameEncoder.EncodeFail(frame.Header.StreamId, VatpErrorCode.OpenRejected))
                    .ConfigureAwait(false);
                return true;
            }

            if (transferMode == TransferMode.DataThenSizeReject)
            {
                var artData = record.ArtData.ToArray();
                await WriteFrameAsync(VatpFrameEncoder.EncodeMeta(frame.Header.StreamId, metaBytes)).ConfigureAwait(false);
                await WriteProbeDataAsync(frame.Header.StreamId, artData, fin: false).ConfigureAwait(false);
                var overflow = new byte[artData.Length - VatpLoopbackTestServer.ProbePayloadBytes + 1];
                await WriteFrameAsync(VatpFrameEncoder.EncodeData(frame.Header.StreamId, overflow, fin: false))
                    .ConfigureAwait(false);
                return true;
            }

            var send = new SendStream(
                frame.Header.StreamId,
                record.ArtData.ToArray(),
                new ArticleTransferWindow(_limits.InitialStreamWindowBytes, _limits.MaxStreamCreditBytes),
                transferMode);
            _streams[frame.Header.StreamId] = send;

            if (transferMode == TransferMode.OversizedDataFrame)
            {
                await WriteFrameAsync(VatpFrameEncoder.EncodeMeta(frame.Header.StreamId, metaBytes))
                    .ConfigureAwait(false);
                await WriteOversizedDataFrameAsync(frame.Header.StreamId).ConfigureAwait(false);
                return true;
            }

            if (transferMode is TransferMode.CoalesceOutboundFrames or TransferMode.TinyOutboundWrites)
            {
                await WriteTransferBatchAsync(send, metaBytes).ConfigureAwait(false);
                return true;
            }

            if (transferMode == TransferMode.HoldAfterFirstData)
            {
                await WriteFrameAsync(VatpFrameEncoder.EncodeMeta(frame.Header.StreamId, metaBytes))
                    .ConfigureAwait(false);
                var firstChunk = (int)Math.Min(
                    Math.Min(send.SendWindow.Credit, _maxFramePayload),
                    send.ArtData.Length);
                if (firstChunk <= 0 || !send.SendWindow.TryConsume(firstChunk))
                {
                    return true;
                }

                send.SentBytes = firstChunk;
                await WriteFrameAsync(
                        VatpFrameEncoder.EncodeData(
                            frame.Header.StreamId,
                            send.ArtData.AsMemory(0, firstChunk),
                            fin: false))
                    .ConfigureAwait(false);
                registered.HeldStarted?.TrySetResult();
                return true;
            }

            await WriteFrameAsync(VatpFrameEncoder.EncodeMeta(frame.Header.StreamId, metaBytes)).ConfigureAwait(false);
            await PumpStreamAsync(send).ConfigureAwait(false);
            return true;
        }

        private async Task WriteTransferBatchAsync(SendStream sendStream, ReadOnlyMemory<byte> metaBytes)
        {
            var frames = new List<byte[]>();
            frames.Add(VatpFrameEncoder.ToSingleBuffer(
                VatpFrameEncoder.EncodeMeta(sendStream.StreamId, metaBytes)));

            var offset = 0;
            while (offset < sendStream.ArtData.Length)
            {
                var remaining = sendStream.ArtData.Length - offset;
                var chunkSize = (int)Math.Min(_maxFramePayload, remaining);
                var fin = offset + chunkSize >= sendStream.ArtData.Length;
                var chunk = sendStream.ArtData.AsMemory(offset, chunkSize);
                frames.Add(VatpFrameEncoder.ToSingleBuffer(
                    VatpFrameEncoder.EncodeData(sendStream.StreamId, chunk, fin)));
                offset += chunkSize;
            }

            frames.Add(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeEnd(sendStream.StreamId)));

            var total = 0;
            foreach (var frame in frames)
            {
                total += frame.Length;
            }

            var batch = new byte[total];
            var written = 0;
            foreach (var frame in frames)
            {
                frame.AsSpan().CopyTo(batch.AsSpan(written));
                written += frame.Length;
            }

            if (sendStream.TransferMode == TransferMode.TinyOutboundWrites)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    await stream.WriteAsync(batch.AsMemory(i, 1), cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await stream.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
            }

            sendStream.SentBytes = sendStream.ArtData.Length;
            _streams.Remove(sendStream.StreamId);
        }

        private async Task WriteOversizedDataFrameAsync(uint streamId)
        {
            var oversizedPayload = (int)_maxFramePayload + 1;
            var header = new byte[VatpProtocol.HeaderLengthBytes];
            VatpFrameHeader.Create(VatpFrameType.Data, streamId, (uint)oversizedPayload, flags: 0)
                .WriteTo(header);
            var payload = new byte[oversizedPayload];
            payload.AsSpan().Fill(0x41);
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        private async Task HandleWindowAsync(VatpParsedFrame frame)
        {
            if (!_streams.TryGetValue(frame.Header.StreamId, out var sendStream))
            {
                await WriteFrameAsync(VatpFrameEncoder.EncodeFail(frame.Header.StreamId, VatpErrorCode.UnknownStream))
                    .ConfigureAwait(false);
                return;
            }

            Span<byte> windowBytes = stackalloc byte[VatpProtocol.WindowPayloadLength];
            frame.Payload.CopyTo(windowBytes);
            if (!VatpControlPayload.TryDecodeWindow(windowBytes, out var credit))
            {
                return;
            }

            _ = sendStream.SendWindow.Add(credit);
            await PumpStreamAsync(sendStream).ConfigureAwait(false);
        }

        private async Task PumpStreamAsync(SendStream sendStream)
        {
            if (sendStream.TransferMode == TransferMode.HoldAfterFirstData)
            {
                return;
            }

            while (sendStream.SentBytes < sendStream.ArtData.Length && sendStream.SendWindow.Credit > 0)
            {
                if (sendStream.TransferMode == TransferMode.DisconnectMidData && sendStream.SentBytes > 0)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                var remaining = sendStream.ArtData.Length - sendStream.SentBytes;
                var chunkSize = (int)Math.Min(Math.Min(sendStream.SendWindow.Credit, _maxFramePayload), remaining);
                if (chunkSize <= 0)
                {
                    return;
                }

                if (!sendStream.SendWindow.TryConsume(chunkSize))
                {
                    return;
                }

                sendStream.SentBytes += chunkSize;
                var fin = sendStream.SentBytes >= sendStream.ArtData.Length;
                var chunk = sendStream.ArtData.AsMemory(sendStream.SentBytes - chunkSize, chunkSize);
                await WriteFrameAsync(VatpFrameEncoder.EncodeData(sendStream.StreamId, chunk, fin)).ConfigureAwait(false);

                if (fin && sendStream.TransferMode == TransferMode.FinWithoutEnd)
                {
                    return;
                }

                if (fin && sendStream.TransferMode == TransferMode.DisconnectAfterFinBeforeEnd)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                if (fin && sendStream.TransferMode is TransferMode.Complete or TransferMode.CorruptArtHash)
                {
                    await WriteFrameAsync(VatpFrameEncoder.EncodeEnd(sendStream.StreamId)).ConfigureAwait(false);
                    _streams.Remove(sendStream.StreamId);
                    if (emitSpuriousAfterTerminal())
                    {
                        await WriteSpuriousAfterTerminalAsync(sendStream.StreamId).ConfigureAwait(false);
                    }
                }
            }
        }

        private async Task WriteSpuriousAfterTerminalAsync(uint terminalStreamId)
        {
            // Late frames for a stream the client already (or will imminently) remove.
            await WriteFrameAsync(VatpFrameEncoder.EncodeEnd(terminalStreamId)).ConfigureAwait(false);
            await WriteFrameAsync(
                    VatpFrameEncoder.EncodeFail(terminalStreamId, VatpErrorCode.UnknownStream))
                .ConfigureAwait(false);
            await WriteFrameAsync(
                    VatpFrameEncoder.EncodeFail(terminalStreamId, VatpErrorCode.OpenRejected))
                .ConfigureAwait(false);

            // Completely unallocated StreamId: must not create client stream state.
            var junkMeta = new byte[VatpProtocol.MetaPayloadLength];
            await WriteFrameAsync(VatpFrameEncoder.EncodeMeta(NeverAllocatedStreamId, junkMeta))
                .ConfigureAwait(false);
            await WriteFrameAsync(
                    VatpFrameEncoder.EncodeData(NeverAllocatedStreamId, new byte[] { 0x41 }, fin: true))
                .ConfigureAwait(false);
            await WriteFrameAsync(VatpFrameEncoder.EncodeEnd(NeverAllocatedStreamId)).ConfigureAwait(false);
            await WriteFrameAsync(
                    VatpFrameEncoder.EncodeFail(NeverAllocatedStreamId, VatpErrorCode.UnknownStream))
                .ConfigureAwait(false);
        }

        private async Task WriteProbeDataAsync(uint streamId, byte[] artData, bool fin)
        {
            if (artData.Length <= VatpLoopbackTestServer.ProbePayloadBytes)
            {
                throw new InvalidOperationException("Article is smaller than the accepted-data probe.");
            }

            await WriteFrameAsync(
                    VatpFrameEncoder.EncodeData(
                        streamId,
                        artData.AsMemory(0, VatpLoopbackTestServer.ProbePayloadBytes),
                        fin))
                .ConfigureAwait(false);
        }

        private async Task WriteFrameAsync(VatpFrameEncoder.EncodedFrame frame)
        {
            if (frame.Header.Length > 0)
            {
                await stream.WriteAsync(frame.Header, cancellationToken).ConfigureAwait(false);
            }

            if (frame.Payload.Length > 0)
            {
                await stream.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class SendStream(
        uint streamId,
        byte[] artData,
        ArticleTransferWindow sendWindow,
        TransferMode transferMode)
    {
        public uint StreamId { get; } = streamId;

        public byte[] ArtData { get; } = artData;

        public ArticleTransferWindow SendWindow { get; } = sendWindow;

        public TransferMode TransferMode { get; } = transferMode;

        public int SentBytes { get; set; }
    }
}

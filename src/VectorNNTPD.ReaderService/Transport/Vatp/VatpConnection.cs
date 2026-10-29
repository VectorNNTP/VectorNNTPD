using System.Buffers;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading.Channels;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Transport.ArticleTransfer;

namespace VectorNNTPD.ReaderService.Transport.Vatp
{
    /// <summary>One multiplexed TLS+VATP connection to a cache listener.</summary>
    internal sealed class VatpConnection : IAsyncDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly Stream _stream;
        private readonly TcpClient _tcp;
        private readonly ILogger _logger;
        private readonly VatpClientOptions _options;
        private readonly ArticleTransferLimits _limits;
        private readonly ArticleTransferStreamTable _streamTable;
        private readonly Dictionary<uint, PendingStream> _pending = new();
        private readonly object _gate = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly Channel<VatpFrameEncoder.EncodedFrame> _outbound;
        private uint _nextStreamId = 1;
        private uint _maxFramePayload = VatpProtocol.DefaultMaxFramePayload;
        private Task? _runTask;
        private CancellationTokenSource? _runCts;
        private int _disposed;
        private int _dead;

        private VatpConnection(
            string host,
            int port,
            TcpClient tcp,
            Stream stream,
            ILogger logger,
            VatpClientOptions options,
            ArticleTransferLimits limits,
            uint maxFramePayload)
        {
            _host = host;
            _port = port;
            _tcp = tcp;
            _stream = stream;
            _logger = logger;
            _options = options;
            _limits = limits;
            _streamTable = new ArticleTransferStreamTable(limits);
            _maxFramePayload = maxFramePayload;
            _outbound = Channel.CreateUnbounded<VatpFrameEncoder.EncodedFrame>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        }

        /// <summary>Gets whether the connection is dead and must be removed from the pool.</summary>
        public bool IsDead => Volatile.Read(ref _dead) != 0;

        /// <summary>Gets whether another stream can be opened on this connection.</summary>
        public bool HasStreamCapacity
        {
            get
            {
                lock (_gate)
                {
                    return !IsDead && _streamTable.Count < _limits.MaxStreamsPerConnection;
                }
            }
        }

        /// <summary>Endpoint host used for TLS and logging.</summary>
        public string Host => _host;

        /// <summary>Endpoint port.</summary>
        public int Port => _port;

        internal static async Task<VatpConnection> ConnectAsync(
            string host,
            int port,
            ILogger logger,
            VatpClientOptions options,
            RemoteCertificateValidationCallback? serverCertificateValidationCallback,
            CancellationToken cancellationToken,
            string? tcpConnectHost = null)
        {
            VatpClientLogMessages.Connecting(logger, host, port);
            var tcp = new TcpClient();
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(options.ConnectTimeout);
                await tcp.ConnectAsync(tcpConnectHost ?? host, port, connectCts.Token).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tcp.Dispose();
                throw new VatpConnectionException("TCP connect failed.", ex);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                tcp.Dispose();
                throw new VatpConnectionException("TCP connect timed out.");
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch
            {
                tcp.Dispose();
                throw;
            }

            Stream transport;
            try
            {
                var ssl = await VatpTlsClient.AuthenticateAsClientAsync(
                    tcp.GetStream(),
                    host,
                    options.TlsHandshakeTimeout,
                    serverCertificateValidationCallback,
                    cancellationToken).ConfigureAwait(false);
                transport = ssl;
                VatpClientLogMessages.TlsEstablished(logger, host, port);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tcp.Dispose();
                throw new VatpConnectionException("TLS handshake failed.", ex);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                tcp.Dispose();
                throw new VatpConnectionException("TLS handshake timed out.");
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch
            {
                tcp.Dispose();
                throw;
            }

            var limits = ArticleTransferLimits.Default;
            var connection = new VatpConnection(
                host,
                port,
                tcp,
                transport,
                logger,
                options,
                limits,
                VatpProtocol.DefaultMaxFramePayload);

            try
            {
                var maxPayload = await connection.PerformHelloExchangeAsync(cancellationToken).ConfigureAwait(false);
                connection._maxFramePayload = maxPayload;
                VatpClientLogMessages.HelloComplete(logger, host, port, maxPayload);
                connection.StartRunLoop();
                return connection;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        internal async Task<VatpFetchResult> FetchArticleAsync(
            Guid requestId,
            ArticleId articleId,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (IsDead)
            {
                return VatpFetchResult.ConnectionFailure("VATP connection is no longer usable.", requestId, articleId);
            }

            uint streamId;
            PendingStream pending;
            lock (_gate)
            {
                if (_streamTable.Count >= _limits.MaxStreamsPerConnection)
                {
                    return VatpFetchResult.ConnectionFailure("VATP connection stream table is full.", requestId, articleId);
                }

                streamId = AllocateStreamIdLocked();
                var openResult = _streamTable.TryOpen(streamId, requestId, articleId, out var receiveStream);
                if (!openResult.Success || receiveStream is null)
                {
                    RecycleStreamIdLocked(streamId);
                    return VatpFetchResult.ProtocolFailure(
                        $"Failed to open local stream: {openResult.Error}",
                        requestId,
                        articleId);
                }

                pending = new PendingStream(receiveStream, articleId);
                _pending[streamId] = pending;
            }

            var articleHex = articleId.ToLowerHexString();
            VatpClientLogMessages.OpenSent(_logger, streamId, requestId, articleHex);

            Span<byte> artBytes = stackalloc byte[ArticleId.Length];
            articleId.CopyTo(artBytes);
            await EnqueueFrameAsync(
                VatpFrameEncoder.EncodeOpen(streamId, requestId, artBytes),
                cancellationToken).ConfigureAwait(false);

            using var cancelReg = cancellationToken.Register(static state =>
            {
                var tuple = ((VatpConnection Connection, uint StreamId))state!;
                _ = tuple.Connection.TrySendCancelAsync(tuple.StreamId);
            }, (this, streamId));

            try
            {
                return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return VatpFetchResult.Cancelled(requestId, articleId, pending.ReceiveStream.ReceivedBytes);
            }
            finally
            {
                lock (_gate)
                {
                    _pending.Remove(streamId);
                    _streamTable.TryRemove(streamId);
                    RecycleStreamIdLocked(streamId);
                }
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            MarkDead("Connection disposed.");
            try
            {
                _runCts?.Cancel();
            }
            catch (ObjectDisposedException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }

            _outbound.Writer.TryComplete();
            if (_runTask is not null)
            {
                try
                {
                    await _runTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                }
            }

            await _stream.DisposeAsync().ConfigureAwait(false);
            _tcp.Dispose();
            _writeLock.Dispose();
            VatpClientLogMessages.ConnectionClosed(_logger, _host, _port);
        }

        private async Task<uint> PerformHelloExchangeAsync(CancellationToken cancellationToken)
        {
            await WriteFrameDirectAsync(
                VatpFrameEncoder.EncodeHello(_limits.DefaultMaxFramePayload),
                cancellationToken).ConfigureAwait(false);

            var frame = await ReadOneFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame.Header.Type != VatpFrameType.Hello
                || frame.Header.StreamId != VatpProtocol.ConnectionStreamId)
            {
                throw new VatpConnectionException("Expected server HELLO.");
            }

            Span<byte> helloBytes = stackalloc byte[VatpProtocol.HelloPayloadLength];
            CopyPayload(frame.Payload, helloBytes);
            if (!VatpHello.TryDecode(helloBytes, out var hello, out var error))
            {
                throw new VatpConnectionException($"Invalid server HELLO: {error}");
            }

            return Math.Min(hello.MaxFramePayload, _limits.DefaultMaxFramePayload);
        }

        private void StartRunLoop()
        {
            _runCts = new CancellationTokenSource();
            _runTask = RunAsync(_runCts.Token);
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            var writer = RunWriterAsync(cancellationToken);
            try
            {
                await RunReadLoopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                MarkDead(ex.Message);
            }
            finally
            {
                _outbound.Writer.TryComplete();
                try
                {
                    await writer.ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                }

                FailAllPending(VatpFetchResult.ConnectionFailure("VATP connection closed."));
            }
        }

        private async Task RunWriterAsync(CancellationToken cancellationToken)
        {
            await foreach (var frame in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await WriteFrameDirectAsync(frame, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task RunReadLoopAsync(CancellationToken cancellationToken)
        {
            // Socket reads are independent of VATP framing: one ReadAsync may yield a partial
            // frame, one frame, or many frames. Only the incomplete tail is retained, and that
            // tail is bounded to one negotiated maximum frame.
            var readBuffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
            var parseBuffer = ArrayPool<byte>.Shared.Rent(VatpProtocol.HeaderLengthBytes + (int)_maxFramePayload);
            var buffered = 0;
            try
            {
                while (!cancellationToken.IsCancellationRequested && !IsDead)
                {
                    var maxIncomplete = VatpProtocol.HeaderLengthBytes + (int)_maxFramePayload;
                    if (buffered > maxIncomplete)
                    {
                        MarkDead("Read buffer exceeded negotiated frame size.");
                        return;
                    }

                    int bytesRead;
                    try
                    {
                        using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        ioCts.CancelAfter(_options.IoTimeout);
                        bytesRead = await _stream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), ioCts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        MarkDead("Read timed out.");
                        return;
                    }

                    if (bytesRead == 0)
                    {
                        MarkDead("Peer closed connection.");
                        return;
                    }

                    EnsureParseCapacity(ref parseBuffer, buffered + bytesRead);
                    readBuffer.AsSpan(0, bytesRead).CopyTo(parseBuffer.AsSpan(buffered));
                    buffered += bytesRead;

                    var consumed = 0;
                    while (consumed < buffered)
                    {
                        var candidate = new ReadOnlySequence<byte>(parseBuffer, consumed, buffered - consumed);
                        var parsed = VatpFrameParser.ParseOneFrame(in candidate, _maxFramePayload);
                        if (parsed.Status == VatpFrameParseStatus.Incomplete)
                        {
                            break;
                        }

                        if (parsed.Status == VatpFrameParseStatus.Invalid)
                        {
                            VatpClientLogMessages.ProtocolViolation(_logger, _host, _port, parsed.Error.ToString());
                            MarkDead($"Invalid frame: {parsed.Error}");
                            return;
                        }

                        consumed += checked((int)parsed.ConsumedBytes);
                        if (!await HandleFrameAsync(parsed.Frame!.Value, cancellationToken).ConfigureAwait(false))
                        {
                            return;
                        }
                    }

                    if (consumed > 0)
                    {
                        parseBuffer.AsSpan(consumed, buffered - consumed).CopyTo(parseBuffer);
                        buffered -= consumed;
                    }

                    // After dispatching every complete frame, only an incomplete frame may remain.
                    if (buffered > maxIncomplete)
                    {
                        MarkDead("Read buffer exceeded negotiated frame size.");
                        return;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(readBuffer);
                ArrayPool<byte>.Shared.Return(parseBuffer);
            }
        }

        private async Task<bool> HandleFrameAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
        {
            if (frame.Header.Type == VatpFrameType.Fail)
            {
                return HandleFailFrame(frame);
            }

            if (frame.Header.StreamId == VatpProtocol.ConnectionStreamId)
            {
                VatpClientLogMessages.ProtocolViolation(_logger, _host, _port, "Unexpected connection-level frame.");
                MarkDead("Unexpected connection-level frame.");
                return false;
            }

            PendingStream? pending;
            ArticleTransferReceiveStream? receive;
            lock (_gate)
            {
                if (!_pending.TryGetValue(frame.Header.StreamId, out pending))
                {
                    // Late META/DATA/END after local stream cleanup (e.g. fetch already
                    // completed while WINDOW/FAIL races drained) must not kill the connection.
                    VatpClientLogMessages.ProtocolViolation(
                        _logger,
                        _host,
                        _port,
                        $"Ignoring frame for inactive StreamId {frame.Header.StreamId}.");
                    return !IsDead;
                }

                receive = pending.ReceiveStream;
            }

            switch (frame.Header.Type)
            {
                case VatpFrameType.Meta:
                    VatpClientLogMessages.MetaReceived(
                        _logger,
                        frame.Header.StreamId,
                        receive.RequestId,
                        receive.ExpectedArtId.ToLowerHexString());
                    if (!receive.TryAcceptMeta(frame.Payload).Success)
                    {
                        CompletePending(
                            frame.Header.StreamId,
                            ToFetchFailure(receive, "META rejected."));
                    }

                    break;
                case VatpFrameType.Data:
                    {
                        // Receive credit is consumed on accept. Replenish locally and advertise
                        // the same amount via WINDOW so ArtSize may exceed InitialStreamWindowBytes
                        // without treating the initial window as an article-size ceiling.
                        var apply = receive.TryAcceptData(frame.Payload, frame.Header.HasFin);
                        if (!apply.Success)
                        {
                            CompletePending(
                                frame.Header.StreamId,
                                ToFetchFailure(receive, "DATA rejected."));
                            break;
                        }

                        var payloadLength = checked((uint)frame.Payload.Length);
                        // Pair outbound WINDOW with local credit restore. Skip both when the
                        // stream was cancelled between accept and replenish (no cross-stream credit).
                        if (payloadLength > 0 && receive.TryAcceptWindow(payloadLength).Success)
                        {
                            await EnqueueFrameAsync(
                                VatpFrameEncoder.EncodeWindow(frame.Header.StreamId, payloadLength),
                                cancellationToken).ConfigureAwait(false);
                        }

                        if (receive.IsTerminal)
                        {
                            CompletePending(
                                frame.Header.StreamId,
                                ToFetchFailure(receive, "DATA rejected."));
                        }

                        break;
                    }

                case VatpFrameType.End:
                    if (!receive.TryAcceptEnd().Success)
                    {
                        CompletePending(
                            frame.Header.StreamId,
                            ToFetchFailure(receive, "END rejected."));
                        break;
                    }

                    if (receive.TryTakeRecord(out var record))
                    {
                        VatpClientLogMessages.TransferComplete(
                            _logger,
                            frame.Header.StreamId,
                            receive.RequestId,
                            receive.ExpectedArtId.ToLowerHexString());
                        CompletePending(
                            frame.Header.StreamId,
                            VatpFetchResult.FromSuccess(
                                record,
                                receive.RequestId,
                                receive.ExpectedArtId,
                                receive.ReceivedBytes));
                    }
                    else
                    {
                        CompletePending(
                            frame.Header.StreamId,
                            VatpFetchResult.IncompleteOrMalformed(
                                "END received without consumable record.",
                                receive.Failure,
                                receive.RequestId,
                                receive.ExpectedArtId,
                                receive.ReceivedBytes));
                    }

                    break;
                default:
                    VatpClientLogMessages.ProtocolViolation(
                        _logger,
                        _host,
                        _port,
                        $"Unexpected frame type {frame.Header.Type}.");
                    MarkDead("Unexpected frame type.");
                    return false;
            }

            return !IsDead;
        }

        private bool HandleFailFrame(VatpParsedFrame frame)
        {
            Span<byte> payload = stackalloc byte[(int)Math.Min(frame.Payload.Length, VatpProtocol.FailMaxPayloadLength)];
            CopyPayload(frame.Payload, payload);
            if (!VatpControlPayload.TryDecodeFail(payload, out var errorCode, out var reason))
            {
                MarkDead("Invalid FAIL payload.");
                return false;
            }

            var reasonText = reason.IsEmpty ? null : System.Text.Encoding.ASCII.GetString(reason);
            if (frame.Header.StreamId == VatpProtocol.ConnectionStreamId)
            {
                MarkDead(reasonText ?? errorCode.ToString());
                return false;
            }

            // Server may emit UnknownStream for WINDOW/CANCEL that arrive after it already
            // removed a finished stream (common when ArtSize fits in the initial window).
            // That race must not fail an in-flight or just-completed transfer, nor poison siblings.
            if (errorCode == VatpErrorCode.UnknownStream)
            {
                return !IsDead;
            }

            lock (_gate)
            {
                if (_pending.TryGetValue(frame.Header.StreamId, out var pending))
                {
                    VatpClientLogMessages.TransferFailed(
                        _logger,
                        frame.Header.StreamId,
                        pending.ReceiveStream.RequestId,
                        pending.ArticleId.ToLowerHexString(),
                        errorCode);
                    CompletePendingLocked(
                        frame.Header.StreamId,
                        VatpFetchResult.RemoteFailure(
                            reasonText,
                            errorCode,
                            pending.ReceiveStream.RequestId,
                            pending.ArticleId,
                            pending.ReceiveStream.ReceivedBytes));
                }
            }

            return !IsDead;
        }

        private void CompletePending(uint streamId, VatpFetchResult result)
        {
            lock (_gate)
            {
                CompletePendingLocked(streamId, result);
            }
        }

        private void CompletePendingLocked(uint streamId, VatpFetchResult result)
        {
            if (_pending.TryGetValue(streamId, out var pending))
            {
                pending.Completion.TrySetResult(result);
            }
        }

        private VatpFetchResult ToFetchFailure(ArticleTransferReceiveStream receive, string detail)
        {
            VatpClientLogMessages.TransferFailed(
                _logger,
                receive.StreamId,
                receive.RequestId,
                receive.ExpectedArtId.ToLowerHexString(),
                receive.Failure);
            if (receive.Failure == VatpErrorCode.Cancelled)
            {
                return VatpFetchResult.Cancelled(
                    receive.RequestId,
                    receive.ExpectedArtId,
                    receive.ReceivedBytes);
            }

            return VatpFetchResult.IncompleteOrMalformed(
                detail,
                receive.Failure,
                receive.RequestId,
                receive.ExpectedArtId,
                receive.ReceivedBytes);
        }

        private void FailAllPending(VatpFetchResult result)
        {
            lock (_gate)
            {
                foreach (var (streamId, pending) in _pending.ToArray())
                {
                    pending.Completion.TrySetResult(
                        VatpFetchResult.ConnectionFailure(
                            result.Error,
                            pending.ReceiveStream.RequestId,
                            pending.ArticleId,
                            pending.ReceiveStream.ReceivedBytes));
                    _pending.Remove(streamId);
                    _streamTable.TryRemove(streamId);
                    RecycleStreamIdLocked(streamId);
                }
            }
        }

        private void MarkDead(string reason)
        {
            if (Interlocked.Exchange(ref _dead, 1) == 1)
            {
                return;
            }

            VatpClientLogMessages.ProtocolViolation(_logger, _host, _port, reason);
            FailAllPending(VatpFetchResult.ConnectionFailure(reason));
        }

        private async Task TrySendCancelAsync(uint streamId)
        {
            PendingStream? pending;
            lock (_gate)
            {
                if (!_pending.TryGetValue(streamId, out pending) || pending.ReceiveStream.IsTerminal)
                {
                    return;
                }

                _ = pending.ReceiveStream.TryCancel();
            }

            VatpClientLogMessages.CancelSent(
                _logger,
                streamId,
                pending!.ReceiveStream.RequestId,
                pending.ArticleId.ToLowerHexString());

            try
            {
                await EnqueueFrameAsync(VatpFrameEncoder.EncodeCancel(streamId), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (ChannelClosedException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
        }

        private async Task EnqueueFrameAsync(VatpFrameEncoder.EncodedFrame frame, CancellationToken cancellationToken)
        {
            if (IsDead)
            {
                throw new VatpConnectionException("Connection is dead.");
            }

            await _outbound.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }

        private async Task WriteFrameDirectAsync(VatpFrameEncoder.EncodedFrame frame, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (frame.Header.Length > 0)
                {
                    await _stream.WriteAsync(frame.Header, cancellationToken).ConfigureAwait(false);
                }

                if (frame.Payload.Length > 0)
                {
                    await _stream.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task<VatpParsedFrame> ReadOneFrameAsync(CancellationToken cancellationToken)
        {
            var header = new byte[VatpProtocol.HeaderLengthBytes];
            await ReadExactAsync(header, cancellationToken).ConfigureAwait(false);
            var parsedHeader = VatpFrameHeader.ReadFrom(header);
            var payload = new byte[parsedHeader.PayloadLength];
            if (payload.Length > 0)
            {
                await ReadExactAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            return new VatpParsedFrame(parsedHeader, new ReadOnlySequence<byte>(payload));
        }

        private async Task ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                ioCts.CancelAfter(_options.IoTimeout);
                var read = await _stream.ReadAsync(buffer[total..], ioCts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new VatpConnectionException("Unexpected end of stream.");
                }

                total += read;
            }
        }

        private uint AllocateStreamIdLocked()
        {
            // Do not reuse StreamIds on a live connection. Late WINDOW/END/FAIL for a just-
            // finished stream must not be applied to a successor transfer that recycled the id.
            // Concurrent capacity remains bounded by MaxStreamsPerConnection via the stream table.
            if (_nextStreamId == 0)
            {
                _nextStreamId = 1;
            }

            return _nextStreamId++;
        }

        private void RecycleStreamIdLocked(uint streamId)
        {
            // Intentionally unused for wire StreamId values; retained as a call-site hook so
            // stream-table removal stays paired with allocation cleanup.
            _ = streamId;
        }

        private static void EnsureParseCapacity(ref byte[] parseBuffer, int required)
        {
            if (required <= parseBuffer.Length)
            {
                return;
            }

            var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(parseBuffer.Length * 2, required));
            parseBuffer.AsSpan(0, Math.Min(parseBuffer.Length, required)).CopyTo(bigger);
            ArrayPool<byte>.Shared.Return(parseBuffer);
            parseBuffer = bigger;
        }

        private static void CopyPayload(in ReadOnlySequence<byte> payload, Span<byte> destination)
        {
            if (payload.Length > destination.Length)
            {
                throw new InvalidOperationException("Payload buffer too small.");
            }

            payload.CopyTo(destination);
        }

        private sealed class PendingStream(ArticleTransferReceiveStream receiveStream, ArticleId articleId)
        {
            public ArticleTransferReceiveStream ReceiveStream { get; } = receiveStream;

            public ArticleId ArticleId { get; } = articleId;

            public TaskCompletionSource<VatpFetchResult> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Connection setup failure surfaced to the pool.</summary>
    internal sealed class VatpConnectionException : Exception
    {
        public VatpConnectionException(string message)
            : base(message)
        {
        }

        public VatpConnectionException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}

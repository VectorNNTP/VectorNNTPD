using System.Buffers;
using System.Net.Security;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Transport.ArticleTransfer;
using VectorNNTPD.TransitService.Transport.Vatp;

namespace VectorNNTPD.TransitService.Storage
{
    /// <summary>
    /// One TLS connection to one placement target. One writer. At most
    /// <see cref="MaxOutstanding"/> STORE streams.
    /// </summary>
    internal sealed class ArticlePlacementConnection : IAsyncDisposable
    {
        private readonly string _fqdn;
        private readonly int _port;
        private readonly TcpClient _tcp;
        private readonly SslStream _ssl;
        private readonly ILogger _logger;
        private readonly ArticleTransferLimits _limits;
        private readonly TimeSpan _ioTimeout;
        private readonly SemaphoreSlim _slots;
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly object _tableGate = new();
        private readonly Dictionary<uint, StoreOperation> _operations = [];
        private readonly CancellationTokenSource _lifetime = new();
        private readonly FrameBuffer _frames = new();
        private Task? _reader;
        private int _dead;
        private int _disposed;
        private int _active;
        private int _peakActive;
        private uint _nextStreamId = 1;
        private uint _maxFramePayload;
        private long _slotWaitTicks;
        private long _creditWaitTicks;
        private long _resultTicks;
        private long _resultCount;
        private long _resultMaxTicks;
        private long _dataFramesWritten;
        private int _storesAwaitingInitialCredit;
        private readonly object _readTimeoutGate = new();
        private CancellationTokenSource? _activeReadTimeout;

        private ArticlePlacementConnection(
            string fqdn,
            int port,
            TcpClient tcp,
            SslStream ssl,
            ILogger logger,
            ArticleTransferLimits limits,
            TimeSpan ioTimeout,
            int maxOutstanding,
            uint maxFramePayload)
        {
            _fqdn = fqdn;
            _port = port;
            _tcp = tcp;
            _ssl = ssl;
            _logger = logger;
            _limits = limits;
            _ioTimeout = ioTimeout;
            MaxOutstanding = maxOutstanding;
            _maxFramePayload = maxFramePayload;
            _slots = new SemaphoreSlim(maxOutstanding, maxOutstanding);
        }

        public int MaxOutstanding { get; }

        /// <summary>Gets the highest number of STORE operations that were in flight together.</summary>
        internal int PeakActive => Volatile.Read(ref _peakActive);

        /// <summary>Gets ticks spent waiting for a free STORE slot.</summary>
        internal long SlotWaitTicks => Volatile.Read(ref _slotWaitTicks);

        /// <summary>Gets ticks spent waiting for WINDOW credit.</summary>
        internal long CreditWaitTicks => Volatile.Read(ref _creditWaitTicks);

        /// <summary>Gets how many RESULT waits were recorded.</summary>
        internal long ResultCount => Volatile.Read(ref _resultCount);

        /// <summary>Gets the total ticks spent waiting for RESULT after END.</summary>
        internal long ResultTicks => Volatile.Read(ref _resultTicks);

        /// <summary>Gets the longest RESULT wait in ticks.</summary>
        internal long ResultMaxTicks => Volatile.Read(ref _resultMaxTicks);

        /// <summary>Gets DATA frames this connection has written. Credit is zero until the server sends WINDOW.</summary>
        internal long DataFramesWritten => Volatile.Read(ref _dataFramesWritten);

        public bool IsDead => Volatile.Read(ref _dead) != 0;

        public int ActiveStores => Volatile.Read(ref _active);

        public static async Task<ArticlePlacementConnection> EstablishAsync(
            string fqdn,
            int port,
            string connectHost,
            ILogger logger,
            VatpClientOptions options,
            ArticleTransferLimits limits,
            int maxOutstanding,
            RemoteCertificateValidationCallback? certificateValidation,
            CancellationToken cancellationToken)
        {
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                tcp.SendBufferSize = 1 << 20;
                tcp.ReceiveBufferSize = 1 << 20;
            }
            catch (SocketException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(options.ConnectTimeout);
            try
            {
                await tcp.ConnectAsync(connectHost, port, connectCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                tcp.Dispose();
                if (ex.InnerException is SocketException)
                {
                    throw ex.InnerException;
                }

                throw new TimeoutException("timeout");
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch
            {
                tcp.Dispose();
                throw;
            }
            SslStream ssl;
            try
            {
                ssl = await VatpTlsClient.AuthenticateAsClientAsync(
                        tcp.GetStream(),
                        fqdn,
                        options.TlsHandshakeTimeout,
                        certificateValidation,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("timeout");
            }
            var connection = new ArticlePlacementConnection(
                fqdn,
                port,
                tcp,
                ssl,
                logger,
                limits,
                options.IoTimeout,
                maxOutstanding,
                limits.DefaultMaxFramePayload);
            try
            {
                await connection.ExchangeHelloAsync(cancellationToken).ConfigureAwait(false);
                connection._reader = Task.Run(connection.ReadLoopAsync);
                ArticlePlacementTransportLogMessages.Established(logger, fqdn, port, maxOutstanding);
                return connection;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task<ArticlePlacementResult> PlaceAsync(
            ArticleRecord record,
            ArticleCanonicalTransferMeta meta,
            CancellationToken cancellationToken)
        {
            if (IsDead || Volatile.Read(ref _disposed) != 0)
            {
                return new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "closed");
            }

            var slotWait = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref _slotWaitTicks, System.Diagnostics.Stopwatch.GetTimestamp() - slotWait);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new ArticlePlacementResult(ArticlePlacementKind.Cancelled);
            }
            catch (ObjectDisposedException)
            {
                return new ArticlePlacementResult(ArticlePlacementKind.Cancelled);
            }

            try
            {
                using (var operation = Begin(record))
                {
                    try
                    {
                        var now = Interlocked.Increment(ref _active);
                        int previousPeak;
                        do
                        {
                            previousPeak = Volatile.Read(ref _peakActive);
                            if (now <= previousPeak)
                            {
                                break;
                            }
                        }
                        while (Interlocked.CompareExchange(ref _peakActive, now, previousPeak) != previousPeak);

                        return await SendAsync(operation, record, meta, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _active);
                        lock (_tableGate)
                        {
                            _operations.Remove(operation.StreamId);
                        }
                    }
                }
            }
            finally
            {
                try
                {
                    _slots.Release();
                }
                catch (ObjectDisposedException ex)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var outstanding = ActiveStores;
            MarkDead();
            try
            {
                await _lifetime.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }

            try
            {
                if (_reader is not null)
                {
                    await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
            }
            catch (TimeoutException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
            catch (OperationCanceledException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }

            FaultOutstanding(shutdown: true);
            ArticlePlacementTransportLogMessages.Closed(_logger, _fqdn, _port, outstanding);
            await _ssl.DisposeAsync().ConfigureAwait(false);
            _tcp.Dispose();
            _slots.Dispose();
            _write.Dispose();
            _lifetime.Dispose();
        }

        private StoreOperation Begin(ArticleRecord record)
        {
            lock (_tableGate)
            {
                if (_nextStreamId == 0)
                {
                    MarkDead();
                    throw new IOException("stream-id-space");
                }

                var streamId = _nextStreamId++;
                var operation = new StoreOperation(streamId, record.ArtId, _limits);
                _operations.Add(streamId, operation);
                return operation;
            }
        }

        private async Task<ArticlePlacementResult> SendAsync(
            StoreOperation operation,
            ArticleRecord record,
            ArticleCanonicalTransferMeta meta,
            CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            try
            {
                var idBytes = new byte[ArticleId.Length];
                record.ArtId.CopyTo(idBytes);
                await WriteFrameAsync(VatpFrameEncoder.EncodeStore(operation.StreamId, idBytes), linked.Token)
                    .ConfigureAwait(false);
                operation.Started = true;
                var metaBytes = VatpMetaCodec.Encode(in meta);
                operation.AwaitingInitialCredit = 1;
                Interlocked.Increment(ref _storesAwaitingInitialCredit);
                await WriteFrameAsync(VatpFrameEncoder.EncodeMeta(operation.StreamId, metaBytes), linked.Token)
                    .ConfigureAwait(false);
                DisarmReadTimeout();

                var artData = record.ArtData;
                var offset = 0;
                while (offset < record.ArtSize)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    int length;
                    lock (operation.Gate)
                    {
                        length = ArticleTransferReadyRing.ComputeDataPayloadLength(
                            record.ArtSize - offset,
                            operation.Window.Credit,
                            _maxFramePayload);
                        if (length > 0 && !operation.Window.TryConsume(length))
                        {
                            length = 0;
                        }
                    }

                    if (length <= 0)
                    {
                        var creditWait = System.Diagnostics.Stopwatch.GetTimestamp();
                        await operation.Credit.WaitAsync(linked.Token).ConfigureAwait(false);
                        Interlocked.Add(ref _creditWaitTicks, System.Diagnostics.Stopwatch.GetTimestamp() - creditWait);
                        if (operation.IsCompleted)
                        {
                            return operation.Result;
                        }

                        continue;
                    }

                    var fin = offset + length == record.ArtSize;
                    await WriteFrameAsync(
                            VatpFrameEncoder.EncodeData(operation.StreamId, artData.Slice(offset, length), fin),
                            linked.Token)
                        .ConfigureAwait(false);
                    Interlocked.Increment(ref _dataFramesWritten);
                    offset += length;
                }

                await WriteFrameAsync(VatpFrameEncoder.EncodeEnd(operation.StreamId), linked.Token).ConfigureAwait(false);
                operation.EndWritten = true;
                var resultWait = System.Diagnostics.Stopwatch.GetTimestamp();
                var result = await operation.Completion.Task.WaitAsync(_ioTimeout, linked.Token).ConfigureAwait(false);
                NoteResultWait(System.Diagnostics.Stopwatch.GetTimestamp() - resultWait);
                LogOutcome(operation, result);
                return result;
            }
            catch (TimeoutException)
            {
                var result = operation.EndWritten
                    ? new ArticlePlacementResult(ArticlePlacementKind.AcknowledgementNotObserved)
                    : new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "timeout");
                Finish(operation, result);
                LogOutcome(operation, result);
                return result;
            }
            catch (OperationCanceledException) when (operation.IsCompleted)
            {
                LogOutcome(operation, operation.Result);
                return operation.Result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (operation.EndWritten)
                {
                    var ambiguous = new ArticlePlacementResult(ArticlePlacementKind.AcknowledgementNotObserved);
                    Finish(operation, ambiguous);
                    LogOutcome(operation, ambiguous);
                    return ambiguous;
                }

                await TryWriteCancelAsync(operation).ConfigureAwait(false);
                var cancelled = new ArticlePlacementResult(ArticlePlacementKind.Cancelled);
                Finish(operation, cancelled);
                LogOutcome(operation, cancelled);
                return cancelled;
            }
            catch (OperationCanceledException)
            {
                var result = _disposed != 0
                    ? operation.EndWritten
                        ? new ArticlePlacementResult(ArticlePlacementKind.AcknowledgementNotObserved)
                        : new ArticlePlacementResult(ArticlePlacementKind.Cancelled)
                    : operation.EndWritten
                        ? new ArticlePlacementResult(ArticlePlacementKind.AcknowledgementNotObserved)
                        : new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "closed");
                Finish(operation, result);
                LogOutcome(operation, result);
                return result;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                MarkDead();
                var result = operation.EndWritten
                    ? new ArticlePlacementResult(ArticlePlacementKind.AcknowledgementNotObserved)
                    : new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, ex.GetType().Name);
                Finish(operation, result);
                LogOutcome(operation, result);
                return result;
            }
        }

        private async Task ExchangeHelloAsync(CancellationToken cancellationToken)
        {
            await WriteFrameAsync(VatpFrameEncoder.EncodeHello(_maxFramePayload), cancellationToken).ConfigureAwait(false);
            var frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null
                || frame.Value.Header.Type != VatpFrameType.Hello
                || !VatpHello.TryDecode(frame.Value.Payload, out var hello, out _))
            {
                throw new IOException("hello");
            }

            _maxFramePayload = Math.Min(hello.MaxFramePayload, VatpProtocol.DefaultMaxFramePayload);
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (!IsDead && Volatile.Read(ref _disposed) == 0)
                {
                    var frame = await ReadFrameAsync(_lifetime.Token).ConfigureAwait(false);
                    if (frame is null)
                    {
                        MarkDead();
                        FaultOutstanding(shutdown: false);
                        return;
                    }

                    Dispatch(frame.Value);
                }
            }
            catch (OperationCanceledException)
            {
                FaultOutstanding(shutdown: Volatile.Read(ref _disposed) != 0);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or TimeoutException)
            {
                MarkDead();
                ArticlePlacementTransportLogMessages.ConnectionFailed(_logger, _fqdn, _port, ex.GetType().Name);
                FaultOutstanding(shutdown: false);
            }
        }

        private void Dispatch(VatpParsedFrame frame)
        {
            if (frame.Header.Type == VatpFrameType.Hello)
            {
                return;
            }

            if (frame.Header.StreamId == VatpProtocol.ConnectionStreamId)
            {
                MarkDead();
                FaultOutstanding(shutdown: false);
                return;
            }

            StoreOperation? operation;
            lock (_tableGate)
            {
                _operations.TryGetValue(frame.Header.StreamId, out operation);
            }

            if (operation is null)
            {
                return;
            }

            switch (frame.Header.Type)
            {
                case VatpFrameType.Window:
                    if (!VatpControlPayload.TryDecodeWindow(Copy(frame.Payload), out var add))
                    {
                        Finish(operation, new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "window"));
                        break;
                    }

                    var gainedInitialCredit = false;
                    lock (operation.Gate)
                    {
                        gainedInitialCredit = operation.AwaitingInitialCredit == 1 && add > 0;
                        _ = operation.Window.Add(add);
                    }

                    if (gainedInitialCredit && Interlocked.Exchange(ref operation.AwaitingInitialCredit, 0) == 1)
                    {
                        Interlocked.Decrement(ref _storesAwaitingInitialCredit);
                    }

                    try
                    {
                        operation.Credit.Release();
                    }
                    catch (ObjectDisposedException ex)
                    {
                        _ = ex; // observed; intentionally ignored at this boundary
                    }

                    break;
                case VatpFrameType.Result:
                    if (!VatpResultPayload.TryDecode(frame.Payload, out var outcome, out _))
                    {
                        Finish(operation, new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "result"));
                        break;
                    }

                    Finish(operation, ArticlePlacementResult.FromOutcomeByte(outcome));
                    break;
                case VatpFrameType.Fail:
                    if (!VatpControlPayload.TryDecodeFail(Copy(frame.Payload), out var error, out _))
                    {
                        Finish(operation, new ArticlePlacementResult(
                            operation.EndWritten
                                ? ArticlePlacementKind.AcknowledgementNotObserved
                                : ArticlePlacementKind.TransportFailure,
                            "fail"));
                        break;
                    }

                    Finish(operation, error == VatpErrorCode.Cancelled
                        ? new ArticlePlacementResult(ArticlePlacementKind.Cancelled)
                        : new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, error.ToString()));
                    break;
                default:
                    Finish(operation, new ArticlePlacementResult(
                        ArticlePlacementKind.TransportFailure,
                        frame.Header.Type.ToString()));
                    break;
            }
        }

        private void FaultOutstanding(bool shutdown)
        {
            StoreOperation[] operations;
            lock (_tableGate)
            {
                operations = _operations.Values.ToArray();
            }

            foreach (var operation in operations)
            {
                var result = operation.EndWritten
                    ? new ArticlePlacementResult(ArticlePlacementKind.AcknowledgementNotObserved)
                    : shutdown
                        ? new ArticlePlacementResult(ArticlePlacementKind.Cancelled)
                        : new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "closed");
                Finish(operation, result);
            }
        }

        private async Task TryWriteCancelAsync(StoreOperation operation)
        {
            if (IsDead)
            {
                return;
            }

            try
            {
                await WriteFrameAsync(VatpFrameEncoder.EncodeCancel(operation.StreamId), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or OperationCanceledException)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
        }

        private async Task WriteFrameAsync(VatpFrameEncoder.EncodedFrame frame, CancellationToken cancellationToken)
        {
            await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                try
                {
                    if (!frame.Header.IsEmpty)
                    {
                        await _ssl.WriteAsync(frame.Header, cancellationToken).ConfigureAwait(false);
                    }

                    if (!frame.Payload.IsEmpty)
                    {
                        await _ssl.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
                    }
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch
                {
                    MarkDead();
                    throw;
                }
            }
            finally
            {
                try
                {
                    _write.Release();
                }
                catch (ObjectDisposedException ex)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                }
            }
        }

        private void NoteResultWait(long ticks)
        {
            Interlocked.Increment(ref _resultCount);
            Interlocked.Add(ref _resultTicks, ticks);
            long previous;
            do
            {
                previous = Volatile.Read(ref _resultMaxTicks);
                if (ticks <= previous)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _resultMaxTicks, ticks, previous) != previous);
        }

        private async Task<VatpParsedFrame?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                if (_frames.TryTake(_maxFramePayload, out var frame))
                {
                    return frame;
                }

                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    lock (_readTimeoutGate)
                    {
                        _activeReadTimeout = timeout;
                        if (Volatile.Read(ref _storesAwaitingInitialCredit) == 0)
                        {
                            timeout.CancelAfter(_ioTimeout);
                        }
                    }

                    int read;
                    try
                    {
                        read = await _frames.ReadFromAsync(_ssl, timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException("placement-io");
                    }
                    finally
                    {
                        lock (_readTimeoutGate)
                        {
                            if (ReferenceEquals(_activeReadTimeout, timeout))
                            {
                                _activeReadTimeout = null;
                            }
                        }
                    }

                    if (read == 0)
                    {
                        return null;
                    }
                }
            }
        }

        /// <summary>Completes one STORE and drops its initial-credit wait from the read-timeout count.</summary>
        private void Finish(StoreOperation operation, ArticlePlacementResult result)
        {
            if (operation.TryFinish(result))
            {
                Interlocked.Decrement(ref _storesAwaitingInitialCredit);
            }
        }

        /// <summary>
        /// Disarms the outstanding read while at least one STORE is waiting for its first WINDOW.
        /// The next read arms the timeout again once that count returns to zero.
        /// </summary>
        private void DisarmReadTimeout()
        {
            lock (_readTimeoutGate)
            {
                if (Volatile.Read(ref _storesAwaitingInitialCredit) == 0)
                {
                    return;
                }

                var timeout = _activeReadTimeout;
                if (timeout is null)
                {
                    return;
                }

                try
                {
                    timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException ex)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                }
            }
        }

        private void LogOutcome(StoreOperation operation, ArticlePlacementResult result)
        {
            if (result.Kind == ArticlePlacementKind.AcknowledgementNotObserved)
            {
                ArticlePlacementTransportLogMessages.StoreAmbiguous(_logger, _fqdn, _port, operation.StreamId);
                return;
            }

            if (result.Succeeded)
            {
                ArticlePlacementTransportLogMessages.StoreCompleted(
                    _logger, _fqdn, _port, operation.StreamId, result.Kind.ToString());
                return;
            }

            ArticlePlacementTransportLogMessages.StoreRejected(
                _logger, _fqdn, _port, operation.StreamId, result.Kind.ToString(), result.Failure);
        }

        private void MarkDead() => Volatile.Write(ref _dead, 1);

        private static byte[] Copy(ReadOnlySequence<byte> payload)
        {
            var bytes = new byte[payload.Length];
            payload.CopyTo(bytes);
            return bytes;
        }

        private sealed class StoreOperation : IDisposable
        {
            private int _completed;

            public StoreOperation(uint streamId, ArticleId articleId, ArticleTransferLimits limits)
            {
                StreamId = streamId;
                ArticleId = articleId;
                Window = new ArticleTransferWindow(0, limits.MaxStreamCreditBytes);
                Credit = new SemaphoreSlim(0);
                Completion = new TaskCompletionSource<ArticlePlacementResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public uint StreamId { get; }

            public ArticleId ArticleId { get; }

            public object Gate { get; } = new();

            // codeql[cs/missed-readonly-modifier] Mutable struct; readonly would copy on Add/TryConsume and break window credit.
            public ArticleTransferWindow Window;

            public SemaphoreSlim Credit { get; }

            public TaskCompletionSource<ArticlePlacementResult> Completion { get; }

            public bool Started { get; set; }

            private volatile bool _endWritten;

            public bool EndWritten
            {
                get => _endWritten;
                set => _endWritten = value;
            }

            public bool IsCompleted => Volatile.Read(ref _completed) != 0;

            public ArticlePlacementResult Result { get; private set; }

            /// <summary>1 after META until the first WINDOW or a terminal result, then 0.</summary>
            public int AwaitingInitialCredit;

            /// <summary>
            /// Completes the operation once.
            /// Returns <see langword="true"/> when this call clears a pending initial-credit wait.
            /// </summary>
            public bool TryFinish(ArticlePlacementResult result)
            {
                var releasedCreditWait = Interlocked.Exchange(ref AwaitingInitialCredit, 0) == 1;
                if (Interlocked.Exchange(ref _completed, 1) != 0)
                {
                    return releasedCreditWait;
                }

                Result = result;
                Completion.TrySetResult(result);
                try
                {
                    Credit.Release();
                }
                catch (ObjectDisposedException ex)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                }

                return releasedCreditWait;
            }

            public void Dispose() => Credit.Dispose();
        }

        /// <summary>Accumulates socket bytes and yields one parsed frame at a time.</summary>
        private sealed class FrameBuffer
        {
            private byte[] _buffer = new byte[64 * 1024];
            private int _start;
            private int _end;

            public bool TryTake(uint maxFramePayload, out VatpParsedFrame frame)
            {
                frame = default;
                if (_end <= _start)
                {
                    return false;
                }

                var parsed = VatpFrameParser.ParseOneFrame(
                    new ReadOnlySequence<byte>(_buffer, _start, _end - _start),
                    maxFramePayload);
                if (parsed.Status == VatpFrameParseStatus.Invalid)
                {
                    throw new IOException(parsed.Error.ToString());
                }

                if (parsed.Status != VatpFrameParseStatus.Success || parsed.Frame is not { } parsedFrame)
                {
                    return false;
                }

                var payload = new byte[parsedFrame.Payload.Length];
                parsedFrame.Payload.CopyTo(payload);
                frame = new VatpParsedFrame(parsedFrame.Header, new ReadOnlySequence<byte>(payload));
                _start += checked((int)parsed.ConsumedBytes);
                if (_start == _end)
                {
                    _start = 0;
                    _end = 0;
                }

                return true;
            }

            public async ValueTask<int> ReadFromAsync(Stream stream, CancellationToken cancellationToken)
            {
                Compact();
                if (_end == _buffer.Length)
                {
                    Array.Resize(ref _buffer, _buffer.Length * 2);
                }

                var read = await stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
                _end += read;
                return read;
            }

            private void Compact()
            {
                if (_start == 0)
                {
                    return;
                }

                if (_start == _end)
                {
                    _start = 0;
                    _end = 0;
                    return;
                }

                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
        }
    }
}

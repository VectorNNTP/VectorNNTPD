using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.StorageService;

namespace VectorNNTPD.StorageService.Tests.Support.Placement
{
    /// <summary>
    /// One TLS NNTP connection to one placement target for pipelined <c>TAKETHIS</c>.
    /// At most <see cref="MaxOutstanding"/> outstanding operations.
    /// </summary>
    internal sealed class ArticlePlacementConnection : IAsyncDisposable
    {
        private static readonly byte[] Quit = "QUIT\r\n"u8.ToArray();
        private static readonly byte[] TakeThisPrefix = "TAKETHIS "u8.ToArray();
        private static readonly byte[] Crlf = "\r\n"u8.ToArray();
        private static readonly byte[] Terminator = ".\r\n"u8.ToArray();

        private readonly string _fqdn;
        private readonly int _port;
        private readonly TcpClient _tcp;
        private readonly SslStream _ssl;
        private readonly ILogger _logger;
        private readonly TimeSpan _ioTimeout;
        private readonly SemaphoreSlim _slots;
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly object _pendingGate = new();
        private readonly Queue<PendingTakeThis> _pending = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly byte[] _readBuffer = new byte[64 * 1024];
        private readonly List<byte> _lineBuffer = [];
        private Task? _reader;
        private int _dead;
        private int _disposed;
        private int _active;
        private int _peakActive;
        private long _slotWaitTicks;
        private long _responseWaitTicks;
        private long _responseCount;
        private long _responseMaxTicks;
        private long _articleBytesWritten;

        private ArticlePlacementConnection(
            string fqdn,
            int port,
            TcpClient tcp,
            SslStream ssl,
            ILogger logger,
            TimeSpan ioTimeout,
            int maxOutstanding)
        {
            _fqdn = fqdn;
            _port = port;
            _tcp = tcp;
            _ssl = ssl;
            _logger = logger;
            _ioTimeout = ioTimeout;
            MaxOutstanding = maxOutstanding;
            _slots = new SemaphoreSlim(maxOutstanding, maxOutstanding);
        }

        public int MaxOutstanding { get; }

        internal int PeakActive => Volatile.Read(ref _peakActive);

        internal long SlotWaitTicks => Volatile.Read(ref _slotWaitTicks);

        /// <summary>Ticks spent waiting for a TAKETHIS status after the article was written.</summary>
        internal long CreditWaitTicks => Volatile.Read(ref _responseWaitTicks);

        internal long ResultCount => Volatile.Read(ref _responseCount);

        internal long ResultTicks => Volatile.Read(ref _responseWaitTicks);

        internal long ResultMaxTicks => Volatile.Read(ref _responseMaxTicks);

        /// <summary>Article payload octets written (destuffed size, not including stuffing or framing).</summary>
        internal long DataFramesWritten => Volatile.Read(ref _articleBytesWritten);

        public bool IsDead => Volatile.Read(ref _dead) != 0;

        public int ActiveStores => Volatile.Read(ref _active);

        public static async Task<ArticlePlacementConnection> EstablishAsync(
            string fqdn,
            int port,
            string connectHost,
            ILogger logger,
            StorageFeedClientOptions options,
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
                ExpectedException.Ignore(ex);
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
            catch
            {
                tcp.Dispose();
                throw;
            }

            SslStream ssl;
            try
            {
                ssl = await StorageTlsClient.AuthenticateAsClientAsync(
                        tcp.GetStream(),
                        fqdn,
                        options.TlsHandshakeTimeout,
                        certificateValidation,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                tcp.Dispose();
                throw;
            }

            var connection = new ArticlePlacementConnection(
                fqdn,
                port,
                tcp,
                ssl,
                logger,
                options.IoTimeout,
                maxOutstanding);
            try
            {
                await connection.HandshakeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            ArticlePlacementTransportLogMessages.Established(logger, fqdn, port, maxOutstanding);
            return connection;
        }

        public async Task<ArticlePlacementResult> PlaceAsync(
            ArticleRecord record,
            CancellationToken cancellationToken)
        {
            if (IsDead)
            {
                return new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "closed");
            }

            if (!ArticlePlacementMessageId.TryCopy(in record, out var messageId))
            {
                return new ArticlePlacementResult(ArticlePlacementKind.RejectedInvalid, "message-id");
            }

            var slotStarted = Stopwatch.GetTimestamp();
            try
            {
                await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new ArticlePlacementResult(ArticlePlacementKind.Cancelled);
            }

            Interlocked.Add(ref _slotWaitTicks, Stopwatch.GetTimestamp() - slotStarted);
            var active = Interlocked.Increment(ref _active);
            UpdatePeak(active);

            var pending = new PendingTakeThis();
            var responseStarted = Stopwatch.GetTimestamp();
            var articleWritten = false;
            try
            {
                await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    lock (_pendingGate)
                    {
                        _pending.Enqueue(pending);
                    }

                    // ArtData is storage-stuffed; TAKETHIS needs the logical article with transfer stuffing only.
                    var logical = UnstuffCanonicalArticle(record.ArtData);
                    await WriteTakeThisAsync(messageId, logical, cancellationToken).ConfigureAwait(false);
                    articleWritten = true;
                    Interlocked.Add(ref _articleBytesWritten, record.ArtSize);
                }
                finally
                {
                    _write.Release();
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                linked.CancelAfter(_ioTimeout);
                var result = await pending.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                NoteResponse(Stopwatch.GetTimestamp() - responseStarted);
                if (result.Succeeded)
                {
                    ArticlePlacementTransportLogMessages.TakeThisCompleted(_logger, _fqdn, _port, result.Kind.ToString());
                }
                else
                {
                    ArticlePlacementTransportLogMessages.TakeThisRejected(
                        _logger, _fqdn, _port, result.Kind.ToString(), result.Failure);
                }

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                MarkDead();
                FailPending(pending, articleWritten
                    ? new ArticlePlacementResult(ArticlePlacementKind.AcknowledgementNotObserved)
                    : new ArticlePlacementResult(ArticlePlacementKind.Cancelled));
                return articleWritten
                    ? new ArticlePlacementResult(ArticlePlacementKind.AcknowledgementNotObserved)
                    : new ArticlePlacementResult(ArticlePlacementKind.Cancelled);
            }
            catch (OperationCanceledException)
            {
                MarkDead();
                var ambiguous = articleWritten
                    ? new ArticlePlacementResult(ArticlePlacementKind.AcknowledgementNotObserved)
                    : new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "timeout");
                FailPending(pending, ambiguous);
                if (articleWritten)
                {
                    ArticlePlacementTransportLogMessages.TakeThisAmbiguous(_logger, _fqdn, _port);
                }

                return ambiguous;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                MarkDead();
                var failure = articleWritten
                    ? new ArticlePlacementResult(ArticlePlacementKind.AcknowledgementNotObserved)
                    : new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, ex.GetType().Name);
                FailPending(pending, failure);
                return failure;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
                _slots.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            MarkDead();
            try
            {
                await _write.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await _ssl.WriteAsync(Quit, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
                {
                    ExpectedException.Ignore(ex);
                }
                finally
                {
                    _write.Release();
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException)
            {
                ExpectedException.Ignore(ex);
            }

            await _lifetime.CancelAsync().ConfigureAwait(false);
            if (_reader is not null)
            {
                try
                {
                    await _reader.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    ExpectedException.Ignore(ex);
                }
            }

            CompleteAllPending(new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "closed"));
            ArticlePlacementTransportLogMessages.Closed(_logger, _fqdn, _port, ActiveStores);
            await _ssl.DisposeAsync().ConfigureAwait(false);
            _tcp.Dispose();
            _slots.Dispose();
            _write.Dispose();
            _lifetime.Dispose();
        }

        private async Task HandshakeAsync(CancellationToken cancellationToken)
        {
            var greeting = await ReadStatusLineAsync(cancellationToken).ConfigureAwait(false);
            if (greeting.Code / 100 != 2)
            {
                throw new IOException("nntp-greeting");
            }

            _reader = Task.Run(() => ReadLoopAsync(_lifetime.Token), CancellationToken.None);
        }

        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && !IsDead)
                {
                    var status = await ReadStatusLineAsync(cancellationToken).ConfigureAwait(false);
                    PendingTakeThis? next;
                    lock (_pendingGate)
                    {
                        next = _pending.Count > 0 ? _pending.Dequeue() : null;
                    }

                    if (next is null)
                    {
                        MarkDead();
                        return;
                    }

                    next.Completion.TrySetResult(ArticlePlacementResult.FromNntpStatus(status.Code));
                    if (status.Code == 400)
                    {
                        MarkDead();
                        CompleteAllPending(new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "temporary-failure"));
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CompleteAllPending(new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "closed"));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or TimeoutException)
            {
                MarkDead();
                CompleteAllPending(new ArticlePlacementResult(ArticlePlacementKind.AcknowledgementNotObserved));
            }
        }

        private async Task WriteTakeThisAsync(
            byte[] messageId,
            ReadOnlyMemory<byte> article,
            CancellationToken cancellationToken)
        {
            var commandLength = TakeThisPrefix.Length + messageId.Length + Crlf.Length;
            var command = new byte[commandLength];
            TakeThisPrefix.CopyTo(command);
            messageId.CopyTo(command.AsSpan(TakeThisPrefix.Length));
            Crlf.CopyTo(command.AsSpan(TakeThisPrefix.Length + messageId.Length));
            await _ssl.WriteAsync(command, cancellationToken).ConfigureAwait(false);
            await WriteStuffedArticleAsync(article, cancellationToken).ConfigureAwait(false);
            await _ssl.WriteAsync(Terminator, cancellationToken).ConfigureAwait(false);
        }

        private async Task WriteStuffedArticleAsync(ReadOnlyMemory<byte> articleMemory, CancellationToken cancellationToken)
        {
            // Stuff into a contiguous buffer and write in large SSL records. Per-line WriteAsync on
            // SslStream while a concurrent read loop runs can stall for large articles (IoProgress timeout → 400).
            var stuffed = StuffArticleForTransfer(articleMemory.Span);
            const int writeChunk = 64 * 1024;
            for (var offset = 0; offset < stuffed.Length; offset += writeChunk)
            {
                var length = Math.Min(writeChunk, stuffed.Length - offset);
                await _ssl.WriteAsync(stuffed.AsMemory(offset, length), cancellationToken).ConfigureAwait(false);
            }
        }

        private static byte[] StuffArticleForTransfer(ReadOnlySpan<byte> article)
        {
            var stuffedLength = article.Length;
            var atLineStart = true;
            for (var i = 0; i < article.Length; i++)
            {
                if (atLineStart && article[i] == (byte)'.')
                {
                    stuffedLength++;
                }

                atLineStart = article[i] == (byte)'\n';
            }

            var stuffed = new byte[stuffedLength];
            var w = 0;
            atLineStart = true;
            for (var i = 0; i < article.Length; i++)
            {
                if (atLineStart && article[i] == (byte)'.')
                {
                    stuffed[w++] = (byte)'.';
                }

                stuffed[w++] = article[i];
                atLineStart = article[i] == (byte)'\n';
            }

            return stuffed;
        }

        /// <summary>
        /// Drops one leading body stuffing dot from canonical ArtData so TAKETHIS can apply transfer stuffing once.
        /// </summary>
        private static byte[] UnstuffCanonicalArticle(ReadOnlyMemory<byte> artData)
        {
            var span = artData.Span;
            var separator = span.IndexOf("\r\n\r\n"u8);
            if (separator < 0)
            {
                return artData.ToArray();
            }

            var bodyStart = separator + 4;
            var header = span[..bodyStart];
            var body = span[bodyStart..];
            var unstuffedBodyLength = body.Length;
            var atLineStart = true;
            for (var i = 0; i < body.Length; i++)
            {
                if (atLineStart && body[i] == (byte)'.' && i + 1 < body.Length)
                {
                    unstuffedBodyLength--;
                    atLineStart = false;
                    continue;
                }

                atLineStart = body[i] == (byte)'\n';
            }

            var result = new byte[header.Length + unstuffedBodyLength];
            header.CopyTo(result);
            var w = header.Length;
            atLineStart = true;
            for (var i = 0; i < body.Length; i++)
            {
                var b = body[i];
                if (atLineStart && b == (byte)'.' && i + 1 < body.Length)
                {
                    // Drop one storage stuffing dot; transfer stuffing is applied by WriteStuffedArticleAsync.
                    atLineStart = false;
                    continue;
                }

                result[w++] = b;
                atLineStart = b == (byte)'\n';
            }

            return result;
        }

        private async Task<(int Code, string Line)> ReadStatusLineAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_ioTimeout);
            while (true)
            {
                for (var i = 0; i < _lineBuffer.Count - 1; i++)
                {
                    if (_lineBuffer[i] == (byte)'\r' && _lineBuffer[i + 1] == (byte)'\n')
                    {
                        var lineBytes = _lineBuffer.GetRange(0, i).ToArray();
                        _lineBuffer.RemoveRange(0, i + 2);
                        return ParseStatus(lineBytes);
                    }
                }

                var read = await _ssl.ReadAsync(_readBuffer, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("nntp-closed");
                }

                for (var i = 0; i < read; i++)
                {
                    _lineBuffer.Add(_readBuffer[i]);
                }
            }
        }

        private static (int Code, string Line) ParseStatus(byte[] lineBytes)
        {
            var line = Encoding.ASCII.GetString(lineBytes);
            if (line.Length < 3
                || !char.IsDigit(line[0])
                || !char.IsDigit(line[1])
                || !char.IsDigit(line[2]))
            {
                throw new IOException("nntp-status");
            }

            var code = ((line[0] - '0') * 100) + ((line[1] - '0') * 10) + (line[2] - '0');
            return (code, line);
        }

        private void FailPending(PendingTakeThis pending, ArticlePlacementResult result)
        {
            // Complete without dequeuing: orphaned slots stay until reader or dispose clears under _pendingGate.
            pending.Completion.TrySetResult(result);
        }

        private void CompleteAllPending(ArticlePlacementResult result)
        {
            lock (_pendingGate)
            {
                while (_pending.Count > 0)
                {
                    _pending.Dequeue().Completion.TrySetResult(result);
                }
            }
        }

        private void MarkDead() => Interlocked.Exchange(ref _dead, 1);

        private void UpdatePeak(int active)
        {
            while (true)
            {
                var peak = Volatile.Read(ref _peakActive);
                if (active <= peak || Interlocked.CompareExchange(ref _peakActive, active, peak) == peak)
                {
                    return;
                }
            }
        }

        private void NoteResponse(long ticks)
        {
            Interlocked.Increment(ref _responseCount);
            Interlocked.Add(ref _responseWaitTicks, ticks);
            while (true)
            {
                var max = Volatile.Read(ref _responseMaxTicks);
                if (ticks <= max || Interlocked.CompareExchange(ref _responseMaxTicks, ticks, max) == max)
                {
                    return;
                }
            }
        }

        private sealed class PendingTakeThis
        {
            public TaskCompletionSource<ArticlePlacementResult> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}

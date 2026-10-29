using System.Buffers;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Transport.ArticleTransfer;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Transport.Vatp;

namespace VectorNNTPD.ReaderService.Storage
{
    using VectorNNTPD.Common.Core;
    /// <summary>
    /// Long-lived STORE client. One TLS connection per placement target. Does not use <see cref="VatpConnectionPool"/>.
    /// </summary>
    /// <remarks>
    /// Connections stay open while they are healthy. Each connection has one frame writer and at most
    /// <see cref="MaxOutstandingStores"/> outstanding STORE streams. A lost RESULT after END is
    /// <see cref="ArticlePlacementKind.AcknowledgementNotObserved"/> and is not retried. Placement itself
    /// stays disabled in the spool writer.
    /// </remarks>
    public sealed class ArticlePlacementClient : IArticlePlacementClient, IAsyncDisposable
    {
        private static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(5);

        private readonly ILogger<ArticlePlacementClient> _logger;
        private readonly VatpClientOptions _options;
        private readonly ArticleTransferLimits _limits;
        private readonly TimeProvider _time;
        private readonly object _gate = new();
        private readonly Dictionary<string, ArticlePlacementConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task<ArticlePlacementConnection>> _dialing = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTimeOffset> _nextDial = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TimeSpan> _backoff = new(StringComparer.OrdinalIgnoreCase);
        private int _disposed;
        private int _connects;
        private int _dials;

        /// <summary>Optional certificate callback for tests only.</summary>
        internal RemoteCertificateValidationCallback? ServerCertificateValidationCallback { get; set; }

        /// <summary>Optional TCP connect host override for tests. TLS TargetHost remains the advertised FQDN.</summary>
        internal string? TestTcpConnectHost { get; set; }

        /// <summary>Gets how many TCP connections this client has established.</summary>
        internal int ConnectCount => Volatile.Read(ref _connects);

        /// <summary>Gets how many dial attempts have started, including failures.</summary>
        internal int DialAttempts => Volatile.Read(ref _dials);

        /// <summary>Gets the outstanding STORE limit applied to each connection.</summary>
        internal int MaxOutstandingStores { get; }

        /// <summary>Sums wait and RESULT counters across connections still owned by this client.</summary>
        internal PlacementTransportCounters Snapshot()
        {
            lock (_gate)
            {
                var counters = default(PlacementTransportCounters);
                foreach (var connection in _connections.Values)
                {
                    counters = counters.Add(connection);
                }

                return counters;
            }
        }

        /// <summary>Initializes a placement client with two outstanding STOREs and the production VATP limits.</summary>
        public ArticlePlacementClient(ILogger<ArticlePlacementClient> logger)
            : this(logger, ArticleIngestionOptions.DefaultMaxOutstandingStoresPerConnection)
        {
        }

        /// <summary>Initializes a placement client with an explicit outstanding-STORE limit.</summary>
        public ArticlePlacementClient(ILogger<ArticlePlacementClient> logger, int maxOutstandingStores)
            : this(logger, new VatpClientOptions(), ArticleTransferLimits.Default, maxOutstandingStores, TimeProvider.System)
        {
        }

        internal ArticlePlacementClient(
            ILogger<ArticlePlacementClient> logger,
            VatpClientOptions options,
            ArticleTransferLimits limits,
            int maxOutstandingStores = ArticleIngestionOptions.DefaultMaxOutstandingStoresPerConnection,
            TimeProvider? time = null)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(limits);
            if (maxOutstandingStores is not (1 or 2))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxOutstandingStores),
                    maxOutstandingStores,
                    "Outstanding STORE operations per connection must be 1 or 2.");
            }

            _logger = logger;
            _options = options;
            _limits = limits;
            _time = time ?? TimeProvider.System;
            MaxOutstandingStores = maxOutstandingStores;
        }

        /// <inheritdoc />
        public async ValueTask<ArticlePlacementResult> PlaceAsync(
            ArticleRecord record,
            StorageServerFleetEntry target,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "shutdown");
            }

            if (target.VatpPort is not int port || port is < 1 or > 65535)
            {
                return new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "vatp-port");
            }

            if (!ArticlePlacementMeta.TryFromRecord(in record, out var meta))
            {
                return new ArticlePlacementResult(ArticlePlacementKind.RejectedInvalid, "meta");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var leased = await LeaseAsync(target.Fqdn, port, cancellationToken).ConfigureAwait(false);
                if (leased.Failure is { } failure)
                {
                    return failure;
                }

                var connection = leased.Connection
                    ?? throw new InvalidOperationException("Placement lease returned neither a connection nor a failure.");
                var result = await connection.PlaceAsync(record, meta, cancellationToken).ConfigureAwait(false);
                if (connection.IsDead)
                {
                    Forget(target.Fqdn, port, connection);
                    try
                    {
                        await connection.DisposeAsync().ConfigureAwait(false);
                    }
                    // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException)
                    {
                        _ = ex; // observed; intentionally ignored at this boundary
                    }
                }

                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "timeout");
            }
            catch (OperationCanceledException)
            {
                return new ArticlePlacementResult(ArticlePlacementKind.Cancelled);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                return new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, DescribeTransportFailure(ex));
            }
        }

        /// <summary>Closes every placement connection and rejects later calls.</summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            ArticlePlacementConnection[] connections;
            lock (_gate)
            {
                connections = _connections.Values.ToArray();
                _connections.Clear();
            }

            var outstanding = 0;
            foreach (var connection in connections)
            {
                outstanding += connection.ActiveStores;
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            ArticlePlacementTransportLogMessages.Shutdown(_logger, outstanding);
        }

        private async Task<(ArticlePlacementConnection? Connection, ArticlePlacementResult? Failure)> LeaseAsync(
            string fqdn,
            int port,
            CancellationToken cancellationToken)
        {
            var key = fqdn + ":" + port.ToString(CultureInfo.InvariantCulture);
            Task<ArticlePlacementConnection>? pending = null;
            var owner = false;
            ArticlePlacementConnection? dead = null;
            ArticlePlacementResult? deferred = null;
            lock (_gate)
            {
                if (_disposed != 0)
                {
                    return (null, new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "shutdown"));
                }

                if (_connections.TryGetValue(key, out var live) && !live.IsDead)
                {
                    ArticlePlacementTransportLogMessages.Reused(_logger, fqdn, port, live.ActiveStores);
                    return (live, null);
                }

                if (_connections.TryGetValue(key, out dead))
                {
                    _connections.Remove(key);
                }

                if (_dialing.TryGetValue(key, out pending))
                {
                    // Dial already in flight; await the captured pending task below.
                }
                else if (_nextDial.TryGetValue(key, out var next) && _time.GetUtcNow() < next)
                {
                    var wait = next - _time.GetUtcNow();
                    ArticlePlacementTransportLogMessages.ReconnectDeferred(
                        _logger, fqdn, port, (long)wait.TotalMilliseconds, "backoff");
                    deferred = new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "backoff");
                }
                else
                {
                    pending = DialAsync(fqdn, port, key);
                    _dialing[key] = pending;
                    owner = true;
                }
            }

            if (dead is not null)
            {
                try
                {
                    await dead.DisposeAsync().ConfigureAwait(false);
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                }
            }

            if (deferred is not null)
            {
                return (null, deferred);
            }

            try
            {
                var connection = await pending!.WaitAsync(cancellationToken).ConfigureAwait(false);
                return (connection, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return (null, new ArticlePlacementResult(ArticlePlacementKind.Cancelled));
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or OperationCanceledException)
            {
                if (owner)
                {
                    NoteFailure(key);
                }

                var failure = DescribeTransportFailure(ex);
                ArticlePlacementTransportLogMessages.ConnectionFailed(_logger, fqdn, port, failure);
                return (null, new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, failure));
            }
            finally
            {
                if (owner)
                {
                    _ = ReleaseDialAsync(key, pending!);
                }
            }
        }

        private async Task ReleaseDialAsync(string key, Task<ArticlePlacementConnection> pending)
        {
            try
            {
                await pending.ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
            {
                _ = ignored; // observed; intentionally ignored at this best-effort boundary
            }

            lock (_gate)
            {
                if (_dialing.TryGetValue(key, out var current) && ReferenceEquals(current, pending))
                {
                    _dialing.Remove(key);
                }
            }
        }

        private async Task<ArticlePlacementConnection> DialAsync(string fqdn, int port, string key)
        {
            Interlocked.Increment(ref _dials);
            var connection = await ArticlePlacementConnection.EstablishAsync(
                    fqdn,
                    port,
                    TestTcpConnectHost ?? fqdn,
                    _logger,
                    _options,
                    _limits,
                    MaxOutstandingStores,
                    ServerCertificateValidationCallback,
                    CancellationToken.None)
                .ConfigureAwait(false);
            Interlocked.Increment(ref _connects);
            lock (_gate)
            {
                _backoff[key] = InitialBackoff;
                _nextDial.Remove(key);
                if (_disposed != 0)
                {
                    _ = connection.DisposeAsync();
                    throw new IOException("shutdown");
                }

                _connections[key] = connection;
            }

            return connection;
        }

        private void NoteFailure(string key)
        {
            lock (_gate)
            {
                var current = _backoff.TryGetValue(key, out var backoff) ? backoff : InitialBackoff;
                _nextDial[key] = _time.GetUtcNow().Add(current);
                var doubled = TimeSpan.FromTicks(Math.Min(current.Ticks * 2, MaximumBackoff.Ticks));
                _backoff[key] = doubled;
            }
        }

        private void Forget(string fqdn, int port, ArticlePlacementConnection connection)
        {
            var key = fqdn + ":" + port.ToString(CultureInfo.InvariantCulture);
            lock (_gate)
            {
                if (_connections.TryGetValue(key, out var current) && ReferenceEquals(current, connection))
                {
                    _connections.Remove(key);
                }
            }
        }

        private static string DescribeTransportFailure(Exception ex)
        {
            if (ex is TimeoutException)
            {
                return "timeout";
            }

            if (ex is SocketException socket)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"SocketException: SocketErrorCode={socket.SocketErrorCode} HResult={socket.HResult} Message={socket.Message}");
            }

            return ex.GetType().Name;
        }
    }

    /// <summary>Wait and RESULT totals for connections still held by one placement client.</summary>
    internal readonly record struct PlacementTransportCounters(
        long SlotWaitTicks,
        long CreditWaitTicks,
        long ResultCount,
        long ResultTicks,
        long ResultMaxTicks,
        int PeakActive,
        long DataFramesWritten)
    {
        /// <summary>Adds one connection's counters.</summary>
        internal PlacementTransportCounters Add(ArticlePlacementConnection connection) =>
            new(
                SlotWaitTicks + connection.SlotWaitTicks,
                CreditWaitTicks + connection.CreditWaitTicks,
                ResultCount + connection.ResultCount,
                ResultTicks + connection.ResultTicks,
                Math.Max(ResultMaxTicks, connection.ResultMaxTicks),
                Math.Max(PeakActive, connection.PeakActive),
                DataFramesWritten + connection.DataFramesWritten);
    }

    /// <summary>STORE/META/DATA/END sender over an already connected stream.</summary>
    internal static class VatpPlacementTransfer
    {
        public static async Task<ArticlePlacementResult> PlaceAsync(
            Stream stream,
            ArticleRecord record,
            ArticleCanonicalTransferMeta meta,
            ArticleTransferLimits limits,
            TimeSpan ioTimeout,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(stream);
            var reader = new FrameReader(stream, ioTimeout);
            var endWritten = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var maxFrame = limits.DefaultMaxFramePayload;
                await WriteAsync(stream, VatpFrameEncoder.EncodeHello(maxFrame), cancellationToken).ConfigureAwait(false);
                var hello = await reader.ReadAsync(maxFrame, cancellationToken).ConfigureAwait(false);
                if (hello is null)
                {
                    return new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "hello-closed");
                }

                if (hello.Value.Header.Type != VatpFrameType.Hello
                    || !VatpHello.TryDecode(hello.Value.Payload, out var decodedHello, out _))
                {
                    return new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "hello");
                }

                maxFrame = Math.Min(decodedHello.MaxFramePayload, VatpProtocol.DefaultMaxFramePayload);
                const uint streamId = 1;
                var id = new byte[ArticleId.Length];
                record.ArtId.CopyTo(id);
                await WriteAsync(stream, VatpFrameEncoder.EncodeStore(streamId, id), cancellationToken).ConfigureAwait(false);
                var metaBytes = VatpMetaCodec.Encode(in meta);
                await WriteAsync(stream, VatpFrameEncoder.EncodeMeta(streamId, metaBytes), cancellationToken).ConfigureAwait(false);

                var window = new ArticleTransferWindow(0, limits.MaxStreamCreditBytes);
                var offset = 0;
                var artData = record.ArtData;
                while (offset < record.ArtSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var length = ArticleTransferReadyRing.ComputeDataPayloadLength(
                        record.ArtSize - offset,
                        window.Credit,
                        maxFrame);
                    if (length <= 0)
                    {
                        var paced = await ReadControlAsync(reader, maxFrame, endWritten: false, cancellationToken)
                            .ConfigureAwait(false);
                        if (paced.Terminal is { } terminal)
                        {
                            return terminal;
                        }

                        if (paced.AddedCredit > 0)
                        {
                            _ = window.Add(paced.AddedCredit);
                        }

                        continue;
                    }

                    if (!window.TryConsume(length))
                    {
                        return new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "window");
                    }

                    var fin = offset + length == record.ArtSize;
                    await WriteAsync(
                            stream,
                            VatpFrameEncoder.EncodeData(streamId, artData.Slice(offset, length), fin),
                            cancellationToken)
                        .ConfigureAwait(false);
                    offset += length;
                }

                await WriteAsync(stream, VatpFrameEncoder.EncodeEnd(streamId), cancellationToken).ConfigureAwait(false);
                endWritten = true;
                while (true)
                {
                    var final = await ReadControlAsync(reader, maxFrame, endWritten: true, cancellationToken)
                        .ConfigureAwait(false);
                    if (final.Terminal is { } terminal)
                    {
                        return terminal;
                    }

                    if (final.AddedCredit > 0)
                    {
                        _ = window.Add(final.AddedCredit);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!endWritten)
                {
                    try
                    {
                        await WriteAsync(stream, VatpFrameEncoder.EncodeCancel(1), CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                    {
                        _ = ex; // observed; intentionally ignored at this boundary
                    }

                    return new ArticlePlacementResult(ArticlePlacementKind.Cancelled);
                }

                return new ArticlePlacementResult(ArticlePlacementKind.AcknowledgementNotObserved);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
            {
                return endWritten
                    ? new ArticlePlacementResult(ArticlePlacementKind.AcknowledgementNotObserved)
                    : new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, ex.GetType().Name);
            }
        }

        private readonly record struct ControlRead(ArticlePlacementResult? Terminal, uint AddedCredit);

        private static async Task<ControlRead> ReadControlAsync(
            FrameReader reader,
            uint maxFrame,
            bool endWritten,
            CancellationToken cancellationToken)
        {
            var frame = await reader.ReadAsync(maxFrame, cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                return new ControlRead(
                    endWritten
                        ? new ArticlePlacementResult(ArticlePlacementKind.AcknowledgementNotObserved)
                        : new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "closed"),
                    0);
            }

            switch (frame.Value.Header.Type)
            {
                case VatpFrameType.Window:
                    return VatpControlPayload.TryDecodeWindow(Copy(frame.Value.Payload), out var add)
                        ? new ControlRead(null, add)
                        : new ControlRead(new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "window"), 0);
                case VatpFrameType.Result:
                    if (!VatpResultPayload.TryDecode(frame.Value.Payload, out var outcome, out _))
                    {
                        return new ControlRead(new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "result"), 0);
                    }

                    return new ControlRead(ArticlePlacementResult.FromOutcomeByte(outcome), 0);
                case VatpFrameType.Fail:
                    if (!VatpControlPayload.TryDecodeFail(Copy(frame.Value.Payload), out var error, out _))
                    {
                        return new ControlRead(
                            new ArticlePlacementResult(
                                endWritten
                                    ? ArticlePlacementKind.AcknowledgementNotObserved
                                    : ArticlePlacementKind.TransportFailure,
                                "fail"),
                            0);
                    }

                    if (error == VatpErrorCode.Cancelled)
                    {
                        return new ControlRead(new ArticlePlacementResult(ArticlePlacementKind.Cancelled), 0);
                    }

                    return new ControlRead(new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, error.ToString()), 0);
                default:
                    return new ControlRead(
                        new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, frame.Value.Header.Type.ToString()),
                        0);
            }
        }

        private static byte[] Copy(ReadOnlySequence<byte> payload)
        {
            var bytes = new byte[payload.Length];
            payload.CopyTo(bytes);
            return bytes;
        }

        private static async Task WriteAsync(
            Stream stream,
            VatpFrameEncoder.EncodedFrame frame,
            CancellationToken cancellationToken)
        {
            if (!frame.Header.IsEmpty)
            {
                await stream.WriteAsync(frame.Header, cancellationToken).ConfigureAwait(false);
            }

            if (!frame.Payload.IsEmpty)
            {
                await stream.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
            }
        }

        private sealed class FrameReader
        {
            private readonly Stream _stream;
            private readonly TimeSpan _ioTimeout;
            private readonly byte[] _buffer = new byte[64 * 1024];
            private readonly List<byte> _pending = [];

            public FrameReader(Stream stream, TimeSpan ioTimeout)
            {
                _stream = stream;
                _ioTimeout = ioTimeout;
            }

            public async Task<VatpParsedFrame?> ReadAsync(uint maxFramePayload, CancellationToken cancellationToken)
            {
                while (true)
                {
                    var parsed = VatpFrameParser.ParseOneFrame(new ReadOnlySequence<byte>(_pending.ToArray()), maxFramePayload);
                    if (parsed.Status == VatpFrameParseStatus.Invalid)
                    {
                        throw new IOException(parsed.Error.ToString());
                    }

                    if (parsed.Status == VatpFrameParseStatus.Success && parsed.Frame is { } frame)
                    {
                        _pending.RemoveRange(0, checked((int)parsed.ConsumedBytes));
                        return CopyFrame(frame);
                    }

                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(_ioTimeout);
                    int read;
                    try
                    {
                        read = await _stream.ReadAsync(_buffer, timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException("placement-io");
                    }

                    if (read == 0)
                    {
                        return null;
                    }

                    for (var i = 0; i < read; i++)
                    {
                        _pending.Add(_buffer[i]);
                    }
                }
            }

            private static VatpParsedFrame CopyFrame(VatpParsedFrame frame)
            {
                var payload = new byte[frame.Payload.Length];
                frame.Payload.CopyTo(payload);
                return new VatpParsedFrame(frame.Header, new ReadOnlySequence<byte>(payload));
            }
        }
    }
}

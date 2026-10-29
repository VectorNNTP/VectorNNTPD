using System.Globalization;
using System.Net.Sockets;
using System.Text;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.ReaderService.Configuration;

namespace VectorNNTPD.ReaderService.PostFilter
{
    /// <summary>
    /// Persistent SPAMD CHECK client. Builds a disposable scan message and never
    /// mutates <see cref="ArticleRecord"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SPAMD PROTOCOL has no request identifiers and says each side shuts down after
    /// writing. This client therefore does not pipeline or multiplex CHECKs on one
    /// socket. Each pooled connection serializes requests. Concurrent CHECKs use
    /// distinct connections up to the snapshot pool size.
    /// </para>
    /// <para>
    /// After a complete CHECK response the socket is returned to the pool when it is
    /// still connected and has no leftover bytes. A broken connection is evicted
    /// before another request can use it. Hosts, port, pool size, and timeouts come
    /// from the PostFilter snapshot captured for this POST.
    /// </para>
    /// </remarks>
    internal sealed class SpamdCheckClient : IPostFilterSpamAssassin, IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<(string Host, int Port), Queue<SpamdConnection>> _idle = new();
        private readonly List<TaskCompletionSource> _waiters = [];
        private readonly ILogger<SpamdCheckClient>? _logger;
        private int _live;
        private int _roundRobin;
        private bool _disposed;

        /// <summary>Initializes the client with process-wide transport counters.</summary>
        public SpamdCheckClient()
            : this(new SpamdTransportMetrics(), logger: null)
        {
        }

        /// <summary>Initializes the client with injected counters and optional shutdown logging.</summary>
        public SpamdCheckClient(SpamdTransportMetrics metrics, ILogger<SpamdCheckClient>? logger = null)
        {
            ArgumentNullException.ThrowIfNull(metrics);
            Metrics = metrics;
            _logger = logger;
        }

        /// <summary>Gets process-wide transport counters.</summary>
        public SpamdTransportMetrics Metrics { get; }

        /// <inheritdoc />
        public async ValueTask<PostFilterSpamAssassinResult> CheckAsync(
            ArticleRecord article,
            string? accountName,
            PostFilterSpamAssassinTarget target,
            SpamdScanContext scanContext,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed)
            {
                return PostFilterSpamAssassinResult.Failed("disposed");
            }

            if (target.Hosts.Count == 0)
            {
                return PostFilterSpamAssassinResult.Failed("no-hosts");
            }

            byte[] scan;
            try
            {
                scan = SpamdScanArticleBuilder.Build(article, scanContext);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                return PostFilterSpamAssassinResult.Failed(ex.GetType().Name);
            }

            var start = SelectStartIndex(target);
            Exception? lastConnectFailure = null;
            for (var attempt = 0; attempt < target.Hosts.Count; attempt++)
            {
                var host = target.Hosts[(start + attempt) % target.Hosts.Count];
                if (string.IsNullOrWhiteSpace(host))
                {
                    lastConnectFailure = new InvalidOperationException("empty-host");
                    continue;
                }

                host = host.Trim();
                SpamdConnection? leased = null;
                var reused = false;
                try
                {
                    (leased, reused) = await RentAsync(host, target, cancellationToken).ConfigureAwait(false);
                    var started = TimeProvider.System.GetTimestamp();
                    try
                    {
                        var result = await CheckOnConnectionAsync(
                                leased,
                                scan,
                                accountName,
                                target,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (reused && IsStaleIdleFailure(result))
                        {
                            throw new IOException(result.Detail);
                        }

                        Metrics.RecordCheck(TimeProvider.System.GetElapsedTime(started));
                        Return(leased, reusable: result.Status != PostFilterSpamAssassinStatus.Failed);
                        return result;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        Evict(leased);
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        Evict(leased);
                        Metrics.RecordCheck(TimeProvider.System.GetElapsedTime(started));
                        return PostFilterSpamAssassinResult.Failed("timeout");
                    }
                    // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                    catch (Exception ex) when (reused && IsConnectionFailure(ex))
                    {
                        Evict(leased);
                        (leased, reused) = await RentAsync(host, target, cancellationToken).ConfigureAwait(false);
                        started = TimeProvider.System.GetTimestamp();
                        try
                        {
                            var retry = await CheckOnConnectionAsync(
                                    leased,
                                    scan,
                                    accountName,
                                    target,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            Metrics.RecordCheck(TimeProvider.System.GetElapsedTime(started));
                            Return(leased, reusable: retry.Status != PostFilterSpamAssassinStatus.Failed);
                            return retry;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            Evict(leased);
                            throw;
                        }
                        catch (OperationCanceledException)
                        {
                            Evict(leased);
                            Metrics.RecordCheck(TimeProvider.System.GetElapsedTime(started));
                            return PostFilterSpamAssassinResult.Failed("timeout");
                        }
                        // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                        catch (Exception retryEx)
                        {
                            Evict(leased);
                            Metrics.RecordCheck(TimeProvider.System.GetElapsedTime(started));
                            return ClassifyAfterWrite(retryEx);
                        }
                    }
                    // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                    catch (Exception ex)
                    {
                        Evict(leased);
                        Metrics.RecordCheck(TimeProvider.System.GetElapsedTime(started));
                        return ClassifyAfterWrite(ex);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (leased is null)
                {
                    lastConnectFailure = new TimeoutException("connect-timeout");
                    if (attempt + 1 < target.Hosts.Count)
                    {
                        continue;
                    }

                    return PostFilterSpamAssassinResult.Failed("timeout");
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex) when (leased is null && IsConnectionFailure(ex) && attempt + 1 < target.Hosts.Count)
                {
                    lastConnectFailure = ex;
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex) when (leased is null)
                {
                    return ClassifyConnect(ex);
                }
                finally
                {
                    _ = reused;
                }
            }

            return PostFilterSpamAssassinResult.Failed(lastConnectFailure?.GetType().Name ?? "no-hosts");
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            List<SpamdConnection> idle;
            List<TaskCompletionSource> waiters;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                idle = [];
                foreach (var queue in _idle.Values)
                {
                    while (queue.Count > 0)
                    {
                        idle.Add(queue.Dequeue());
                    }
                }

                _idle.Clear();
                _live = 0;
                waiters = [.. _waiters];
                _waiters.Clear();
            }

            foreach (var waiter in waiters)
            {
                waiter.TrySetCanceled();
            }

            foreach (var connection in idle)
            {
                connection.Dispose();
            }

            if (_logger is not null)
            {
                PostFilterLogMessages.SpamAssassinTransportStopped(
                    _logger,
                    Metrics.ConnectionsEstablished,
                    Metrics.CheckRequests,
                    Metrics.ConnectionReuses,
                    Metrics.Reconnects,
                    Metrics.Evictions);
            }

            await ValueTask.CompletedTask.ConfigureAwait(false);
        }

        private int SelectStartIndex(in PostFilterSpamAssassinTarget target)
        {
            if (target.Hosts.Count == 0)
            {
                return 0;
            }

            return SpamdHostSelector.NextStart(target.HostSelection, target.Hosts.Count, ref _roundRobin);
        }

        private async Task<(SpamdConnection Connection, bool Reused)> RentAsync(
            string host,
            PostFilterSpamAssassinTarget target,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task wait;
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (TryDequeueIdle(host, target.Port, out var idle))
                    {
                        Metrics.RecordReuse();
                        return (idle, true);
                    }

                    if (_live < target.MaxConnections)
                    {
                        _live++;
                        wait = Task.CompletedTask;
                    }
                    else
                    {
                        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        _waiters.Add(tcs);
                        wait = tcs.Task;
                    }
                }

                if (wait.IsCompleted)
                {
                    try
                    {
                        var connection = await ConnectAsync(host, target, cancellationToken).ConfigureAwait(false);
                        return (connection, false);
                    }
                    // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                    catch
                    {
                        lock (_gate)
                        {
                            _live--;
                            PulseWaiter();
                        }

                        throw;
                    }
                }

                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private bool TryDequeueIdle(string host, int port, out SpamdConnection connection)
        {
            var key = (host, port);
            if (_idle.TryGetValue(key, out var queue))
            {
                while (queue.Count > 0)
                {
                    var candidate = queue.Dequeue();
                    if (candidate.IsReusable)
                    {
                        connection = candidate;
                        return true;
                    }

                    // codeql[cs/dispose-not-called-on-throw] Candidate is pool-owned from Dequeue; Dispose here is best-effort eviction. Dequeue cannot create a new undisposed resource, and ConnectAsync already disposes TcpClient on connect failure.
                    candidate.Dispose();
                    _live--;
                    Metrics.RecordEviction();
                    Metrics.RecordReconnect();
                }
            }

            connection = null!;
            return false;
        }

        private async Task<SpamdConnection> ConnectAsync(
            string host,
            PostFilterSpamAssassinTarget target,
            CancellationToken cancellationToken)
        {
            var client = new TcpClient();
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(target.ConnectTimeout);
                await client.ConnectAsync(host, target.Port, connectCts.Token).ConfigureAwait(false);
                Metrics.RecordConnect();
                return new SpamdConnection(client, host, target.Port);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private void Return(SpamdConnection connection, bool reusable)
        {
            lock (_gate)
            {
                if (_disposed || !reusable || !connection.IsReusable)
                {
                    connection.Dispose();
                    if (!_disposed)
                    {
                        _live--;
                        Metrics.RecordEviction();
                        PulseWaiter();
                    }

                    return;
                }

                var key = (connection.Host, connection.Port);
                if (!_idle.TryGetValue(key, out var queue))
                {
                    queue = new Queue<SpamdConnection>();
                    _idle[key] = queue;
                }

                queue.Enqueue(connection);
                PulseWaiter();
            }
        }

        private void Evict(SpamdConnection connection)
        {
            connection.Dispose();
            lock (_gate)
            {
                if (!_disposed)
                {
                    _live--;
                    Metrics.RecordEviction();
                    Metrics.RecordReconnect();
                    PulseWaiter();
                }
            }
        }

        private void PulseWaiter()
        {
            if (_waiters.Count == 0)
            {
                return;
            }

            var waiter = _waiters[0];
            _waiters.RemoveAt(0);
            waiter.TrySetResult();
        }

        private static async Task<PostFilterSpamAssassinResult> CheckOnConnectionAsync(
            SpamdConnection connection,
            ReadOnlyMemory<byte> scan,
            string? accountName,
            PostFilterSpamAssassinTarget target,
            CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(target.OperationTimeout);
            await WriteCheckAsync(connection.Stream, scan, accountName, target.ProtocolVersion, timeoutCts.Token)
                .ConfigureAwait(false);
            return await ReadCheckAsync(connection.Stream, timeoutCts.Token).ConfigureAwait(false);
        }

        private static async Task WriteCheckAsync(
            NetworkStream stream,
            ReadOnlyMemory<byte> scan,
            string? accountName,
            string protocolVersion,
            CancellationToken cancellationToken)
        {
            var version = string.IsNullOrWhiteSpace(protocolVersion)
                ? PostFilterSpamAssassinOptions.DefaultProtocolVersion
                : protocolVersion.Trim();
            var header = new StringBuilder(64);
            header.Append("CHECK SPAMC/");
            header.Append(version);
            header.Append("\r\n");
            header.Append("Content-length: ");
            header.Append(scan.Length.ToString(CultureInfo.InvariantCulture));
            header.Append("\r\n");
            if (!string.IsNullOrWhiteSpace(accountName))
            {
                header.Append("User: ");
                header.Append(accountName);
                header.Append("\r\n");
            }

            header.Append("\r\n");
            var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
            await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(scan, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task<PostFilterSpamAssassinResult> ReadCheckAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[2048];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8) >= 0)
                {
                    break;
                }
            }

            if (total == 0)
            {
                return PostFilterSpamAssassinResult.Failed("closed");
            }

            var terminator = buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8);
            if (terminator < 0)
            {
                return PostFilterSpamAssassinResult.Failed("malformed");
            }

            if (terminator + 4 != total)
            {
                return PostFilterSpamAssassinResult.Failed("leftover");
            }

            var text = Encoding.ASCII.GetString(buffer.AsSpan(0, total));
            if (!text.StartsWith("SPAMD/", StringComparison.Ordinal))
            {
                return PostFilterSpamAssassinResult.Failed("malformed");
            }

            var firstLineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (firstLineEnd < 0)
            {
                return PostFilterSpamAssassinResult.Failed("malformed");
            }

            var statusLine = text[..firstLineEnd];
            var parts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[1] != "0")
            {
                return PostFilterSpamAssassinResult.Failed(statusLine);
            }

            var spamIndex = text.IndexOf("Spam:", StringComparison.OrdinalIgnoreCase);
            if (spamIndex < 0)
            {
                return PostFilterSpamAssassinResult.Failed("missing-spam-header");
            }

            var lineEnd = text.IndexOf("\r\n", spamIndex, StringComparison.Ordinal);
            var spamLine = lineEnd < 0 ? text[spamIndex..] : text[spamIndex..lineEnd];
            TryParseSpamScores(spamLine, out var score, out var threshold);
            if (spamLine.Contains("True", StringComparison.OrdinalIgnoreCase))
            {
                return PostFilterSpamAssassinResult.Spam(spamLine, score, threshold);
            }

            if (spamLine.Contains("False", StringComparison.OrdinalIgnoreCase))
            {
                return PostFilterSpamAssassinResult.Ham(score, threshold);
            }

            return PostFilterSpamAssassinResult.Failed(spamLine);
        }

        private static void TryParseSpamScores(string spamLine, out decimal? score, out decimal? threshold)
        {
            score = null;
            threshold = null;
            var separator = spamLine.IndexOf(';');
            if (separator < 0)
            {
                return;
            }

            var scores = spamLine[(separator + 1)..].Split('/', 2, StringSplitOptions.TrimEntries);
            if (scores.Length == 2
                && decimal.TryParse(scores[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedScore)
                && decimal.TryParse(scores[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedThreshold))
            {
                score = parsedScore;
                threshold = parsedThreshold;
            }
        }

        private static PostFilterSpamAssassinResult ClassifyConnect(Exception ex) =>
            ex is OperationCanceledException
                ? PostFilterSpamAssassinResult.Failed("timeout")
                : PostFilterSpamAssassinResult.Failed(ex.GetType().Name);

        private static PostFilterSpamAssassinResult ClassifyAfterWrite(Exception ex) =>
            ex is IOException or SocketException
                ? PostFilterSpamAssassinResult.Failed("connection")
                : PostFilterSpamAssassinResult.Failed(ex.GetType().Name);

        private static bool IsConnectionFailure(Exception ex) =>
            ex is SocketException or IOException or TimeoutException or ObjectDisposedException;

        private static bool IsStaleIdleFailure(in PostFilterSpamAssassinResult result) =>
            result.Status == PostFilterSpamAssassinStatus.Failed
            && result.Detail is "closed" or "connection";

        private sealed class SpamdConnection : IDisposable
        {
            private readonly TcpClient _client;

            public SpamdConnection(TcpClient client, string host, int port)
            {
                _client = client;
                Host = host;
                Port = port;
                Stream = client.GetStream();
            }

            public string Host { get; }

            public int Port { get; }

            public NetworkStream Stream { get; }

            public bool IsReusable
            {
                get
                {
                    try
                    {
                        var socket = _client.Client;
                        if (!_client.Connected || socket is null || !socket.Connected)
                        {
                            return false;
                        }

                        if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0)
                        {
                            return false;
                        }

                        return !Stream.DataAvailable;
                    }
                    // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                    catch (Exception)
                    {
                        return false;
                    }
                }
            }

            public void Dispose() => _client.Dispose();
        }
    }
}

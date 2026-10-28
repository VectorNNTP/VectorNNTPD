using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    /// <summary>
    /// Lab-only loopback NNTP peer that serves corpus ARTICLEs for BackFiller client connect/AUTH/ARTICLE.
    /// Cleartext only (no STARTTLS advertisement). Not a production NNTP server.
    /// </summary>
    internal sealed class LocalArticleNntpPeer : IAsyncDisposable
    {
        private readonly LocalArticleNntpPeerOptions _options;
        private readonly CorpusArticleIndex _index;
        private readonly PreloadedArticleStore? _preloaded;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _sessionGate;
        private readonly Task _acceptLoop;
        private int _disposed;

        /// <summary>Starts listening after validating bind address and indexing the corpus.</summary>
        internal LocalArticleNntpPeer(
            LocalArticleNntpPeerOptions options,
            CorpusArticleIndex index,
            PreloadedArticleStore? preloaded = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _index = index ?? throw new ArgumentNullException(nameof(index));
            _preloaded = preloaded;
            if (_options.PreloadArticlesIntoMemory && _preloaded is null)
            {
                throw new InvalidOperationException(
                    "PreloadArticlesIntoMemory requires a non-null PreloadedArticleStore.");
            }

            _options.ValidateBindOrThrow();
            Metrics = new LocalArticleNntpPeerMetrics();
            _sessionGate = new SemaphoreSlim(_options.MaxConcurrentSessions, _options.MaxConcurrentSessions);
            _listener = new TcpListener(_options.BindAddress, _options.Port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = AcceptLoopAsync(_cts.Token);
        }

        /// <summary>Bound TCP port (ephemeral when options requested port 0).</summary>
        internal int Port { get; }

        /// <summary>Aggregate peer counters.</summary>
        internal LocalArticleNntpPeerMetrics Metrics { get; }

        /// <summary>Corpus index used for Message-ID resolution.</summary>
        internal CorpusArticleIndex Index => _index;

        /// <summary>Preloaded ARTICLE store when memory-resident mode is enabled; otherwise null.</summary>
        internal PreloadedArticleStore? PreloadedStore => _preloaded;

        /// <summary><c>memory</c> when serving precomputed ARTICLE bytes; otherwise <c>file</c>.</summary>
        internal string PeerMode => _preloaded is not null ? "memory" : "file";

        /// <summary>
        /// Builds a corpus index, optionally preloads framed ARTICLE responses, then starts the peer.
        /// Prefer this over constructing with a raw empty index.
        /// </summary>
        internal static async Task<LocalArticleNntpPeer> StartAsync(
            LocalArticleNntpPeerOptions options,
            CancellationToken cancellationToken = default)
        {
            var index = await CorpusArticleIndex.BuildAsync(options.CorpusRoot, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            PreloadedArticleStore? preloaded = null;
            if (options.PreloadArticlesIntoMemory)
            {
                preloaded = await PreloadedArticleStore
                    .LoadAsync(index, options.MaxArticleBytes, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new LocalArticleNntpPeer(options, index, preloaded);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _cts.CancelAsync().ConfigureAwait(false);
            try
            {
                _listener.Stop();
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                // Best-effort shutdown: listener may already be stopped.
                Metrics.RecordProtocolError();
                _ = ex;
            }

            try
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                // Accept loop may already be faulted or cancelled during dispose.
                Metrics.RecordProtocolError();
                _ = ex;
            }

            _cts.Dispose();
            _sessionGate.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                {
                    Metrics.RecordProtocolError();
                    continue;
                }

                if (!await _sessionGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                {
                    Metrics.ConnectionRejected();
                    client.Dispose();
                    continue;
                }

                Metrics.ConnectionAccepted();
                // Session task owns disposal via using; gate releases on every completion path.
                _ = Task.Run(
                    () => RunAcceptedSessionAsync(client, cancellationToken),
                    CancellationToken.None);
            }
        }

        /// <summary>
        /// Owns <paramref name="client"/> for the session lifetime and always releases <see cref="_sessionGate"/>.
        /// </summary>
        private async Task RunAcceptedSessionAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                try
                {
                    await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _sessionGate.Release();
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            Metrics.SessionStarted();
            try
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();
                await WriteAsciiAsync(stream, "200 LocalArticleNntpPeer ready\r\n", cancellationToken)
                    .ConfigureAwait(false);

                var authenticated = !_options.RequiresAuthentication;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        return;
                    }

                    if (line.Equals("CAPABILITIES", StringComparison.OrdinalIgnoreCase))
                    {
                        // Do not advertise STARTTLS — cleartext lab peer; client UseTls must be false.
                        await WriteAsciiAsync(
                                stream,
                                "101 Capability list follows\r\nVERSION 2\r\nREADER\r\nAUTHINFO USER\r\n.\r\n",
                                cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (line.StartsWith("AUTHINFO USER ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!_options.RequiresAuthentication)
                        {
                            await WriteAsciiAsync(stream, "281 Authentication accepted\r\n", cancellationToken)
                                .ConfigureAwait(false);
                            authenticated = true;
                            continue;
                        }

                        var user = line["AUTHINFO USER ".Length..];
                        if (!string.Equals(user, _options.Username, StringComparison.Ordinal))
                        {
                            await WriteAsciiAsync(stream, "481 Authentication failed\r\n", cancellationToken)
                                .ConfigureAwait(false);
                            Metrics.RecordProtocolError();
                            return;
                        }

                        await WriteAsciiAsync(stream, "381 Password required\r\n", cancellationToken)
                            .ConfigureAwait(false);
                        var passLine = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
                        if (passLine is null
                            || !passLine.StartsWith("AUTHINFO PASS ", StringComparison.OrdinalIgnoreCase)
                            || !string.Equals(
                                passLine["AUTHINFO PASS ".Length..],
                                _options.Password,
                                StringComparison.Ordinal))
                        {
                            await WriteAsciiAsync(stream, "481 Authentication failed\r\n", cancellationToken)
                                .ConfigureAwait(false);
                            Metrics.RecordProtocolError();
                            return;
                        }

                        await WriteAsciiAsync(stream, "281 Authentication accepted\r\n", cancellationToken)
                            .ConfigureAwait(false);
                        authenticated = true;
                        continue;
                    }

                    if (line.Equals("DATE", StringComparison.OrdinalIgnoreCase))
                    {
                        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                        await WriteAsciiAsync(stream, $"111 {stamp}\r\n", cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsciiAsync(stream, "205 Connection closing\r\n", cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    if (line.StartsWith("ARTICLE ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_options.RequiresAuthentication && !authenticated)
                        {
                            await WriteAsciiAsync(stream, "480 Authentication required\r\n", cancellationToken)
                                .ConfigureAwait(false);
                            Metrics.RecordProtocolError();
                            continue;
                        }

                        await ServeArticleAsync(stream, line["ARTICLE ".Length..].Trim(), cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    await WriteAsciiAsync(stream, "500 Unknown command\r\n", cancellationToken).ConfigureAwait(false);
                    Metrics.RecordProtocolError();
                }
            }
            finally
            {
                Metrics.SessionEnded();
            }
        }

        private async Task ServeArticleAsync(NetworkStream stream, string messageId, CancellationToken cancellationToken)
        {
            Metrics.ArticleBegin();
            // True after a path has already updated article metrics so a later write failure does not double-count.
            var metricsRecorded = false;
            try
            {
                if (_options.ArticleDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_options.ArticleDelay, cancellationToken).ConfigureAwait(false);
                }

                if (_preloaded is not null)
                {
                    if (!_preloaded.TryGet(messageId, out var preloaded))
                    {
                        // Record before write so clients observing the status line cannot race the counter.
                        Metrics.RecordArticleMissing();
                        metricsRecorded = true;
                        await WriteAsciiAsync(stream, "430 No Such Article Found\r\n", cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    // Shared immutable buffer: concurrent sessions may write the same array; none mutate it.
                    await stream.WriteAsync(preloaded.WireResponse, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    // After a successful write/flush: success means the ARTICLE response left the peer.
                    // No metricsRecorded=true: this path returns immediately; a later catch cannot run.
                    Metrics.RecordArticleSuccess(preloaded.LogicalByteCount, preloaded.WireResponse.Length);
                    return;
                }

                if (!_index.TryResolve(messageId, out var path))
                {
                    // Record before write so clients observing the status line cannot race the counter.
                    Metrics.RecordArticleMissing();
                    metricsRecorded = true;
                    await WriteAsciiAsync(stream, "430 No Such Article Found\r\n", cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                byte[] logical;
                byte[] framed;
                var readFailed = false;
                try
                {
                    (logical, framed) = await NntpWireFramer
                        .ReadAndFrameAsync(path, _options.MaxArticleBytes, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                {
                    Metrics.RecordArticleError();
                    metricsRecorded = true;
                    readFailed = true;
                    logical = [];
                    framed = [];
                }

                if (readFailed)
                {
                    await WriteAsciiAsync(stream, "403 Article unavailable\r\n", cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                // Status text is optional for the client on 220; keep Message-ID for debuggability.
                var status = Encoding.ASCII.GetBytes($"220 0 {messageId} article follows\r\n");
                await stream.WriteAsync(status, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(framed, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                // After a successful write/flush: success means the ARTICLE response left the peer.
                // No metricsRecorded=true: try ends here; a later catch cannot run after this statement.
                Metrics.RecordArticleSuccess(logical.Length, status.Length + framed.Length);
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                if (!metricsRecorded)
                {
                    Metrics.RecordArticleError();
                }

                throw;
            }
        }

        private static async Task WriteAsciiAsync(Stream stream, string text, CancellationToken cancellationToken)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(1024);
            try
            {
                var length = 0;
                while (true)
                {
                    var rented = ArrayPool<byte>.Shared.Rent(1);
                    try
                    {
                        var n = await stream.ReadAsync(rented.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
                        if (n == 0)
                        {
                            return length == 0 ? null : Encoding.ASCII.GetString(buffer, 0, length);
                        }

                        if (length >= buffer.Length)
                        {
                            return null;
                        }

                        buffer[length++] = rented[0];
                        if (length >= 2 && buffer[length - 2] == (byte)'\r' && buffer[length - 1] == (byte)'\n')
                        {
                            return Encoding.ASCII.GetString(buffer, 0, length - 2);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}

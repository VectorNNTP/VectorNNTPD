using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// TLS NNTP client for BackFiller's cache listener subset: greeting, <c>ARTICLE</c>, <c>QUIT</c>.
    /// </summary>
    public sealed class BackFillerNntpStressClient : IAsyncDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly bool _acceptAnyServerCertificate;
        private TcpClient? _tcp;
        private SslStream? _ssl;
        private Stream? _stream;
        private readonly byte[] _lineBuffer = new byte[512];
        private readonly byte[] _readBuffer = new byte[64 * 1024];
        private readonly byte[] _commandBuffer = new byte[512];
        private int _disposed;

        public BackFillerNntpStressClient(string host, int port, bool acceptAnyServerCertificate = true)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(host);
            ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
            _host = host;
            _port = port;
            _acceptAnyServerCertificate = acceptAnyServerCertificate;
        }

        public bool IsConnected => _stream is not null && _tcp is { Connected: true };

        /// <summary>Connects, completes TLS, and consumes the <c>201</c> greeting.</summary>
        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (IsConnected)
            {
                return;
            }

            var tcp = new TcpClient();
            tcp.NoDelay = true;
            await tcp.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);
            var ssl = new SslStream(
                tcp.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: _acceptAnyServerCertificate
                    ? static (_, _, _, _) => true
                    : null);
            await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = _host,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            _tcp = tcp;
            _ssl = ssl;
            _stream = ssl;

            var greeting = await NntpArticleResponseReader.ReadStatusLineAsync(
                    _stream,
                    _lineBuffer,
                    cancellationToken)
                .ConfigureAwait(false);
            if (greeting.Code is not (200 or 201))
            {
                throw new InvalidOperationException($"Unexpected NNTP greeting code {greeting.Code}.");
            }
        }

        /// <summary>
        /// Sends <c>ARTICLE &lt;message-id&gt;</c> and fully consumes the response before returning.
        /// </summary>
        public async Task<NntpArticleExchangeResult> ArticleAsync(
            string messageId,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_stream is null)
            {
                throw new InvalidOperationException("Not connected.");
            }

            var commandLength = Encoding.ASCII.GetBytes("ARTICLE ", _commandBuffer.AsSpan());
            var idBytes = Encoding.ASCII.GetBytes(messageId);
            if (commandLength + idBytes.Length + 2 > _commandBuffer.Length)
            {
                return new NntpArticleExchangeResult(
                    NntpResponseKind.ProtocolError,
                    0,
                    0,
                    0,
                    "ARTICLE command exceeds 512-octet limit");
            }

            idBytes.CopyTo(_commandBuffer.AsSpan(commandLength));
            commandLength += idBytes.Length;
            _commandBuffer[commandLength++] = (byte)'\r';
            _commandBuffer[commandLength++] = (byte)'\n';
            await _stream.WriteAsync(_commandBuffer.AsMemory(0, commandLength), cancellationToken)
                .ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            return await NntpArticleResponseReader.ReadArticleExchangeAsync(
                    _stream,
                    _lineBuffer,
                    _readBuffer,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>Sends <c>QUIT</c> and closes the transport.</summary>
        public async Task QuitAsync(CancellationToken cancellationToken)
        {
            if (_stream is null)
            {
                return;
            }

            try
            {
                await _stream.WriteAsync("QUIT\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                _ = await NntpArticleResponseReader.ReadStatusLineAsync(_stream, _lineBuffer, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException)
            {
                // Peer may close without 205 under load; still dispose.
            }
            catch (ObjectDisposedException)
            {
                // Transport already closed during QUIT; still dispose.
            }
            catch (AuthenticationException)
            {
                // TLS fault during QUIT write/read; still dispose.
            }
            catch (InvalidOperationException)
            {
                // Stream/state fault during QUIT; still dispose.
            }
            finally
            {
                await DisposeAsync().ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (_ssl is not null)
            {
                await _ssl.DisposeAsync().ConfigureAwait(false);
            }

            _tcp?.Dispose();
            _stream = null;
            _ssl = null;
            _tcp = null;
        }
    }

    /// <summary>Pool of reusable TLS NNTP sessions with bounded size.</summary>
    public sealed class BackFillerNntpConnectionPool : IAsyncDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly int _capacity;
        private readonly ConcurrentBag<BackFillerNntpStressClient> _idle = [];
        private int _created;
        private int _disposed;

        public BackFillerNntpConnectionPool(string host, int port, int capacity)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(host);
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
            _host = host;
            _port = port;
            _capacity = capacity;
        }

        public async Task<BackFillerNntpStressClient> RentAsync(
            StressMetrics metrics,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            while (_idle.TryTake(out var existing))
            {
                if (existing.IsConnected)
                {
                    return existing;
                }

                await existing.DisposeAsync().ConfigureAwait(false);
                Interlocked.Decrement(ref _created);
                metrics.ConnectionClosed();
            }

            while (true)
            {
                var created = Interlocked.Increment(ref _created);
                if (created <= _capacity)
                {
                    var client = new BackFillerNntpStressClient(_host, _port);
                    await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    metrics.ConnectionOpened();
                    return client;
                }

                Interlocked.Decrement(ref _created);
                // Wait briefly for a return under saturation.
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                if (!_idle.TryTake(out var recycled) || recycled is null)
                {
                    continue;
                }

                if (recycled.IsConnected)
                {
                    return recycled;
                }

                await recycled.DisposeAsync().ConfigureAwait(false);
                Interlocked.Decrement(ref _created);
                metrics.ConnectionClosed();
            }
        }

        public void Return(BackFillerNntpStressClient client, StressMetrics metrics, bool healthy)
        {
            if (_disposed != 0 || !healthy || !client.IsConnected)
            {
                _ = client.DisposeAsync().AsTask();
                Interlocked.Decrement(ref _created);
                metrics.ConnectionClosed();
                return;
            }

            _idle.Add(client);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            while (_idle.TryTake(out var client))
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await client.QuitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                catch (IOException)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                catch (AuthenticationException)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }
}

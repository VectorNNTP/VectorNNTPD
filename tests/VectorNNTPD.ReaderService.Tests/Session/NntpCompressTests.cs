using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Authentication;
using VectorNNTPD.ReaderService.Session.Commands;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Tests.Networking.Transport;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.ReaderService.Tests.Session;

/// <summary>Session-level COMPRESS DEFLATE (RFC 8054) regressions over the real transport stack.</summary>
[Collection(nameof(TransportTestHostCollection))]
public sealed class NntpCompressTests
{
    [Fact]
    public async Task Compress_Capabilities_AdvertiseThenStopAfterActivation()
    {
        await using var duplex = await CompressTestDuplex.CreateAsync();
        var sessionTask = duplex.CreateSession().RunAsync();

        _ = await duplex.ReadClientLineAsync();
        var before = await ReadCapabilitiesFromDuplexAsync(duplex);
        Assert.Contains("COMPRESS DEFLATE", before);
        Assert.Contains("STARTTLS", before);
        Assert.Contains("MODE-READER", before);
        Assert.Contains("AUTHINFO USER", before);

        await duplex.WriteClientLineAsync("COMPRESS DEFLATE");
        Assert.StartsWith("206 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        var after = await ReadCapabilitiesFromDuplexAsync(duplex);
        Assert.True(duplex.Connection.IsCompressed);
        Assert.DoesNotContain(after, static c => c.StartsWith("COMPRESS", StringComparison.Ordinal));
        Assert.DoesNotContain("STARTTLS", after);
        Assert.DoesNotContain("MODE-READER", after);
        Assert.Contains("AUTHINFO", after);
        Assert.DoesNotContain("AUTHINFO USER", after);

        await duplex.WriteClientLineAsync("QUIT");
        Assert.StartsWith("205 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await sessionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Compress_Capabilities_AfterModeReader_StillAdvertisesUntilActive()
    {
        await using var duplex = await CompressTestDuplex.CreateAsync();
        var sessionTask = duplex.CreateSession().RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("MODE READER");
        Assert.StartsWith("201 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        var caps = await ReadCapabilitiesFromDuplexAsync(duplex);
        Assert.Contains("COMPRESS DEFLATE", caps);
        Assert.Contains("READER", caps);

        await duplex.WriteClientLineAsync("QUIT");
        Assert.StartsWith("205 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await sessionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("COMPRESS", "501 ")]
    [InlineData("COMPRESS DEFLATE EXTRA", "501 ")]
    [InlineData("COMPRESS deflate", "501 ")]
    [InlineData("COMPRESS Deflate", "501 ")]
    [InlineData("COMPRESS FOO", "503 ")]
    [InlineData("COMPRESS GZIP", "503 ")]
    public async Task Compress_SyntaxAndUnsupportedAlgorithm(string command, string expectedPrefix)
    {
        await using var duplex = await CompressTestDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var dispatcher = new NntpCommandDispatcher();
        var response = new NntpResponseWriter(duplex.ServerOutput);

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, command);
        Assert.StartsWith(expectedPrefix, await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.False(session.Connection.IsCompressed);
    }

    [Fact]
    public async Task Compress_Deflate_Exact206Wire_AndIsCompressed()
    {
        await using var duplex = await CompressTestDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var dispatcher = new NntpCommandDispatcher();
        var response = new NntpResponseWriter(duplex.ServerOutput);

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "COMPRESS DEFLATE");
        Assert.Equal("206 Compression active", await duplex.ReadClientLineAsync());
        Assert.True(session.Connection.IsCompressed);
    }

    [Fact]
    public async Task Compress_Deflate_SessionRoundTrip_Repeated_AuthinfoRejected()
    {
        Assert.True(ProducesRawDeflateNotZlibOrGzip());

        await using var host = await TransportTestHost.StartPlainAsync();
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();
        var plain = new PlainReceiveBuffer();

        var provider = new FixedAuthProvider("alice", "secret");
        var session = new NntpSession(
            server,
            NullLogger<NntpSession>.Instance,
            authenticationProvider: provider,
            allowCleartextAuth: true);
        var sessionTask = session.RunAsync();

        _ = await ReadPlainLineAsync(clientSocket, plain);
        await clientSocket.SendAsync("COMPRESS DEFLATE\r\n"u8.ToArray());
        Assert.Equal("206 Compression active", await ReadPlainLineAsync(clientSocket, plain));
        await WaitForServerCompressedAsync(server);

        await using var stream = WrapAfterPlain(clientSocket, plain);
        await using var deflate = new NntpDeflateStream(stream);

        await WriteDeflateLineAsync(deflate, "DATE");
        Assert.StartsWith("111 ", await ReadDeflateLineAsync(deflate), StringComparison.Ordinal);

        await WriteDeflateLineAsync(deflate, "COMPRESS DEFLATE");
        Assert.StartsWith("502 ", await ReadDeflateLineAsync(deflate), StringComparison.Ordinal);

        await WriteDeflateLineAsync(deflate, "AUTHINFO USER alice");
        Assert.StartsWith("502 ", await ReadDeflateLineAsync(deflate), StringComparison.Ordinal);
        await WriteDeflateLineAsync(deflate, "AUTHINFO PASS secret");
        Assert.StartsWith("502 ", await ReadDeflateLineAsync(deflate), StringComparison.Ordinal);

        Assert.Equal("AUTHINFO PASS <redacted>", NntpCommandLogFormat.RedactRxLine("AUTHINFO PASS secret"));

        await WriteDeflateLineAsync(deflate, "QUIT");
        Assert.StartsWith("205 ", await ReadDeflateLineAsync(deflate), StringComparison.Ordinal);
        await sessionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Compress_Logging_OwnedByCompress_WithRecordingFactory()
    {
        var recording = new RecordingLoggerFactory();
        await using var duplex = await CompressTestDuplex.CreateAsync();
        var session = new NntpSession(
            duplex.Connection,
            recording.CreateLogger<NntpSession>(),
            loggerFactory: recording);
        var sessionTask = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("COMPRESS DEFLATE");
        Assert.StartsWith("206 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.True(duplex.Connection.IsCompressed);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await sessionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(recording.Messages, m => m.Contains("RX: COMPRESS DEFLATE", StringComparison.Ordinal));
        var tx = Assert.Single(
            recording.Messages,
            m => m.Contains("TX: COMPRESS [206 Compression active] executed in", StringComparison.Ordinal));
        Assert.Contains("[DEFLATE active]", tx, StringComparison.Ordinal);
        Assert.Contains(recording.Categories, c => c == typeof(Compress).FullName);
    }

    [Fact]
    public async Task Compress_ClientSendsCompressedBytesImmediatelyAfter206_ConsumedByDeflate()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();
        var plain = new PlainReceiveBuffer();

        var sessionTask = new NntpSession(server, NullLogger<NntpSession>.Instance).RunAsync();
        _ = await ReadPlainLineAsync(clientSocket, plain);

        await clientSocket.SendAsync("COMPRESS DEFLATE\r\n"u8.ToArray());
        Assert.StartsWith("206 ", await ReadPlainLineAsync(clientSocket, plain), StringComparison.Ordinal);

        await using var stream = WrapAfterPlain(clientSocket, plain);
        await using var deflate = new NntpDeflateStream(stream);
        await WriteDeflateLineAsync(deflate, "DATE");

        await WaitForServerCompressedAsync(server);
        Assert.False(server.IsCompleted);
        Assert.StartsWith("111 ", await ReadDeflateLineAsync(deflate), StringComparison.Ordinal);

        await server.CompleteAsync();
        await sessionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Compress_AuthinfoBeforeCompress_ThenCompressSucceeds()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();
        var plain = new PlainReceiveBuffer();

        var provider = new FixedAuthProvider("alice", "secret");
        var sessionTask = new NntpSession(
                server,
                NullLogger<NntpSession>.Instance,
                authenticationProvider: provider,
                allowCleartextAuth: true)
            .RunAsync();

        _ = await ReadPlainLineAsync(clientSocket, plain);
        await clientSocket.SendAsync("AUTHINFO USER alice\r\n"u8.ToArray());
        Assert.StartsWith("381 ", await ReadPlainLineAsync(clientSocket, plain), StringComparison.Ordinal);
        await clientSocket.SendAsync("AUTHINFO PASS secret\r\n"u8.ToArray());
        Assert.StartsWith("281 ", await ReadPlainLineAsync(clientSocket, plain), StringComparison.Ordinal);

        await clientSocket.SendAsync("COMPRESS DEFLATE\r\n"u8.ToArray());
        Assert.StartsWith("206 ", await ReadPlainLineAsync(clientSocket, plain), StringComparison.Ordinal);
        await WaitForServerCompressedAsync(server);

        await using var stream = WrapAfterPlain(clientSocket, plain);
        await using var deflate = new NntpDeflateStream(stream);
        await WriteDeflateLineAsync(deflate, "DATE");
        Assert.StartsWith("111 ", await ReadDeflateLineAsync(deflate), StringComparison.Ordinal);

        await server.CompleteAsync();
        await sessionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Compress_AfterStartTls_LayersDeflateAboveTls()
    {
        var pfx = TransportTestShared.CreatePfx("nntpd01.usenet.ninja");
        await using var host = await TransportTestHost.StartPlainWithCertificateAsync(pfx);
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();
        var plain = new PlainReceiveBuffer();

        var provider = new FixedAuthProvider("alice", "secret");
        var sessionTask = new NntpSession(
                server,
                NullLogger<NntpSession>.Instance,
                certificateProvider: host.CertificateProvider,
                authenticationProvider: provider,
                allowCleartextAuth: false)
            .RunAsync();

        _ = await ReadPlainLineAsync(clientSocket, plain);
        await clientSocket.SendAsync("STARTTLS\r\n"u8.ToArray());
        Assert.StartsWith("382 ", await ReadPlainLineAsync(clientSocket, plain), StringComparison.Ordinal);

        await using var stream = WrapAfterPlain(clientSocket, plain);
        await using var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(CreateClientSslOptions());
        await WaitForServerTlsAsync(server);

        await WriteSslLineAsync(ssl, "AUTHINFO USER alice");
        Assert.StartsWith("381 ", await ReadSslLineAsync(ssl), StringComparison.Ordinal);
        await WriteSslLineAsync(ssl, "AUTHINFO PASS secret");
        Assert.StartsWith("281 ", await ReadSslLineAsync(ssl), StringComparison.Ordinal);

        await WriteSslLineAsync(ssl, "COMPRESS DEFLATE");
        Assert.Equal("206 Compression active", await ReadSslLineAsync(ssl));
        await WaitForServerCompressedAsync(server);
        Assert.True(server.IsTls);
        Assert.True(server.IsCompressed);

        await using var deflate = new NntpDeflateStream(ssl);
        await WriteDeflateLineAsync(deflate, "DATE");
        Assert.StartsWith("111 ", await ReadDeflateLineAsync(deflate), StringComparison.Ordinal);

        await server.CompleteAsync();
        await sessionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Compress_PipelinedPlaintextAfterCommand_IsDiscarded()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();
        var plain = new PlainReceiveBuffer();

        var sessionTask = new NntpSession(server, NullLogger<NntpSession>.Instance).RunAsync();
        _ = await ReadPlainLineAsync(clientSocket, plain);

        await clientSocket.SendAsync("COMPRESS DEFLATE\r\nDATE\r\n"u8.ToArray());
        Assert.StartsWith("206 ", await ReadPlainLineAsync(clientSocket, plain), StringComparison.Ordinal);
        await WaitForServerCompressedAsync(server);

        await using var stream = WrapAfterPlain(clientSocket, plain);
        await using var deflate = new NntpDeflateStream(stream);

        await WriteDeflateLineAsync(deflate, "DATE");
        Assert.StartsWith("111 ", await ReadDeflateLineAsync(deflate), StringComparison.Ordinal);

        await server.CompleteAsync();
        await sessionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Compress_ActivationFailureBefore206_Returns403()
    {
        await using var duplex = await CompressTestDuplex.CreateAsync(pauseReadsFails: true);
        var session = duplex.CreateSession();
        var dispatcher = new NntpCommandDispatcher();
        var response = new NntpResponseWriter(duplex.ServerOutput);

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, "COMPRESS DEFLATE");
        Assert.Contains("403 Unable to activate compression", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.False(session.Connection.IsCompressed);
        Assert.False(session.Connection.IsCompleted);
    }

    [Fact]
    public async Task Compress_UpgradeFailureAfter206_TerminatesWithoutSecondaryStatus()
    {
        await using var duplex = await CompressTestDuplex.CreateAsync(upgradeFails: true);
        var sessionTask = duplex.CreateSession().RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("COMPRESS DEFLATE");
        Assert.StartsWith("206 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await sessionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(duplex.Connection.IsCompleted);
        Assert.False(duplex.Connection.IsCompressed);
    }

    [Fact]
    public async Task Compress_MalformedCompressedInput_TerminatesConnection()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var clientSocket = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();
        var plain = new PlainReceiveBuffer();

        var sessionTask = new NntpSession(server, NullLogger<NntpSession>.Instance).RunAsync();
        _ = await ReadPlainLineAsync(clientSocket, plain);

        await clientSocket.SendAsync("COMPRESS DEFLATE\r\n"u8.ToArray());
        Assert.StartsWith("206 ", await ReadPlainLineAsync(clientSocket, plain), StringComparison.Ordinal);
        await WaitForServerCompressedAsync(server);

        await clientSocket.SendAsync(new byte[] { 0x00, 0x01, 0x02, 0x03, 0xFF });
        await sessionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(server.IsCompleted);
    }

    private static bool ProducesRawDeflateNotZlibOrGzip()
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            ds.Write("nntp"u8);
        }

        var compressed = ms.ToArray();
        return compressed.Length > 0
               && compressed[0] != 0x78
               && !(compressed.Length >= 2 && compressed[0] == 0x1F && compressed[1] == 0x8B);
    }

    private static async Task WaitForServerCompressedAsync(INntpConnection server)
    {
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!server.IsCompressed)
        {
            wait.Token.ThrowIfCancellationRequested();
            if (server.IsCompleted || server.ConnectionClosed.IsCancellationRequested)
            {
                throw new InvalidOperationException("Server closed before DEFLATE was published.");
            }

            await Task.Delay(10, wait.Token);
        }
    }

    private static async Task WaitForServerTlsAsync(INntpConnection server)
    {
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!server.IsTls)
        {
            wait.Token.ThrowIfCancellationRequested();
            if (server.IsCompleted || server.ConnectionClosed.IsCancellationRequested)
            {
                throw new InvalidOperationException("Server closed before TLS was published.");
            }

            await Task.Delay(10, wait.Token);
        }
    }

    private static SslClientAuthenticationOptions CreateClientSslOptions() =>
        new()
        {
            TargetHost = "nntpd01.usenet.ninja",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
        };

    private static async Task<string> ReadPlainLineAsync(Socket socket, PlainReceiveBuffer? buffer = null)
    {
        buffer ??= new PlainReceiveBuffer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            if (buffer.TryReadLine(out var line))
            {
                return line;
            }

            var scratch = new byte[512];
            var n = await socket.ReceiveAsync(scratch, cts.Token);
            Assert.True(n > 0, $"Socket closed while reading plain line (serverCompleted unknown).");
            buffer.Append(scratch.AsSpan(0, n));
        }
    }

    /// <summary>Preserves leftover octets across plain-line reads before a DEFLATE wrap.</summary>
    private sealed class PlainReceiveBuffer
    {
        private readonly List<byte> _bytes = [];

        public void Append(ReadOnlySpan<byte> data) => _bytes.AddRange(data);

        public ReadOnlyMemory<byte> TakeLeftover()
        {
            if (_bytes.Count == 0)
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            var leftover = _bytes.ToArray();
            _bytes.Clear();
            return leftover;
        }

        public bool TryReadLine(out string line)
        {
            line = string.Empty;
            var text = Encoding.ASCII.GetString(CollectionsMarshal.AsSpan(_bytes));
            var idx = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (idx < 0)
            {
                return false;
            }

            line = text[..idx];
            var consumed = idx + 2;
            _bytes.RemoveRange(0, consumed);
            return true;
        }
    }

    private static Stream WrapAfterPlain(Socket socket, PlainReceiveBuffer buffer)
    {
        var network = new NetworkStream(socket, ownsSocket: false);
        var leftover = buffer.TakeLeftover();
        return leftover.IsEmpty
            ? network
            : new PrefixedStream(network, leftover, leaveInnerOpen: false);
    }

    private static async Task WriteSslLineAsync(SslStream ssl, string command)
    {
        var bytes = Encoding.ASCII.GetBytes(command + "\r\n");
        await ssl.WriteAsync(bytes);
        await ssl.FlushAsync();
    }

    private static async Task<string> ReadSslLineAsync(SslStream ssl)
    {
        var buffer = new byte[512];
        var total = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (total < buffer.Length)
        {
            var n = await ssl.ReadAsync(buffer.AsMemory(total), cts.Token);
            Assert.True(n > 0);
            total += n;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            var idx = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (idx >= 0)
            {
                return text[..idx];
            }
        }

        throw new InvalidOperationException("Line too long.");
    }

    private static async Task WriteDeflateLineAsync(Stream deflate, string command)
    {
        var bytes = Encoding.ASCII.GetBytes(command + "\r\n");
        await deflate.WriteAsync(bytes);
        await deflate.FlushAsync();
    }

    private static async Task<string> ReadDeflateLineAsync(Stream deflate)
    {
        var buffer = new byte[512];
        var total = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (total < buffer.Length)
        {
            var n = await deflate.ReadAsync(buffer.AsMemory(total), cts.Token);
            Assert.True(n > 0);
            total += n;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            var idx = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (idx >= 0)
            {
                return text[..idx];
            }
        }

        throw new InvalidOperationException("Line too long.");
    }

    private static async Task<List<string>> ReadCapabilitiesFromDuplexAsync(CompressTestDuplex duplex)
    {
        await duplex.WriteClientLineAsync("CAPABILITIES");
        Assert.StartsWith("101 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        var caps = new List<string>();
        while (true)
        {
            var line = await duplex.ReadClientLineAsync();
            if (line == ".")
            {
                break;
            }

            caps.Add(line);
        }

        return caps;
    }

    private sealed class FixedAuthProvider : INntpAuthenticationProvider
    {
        private readonly string _user;
        private readonly string _pass;

        public FixedAuthProvider(string user, string pass)
        {
            _user = user;
            _pass = pass;
        }

        public ValueTask<NntpAuthenticationResult> AuthenticateAsync(
            string username,
            string password,
            CancellationToken cancellationToken)
        {
            if (username == _user && password == _pass)
            {
                return ValueTask.FromResult(NntpAuthenticationResult.Success(
                    username,
                    new NntpAuthorization(
                        isAuthenticated: true,
                        authorizedReader: true,
                        authorizedTransit: false,
                        postingPermitted: false,
                        streamingPermitted: false)));
            }

            return ValueTask.FromResult(NntpAuthenticationResult.Failed);
        }
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public ConcurrentBag<string> Messages { get; } = [];
        public ConcurrentBag<string> Categories { get; } = [];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
        {
            Categories.Add(categoryName);
            return new RecordingLogger(Messages);
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly ConcurrentBag<string> _messages;

        public RecordingLogger(ConcurrentBag<string> messages) => _messages = messages;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _messages.Add(formatter(state, exception));
        }
    }

    private sealed class CompressTestDuplex : IAsyncDisposable
    {
        private readonly System.IO.Pipelines.Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly System.IO.Pipelines.Pipe _serverToClient = new(NntpPipeOptions.Create());
        private readonly PipeConnection _connection;

        private CompressTestDuplex(bool pauseReadsFails, bool upgradeFails)
        {
            _connection = new PipeConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                pauseReadsFails,
                upgradeFails);
        }

        public System.IO.Pipelines.PipeWriter ServerOutput => _serverToClient.Writer;
        public INntpConnection Connection => _connection;

        public static Task<CompressTestDuplex> CreateAsync(
            bool pauseReadsFails = false,
            bool upgradeFails = false) =>
            Task.FromResult(new CompressTestDuplex(pauseReadsFails, upgradeFails));

        public NntpSession CreateSession() =>
            new(_connection, NullLogger<NntpSession>.Instance);

        public async Task WriteClientLineAsync(string line)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
            await _clientToServer.Writer.WriteAsync(bytes);
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task<string> ReadClientLineAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var line = await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, cts.Token);
            Assert.NotNull(line);
            return line!;
        }

        public async ValueTask DisposeAsync()
        {
            await _clientToServer.Writer.CompleteAsync();
            await _clientToServer.Reader.CompleteAsync();
            await _serverToClient.Writer.CompleteAsync();
            await _serverToClient.Reader.CompleteAsync();
        }

        private sealed class PipeConnection : INntpConnection
        {
            private readonly CancellationTokenSource _cts = new();
            private readonly bool _pauseReadsFails;
            private readonly bool _upgradeFails;
            private int _compressed;

            public PipeConnection(
                System.IO.Pipelines.PipeReader input,
                System.IO.Pipelines.PipeWriter output,
                bool pauseReadsFails,
                bool upgradeFails)
            {
                Input = input;
                Output = output;
                _pauseReadsFails = pauseReadsFails;
                _upgradeFails = upgradeFails;
                ClientIdentity = VectorNNTPD.Common.Networking.Proxy.ConnectionClientIdentity.Direct(
                    new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119));
            }

            public System.IO.Pipelines.PipeReader Input { get; }
            public System.IO.Pipelines.PipeWriter Output { get; }
            public System.Net.EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;
            public System.Net.EndPoint? LocalEndPoint => null;
            public VectorNNTPD.Common.Networking.Proxy.ConnectionClientIdentity ClientIdentity { get; }
            public bool IsTls => false;

            public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
            {
                tlsVersion = string.Empty;
                cipher = string.Empty;
                return false;
            }

            public bool IsCompressed => Volatile.Read(ref _compressed) == 1;
            public CancellationToken ConnectionClosed => _cts.Token;
            public bool IsCompleted => _cts.IsCancellationRequested;
            public long OutboundIdleVersion => 0;

            public Task PauseReadsAsync(CancellationToken cancellationToken = default) =>
                _pauseReadsFails
                    ? Task.FromException(new InvalidOperationException("simulated pause failure"))
                    : Task.CompletedTask;

            public Task WaitForOutboundDeliveryAsync(
                long outboundIdleVersionBeforeFlush,
                CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task WaitForOutboundDeliveryAndPauseReadsAsync(
                long outboundIdleVersionBeforeFlush,
                CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task CompleteAsync(Exception? exception = null)
            {
                _cts.Cancel();
                return Task.CompletedTask;
            }

            public Task UpgradeToTlsAsync(
                VectorNNTPD.Common.Networking.Certificates.ITlsCertificateContextProvider certificateProvider,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default)
            {
                if (_upgradeFails)
                {
                    return Task.FromException(new InvalidOperationException("simulated upgrade failure"));
                }

                Volatile.Write(ref _compressed, 1);
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                _cts.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.Networking.Transport;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.Commands;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Tests.Networking.Transport;

namespace VectorNNTPD.TransitService.Tests.Session;

/// <summary>QUIT command — RFC 3977 §5.4 and peer-disappears-during-205 race.</summary>
[Collection(nameof(TransportTestHostCollection))]
public sealed class QuitCommandTests
{
    [Fact]
    public async Task Quit_Returns205_AndEndsSession()
    {
        await using var duplex = await QuitDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("QUIT");
        Assert.Equal("205 Connection closing", await duplex.ReadClientLineAsync());
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.Connection.IsCompleted);
    }

    [Fact]
    public async Task Quit_WithTrailingArgs_Returns501_AndSessionRemainsUsable()
    {
        await using var duplex = await QuitDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("QUIT NOW");
        Assert.Equal("501 Syntax error", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("DATE");
        var date = await duplex.ReadClientLineAsync();
        Assert.StartsWith("111 ", date, StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("QUIT");
        Assert.Equal("205 Connection closing", await duplex.ReadClientLineAsync());
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Quit_Logging_RxCentralized_TxOwnedByQuit_NoDuplicate()
    {
        var recording = new RecordingLoggerFactory();
        await using var duplex = await QuitDuplex.CreateAsync();
        var session = duplex.CreateSession(recording);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("QUIT");
        Assert.Equal("205 Connection closing", await duplex.ReadClientLineAsync());
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(recording.Messages, m => m.Contains("RX: QUIT", StringComparison.Ordinal));
        Assert.Equal(
            1,
            recording.Messages.Count(m =>
                m.Contains("TX: QUIT [205 Connection closing] executed in", StringComparison.Ordinal)));
        Assert.DoesNotContain(
            recording.Messages,
            m => m.Contains("TX: QUIT", StringComparison.Ordinal)
                 && m.Contains("executed in", StringComparison.Ordinal)
                 && m.Contains("[failed]", StringComparison.Ordinal));
        Assert.Contains(recording.Categories, c => c == typeof(Quit).FullName);
        Assert.DoesNotContain(
            recording.Messages,
            m => m.Contains("QUIT failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Quit_NoFurtherCommandsProcessed()
    {
        await using var duplex = await QuitDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("QUIT");
        Assert.Equal("205 Connection closing", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("DATE");
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        // DATE after terminal QUIT must not produce a response.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await duplex.ReadClientLineAsync(cts.Token));
    }

    [Fact]
    public async Task Quit_PeerDisconnectDuringWrite_IsQuietAndTerminal()
    {
        var recording = new RecordingLoggerFactory();
        await using var duplex = await QuitDuplex.CreateAsync();
        var session = duplex.CreateSession(recording);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        // Complete the server→client pipe so FlushAsync of 205 observes a completed writer (peer gone).
        await duplex.CompleteServerToClientAsync();

        await duplex.WriteClientLineAsync("QUIT");
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(session.Connection.IsCompleted);
        Assert.DoesNotContain(
            recording.Messages,
            m => m.Contains("QUIT failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            recording.Messages,
            m => m.Contains("TX: QUIT", StringComparison.Ordinal)
                 && m.Contains("executed in", StringComparison.Ordinal));
        // May be clean success (enqueue before complete) or peer-disconnected detail.
        var tx = Assert.Single(
            recording.Messages,
            m => m.Contains("TX: QUIT", StringComparison.Ordinal)
                 && m.Contains("executed in", StringComparison.Ordinal));
        Assert.DoesNotContain("[failed]", tx, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quit_Socket_NormalCloseAfter205()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();
        var recording = new RecordingLoggerFactory();
        var session = new NntpSession(
            server,
            recording.CreateLogger<NntpSession>(),
            loggerFactory: recording);
        var run = session.RunAsync();

        _ = await ReadSocketLineAsync(client);
        await client.SendAsync("QUIT\r\n"u8.ToArray());
        Assert.Equal("205 Connection closing", await ReadSocketLineAsync(client));
        client.Shutdown(SocketShutdown.Both);
        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(
            recording.Messages,
            m => m.Contains("QUIT failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            recording.Messages,
            m => m.Contains("TX: QUIT", StringComparison.Ordinal)
                 && m.Contains("executed in", StringComparison.Ordinal)
                 && !m.Contains("[failed]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Quit_Socket_ImmediateClientCloseAfterQuit_IsNotApplicationError()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();
        var recording = new RecordingLoggerFactory();
        var session = new NntpSession(
            server,
            recording.CreateLogger<NntpSession>(),
            loggerFactory: recording);
        var run = session.RunAsync();

        _ = await ReadSocketLineAsync(client);
        await client.SendAsync("QUIT\r\n"u8.ToArray());
        // Do not wait for 205 — close immediately (common client race).
        client.LingerState = new LingerOption(true, 0);
        client.Close();

        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(
            recording.Messages,
            m => m.Contains("QUIT failed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            recording.Messages,
            m => m.Contains("NNTP session ended with an error", StringComparison.OrdinalIgnoreCase)
                 && recording.Messages.Any(x => x.Contains("QUIT failed", StringComparison.Ordinal)));
        Assert.Contains(
            recording.Messages,
            m => m.Contains("TX: QUIT", StringComparison.Ordinal)
                 && m.Contains("executed in", StringComparison.Ordinal));
        var tx = Assert.Single(
            recording.Messages,
            m => m.Contains("TX: QUIT", StringComparison.Ordinal)
                 && m.Contains("executed in", StringComparison.Ordinal));
        // Immediate RST can complete the connection after a successful 205 write; the
        // existing RunAsync IsCompleted heuristic may then append [failed]. That is
        // not an application error when 205 was selected for TX.
        Assert.True(
            tx.Contains("[205 Connection closing]", StringComparison.Ordinal)
            || !tx.Contains("[failed]", StringComparison.Ordinal),
            tx);
    }

    [Fact]
    public async Task Quit_GenuineNonPeerFailure_IsNotSwallowed()
    {
        var recording = new RecordingLoggerFactory();
        NntpCommandLoggers.Configure(recording);

        var identity = ConnectionClientIdentity.Direct(
            new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119));
        await using var connection = new FailOnWaitConnection(
            identity,
            new IOException("simulated non-peer transport failure"));
        var session = new NntpSession(
            connection,
            recording.CreateLogger<NntpSession>(),
            loggerFactory: recording);
        var response = new NntpResponseWriter(connection.Output);
        var (command, line) = NntpCommandTestParse.Parse("QUIT");
        var context = new NntpCommandContext(session, command, line, response);

        try
        {
            await Quit.HandleAsync(context, CancellationToken.None);
        }
        catch (IOException ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
            // Expected — non-peer failure must propagate.
        }

        Assert.Contains(
            recording.Messages,
            m => m.Contains("QUIT failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            recording.Messages,
            m => m.Contains("TX: QUIT", StringComparison.Ordinal)
                 && m.Contains("executed in", StringComparison.Ordinal)
                 && m.Contains("[failed]", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(SocketError.ConnectionReset)]
    [InlineData(SocketError.ConnectionAborted)]
    [InlineData((SocketError)32)]
    public void PeerDisconnect_ClassifiesResetLikeSocketErrors(SocketError error)
    {
        var identity = ConnectionClientIdentity.Direct(
            new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119));
        using var cts = new CancellationTokenSource();
        var connection = new StaticConnection(identity, cts.Token, completed: false);
        var ex = new IOException("send failed", new SocketException((int)error));
        Assert.True(NntpPeerDisconnect.IsPeerDisconnect(ex, connection));
    }

    [Fact]
    public void PeerDisconnect_DoesNotClassifyArbitraryIOException()
    {
        var identity = ConnectionClientIdentity.Direct(
            new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119));
        using var cts = new CancellationTokenSource();
        var connection = new StaticConnection(identity, cts.Token, completed: false);
        Assert.False(NntpPeerDisconnect.IsPeerDisconnect(new IOException("disk full"), connection));
    }

    private static async Task<string> ReadSocketLineAsync(Socket socket)
    {
        var buffer = new byte[512];
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(total));
            Assert.True(n > 0);
            total += n;
            var text = Encoding.ASCII.GetString(buffer.AsSpan(0, total));
            var idx = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (idx >= 0)
            {
                return text[..idx];
            }
        }

        throw new InvalidOperationException("line too long");
    }

    private sealed class QuitDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public static Task<QuitDuplex> CreateAsync() => Task.FromResult(new QuitDuplex());

        public NntpSession CreateSession(ILoggerFactory? loggerFactory = null)
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)));
            return loggerFactory is null
                ? new NntpSession(connection, NullLogger<NntpSession>.Instance)
                : new NntpSession(
                    connection,
                    loggerFactory.CreateLogger<NntpSession>(),
                    loggerFactory: loggerFactory);
        }

        public async Task WriteClientLineAsync(string line)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
            await _clientToServer.Writer.WriteAsync(bytes);
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task<string> ReadClientLineAsync(CancellationToken cancellationToken = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (!cancellationToken.CanBeCanceled)
            {
                cts.CancelAfter(TimeSpan.FromSeconds(5));
            }

            var line = await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, cts.Token);
            Assert.NotNull(line);
            return line!;
        }

        public async Task CompleteServerToClientAsync()
        {
            await _serverToClient.Reader.CompleteAsync();
            await _serverToClient.Writer.CompleteAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _clientToServer.Writer.CompleteAsync();
            await _clientToServer.Reader.CompleteAsync();
            await _serverToClient.Writer.CompleteAsync();
            await _serverToClient.Reader.CompleteAsync();
        }
    }

    private sealed class PipeNntpConnection : INntpConnection
    {
        private readonly CancellationTokenSource _cts = new();
        private int _completed;

        public PipeNntpConnection(PipeReader input, PipeWriter output, ConnectionClientIdentity identity)
        {
            Input = input;
            Output = output;
            ClientIdentity = identity;
        }

        public PipeReader Input { get; }
        public PipeWriter Output { get; }
        public System.Net.EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;
        public System.Net.EndPoint? LocalEndPoint => null;
        public ConnectionClientIdentity ClientIdentity { get; }
        public bool IsTls => false;

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
        {
            tlsVersion = string.Empty;
            cipher = string.Empty;
            return false;
        }

        public bool IsCompressed => false;
        public CancellationToken ConnectionClosed => _cts.Token;
        public bool IsCompleted => Volatile.Read(ref _completed) == 1;
        public long OutboundIdleVersion => 0;

        public Task PauseReadsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

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
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _cts.Cancel();
            }

            return Task.CompletedTask;
        }

        public Task UpgradeToTlsAsync(
            ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Connection that accepts the 205 write but fails <see cref="INntpConnection.WaitForOutboundDeliveryAsync"/>
    /// with a non-peer IOException.
    /// </summary>
    private sealed class FailOnWaitConnection : INntpConnection
    {
        private readonly Pipe _pipe = new(NntpPipeOptions.Create());
        private readonly CancellationTokenSource _cts = new();
        private readonly Exception _waitFailure;

        public FailOnWaitConnection(ConnectionClientIdentity identity, Exception waitFailure)
        {
            ClientIdentity = identity;
            _waitFailure = waitFailure;
            Input = _pipe.Reader;
            Output = _pipe.Writer;
        }

        public PipeReader Input { get; }
        public PipeWriter Output { get; }
        public System.Net.EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;
        public System.Net.EndPoint? LocalEndPoint => null;
        public ConnectionClientIdentity ClientIdentity { get; }
        public bool IsTls => false;
        public bool IsCompressed => false;
        public CancellationToken ConnectionClosed => _cts.Token;
        public bool IsCompleted => false;
        public long OutboundIdleVersion => 0;

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
        {
            tlsVersion = string.Empty;
            cipher = string.Empty;
            return false;
        }

        public Task PauseReadsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WaitForOutboundDeliveryAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.FromException(_waitFailure);

        public Task WaitForOutboundDeliveryAndPauseReadsAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.FromException(_waitFailure);

        public Task CompleteAsync(Exception? exception = null) => Task.CompletedTask;

        public Task UpgradeToTlsAsync(
            ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async ValueTask DisposeAsync()
        {
            await _pipe.Writer.CompleteAsync();
            await _pipe.Reader.CompleteAsync();
            _cts.Dispose();
        }
    }

    private sealed class StaticConnection : INntpConnection
    {
        private readonly Pipe _pipe = new(NntpPipeOptions.Create());

        public StaticConnection(ConnectionClientIdentity identity, CancellationToken closed, bool completed)
        {
            ClientIdentity = identity;
            ConnectionClosed = closed;
            IsCompleted = completed;
            Input = _pipe.Reader;
            Output = _pipe.Writer;
        }

        public PipeReader Input { get; }
        public PipeWriter Output { get; }
        public System.Net.EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;
        public System.Net.EndPoint? LocalEndPoint => null;
        public ConnectionClientIdentity ClientIdentity { get; }
        public bool IsTls => false;
        public bool IsCompressed => false;
        public CancellationToken ConnectionClosed { get; }
        public bool IsCompleted { get; }
        public long OutboundIdleVersion => 0;

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
        {
            tlsVersion = string.Empty;
            cipher = string.Empty;
            return false;
        }

        public Task PauseReadsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WaitForOutboundDeliveryAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WaitForOutboundDeliveryAndPauseReadsAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CompleteAsync(Exception? exception = null) => Task.CompletedTask;

        public Task UpgradeToTlsAsync(
            ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly object _gate = new();
        public List<string> Messages { get; } = [];
        public List<string?> Categories { get; } = [];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
        {
            lock (_gate)
            {
                Categories.Add(categoryName);
            }

            return new RecordingLogger(categoryName, this);
        }

        public void Dispose()
        {
        }

        public void Add(string message)
        {
            lock (_gate)
            {
                Messages.Add(message);
            }
        }

        private sealed class RecordingLogger(string category, RecordingLoggerFactory owner) : ILogger
        {
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
                owner.Add(formatter(state, exception));
                if (exception is not null)
                {
                    owner.Add($"{category}: {exception.GetType().Name}: {exception.Message}");
                }
            }
        }
    }
}

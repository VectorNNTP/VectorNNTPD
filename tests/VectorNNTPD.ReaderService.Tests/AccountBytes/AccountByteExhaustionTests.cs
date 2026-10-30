using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.SessionState.BytesAccounting;
using VectorNNTPD.Common.Networking.RateLimiting;
using VectorNNTPD.ReaderService.Authentication;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Authentication;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Tests.TestDoubles;
using VectorNNTPD.ReaderService.SessionState.RateLimiting;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.ReaderService.Tests.AccountBytes;

public sealed class AccountByteExhaustionTests
{
    [Fact]
    public async Task Authinfo_ZeroRemaining_Still281_NextCommand400AndClose()
    {
        var durable = new InMemoryAccountByteDurableStore();
        var cluster = new InMemoryAccountByteStore();
        durable.SeedByteAccount("alice", 0);
        var tracker = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        await using var duplex = new ExhaustionDuplex(CreateProvider("alice", byteLimit: 0), tracker);
        var session = duplex.CreateSession();
        var run = session.RunAsync();
        await duplex.ReadGreetingAsync();
        await duplex.WriteClientLineAsync("AUTHINFO USER alice");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.StartsWith("281 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.True(session.Authentication.IsAuthenticated);
        Assert.True(tracker.IsExhausted("alice"));

        await duplex.WriteClientLineAsync("HELP");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(run.IsCompleted);
    }

    [Fact]
    public async Task Authinfo_StaleCachedPositiveByteLimit_CannotResurrectExhaustedAccount()
    {
        var durable = new InMemoryAccountByteDurableStore();
        var cluster = new InMemoryAccountByteStore();
        durable.SeedByteAccount("alice", 0);
        await cluster.ApplyAsync("alice", "seed", 0, 0);
        var tracker = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        await using var duplex = new ExhaustionDuplex(CreateProvider("alice", byteLimit: 99_999), tracker);
        var session = duplex.CreateSession();
        var run = session.RunAsync();
        await duplex.ReadGreetingAsync();
        await duplex.WriteClientLineAsync("AUTHINFO USER alice");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.StartsWith("281 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.True(tracker.IsExhausted("alice"));
        await duplex.WriteClientLineAsync("HELP");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Authinfo_TopUpWithoutInvalidation_Stays400_UntilRedisDeleted()
    {
        var durable = new InMemoryAccountByteDurableStore();
        var cluster = new InMemoryAccountByteStore();
        durable.SeedByteAccount("alice", 0);
        await cluster.ApplyAsync("alice", "seed", 0, 0);
        var tracker = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);

        await using (var duplex = new ExhaustionDuplex(CreateProvider("alice", byteLimit: 0), tracker))
        {
            var session = duplex.CreateSession();
            var run = session.RunAsync();
            await duplex.ReadGreetingAsync();
            await duplex.WriteClientLineAsync("AUTHINFO USER alice");
            Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
            await duplex.WriteClientLineAsync("AUTHINFO PASS secret");
            Assert.StartsWith("281 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
            await duplex.WriteClientLineAsync("DATE");
            Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }

        durable.SeedByteAccount("alice", 1000);
        await using (var stillExhausted = new ExhaustionDuplex(CreateProvider("alice", byteLimit: 1000), tracker))
        {
            var session = stillExhausted.CreateSession();
            var run = session.RunAsync();
            await stillExhausted.ReadGreetingAsync();
            await stillExhausted.WriteClientLineAsync("AUTHINFO USER alice");
            await stillExhausted.ReadClientLineAsync();
            await stillExhausted.WriteClientLineAsync("AUTHINFO PASS secret");
            Assert.StartsWith("281 ", await stillExhausted.ReadClientLineAsync(), StringComparison.Ordinal);
            await stillExhausted.WriteClientLineAsync("DATE");
            Assert.Equal("400 Service temporarily unavailable", await stillExhausted.ReadClientLineAsync());
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(await tracker.DeleteAccountByteStateAsync("alice"));
        await using var reenabled = new ExhaustionDuplex(CreateProvider("alice", byteLimit: 1000), tracker);
        var live = reenabled.CreateSession();
        var liveRun = live.RunAsync();
        await reenabled.ReadGreetingAsync();
        await reenabled.WriteClientLineAsync("AUTHINFO USER alice");
        await reenabled.ReadClientLineAsync();
        await reenabled.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.StartsWith("281 ", await reenabled.ReadClientLineAsync(), StringComparison.Ordinal);
        await reenabled.WriteClientLineAsync("DATE");
        Assert.StartsWith("111 ", await reenabled.ReadClientLineAsync(), StringComparison.Ordinal);
        await reenabled.WriteClientLineAsync("QUIT");
        await reenabled.ReadClientLineAsync();
        await liveRun;
    }

    [Fact]
    public async Task ExactZeroAfterReconcile_RejectsNextCommandOnAllLocalSessions()
    {
        var durable = new InMemoryAccountByteDurableStore();
        var cluster = new InMemoryAccountByteStore();
        durable.SeedByteAccount("alice", 100);
        var tracker = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        tracker.CreateSink("alice").ObserveCopied(100);
        await tracker.ReconcileAsync();
        Assert.True(tracker.IsExhausted("alice"));
        Assert.Equal(0, durable.Remaining("alice"));

        await using var duplex = new ExhaustionDuplex(CreateProvider("alice", byteLimit: 100), tracker);
        var session = duplex.CreateSession();
        var run = session.RunAsync();
        await duplex.ReadGreetingAsync();
        await duplex.WriteClientLineAsync("AUTHINFO USER alice");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.StartsWith("281 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("DATE");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task InFlightResponse_IsNotTruncated_WhenExhaustionIsMarkedAfterWriteStarts()
    {
        var durable = new InMemoryAccountByteDurableStore();
        var cluster = new InMemoryAccountByteStore();
        durable.SeedByteAccount("alice", 10_000);
        var tracker = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        var pipe = new Pipe();
        await using var writer = new NntpResponseWriter(pipe.Writer);
        writer.SetByteSink(tracker.CreateSink("alice"));
        await writer.WriteMultilineStartAsync(100, "Help text follows");
        tracker.MarkExhausted("alice");
        await writer.WriteMultilineDataAsync("line-one");
        await writer.WriteMultilineEndAsync();
        var expected = "100 Help text follows\r\nline-one\r\n.\r\n";
        var buffer = new byte[expected.Length];
        var read = 0;
        while (read < expected.Length)
        {
            var result = await pipe.Reader.ReadAsync();
            var slice = result.Buffer.Slice(0, Math.Min(result.Buffer.Length, expected.Length - read));
            slice.CopyTo(buffer.AsSpan(read, (int)slice.Length));
            read += (int)slice.Length;
            pipe.Reader.AdvanceTo(slice.End);
        }

        Assert.Equal(expected, Encoding.ASCII.GetString(buffer));
        Assert.True(tracker.IsExhausted("alice"));
        await pipe.Reader.CompleteAsync();
    }

    [Fact]
    public async Task Authinfo_ZeroBytes_2400Bps_AttachesBoth_WithoutWriteOnObserve()
    {
        var durable = new InMemoryAccountByteDurableStore();
        var cluster = new InMemoryAccountByteStore();
        durable.SeedByteAccount("alice", 0);
        var bytes = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        var rates = new AccountRateAllocator();
        var admission = new InMemorySessionStateTracker(rates);
        var cap = new FakeCap();
        await using var duplex = new ExhaustionDuplex(
            CreateProvider("alice", byteLimit: 0, rateLimitBps: 2_400),
            bytes);
        var session = duplex.CreateSession(admission, rates, cap);
        var run = session.RunAsync();
        await duplex.ReadGreetingAsync();
        await duplex.WriteClientLineAsync("AUTHINFO USER alice");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.StartsWith("281 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.True(session.Authentication.IsAuthenticated);
        Assert.True(bytes.IsExhausted("alice"));
        Assert.Equal(2_400, session.AccountPolicy!.RateLimitBps);
        Assert.Equal(300, cap.MaxSendBytesPerSecond);
        Assert.Equal(0, cluster.ApplyCalls);
        Assert.Equal(0, durable.ConsumeCalls);
        Assert.True(cluster.ObserveCalls >= 1);
        Assert.True(durable.QueryCalls >= 1);

        await duplex.WriteClientLineAsync("HELP");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        Assert.Equal(300, cap.MaxSendBytesPerSecond);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ZeroBytes_PositiveRate_Still281Then400()
    {
        var durable = new InMemoryAccountByteDurableStore();
        var cluster = new InMemoryAccountByteStore();
        durable.SeedByteAccount("alice", 0);
        var tracker = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        await using var duplex = new ExhaustionDuplex(
            CreateProvider("alice", byteLimit: 0, rateLimitBps: 10_000_000),
            tracker);
        var session = duplex.CreateSession();
        var run = session.RunAsync();
        await duplex.ReadGreetingAsync();
        await duplex.WriteClientLineAsync("AUTHINFO USER alice");
        await duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.StartsWith("281 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.True(tracker.IsExhausted("alice"));
        Assert.Equal(10_000_000, session.AccountPolicy!.RateLimitBps);
        await duplex.WriteClientLineAsync("DATE");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static INntpAuthenticationProvider CreateProvider(
        string user,
        long byteLimit,
        int rateLimitBps = 0)
    {
        var record = MemoryNntpUserRecordStore.Create(user, "secret", rateLimitBps: rateLimitBps, byteLimit: byteLimit);
        var policy = NntpAccountPolicy.FromRecord(record);
        return new ScriptedProvider(user, policy);
    }

    private sealed class ScriptedProvider : INntpAuthenticationProvider
    {
        private readonly string _user;
        private readonly NntpAccountPolicy _policy;

        public ScriptedProvider(string user, NntpAccountPolicy policy)
        {
            _user = user;
            _policy = policy;
        }

        public ValueTask<NntpAuthenticationResult> AuthenticateAsync(
            string username,
            string password,
            CancellationToken cancellationToken = default)
        {
            if (username == _user && password == "secret")
            {
                return ValueTask.FromResult(NntpAuthenticationResult.Success(
                    username,
                    new NntpAuthorization(
                        isAuthenticated: true,
                        authorizedReader: true,
                        authorizedTransit: false,
                        postingPermitted: true,
                        streamingPermitted: false),
                    _policy));
            }

            return ValueTask.FromResult(NntpAuthenticationResult.Failed);
        }
    }

    private sealed class ExhaustionDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());
        private readonly INntpAuthenticationProvider _provider;
        private readonly IAccountByteAccountant _accountant;

        public ExhaustionDuplex(INntpAuthenticationProvider provider, IAccountByteAccountant accountant)
        {
            _provider = provider;
            _accountant = accountant;
        }

        public NntpSession CreateSession(
            ISessionStateTracker? admission = null,
            IAccountRateAllocator? rates = null,
            IOutboundRateCap? outboundRate = null)
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)),
                outboundRate);
            return new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                authenticationProvider: _provider,
                sessionAdmission: admission,
                accountBytes: _accountant,
                accountRates: rates);
        }

        public async Task ReadGreetingAsync()
        {
            var line = await ReadClientLineAsync();
            Assert.StartsWith("201 ", line, StringComparison.Ordinal);
        }

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
    }

    private sealed class PipeNntpConnection : INntpConnection
    {
        private readonly CancellationTokenSource _cts = new();
        private int _compressed;

        public PipeNntpConnection(
            PipeReader input,
            PipeWriter output,
            ConnectionClientIdentity identity,
            IOutboundRateCap? outboundRate = null)
        {
            Input = input;
            Output = output;
            ClientIdentity = identity;
            OutboundRate = outboundRate;
        }

        public IOutboundRateCap? OutboundRate { get; }

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

        public bool IsCompressed => Volatile.Read(ref _compressed) == 1;

        public CancellationToken ConnectionClosed => _cts.Token;

        public bool IsCompleted => _cts.IsCancellationRequested;

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
            _cts.Cancel();
            return Task.CompletedTask;
        }

        public Task UpgradeToTlsAsync(
            ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _compressed, 1);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCap : IOutboundRateCap
    {
        public long MaxSendBytesPerSecond { get; private set; }

        public void UpdateMaxSendBytesPerSecond(long bytesPerSecond) => MaxSendBytesPerSecond = bytesPerSecond;
    }
}

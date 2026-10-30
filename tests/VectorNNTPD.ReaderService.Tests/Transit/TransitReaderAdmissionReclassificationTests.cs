using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.Authentication;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Authentication;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.Tests.Session;
using VectorNNTPD.ReaderService.Tests.TestDoubles;
using VectorNNTPD.ReaderService.Transit;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.ReaderService.Tests.Transit;

/// <summary>
/// AllowFrom identifies a connection as a named Transit peer before AUTHINFO.
/// READER AUTHINFO replaces that identity and must release the TransitPeerState slot.
/// </summary>
[Collection(nameof(NntpCommandLoggerCollection))]
public sealed class TransitReaderAdmissionReclassificationTests
{
    private static readonly IPAddress AllowFromClient = IPAddress.Parse("198.18.0.70");
    private const string PeerId = "usenet-ninja";

    [Fact]
    public async Task ReaderFromAllowFrom_DoesNotKeepTransitSlot()
    {
        var store = CreateStore(maxIncoming: 10);
        var (limiter, tracker, membership) = TransitPeerStateTestFactory.CreateLimiter(store);
        var admission = new InMemorySessionStateTracker();
        await using var duplex = new AuthDuplex(AllowFromClient);
        var session = duplex.CreateSession(store, admission);
        var lease = await AdmitAsync(limiter, session);
        Assert.True(lease.IsHeld);
        Assert.Equal(1, tracker.GetLocalCount(PeerId));
        Assert.Equal(1, membership.ActiveCount(PeerId, TransitPeerStateTestFactory.NowMs()));

        var run = session.RunAsync();
        await duplex.ReadGreetingAsync();
        await duplex.AuthenticateReaderAsync("a", "a");

        Assert.True(session.Authentication.IsAuthenticated);
        Assert.Equal("a", session.Authentication.Username);
        Assert.True(session.Authorization.AuthorizedReader);
        Assert.False(session.Authorization.AuthorizedTransit);
        Assert.Null(session.Authorization.TransitPeerName);
        Assert.False(lease.IsHeld);
        Assert.Equal(0, tracker.GetLocalCount(PeerId));
        Assert.Equal(0, membership.ActiveCount(PeerId, TransitPeerStateTestFactory.NowMs()));
        Assert.Equal(
            SessionAdmissionResult.SessionLimitExceeded,
            admission.TryAdmit("a", "other", AllowFromClient, sessionLimit: 1, srcIpLimit: 0));

        await duplex.QuitAsync(run);
        await lease.DisposeAsync();
        Assert.Equal(0, tracker.GetLocalCount(PeerId));
    }

    [Fact]
    public async Task GenuineTransit_WithoutAuthinfo_KeepsSlotAndEnforcesLimit()
    {
        var store = CreateStore(maxIncoming: 1);
        var (limiter, tracker, membership) = TransitPeerStateTestFactory.CreateLimiter(store);
        await using var firstDuplex = new AuthDuplex(AllowFromClient);
        var first = firstDuplex.CreateSession(store);
        var lease1 = await AdmitAsync(limiter, first);
        Assert.True(lease1.IsHeld);
        Assert.Equal(1, tracker.GetLocalCount(PeerId));

        var firstRun = first.RunAsync();
        await firstDuplex.ReadGreetingAsync();
        Assert.Equal(PeerId, first.Authorization.TransitPeerName);
        Assert.True(first.Authorization.AuthorizedTransit);
        Assert.False(first.Authentication.IsAuthenticated);

        await using var blockedDuplex = new AuthDuplex(IPAddress.Parse("198.18.0.71"));
        var blocked = blockedDuplex.CreateSession(store);
        var lease2 = await TransitConnectionAdmission.TryAdmitAsync(limiter, blocked);
        Assert.False(lease2.Admitted);
        Assert.False(lease2.Lease.IsHeld);
        Assert.Equal(1, tracker.GetLocalCount(PeerId));
        Assert.Equal(1, membership.ActiveCount(PeerId, TransitPeerStateTestFactory.NowMs()));

        await firstDuplex.QuitAsync(firstRun);
        await lease1.DisposeAsync();
        Assert.Equal(0, tracker.GetLocalCount(PeerId));

        await using var reusedDuplex = new AuthDuplex(IPAddress.Parse("198.18.0.72"));
        var reused = reusedDuplex.CreateSession(store);
        var lease3 = await AdmitAsync(limiter, reused);
        Assert.True(lease3.IsHeld);
        Assert.Equal(1, tracker.GetLocalCount(PeerId));
        await lease3.DisposeAsync();
        Assert.Equal(0, tracker.GetLocalCount(PeerId));
    }

    [Fact]
    public async Task GenuineTransit_ReaderRole_PeerAuthinfoDoesNotAuthenticate_AndKeepsSlot()
    {
        var store = CreateStore(maxIncoming: 1, transitUsername: "feed", transitPassword: "peer-secret");
        var (limiter, tracker, _) = TransitPeerStateTestFactory.CreateLimiter(store);
        await using var duplex = new AuthDuplex(AllowFromClient);
        var session = duplex.CreateSession(store);
        var lease = await AdmitAsync(limiter, session);
        var run = session.RunAsync();
        await duplex.ReadGreetingAsync();
        await duplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("502 Command not available on this server", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("AUTHINFO USER feed");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS peer-secret");
        Assert.Equal("481 Authentication failed", await duplex.ReadClientLineAsync());

        // Reader role: peer credentials never authenticate and never reclassify the admission slot.
        Assert.False(session.Authentication.IsAuthenticated);
        Assert.False(session.Authorization.AuthorizedTransit);
        Assert.Equal(PeerId, session.Authorization.TransitPeerName);
        Assert.True(lease.IsHeld);
        Assert.Equal(1, tracker.GetLocalCount(PeerId));

        await duplex.QuitAsync(run);
        await lease.DisposeAsync();
        Assert.Equal(0, tracker.GetLocalCount(PeerId));
    }

    [Fact]
    public async Task ReadersFromAllowFrom_DoNotStarveTransitCapacity()
    {
        var store = CreateStore(maxIncoming: 1);
        var (limiter, tracker, membership) = TransitPeerStateTestFactory.CreateLimiter(store);
        var admission = new InMemorySessionStateTracker();
        for (var i = 0; i < 3; i++)
        {
            await using var readerDuplex = new AuthDuplex(AllowFromClient);
            var reader = readerDuplex.CreateSession(store, admission, username: "a", password: "a");
            var readerLease = await AdmitAsync(limiter, reader);
            var readerRun = reader.RunAsync();
            await readerDuplex.ReadGreetingAsync();
            await readerDuplex.AuthenticateReaderAsync("a", "a");
            Assert.Null(reader.Authorization.TransitPeerName);
            Assert.False(readerLease.IsHeld);
            Assert.Equal(0, tracker.GetLocalCount(PeerId));
            await readerDuplex.QuitAsync(readerRun);
            await readerLease.DisposeAsync();
        }

        await using var transitDuplex = new AuthDuplex(IPAddress.Parse("198.18.0.80"));
        var transit = transitDuplex.CreateSession(store);
        var transitLease = await AdmitAsync(limiter, transit);
        Assert.True(transitLease.IsHeld);
        Assert.Equal(1, tracker.GetLocalCount(PeerId));
        Assert.Equal(1, membership.ActiveCount(PeerId, TransitPeerStateTestFactory.NowMs()));
        await transitLease.DisposeAsync();
    }

    [Fact]
    public async Task FailedReaderAuthinfo_KeepsTransitSlotUntilDisconnect()
    {
        var store = CreateStore(maxIncoming: 1);
        var (limiter, tracker, membership) = TransitPeerStateTestFactory.CreateLimiter(store);
        await using var duplex = new AuthDuplex(AllowFromClient);
        var session = duplex.CreateSession(store);
        var lease = await AdmitAsync(limiter, session);
        var run = session.RunAsync();
        await duplex.ReadGreetingAsync();
        await duplex.WriteClientLineAsync("AUTHINFO USER a");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS wrong");
        Assert.Equal("481 Authentication failed", await duplex.ReadClientLineAsync());

        Assert.False(session.Authentication.IsAuthenticated);
        Assert.True(session.Authorization.AuthorizedTransit);
        Assert.Equal(PeerId, session.Authorization.TransitPeerName);
        Assert.True(lease.IsHeld);
        Assert.Equal(1, tracker.GetLocalCount(PeerId));
        Assert.Equal(1, membership.ActiveCount(PeerId, TransitPeerStateTestFactory.NowMs()));

        await duplex.QuitAsync(run);
        await lease.DisposeAsync();
        Assert.False(lease.IsHeld);
        Assert.Equal(0, tracker.GetLocalCount(PeerId));
    }

    [Fact]
    public async Task AllowFromDisconnectBeforeAuthinfo_ReleasesSlotOnce()
    {
        var store = CreateStore(maxIncoming: 1);
        var (limiter, tracker, _) = TransitPeerStateTestFactory.CreateLimiter(store);
        await using var duplex = new AuthDuplex(AllowFromClient);
        var session = duplex.CreateSession(store);
        var lease = await AdmitAsync(limiter, session);
        Assert.Equal(1, tracker.GetLocalCount(PeerId));
        var run = session.RunAsync();
        await duplex.ReadGreetingAsync();
        await duplex.QuitAsync(run);
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        Assert.Equal(0, tracker.GetLocalCount(PeerId));
    }

    [Fact]
    public async Task AmbiguousAllowFrom_DoesNotConsumeSlot_AndReaderAuthSucceeds()
    {
        var options = new TransitPeersOptions
        {
            ["one"] = TransitTestPeers.Peer(maxIncoming: 1, allowFrom: ["198.18.0.0/15"]),
            ["two"] = TransitTestPeers.Peer(maxIncoming: 1, allowFrom: ["198.18.0.0/15"]),
        };
        var store = new TransitConfigurationStore();
        store.Replace(TransitConfigurationSnapshot.Create(options));
        var (limiter, _, membership) = TransitPeerStateTestFactory.CreateLimiter(store);
        await using var duplex = new AuthDuplex(AllowFromClient);
        var session = duplex.CreateSession(store);
        Assert.Null(session.Authorization.TransitPeerName);
        var admitted = await TransitConnectionAdmission.TryAdmitAsync(limiter, session);
        Assert.True(admitted.Admitted);
        Assert.False(admitted.Lease.IsHeld);
        session.AttachTransitAdmission(admitted.Lease);
        var run = session.RunAsync();
        await duplex.ReadGreetingAsync();
        await duplex.AuthenticateReaderAsync("a", "a");
        Assert.True(session.Authorization.AuthorizedReader);
        Assert.Null(session.Authorization.TransitPeerName);
        Assert.Equal(0, membership.ActiveCount("one", TransitPeerStateTestFactory.NowMs()));
        Assert.Equal(0, membership.ActiveCount("two", TransitPeerStateTestFactory.NowMs()));
        await duplex.QuitAsync(run);
    }

    [Fact]
    public async Task ReaderReclassification_RenewalDoesNotRecreateSlot()
    {
        var store = CreateStore(maxIncoming: 10);
        var (limiter, tracker, membership) = TransitPeerStateTestFactory.CreateLimiter(store);
        await using var duplex = new AuthDuplex(AllowFromClient);
        var session = duplex.CreateSession(store);
        var lease = await AdmitAsync(limiter, session);
        var run = session.RunAsync();
        await duplex.ReadGreetingAsync();
        await duplex.AuthenticateReaderAsync("a", "a");
        Assert.Equal(0, tracker.GetLocalCount(PeerId));
        await tracker.RenewLeasesAsync(CancellationToken.None);
        Assert.Equal(0, tracker.GetLocalCount(PeerId));
        Assert.Equal(0, membership.ActiveCount(PeerId, TransitPeerStateTestFactory.NowMs()));
        await duplex.QuitAsync(run);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task InFlightRenewal_CannotRecreateSlotAfterReaderReclassification()
    {
        var store = CreateStore(maxIncoming: 10);
        var (limiter, tracker, membership) = TransitPeerStateTestFactory.CreateLimiter(store);
        membership.NotifyRenewStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        membership.BlockRenew = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var duplex = new AuthDuplex(AllowFromClient);
        var session = duplex.CreateSession(store);
        var lease = await AdmitAsync(limiter, session);
        var run = session.RunAsync();
        await duplex.ReadGreetingAsync();
        Assert.Equal(1, tracker.GetLocalCount(PeerId));

        var renew = tracker.RenewLeasesAsync(CancellationToken.None).AsTask();
        await membership.NotifyRenewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await duplex.AuthenticateReaderAsync("a", "a");
        Assert.Null(session.Authorization.TransitPeerName);
        Assert.Equal(0, tracker.GetLocalCount(PeerId));
        Assert.Equal(0, membership.ActiveCount(PeerId, TransitPeerStateTestFactory.NowMs()));

        membership.BlockRenew.TrySetResult();
        await renew.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, tracker.GetLocalCount(PeerId));
        Assert.Equal(0, membership.ActiveCount(PeerId, TransitPeerStateTestFactory.NowMs()));
        Assert.Equal(1, tracker.ReleaseCalls);

        await duplex.QuitAsync(run);
        await lease.DisposeAsync();
        Assert.Equal(1, tracker.ReleaseCalls);
    }

    [Fact]
    public async Task ReaderAuthinfoRelease_ConcurrentTeardownDispose_ReleasesOnce()
    {
        var store = CreateStore(maxIncoming: 10);
        var (limiter, tracker, membership) = TransitPeerStateTestFactory.CreateLimiter(store);
        membership.BlockRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var duplex = new AuthDuplex(AllowFromClient);
        var session = duplex.CreateSession(store);
        var lease = await AdmitAsync(limiter, session);
        var run = session.RunAsync();
        await duplex.ReadGreetingAsync();
        await duplex.WriteClientLineAsync("AUTHINFO USER a");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS a");
        await WaitUntilAsync(() => session.Authorization.AuthorizedReader);

        Assert.Null(session.Authorization.TransitPeerName);
        Assert.False(lease.IsHeld);
        Assert.Equal(0, tracker.GetLocalCount(PeerId));
        Assert.Equal(0, membership.ReleaseCalls);

        var teardown = lease.DisposeAsync();
        membership.BlockRelease.TrySetResult();
        await teardown;
        Assert.StartsWith("281 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.Equal(1, tracker.ReleaseCalls);
        Assert.Equal(1, membership.ReleaseCalls);
        Assert.Equal(0, membership.ActiveCount(PeerId, TransitPeerStateTestFactory.NowMs()));

        await duplex.QuitAsync(run);
        await lease.DisposeAsync();
        Assert.Equal(1, tracker.ReleaseCalls);
    }

    private static TransitConfigurationStore CreateStore(
        int maxIncoming,
        string transitUsername = "",
        string transitPassword = "")
    {
        var store = new TransitConfigurationStore();
        store.Replace(
            TransitTestPeers.Snapshot(
                PeerId,
                TransitTestPeers.Peer(
                    maxIncoming: maxIncoming,
                    allowFrom: ["198.18.0.0/15"],
                    username: transitUsername,
                    password: transitPassword)));
        return store;
    }

    private static async Task<TransitInboundConnectionLease> AdmitAsync(
        TransitInboundConnectionLimiter limiter,
        NntpSession session)
    {
        var outcome = await TransitConnectionAdmission.TryAdmitAsync(limiter, session);
        Assert.True(outcome.Admitted);
        session.AttachTransitAdmission(outcome.Lease);
        return outcome.Lease;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(1, cts.Token);
        }
    }

    private sealed class AuthDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());
        private readonly IPAddress _clientIp;

        public AuthDuplex(IPAddress clientIp) => _clientIp = clientIp;

        public NntpSession CreateSession(
            TransitConfigurationStore store,
            ISessionStateTracker? admission = null,
            string username = "a",
            string password = "a")
        {
            var users = new MemoryNntpUserRecordStore();
            users.Add(MemoryNntpUserRecordStore.Create(username, password, sessionLimit: 1));
            var validator = new MySqlNntpCredentialValidator(users, NullLogger<MySqlNntpCredentialValidator>.Instance);
            var provider = new CompositeNntpAuthenticationProvider(
                DenyAllNntpAuthenticationProvider.Instance,
                newsmasterUsername: null,
                validator);
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new IPEndPoint(_clientIp, 119)));
            return new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                authenticationProvider: provider,
                transitPeerAuthorization: TransitPeerAuthorization.CreateForStore(store),
                sessionAdmission: admission);
        }

        public async Task AuthenticateReaderAsync(string username, string password)
        {
            await WriteClientLineAsync($"AUTHINFO USER {username}");
            Assert.StartsWith("381 ", await ReadClientLineAsync(), StringComparison.Ordinal);
            await WriteClientLineAsync($"AUTHINFO PASS {password}");
            Assert.StartsWith("281 ", await ReadClientLineAsync(), StringComparison.Ordinal);
        }

        public async Task ReadGreetingAsync()
        {
            var line = await ReadClientLineAsync();
            Assert.StartsWith("20", line, StringComparison.Ordinal);
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

        public async Task QuitAsync(Task run)
        {
            await WriteClientLineAsync("QUIT");
            await ReadClientLineAsync();
            await run;
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

        public PipeNntpConnection(PipeReader input, PipeWriter output, ConnectionClientIdentity identity)
        {
            Input = input;
            Output = output;
            ClientIdentity = identity;
        }

        public PipeReader Input { get; }

        public PipeWriter Output { get; }

        public ConnectionClientIdentity ClientIdentity { get; }

        public EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;

        public EndPoint? LocalEndPoint => null;

        public bool IsTls => false;

        public bool IsCompressed => false;

        public bool IsCompleted => _cts.IsCancellationRequested;

        public long OutboundIdleVersion => 0;

        public CancellationToken ConnectionClosed => _cts.Token;

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

        public Task CompleteAsync(Exception? exception = null)
        {
            _cts.Cancel();
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
}

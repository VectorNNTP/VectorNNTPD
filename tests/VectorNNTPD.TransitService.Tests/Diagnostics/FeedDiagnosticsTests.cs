using System.IO.Pipelines;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Diagnostics;
using VectorNNTPD.TransitService.History;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.Networking.Transport;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Tests.Transit;
using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.Tests.Diagnostics;

public sealed class FeedDiagnosticsTests
{
    [Fact]
    public void Disabled_IsNoOp()
    {
        var feed = NullFeedDiagnostics.Instance;
        Assert.False(feed.IsEnabled);
        feed.OnRejected("giganews", "1.2.3.4:119");
        Assert.Null(feed.OnAccepted(CreateSession("192.0.2.10")));
        feed.OnReleased(null);
        var snapshot = feed.CaptureSnapshot(
            DisabledArticleIngestionQueue.Instance,
            new TransitConfigurationStore());
        Assert.Same(FeedDiagnosticsSnapshot.Empty, snapshot);
    }

    [Fact]
    public void AcceptRelease_AdjustsActiveAndPeak()
    {
        var hub = new FeedDiagnosticsHub();
        var first = hub.OnAccepted(CreateSession("192.0.2.10"));
        var second = hub.OnAccepted(CreateSession("192.0.2.11"));
        Assert.NotNull(first);
        Assert.NotNull(second);

        var store = CreateStore(maxIncoming: 10);
        var mid = hub.CaptureSnapshot(DisabledArticleIngestionQueue.Instance, store);
        var peer = Assert.Single(mid.Peers);
        Assert.Equal(TransitTestPeers.DefaultPeerName, peer.PeerName);
        Assert.Equal(2, peer.ActiveConnections);
        Assert.Equal(2, peer.PeakConnections);
        Assert.Equal(2, peer.AcceptedConnections);
        Assert.Equal(0, peer.RejectedConnections);
        Assert.Equal(10, peer.ConfiguredMaxConnections);

        hub.OnReleased(first);
        var after = hub.CaptureSnapshot(DisabledArticleIngestionQueue.Instance, store);
        peer = Assert.Single(after.Peers);
        Assert.Equal(1, peer.ActiveConnections);
        Assert.Equal(2, peer.PeakConnections);
        Assert.Equal(2, peer.AcceptedConnections);
    }

    [Fact]
    public void Reject_IncrementsWithoutActivating()
    {
        var hub = new FeedDiagnosticsHub();
        for (var i = 0; i < 10; i++)
        {
            Assert.NotNull(hub.OnAccepted(CreateSession("192.0.2.10")));
        }

        hub.OnRejected(TransitTestPeers.DefaultPeerName, "192.0.2.99:1");
        var snapshot = hub.CaptureSnapshot(DisabledArticleIngestionQueue.Instance, CreateStore(maxIncoming: 10));
        var peer = Assert.Single(snapshot.Peers);
        Assert.Equal(10, peer.ActiveConnections);
        Assert.Equal(10, peer.PeakConnections);
        Assert.Equal(10, peer.AcceptedConnections);
        Assert.Equal(1, peer.RejectedConnections);
    }

    [Fact]
    public void Release_DoesNotGoNegative()
    {
        var hub = new FeedDiagnosticsHub();
        var probe = hub.OnAccepted(CreateSession("192.0.2.10"));
        hub.OnReleased(probe);
        hub.OnReleased(probe);
        var snapshot = hub.CaptureSnapshot(DisabledArticleIngestionQueue.Instance, CreateStore(maxIncoming: 10));
        var peer = Assert.Single(snapshot.Peers);
        Assert.Equal(0, peer.ActiveConnections);
        Assert.Equal(1, peer.PeakConnections);
    }

    [Fact]
    public void Peak_IsMonotonic()
    {
        var hub = new FeedDiagnosticsHub();
        var a = hub.OnAccepted(CreateSession("192.0.2.10"));
        var b = hub.OnAccepted(CreateSession("192.0.2.11"));
        var c = hub.OnAccepted(CreateSession("192.0.2.12"));
        hub.OnReleased(a);
        hub.OnReleased(b);
        _ = hub.OnAccepted(CreateSession("192.0.2.13"));
        var snapshot = hub.CaptureSnapshot(DisabledArticleIngestionQueue.Instance, CreateStore(maxIncoming: 10));
        Assert.Equal(3, Assert.Single(snapshot.Peers).PeakConnections);
        Assert.Equal(2, Assert.Single(snapshot.Peers).ActiveConnections);
        Assert.NotNull(c);
    }

    [Fact]
    public void PerPeerIsolation_DoesNotShareSlots()
    {
        var hub = new FeedDiagnosticsHub();
        var store = new TransitConfigurationStore();
        store.Replace(
            new TransitConfigurationSnapshot(
                new Dictionary<string, TransitPeerPolicy>(StringComparer.Ordinal)
                {
                    ["alpha"] = TransitConfigurationSnapshot.CreatePeer(
                        "alpha",
                        TransitTestPeers.Peer(maxIncoming: 1, allowFrom: ["192.0.2.10"], peerName: "A")),
                    ["beta"] = TransitConfigurationSnapshot.CreatePeer(
                        "beta",
                        TransitTestPeers.Peer(maxIncoming: 8, allowFrom: ["198.51.100.1"], peerName: "B")),
                }));

        var alphaAuth = TransitPeerAuthorization.CreateForStore(store);
        var alpha = hub.OnAccepted(CreateSession(alphaAuth, IPAddress.Parse("192.0.2.10")));
        hub.OnRejected("alpha", "192.0.2.10:2");
        var beta = hub.OnAccepted(CreateSession(alphaAuth, IPAddress.Parse("198.51.100.1")));
        Assert.NotNull(alpha);
        Assert.NotNull(beta);

        var snapshot = hub.CaptureSnapshot(DisabledArticleIngestionQueue.Instance, store);
        var alphaRow = Assert.Single(snapshot.Peers, p => p.PeerName == "alpha");
        var betaRow = Assert.Single(snapshot.Peers, p => p.PeerName == "beta");
        Assert.Equal(1, alphaRow.ActiveConnections);
        Assert.Equal(1, alphaRow.RejectedConnections);
        Assert.Equal(1, betaRow.ActiveConnections);
        Assert.Equal(0, betaRow.RejectedConnections);
    }

    [Fact]
    public void Probe_ArticleAndHistoryCounters()
    {
        var probe = new FeedSessionProbe("192.0.2.10:40000", "giganews");
        probe.RecordCommand();
        probe.SetState(FeedSessionState.Receiving);
        probe.RecordArticleReceived(768_000, 1_000);
        probe.RecordHistory(HistoryLookupResult.Unseen, 500);
        probe.RecordQueue(ArticleEnqueueResult.Accepted, 200, 768_000);
        probe.RecordArticleCompleted(duplicate: false, responseTicks: 100);

        Assert.Equal(1, probe.Commands);
        Assert.Equal(1, probe.ArticlesReceived);
        Assert.Equal(1, probe.ArticlesCompleted);
        Assert.Equal(768_000, probe.ArticleBytes);
        Assert.Equal(1, probe.HistoryMisses);
        Assert.Equal(0, probe.HistoryHits);
        Assert.Equal(1, probe.QueueAccepted);
        Assert.Equal(768_000, probe.QueueAdmittedBytes);
        Assert.Equal(FeedSessionState.Idle, probe.State);
        Assert.True(probe.ReceiveTime > TimeSpan.Zero || probe.ArticleBytes == 768_000);
    }

    [Fact]
    public void Probe_StateTransitions_DoNotCorruptCounters()
    {
        var probe = new FeedSessionProbe("192.0.2.10:40000", "giganews");
        probe.RecordArticleReceived(100, 10);
        probe.SetState(FeedSessionState.WaitingHistory);
        probe.SetState(FeedSessionState.WaitingQueue);
        probe.SetState(FeedSessionState.Completing);
        probe.SetState(FeedSessionState.Idle);
        probe.SetState(FeedSessionState.Receiving);

        Assert.Equal(1, probe.ArticlesReceived);
        Assert.Equal(100, probe.ArticleBytes);
        Assert.Equal(0, probe.ArticlesCompleted);
        Assert.Equal(0, probe.HistoryHits);
        Assert.Equal(0, probe.HistoryMisses);
        Assert.Equal(0, probe.QueueAccepted);
        Assert.Equal(FeedSessionState.Receiving, probe.State);

        probe.RecordArticleCompleted(duplicate: false, responseTicks: 1);
        Assert.Equal(1, probe.ArticlesReceived);
        Assert.Equal(1, probe.ArticlesCompleted);
        Assert.Equal(100, probe.ArticleBytes);
        Assert.Equal(FeedSessionState.Idle, probe.State);
    }

    [Fact]
    public void Snapshot_IsInternallyConsistent()
    {
        var hub = new FeedDiagnosticsHub();
        var session = CreateSession("192.0.2.10");
        var probe = hub.OnAccepted(session);
        Assert.NotNull(probe);
        session.FeedProbe = probe;
        probe.RecordArticleReceived(1000, 10);
        probe.RecordHistory(HistoryLookupResult.Seen, 5);
        probe.RecordArticleCompleted(duplicate: true, responseTicks: 1);

        var snapshot = hub.CaptureSnapshot(DisabledArticleIngestionQueue.Instance, CreateStore(maxIncoming: 50));
        var peer = Assert.Single(snapshot.Peers);
        var row = Assert.Single(snapshot.Sessions);
        Assert.Equal(peer.ActiveConnections, snapshot.Sessions.Count);
        Assert.Equal(probe.ArticlesCompleted, peer.ArticlesCompleted);
        Assert.Equal(probe.ArticleBytes, peer.ArticleBytes);
        Assert.Equal(probe.ArticlesCompleted, row.ArticlesCompleted);
        Assert.Equal("192.0.2.10:40000", row.Remote);
        Assert.Equal(1, snapshot.HistoryHits);
        Assert.Equal(0, snapshot.HistoryMisses);
    }

    [Fact]
    public void Formatter_DoesNotLeakSecretsOrPayloads()
    {
        var snapshot = new FeedDiagnosticsSnapshot
        {
            CapturedAt = DateTimeOffset.UtcNow,
            Interval = TimeSpan.FromSeconds(10),
            Peers =
            [
                new FeedPeerSnapshot
                {
                    PeerName = "giganews",
                    ConfiguredMaxConnections = 50,
                    ActiveConnections = 50,
                    PeakConnections = 50,
                    AcceptedConnections = 50,
                    RejectedConnections = 0,
                    ArticlesCompleted = 12,
                    ArticleBytes = 9_000_000,
                },
            ],
            Sessions =
            [
                new FeedSessionSnapshot
                {
                    Remote = "69.80.99.14:119",
                    PeerName = "giganews",
                    Age = TimeSpan.FromMinutes(3),
                    State = FeedSessionState.Receiving,
                    ArticlesCompleted = 4,
                    ArticleBytes = 3_000_000,
                },
            ],
            IntervalArticles = 12,
            IntervalBytes = 9_000_000,
        };

        var text = FeedDiagnosticsFormatter.Format(snapshot, includeSessions: true);
        Assert.Contains("giganews", text, StringComparison.Ordinal);
        Assert.Contains("69.80.99.14:119", text, StringComparison.Ordinal);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AUTHINFO", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Message-ID", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stage routing status=not_implemented", text, StringComparison.Ordinal);
        Assert.Contains("stage rx_app", text, StringComparison.Ordinal);
        Assert.Contains("stage ingest", text, StringComparison.Ordinal);
        Assert.Contains("stage process", text, StringComparison.Ordinal);
        Assert.Contains("stage spool", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RateCalculator_ByteDelta_ProducesCorrectMbps()
    {
        var elapsed = TimeSpan.FromSeconds(2);
        var mbps = FeedRateCalculator.MegabitsPerSecond(125_000_000, elapsed);
        Assert.Equal(500d, mbps, precision: 6);
        Assert.Equal(12.5d, FeedRateCalculator.PerSecond(25, elapsed), precision: 6);
    }

    [Fact]
    public void RateCalculator_ZeroElapsed_IsZero()
    {
        Assert.Equal(0, FeedRateCalculator.PerSecond(1_000, TimeSpan.Zero));
        Assert.Equal(0, FeedRateCalculator.MegabitsPerSecond(1_000_000, TimeSpan.Zero));
        Assert.Equal(0, FeedRateCalculator.PerSecond(1_000, TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void RateCalculator_CounterReset_DoesNotProduceNegativeRates()
    {
        Assert.Equal(0, FeedRateCalculator.NonNegativeDelta(10, 20));
        Assert.Equal(0, FeedRateCalculator.PerSecond(FeedRateCalculator.NonNegativeDelta(10, 20), TimeSpan.FromSeconds(1)));
        Assert.Equal(0, FeedRateCalculator.MegabitsPerSecond(FeedRateCalculator.NonNegativeDelta(5, 50), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void RateCalculator_ZeroActivity_IsZero()
    {
        Assert.Equal(0, FeedRateCalculator.NonNegativeDelta(42, 42));
        Assert.Equal(0, FeedRateCalculator.PerSecond(0, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, FeedRateCalculator.MegabitsPerSecond(0, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Snapshot_IntervalDeltas_AreMonotonicAndAggregated()
    {
        var hub = new FeedDiagnosticsHub();
        var session = CreateSession("192.0.2.10");
        var probe = hub.OnAccepted(session);
        Assert.NotNull(probe);
        probe.RecordCommand();
        probe.RecordArticleReceived(1_000, 1);
        probe.RecordQueue(ArticleEnqueueResult.Accepted, 1, 1_000);
        probe.RecordArticleCompleted(duplicate: false, responseTicks: 1);
        hub.RecordTcpBytes(2_000);
        hub.BeginSpoolWork();
        hub.EndSpoolWork(1_000, persisted: true);

        var store = CreateStore(maxIncoming: 50);
        var first = hub.CaptureSnapshot(DisabledArticleIngestionQueue.Instance, store);
        Assert.Equal(1, first.IntervalArticles);
        Assert.Equal(1, first.IntervalArticlesReceived);
        Assert.Equal(1, first.IntervalCommands);
        Assert.Equal(1_000, first.IntervalBytes);
        Assert.Equal(2_000, first.IntervalTcpBytes);
        Assert.Equal(1_000, first.IntervalQueuedBytes);
        Assert.Equal(1, first.IntervalQueuedArticles);
        Assert.Equal(1, first.IntervalSpoolArticles);
        Assert.Equal(1_000, first.IntervalSpoolBytes);
        Assert.Equal(1, first.ActiveSessions);
        Assert.Equal(0, first.SpoolActiveWorkers);

        var second = hub.CaptureSnapshot(DisabledArticleIngestionQueue.Instance, store);
        Assert.Equal(0, second.IntervalArticles);
        Assert.Equal(0, second.IntervalArticlesReceived);
        Assert.Equal(0, second.IntervalCommands);
        Assert.Equal(0, second.IntervalBytes);
        Assert.Equal(0, second.IntervalTcpBytes);
        Assert.Equal(0, second.IntervalQueuedBytes);
        Assert.Equal(0, second.IntervalSpoolArticles);
        Assert.Equal(0, second.ArticlesPerSecond);
        Assert.Equal(0, second.TcpMegabitsPerSecond);

        probe.RecordCommand();
        probe.RecordArticleReceived(3_000, 1);
        probe.RecordQueue(ArticleEnqueueResult.Accepted, 1, 3_000);
        probe.RecordArticleCompleted(duplicate: false, responseTicks: 1);
        hub.RecordTcpBytes(4_000);
        hub.BeginSpoolWork();
        hub.EndSpoolWork(3_000, persisted: true);

        var third = hub.CaptureSnapshot(DisabledArticleIngestionQueue.Instance, store);
        Assert.Equal(1, third.IntervalArticles);
        Assert.Equal(1, third.IntervalArticlesReceived);
        Assert.Equal(1, third.IntervalCommands);
        Assert.Equal(3_000, third.IntervalBytes);
        Assert.Equal(4_000, third.IntervalTcpBytes);
        Assert.Equal(3_000, third.IntervalQueuedBytes);
        Assert.Equal(1, third.IntervalQueuedArticles);
        Assert.Equal(1, third.IntervalSpoolArticles);
        Assert.Equal(3_000, third.IntervalSpoolBytes);
        var row = Assert.Single(third.Sessions);
        Assert.Equal(1, row.IntervalCommands);
        Assert.Equal(1, row.IntervalArticlesReceived);
        Assert.Equal(3_000, row.IntervalBytes);
    }

    [Fact]
    public void Snapshot_Rates_UseIntervalElapsed()
    {
        var snapshot = new FeedDiagnosticsSnapshot
        {
            CapturedAt = DateTimeOffset.UtcNow,
            Interval = TimeSpan.FromSeconds(5),
            Peers = [],
            Sessions = [],
            IntervalArticles = 100,
            IntervalArticlesReceived = 100,
            IntervalBytes = 62_500_000,
            IntervalCommands = 250,
            IntervalTcpBytes = 62_500_000,
            IntervalQueuedBytes = 62_500_000,
            IntervalSpoolBytes = 31_250_000,
        };

        Assert.Equal(20d, snapshot.ArticlesPerSecond, precision: 6);
        Assert.Equal(50d, snapshot.CommandsPerSecond, precision: 6);
        Assert.Equal(100d, snapshot.TcpMegabitsPerSecond, precision: 6);
        Assert.Equal(100d, snapshot.IngestMegabitsPerSecond, precision: 6);
        Assert.Equal(100d, snapshot.ProcessMegabitsPerSecond, precision: 6);
        Assert.Equal(50d, snapshot.SpoolMegabitsPerSecond, precision: 6);
        Assert.Equal(0, FeedRateCalculator.MegabitsPerSecond(62_500_000, TimeSpan.Zero));
    }

    [Fact]
    public void FeedDiagnosticsOptions_InvalidInterval_FailsValidation()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.FeedDiagnostics = new FeedDiagnosticsOptions { IntervalSeconds = 0 };
        var validator = new NntpdOptionsValidator(new VectorNNTPD.TransitService.Tests.TestDoubles.FakeLocalIpAddressAssignee(true));
        Assert.True(validator.Validate(null, options).Failed);
    }

    private static TransitConfigurationStore CreateStore(int maxIncoming)
    {
        var store = new TransitConfigurationStore();
        store.Replace(
            TransitTestPeers.Snapshot(
                TransitTestPeers.DefaultPeerName,
                TransitTestPeers.Peer(maxIncoming: maxIncoming, allowFrom: ["192.0.2.0/24"])));
        return store;
    }

    private static NntpSession CreateSession(string address) =>
        CreateSession(
            TransitPeerAuthorization.CreateForStore(CreateStore(maxIncoming: 50)),
            IPAddress.Parse(address));

    private static NntpSession CreateSession(ITransitPeerAuthorization peers, IPAddress source)
    {
        var input = new Pipe();
        var output = new Pipe();
        var connection = new ProbeConnection(input.Reader, output.Writer, source);
        return new NntpSession(
            connection,
            NullLogger<NntpSession>.Instance,
            transitPeerAuthorization: peers);
    }

    private sealed class ProbeConnection(PipeReader input, PipeWriter output, IPAddress address) : INntpConnection
    {
        private readonly CancellationTokenSource _closed = new();

        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
        public ConnectionClientIdentity ClientIdentity { get; } =
            ConnectionClientIdentity.Direct(new IPEndPoint(address, 40000));
        public EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;
        public EndPoint? LocalEndPoint => null;
        public bool IsTls => false;
        public bool IsCompressed => false;
        public bool IsCompleted => _closed.IsCancellationRequested;
        public long OutboundIdleVersion => 0;
        public CancellationToken ConnectionClosed => _closed.Token;

        public Task CompleteAsync(Exception? exception = null)
        {
            _closed.Cancel();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _closed.Dispose();
            return ValueTask.CompletedTask;
        }

        public Task WaitForOutboundDeliveryAsync(long outboundIdleVersionBeforeFlush, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WaitForOutboundDeliveryAndPauseReadsAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task PauseReadsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpgradeToTlsAsync(
            VectorNNTPD.Common.Networking.Certificates.ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipherSuite)
        {
            tlsVersion = string.Empty;
            cipherSuite = string.Empty;
            return false;
        }
    }
}

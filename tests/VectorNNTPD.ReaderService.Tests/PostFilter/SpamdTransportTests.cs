using System.Net;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.PostFilter;

namespace VectorNNTPD.ReaderService.Tests.PostFilter;

public sealed class SpamdTransportTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SequentialChecks_ReuseOneConnection()
    {
        await using var server = await LocalSpamdServer.StartAsync(keepOpen: true);
        var metrics = new SpamdTransportMetrics();
        await using var client = new SpamdCheckClient(metrics);
        var target = Target(server.Port, maxConnections: 1);
        Assert.Equal(PostFilterSpamAssassinStatus.Ham, (await CheckAsync(client, target)).Status);
        Assert.Equal(PostFilterSpamAssassinStatus.Ham, (await CheckAsync(client, target)).Status);
        Assert.Equal(2, server.Checks);
        Assert.Equal(1, server.Connections);
        Assert.Equal(1, metrics.ConnectionsEstablished);
        Assert.Equal(2, metrics.CheckRequests);
        Assert.Equal(1, metrics.ConnectionReuses);
    }

    [Fact]
    public async Task BrokenConnection_IsEvictedAndReplaced()
    {
        await using var server = await LocalSpamdServer.StartAsync(keepOpen: false);
        var metrics = new SpamdTransportMetrics();
        await using var client = new SpamdCheckClient(metrics);
        var target = Target(server.Port, maxConnections: 1);
        Assert.Equal(PostFilterSpamAssassinStatus.Ham, (await CheckAsync(client, target)).Status);
        Assert.Equal(PostFilterSpamAssassinStatus.Ham, (await CheckAsync(client, target)).Status);
        Assert.Equal(2, server.Checks);
        Assert.Equal(2, server.Connections);
        Assert.Equal(2, metrics.ConnectionsEstablished);
        Assert.True(metrics.Evictions >= 1);
    }

    [Fact]
    public async Task ProtocolError_DoesNotReuseConnection()
    {
        await using var server = await LocalSpamdServer.StartAsync(keepOpen: true, response: "NOPE\r\n\r\n"u8.ToArray());
        var metrics = new SpamdTransportMetrics();
        await using var client = new SpamdCheckClient(metrics);
        var result = await CheckAsync(client, Target(server.Port, maxConnections: 1));
        Assert.Equal(PostFilterSpamAssassinStatus.Failed, result.Status);
        Assert.Contains("malformed", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, metrics.Evictions);
    }

    [Fact]
    public async Task Timeout_IsClassifiedAndEvicts()
    {
        await using var server = await LocalSpamdServer.StartAsync(keepOpen: true, hang: true);
        var metrics = new SpamdTransportMetrics();
        await using var client = new SpamdCheckClient(metrics);
        var target = Target(server.Port, maxConnections: 1, operation: TimeSpan.FromMilliseconds(200));
        var result = await CheckAsync(client, target);
        Assert.Equal(PostFilterSpamAssassinStatus.Failed, result.Status);
        Assert.Equal("timeout", result.Detail);
        Assert.True(metrics.Evictions >= 1);
    }

    [Fact]
    public async Task Cancellation_IsRethrown()
    {
        await using var server = await LocalSpamdServer.StartAsync(keepOpen: true, hang: true);
        await using var client = new SpamdCheckClient();
        using var cts = new CancellationTokenSource();
        var check = CheckAsync(client, Target(server.Port, operation: TimeSpan.FromSeconds(5)), cts.Token);
        await server.Accepted;
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await check);
    }

    [Fact]
    public void HostSelection_RoundRobinRotates_FailoverStaysAtZero()
    {
        var rr = 0;
        Assert.Equal(0, SpamdHostSelector.NextStart(PostFilterSpamAssassinHostSelection.RoundRobin, 2, ref rr));
        Assert.Equal(1, SpamdHostSelector.NextStart(PostFilterSpamAssassinHostSelection.RoundRobin, 2, ref rr));
        Assert.Equal(0, SpamdHostSelector.NextStart(PostFilterSpamAssassinHostSelection.RoundRobin, 2, ref rr));
        var failover = 99;
        Assert.Equal(0, SpamdHostSelector.NextStart(PostFilterSpamAssassinHostSelection.Failover, 3, ref failover));
        Assert.Equal(0, SpamdHostSelector.NextStart(PostFilterSpamAssassinHostSelection.Failover, 3, ref failover));
        Assert.Equal(99, failover);
    }

    [Fact]
    public async Task Failover_SkipsUnreachableHost()
    {
        await using var good = await LocalSpamdServer.StartAsync(keepOpen: true);
        await using var client = new SpamdCheckClient();
        var result = await client.CheckAsync(
            Article(),
            "poster",
            new PostFilterSpamAssassinTarget(
                ["203.0.113.1", "127.0.0.1"],
                good.Port,
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromSeconds(3),
                "1.5",
                1,
                PostFilterSpamAssassinHostSelection.Failover),
            ScanContext());
        Assert.Equal(PostFilterSpamAssassinStatus.Ham, result.Status);
        Assert.Equal(1, good.Checks);
    }

    [Fact]
    public async Task ConcurrentChecks_HonorPoolSize()
    {
        await using var server = await LocalSpamdServer.StartAsync(keepOpen: true, delay: TimeSpan.FromMilliseconds(80));
        var metrics = new SpamdTransportMetrics();
        await using var client = new SpamdCheckClient(metrics);
        var target = Target(server.Port, maxConnections: 2);
        var tasks = Enumerable.Range(0, 4).Select(_ => CheckAsync(client, target).AsTask()).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, result => Assert.Equal(PostFilterSpamAssassinStatus.Ham, result.Status));
        Assert.Equal(4, metrics.CheckRequests);
        Assert.True(server.PeakConnections <= 2);
        Assert.True(metrics.ConnectionsEstablished <= 2);
    }

    [Fact]
    public async Task DisposeAsync_RejectsFurtherChecks()
    {
        await using var client = new SpamdCheckClient();
        await client.DisposeAsync();
        var result = await CheckAsync(client, Target(9));
        Assert.Equal(PostFilterSpamAssassinStatus.Failed, result.Status);
        Assert.Equal("disposed", result.Detail);
    }

    [Fact]
    public async Task LeftoverBytes_AreProtocolFailure()
    {
        var extra = "SPAMD/1.1 0 EX_OK\r\nSpam: False ; 0.0 / 5.0\r\n\r\nEXTRA"u8.ToArray();
        await using var server = await LocalSpamdServer.StartAsync(keepOpen: true, response: extra);
        await using var client = new SpamdCheckClient();
        var result = await CheckAsync(client, Target(server.Port));
        Assert.Equal(PostFilterSpamAssassinStatus.Failed, result.Status);
        Assert.Equal("leftover", result.Detail);
    }

    [Fact]
    public async Task LeftoverBytes_DoNotPoisonTheNextCheck()
    {
        var leftover = "SPAMD/1.1 0 EX_OK\r\nSpam: False ; 0.0 / 5.0\r\n\r\nSPAMD/1.1 0 EX_OK\r\nSpam: False ; 0.0 / 5.0\r\n\r\n"u8.ToArray();
        var ham = "SPAMD/1.1 0 EX_OK\r\nSpam: False ; 0.0 / 5.0\r\n\r\n"u8.ToArray();
        await using var server = await LocalSpamdServer.StartAsync(
            keepOpen: true,
            response: leftover,
            subsequentResponse: ham);
        var metrics = new SpamdTransportMetrics();
        await using var client = new SpamdCheckClient(metrics);
        var target = Target(server.Port, maxConnections: 1);
        var first = await CheckAsync(client, target);
        Assert.Equal(PostFilterSpamAssassinStatus.Failed, first.Status);
        Assert.Equal("leftover", first.Detail);
        var second = await CheckAsync(client, target);
        Assert.Equal(PostFilterSpamAssassinStatus.Ham, second.Status);
        Assert.Equal(2, server.Connections);
        Assert.True(metrics.Evictions >= 1);
    }

    private static ValueTask<PostFilterSpamAssassinResult> CheckAsync(
        SpamdCheckClient client,
        PostFilterSpamAssassinTarget target,
        CancellationToken cancellationToken = default) =>
        client.CheckAsync(Article(), "poster", target, ScanContext(), cancellationToken);

    private static PostFilterSpamAssassinTarget Target(
        int port,
        int maxConnections = 4,
        TimeSpan? operation = null) =>
        new(
            ["127.0.0.1"],
            port,
            TimeSpan.FromSeconds(2),
            operation ?? TimeSpan.FromSeconds(5),
            "1.5",
            maxConnections,
            PostFilterSpamAssassinHostSelection.RoundRobin);

    private static SpamdScanContext ScanContext() =>
        new(IPAddress.Loopback, "nntpd01.usenet.ninja", Now);

    private static ArticleRecord Article()
    {
        var bytes = "From: a@b\r\nNewsgroups: misc.test\r\nSubject: t\r\nMessage-ID: <h@example.com>\r\nDate: 1 Jan 2026 00:00:00 +0000\r\n\r\nbody\r\n"u8.ToArray();
        return new ArticleRecord(
            ArticleId.FromMessageId("<h@example.com>"u8),
            artHash: 1,
            artType: ArticleType.Default,
            artLines: 1,
            canonicalUtc: Now.UtcDateTime,
            parseStatus: ArticleParseStatus.CanonicalV1,
            artData: bytes,
            fields: default);
    }
}

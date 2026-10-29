using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Authentication;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Diagnostics;
using VectorNNTPD.TransitService.History;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.Networking.Transport;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Telemetry;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.TestDoubles;
using VectorNNTPD.TransitService.Tests.Transit;
using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.Tests.Telemetry;

public sealed class ApplicationTelemetryTests
{
    [Fact]
    public void Period_IsSixtySeconds()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), ApplicationTelemetryService.Period);
    }

    [Fact]
    public async Task FirstSnapshot_EmitsOnlyAfterFirstInterval_AtInformation()
    {
        var interval = new ControllableInterval();
        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, interval);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => interval.WaitCount >= 1);
        Assert.Equal(0, service.EmitCount);
        Assert.Empty(logger.Entries);

        interval.ReleaseTick();
        await WaitUntilAsync(() => service.EmitCount == 1);

        Assert.Equal(3, logger.Entries.Count);
        Assert.All(logger.Entries, static e => Assert.Equal(LogLevel.Information, e.Level));
        Assert.Equal(2400, logger.Entries[0].EventId.Id);
        Assert.Equal(2401, logger.Entries[1].EventId.Id);
        Assert.Equal(2402, logger.Entries[2].EventId.Id);
        Assert.Contains("HistoryDb lookups=", logger.Entries[0].Message);
        Assert.Contains("TransitIngressQueue articles=", logger.Entries[1].Message);
        Assert.Contains("ActiveSessions active=", logger.Entries[2].Message);
    }

    [Fact]
    public async Task Cadence_EmitsOncePerTick_WithoutOverlap()
    {
        var interval = new ControllableInterval();
        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, interval);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => interval.WaitCount >= 1);

        interval.ReleaseTick();
        await WaitUntilAsync(() => service.EmitCount == 1);
        Assert.False(service.IsEmitBusy);
        interval.ReleaseTick();
        await WaitUntilAsync(() => service.EmitCount == 2);
        Assert.False(service.IsEmitBusy);

        Assert.Equal(6, logger.Entries.Count);
        Assert.Equal(2, logger.Entries.Count(static e => e.EventId.Id == 2400));
        Assert.Equal(2, logger.Entries.Count(static e => e.EventId.Id == 2401));
        Assert.Equal(2, logger.Entries.Count(static e => e.EventId.Id == 2402));
    }

    [Fact]
    public async Task DisabledFeedDiagnostics_DoesNotDisableTelemetry()
    {
        Assert.False(NullFeedDiagnostics.Instance.IsEnabled);

        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions(), 64);
        await using var diagnostics = new FeedDiagnosticsService(
            NullFeedDiagnostics.Instance,
            queue,
            new TransitConfigurationStore(),
            Options.Create(new NntpdOptions()),
            NullLogger<FeedDiagnosticsService>.Instance);
        await diagnostics.StartAsync(CancellationToken.None);
        Assert.Null(diagnostics.Execution);

        var interval = new ControllableInterval();
        var logger = new RecordingLogger<ApplicationTelemetryService>();
        var census = new NntpSessionCensus();
        await using var telemetry = new ApplicationTelemetryService(
            queue,
            census,
            logger,
            history: null,
            timeProvider: null,
            interval,
            ApplicationTelemetryService.Period);

        await telemetry.StartAsync(CancellationToken.None);
        interval.ReleaseTick();
        await WaitUntilAsync(() => telemetry.EmitCount == 1);
        Assert.Equal(3, logger.Entries.Count);
        Assert.All(logger.Entries, static e => Assert.Equal(LogLevel.Information, e.Level));
    }

    [Fact]
    public async Task HistoryDb_ValuesComeFromAuthoritativeHistoryMetrics()
    {
        var redis = new FakeRedisService();
        var history = new HistoryDb(
            redis,
            TimeSpan.FromHours(2),
            NullLogger<HistoryDb>.Instance,
            TimeProvider.System,
            new HistoryWriteQueue());
        Assert.Equal(HistoryLookupResult.Unseen, await history.LookupAsync("<a@ex.com>"u8.ToArray()));
        Assert.Equal(HistoryLookupResult.Seen, await history.LookupAsync("<a@ex.com>"u8.ToArray()));
        redis.IsUnavailable = true;
        Assert.Equal(HistoryLookupResult.Unavailable, await history.LookupAsync("<b@ex.com>"u8.ToArray()));
        var captured = history.Capture();

        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, history: history);
        service.Emit();

        var row = Assert.Single(logger.Entries, static e => e.EventId.Id == 2400);
        Assert.Equal(captured.Lookups, GetInt64(row, "Lookups"));
        Assert.Equal(captured.Hits, GetInt64(row, "Hits"));
        Assert.Equal(captured.Misses, GetInt64(row, "Misses"));
        Assert.Equal(captured.Errors, GetInt64(row, "Errors"));

        logger.Entries.Clear();
        service.Emit();
        var second = Assert.Single(logger.Entries, static e => e.EventId.Id == 2400);
        Assert.Equal(0L, GetInt64(second, "Lookups"));
        Assert.Equal(0L, GetInt64(second, "Hits"));
        Assert.Equal(0L, GetInt64(second, "Misses"));
        Assert.Equal(0L, GetInt64(second, "Errors"));
    }

    [Fact]
    public async Task TransitIngressQueue_ValuesComeFromAuthoritativeQueue()
    {
        var a = Article("<a@ex.com>");
        var b = Article("<b@ex.com>");
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions(), a.Payload.Length + b.Payload.Length);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(a, CancellationToken.None));
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(b, CancellationToken.None));
        Assert.Equal(
            ArticleEnqueueResult.Rejected,
            queue.TryAdmit(Article(
                "<huge@ex.com>",
                body: string.Concat(Enumerable.Repeat(new string('Z', 80) + "\r\n", 20)))));

        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, queue: queue);
        service.Emit();

        var row = Assert.Single(logger.Entries, static e => e.EventId.Id == 2401);
        Assert.Equal(queue.Count, GetInt32(row, "Articles"));
        Assert.Equal(queue.QueuedBytes, GetInt64(row, "Bytes"));
        Assert.Equal(queue.PeakCount, GetInt32(row, "PeakArticles"));
        Assert.Equal(queue.PeakQueuedBytes, GetInt64(row, "PeakBytes"));
        Assert.Equal(queue.WaitingProducerCount, GetInt32(row, "Waiting"));
        Assert.Equal(queue.MemoryLimitBytes, GetInt64(row, "Limit"));
        Assert.Equal(queue.AdmissionFailureCount, GetInt64(row, "AdmissionFailures"));
        Assert.Equal(2, GetInt32(row, "Articles"));
        Assert.Equal(a.Payload.Length + b.Payload.Length, GetInt64(row, "Bytes"));
        Assert.Equal(1L, GetInt64(row, "AdmissionFailures"));
    }

    [Fact]
    public async Task ActiveSessions_CountsCompleteServerPopulation()
    {
        var census = new NntpSessionCensus();
        var idle = CreateSession();
        var receiving = CreateSession();
        var waitingHistory = CreateSession();
        receiving.SetActivityState(FeedSessionState.Receiving);
        waitingHistory.SetActivityState(FeedSessionState.WaitingHistory);
        census.Register(idle);
        census.Register(receiving);
        census.Register(waitingHistory);

        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, census: census);
        service.Emit();

        var row = Assert.Single(logger.Entries, static e => e.EventId.Id == 2402);
        Assert.Equal(3, GetInt32(row, "Active"));
        Assert.Equal(3, GetInt32(row, "Established"));
        Assert.Equal(1, GetInt32(row, "Idle"));
        Assert.Equal(1, GetInt32(row, "Receiving"));
        Assert.Equal(1, GetInt32(row, "WaitingHistory"));
        Assert.Equal(0, GetInt32(row, "WaitingQueue"));
        Assert.Equal(0, GetInt32(row, "WaitingWindow"));
        Assert.Equal(0, GetInt32(row, "Completing"));
    }

    [Fact]
    public async Task ActiveSessions_IncludesReaderAndStreamerSessions()
    {
        var census = new NntpSessionCensus();
        var reader = CreateSession();
        var streamer = CreateSession();
        reader.SetMode(NntpSessionMode.Reader);
        streamer.SetMode(NntpSessionMode.Stream);
        census.Register(reader);
        census.Register(streamer);

        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, census: census);
        service.Emit();

        var row = Assert.Single(logger.Entries, static e => e.EventId.Id == 2402);
        Assert.Equal(2, GetInt32(row, "Active"));
        Assert.Equal(2, GetInt32(row, "Established"));
        Assert.Equal(NntpSessionMode.Reader, reader.Mode);
        Assert.Equal(NntpSessionMode.Stream, streamer.Mode);
    }

    [Fact]
    public async Task ActiveSessions_DoesNotUseFeedDiagnosticsOrTransitOnlyPopulation()
    {
        var hub = new FeedDiagnosticsHub();
        var transit = CreateSession();
        Assert.NotNull(hub.OnAccepted(transit));
        var feed = hub.CaptureSnapshot(DisabledArticleIngestionQueue.Instance, new TransitConfigurationStore());
        Assert.Equal(1, feed.ActiveSessions);

        var census = new NntpSessionCensus();
        var reader = CreateSession();
        var streamer = CreateSession();
        reader.SetMode(NntpSessionMode.Reader);
        streamer.SetMode(NntpSessionMode.Stream);
        census.Register(reader);
        census.Register(streamer);

        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, census: census);
        service.Emit();

        var row = Assert.Single(logger.Entries, static e => e.EventId.Id == 2402);
        Assert.Equal(2, GetInt32(row, "Active"));
        Assert.NotEqual(feed.ActiveSessions, GetInt32(row, "Active"));
    }

    [Fact]
    public async Task RunAsync_RegistersReaderAndStreamer_ThenUnregistersOnExit()
    {
        var census = new NntpSessionCensus();
        var reader = CreateSession(census);
        var streamer = CreateSession(census);
        reader.SetMode(NntpSessionMode.Reader);
        streamer.SetMode(NntpSessionMode.Stream);

        using var cts = new CancellationTokenSource();
        var readerRun = reader.RunAsync(cts.Token);
        var streamerRun = streamer.RunAsync(cts.Token);
        await WaitUntilAsync(() => census.Capture().Active == 2);
        var live = census.Capture();
        Assert.Equal(2, live.Active);
        Assert.Equal(2, live.Established);

        await cts.CancelAsync();
        await readerRun;
        await streamerRun;
        Assert.Equal(0, census.Capture().Active);
    }

    [Fact]
    public async Task Stop_CancelsLoopWithoutFurtherEmits()
    {
        var interval = new ControllableInterval();
        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, interval);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => interval.WaitCount >= 1);
        await service.StopAsync(CancellationToken.None);
        Assert.NotNull(service.Execution);
        await service.Execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, service.EmitCount);
        interval.ReleaseTick();
        await Task.Yield();
        Assert.Equal(0, service.EmitCount);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Emit_IsSingleFlight()
    {
        var census = new BlockingCensus();
        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, census: census);

        var first = Task.Run(service.Emit, CancellationToken.None);
        await census.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Task.Run(service.Emit, CancellationToken.None);
        await Task.Yield();
        Assert.Equal(1, census.Concurrent);
        census.Release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, service.EmitCount);
        Assert.Equal(1, census.MaxConcurrent);
        Assert.False(service.IsEmitBusy);
    }

    [Fact]
    public async Task ConfiguredPeers_EachEmitsOneInformationRecord()
    {
        var store = CreatePeerStore(("blueworld-hosting", 10), ("giganews", 100), ("usenet-ninja", 10));
        var metrics = new TransitPeerMetrics();
        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, peerMetrics: metrics, transit: store);

        service.Emit();

        var peers = logger.Entries.Where(static e => e.EventId.Id == 2403).ToArray();
        Assert.Equal(3, peers.Length);
        Assert.Equal("blueworld-hosting", peers[0].Properties["PeerName"]);
        Assert.Equal("giganews", peers[1].Properties["PeerName"]);
        Assert.Equal("usenet-ninja", peers[2].Properties["PeerName"]);
        Assert.All(peers, static e => Assert.Equal(LogLevel.Information, e.Level));
        Assert.All(peers, static e => Assert.True(e.Properties.ContainsKey("AverageMbps")));
        Assert.All(peers, static e => Assert.Contains("avg_mbps=", e.Message, StringComparison.Ordinal));
        Assert.Equal(6, logger.Entries.Count);
    }

    [Fact]
    public async Task UnconfiguredPeer_ProducesNoTelemetry()
    {
        var store = CreatePeerStore(("giganews", 100));
        var metrics = new TransitPeerMetrics();
        metrics.RecordAccepted("not-a-peer");
        metrics.RecordArticleReceived("not-a-peer", 100);
        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, peerMetrics: metrics, transit: store);

        service.Emit();

        var peers = logger.Entries.Where(static e => e.EventId.Id == 2403).ToArray();
        Assert.Single(peers);
        Assert.Equal("giganews", peers[0].Properties["PeerName"]);
        Assert.DoesNotContain(logger.Entries, static e => e.Message.StartsWith("not-a-peer ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Giganews_RendersCanonicalIdentifierAndMaxIncoming()
    {
        var store = CreatePeerStore(("giganews", 100));
        var metrics = new TransitPeerMetrics();
        for (var i = 0; i < 60; i++)
        {
            metrics.RecordAccepted("giganews");
            metrics.RecordSessionRegistered("giganews");
        }

        const int articles = 1619;
        const long bytes = 1_213_768_568L;
        var bytesPerArticle = (int)(bytes / articles);
        var remainder = (int)(bytes - ((long)bytesPerArticle * articles));
        for (var i = 0; i < articles; i++)
        {
            var size = i == 0 ? bytesPerArticle + remainder : bytesPerArticle;
            metrics.RecordArticleReceived("giganews", size);
        }

        for (var i = 0; i < 1840; i++)
        {
            metrics.RecordCheck("giganews");
        }

        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, peerMetrics: metrics, transit: store);
        service.Emit();

        var row = Assert.Single(logger.Entries, static e => e.EventId.Id == 2403);
        Assert.Equal(LogLevel.Information, row.Level);
        var expectedMbps = ApplicationTelemetryService.AverageMbps(bytes, ApplicationTelemetryService.Period);
        Assert.Equal(
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"giganews active=60/100 peak=60 accepted=60 transmitted=0 rejected=0 articles=1619 bytes=1213768568 avg_article={bytes / articles} avg_mbps={expectedMbps:F2} checks=1840"),
            row.Message);
        Assert.Equal(60, GetInt32(row, "Active"));
        Assert.Equal(100, GetInt32(row, "MaxIncomingConnections"));
        Assert.Equal(60, GetInt32(row, "Peak"));
        Assert.Equal(60L, GetInt64(row, "Accepted"));
        Assert.Equal(0L, GetInt64(row, "Transmitted"));
        Assert.Equal(0L, GetInt64(row, "Rejected"));
        Assert.Equal(1619L, GetInt64(row, "Articles"));
        Assert.Equal(1_213_768_568L, GetInt64(row, "Bytes"));
        Assert.Equal(1_213_768_568L / 1619L, GetInt64(row, "AverageArticleBytes"));
        Assert.Equal(1840L, GetInt64(row, "Checks"));
        Assert.Equal(expectedMbps, GetDouble(row, "AverageMbps"), precision: 6);
    }

    [Fact]
    public async Task PeerActive_IsCurrentEstablishedSessionsFromCensus()
    {
        var store = CreatePeerStore(("giganews", 100));
        var metrics = new TransitPeerMetrics();
        var census = new NntpSessionCensus(metrics);
        var first = CreatePeerSession("giganews", census, metrics);
        var second = CreatePeerSession("giganews", census, metrics);
        census.Register(first);
        census.Register(second);

        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, census: census, peerMetrics: metrics, transit: store);
        service.Emit();

        var row = Assert.Single(logger.Entries, static e => e.EventId.Id == 2403);
        Assert.Equal(2, GetInt32(row, "Active"));
        Assert.Equal(2, GetInt32(row, "Peak"));
        Assert.Equal(2, census.Capture().Active);
    }

    [Fact]
    public async Task PeerAcceptedAndRejected_AreIntervalDeltas()
    {
        var store = CreatePeerStore(("giganews", 100));
        var metrics = new TransitPeerMetrics();
        metrics.RecordAccepted("giganews");
        metrics.RecordAccepted("giganews");
        metrics.RecordRejected("giganews");
        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, peerMetrics: metrics, transit: store);

        service.Emit();
        var first = Assert.Single(logger.Entries, static e => e.EventId.Id == 2403);
        Assert.Equal(2L, GetInt64(first, "Accepted"));
        Assert.Equal(1L, GetInt64(first, "Rejected"));

        logger.Entries.Clear();
        metrics.RecordAccepted("giganews");
        metrics.RecordRejected("giganews");
        metrics.RecordRejected("giganews");
        service.Emit();
        var second = Assert.Single(logger.Entries, static e => e.EventId.Id == 2403);
        Assert.Equal(1L, GetInt64(second, "Accepted"));
        Assert.Equal(2L, GetInt64(second, "Rejected"));
    }

    [Fact]
    public async Task PeerArticlesBytesChecks_AreIntervalValues_AndAverageUsesSameInterval()
    {
        var store = CreatePeerStore(("giganews", 100));
        var metrics = new TransitPeerMetrics();
        metrics.RecordArticleReceived("giganews", 100);
        metrics.RecordArticleReceived("giganews", 50);
        metrics.RecordCheck("giganews");
        metrics.RecordCheck("giganews");
        metrics.RecordCheck("giganews");
        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, peerMetrics: metrics, transit: store);

        service.Emit();
        var first = Assert.Single(logger.Entries, static e => e.EventId.Id == 2403);
        Assert.Equal(2L, GetInt64(first, "Articles"));
        Assert.Equal(150L, GetInt64(first, "Bytes"));
        Assert.Equal(75L, GetInt64(first, "AverageArticleBytes"));
        Assert.Equal(3L, GetInt64(first, "Checks"));

        logger.Entries.Clear();
        metrics.RecordArticleReceived("giganews", 40);
        service.Emit();
        var second = Assert.Single(logger.Entries, static e => e.EventId.Id == 2403);
        Assert.Equal(1L, GetInt64(second, "Articles"));
        Assert.Equal(40L, GetInt64(second, "Bytes"));
        Assert.Equal(40L, GetInt64(second, "AverageArticleBytes"));
        Assert.Equal(0L, GetInt64(second, "Checks"));
    }

    [Fact]
    public async Task Transmitted_IsZeroUntilOutboundAccounting_AndIsNotDerivedFromQueue()
    {
        var store = CreatePeerStore(("giganews", 100));
        var metrics = new TransitPeerMetrics();
        var queued = Article("<queued@ex.com>");
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions(), queued.Payload.Length);
        Assert.Equal(
            ArticleEnqueueResult.Accepted,
            await queue.EnqueueAsync(queued, CancellationToken.None));
        metrics.RecordArticleReceived("giganews", queued.Payload.Length);

        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, queue: queue, peerMetrics: metrics, transit: store);
        service.Emit();

        var first = Assert.Single(logger.Entries, static e => e.EventId.Id == 2403);
        Assert.Equal(0L, GetInt64(first, "Transmitted"));
        Assert.Equal(1L, GetInt64(first, "Articles"));
        Assert.Contains(logger.Entries, static e => e.EventId.Id == 2401 && GetInt32(e, "Articles") == 1);

        logger.Entries.Clear();
        metrics.RecordTransmitted("giganews");
        service.Emit();
        var second = Assert.Single(logger.Entries, static e => e.EventId.Id == 2403);
        Assert.Equal(1L, GetInt64(second, "Transmitted"));
    }

    [Fact]
    public async Task DisabledFeedDiagnostics_DoesNotDisablePeerTelemetry()
    {
        Assert.False(NullFeedDiagnostics.Instance.IsEnabled);
        var store = CreatePeerStore(("giganews", 100));
        var metrics = new TransitPeerMetrics();
        metrics.RecordAccepted("giganews");
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions(), 64);
        await using var diagnostics = new FeedDiagnosticsService(
            NullFeedDiagnostics.Instance,
            queue,
            store,
            Options.Create(new NntpdOptions()),
            NullLogger<FeedDiagnosticsService>.Instance);
        await diagnostics.StartAsync(CancellationToken.None);
        Assert.Null(diagnostics.Execution);

        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var telemetry = CreateService(logger, queue: queue, peerMetrics: metrics, transit: store);
        telemetry.Emit();

        var row = Assert.Single(logger.Entries, static e => e.EventId.Id == 2403);
        Assert.Equal(LogLevel.Information, row.Level);
        Assert.Equal(1L, GetInt64(row, "Accepted"));
    }

    [Fact]
    public async Task ReaderAuthFromAllowFromRange_DoesNotIncrementTransitPeerTelemetry()
    {
        var store = CreatePeerStore(("usenet-ninja", 10));
        var metrics = new TransitPeerMetrics();
        var census = new NntpSessionCensus(metrics);
        var session = CreatePeerSession("usenet-ninja", census, metrics, maxIncoming: 10);
        Assert.Equal("usenet-ninja", session.Authorization.TransitPeerName);

        session.RecordPeerAccepted();
        census.Register(session);
        Assert.Equal(1, metrics.Capture("usenet-ninja").Active);
        Assert.Equal(1, metrics.Capture("usenet-ninja").Accepted);

        session.ApplySuccessfulAuthentication("a", MySqlNntpCredentialValidator.ReaderPrivileges);
        Assert.Null(session.Authorization.TransitPeerName);
        Assert.True(session.Authorization.AuthorizedReader);

        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, census: census, peerMetrics: metrics, transit: store);
        service.Emit();

        var row = Assert.Single(logger.Entries, static e => e.EventId.Id == 2403);
        Assert.Equal("usenet-ninja", row.Properties["PeerName"]);
        Assert.Equal(0, GetInt32(row, "Active"));
        Assert.Equal(10, GetInt32(row, "MaxIncomingConnections"));
        Assert.Equal(0L, GetInt64(row, "Accepted"));
        Assert.Equal(0, metrics.Capture("usenet-ninja").Active);
        Assert.Equal(0, metrics.Capture("usenet-ninja").Accepted);
        Assert.Equal(1, census.Capture().Active);
    }

    [Fact]
    public async Task TransitConnection_IncrementsTransitPeerTelemetry()
    {
        var store = CreatePeerStore(("usenet-ninja", 10));
        var metrics = new TransitPeerMetrics();
        var census = new NntpSessionCensus(metrics);
        var session = CreatePeerSession("usenet-ninja", census, metrics, maxIncoming: 10);

        session.RecordPeerAccepted();
        census.Register(session);

        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, census: census, peerMetrics: metrics, transit: store);
        service.Emit();

        var row = Assert.Single(logger.Entries, static e => e.EventId.Id == 2403);
        Assert.Equal("usenet-ninja", row.Properties["PeerName"]);
        Assert.Equal(1, GetInt32(row, "Active"));
        Assert.Equal(10, GetInt32(row, "MaxIncomingConnections"));
        Assert.Equal(1, GetInt32(row, "Peak"));
        Assert.Equal(1L, GetInt64(row, "Accepted"));
        Assert.Equal("usenet-ninja", session.Authorization.TransitPeerName);
        Assert.False(session.Authorization.AuthorizedReader);
    }

    [Fact]
    public async Task PeerSessionCounts_AreIndependent()
    {
        var store = CreatePeerStore(("blueworld-hosting", 10), ("giganews", 100));
        var metrics = new TransitPeerMetrics();
        var census = new NntpSessionCensus(metrics);
        var giga1 = CreatePeerSession("giganews", census, metrics, maxIncoming: 100);
        var giga2 = CreatePeerSession("giganews", census, metrics, maxIncoming: 100);
        var blue = CreatePeerSession(
            "blueworld-hosting",
            census,
            metrics,
            maxIncoming: 10,
            address: IPAddress.Parse("192.0.2.20"));
        census.Register(giga1);
        census.Register(giga2);
        census.Register(blue);
        metrics.RecordAccepted("giganews");
        metrics.RecordAccepted("giganews");
        metrics.RecordAccepted("blueworld-hosting");
        metrics.RecordArticleReceived("giganews", 80);
        metrics.RecordCheck("blueworld-hosting");

        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, census: census, peerMetrics: metrics, transit: store);
        service.Emit();

        var peers = logger.Entries.Where(static e => e.EventId.Id == 2403).ToArray();
        Assert.Equal(2, peers.Length);
        var blueRow = Assert.Single(peers, static e => (string)e.Properties["PeerName"]! == "blueworld-hosting");
        var gigaRow = Assert.Single(peers, static e => (string)e.Properties["PeerName"]! == "giganews");
        Assert.Equal(1, GetInt32(blueRow, "Active"));
        Assert.Equal(1L, GetInt64(blueRow, "Accepted"));
        Assert.Equal(0L, GetInt64(blueRow, "Articles"));
        Assert.Equal(1L, GetInt64(blueRow, "Checks"));
        Assert.Equal(2, GetInt32(gigaRow, "Active"));
        Assert.Equal(2L, GetInt64(gigaRow, "Accepted"));
        Assert.Equal(1L, GetInt64(gigaRow, "Articles"));
        Assert.Equal(0L, GetInt64(gigaRow, "Checks"));
    }

    [Fact]
    public async Task PeerSnapshots_DoNotDuplicateWithinOneEmit()
    {
        var store = CreatePeerStore(("giganews", 100), ("usenet-ninja", 10));
        var metrics = new TransitPeerMetrics();
        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, peerMetrics: metrics, transit: store);

        service.Emit();

        var names = logger.Entries
            .Where(static e => e.EventId.Id == 2403)
            .Select(static e => (string)e.Properties["PeerName"]!)
            .ToArray();
        Assert.Equal(["giganews", "usenet-ninja"], names);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task AvgMbps_KnownSixtySecondInterval_IsDecimalMegabits()
    {
        const long bytes = 3_227_779_887L;
        var store = CreatePeerStore(("giganews", 100));
        var metrics = new TransitPeerMetrics();
        RecordReceivedBytes(metrics, "giganews", bytes);
        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, peerMetrics: metrics, transit: store);

        service.Emit();

        var row = Assert.Single(logger.Entries, static e => e.EventId.Id == 2403);
        Assert.Equal(bytes, GetInt64(row, "Bytes"));
        Assert.Equal(430.37, GetDouble(row, "AverageMbps"), precision: 2);
        Assert.Contains("avg_mbps=430.37", row.Message, StringComparison.Ordinal);
        Assert.Contains($"bytes={bytes}", row.Message, StringComparison.Ordinal);

        var mebibytePerSecond = bytes / 60d / (1024d * 1024d);
        var megabytePerSecond = bytes / 60d / 1_000_000d;
        Assert.InRange(GetDouble(row, "AverageMbps"), 430, 431);
        Assert.True(GetDouble(row, "AverageMbps") > mebibytePerSecond * 2);
        Assert.True(GetDouble(row, "AverageMbps") > megabytePerSecond * 2);
    }

    [Fact]
    public async Task AvgMbps_ZeroBytes_IsZero()
    {
        var store = CreatePeerStore(("giganews", 100));
        var metrics = new TransitPeerMetrics();
        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, peerMetrics: metrics, transit: store);

        service.Emit();

        var row = Assert.Single(logger.Entries, static e => e.EventId.Id == 2403);
        Assert.Equal(0L, GetInt64(row, "Bytes"));
        Assert.Equal(0d, GetDouble(row, "AverageMbps"));
        Assert.Contains("avg_mbps=0.00", row.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AvgMbps_UsesSameIntervalBytes_AndScalesWithPeriod()
    {
        const long bytes = 3_227_779_887L;
        var store = CreatePeerStore(("giganews", 100));
        var metrics = new TransitPeerMetrics();
        RecordReceivedBytes(metrics, "giganews", bytes);
        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(
            logger,
            peerMetrics: metrics,
            transit: store,
            period: TimeSpan.FromSeconds(30));

        service.Emit();

        var row = Assert.Single(logger.Entries, static e => e.EventId.Id == 2403);
        Assert.Equal(bytes, GetInt64(row, "Bytes"));
        var expected = ApplicationTelemetryService.AverageMbps(bytes, TimeSpan.FromSeconds(30));
        Assert.Equal(expected, GetDouble(row, "AverageMbps"), precision: 6);
        Assert.Equal(860.74, GetDouble(row, "AverageMbps"), precision: 2);
        Assert.Equal(
            ApplicationTelemetryService.AverageMbps(GetInt64(row, "Bytes"), TimeSpan.FromSeconds(30)),
            GetDouble(row, "AverageMbps"),
            precision: 6);
    }

    [Fact]
    public async Task PipelineMetrics_EmitAdditionalInformationRows()
    {
        var pipeline = new IngestionPipelineMetrics();
        RecordAgo(pipeline.RecordWorkerItem, TimeSpan.FromMilliseconds(8));
        pipeline.AddBusyTicks((long)(0.008 * Stopwatch.Frequency));
        RecordAgo(pipeline.RecordPublishAndConfirm, TimeSpan.FromMilliseconds(4));
        RecordAgo(pipeline.RecordHandoff, TimeSpan.FromMilliseconds(5));
        RecordAgo(pipeline.RecordTakeThisEnqueue, TimeSpan.FromMilliseconds(12));
        pipeline.RecordConfirmFailure();
        pipeline.RecordOccupiedFullWait(Stopwatch.GetTimestamp() - (long)(0.003 * Stopwatch.Frequency));

        var logger = new RecordingLogger<ApplicationTelemetryService>();
        await using var service = CreateService(logger, pipelineMetrics: pipeline);
        service.Emit();

        Assert.Equal(8, logger.Entries.Count);
        Assert.All(logger.Entries, static e => Assert.Equal(LogLevel.Information, e.Level));
        var worker = Assert.Single(logger.Entries, static e => e.EventId.Id == 2404);
        var handoff = Assert.Single(logger.Entries, static e => e.EventId.Id == 2405);
        var takeThis = Assert.Single(logger.Entries, static e => e.EventId.Id == 2406);
        var pool = Assert.Single(logger.Entries, static e => e.EventId.Id == 2407);
        var overviewQueue = Assert.Single(logger.Entries, static e => e.EventId.Id == 2408);
        Assert.Equal(1L, GetInt64(worker, "Items"));
        Assert.True(GetDouble(worker, "ArticlesPerSec") > 0);
        Assert.Equal(1L, GetInt64(handoff, "ConfirmFailures"));
        Assert.Equal(0L, GetInt64(handoff, "ConfirmTimeouts"));
        Assert.Equal(1L, GetInt64(takeThis, "OccupiedFullWaits"));
        Assert.Contains("IngestionWorker items=", worker.Message, StringComparison.Ordinal);
        Assert.Contains("OverviewDbHandoff gate_p95_ms=", handoff.Message, StringComparison.Ordinal);
        Assert.Contains("TakeThisPipeline occupied=", takeThis.Message, StringComparison.Ordinal);
        Assert.Contains("IngestionWorkerPool workers=", pool.Message, StringComparison.Ordinal);
        Assert.Contains("OverviewDbWorkQueue articles=", overviewQueue.Message, StringComparison.Ordinal);

        logger.Entries.Clear();
        service.Emit();
        var secondWorker = Assert.Single(logger.Entries, static e => e.EventId.Id == 2404);
        Assert.Equal(0L, GetInt64(secondWorker, "Items"));
    }

    private static void RecordAgo(Action<long> record, TimeSpan ago)
    {
        var start = Stopwatch.GetTimestamp() - (long)(ago.TotalSeconds * Stopwatch.Frequency);
        record(start);
    }

    private static ApplicationTelemetryService CreateService(
        RecordingLogger<ApplicationTelemetryService> logger,
        IAsyncInterval? interval = null,
        IArticleIngestionQueue? queue = null,
        INntpSessionCensus? census = null,
        IHistoryLookupMetrics? history = null,
        ITransitPeerMetrics? peerMetrics = null,
        TransitConfigurationStore? transit = null,
        TimeSpan? period = null,
        IngestionPipelineMetrics? pipelineMetrics = null) =>
        new(
            queue ?? new ArticleIngestionQueue(new ArticleIngestionOptions(), 64),
            census ?? new NntpSessionCensus(),
            logger,
            history,
            timeProvider: null,
            interval,
            period ?? ApplicationTelemetryService.Period,
            transit,
            peerMetrics,
            pipelineMetrics);

    private static void RecordReceivedBytes(TransitPeerMetrics metrics, string peerId, long bytes)
    {
        var remaining = bytes;
        while (remaining > 0)
        {
            var chunk = remaining > int.MaxValue ? int.MaxValue : (int)remaining;
            metrics.RecordArticleReceived(peerId, chunk);
            remaining -= chunk;
        }
    }

    private static NntpSession CreateSession(INntpSessionCensus? census = null)
    {
        var input = new Pipe();
        var output = new Pipe();
        var connection = new HoldingConnection(input.Reader, output.Writer);
        return new NntpSession(
            connection,
            NullLogger<NntpSession>.Instance,
            sessionCensus: census);
    }

    private static NntpSession CreatePeerSession(
        string peerId,
        INntpSessionCensus? census = null,
        ITransitPeerMetrics? metrics = null,
        int maxIncoming = 100,
        IPAddress? address = null)
    {
        address ??= IPAddress.Parse("192.0.2.10");
        var input = new Pipe();
        var output = new Pipe();
        var connection = new HoldingConnection(input.Reader, output.Writer, address);
        var auth = TransitTestPeers.ForAllowFrom(address, name: peerId, maxIncoming: maxIncoming);
        return new NntpSession(
            connection,
            NullLogger<NntpSession>.Instance,
            transitPeerAuthorization: auth,
            sessionCensus: census,
            peerMetrics: metrics);
    }

    private static TransitConfigurationStore CreatePeerStore(params (string Id, int MaxIncoming)[] peers)
    {
        var options = new TransitPeersOptions();
        foreach (var (id, maxIncoming) in peers)
        {
            options[id] = TransitTestPeers.Peer(maxIncoming: maxIncoming, allowFrom: ["192.0.2.0/24"]);
        }

        var store = new TransitConfigurationStore();
        store.Replace(TransitConfigurationSnapshot.Create(options));
        return store;
    }

    private static InboundArticle Article(string messageId, string body = "body\r\n") =>
        CanonicalArticleText.CreateQueued(messageId, InboundArticleProducer.TakeThis, body);

    private static long GetInt64(LogRow row, string name) =>
        Convert.ToInt64(row.Properties[name], System.Globalization.CultureInfo.InvariantCulture);

    private static int GetInt32(LogRow row, string name) =>
        Convert.ToInt32(row.Properties[name], System.Globalization.CultureInfo.InvariantCulture);

    private static double GetDouble(LogRow row, string name) =>
        Convert.ToDouble(row.Properties[name], System.Globalization.CultureInfo.InvariantCulture);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(1, cts.Token);
        }
    }

    private sealed class ControllableInterval : IAsyncInterval
    {
        private readonly Channel<bool> _ticks = Channel.CreateUnbounded<bool>();

        public int WaitCount;

        public ValueTask<bool> WaitNextAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref WaitCount);
            return _ticks.Reader.ReadAsync(cancellationToken);
        }

        public void ReleaseTick() => _ticks.Writer.TryWrite(true);

        public ValueTask DisposeAsync()
        {
            _ticks.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingCensus : INntpSessionCensus
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Concurrent;
        public int MaxConcurrent;

        public void Register(NntpSession session)
        {
        }

        public void Unregister(NntpSession session)
        {
        }

        public void ReleasePeerAttribution(NntpSession session)
        {
        }

        public NntpSessionCensusSnapshot Capture()
        {
            var current = Interlocked.Increment(ref Concurrent);
            UpdateMax(current);
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            Interlocked.Decrement(ref Concurrent);
            return new NntpSessionCensusSnapshot(0, 0, 0, 0, 0, 0, 0, 0);
        }

        private void UpdateMax(int current)
        {
            while (true)
            {
                var observed = Volatile.Read(ref MaxConcurrent);
                if (current <= observed
                    || Interlocked.CompareExchange(ref MaxConcurrent, current, observed) == observed)
                {
                    return;
                }
            }
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogRow> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    properties[pair.Key] = pair.Value;
                }
            }

            var row = new LogRow(logLevel, eventId, formatter(state, exception), properties);
            lock (Entries)
            {
                Entries.Add(row);
            }
        }
    }

    private sealed record LogRow(
        LogLevel Level,
        EventId EventId,
        string Message,
        Dictionary<string, object?> Properties);

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed class HoldingConnection : INntpConnection
    {
        private readonly CancellationTokenSource _closed = new();

        public HoldingConnection(PipeReader input, PipeWriter output, IPAddress? address = null)
        {
            Input = input;
            Output = output;
            ClientIdentity = ConnectionClientIdentity.Direct(
                new IPEndPoint(address ?? IPAddress.Parse("192.0.2.10"), 40000));
        }

        public PipeReader Input { get; }

        public PipeWriter Output { get; }

        public ConnectionClientIdentity ClientIdentity { get; }

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

        public Task WaitForOutboundDeliveryAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
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

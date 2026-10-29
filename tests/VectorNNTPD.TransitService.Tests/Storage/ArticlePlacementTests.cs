using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Messaging.Cache;
using VectorNNTPD.Common.Transport.ArticleTransfer;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Storage;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Transport.Vatp;

namespace VectorNNTPD.TransitService.Tests.Storage;

public sealed class ArticlePlacementTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-30T00:00:00Z");

    [Fact]
    public void Selector_PrefersHighestAvailableBytes_ThenServerId_ThenFqdn()
    {
        var entries = new StorageServerFleetEntry[]
        {
            Entry("cache02.example", 2, available: 10, port: 563),
            Entry("cache09.example", 9, available: 50, port: 563),
            Entry("cache03.example", 3, available: 50, port: 563),
        };
        Assert.True(StorageServerPlacementSelector.TrySelect(entries, out var selected));
        Assert.Equal(3, selected.ServerId);

        var tiedId = new StorageServerFleetEntry[]
        {
            Entry("m.example", 4, available: 20, port: 563),
            Entry("a.example", 4, available: 20, port: 563),
        };
        Assert.True(StorageServerPlacementSelector.TrySelect(tiedId, out var fqdn));
        Assert.Equal("a.example", fqdn.Fqdn);
    }

    [Fact]
    public void Selector_ExcludesMissingPort_AndEmptySetDoesNotSelect()
    {
        var registry = new StorageServerRegistry();
        registry.ApplyAdvertisement(Advertisement("cache01.example", 1, 100, port: null), Now);
        registry.ApplyAdvertisement(Advertisement("stale.example", 2, 500, port: 563), Now.AddSeconds(-10));
        var active = registry.GetActive(Now);
        Assert.Contains(active, static entry => entry.Fqdn == "cache01.example");
        Assert.DoesNotContain(active, static entry => entry.Fqdn == "stale.example");
        Assert.False(StorageServerPlacementSelector.TrySelect(active, out _));
        Assert.False(StorageServerPlacementSelector.TrySelect([], out _));
    }

    [Theory]
    [InlineData(InboundArticleProducer.TakeThis)]
    [InlineData(InboundArticleProducer.IHave)]
    [InlineData(InboundArticleProducer.Post)]
    public async Task LocalProducer_PlacesOnceAfterPersist(InboundArticleProducer producer)
    {
        var client = new RecordingPlacement();
        var registry = RegistryWithTarget();
        var article = CanonicalArticleText.CreateQueued("<place@example.test>", producer);
        await RunAsync(article, client, registry);
        Assert.Equal(0, client.Calls);
        Assert.False(client.SawPersist);
    }

    [Fact]
    public async Task BackFiller_StoresOnceOnTheLowestServerId()
    {
        var client = new RecordingPlacement();
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var article = CanonicalArticleText.CreateQueued("<bf@example.test>", InboundArticleProducer.BackFiller);
        await RunObservedAsync(article, client, DivergentTargets(), logs);
        Assert.Equal(0, client.Calls);
        Assert.Empty(client.Targets);
        Assert.DoesNotContain(2620, logs.EventIds);
    }

    [Fact]
    public async Task NoEligibleServer_DoesNotDial()
    {
        var client = new RecordingPlacement { FailIfCalled = true };
        var registry = new StorageServerRegistry();
        registry.ApplyAdvertisement(Advertisement("cache01.example", 1, 100, port: null), Now);
        var article = CanonicalArticleText.CreateQueued("<none@example.test>", InboundArticleProducer.TakeThis);
        await RunAsync(article, client, registry, time: Now);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task PlacementFailure_DoesNotRequeue()
    {
        var client = new RecordingPlacement
        {
            Result = new ArticlePlacementResult(ArticlePlacementKind.Conflict),
        };
        var article = CanonicalArticleText.CreateQueued("<conflict@example.test>", InboundArticleProducer.Post);
        await RunAsync(article, client, RegistryWithTarget());
        Assert.Equal(0, client.Calls);
    }

    [Theory]
    [InlineData(InboundArticleProducer.TakeThis)]
    [InlineData(InboundArticleProducer.IHave)]
    [InlineData(InboundArticleProducer.Post)]
    public async Task LocalProducer_PlacesOnceAndDoesNotSelectASecondTarget(InboundArticleProducer producer)
    {
        var client = new RecordingPlacement();
        var article = CanonicalArticleText.CreateQueued("<once@example.test>", producer);
        var logs = new ListLogger<IncomingSpoolWriterService>();
        await RunAsync(article, client, TwoTargets(), logs);
        Assert.Equal(0, client.Calls);
        Assert.Empty(client.Targets);
        Assert.Empty(client.Records);
        Assert.DoesNotContain(2620, logs.EventIds);
    }

    [Fact]
    public async Task BackFiller_Duplicate_IsOneStoreAndDoesNotReplicate()
    {
        var client = new RecordingPlacement
        {
            Result = new ArticlePlacementResult(ArticlePlacementKind.Duplicate),
        };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var article = CanonicalArticleText.CreateQueued("<bf-dup@example.test>", InboundArticleProducer.BackFiller);
        await RunObservedAsync(article, client, DivergentTargets(), logs);
        Assert.Equal(0, client.Calls);
        Assert.Empty(client.Targets);
        Assert.DoesNotContain(2621, logs.EventIds);
    }

    [Fact]
    public async Task BackFiller_Conflict_LogsAndDoesNotStoreAgain()
    {
        var client = new RecordingPlacement
        {
            Result = new ArticlePlacementResult(ArticlePlacementKind.Conflict),
        };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var article = CanonicalArticleText.CreateQueued("<bf-conflict@example.test>", InboundArticleProducer.BackFiller);
        await RunObservedAsync(article, client, DivergentTargets(), logs);
        Assert.Equal(0, client.Calls);
        Assert.Empty(client.Targets);
        Assert.DoesNotContain(2622, logs.EventIds);
    }

    [Theory]
    [InlineData(ArticlePlacementKind.RejectedCapacity, 2623)]
    [InlineData(ArticlePlacementKind.RejectedInvalid, 2625)]
    public async Task BackFiller_Rejection_DoesNotTryAnotherServer(ArticlePlacementKind kind, int eventId)
    {
        var client = new RecordingPlacement
        {
            Result = new ArticlePlacementResult(kind),
        };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var article = CanonicalArticleText.CreateQueued("<bf-reject@example.test>", InboundArticleProducer.BackFiller);
        await RunObservedAsync(article, client, DivergentTargets(), logs);
        Assert.Equal(0, client.Calls);
        Assert.Empty(client.Targets);
        Assert.DoesNotContain(eventId, logs.EventIds);
    }

    [Fact]
    public async Task BackFiller_TransportFailure_DoesNotTryAnotherServer()
    {
        var client = new RecordingPlacement
        {
            Result = new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "IOException"),
        };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var article = CanonicalArticleText.CreateQueued("<bf-transport@example.test>", InboundArticleProducer.BackFiller);
        await RunObservedAsync(article, client, DivergentTargets(), logs);
        Assert.Equal(0, client.Calls);
        Assert.Empty(client.Targets);
        Assert.DoesNotContain(2627, logs.EventIds);
    }

    [Fact]
    public async Task PeerProducer_PlacesOnceOnTheHighestFreeSpace()
    {
        var client = new RecordingPlacement();
        var article = CanonicalArticleText.CreateQueued("<peer-rank@example.test>", InboundArticleProducer.Post);
        await RunObservedAsync(article, client, DivergentTargets());
        Assert.Equal(0, client.Calls);
        Assert.Empty(client.Targets);
        Assert.Empty(client.Records);
    }

    [Fact]
    public async Task BackFiller_ConcurrentArticles_DoNotShareStoreState()
    {
        var client = new ConcurrentPlacement();
        var first = CanonicalArticleText.CreateQueued("<bf-a@example.test>", InboundArticleProducer.BackFiller);
        var second = CanonicalArticleText.CreateQueued("<bf-b@example.test>", InboundArticleProducer.BackFiller);
        await RunManyAsync([first, second], client, DivergentTargets(), workers: 2);
        Assert.Equal(0, client.Calls);
        Assert.Empty(client.Targets);
        Assert.Empty(client.Records);
        Assert.NotEqual(first.Record.ArtId, second.Record.ArtId);
    }

    [Fact]
    public async Task BackFiller_SameArticleConcurrently_StoresTwice()
    {
        var client = new ConcurrentPlacement();
        var first = CanonicalArticleText.CreateQueued("<bf-same@example.test>", InboundArticleProducer.BackFiller);
        var second = CanonicalArticleText.CreateQueued("<bf-same@example.test>", InboundArticleProducer.BackFiller);
        Assert.Equal(first.Record.ArtId, second.Record.ArtId);
        await RunManyAsync([first, second], client, DivergentTargets(), workers: 2);
        Assert.Equal(0, client.Calls);
        Assert.Empty(client.Records);
        Assert.Empty(client.Targets);
    }

    [Fact]
    public async Task NoSecondEligible_LeavesSingleCopy()
    {
        var client = new RecordingPlacement();
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var article = CanonicalArticleText.CreateQueued("<single@example.test>", InboundArticleProducer.TakeThis);
        await RunAsync(article, client, RegistryWithTarget(), logs);
        Assert.Equal(0, client.Calls);
        Assert.Empty(client.Targets);
        Assert.DoesNotContain(2620, logs.EventIds);
    }

    [Theory]
    [InlineData(ArticlePlacementKind.Conflict, 2622)]
    [InlineData(ArticlePlacementKind.RejectedCapacity, 2623)]
    [InlineData(ArticlePlacementKind.RejectedInvalid, 2625)]
    [InlineData(ArticlePlacementKind.TransportFailure, 2627)]
    [InlineData(ArticlePlacementKind.AcknowledgementNotObserved, 2629)]
    public async Task FirstFailure_DoesNotAttemptReplica(ArticlePlacementKind kind, int eventId)
    {
        var client = new RecordingPlacement
        {
            Result = new ArticlePlacementResult(kind, kind == ArticlePlacementKind.TransportFailure ? "IOException" : null),
        };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var article = CanonicalArticleText.CreateQueued("<gate@example.test>", InboundArticleProducer.Post);
        await RunAsync(article, client, TwoTargets(), logs);
        Assert.Equal(0, client.Calls);
        Assert.Empty(client.Targets);
        Assert.DoesNotContain(eventId, logs.EventIds);
    }

    [Fact]
    public async Task CancellationBeforeFirstAcceptance_DoesNotAttemptReplica()
    {
        using var cts = new CancellationTokenSource();
        var client = new RecordingPlacement
        {
            CancelAt = CancelPoint.FirstCallReturnsCancelled,
            Cancel = cts,
        };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        await PlaceDirectAsync(
            CanonicalArticleText.CreateQueued("<cancel-first@example.test>", InboundArticleProducer.TakeThis),
            client,
            TwoTargets(),
            cts.Token,
            logs);
        Assert.Equal(1, client.Calls);
        Assert.Equal(ArticlePlacementKind.Cancelled, client.Outcomes[0].Kind);
        Assert.Equal("cache01.example", client.Targets[0].Fqdn);
        Assert.Contains(2628, logs.EventIds);
        Assert.Single(client.Targets);
        Assert.DoesNotContain(logs.EventIds, static id => id >= 2630);
    }

    [Fact]
    public async Task DuplicatePrimary_CompletesAfterOnePlacement()
    {
        var client = new RecordingPlacement
        {
            Result = new ArticlePlacementResult(ArticlePlacementKind.Duplicate),
        };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var article = CanonicalArticleText.CreateQueued("<dup-first@example.test>", InboundArticleProducer.TakeThis);
        await RunAsync(article, client, TwoTargets(), logs);
        Assert.Equal(0, client.Calls);
        Assert.Empty(client.Targets);
        Assert.DoesNotContain(2621, logs.EventIds);
    }

    [Fact]
    public async Task CancellationAfterFirstAcceptance_DoesNotStartReplica()
    {
        using var cts = new CancellationTokenSource();
        var client = new RecordingPlacement
        {
            CancelAt = CancelPoint.AfterFirstAccepted,
            Cancel = cts,
        };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        await PlaceDirectAsync(
            CanonicalArticleText.CreateQueued("<cancel-between@example.test>", InboundArticleProducer.TakeThis),
            client,
            TwoTargets(),
            cts.Token,
            logs);
        Assert.Equal(1, client.Calls);
        Assert.Equal(ArticlePlacementKind.Accepted, client.Outcomes[0].Kind);
        Assert.Equal("cache01.example", client.Targets[0].Fqdn);
        Assert.Contains(2620, logs.EventIds);
        Assert.Single(client.Targets);
        Assert.DoesNotContain(logs.EventIds, static id => id >= 2630);
    }

    [Theory]
    [InlineData(InboundArticleProducer.TakeThis)]
    [InlineData(InboundArticleProducer.IHave)]
    [InlineData(InboundArticleProducer.Post)]
    [InlineData(InboundArticleProducer.BackFiller)]
    public async Task RejectedPressure_RetriesOnceOnTheSameTargetAndPayload(InboundArticleProducer producer)
    {
        var delay = new ImmediatePressureDelay();
        var client = SequenceClient(
            new ArticlePlacementResult(ArticlePlacementKind.RejectedPressure),
            new ArticlePlacementResult(ArticlePlacementKind.Accepted));
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var article = CanonicalArticleText.CreateQueued($"<pressure-ok-{producer}@example.test>", producer);
        await InvokePlacementAsync(article, client, delay.Delay, CancellationToken.None, logs);

        Assert.Equal(TimeSpan.FromSeconds(5), delay.Requested);
        Assert.Equal(1, delay.Calls);
        AssertSamePlacement(article, client, producer);
        Assert.Equal(ArticlePlacementKind.Accepted, client.Outcomes[1].Kind);
        AssertAttempt(logs.Messages, 1, "outcome=RejectedPressure");
        AssertAttempt(logs.Messages, 2, "outcome=Accepted");
        Assert.Equal(2, logs.EventIds.Count(static id => id is 2624 or 2620));
    }

    [Theory]
    [InlineData(InboundArticleProducer.TakeThis)]
    [InlineData(InboundArticleProducer.BackFiller)]
    public async Task RejectedPressure_SecondDuplicate_SettlesOnTheSameTarget(InboundArticleProducer producer)
    {
        var delay = new ImmediatePressureDelay();
        var client = SequenceClient(
            new ArticlePlacementResult(ArticlePlacementKind.RejectedPressure),
            new ArticlePlacementResult(ArticlePlacementKind.Duplicate));
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var article = CanonicalArticleText.CreateQueued($"<pressure-dup-{producer}@example.test>", producer);
        await InvokePlacementAsync(article, client, delay.Delay, CancellationToken.None, logs);

        Assert.Equal(1, delay.Calls);
        Assert.Equal(TimeSpan.FromSeconds(5), delay.Requested);
        AssertSamePlacement(article, client, producer);
        Assert.Equal(ArticlePlacementKind.Duplicate, client.Outcomes[1].Kind);
        AssertAttempt(logs.Messages, 1, "outcome=RejectedPressure");
        AssertAttempt(logs.Messages, 2, "outcome=Duplicate");
        Assert.Contains(2621, logs.EventIds);
    }

    [Theory]
    [InlineData(InboundArticleProducer.TakeThis)]
    [InlineData(InboundArticleProducer.BackFiller)]
    public async Task RejectedPressure_SecondPressure_IsTerminal(InboundArticleProducer producer)
    {
        var delay = new ImmediatePressureDelay();
        var client = SequenceClient(
            new ArticlePlacementResult(ArticlePlacementKind.RejectedPressure),
            new ArticlePlacementResult(ArticlePlacementKind.RejectedPressure));
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var article = CanonicalArticleText.CreateQueued($"<pressure-stop-{producer}@example.test>", producer);
        await InvokePlacementAsync(article, client, delay.Delay, CancellationToken.None, logs);

        Assert.Equal(2, client.Calls);
        Assert.Equal(1, delay.Calls);
        AssertSamePlacement(article, client, producer);
        AssertAttempt(logs.Messages, 1, "outcome=RejectedPressure");
        AssertAttempt(logs.Messages, 2, "outcome=RejectedPressure");
        Assert.Equal(2, logs.Messages.Count(static message => message.Contains("outcome=RejectedPressure", StringComparison.Ordinal)));
        Assert.DoesNotContain(logs.Messages, static message => message.Contains("attempt=3", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(TerminalPlacementOutcomes))]
    public async Task TerminalOutcomes_AreNotRetried(InboundArticleProducer producer, ArticlePlacementKind kind)
    {
        var delay = new ImmediatePressureDelay();
        var client = new RecordingPlacement
        {
            Result = new ArticlePlacementResult(
                kind,
                kind == ArticlePlacementKind.TransportFailure ? "IOException" : null),
        };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var article = CanonicalArticleText.CreateQueued($"<terminal-{producer}-{kind}@example.test>", producer);
        await InvokePlacementAsync(article, client, delay.Delay, CancellationToken.None, logs);

        Assert.Equal(1, client.Calls);
        Assert.Equal(0, delay.Calls);
        AssertOriginalTarget(producer, client.Targets[0]);
        AssertSameArtData(article.Record, client.Records[0]);
        Assert.Contains(logs.Messages, message => message.Contains("attempt=1", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, static message => message.Contains("attempt=2", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(InboundArticleProducer.TakeThis)]
    [InlineData(InboundArticleProducer.BackFiller)]
    public async Task CancellationDuringPressureDelay_DoesNotStartAttemptTwo(InboundArticleProducer producer)
    {
        var client = new RecordingPlacement
        {
            Result = new ArticlePlacementResult(ArticlePlacementKind.RejectedPressure),
        };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var article = CanonicalArticleText.CreateQueued($"<pressure-cancel-{producer}@example.test>", producer);
        var pending = InvokePlacementAsync(
            article,
            client,
            async (delay, token) =>
            {
                Assert.Equal(TimeSpan.FromSeconds(5), delay);
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            },
            cts.Token,
            logs);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, client.Calls);
        AssertOriginalTarget(producer, client.Targets[0]);
        AssertSameArtData(article.Record, client.Records[0]);
        AssertAttempt(logs.Messages, 1, "outcome=RejectedPressure");
        Assert.Contains(
            logs.Messages,
            static message => message.Contains("placement cancelled", StringComparison.Ordinal)
                && message.Contains("attempt=1", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, static message => message.Contains("attempt=2", StringComparison.Ordinal));
    }

    public static IEnumerable<object[]> TerminalPlacementOutcomes()
    {
        var producers = new[] { InboundArticleProducer.TakeThis, InboundArticleProducer.BackFiller };
        var kinds = new[]
        {
            ArticlePlacementKind.RejectedCapacity,
            ArticlePlacementKind.Conflict,
            ArticlePlacementKind.RejectedInvalid,
            ArticlePlacementKind.TransportFailure,
            ArticlePlacementKind.AcknowledgementNotObserved,
            ArticlePlacementKind.Cancelled,
        };
        foreach (var producer in producers)
        {
            foreach (var kind in kinds)
            {
                yield return [producer, kind];
            }
        }
    }

    [Fact]
    public void Placement_DoesNotReferenceLookupOrReadPool()
    {
        var placementFields = typeof(ArticlePlacementClient).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(placementFields, static field => field.FieldType.Name.Contains("Lookup", StringComparison.Ordinal));
        var writerFields = typeof(IncomingSpoolWriterService).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(writerFields, static field => field.FieldType.Name.Contains("Lookup", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(StorageArticleLookupService).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            static method => method.Name.Contains("Replica", StringComparison.Ordinal)
                || method.Name.Contains("Primary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlacementClient_SocketException_ReportsErrorCodeHResultAndMessage()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var client = new ArticlePlacementClient(NullLogger<ArticlePlacementClient>.Instance)
        {
            TestTcpConnectHost = "127.0.0.1",
        };
        var article = CanonicalArticleText.CreateQueued("<socket-diag@example.test>", InboundArticleProducer.TakeThis);
        var result = await client.PlaceAsync(article.Record, Entry("cache01.usenet.ninja", 1, 100, port), CancellationToken.None);

        Assert.Equal(ArticlePlacementKind.TransportFailure, result.Kind);
        Assert.NotNull(result.Failure);
        Assert.StartsWith("SocketException: SocketErrorCode=", result.Failure, StringComparison.Ordinal);
        Assert.Contains("SocketErrorCode=ConnectionRefused", result.Failure, StringComparison.Ordinal);
        Assert.Contains(" HResult=", result.Failure, StringComparison.Ordinal);
        Assert.Contains(" Message=", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void PlacementClient_DoesNotUseReadConnectionPool()
    {
        var fields = typeof(ArticlePlacementClient).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(fields, static field => field.FieldType == typeof(VatpConnectionPool));
        Assert.False(typeof(VatpArticleClient).IsAssignableTo(typeof(IArticlePlacementClient)));
    }

    [Fact]
    public async Task Transfer_ResultAccepted_AndWindowPacing()
    {
        var article = CanonicalArticleText.CreateQueued("<xfer@example.test>", InboundArticleProducer.TakeThis).Record;
        Assert.True(ArticlePlacementMeta.TryFromRecord(article, out var meta));
        var limits = new ArticleTransferLimits
        {
            InitialStreamWindowBytes = 32,
            MaxStreamCreditBytes = 1024 * 1024,
            DefaultMaxFramePayload = 64,
        };
        Assert.True(article.ArtSize > 32);
        await using var link = new Link();
        var server = Task.Run(() => ScriptedServer.RunAsync(link.Server, result: (byte)ArticlePlacementKind.Accepted));
        var result = await VatpPlacementTransfer.PlaceAsync(
            link.Client,
            article,
            meta,
            limits,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        Assert.Equal(ArticlePlacementKind.Accepted, result.Kind);
        Assert.True(await server.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Transfer_DropAfterEnd_IsAcknowledgementNotObserved()
    {
        var article = CanonicalArticleText.CreateQueued("<drop@example.test>", InboundArticleProducer.IHave).Record;
        Assert.True(ArticlePlacementMeta.TryFromRecord(article, out var meta));
        await using var link = new Link();
        var server = Task.Run(() => ScriptedServer.RunAsync(link.Server, result: null));
        var placed = await VatpPlacementTransfer.PlaceAsync(
            link.Client,
            article,
            meta,
            ArticleTransferLimits.Default,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        Assert.Equal(ArticlePlacementKind.AcknowledgementNotObserved, placed.Kind);
        Assert.False(placed.Succeeded);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Transfer_CancelBeforeSend_DoesNotReportSuccess()
    {
        var article = CanonicalArticleText.CreateQueued("<cancel@example.test>", InboundArticleProducer.TakeThis).Record;
        Assert.True(ArticlePlacementMeta.TryFromRecord(article, out var meta));
        await using var link = new Link();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var placed = await VatpPlacementTransfer.PlaceAsync(
            link.Client,
            article,
            meta,
            ArticleTransferLimits.Default,
            TimeSpan.FromSeconds(5),
            cts.Token);
        Assert.Equal(ArticlePlacementKind.Cancelled, placed.Kind);
        Assert.False(placed.Succeeded);
    }

    private static Task RunAsync(
        InboundArticle article,
        RecordingPlacement client,
        StorageServerRegistry registry,
        ListLogger<IncomingSpoolWriterService>? logs = null,
        DateTimeOffset? time = null) =>
        RunCoreAsync(article, client, registry, logs, time);

    private static async Task PlaceDirectAsync(
        InboundArticle article,
        RecordingPlacement client,
        StorageServerRegistry registry,
        CancellationToken cancellationToken,
        ListLogger<IncomingSpoolWriterService>? logs = null)
    {
        var writer = CreateWriter(client, registry, logs, time: null);
        var place = typeof(IncomingSpoolWriterService).GetMethod(
            "PlaceAfterPersistAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(place);
        try
        {
            var pending = (Task)place.Invoke(writer, [article, cancellationToken])!;
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static IncomingSpoolWriterService CreateWriter(
        RecordingPlacement client,
        StorageServerRegistry registry,
        ListLogger<IncomingSpoolWriterService>? logs,
        DateTimeOffset? time)
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        return new IncomingSpoolWriterService(
            queue,
            Options.Create(PlacementOptions()),
            (ILogger<IncomingSpoolWriterService>?)logs ?? NullLogger<IncomingSpoolWriterService>.Instance,
            timeProvider: new FixedTime(time ?? Now),
            placement: client,
            placementRegistry: registry);
    }

    private static async Task RunObservedAsync(
        InboundArticle article,
        RecordingPlacement client,
        StorageServerRegistry registry,
        ListLogger<IncomingSpoolWriterService>? logs = null)
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var writer = new IncomingSpoolWriterService(
            queue,
            Options.Create(PlacementOptions()),
            (ILogger<IncomingSpoolWriterService>?)logs ?? NullLogger<IncomingSpoolWriterService>.Instance,
            timeProvider: new FixedTime(Now),
            placement: client,
            placementRegistry: registry);
        await writer.StartAsync(CancellationToken.None);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(article, CancellationToken.None));
        queue.Complete();
        await writer.StopAsync(CancellationToken.None);
    }

    private static async Task RunManyAsync(
        InboundArticle[] articles,
        ConcurrentPlacement client,
        StorageServerRegistry registry,
        int workers)
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var writer = new IncomingSpoolWriterService(
            queue,
            Options.Create(PlacementOptions(workers)),
            NullLogger<IncomingSpoolWriterService>.Instance,
            timeProvider: new FixedTime(Now),
            placement: client,
            placementRegistry: registry);
        await writer.StartAsync(CancellationToken.None);
        foreach (var article in articles)
        {
            Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(article, CancellationToken.None));
        }

        queue.Complete();
        await writer.StopAsync(CancellationToken.None);
    }

    private static async Task RunCoreAsync(
        InboundArticle article,
        RecordingPlacement client,
        StorageServerRegistry registry,
        ListLogger<IncomingSpoolWriterService>? logs,
        DateTimeOffset? time)
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var writer = new IncomingSpoolWriterService(
            queue,
            Options.Create(PlacementOptions()),
            (ILogger<IncomingSpoolWriterService>?)logs ?? NullLogger<IncomingSpoolWriterService>.Instance,
            timeProvider: new FixedTime(time ?? Now),
            placement: client,
            placementRegistry: registry);
        await writer.StartAsync(CancellationToken.None);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(article, CancellationToken.None));
        queue.Complete();
        await writer.StopAsync(CancellationToken.None);
    }

    private static async Task InvokePlacementAsync(
        InboundArticle article,
        RecordingPlacement client,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken,
        ListLogger<IncomingSpoolWriterService>? logs = null)
    {
        var writer = CreateWriter(
            client,
            TargetsFor(article.Producer),
            logs,
            time: null);
        writer.PressureRetryDelay = delay;
        var methodName = article.Producer == InboundArticleProducer.BackFiller
            ? "StoreBackFillerAsync"
            : "PlaceAfterPersistAsync";
        var place = typeof(IncomingSpoolWriterService).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(place);
        try
        {
            var pending = (Task)place.Invoke(writer, [article, cancellationToken])!;
            await pending;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static RecordingPlacement SequenceClient(params ArticlePlacementResult[] sequence) =>
        new()
        {
            Sequence = sequence,
            ExpectedCalls = sequence.Length,
        };

    private static StorageServerRegistry TargetsFor(InboundArticleProducer producer) =>
        producer == InboundArticleProducer.BackFiller ? DivergentTargets() : TwoTargets();

    private static void AssertOriginalTarget(InboundArticleProducer producer, StorageServerFleetEntry target)
    {
        if (producer == InboundArticleProducer.BackFiller)
        {
            Assert.Equal(2, target.ServerId);
            Assert.Equal("lowid.example", target.Fqdn);
            return;
        }

        Assert.Equal(1, target.ServerId);
        Assert.Equal("cache01.example", target.Fqdn);
    }

    private static void AssertSamePlacement(
        InboundArticle article,
        RecordingPlacement client,
        InboundArticleProducer producer)
    {
        Assert.Equal(2, client.Calls);
        Assert.Equal(2, client.Targets.Count);
        Assert.Equal(2, client.Records.Count);
        AssertOriginalTarget(producer, client.Targets[0]);
        Assert.Equal(client.Targets[0].ServerId, client.Targets[1].ServerId);
        Assert.Equal(client.Targets[0].Fqdn, client.Targets[1].Fqdn);
        Assert.Equal(client.Targets[0].VatpPort, client.Targets[1].VatpPort);
        Assert.Equal(article.Record.ArtId, client.Records[0].ArtId);
        Assert.Equal(article.Record.ArtId, client.Records[1].ArtId);
        AssertSameArtData(article.Record, client.Records[0]);
        AssertSameArtData(article.Record, client.Records[1]);
        AssertSameArtData(client.Records[0], client.Records[1]);
    }

    private static void AssertAttempt(IReadOnlyList<string> messages, int attempt, string outcome) =>
        Assert.Contains(
            messages,
            message => message.Contains($"attempt={attempt}", StringComparison.Ordinal)
                && message.Contains(outcome, StringComparison.Ordinal));

    private static void AssertSameArtData(ArticleRecord expected, ArticleRecord actual)
    {
        Assert.True(MemoryMarshal.TryGetArray(expected.ArtData, out var expectedArray));
        Assert.True(MemoryMarshal.TryGetArray(actual.ArtData, out var actualArray));
        Assert.Same(expectedArray.Array, actualArray.Array);
        Assert.Equal(expectedArray.Offset, actualArray.Offset);
        Assert.Equal(expectedArray.Count, actualArray.Count);
    }

    private static NntpdOptions PlacementOptions(int workers = 1) =>
        new()
        {
            ArticleIngestion = new ArticleIngestionOptions
            {
                MinWorkers = workers,
                MaxWorkers = workers,
                ScaleIntervalSeconds = 3600,
                OverviewDbMinPublisherWorkers = 1,
                OverviewDbMaxPublisherWorkers = 1,
            },
        };

    private static StorageServerRegistry TwoTargets()
    {
        var registry = RegistryWithTarget();
        registry.ApplyAdvertisement(Advertisement("cache02.example", 2, 100, 564), Now);
        return registry;
    }

    private static StorageServerRegistry DivergentTargets()
    {
        var registry = new StorageServerRegistry();
        registry.ApplyAdvertisement(Advertisement("lowid.example", 2, 10, 564), Now);
        registry.ApplyAdvertisement(Advertisement("highspace.example", 9, 5000, 563), Now);
        return registry;
    }

    private static StorageServerRegistry RegistryWithTarget()
    {
        var registry = new StorageServerRegistry();
        registry.ApplyAdvertisement(Advertisement("cache01.example", 1, 1000, 563), Now);
        return registry;
    }

    private static StorageServerAdvertisement Advertisement(string fqdn, int serverId, long available, int? port) =>
        new(1, serverId, fqdn, 10_000, 10_000 - available, available, Now, port);

    private static StorageServerFleetEntry Entry(string fqdn, int serverId, long available, int? port) =>
        new(serverId, fqdn, 10_000, 10_000 - available, available, Now, port);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private enum CancelPoint
    {
        None,
        FirstCallReturnsCancelled,
        AfterFirstAccepted,
    }

    private sealed class ConcurrentPlacement : IArticlePlacementClient
    {
        private readonly Barrier _barrier = new(2);
        private readonly List<StorageServerFleetEntry> _targets = [];
        private readonly List<ArticleRecord> _records = [];
        private readonly object _gate = new();

        public int Calls { get; private set; }

        public IReadOnlyList<StorageServerFleetEntry> Targets
        {
            get
            {
                lock (_gate)
                {
                    return [.. _targets];
                }
            }
        }

        public IReadOnlyList<ArticleRecord> Records
        {
            get
            {
                lock (_gate)
                {
                    return [.. _records];
                }
            }
        }

        public TaskCompletionSource BothStored { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ArticlePlacementResult> PlaceAsync(
            ArticleRecord record,
            StorageServerFleetEntry target,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Calls++;
                _targets.Add(target);
                _records.Add(record);
            }

            _barrier.SignalAndWait(cancellationToken);
            BothStored.TrySetResult();
            return ValueTask.FromResult(new ArticlePlacementResult(ArticlePlacementKind.Accepted));
        }
    }

    private sealed class RecordingPlacement : IArticlePlacementClient
    {
        private readonly List<StorageServerFleetEntry> _targets = [];
        private readonly List<ArticleRecord> _records = [];
        private readonly List<ArticlePlacementResult> _outcomes = [];

        public int Calls { get; private set; }

        public bool SawPersist { get; private set; }

        public bool FailIfCalled { get; init; }

        public ArticleRecord Last { get; private set; }

        public ArticlePlacementResult Result { get; init; } = new(ArticlePlacementKind.Accepted);

        public ArticlePlacementResult[]? Sequence { get; init; }

        public int ExpectedCalls { get; init; } = 1;

        public CancelPoint CancelAt { get; init; }

        public CancellationTokenSource? Cancel { get; init; }

        public Action<int, StorageServerFleetEntry>? BeforeStore { get; init; }

        public IReadOnlyList<StorageServerFleetEntry> Targets => _targets;

        public IReadOnlyList<ArticleRecord> Records => _records;

        public IReadOnlyList<ArticlePlacementResult> Outcomes => _outcomes;

        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ArticlePlacementResult> PlaceAsync(
            ArticleRecord record,
            StorageServerFleetEntry target,
            CancellationToken cancellationToken)
        {
            if (FailIfCalled)
            {
                throw new InvalidOperationException("Placement dialed without an eligible server.");
            }

            var call = ++Calls;
            SawPersist = true;
            Last = record;
            _targets.Add(target);
            _records.Add(record);
            BeforeStore?.Invoke(call, target);

            ArticlePlacementResult outcome;
            if (CancelAt == CancelPoint.FirstCallReturnsCancelled && call == 1)
            {
                Cancel!.Cancel();
                outcome = new ArticlePlacementResult(ArticlePlacementKind.Cancelled);
            }
            else if (CancelAt == CancelPoint.AfterFirstAccepted && call == 1)
            {
                outcome = new ArticlePlacementResult(ArticlePlacementKind.Accepted);
                Cancel!.Cancel();
            }
            else if (cancellationToken.IsCancellationRequested)
            {
                outcome = new ArticlePlacementResult(ArticlePlacementKind.Cancelled);
            }
            else
            {
                outcome = Sequence is { } sequence && call - 1 < sequence.Length
                    ? sequence[call - 1]
                    : Result;
            }

            _outcomes.Add(outcome);
            if (call >= ExpectedCalls)
            {
                Finished.TrySetResult();
            }

            return ValueTask.FromResult(outcome);
        }
    }

    private sealed class ImmediatePressureDelay
    {
        public int Calls { get; private set; }

        public TimeSpan Requested { get; private set; }

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            Calls++;
            Requested = delay;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<int> EventIds { get; } = [];

        public List<string> Messages { get; } = [];

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
            EventIds.Add(eventId.Id);
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class Link : IAsyncDisposable
    {
        private readonly Pipe _toServer = new();
        private readonly Pipe _toClient = new();

        public Link()
        {
            Client = new PipeStream(_toClient.Reader, _toServer.Writer);
            Server = new PipeStream(_toServer.Reader, _toClient.Writer);
        }

        public Stream Client { get; }

        public Stream Server { get; }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    private sealed class PipeStream(PipeReader reader, PipeWriter writer) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (result.Buffer.IsEmpty && result.IsCompleted)
            {
                return 0;
            }

            var length = (int)Math.Min(buffer.Length, result.Buffer.Length);
            var written = 0;
            foreach (var segment in result.Buffer.Slice(0, length))
            {
                segment.Span.CopyTo(buffer.Span[written..]);
                written += segment.Length;
            }

            reader.AdvanceTo(result.Buffer.GetPosition(length));
            return length;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var copied = buffer.ToArray();
            await writer.WriteAsync(copied, cancellationToken).ConfigureAwait(false);
            var flushed = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (flushed.IsCompleted)
            {
                throw new IOException("peer closed");
            }
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await writer.CompleteAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
            }

            try
            {
                await reader.CompleteAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
            }
        }
    }

    private static class ScriptedServer
    {
        public static async Task<bool> RunAsync(Stream stream, byte? result)
        {
            var pending = new List<byte>();
            var buffer = new byte[8192];
            var windows = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    return windows > 0 || result is null;
                }

                pending.AddRange(buffer.AsSpan(0, read).ToArray());
                while (pending.Count > 0)
                {
                    var parsed = VatpFrameParser.ParseOneFrame(pending.ToArray(), VatpProtocol.DefaultMaxFramePayload);
                    if (parsed.Status == VatpFrameParseStatus.Incomplete)
                    {
                        break;
                    }

                    if (parsed.Status != VatpFrameParseStatus.Success || parsed.Frame is not { } frame)
                    {
                        return false;
                    }

                    pending.RemoveRange(0, checked((int)parsed.ConsumedBytes));
                    if (frame.Header.Type == VatpFrameType.Hello)
                    {
                        var hello = VatpFrameEncoder.ToSingleBuffer(
                            VatpFrameEncoder.EncodeHello(VatpProtocol.DefaultMaxFramePayload));
                        await stream.WriteAsync(hello);
                    }
                    else if (frame.Header.Type == VatpFrameType.Meta
                        && TryDecodeMeta(frame.Payload, out var meta)
                        && meta.ArtSize > 0)
                    {
                        var grant = VatpFrameEncoder.ToSingleBuffer(
                            VatpFrameEncoder.EncodeWindow(frame.Header.StreamId, (uint)meta.ArtSize));
                        await stream.WriteAsync(grant);
                    }
                    else if (frame.Header.Type == VatpFrameType.Data && frame.Payload.Length > 0)
                    {
                        windows++;
                        var window = VatpFrameEncoder.ToSingleBuffer(
                            VatpFrameEncoder.EncodeWindow(frame.Header.StreamId, checked((uint)frame.Payload.Length)));
                        await stream.WriteAsync(window);
                    }
                    else if (frame.Header.Type == VatpFrameType.End)
                    {
                        if (result is byte outcome)
                        {
                            var encoded = VatpFrameEncoder.ToSingleBuffer(
                                VatpFrameEncoder.EncodeResult(frame.Header.StreamId, outcome));
                            await stream.WriteAsync(encoded);
                        }
                        else
                        {
                            await stream.DisposeAsync();
                        }

                        return true;
                    }
                }
            }
        }

        private static bool TryDecodeMeta(ReadOnlySequence<byte> payload, out ArticleCanonicalTransferMeta meta)
        {
            var bytes = new byte[payload.Length];
            payload.CopyTo(bytes);
            return VatpMetaCodec.TryDecode(bytes, out meta, out _);
        }
    }
}

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Articles.OverviewDb;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.ArticleIngestion.OverviewDb;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.TransitService.Diagnostics;
using VectorNNTPD.TransitService.Hosting;
using VectorNNTPD.TransitService.Logging;
using VectorNNTPD.TransitService.Newsgroups;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Transit;
using VectorNNTPD.TransitService.RabbitMq;
using VectorNNTPD.TransitService.RabbitMq.ArticleWork;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.RabbitMq;
using VectorNNTPD.TransitService.Tests.TestDoubles;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.TransitService.Tests.ArticleIngestion;

/// <summary>Ingestion-worker OverviewDB handoff success, requeue, and topology contracts.</summary>
[Collection(SerilogCollection.Name)]
public sealed class IncomingSpoolOverviewHandoffTests
{
    [Fact]
    public async Task Topology_DeclaresOverviewDbClassicQueue_WithoutExchangeOrBinding()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        var topology = new RabbitMqTopologyService(rabbit, NullLogger<RabbitMqTopologyService>.Instance);

        await rabbit.StartAsync(CancellationToken.None);
        await topology.StartAsync(CancellationToken.None);

        var connection = factory.LastConnection ?? throw new InvalidOperationException("Expected a connected fake.");
        var overview = Assert.Single(
            connection.QueueDeclarations,
            static queue => queue.Name == OverviewDbTopology.QueueName);
        Assert.Equal("overviewdb.queue", overview.Name);
        Assert.True(overview.Durable);
        Assert.False(overview.Exclusive);
        Assert.False(overview.AutoDelete);
        var arguments = overview.Arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        Assert.True(arguments.TryGetValue(OverviewDbTopology.QueueTypeArgumentName, out var queueType));
        Assert.Equal(OverviewDbTopology.QuorumQueueType, queueType);
        Assert.False(arguments.ContainsKey("x-message-ttl"));
        Assert.False(arguments.ContainsKey("x-expires"));
        Assert.Single(arguments);
        Assert.DoesNotContain(connection.ExchangeDeclarations, static e => e.Name == OverviewDbTopology.QueueName);
        Assert.DoesNotContain(connection.BindingDeclarations, static b => b.Queue == OverviewDbTopology.QueueName);
    }

    [Fact]
    public async Task PublishSuccess_CompletesWorkerAndPersistsArticle()
    {
        var overview = new RecordingOverviewDbHandoffPublisher();
        var inbound = CreateArticle();
        await RunWorkerAsync(inbound, overview);

        Assert.Equal(1, overview.AttemptCount);
        var payload = Assert.Single(overview.Payloads);
        var decoded = OverviewArticleV1Codec.Decode(payload);
        Assert.True(Encoding.ASCII.GetBytes(inbound.MessageId).AsSpan().SequenceEqual(decoded.MessageId));
        Assert.Equal(
            new[] { "alt.test", "rec.test" }.Select(static g => Encoding.ASCII.GetBytes(g)).ToList(),
            decoded.Newsgroups);
        Assert.Equal((uint)inbound.Record.ArtSize, decoded.Bytes);
        Assert.Equal((uint)inbound.Record.ArtLines, decoded.Lines);
    }

    [Fact]
    public async Task Worker_RecordsOneItemOnPipelineMetrics()
    {
        var overview = new RecordingOverviewDbHandoffPublisher();
        var pipeline = new IngestionPipelineMetrics();
        var inbound = CreateArticle();
        await RunWorkerAsync(inbound, overview, pipeline);

        var snapshot = pipeline.CaptureInterval();
        Assert.Equal(1, snapshot.WorkerItems);
        Assert.Equal(1, snapshot.ToPublishStart.Count);
        Assert.Equal(1, snapshot.Encode.Count);
        Assert.Equal(1, snapshot.News.Count);
        Assert.True(snapshot.BusyTicks > 0);
    }

    [Fact]
    public async Task PublishFailure_RequeuesUntilConfirm_ThenCompletes()
    {
        var overview = new RecordingOverviewDbHandoffPublisher { RemainingFailures = 1 };
        var inbound = CreateArticle();
        await RunWorkerAsync(inbound, overview);

        Assert.Equal(2, overview.AttemptCount);
        Assert.Single(overview.Payloads);
    }

    [Fact]
    public async Task UnroutableReturn_RequeuesSameInboundArticle_UntilConfirm()
    {
        var overview = new RecordingOverviewDbHandoffPublisher
        {
            RemainingFailures = 1,
            TransientPublishException = new InvalidOperationException(
                "RabbitMQ returned the OverviewDB handoff as unroutable."),
        };
        var inbound = CreateArticle();
        await RunWorkerAsync(inbound, overview);

        Assert.Equal(2, overview.AttemptCount);
        Assert.Single(overview.Payloads);
    }

    [Fact]
    public async Task PublishFailure_ArticlePersistsBeforeConfirm_AndWorkItemRetries()
    {
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var overview = new RecordingOverviewDbHandoffPublisher
        {
            RemainingFailures = 1,
            BlockOnFailure = block,
        };
        var diagnostics = new SignalingFeedDiagnostics();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var writer = CreateWriter(queue, overview, diagnostics);
        await writer.StartAsync(CancellationToken.None);
        var inbound = CreateArticle();
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(inbound, CancellationToken.None));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var end = await diagnostics.Ended.Task.WaitAsync(cts.Token);

        // Article path continues after OverviewDB work enqueue; Rabbit confirm is background.
        Assert.False(end.Persisted);
        await overview.FirstAttempt.WaitAsync(cts.Token);
        Assert.Equal(1, overview.AttemptCount);
        Assert.Empty(overview.Payloads);

        block.TrySetResult();
        using var retryWait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (overview.AttemptCount < 2 || overview.Payloads.Count < 1)
        {
            retryWait.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, retryWait.Token);
        }

        queue.Complete();
        await writer.StopAsync(CancellationToken.None);
        Assert.Equal(2, overview.AttemptCount);
        Assert.Single(overview.Payloads);
    }

    [Fact]
    public void Worker_HasNoOverviewDbRpcOrDatabaseDependency()
    {
        var ctor = typeof(IncomingSpoolWriterService).GetConstructors().Single();
        var types = ctor.GetParameters().Select(static p => p.ParameterType).ToArray();
        Assert.Contains(typeof(IOverviewDbHandoffPublisher), types);
        Assert.DoesNotContain(types, static t => t.Name.Contains("Persister", StringComparison.Ordinal));
        Assert.DoesNotContain(types, static t => t == typeof(IRabbitMqRpcChannel));
        Assert.DoesNotContain(types, static t => t.Name.Contains("NntpDb", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(types, static t => t.Name.Contains("MySql", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(types, static t => t.Name.Contains("Http", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(types, static t => t.Name.Contains("Grpc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Host_RegistersOverviewDbHandoffPublisher_NotAnRpcClient()
    {
        var builder = Host.CreateApplicationBuilder();
        TestHostFactory.ConfigureNntpdTestHost(builder);
        builder.ConfigureNntpdLogging(static lc => lc.MinimumLevel.Fatal());
        builder.Services.AddNntpTransitHosting(includePlaceholderService: false);

        using var host = builder.Build();
        var publisher = host.Services.GetRequiredService<IOverviewDbHandoffPublisher>();
        Assert.IsType<OverviewDbHandoffPublisher>(publisher);
        Assert.Same(IngestionPipelineMetrics.Shared, host.Services.GetRequiredService<IngestionPipelineMetrics>());
        Assert.IsNotType<ArticleWorkRpcService>(publisher);
        var services = host.Services.GetServices<IApplicationService>().Select(static s => s.GetType()).ToArray();
        Assert.DoesNotContain(services, static t => t.Name.Contains("OverviewDbClient", StringComparison.Ordinal));
        Assert.DoesNotContain(services, static t => t.Name.Contains("OverviewDbRpc", StringComparison.Ordinal));
    }

    private static async Task RunWorkerAsync(
        InboundArticle inbound,
        RecordingOverviewDbHandoffPublisher overview,
        IngestionPipelineMetrics? pipelineMetrics = null)
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var writer = CreateWriter(queue, overview, pipelineMetrics: pipelineMetrics);
        await writer.StartAsync(CancellationToken.None);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(inbound, CancellationToken.None));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await overview.Confirmed.WaitAsync(cts.Token);
        queue.Complete();
        await writer.StopAsync(CancellationToken.None);
    }

    private static IncomingSpoolWriterService CreateWriter(
        IArticleIngestionQueue queue,
        IOverviewDbHandoffPublisher overview,
        IFeedDiagnostics? feedDiagnostics = null,
        IngestionPipelineMetrics? pipelineMetrics = null) =>
        new(
            queue,
            Options.Create(new NntpdOptions
            {
                ArticleIngestion = new ArticleIngestionOptions
                {
                    MinWorkers = 1,
                    MaxWorkers = 1,
                    MaxPublishConcurrency = 1,
                    OverviewDbMinPublisherWorkers = 1,
                    OverviewDbMaxPublisherWorkers = 1,
                    ScaleIntervalSeconds = 3600,
                },
                Transit = new TransitOptions { WantTrash = true, LogTrash = true },
            }),
            NullLogger<IncomingSpoolWriterService>.Instance,
            feedDiagnostics: feedDiagnostics,
            catalogue: new StaticNewsgroupCatalogue(
                NewsgroupSnapshot.Create(
                    [new NewsgroupDefinition("alt.test", string.Empty, 2, 1, NewsgroupPostingStatus.Allowed),
                     new NewsgroupDefinition("rec.test", string.Empty, 2, 1, NewsgroupPostingStatus.Allowed)])),
            overviewHandoff: overview,
            pipelineMetrics: pipelineMetrics);

    private static InboundArticle CreateArticle() =>
        CanonicalArticleText.CreateQueued(
            "<overview@example.test>",
            InboundArticleProducer.TakeThis,
            body: "overview-body\r\n",
            newsgroups: "alt.test,rec.test",
            subject: "overview-subject",
            from: "poster@example.test",
            references: "<prev@example.test>");

    private static RabbitMqService CreateRabbitMqService(FakeRabbitMqConnectionFactory factory)
    {
        var options = RabbitMqOptionsTests.CreateValid();
        options.PoolReconnectBaseDelayMs = 50;
        options.PoolReconnectMaxDelayMs = 50;
        return new RabbitMqService(
            factory,
            Options.Create(options),
            new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTPD.TransitService:nntpd01.usenet.ninja"),
            NullLogger<RabbitMqService>.Instance);
    }

    /// <summary>Signals <see cref="IncomingSpoolWriterService"/> spool-work completion for tests.</summary>
    private sealed class SignalingFeedDiagnostics : IFeedDiagnostics
    {
        /// <summary>Completes when the worker finishes one spool-work attempt.</summary>
        public TaskCompletionSource<(int Bytes, bool Persisted)> Ended { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public bool IsEnabled => false;

        /// <inheritdoc />
        public void OnRejected(string peerName, string remote)
        {
        }

        /// <inheritdoc />
        public FeedSessionProbe? OnAccepted(NntpSession session) => null;

        /// <inheritdoc />
        public void OnReleased(FeedSessionProbe? probe)
        {
        }

        /// <inheritdoc />
        public void RecordTcpBytes(int bytes)
        {
        }

        /// <inheritdoc />
        public void BeginSpoolWork()
        {
        }

        /// <inheritdoc />
        public void EndSpoolWork(int bytes, bool persisted) => Ended.TrySetResult((bytes, persisted));

        /// <inheritdoc />
        public FeedDiagnosticsSnapshot CaptureSnapshot(
            IArticleIngestionQueue queue,
            TransitConfigurationStore store) =>
            FeedDiagnosticsSnapshot.Empty;
    }
}

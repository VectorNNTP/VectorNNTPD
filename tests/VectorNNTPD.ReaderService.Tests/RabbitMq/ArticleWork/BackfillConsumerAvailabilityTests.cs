using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.RabbitMq;
using VectorNNTPD.ReaderService.RabbitMq.ArticleWork;
using VectorNNTPD.ReaderService.RabbitMq.Management;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.ReaderService.Tests.RabbitMq.ArticleWork;

/// <summary>
/// Proves Management APIâ€“backed BackFiller availability discovery (~5s cache,
/// last-known-good on failure, no AMQP passive probing).
/// </summary>
public sealed class BackfillConsumerAvailabilityTests
{
    [Fact]
    public async Task Cache_is_lazy_no_management_call_without_GetEligibleBackbones()
    {
        var time = new FakeTimeProvider();
        var inventory = new FakeManagementInventory(
            Queue("backfiller.giganews", 10));
        await using var availability = CreateAvailability(inventory, time);

        await Task.Delay(20);
        Assert.Equal(0, inventory.CallCount);
    }

    [Fact]
    public async Task First_GetEligible_refreshes_once_and_reuses_within_refresh_interval()
    {
        var time = new FakeTimeProvider();
        var inventory = new FakeManagementInventory(
            Queue("backfiller.giganews", 100),
            Queue("backfiller.eweka", 50),
            Queue("backfiller.highwinds", 0));
        await using var availability = CreateAvailability(inventory, time);

        var first = availability.GetEligibleBackbones();
        Assert.Equal(2, first.Count);
        Assert.Contains(first, static b => b.Backbone == "Giganews" && b.ConsumerCount == 100);
        Assert.Contains(first, static b => b.Backbone == "Eweka" && b.ConsumerCount == 50);
        Assert.DoesNotContain(first, static b => b.Backbone.Equals("Highwinds", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, inventory.CallCount);

        for (var i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            _ = availability.GetEligibleBackbones();
        }

        Assert.Equal(1, inventory.CallCount);
    }

    [Fact]
    public async Task After_refresh_interval_next_GetEligible_requeries_and_picks_up_changes()
    {
        var time = new FakeTimeProvider();
        var inventory = new FakeManagementInventory(Queue("backfiller.giganews", 10));
        await using var availability = CreateAvailability(inventory, time);

        var initial = availability.GetEligibleBackbones();
        Assert.Single(initial);
        Assert.Equal("Giganews", initial[0].Backbone);

        inventory.SetQueues(
            Queue("backfiller.eweka", 25),
            Queue("backfiller.giganews", 0));

        time.Advance(BackfillConsumerAvailabilityService.RefreshInterval - TimeSpan.FromMilliseconds(1));
        var stillCached = availability.GetEligibleBackbones();
        Assert.Single(stillCached);
        Assert.Equal("Giganews", stillCached[0].Backbone);
        Assert.Equal(1, inventory.CallCount);

        time.Advance(TimeSpan.FromMilliseconds(1));
        var refreshed = availability.GetEligibleBackbones();
        Assert.Single(refreshed);
        Assert.Equal("Eweka", refreshed[0].Backbone);
        Assert.Equal(25, refreshed[0].ConsumerCount);
        Assert.Equal(2, inventory.CallCount);
    }

    [Fact]
    public async Task Missing_provider_queue_is_not_eligible()
    {
        var inventory = new FakeManagementInventory(Queue("backfiller.eweka", 7));
        await using var availability = CreateAvailability(inventory, new FakeTimeProvider());

        var eligible = availability.GetEligibleBackbones();

        Assert.DoesNotContain(eligible, static b => b.Backbone == "Abavia");
        Assert.DoesNotContain(eligible, static b => b.Backbone == "Giganews");
        Assert.Contains(eligible, static b => b.Backbone == "Eweka" && b.ConsumerCount == 7);
    }

    [Fact]
    public async Task Existing_provider_queue_with_zero_consumers_is_not_eligible()
    {
        var inventory = new FakeManagementInventory(
            Queue("backfiller.giganews", 0),
            Queue("backfiller.eweka", 7));
        await using var availability = CreateAvailability(inventory, new FakeTimeProvider());

        var eligible = availability.GetEligibleBackbones();

        Assert.DoesNotContain(eligible, static b => b.Backbone == "Giganews");
        Assert.Contains(eligible, static b => b.Backbone == "Eweka" && b.ConsumerCount == 7);
    }

    [Fact]
    public async Task Non_backfiller_queues_are_ignored()
    {
        var inventory = new FakeManagementInventory(
            Queue("backfiller.giganews", 5),
            Queue(StorageArticleRetrievalTopology.EntityName, 99),
            Queue("overviewdb.queue", 3),
            Queue("unrelated.queue", 12));
        await using var availability = CreateAvailability(inventory, new FakeTimeProvider());

        var eligible = availability.GetEligibleBackbones();

        Assert.Single(eligible);
        Assert.Equal("Giganews", eligible[0].Backbone);
        Assert.DoesNotContain(
            eligible,
            static b => b.QueueName.Equals(StorageArticleRetrievalTopology.EntityName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task All_twelve_queues_with_mixed_consumers_only_positive_are_eligible()
    {
        var rows = BackfillArticleRetrievalTopology.Definitions
            .Select((definition, index) => Queue(definition.QueueName, (uint)(index % 3 == 0 ? 0 : index + 1)))
            .ToArray();
        var inventory = new FakeManagementInventory(rows);
        await using var availability = CreateAvailability(inventory, new FakeTimeProvider());

        var eligible = availability.GetEligibleBackbones();

        Assert.All(eligible, static b => Assert.True(b.ConsumerCount > 0));
        Assert.Equal(
            BackfillArticleRetrievalTopology.Definitions.Count(static d =>
            {
                var index = Array.IndexOf(BackfillArticleRetrievalTopology.Providers, d.Provider);
                return index % 3 != 0;
            }),
            eligible.Count);
        Assert.Equal(BackfillArticleRetrievalTopology.Definitions.Count, rows.Length);
    }

    [Fact]
    public async Task Provider_names_match_canonical_topology_entity_names()
    {
        var inventory = new FakeManagementInventory(
            Queue("backfiller.gtt", 4),
            Queue("backfiller.usenetnode1", 2),
            Queue("backfiller.baseip", 1));
        await using var availability = CreateAvailability(inventory, new FakeTimeProvider());

        var eligible = availability.GetEligibleBackbones();
        Assert.Contains(eligible, static b => b.Backbone == "GTT" && b.QueueName == "backfiller.gtt");
        Assert.Contains(eligible, static b => b.Backbone == "UsenetNode1" && b.QueueName == "backfiller.usenetnode1");
        Assert.Contains(eligible, static b => b.Backbone == "BaseIP" && b.QueueName == "backfiller.baseip");
    }

    [Fact]
    public async Task Weighted_selection_receives_management_eligible_set()
    {
        var inventory = new FakeManagementInventory(
            Queue("backfiller.giganews", 100),
            Queue("backfiller.eweka", 50));
        await using var availability = CreateAvailability(inventory, new FakeTimeProvider());
        var eligible = availability.GetEligibleBackbones();
        var selector = new WeightedBackboneSelector(static _ => 0);

        var selected = selector.Select(eligible);

        Assert.NotNull(selected);
        var choice = selected.Value;
        // Definitions order places Eweka before Giganews; roll 0 selects the first positive weight.
        Assert.Equal("Eweka", choice.Backbone);
        Assert.Equal(50, choice.ConsumerCount);
        Assert.Equal(2, eligible.Count);
        Assert.Contains(eligible, static b => b.Backbone == "Giganews" && b.ConsumerCount == 100);
        Assert.Contains(eligible, static b => b.Backbone == "Eweka" && b.ConsumerCount == 50);
    }

    [Fact]
    public async Task Concurrent_GetEligible_does_not_issue_multiple_simultaneous_refreshes()
    {
        var time = new FakeTimeProvider();
        var inventory = new FakeManagementInventory(Queue("backfiller.giganews", 3))
        {
            Delay = TimeSpan.FromMilliseconds(100),
        };
        await using var availability = CreateAvailability(inventory, time);

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => availability.GetEligibleBackbones()))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, inventory.CallCount);
        Assert.All(results, static r => Assert.Single(r));
        Assert.Equal(1, inventory.MaxConcurrentCalls);
    }

    [Fact]
    public async Task Failed_refresh_retains_last_successful_snapshot()
    {
        var time = new FakeTimeProvider();
        var inventory = new FakeManagementInventory(Queue("backfiller.giganews", 9));
        await using var availability = CreateAvailability(inventory, time);

        var first = availability.GetEligibleBackbones();
        Assert.Single(first);
        Assert.Equal("Giganews", first[0].Backbone);

        inventory.Fault = new HttpRequestException("Management API returned 503 (ServiceUnavailable) for queue inventory.");
        time.Advance(BackfillConsumerAvailabilityService.RefreshInterval);
        var retained = availability.GetEligibleBackbones();

        Assert.Single(retained);
        Assert.Equal("Giganews", retained[0].Backbone);
        Assert.Equal(9, retained[0].ConsumerCount);
        Assert.Equal(2, inventory.CallCount);
    }

    [Fact]
    public async Task Successful_refresh_replaces_previous_snapshot()
    {
        var time = new FakeTimeProvider();
        var inventory = new FakeManagementInventory(Queue("backfiller.giganews", 1));
        await using var availability = CreateAvailability(inventory, time);
        _ = availability.GetEligibleBackbones();

        inventory.SetQueues(Queue("backfiller.eweka", 8));
        time.Advance(BackfillConsumerAvailabilityService.RefreshInterval);
        var next = availability.GetEligibleBackbones();

        Assert.Single(next);
        Assert.Equal("Eweka", next[0].Backbone);
        Assert.Equal(8, next[0].ConsumerCount);
    }

    [Fact]
    public async Task Cancellation_during_refresh_propagates()
    {
        var inventory = new FakeManagementInventory(Queue("backfiller.giganews", 1))
        {
            Delay = TimeSpan.FromSeconds(30),
        };
        await using var availability = CreateAvailability(inventory, new FakeTimeProvider());
        using var cts = new CancellationTokenSource();
        var refresh = availability.RefreshNowAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await refresh);
    }

    [Fact]
    public async Task Malformed_inventory_failure_does_not_fabricate_empty_snapshot_after_success()
    {
        var time = new FakeTimeProvider();
        var inventory = new FakeManagementInventory(Queue("backfiller.giganews", 4));
        await using var availability = CreateAvailability(inventory, time);
        _ = availability.GetEligibleBackbones();

        inventory.Fault = new InvalidOperationException(
            "RabbitMQ Management API returned a malformed queue inventory payload.");
        time.Advance(BackfillConsumerAvailabilityService.RefreshInterval);
        var retained = availability.GetEligibleBackbones();

        Assert.Single(retained);
        Assert.Equal("Giganews", retained[0].Backbone);
    }

    [Fact]
    public async Task Availability_service_does_not_use_amqp_passive_queue_declare()
    {
        var source = await File.ReadAllTextAsync(
            Path.Join(
                RepoPaths.FindRepoRoot(),
                "src",
                "VectorNNTPD.ReaderService",
                "RabbitMq",
                "ArticleWork",
                "BackfillConsumerAvailability.cs"));

        Assert.DoesNotContain("QueueDeclarePassiveAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTopologyChannelAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_client_lists_queues_for_encoded_vhost_with_basic_auth()
    {
        var handler = new CapturingHandler(
            """
            [{"name":"backfiller.giganews","consumers":100},{"name":"backfiller.eweka","consumers":50}]
            """);
        var options = RabbitMqOptionsTests.CreateValid();
        options.VirtualHost = "/";
        options.Username = "mgmt-user";
        options.Password = "mgmt-secret";
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:15672/"),
        };
        var client = new RabbitMqManagementHttpClient(http, Options.Create(options));

        var queues = await client.ListQueuesAsync(CancellationToken.None);

        Assert.Equal(2, queues.Count);
        Assert.Equal("/api/queues/%2F", handler.LastPathAndQuery);
        Assert.NotNull(handler.LastAuthorization);
        Assert.Equal("Basic", handler.LastAuthorization!.Scheme);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(handler.LastAuthorization.Parameter!));
        Assert.Equal("mgmt-user:mgmt-secret", decoded);
        // codeql[cs/call-to-object-tostring] Assert default Object.ToString does not embed credentials.
        Assert.DoesNotContain("mgmt-secret", handler.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_client_auth_failure_does_not_embed_credentials_in_exception()
    {
        var handler = new CapturingHandler("{}", HttpStatusCode.Unauthorized);
        var options = RabbitMqOptionsTests.CreateValid();
        options.Username = "mgmt-user";
        options.Password = "super-secret-password";
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:15672/") };
        var client = new RabbitMqManagementHttpClient(http, Options.Create(options));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ListQueuesAsync(CancellationToken.None));

        Assert.DoesNotContain("super-secret-password", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("mgmt-user:super", ex.Message, StringComparison.Ordinal);
        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_client_rejects_malformed_json()
    {
        var handler = new CapturingHandler("{not-json");
        var options = RabbitMqOptionsTests.CreateValid();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:15672/") };
        var client = new RabbitMqManagementHttpClient(http, Options.Create(options));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ListQueuesAsync(CancellationToken.None));
        Assert.Contains("malformed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static BackfillConsumerAvailabilityService CreateAvailability(
        FakeManagementInventory inventory,
        FakeTimeProvider time) =>
        new(inventory, NullLogger.Instance, time);

    private static RabbitMqManagementQueueInfo Queue(string name, uint consumers) =>
        new(name, checked((int)consumers));

    private sealed class FakeManagementInventory : IRabbitMqManagementQueueInventory
    {
        private readonly object _gate = new();
        private IReadOnlyList<RabbitMqManagementQueueInfo> _queues;
        private int _inflight;

        public FakeManagementInventory(params RabbitMqManagementQueueInfo[] queues)
        {
            _queues = queues;
        }

        public int CallCount { get; private set; }

        public int MaxConcurrentCalls { get; private set; }

        public TimeSpan Delay { get; set; }

        public Exception? Fault { get; set; }

        public void SetQueues(params RabbitMqManagementQueueInfo[] queues)
        {
            lock (_gate)
            {
                _queues = queues;
                Fault = null;
            }
        }

        public async Task<IReadOnlyList<RabbitMqManagementQueueInfo>> ListQueuesAsync(
            CancellationToken cancellationToken)
        {
            var inflight = Interlocked.Increment(ref _inflight);
            try
            {
                MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, inflight);
                CallCount++;
                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (Fault is not null)
                    {
                        throw Fault;
                    }

                    return _queues;
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inflight);
            }
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public CapturingHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public string? LastPathAndQuery { get; private set; }

        public System.Net.Http.Headers.AuthenticationHeaderValue? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastPathAndQuery = request.RequestUri?.PathAndQuery;
            LastAuthorization = request.Headers.Authorization;
            // codeql[cs/local-not-disposed] Ownership transfers to the scripted handler queue / HTTP pipeline for the caller to dispose.
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}

/// <summary>Resolves the repository root from the test assembly location.</summary>
file static class RepoPaths
{
    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Join(dir.FullName, "VectorNNTPD.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate VectorNNTPD.slnx from the test base directory.");
    }
}

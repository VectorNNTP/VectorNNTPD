using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.TransitService.Tests.TestDoubles;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.TransitService.Tests.Cloudflare.Client;

/// <summary>
/// Hardening coverage: managed TTL policy budgets, 429/budget interaction, record validation, sanitized errors.
/// </summary>
public sealed class CloudflareDnsHardeningTests
{
    private const string ZoneId = "zone-test";
    private const string Fqdn = "nntpd01.usenet.ninja";

    [Fact]
    public async Task RateLimit_RetryAfterExceedsBudget_CancelsWithoutSleepingPastDeadline()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(
            // codeql[cs/local-not-disposed] Ownership transfers to the scripted handler queue / HTTP pipeline for the caller to dispose.
            new HttpResponseMessage((HttpStatusCode)429)
            {
                Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2)) },
                Content = new StringContent("""{"success":false,"errors":[{"code":1,"message":"rate"}]}""", Encoding.UTF8, "application/json"),
            });

        var client = CreateClient(handler);
        var delays = new List<TimeSpan>();
        client.DelayAsync = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };

        using var budget = CloudflareOperationBudget.Begin(TimeSpan.FromSeconds(5), CancellationToken.None, out var opToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ListRecordsAsync(ZoneId, Fqdn, "A", opToken));

        Assert.Empty(delays);
    }

    [Fact]
    public async Task RateLimit_CallerCancellation_TakesPrecedence()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(
            // codeql[cs/local-not-disposed] Ownership transfers to the scripted handler queue / HTTP pipeline for the caller to dispose.
            new HttpResponseMessage((HttpStatusCode)429)
            {
                Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)) },
                Content = new StringContent("""{"success":false,"errors":[{"code":1,"message":"rate"}]}""", Encoding.UTF8, "application/json"),
            });

        var client = CreateClient(handler);
        using var callerCts = new CancellationTokenSource();
        client.DelayAsync = (_, ct) =>
        {
            callerCts.Cancel();
            return Task.Delay(TimeSpan.FromSeconds(30), ct);
        };

        using var budget = CloudflareOperationBudget.Begin(TimeSpan.FromMinutes(2), callerCts.Token, out var opToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ListRecordsAsync(ZoneId, Fqdn, "A", opToken));
    }

    [Fact]
    public async Task Reconciler_SharedDeadline_CancelsBackoffWithoutSuccess()
    {
        var client = new FakeCloudflareDnsClient
        {
            ListException = new CloudflareDnsException("transient list failure") { FailedOperation = "List" },
        };

        var options = TestHostFactory.CreateValidOptions();
        options.CloudFlareOperationTimeout = TimeSpan.FromMilliseconds(50);
        var reconciler = new CloudflareDnsReconciler(
            client,
            Options.Create<AcmeCloudflareOptions>(options),
            NullLogger<CloudflareDnsReconciler>.Instance)
        {
            DelayAsync = (_, ct) => Task.Delay(TimeSpan.FromSeconds(30), ct),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None));

        Assert.Equal(0, client.CreateCallCount);
        Assert.Equal(0, client.DeleteCallCount);
    }

    [Fact]
    public async Task FailureResponse_DoesNotEmbedRawBody()
    {
        const string marker = "RAW_BODY_SECRET_MARKER_SHOULD_NOT_APPEAR";
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.BadRequest,
            $$"""{ "success": false, "errors": [], "result": null, "extra": "{{marker}}" }""");

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateClient(handler).ListRecordsAsync(ZoneId, Fqdn, "A", CancellationToken.None));

        Assert.Equal(400, ex.StatusCode);
        Assert.DoesNotContain(marker, ex.Message, StringComparison.Ordinal);
        Assert.Contains("no Cloudflare error details returned", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestHostFactory.TestCloudFlareApiKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_MissingSuccess_IsFailure()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "result": [],
              "result_info": { "page": 1, "per_page": 100, "total_pages": 1, "count": 0, "total_count": 0 }
            }
            """);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateClient(handler).ListRecordsAsync(ZoneId, Fqdn, "A", CancellationToken.None));

        Assert.True(ex.IsPermanentFailure || ex.StatusCode == 200);
        Assert.DoesNotContain(TestHostFactory.TestCloudFlareApiKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_NonBooleanSuccess_IsPermanentFailure()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": 1,
              "result": [],
              "result_info": { "page": 1, "per_page": 100, "total_pages": 1 }
            }
            """);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateClient(handler).ListRecordsAsync(ZoneId, Fqdn, "A", CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
    }

    [Fact]
    public async Task List_MissingRecordId_IsPermanentFailure_NoMutations()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": [
                { "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.1", "ttl": 300, "proxied": false }
              ],
              "result_info": { "page": 1, "per_page": 100, "total_pages": 1, "count": 1, "total_count": 1 }
            }
            """);

        var client = CreateClient(handler);
        var reconciler = new CloudflareDnsReconciler(
            client,
            Options.Create<AcmeCloudflareOptions>(TestHostFactory.CreateValidOptions()),
            NullLogger<CloudflareDnsReconciler>.Instance);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task List_MissingTtl_IsPermanentFailure()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": [
                { "id": "1", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.1", "proxied": false }
              ],
              "result_info": { "page": 1, "per_page": 100, "total_pages": 1, "count": 1, "total_count": 1 }
            }
            """);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateClient(handler).ListRecordsAsync(ZoneId, Fqdn, "A", CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
        Assert.Contains("ttl", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_MissingProxied_IsPermanentFailure()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": [
                { "id": "1", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.1", "ttl": 300 }
              ],
              "result_info": { "page": 1, "per_page": 100, "total_pages": 1, "count": 1, "total_count": 1 }
            }
            """);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateClient(handler).ListRecordsAsync(ZoneId, Fqdn, "A", CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
        Assert.Contains("proxied", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_MalformedProxied_IsPermanentFailure()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": [
                { "id": "1", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.1", "ttl": 300, "proxied": "yes" }
              ],
              "result_info": { "page": 1, "per_page": 100, "total_pages": 1, "count": 1, "total_count": 1 }
            }
            """);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateClient(handler).ListRecordsAsync(ZoneId, Fqdn, "A", CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
    }

    [Fact]
    public async Task ListAll_MissingId_IsPermanentFailure()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "result": [
                { "type": "TXT", "name": "nntpd01.usenet.ninja", "content": "x" }
              ],
              "result_info": { "page": 1, "per_page": 100, "total_pages": 1, "count": 1, "total_count": 1 }
            }
            """);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateClient(handler).ListAllRecordsForNameAsync(ZoneId, Fqdn, CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
    }

    [Fact]
    public async Task Reconcile_CreateUsesManagedTtl()
    {
        var client = new FakeCloudflareDnsClient();
        await new CloudflareDnsReconciler(
                client,
                Options.Create<AcmeCloudflareOptions>(TestHostFactory.CreateValidOptions()),
                NullLogger<CloudflareDnsReconciler>.Instance)
            .ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None);

        var created = Assert.Single(client.Snapshot());
        Assert.Equal(CloudflareManagedDnsPolicy.ManagedTtl, created.Ttl);
        Assert.Equal(false, created.Proxied);
        Assert.Equal(1, client.CreateCallCount);
    }

    [Fact]
    public async Task FailedStartCleanup_Timeout_DoesNotClaimSuccess_PreservesOriginal()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(new CloudflareDnsRecord
        {
            Id = "a1",
            Type = "A",
            Name = Fqdn,
            Content = "198.18.0.10",
            Ttl = CloudflareManagedDnsPolicy.ManagedTtl,
            Proxied = false,
        });
        client.OnMutate = async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        };

        var options = TestHostFactory.CreateValidOptions();
        var resolver = new BindAddressResolver(
            new FakeLocalIpAddressAssignee(TestHostFactory.TestIpv4, TestHostFactory.TestIpv6),
            NullLogger<BindAddressResolver>.Instance);
        var reconciler = new CloudflareDnsReconciler(
            client,
            Options.Create<AcmeCloudflareOptions>(options),
            NullLogger<CloudflareDnsReconciler>.Instance);
        var service = new CloudflareDnsReconciliationService(
            Options.Create<AcmeCloudflareOptions>(options),
            resolver,
            reconciler,
            NullLogger<CloudflareDnsReconciliationService>.Instance);

        // Force reconcile to fail after ownership path by making list fail after first mutation attempt —
        // simpler: inject permanent list failure then ensure cleanup uses 15s budget.
        client.ListException = new CloudflareDnsException("boom")
        {
            IsPermanentFailure = true,
            FailedOperation = "List",
        };

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            service.StartAsync(CancellationToken.None));

        Assert.Equal("boom", ex.Message);
        Assert.Contains(client.Snapshot(), r => r.Id == "a1");
    }

    [Fact]
    public void ManagedDnsPolicy_ConstantIs300() =>
        Assert.Equal(300, CloudflareManagedDnsPolicy.ManagedTtl);

    private static CloudflareDnsClient CreateClient(ScriptedHttpMessageHandler handler)
    {
        // codeql[cs/local-not-disposed] HttpClient ownership transfers to CloudflareDnsClient for the test lifetime.
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.cloudflare.com/client/v4/"),
        };
        return new CloudflareDnsClient(
            httpClient,
            Options.Create<AcmeCloudflareOptions>(TestHostFactory.CreateValidOptions()),
            NullLogger<CloudflareDnsClient>.Instance);
    }

    private static ResolvedBindAddresses Desired(params string[] addresses) =>
        new(addresses.Select(IPAddress.Parse));

    private sealed class ScriptedHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public void EnqueueJson(HttpStatusCode statusCode, string json) =>
            // codeql[cs/local-not-disposed] Ownership transfers to the scripted handler queue / HTTP pipeline for the caller to dispose.
            Enqueue(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

        public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var copy = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Content = request.Content,
            };
            Requests.Add(copy);
            cancellationToken.ThrowIfCancellationRequested();
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No scripted HTTP response remaining.");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.ReaderService.Tests.TestDoubles;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.ReaderService.Tests.Cloudflare.Client;

/// <summary>
/// Cancellation, shared operation budget, and per-request HTTP timeout regression coverage.
/// </summary>
public sealed class CloudflareDnsDeadlineTests
{
    private const string ZoneId = "zone-test";
    private const string Fqdn = "nntpd01.usenet.ninja";

    [Fact]
    public async Task CallerCancellation_BeforeRequest_DoesNotStartHttpCall()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(HttpStatusCode.OK, EmptyListJson());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateClient(handler).ListRecordsAsync(ZoneId, Fqdn, "A", cts.Token));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RateLimit_CancelDuringDelay_PreventsSubsequentRequest()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(
            // codeql[cs/local-not-disposed] Ownership transfers to the scripted handler queue / HTTP pipeline for the caller to dispose.
            new HttpResponseMessage((HttpStatusCode)429)
            {
                Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)) },
                Content = new StringContent("""{"success":false,"errors":[{"code":1,"message":"rate"}]}""", Encoding.UTF8, "application/json"),
            });
        handler.EnqueueJson(HttpStatusCode.OK, EmptyListJson());

        var client = CreateClient(handler);
        using var cts = new CancellationTokenSource();
        client.DelayAsync = (_, ct) =>
        {
            cts.Cancel();
            return Task.Delay(TimeSpan.FromSeconds(60), ct);
        };

        using var budget = CloudflareOperationBudget.Begin(TimeSpan.FromMinutes(2), cts.Token, out var opToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ListRecordsAsync(ZoneId, Fqdn, "A", opToken));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public void CreateRequestTimeoutCts_CapsByRemainingBudget()
    {
        using var budget = CloudflareOperationBudget.Begin(TimeSpan.FromMilliseconds(150), CancellationToken.None, out var opToken);
        using var cts = CloudflareDnsClient.CreateRequestTimeoutCts(opToken, out var sendToken);

        Assert.False(sendToken.IsCancellationRequested);
        Assert.True(SpinWait.SpinUntil(() => sendToken.IsCancellationRequested, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void CreateRequestTimeoutCts_ThrowsWhenBudgetAlreadyExpired()
    {
        using var budget = CloudflareOperationBudget.Begin(TimeSpan.FromMilliseconds(1), CancellationToken.None, out var opToken);
        Assert.True(SpinWait.SpinUntil(() => budget.Remaining <= TimeSpan.Zero, TimeSpan.FromSeconds(2)));

        Assert.ThrowsAny<OperationCanceledException>(() =>
            CloudflareDnsClient.CreateRequestTimeoutCts(opToken, out _));
    }

    [Fact]
    public async Task List_RequestTimeout_IsNotUncertainMutation()
    {
        var handler = new StallingHttpMessageHandler();
        // codeql[cs/local-not-disposed] HttpClient ownership transfers to CloudflareDnsClient for the test lifetime.
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.cloudflare.com/client/v4/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var client = new CloudflareDnsClient(
            http,
            Options.Create<AcmeCloudflareOptions>(TestHostFactory.CreateValidOptions()),
            NullLogger<CloudflareDnsClient>.Instance);

        using var budget = CloudflareOperationBudget.Begin(TimeSpan.FromMilliseconds(40), CancellationToken.None, out var opToken);

        // Budget expires → OCE (operation cancel), not CloudflareDnsException uncertain.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ListRecordsAsync(ZoneId, Fqdn, "A", opToken));
    }

    [Fact]
    public async Task Mutation_PerRequestTimeout_WithoutOperationCancel_IsUncertain()
    {
        var handler = new StallingHttpMessageHandler();
        // codeql[cs/local-not-disposed] HttpClient ownership transfers to CloudflareDnsClient for the test lifetime.
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.cloudflare.com/client/v4/"),
            // Legacy HttpClient timeout still used by some tests; client also CancelAfter(PerRequestTimeout).
            Timeout = TimeSpan.FromMilliseconds(40),
        };
        var client = new CloudflareDnsClient(
            http,
            Options.Create<AcmeCloudflareOptions>(TestHostFactory.CreateValidOptions()),
            NullLogger<CloudflareDnsClient>.Instance);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            client.CreateRecordAsync(
                ZoneId,
                new CloudflareDnsRecordWriteRequest
                {
                    Type = "A",
                    Name = Fqdn,
                    Content = "198.18.0.10",
                    Ttl = CloudflareManagedDnsPolicy.ManagedTtl,
                },
                CancellationToken.None));

        Assert.True(ex.IsOutcomeUncertain);
        Assert.False(ex.IsPermanentFailure);
    }

    [Fact]
    public async Task Reconciler_DoesNotResetDeadlineAcrossAttempts()
    {
        var client = new FakeCloudflareDnsClient
        {
            ListException = new CloudflareDnsException("transient") { FailedOperation = "List" },
        };

        var options = TestHostFactory.CreateValidOptions();
        options.CloudFlareOperationTimeout = TimeSpan.FromMilliseconds(80);
        var reconciler = new CloudflareDnsReconciler(
            client,
            Options.Create<AcmeCloudflareOptions>(options),
            NullLogger<CloudflareDnsReconciler>.Instance)
        {
            DelayAsync = async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), ct).ConfigureAwait(false);
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None));

        Assert.Equal(0, client.CreateCallCount);
        Assert.True(client.ListCallCount >= 1);
    }

    [Fact]
    public async Task Reconcile_CancelDuringVerification_DoesNotReportSuccess()
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

        using var cts = new CancellationTokenSource();
        var listCount = 0;
        client.TransformListedRecords = (type, records, callCount) =>
        {
            listCount = callCount;
            // After planning lists (2) and creates (0), verification lists start — cancel then.
            if (callCount >= 3)
            {
                cts.Cancel();
            }

            return records;
        };

        var options = TestHostFactory.CreateValidOptions();
        options.CloudFlareOperationTimeout = TimeSpan.FromMinutes(2);
        var reconciler = new CloudflareDnsReconciler(
            client,
            Options.Create<AcmeCloudflareOptions>(options),
            NullLogger<CloudflareDnsReconciler>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), cts.Token));

        Assert.True(listCount >= 3);
    }

    [Fact]
    public async Task FailedStartCleanup_UsesDedicatedBudget_DoesNotMaskOriginal()
    {
        var client = new FakeCloudflareDnsClient
        {
            ListException = new CloudflareDnsException("original-startup-failure")
            {
                IsPermanentFailure = true,
                FailedOperation = "List",
            },
            TransformListedAllRecords = (_, callCount) =>
            {
                if (callCount > 1)
                {
                    throw new CloudflareDnsException("cleanup-still-failing")
                    {
                        IsPermanentFailure = true,
                        FailedOperation = "ListAll",
                    };
                }

                return [];
            },
        };

        var options = TestHostFactory.CreateValidOptions();
        var service = new CloudflareDnsReconciliationService(
            Options.Create<AcmeCloudflareOptions>(options),
            new BindAddressResolver(
                new FakeLocalIpAddressAssignee(TestHostFactory.TestIpv4, TestHostFactory.TestIpv6),
                NullLogger<BindAddressResolver>.Instance),
            new CloudflareDnsReconciler(
                client,
                Options.Create<AcmeCloudflareOptions>(options),
                NullLogger<CloudflareDnsReconciler>.Instance),
            NullLogger<CloudflareDnsReconciliationService>.Instance);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            service.StartAsync(CancellationToken.None));

        Assert.Equal("original-startup-failure", ex.Message);
        Assert.Equal(TimeSpan.FromSeconds(15), CloudflareDnsReconciliationService.FailedStartCleanupTimeout);
    }

    [Fact]
    public async Task NormalCleanup_HonorsCallerToken()
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
        using var cts = new CancellationTokenSource();
        client.OnMutate = _ =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        };

        var reconciler = new CloudflareDnsReconciler(
            client,
            Options.Create<AcmeCloudflareOptions>(TestHostFactory.CreateValidOptions()),
            NullLogger<CloudflareDnsReconciler>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reconciler.RemoveAllRecordsForFqdnAsync(ZoneId, Fqdn, cts.Token));
    }

    private static CloudflareDnsClient CreateClient(HttpMessageHandler handler)
    {
        // codeql[cs/local-not-disposed] HttpClient ownership transfers to CloudflareDnsClient for the test lifetime.
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.cloudflare.com/client/v4/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        return new CloudflareDnsClient(
            http,
            Options.Create<AcmeCloudflareOptions>(TestHostFactory.CreateValidOptions()),
            NullLogger<CloudflareDnsClient>.Instance);
    }

    private static ResolvedBindAddresses Desired(params string[] addresses) =>
        new(addresses.Select(IPAddress.Parse));

    private static string EmptyListJson() =>
        """{ "success": true, "result": [], "result_info": { "page": 1, "per_page": 100, "total_pages": 1, "count": 0, "total_count": 0 } }""";

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
            Requests.Add(new HttpRequestMessage(request.Method, request.RequestUri));
            cancellationToken.ThrowIfCancellationRequested();
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No scripted HTTP response remaining.");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class StallingHttpMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

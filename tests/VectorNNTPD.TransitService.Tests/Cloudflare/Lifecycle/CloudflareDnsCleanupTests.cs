using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.TransitService.Tests.Cloudflare.Lifecycle;

/// <summary>
/// Shutdown cleanup: remove every DNS record for the exact FQDN (all types), with lifecycle coverage.
/// </summary>
public sealed class CloudflareDnsCleanupTests
{
    private const string ZoneId = "zone-test";
    private const string Fqdn = "nntpd01.usenet.ninja";

    [Fact]
    public async Task RemoveAll_DeletesAAndAaaa()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(
            Record("a", "A", "198.18.0.10"),
            Record("aaaa", "AAAA", "2001:db8::10"));

        await CreateReconciler(client).RemoveAllRecordsForFqdnAsync(ZoneId, Fqdn, CancellationToken.None);

        Assert.DoesNotContain(client.Snapshot(), r => NamesEqual(r.Name, Fqdn));
    }

    [Fact]
    public async Task RemoveAll_DeletesAllRecordTypesForExactFqdn()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(
            Record("a", "A", "198.18.0.10"),
            Record("aaaa", "AAAA", "2001:db8::10"),
            Record("cname", "CNAME", "target.example"),
            Record("txt", "TXT", "v=spf1"),
            Record("mx", "MX", "10 mail.example"),
            Record("srv", "SRV", "1 1 443 target"),
            Record("caa", "CAA", "0 issue \"letsencrypt.org\""),
            Record("https", "HTTPS", "1 . alpn=h2"),
            Record("svcb", "SVCB", "1 . alpn=h2"));

        await CreateReconciler(client).RemoveAllRecordsForFqdnAsync(ZoneId, Fqdn, CancellationToken.None);

        Assert.DoesNotContain(client.Snapshot(), r => NamesEqual(r.Name, Fqdn));
        Assert.True(client.SuccessfulDeleteCount >= 9);
    }

    [Fact]
    public async Task RemoveAll_DeletesDuplicates()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(
            Record("a1", "A", "198.18.0.10"),
            Record("a2", "A", "198.18.0.10"),
            Record("txt1", "TXT", "one"),
            Record("txt2", "TXT", "two"));

        await CreateReconciler(client).RemoveAllRecordsForFqdnAsync(ZoneId, Fqdn, CancellationToken.None);

        Assert.DoesNotContain(client.Snapshot(), r => NamesEqual(r.Name, Fqdn));
        Assert.Equal(4, client.SuccessfulDeleteCount);
    }

    [Fact]
    public async Task ListAll_FollowsPagination_InHttpClient()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "errors": [],
              "result": [ { "id": "1", "type": "A", "name": "nntpd01.usenet.ninja", "content": "198.18.0.1", "proxied": false, "ttl": 1 } ],
              "result_info": { "page": 1, "per_page": 100, "total_pages": 2, "count": 1, "total_count": 2 }
            }
            """);
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "success": true,
              "errors": [],
              "result": [ { "id": "2", "type": "TXT", "name": "nntpd01.usenet.ninja", "content": "x", "proxied": false, "ttl": 1 } ],
              "result_info": { "page": 2, "per_page": 100, "total_pages": 2, "count": 1, "total_count": 2 }
            }
            """);

        var client = CreateHttpClient(handler);
        var records = await client.ListAllRecordsForNameAsync(ZoneId, Fqdn, CancellationToken.None);

        Assert.Equal(2, records.Count);
        Assert.Contains(records, r => r.Type == "TXT");
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain("type=", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveAll_PreservesParentChildAndSimilarSuffixHostnames()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(
            Record("ours", "A", "198.18.0.10"),
            new CloudflareDnsRecord { Id = "parent", Type = "A", Name = "usenet.ninja", Content = "198.18.0.1" },
            new CloudflareDnsRecord { Id = "child", Type = "A", Name = "other.nntpd01.usenet.ninja", Content = "198.18.0.2" },
            new CloudflareDnsRecord { Id = "suffix", Type = "A", Name = "xnntpd01.usenet.ninja", Content = "198.18.0.3" },
            new CloudflareDnsRecord { Id = "peer", Type = "TXT", Name = "nntpd02.usenet.ninja", Content = "keep" });

        await CreateReconciler(client).RemoveAllRecordsForFqdnAsync(ZoneId, Fqdn, CancellationToken.None);

        var snap = client.Snapshot();
        Assert.DoesNotContain(snap, r => r.Id == "ours");
        Assert.Contains(snap, r => r.Id == "parent");
        Assert.Contains(snap, r => r.Id == "child");
        Assert.Contains(snap, r => r.Id == "suffix");
        Assert.Contains(snap, r => r.Id == "peer");
    }

    [Fact]
    public async Task RemoveAll_PartialDelete_ThenRetry_Converges()
    {
        var client = new FakeCloudflareDnsClient
        {
            DeleteException = new CloudflareDnsException("transient delete")
            {
                StatusCode = 502,
                IsOutcomeUncertain = true,
            },
            DeleteFailRemaining = 1,
        };
        client.Seed(
            Record("a", "A", "198.18.0.10"),
            Record("txt", "TXT", "hello"));

        await CreateReconciler(client).RemoveAllRecordsForFqdnAsync(ZoneId, Fqdn, CancellationToken.None);

        Assert.DoesNotContain(client.Snapshot(), r => NamesEqual(r.Name, Fqdn));
    }

    [Fact]
    public async Task RemoveAll_TimeoutAfterDeleteApplied_RetryVerifiesGone()
    {
        var client = new FakeCloudflareDnsClient
        {
            DeleteException = new CloudflareDnsException("timeout after delete")
            {
                IsOutcomeUncertain = true,
            },
            DeleteFailRemaining = 1,
            ApplyDeleteThenFailUncertain = true,
        };
        client.Seed(Record("a", "A", "198.18.0.10"));

        await CreateReconciler(client).RemoveAllRecordsForFqdnAsync(ZoneId, Fqdn, CancellationToken.None);

        Assert.Empty(client.Snapshot());
        Assert.True(client.ListAllCallCount >= 2);
    }

    [Fact]
    public async Task RemoveAll_TimeoutWhenDeleteNotApplied_RetryDeletes()
    {
        var client = new FakeCloudflareDnsClient
        {
            DeleteException = new CloudflareDnsException("timeout no apply")
            {
                IsOutcomeUncertain = true,
            },
            DeleteFailRemaining = 1,
            ApplyDeleteThenFailUncertain = false,
        };
        client.Seed(Record("a", "A", "198.18.0.10"));

        await CreateReconciler(client).RemoveAllRecordsForFqdnAsync(ZoneId, Fqdn, CancellationToken.None);

        Assert.Empty(client.Snapshot());
        Assert.True(client.SuccessfulDeleteCount >= 1);
    }

    [Fact]
    public async Task RemoveAll_Retry_ReReadsActualState()
    {
        var client = new FakeCloudflareDnsClient
        {
            DeleteException = new CloudflareDnsException("fail once")
            {
                StatusCode = 502,
                IsOutcomeUncertain = true,
            },
            DeleteFailRemaining = 1,
        };
        client.Seed(Record("txt", "TXT", "x"));

        var listsBefore = 0;
        await CreateReconciler(client).RemoveAllRecordsForFqdnAsync(ZoneId, Fqdn, CancellationToken.None);
        Assert.True(client.ListAllCallCount > 2);
        _ = listsBefore;
    }

    [Fact]
    public async Task RemoveAll_PermanentFailure_DoesNotRetryPointlessly()
    {
        var client = new FakeCloudflareDnsClient
        {
            ListAllException = new CloudflareDnsException("auth")
            {
                StatusCode = 401,
                IsPermanentFailure = true,
            },
        };
        client.Seed(Record("a", "A", "198.18.0.10"));

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateReconciler(client).RemoveAllRecordsForFqdnAsync(ZoneId, Fqdn, CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
        Assert.Equal(1, client.ListAllCallCount);
        Assert.Equal(0, client.DeleteCallCount);
    }

    [Fact]
    public async Task RemoveAll_VerificationMismatch_FailsClosed()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(Record("a", "A", "198.18.0.10"));
        client.TransformListedAllRecords = (records, callCount) =>
        {
            // After deletes, verification lists still show a record.
            if (client.SuccessfulDeleteCount > 0 && callCount > 1)
            {
                return
                [
                    Record("ghost", "TXT", "still-here"),
                ];
            }

            return records;
        };

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateReconciler(client).RemoveAllRecordsForFqdnAsync(ZoneId, Fqdn, CancellationToken.None));

        Assert.Contains("verification failed", ex.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cleanup completed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoveAll_RetryExhaustion_WithRemainingRecords_FailsClosed()
    {
        var client = new FakeCloudflareDnsClient
        {
            DeleteException = new CloudflareDnsException("always fail")
            {
                StatusCode = 502,
                IsOutcomeUncertain = true,
            },
        };
        client.Seed(
            Record("a", "A", "198.18.0.10"),
            Record("txt", "TXT", "remain"));

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateReconciler(client).RemoveAllRecordsForFqdnAsync(ZoneId, Fqdn, CancellationToken.None));

        Assert.Contains("failed after", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(client.Snapshot());
    }

    [Fact]
    public async Task RemoveAll_CancellationDuringDelete_DoesNotClaimSuccess()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(Record("a", "A", "198.18.0.10"), Record("txt", "TXT", "x"));
        using var cts = new CancellationTokenSource();
        client.OnMutate = _ =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateReconciler(client).RemoveAllRecordsForFqdnAsync(ZoneId, Fqdn, cts.Token));
    }

    [Fact]
    public async Task RemoveAll_CancellationDuringBackoff_DoesNotClaimSuccess()
    {
        var client = new FakeCloudflareDnsClient
        {
            DeleteException = new CloudflareDnsException("fail")
            {
                StatusCode = 502,
                IsOutcomeUncertain = true,
            },
        };
        client.Seed(Record("a", "A", "198.18.0.10"));
        using var cts = new CancellationTokenSource();
        var failedOnce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnMutate = _ =>
        {
            failedOnce.TrySetResult();
            return Task.CompletedTask;
        };

        var task = CreateReconciler(client).RemoveAllRecordsForFqdnAsync(ZoneId, Fqdn, cts.Token);
        await failedOnce.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task RemoveAll_AlreadyAbsent_IsIdempotentSuccess()
    {
        var client = new FakeCloudflareDnsClient();
        await CreateReconciler(client).RemoveAllRecordsForFqdnAsync(ZoneId, Fqdn, CancellationToken.None);
        Assert.Equal(0, client.DeleteCallCount);
        Assert.True(client.ListAllCallCount >= 1);
    }

    [Fact]
    public async Task AmbiguousRecordName_FailsSafelyWithoutDeletingOthers()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(Record("good", "A", "198.18.0.10"));
        client.TransformListedAllRecords = (records, _) =>
            records.Append(new CloudflareDnsRecord
            {
                Id = "bad",
                Type = "TXT",
                Name = "nntpd01..usenet.ninja",
                Content = "ambiguous",
            }).ToArray();

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateReconciler(client).RemoveAllRecordsForFqdnAsync(ZoneId, Fqdn, CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
        Assert.Contains("ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.SuccessfulDeleteCount);
    }

    [Fact]
    public async Task Lifecycle_Stop_RemovesAllRecordsForExactFqdn()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["*"];
        options.GracefulShutdownTimeout = TimeSpan.FromSeconds(30);

        var client = new FakeCloudflareDnsClient();
        client.Seed(
            Record("txt", "TXT", "preexisting"),
            new CloudflareDnsRecord { Id = "child", Type = "A", Name = "other." + Fqdn, Content = "198.18.0.9" });

        var dns = CreateService(options, client);
        var other = new FakeApplicationService("listener");
        await using var lifecycle = TestHostFactory.CreateLifecycle([TestHostFactory.WrapDns(dns), other], options);

        await lifecycle.StartAsync(CancellationToken.None);
        Assert.Equal(ApplicationState.Running, lifecycle.State);
        Assert.Contains(client.Snapshot(), r => r.Type == "A");

        await lifecycle.StopAsync(CancellationToken.None);
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);

        Assert.DoesNotContain(client.Snapshot(), r => NamesEqual(r.Name, options.Fqdn));
        Assert.Contains(client.Snapshot(), r => r.Id == "child");
        Assert.Equal(1, other.StopCount);
    }

    [Fact]
    public async Task Lifecycle_StopOrder_DnsCleanupRunsAfterOtherServices()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["*"];
        var client = new FakeCloudflareDnsClient();
        var order = new List<string>();

        var dns = CreateService(options, client);
        var trackingDns = new OrderedStopService(TestHostFactory.WrapDns(dns), order, "dns");
        var other = new FakeApplicationService(
            "listener",
            onStop: _ =>
            {
                order.Add("listener");
                return Task.CompletedTask;
            });

        await using var lifecycle = TestHostFactory.CreateLifecycle([trackingDns, other], options);
        await lifecycle.StartAsync(CancellationToken.None);
        await lifecycle.StopAsync(CancellationToken.None);

        Assert.Equal(["listener", "dns"], order);
    }

    [Fact]
    public async Task Lifecycle_PartialStartup_DnsRollbackCleansFqdn()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["*"];
        var client = new FakeCloudflareDnsClient();

        var dns = CreateService(options, client);
        var failing = new FakeApplicationService(
            "failing",
            onStart: _ => throw new InvalidOperationException("boom"));

        await using var lifecycle = TestHostFactory.CreateLifecycle([TestHostFactory.WrapDns(dns), failing], options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.StartAsync(CancellationToken.None));

        Assert.DoesNotContain(client.Snapshot(), r => NamesEqual(r.Name, options.Fqdn));
    }

    [Fact]
    public async Task StartFailure_DuringReconcile_AttemptsCleanup_DoesNotClaimSuccessOnCleanupFail()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["*"];
        var client = new FakeCloudflareDnsClient
        {
            CreateFailType = "AAAA",
            CreateFailTypeException = new CloudflareDnsException("aaaa fail")
            {
                StatusCode = 502,
                IsOutcomeUncertain = true,
            },
            // Cleanup delete also fails — must not report cleanup success; original start failure surfaces.
            DeleteException = new CloudflareDnsException("cleanup fail")
            {
                StatusCode = 502,
                IsOutcomeUncertain = true,
            },
        };

        var service = CreateService(options, client);
        await Assert.ThrowsAsync<CloudflareDnsException>(() => service.StartAsync(CancellationToken.None));
        // Ownership was never marked active; Stop should no-op.
        await service.StopAsync(CancellationToken.None);
        Assert.True(client.ListAllCallCount >= 1);
    }

    [Fact]
    public async Task Stop_WhenOwnershipInactive_DoesNotClaimCleanup()
    {
        var options = TestHostFactory.CreateValidOptions();
        var client = new FakeCloudflareDnsClient();
        client.Seed(Record("a", "A", "198.18.0.10"));
        var service = CreateService(options, client);

        await service.StopAsync(CancellationToken.None);
        Assert.Equal(0, client.DeleteCallCount);
        Assert.Single(client.Snapshot());
    }

    [Fact]
    public async Task ReconcileAndCleanup_SameInstance_AreSerialized()
    {
        var client = new FakeCloudflareDnsClient();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var max = 0;
        var concurrent = 0;

        client.OnMutate = async ct =>
        {
            Interlocked.Increment(ref entered);
            var now = Interlocked.Increment(ref concurrent);
            Interlocked.Exchange(ref max, Math.Max(Volatile.Read(ref max), now));
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            Interlocked.Decrement(ref concurrent);
        };

        var reconciler = CreateReconciler(client);
        var desired = new ResolvedBindAddresses([IPAddress.Parse("198.18.0.10")]);
        var reconcile = reconciler.ReconcileAsync(ZoneId, Fqdn, desired, CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref entered) >= 1);

        var cleanup = reconciler.RemoveAllRecordsForFqdnAsync(ZoneId, Fqdn, CancellationToken.None);
        await Task.Delay(30);
        Assert.Equal(1, Volatile.Read(ref entered));

        gate.TrySetResult();
        await Task.WhenAll(reconcile, cleanup);
        Assert.Equal(1, max);
        Assert.DoesNotContain(client.Snapshot(), r => NamesEqual(r.Name, Fqdn));
    }

    [Fact]
    public async Task RepeatedStop_IsSafe_AfterCleanup()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["*"];
        var client = new FakeCloudflareDnsClient();
        var service = CreateService(options, client);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.DoesNotContain(client.Snapshot(), r => NamesEqual(r.Name, options.Fqdn));
    }

    [Fact]
    public async Task ShutdownDeadline_PropagatesCancellation_WithoutClaimingRemoval()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["*"];
        options.GracefulShutdownTimeout = TimeSpan.FromMilliseconds(50);

        var client = new FakeCloudflareDnsClient();
        var dns = CreateService(options, client);
        await using var lifecycle = TestHostFactory.CreateLifecycle([TestHostFactory.WrapDns(dns)], options);
        await lifecycle.StartAsync(CancellationToken.None);

        client.OnMutate = async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        };

        // Stop should surface timeout/cancel from manager budget while cleanup is in progress.
        await Assert.ThrowsAnyAsync<Exception>(() => lifecycle.StopAsync(CancellationToken.None));
    }

    [Fact]
    public void TryClassifyExactFqdn_RejectsAmbiguousAndNonExact()
    {
        Assert.True(CloudflareDnsReconciler.TryClassifyExactFqdn("NNTPD01.Usenet.Ninja.", "nntpd01.usenet.ninja", out var exact));
        Assert.True(exact);

        Assert.True(CloudflareDnsReconciler.TryClassifyExactFqdn("other.nntpd01.usenet.ninja", "nntpd01.usenet.ninja", out exact));
        Assert.False(exact);

        Assert.False(CloudflareDnsReconciler.TryClassifyExactFqdn("nntpd01..usenet.ninja", "nntpd01.usenet.ninja", out _));
        Assert.False(CloudflareDnsReconciler.TryClassifyExactFqdn("  ", "nntpd01.usenet.ninja", out _));
    }

    private static CloudflareDnsReconciler CreateReconciler(ICloudflareDnsClient client) =>
        new(client, Options.Create<AcmeCloudflareOptions>(TestHostFactory.CreateValidOptions()), NullLogger<CloudflareDnsReconciler>.Instance);

    private static CloudflareDnsReconciliationService CreateService(NntpdOptions options, ICloudflareDnsClient client)
    {
        var resolver = new BindAddressResolver(
            new FakeLocalIpAddressAssignee(TestHostFactory.TestIpv4, TestHostFactory.TestIpv6),
            NullLogger<BindAddressResolver>.Instance);
        return new CloudflareDnsReconciliationService(
            Options.Create<AcmeCloudflareOptions>(options),
            resolver,
            CreateReconciler(client),
            NullLogger<CloudflareDnsReconciliationService>.Instance);
    }

    private static CloudflareDnsClient CreateHttpClient(HttpMessageHandler handler)
    {
        var options = TestHostFactory.CreateValidOptions();
        // codeql[cs/local-not-disposed] HttpClient ownership transfers to CloudflareDnsClient for the test lifetime.
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.cloudflare.com/client/v4/") };
        return new CloudflareDnsClient(
            http,
            Options.Create<AcmeCloudflareOptions>(options),
            NullLogger<CloudflareDnsClient>.Instance);
    }

    private static CloudflareDnsRecord Record(string id, string type, string content) =>
        new()
        {
            Id = id,
            Type = type,
            Name = Fqdn,
            Content = content,
            Proxied = false,
            Ttl = CloudflareManagedDnsPolicy.ManagedTtl,
        };

    private static bool NamesEqual(string left, string right) =>
        string.Equals(
            left.Trim().TrimEnd('.'),
            right.Trim().TrimEnd('.'),
            StringComparison.OrdinalIgnoreCase);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Condition was not met within the safety timeout.");
    }

    private sealed class OrderedStopService : IApplicationService
    {
        private readonly IApplicationService _inner;
        private readonly List<string> _order;
        private readonly string _label;

        public OrderedStopService(IApplicationService inner, List<string> order, string label)
        {
            _inner = inner;
            _order = order;
            _label = label;
        }

        public string Name => _inner.Name;
        public Task? Execution => _inner.Execution;
        public Task StartAsync(CancellationToken cancellationToken) => _inner.StartAsync(cancellationToken);

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _inner.StopAsync(cancellationToken).ConfigureAwait(false);
            _order.Add(_label);
        }
    }

    private sealed class ScriptedHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public void EnqueueJson(HttpStatusCode statusCode, string json) =>
            // codeql[cs/local-not-disposed] Ownership transfers to the scripted handler queue / HTTP pipeline for the caller to dispose.
            _responses.Enqueue(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var copy = new HttpRequestMessage(request.Method, request.RequestUri);
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

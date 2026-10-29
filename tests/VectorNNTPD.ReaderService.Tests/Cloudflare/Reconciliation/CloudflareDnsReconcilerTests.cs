using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.ReaderService.Tests.Cloudflare.Reconciliation;

public sealed class CloudflareDnsReconcilerTests
{
    private const string ZoneId = "zone-test";
    private const string Fqdn = "nntpd01.usenet.ninja";

    [Fact]
    public async Task Reconcile_WhenRecordsAlreadyMatch_MakesNoMutations()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(
            Record("a1", "A", "198.18.0.10"),
            Record("aaaa1", "AAAA", "2001:db8::10"));

        var reconciler = CreateReconciler(client);
        var desired = Desired("198.18.0.10", "2001:db8::10");

        await reconciler.ReconcileAsync(ZoneId, Fqdn, desired, CancellationToken.None);

        Assert.Equal(0, client.CreateCallCount);
        Assert.Equal(0, client.UpdateCallCount);
        Assert.Equal(0, client.DeleteCallCount);
        Assert.Equal(2, client.Snapshot().Count);
    }

    [Fact]
    public async Task Reconcile_MissingRecords_CreatesThem()
    {
        var client = new FakeCloudflareDnsClient();
        var reconciler = CreateReconciler(client);
        var desired = Desired("198.18.0.10", "2001:db8::10");

        await reconciler.ReconcileAsync(ZoneId, Fqdn, desired, CancellationToken.None);

        Assert.Equal(2, client.CreateCallCount);
        var snapshot = client.Snapshot();
        Assert.Contains(snapshot, r => r.Type == "A" && r.Content == "198.18.0.10" && r.Proxied == false);
        Assert.Contains(snapshot, r => r.Type == "AAAA" && r.Content == "2001:db8::10" && r.Proxied == false);
    }

    [Fact]
    public async Task Reconcile_StaleRecords_AreRemoved()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(
            Record("stale-a", "A", "198.18.0.99"),
            Record("stale-aaaa", "AAAA", "2001:db8::99"),
            Record("other", "A", "198.18.0.50", name: "other.usenet.ninja"));

        var reconciler = CreateReconciler(client);
        var desired = Desired("198.18.0.10", "2001:db8::10");

        await reconciler.ReconcileAsync(ZoneId, Fqdn, desired, CancellationToken.None);

        var snapshot = client.Snapshot();
        Assert.DoesNotContain(snapshot, r => r.Content is "198.18.0.99" or "2001:db8::99");
        Assert.Contains(snapshot, r => r.Name == "other.usenet.ninja" && r.Content == "198.18.0.50");
        Assert.Contains(snapshot, r => r.Type == "A" && r.Content == "198.18.0.10");
        Assert.Contains(snapshot, r => r.Type == "AAAA" && r.Content == "2001:db8::10");
    }

    [Fact]
    public async Task Reconcile_MixedDesiredAndStale_RetainsDesiredRemovesStale()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(
            Record("keep", "A", "198.18.0.10"),
            Record("stale", "A", "198.18.0.99"));

        var reconciler = CreateReconciler(client);
        var desired = Desired("198.18.0.10");

        await reconciler.ReconcileAsync(ZoneId, Fqdn, desired, CancellationToken.None);

        var snapshot = client.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal("198.18.0.10", snapshot[0].Content);
        Assert.Equal(0, client.CreateCallCount);
        Assert.Equal(1, client.DeleteCallCount);
    }

    [Fact]
    public async Task Reconcile_NoIpv4_RemovesAllARecords()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(
            Record("a1", "A", "198.18.0.10"),
            Record("aaaa1", "AAAA", "2001:db8::10"));

        var reconciler = CreateReconciler(client);
        var desired = new ResolvedBindAddresses([IPAddress.Parse("2001:db8::10")]);

        await reconciler.ReconcileAsync(ZoneId, Fqdn, desired, CancellationToken.None);

        var snapshot = client.Snapshot();
        Assert.DoesNotContain(snapshot, r => r.Type == "A");
        Assert.Contains(snapshot, r => r.Type == "AAAA" && r.Content == "2001:db8::10");
    }

    [Fact]
    public async Task Reconcile_NoIpv6_RemovesAllAaaaRecords()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(
            Record("a1", "A", "198.18.0.10"),
            Record("aaaa1", "AAAA", "2001:db8::10"));

        var reconciler = CreateReconciler(client);
        var desired = new ResolvedBindAddresses([IPAddress.Parse("198.18.0.10")]);

        await reconciler.ReconcileAsync(ZoneId, Fqdn, desired, CancellationToken.None);

        var snapshot = client.Snapshot();
        Assert.DoesNotContain(snapshot, r => r.Type == "AAAA");
        Assert.Contains(snapshot, r => r.Type == "A" && r.Content == "198.18.0.10");
    }

    [Fact]
    public async Task Reconcile_EmptyDesired_ThrowsWithoutMutating()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(Record("a1", "A", "198.18.0.10"));
        var reconciler = CreateReconciler(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reconciler.ReconcileAsync(ZoneId, Fqdn, new ResolvedBindAddresses([]), CancellationToken.None));

        Assert.Equal(0, client.CreateCallCount);
        Assert.Equal(0, client.DeleteCallCount);
        Assert.Single(client.Snapshot());
    }

    [Fact]
    public async Task Reconcile_DuplicateRecords_AreCollapsed()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(
            Record("a1", "A", "198.18.0.10"),
            Record("a2", "A", "198.18.0.10"),
            Record("aaaa1", "AAAA", "2001:db8::10"),
            Record("aaaa2", "AAAA", "2001:db8::10"));

        var reconciler = CreateReconciler(client);
        var desired = Desired("198.18.0.10", "2001:db8::10");

        await reconciler.ReconcileAsync(ZoneId, Fqdn, desired, CancellationToken.None);

        var snapshot = client.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(2, client.DeleteCallCount);
    }

    [Fact]
    public async Task Reconcile_UnrelatedTxtRecord_RemainsUntouched()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(
            new CloudflareDnsRecord
            {
                Id = "txt1",
                Type = "TXT",
                Name = Fqdn,
                Content = "v=spf1 -all",
            });

        var reconciler = CreateReconciler(client);
        var desired = Desired("198.18.0.10");

        await reconciler.ReconcileAsync(ZoneId, Fqdn, desired, CancellationToken.None);

        Assert.Contains(client.Snapshot(), r => r.Type == "TXT" && r.Id == "txt1");
    }

    [Fact]
    public async Task Reconcile_ApiFailure_SurfacesAndIsNotTreatedAsSuccess()
    {
        var client = new FakeCloudflareDnsClient
        {
            CreateException = new CloudflareDnsException("create failed") { StatusCode = 500 },
        };
        var reconciler = CreateReconciler(client);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None));

        Assert.Contains("failed after", ex.Message, StringComparison.Ordinal);
        Assert.Contains("create failed", ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(TestHostFactory.TestCloudFlareApiKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconcile_PartialFailureAfterAUpdates_Surfaces()
    {
        var client = new FakeCloudflareDnsClient
        {
            CreateFailType = "AAAA",
            CreateFailTypeException = new CloudflareDnsException("AAAA create failed") { StatusCode = 502 },
        };
        var reconciler = CreateReconciler(client);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10", "2001:db8::10"), CancellationToken.None));

        Assert.Contains("failed after", ex.Message, StringComparison.Ordinal);
        Assert.Contains(client.Snapshot(), r => r.Type == "A" && r.Content == "198.18.0.10");
        Assert.DoesNotContain(client.Snapshot(), r => r.Type == "AAAA");
    }

    [Fact]
    public async Task Reconcile_VerificationMismatch_Throws()
    {
        var client = new FakeCloudflareDnsClient();
        // Force verification lists to observe a mismatched set after mutations.
        client.MutationsBeforeListFailure = 1;
        client.FailListAfterMutationsException = new CloudflareDnsException(
            "Cloudflare DNS verification failed for 'nntpd01.usenet.ninja': mismatched")
        {
            FailedOperation = "Verify",
        };
        var reconciler = CreateReconciler(client);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None));

        Assert.Contains("failed after", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reconcile_Cancellation_Propagates()
    {
        var client = new FakeCloudflareDnsClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var reconciler = CreateReconciler(client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), cts.Token));
    }

    [Fact]
    public async Task Reconcile_ConcurrentCalls_AreSerialized()
    {
        var client = new FakeCloudflareDnsClient();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var concurrent = 0;
        var maxConcurrent = 0;

        client.OnMutate = async ct =>
        {
            Interlocked.Increment(ref entered);
            var now = Interlocked.Increment(ref concurrent);
            Interlocked.Exchange(ref maxConcurrent, Math.Max(Volatile.Read(ref maxConcurrent), now));
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            Interlocked.Decrement(ref concurrent);
        };

        var reconciler = CreateReconciler(client);
        var desired = Desired("198.18.0.10");

        var first = reconciler.ReconcileAsync(ZoneId, Fqdn, desired, CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref entered) >= 1);

        var second = reconciler.ReconcileAsync(ZoneId, Fqdn, desired, CancellationToken.None);
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref entered));

        gate.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task Reconcile_ProxiedRecord_IsUpdatedToDnsOnly()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(new CloudflareDnsRecord
        {
            Id = "proxied",
            Type = "A",
            Name = Fqdn,
            Content = "198.18.0.10",
            Proxied = true,
        });

        var reconciler = CreateReconciler(client);
        await reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None);

        Assert.Equal(1, client.UpdateCallCount);
        var updated = Assert.Single(client.Snapshot());
        Assert.Equal(false, updated.Proxied);
        Assert.Equal(CloudflareManagedDnsPolicy.ManagedTtl, updated.Ttl);
    }

    [Fact]
    public async Task Reconcile_IncorrectTtl_IsUpdatedInPlace()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(new CloudflareDnsRecord
        {
            Id = "a1",
            Type = "A",
            Name = Fqdn,
            Content = "198.18.0.10",
            Proxied = false,
            Ttl = 120,
        });

        await CreateReconciler(client).ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None);

        Assert.Equal(1, client.UpdateCallCount);
        Assert.Equal(0, client.CreateCallCount);
        Assert.Equal(0, client.DeleteCallCount);
        var updated = Assert.Single(client.Snapshot());
        Assert.Equal(CloudflareManagedDnsPolicy.ManagedTtl, updated.Ttl);
        Assert.Equal(false, updated.Proxied);
    }

    [Fact]
    public async Task Reconcile_IncorrectTtlAndProxied_UpdatesOnce()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(new CloudflareDnsRecord
        {
            Id = "a1",
            Type = "A",
            Name = Fqdn,
            Content = "198.18.0.10",
            Proxied = true,
            Ttl = 60,
        });

        await CreateReconciler(client).ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None);

        Assert.Equal(1, client.UpdateCallCount);
        var updated = Assert.Single(client.Snapshot());
        Assert.Equal(CloudflareManagedDnsPolicy.ManagedTtl, updated.Ttl);
        Assert.Equal(false, updated.Proxied);
    }

    private static CloudflareDnsReconciler CreateReconciler(ICloudflareDnsClient client) =>
        new(client, Options.Create<AcmeCloudflareOptions>(TestHostFactory.CreateValidOptions()), NullLogger<CloudflareDnsReconciler>.Instance);

    private static ResolvedBindAddresses Desired(params string[] addresses) =>
        new(addresses.Select(IPAddress.Parse));

    private static CloudflareDnsRecord Record(string id, string type, string content, string? name = null) =>
        new()
        {
            Id = id,
            Type = type,
            Name = name ?? Fqdn,
            Content = content,
            Proxied = false,
            Ttl = CloudflareManagedDnsPolicy.ManagedTtl,
        };

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
}

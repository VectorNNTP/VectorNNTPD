using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.ReaderService.Tests.Cloudflare.Reconciliation;

/// <summary>
/// Final regression coverage for staged create-all-then-delete, retry classification, and exact-set verify.
/// </summary>
public sealed class CloudflareDnsFinalRegressionTests
{
    private const string ZoneId = "zone-test";
    private const string Fqdn = "nntpd01.usenet.ninja";

    [Fact]
    public async Task TransientFailure_ThenSuccessfulRetry_ReportsSuccessOnlyAfterVerify()
    {
        var client = new FakeCloudflareDnsClient
        {
            CreateFailType = "AAAA",
            CreateFailTypeRemaining = 1,
            CreateFailTypeException = new CloudflareDnsException("transient 502")
            {
                StatusCode = 502,
                IsOutcomeUncertain = true,
            },
        };

        await CreateReconciler(client)
            .ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10", "2001:db8::10"), CancellationToken.None);

        Assert.Equal(2, client.Snapshot().Count);
        Assert.True(client.CreateCallCount >= 2);
    }

    [Fact]
    public async Task PermanentAuthFailure_DoesNotRetryPointlessly()
    {
        var client = new FakeCloudflareDnsClient
        {
            ListException = new CloudflareDnsException("Authentication error")
            {
                StatusCode = 401,
                IsPermanentFailure = true,
                FailedOperation = "GET list",
            },
        };

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateReconciler(client).ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
        Assert.Equal(401, ex.StatusCode);
        Assert.Equal(1, client.ListCallCount);
        Assert.Equal(0, client.CreateCallCount);
    }

    [Fact]
    public async Task TimeoutAfterMutationApplied_RetryConvergesWithoutDuplicateSuccessClaim()
    {
        var client = new FakeCloudflareDnsClient
        {
            CreateFailType = "A",
            CreateFailTypeRemaining = 1,
            ApplyCreateThenFailUncertain = true,
            CreateFailTypeException = new CloudflareDnsException("timeout after apply")
            {
                IsOutcomeUncertain = true,
            },
        };

        await CreateReconciler(client)
            .ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None);

        Assert.Single(client.Snapshot());
        Assert.Equal("198.18.0.10", client.Snapshot()[0].Content);
    }

    [Fact]
    public async Task TimeoutAfterMutationNotApplied_RetryCreatesThenVerifies()
    {
        var client = new FakeCloudflareDnsClient
        {
            CreateFailType = "A",
            CreateFailTypeRemaining = 1,
            ApplyCreateThenFailUncertain = false,
            CreateFailTypeException = new CloudflareDnsException("timeout no apply")
            {
                IsOutcomeUncertain = true,
            },
        };

        await CreateReconciler(client)
            .ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None);

        Assert.Single(client.Snapshot());
        Assert.True(client.SuccessfulCreateCount >= 1);
    }

    [Fact]
    public async Task Retry_ReReadsActualState_DoesNotAssumePriorMutationFailed()
    {
        var client = new FakeCloudflareDnsClient
        {
            CreateFailType = "AAAA",
            CreateFailTypeRemaining = 1,
            ApplyCreateThenFailUncertain = true,
            CreateFailTypeException = new CloudflareDnsException("uncertain") { IsOutcomeUncertain = true },
        };

        await CreateReconciler(client)
            .ReconcileAsync(ZoneId, Fqdn, Desired("10.0.0.1", "fd00::1"), CancellationToken.None);

        // Attempt 1: list A+AAAA; attempt 2: list A+AAAA again (+ verify lists). Must exceed one attempt.
        Assert.True(client.ListCallCount > 4);
        Assert.Equal(2, client.Snapshot().Count);
    }

    [Fact]
    public async Task RetryExhaustion_AfterPartialChanges_FailsClosed()
    {
        var client = new FakeCloudflareDnsClient
        {
            CreateFailType = "AAAA",
            CreateFailTypeException = new CloudflareDnsException("AAAA always fails")
            {
                StatusCode = 502,
                IsOutcomeUncertain = true,
            },
        };

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateReconciler(client).ReconcileAsync(
                ZoneId,
                Fqdn,
                Desired("198.18.0.10", "2001:db8::10"),
                CancellationToken.None));

        Assert.Contains("failed after", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(client.Snapshot(), r => r.Type == "A");
        Assert.DoesNotContain(client.Snapshot(), r => r.Type == "AAAA");
    }

    [Fact]
    public async Task CreateFailure_BeforeAnyDeletion_LeavesStaleIntact()
    {
        var client = new FakeCloudflareDnsClient
        {
            CreateException = new CloudflareDnsException("create failed")
            {
                StatusCode = 500,
                IsOutcomeUncertain = true,
            },
        };
        client.Seed(Record("stale", "A", "198.18.0.99"));

        await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateReconciler(client).ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None));

        Assert.Contains(client.Snapshot(), r => r.Content == "198.18.0.99");
        Assert.Equal(0, client.SuccessfulDeleteCount);
    }

    [Fact]
    public async Task DeleteFailure_AfterDesiredCreated_FailsClosed_KeepsSuperset()
    {
        var client = new FakeCloudflareDnsClient
        {
            DeleteException = new CloudflareDnsException("delete failed")
            {
                StatusCode = 500,
                IsOutcomeUncertain = true,
            },
        };
        client.Seed(Record("stale", "A", "198.18.0.99"));

        await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateReconciler(client).ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None));

        Assert.Contains(client.Snapshot(), r => r.Content == "198.18.0.10");
        Assert.Contains(client.Snapshot(), r => r.Content == "198.18.0.99");
    }

    [Fact]
    public async Task CrossFamily_CreatesAllMissingBeforeAnyDeletes()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(
            Record("stale-a", "A", "198.18.0.99"),
            Record("stale-aaaa", "AAAA", "2001:db8::99"));

        await CreateReconciler(client)
            .ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10", "2001:db8::10"), CancellationToken.None);

        Assert.Equal(["Create:A", "Create:AAAA", "Delete", "Delete"], client.MutationOrder.Take(4).ToArray());
        Assert.DoesNotContain(client.Snapshot(), r => r.Content is "198.18.0.99" or "2001:db8::99");
    }

    [Fact]
    public async Task AFailureWhileAaaaAlsoNeedsChanges_DoesNotDeleteYet()
    {
        var client = new FakeCloudflareDnsClient
        {
            CreateFailType = "A",
            CreateFailTypeException = new CloudflareDnsException("A create failed")
            {
                StatusCode = 502,
                IsOutcomeUncertain = true,
            },
        };
        client.Seed(
            Record("stale-a", "A", "198.18.0.99"),
            Record("stale-aaaa", "AAAA", "2001:db8::99"));

        await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateReconciler(client).ReconcileAsync(
                ZoneId,
                Fqdn,
                Desired("198.18.0.10", "2001:db8::10"),
                CancellationToken.None));

        Assert.Equal(0, client.SuccessfulDeleteCount);
        Assert.DoesNotContain(client.MutationOrder, m => m.StartsWith("Create:AAAA", StringComparison.Ordinal));
        Assert.Contains(client.Snapshot(), r => r.Content == "198.18.0.99");
        Assert.Contains(client.Snapshot(), r => r.Content == "2001:db8::99");
    }

    [Fact]
    public async Task AaaaFailureAfterACreated_BeforeDeletes_KeepsStale()
    {
        var client = new FakeCloudflareDnsClient
        {
            CreateFailType = "AAAA",
            CreateFailTypeException = new CloudflareDnsException("AAAA create failed")
            {
                StatusCode = 502,
                IsOutcomeUncertain = true,
            },
        };
        client.Seed(
            Record("stale-a", "A", "198.18.0.99"),
            Record("stale-aaaa", "AAAA", "2001:db8::99"));

        await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateReconciler(client).ReconcileAsync(
                ZoneId,
                Fqdn,
                Desired("198.18.0.10", "2001:db8::10"),
                CancellationToken.None));

        Assert.Contains(client.Snapshot(), r => r.Content == "198.18.0.10");
        Assert.Equal(0, client.SuccessfulDeleteCount);
        Assert.Contains(client.Snapshot(), r => r.Content == "198.18.0.99");
    }

    [Fact]
    public async Task Ipv6ExpandedAndCompressedForms_NormalizeToSameDesiredSet()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(Record("aaaa", "AAAA", "2001:db8::1"));

        var desired = new ResolvedBindAddresses(
        [
            IPAddress.Parse("2001:0db8:0000:0000:0000:0000:0000:0001"),
        ]);

        await CreateReconciler(client).ReconcileAsync(ZoneId, Fqdn, desired, CancellationToken.None);

        Assert.Equal(0, client.CreateCallCount);
        Assert.Single(client.Snapshot());
    }

    [Fact]
    public async Task ProxiedDesiredRecord_IsUpdatedBeforeSuccess()
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

        await CreateReconciler(client)
            .ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None);

        Assert.Equal(1, client.UpdateCallCount);
        Assert.False(Assert.Single(client.Snapshot()).Proxied);
    }

    [Fact]
    public async Task Verify_RejectsProxiedRecordEvenIfContentMatches()
    {
        var client = new FakeCloudflareDnsClient();
        client.TransformListedRecords = (_, records, _) =>
            records
                .Select(r => new CloudflareDnsRecord
                {
                    Id = r.Id,
                    Type = r.Type,
                    Name = r.Name,
                    Content = r.Content,
                    Ttl = r.Ttl,
                    Proxied = true,
                })
                .ToArray();

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateReconciler(client).ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None));

        Assert.Contains("proxied", ex.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationDuringBackoff_DoesNotReportSuccess()
    {
        var client = new FakeCloudflareDnsClient
        {
            CreateException = new CloudflareDnsException("fail")
            {
                StatusCode = 502,
                IsOutcomeUncertain = true,
            },
        };
        using var cts = new CancellationTokenSource();
        var failedOnce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = client.CreateException;
        client.CreateException = new CloudflareDnsException("fail")
        {
            StatusCode = 502,
            IsOutcomeUncertain = true,
        };

        // Signal after first create attempt by wrapping via OnMutate.
        client.OnMutate = _ =>
        {
            failedOnce.TrySetResult();
            return Task.CompletedTask;
        };
        // CreateException throws after OnMutate — keep original fail.
        client.CreateException = original;

        var task = CreateReconciler(client).ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), cts.Token);
        await failedOnce.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task ConcurrentReconcile_SameInstance_IsSerialized()
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
        var first = reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref entered) >= 1);
        var second = reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None);
        await Task.Delay(30);
        Assert.Equal(1, Volatile.Read(ref entered));
        gate.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, max);
    }

    [Fact]
    public async Task ExternalModification_BetweenReadAndMutation_RetryConvergesOrFailsClosed()
    {
        var client = new FakeCloudflareDnsClient();
        var injected = 0;
        client.OnMutate = _ =>
        {
            // Between planned create and completion: external actor also publishes desired A.
            if (Interlocked.Exchange(ref injected, 1) == 0)
            {
                client.Inject(Record("external", "A", "198.18.0.10"));
            }

            return Task.CompletedTask;
        };

        // May succeed on retry after deduping, or exhaust if inject keeps happening — inject only once.
        await CreateReconciler(client)
            .ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None);

        Assert.Single(client.Snapshot());
        Assert.Equal("198.18.0.10", client.Snapshot()[0].Content);
    }

    [Fact]
    public async Task ExternalModification_DetectedOnFinalVerification_FailsClosed()
    {
        var client = new FakeCloudflareDnsClient();
        client.TransformListedRecords = (type, records, listCallCount) =>
        {
            // After mutations (create call happened), inject an unexpected A on verification lists.
            if (client.SuccessfulCreateCount > 0
                && string.Equals(type, "A", StringComparison.OrdinalIgnoreCase)
                && listCallCount > 2)
            {
                return records
                    .Append(Record("external-stale", "A", "198.18.0.77"))
                    .ToArray();
            }

            return records;
        };

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateReconciler(client).ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None));

        Assert.Contains("verification failed", ex.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrivateAddresses_RemainPublished()
    {
        var client = new FakeCloudflareDnsClient();
        await CreateReconciler(client).ReconcileAsync(
            ZoneId,
            Fqdn,
            Desired("10.0.0.8", "192.168.0.8", "fd00::8"),
            CancellationToken.None);

        Assert.Equal(3, client.Snapshot().Count);
    }

    [Fact]
    public async Task NoEligibleAddresses_StartupFails()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["127.0.0.1"];
        var service = new CloudflareDnsReconciliationService(
            Options.Create<AcmeCloudflareOptions>(options),
            new BindAddressResolver(new FakeLocalIpAddressAssignee(IPAddress.Loopback), NullLogger<BindAddressResolver>.Instance),
            CreateReconciler(new FakeCloudflareDnsClient()),
            NullLogger<CloudflareDnsReconciliationService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.Contains("No eligible IP addresses", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateAndPagination_StillConverge()
    {
        var client = new FakeCloudflareDnsClient();
        client.Seed(
            Record("a1", "A", "198.18.0.10"),
            Record("a2", "A", "198.18.0.10"),
            Record("aaaa1", "AAAA", "2001:db8::10"),
            Record("aaaa2", "AAAA", "2001:db8::10"));

        await CreateReconciler(client)
            .ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10", "2001:db8::10"), CancellationToken.None);

        Assert.Equal(2, client.Snapshot().Count);
    }

    [Fact]
    public async Task PermanentValidationFailure_OnDelete_DoesNotRetryIndefinitely()
    {
        var client = new FakeCloudflareDnsClient
        {
            DeleteException = new CloudflareDnsException("validation error")
            {
                StatusCode = 400,
                IsPermanentFailure = true,
            },
        };
        client.Seed(Record("stale", "A", "198.18.0.99"));

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            CreateReconciler(client).ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None));

        Assert.True(ex.IsPermanentFailure);
        // Create once, delete once — no reconciler retry loop.
        Assert.Equal(1, client.SuccessfulCreateCount);
        Assert.Equal(1, client.DeleteCallCount);
        Assert.DoesNotContain("failed after", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CloudflareDnsReconciler CreateReconciler(ICloudflareDnsClient client) =>
        new(client, Options.Create<AcmeCloudflareOptions>(TestHostFactory.CreateValidOptions()), NullLogger<CloudflareDnsReconciler>.Instance);

    private static ResolvedBindAddresses Desired(params string[] addresses) =>
        new(addresses.Select(IPAddress.Parse));

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

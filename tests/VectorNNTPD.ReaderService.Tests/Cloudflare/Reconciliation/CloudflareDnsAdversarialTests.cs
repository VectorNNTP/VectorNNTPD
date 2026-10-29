using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.ReaderService.Tests.Cloudflare.Reconciliation;

/// <summary>
/// Adversarial coverage for non-atomic Cloudflare mutations, uncertain outcomes, and fail-closed startup.
/// </summary>
public sealed class CloudflareDnsAdversarialTests
{
    private const string ZoneId = "zone-test";
    private const string Fqdn = "nntpd01.usenet.ninja";

    [Fact]
    public async Task ASucceedsThenAaaaCreateFails_DoesNotReportSuccess_LeavesPartialRemoteState()
    {
        var client = new FakeCloudflareDnsClient
        {
            CreateFailType = "AAAA",
            CreateFailTypeException = new CloudflareDnsException("AAAA create failed") { StatusCode = 500, IsOutcomeUncertain = true },
        };
        var reconciler = CreateReconciler(client);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10", "2001:db8::10"), CancellationToken.None));

        Assert.Contains("failed after", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(client.Snapshot(), r => r.Type == "A" && r.Content == "198.18.0.10");
        Assert.DoesNotContain(client.Snapshot(), r => r.Type == "AAAA");
        Assert.True(client.SuccessfulCreateCount >= 1);
    }

    [Fact]
    public async Task AaaaSucceedsThenADeleteFails_DoesNotReportSuccess()
    {
        // Seed stale A that must be deleted after AAAA create path; fail deletes permanently.
        var client = new FakeCloudflareDnsClient
        {
            DeleteException = new CloudflareDnsException("delete failed") { StatusCode = 500, IsOutcomeUncertain = true },
        };
        client.Seed(
            Record("stale", "A", "198.18.0.99"),
            Record("aaaa", "AAAA", "2001:db8::10"));

        var reconciler = CreateReconciler(client);
        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10", "2001:db8::10"), CancellationToken.None));

        Assert.Contains("failed after", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Desired A was created (create-before-delete) but stale may remain.
        Assert.Contains(client.Snapshot(), r => r.Type == "A" && r.Content == "198.18.0.10");
        Assert.Contains(client.Snapshot(), r => r.Content == "198.18.0.99");
    }

    [Fact]
    public async Task CreateSucceedsThenDeleteFails_DoesNotReportSuccess()
    {
        var client = new FakeCloudflareDnsClient
        {
            DeleteException = new CloudflareDnsException("delete failed")
            {
                StatusCode = 400,
                IsPermanentFailure = true,
            },
        };
        client.Seed(Record("stale", "A", "198.18.0.99"));

        var reconciler = CreateReconciler(client);
        await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None));

        var snapshot = client.Snapshot();
        Assert.Contains(snapshot, r => r.Content == "198.18.0.10");
        Assert.Contains(snapshot, r => r.Content == "198.18.0.99");
    }

    [Fact]
    public async Task DeleteWouldSucceedButCreateFailsFirst_DoesNotReportSuccess()
    {
        var client = new FakeCloudflareDnsClient
        {
            CreateException = new CloudflareDnsException("create failed")
            {
                StatusCode = 400,
                IsPermanentFailure = true,
            },
        };
        client.Seed(Record("stale", "A", "198.18.0.99"));

        var reconciler = CreateReconciler(client);
        await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None));

        // create-before-delete: create failed, so stale should still be present and desired absent.
        Assert.Contains(client.Snapshot(), r => r.Content == "198.18.0.99");
        Assert.DoesNotContain(client.Snapshot(), r => r.Content == "198.18.0.10");
        Assert.Equal(0, client.SuccessfulDeleteCount);
    }

    [Fact]
    public async Task UncertainCreateOutcome_AppliedRemotely_RetryConvergesAndVerifies()
    {
        var client = new FakeCloudflareDnsClient
        {
            CreateFailType = "AAAA",
            CreateFailTypeRemaining = 1,
            CreateFailTypeException = new CloudflareDnsException("timeout after apply")
            {
                IsOutcomeUncertain = true,
            },
            ApplyCreateThenFailUncertain = true,
        };

        var reconciler = CreateReconciler(client);
        await reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10", "2001:db8::10"), CancellationToken.None);

        var snapshot = client.Snapshot();
        Assert.Contains(snapshot, r => r.Type == "A" && r.Content == "198.18.0.10");
        Assert.Contains(snapshot, r => r.Type == "AAAA" && r.Content == "2001:db8::10");
        Assert.Equal(2, snapshot.Count);
    }

    [Fact]
    public async Task UncertainOutcomeExhausted_FailsClosed_DoesNotClaimSuccess()
    {
        var client = new FakeCloudflareDnsClient
        {
            CreateFailType = "A",
            CreateFailTypeException = new CloudflareDnsException("transport timeout")
            {
                IsOutcomeUncertain = true,
            },
            // Do not apply: simulates a timeout where we cannot know whether Cloudflare wrote —
            // here we model the "never observed as present" exhausted path by not mutating.
            ApplyCreateThenFailUncertain = false,
        };

        var reconciler = CreateReconciler(client);
        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None));

        Assert.True(ex.IsOutcomeUncertain);
        Assert.Contains("must not proceed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(client.Snapshot(), r => r.Content == "198.18.0.10");
    }

    [Fact]
    public async Task RetryAfterPartialMutation_ReReadsAndConverges()
    {
        var client = new FakeCloudflareDnsClient
        {
            CreateFailType = "AAAA",
            CreateFailTypeException = new CloudflareDnsException("AAAA failed"),
        };

        var reconciler = CreateReconciler(client);
        await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("10.0.0.5", "fd12:3456:789a::1"), CancellationToken.None));

        Assert.Contains(client.Snapshot(), r => r.Content == "10.0.0.5");
        Assert.DoesNotContain(client.Snapshot(), r => r.Type == "AAAA");

        client.CreateFailType = null;
        client.CreateFailTypeException = null;

        await reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("10.0.0.5", "fd12:3456:789a::1"), CancellationToken.None);

        var snapshot = client.Snapshot();
        Assert.Contains(snapshot, r => r.Type == "A" && r.Content == "10.0.0.5");
        Assert.Contains(snapshot, r => r.Type == "AAAA" && r.Content == "fd12:3456:789a::1");
        Assert.Equal(2, snapshot.Count);
    }

    [Fact]
    public async Task PrivateIpv4AndIpv6_ArePublishedExactly()
    {
        var client = new FakeCloudflareDnsClient();
        var reconciler = CreateReconciler(client);
        var desired = Desired("10.0.0.8", "192.168.1.8", "172.16.0.8", "fd00::8");

        await reconciler.ReconcileAsync(ZoneId, Fqdn, desired, CancellationToken.None);

        var snapshot = client.Snapshot();
        Assert.Equal(4, snapshot.Count);
        Assert.Contains(snapshot, r => r.Type == "A" && r.Content == "10.0.0.8");
        Assert.Contains(snapshot, r => r.Type == "A" && r.Content == "192.168.1.8");
        Assert.Contains(snapshot, r => r.Type == "A" && r.Content == "172.16.0.8");
        Assert.Contains(snapshot, r => r.Type == "AAAA" && r.Content == "fd00::8");
    }

    [Fact]
    public async Task VerificationMismatch_NeverReportsSuccess()
    {
        var client = new AlwaysMismatchAfterMutationsClient();
        var reconciler = CreateReconciler(client);

        var ex = await Assert.ThrowsAsync<CloudflareDnsException>(() =>
            reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), CancellationToken.None));

        Assert.Contains("must not proceed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("verification failed", ex.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationDuringReconcile_DoesNotReportSuccess()
    {
        var client = new FakeCloudflareDnsClient();
        using var cts = new CancellationTokenSource();
        client.OnMutate = _ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        var reconciler = CreateReconciler(client);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reconciler.ReconcileAsync(ZoneId, Fqdn, Desired("198.18.0.10"), cts.Token));
    }

    [Fact]
    public async Task Startup_AfterDnsPartialFailure_DoesNotReachRunning_AndRollbackStopsDnsService()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["*"];

        var client = new FakeCloudflareDnsClient
        {
            CreateFailType = "AAAA",
            CreateFailTypeException = new CloudflareDnsException("AAAA failed"),
        };

        var resolver = new BindAddressResolver(
            new FakeLocalIpAddressAssignee(IPAddress.Parse("10.0.0.9"), IPAddress.Parse("fd00::9")),
            NullLogger<BindAddressResolver>.Instance);
        var reconciler = new CloudflareDnsReconciler(client, Options.Create<AcmeCloudflareOptions>(TestHostFactory.CreateValidOptions()), NullLogger<CloudflareDnsReconciler>.Instance);
        var dns = new CloudflareDnsReconciliationService(
            Options.Create<AcmeCloudflareOptions>(options),
            resolver,
            reconciler,
            NullLogger<CloudflareDnsReconciliationService>.Instance);

        var other = new FakeApplicationService("other");
        await using var lifecycle = TestHostFactory.CreateLifecycle([TestHostFactory.WrapDns(dns), other], options);

        await Assert.ThrowsAsync<CloudflareDnsException>(() => lifecycle.StartAsync(CancellationToken.None));
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        Assert.Equal(0, other.StartCount);
        Assert.DoesNotContain(
            client.Snapshot(),
            r => string.Equals(
                r.Name.Trim().TrimEnd('.'),
                options.Fqdn.Trim().TrimEnd('.'),
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MutationTimeout_IsMarkedUncertain_ByHttpClient()
    {
        var handler = new ScriptedTimeoutHandler();
        // codeql[cs/local-not-disposed] HttpClient ownership transfers to CloudflareDnsClient for the test lifetime.
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.cloudflare.com/client/v4/"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var client = new CloudflareDnsClient(
            httpClient,
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
                },
                CancellationToken.None));

        Assert.True(ex.IsOutcomeUncertain);
        Assert.DoesNotContain(TestHostFactory.TestCloudFlareApiKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BindAddressResolver_PublishesPrivateAddresses_ExcludesLinkLocal()
    {
        var privateV4 = IPAddress.Parse("10.1.2.3");
        var ula = IPAddress.Parse("fd12::1");
        var linkLocal = IPAddress.Parse("169.254.1.1");
        var resolver = new BindAddressResolver(
            new FakeLocalIpAddressAssignee(privateV4, ula, linkLocal),
            NullLogger<BindAddressResolver>.Instance);

        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["*"];
        var resolved = resolver.Resolve(options);

        Assert.Contains(privateV4, resolved.IPv4);
        Assert.Contains(ula, resolved.IPv6);
        Assert.DoesNotContain(linkLocal, resolved.All);
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

    /// <summary>
    /// Lists empty during mutation planning, then returns a mismatched set on verification lists.
    /// </summary>
    private sealed class AlwaysMismatchAfterMutationsClient : ICloudflareDnsClient
    {
        private int _creates;

        public Task<IReadOnlyList<CloudflareDnsRecord>> ListRecordsAsync(
            string zoneId,
            string fqdn,
            string type,
            CancellationToken cancellationToken)
        {
            if (_creates == 0)
            {
                return Task.FromResult<IReadOnlyList<CloudflareDnsRecord>>([]);
            }

            // After any create, always report a mismatched address so verification cannot succeed.
            return Task.FromResult<IReadOnlyList<CloudflareDnsRecord>>(
            [
                new CloudflareDnsRecord
                {
                    Id = "mismatch",
                    Type = type,
                    Name = fqdn,
                    Content = type == "AAAA" ? "2001:db8::99" : "198.18.0.99",
                },
            ]);
        }

        public Task<IReadOnlyList<CloudflareDnsRecord>> ListAllRecordsForNameAsync(
            string zoneId,
            string fqdn,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CloudflareDnsRecord>>([]);

        public Task<CloudflareDnsRecord> CreateRecordAsync(
            string zoneId,
            CloudflareDnsRecordWriteRequest request,
            CancellationToken cancellationToken)
        {
            _creates++;
            return Task.FromResult(new CloudflareDnsRecord
            {
                Id = $"c{_creates}",
                Type = request.Type,
                Name = request.Name,
                Content = request.Content,
            });
        }

        public Task<CloudflareDnsRecord> UpdateRecordAsync(
            string zoneId,
            string recordId,
            CloudflareDnsRecordWriteRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CloudflareDnsRecord
            {
                Id = recordId,
                Type = request.Type,
                Name = request.Name,
                Content = request.Content,
            });

        public Task DeleteRecordAsync(string zoneId, string recordId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class ScriptedTimeoutHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                Headers = { },
            };
        }
    }
}

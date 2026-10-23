using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.Common.Core;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Listener;
using VectorNNTPD.StorageService.Tests.Fixtures;
using VectorNNTPD.StorageService.Tests.TestDoubles;

namespace VectorNNTPD.StorageService.Tests.Hosting
{

    public sealed class StorageDnsPublicationRollbackTests
    {
        private static readonly IPAddress[] HealthyAddresses = [IPAddress.Parse("198.18.0.66"), IPAddress.Parse("2001:db8::10")];
        private static readonly IPAddress[] IntruderAddresses = [IPAddress.Parse("198.18.0.67"), IPAddress.Parse("2001:db8::11")];

        [Fact]
        public async Task Bind_failure_before_publication_leaves_existing_records()
        {
            var harness = CreateHarness(HealthyAddresses);
            Seed(harness, "healthy-a", "A", "198.18.0.66", "healthy-aaaa", "AAAA", "2001:db8::10");
            var before = Ids(harness.Client);
            await harness.Service.StartAsync(CancellationToken.None);

            using var blocker = new TcpListener(IPAddress.Loopback, 0);
            blocker.Start();
            var port = ((IPEndPoint)blocker.LocalEndpoint!).Port;
            var published = 0;
            await using var listener = CreateListener(
                port,
                cancellationToken =>
                {
                    Interlocked.Increment(ref published);
                    return harness.Service.PublishAfterListenerBoundAsync(cancellationToken);
                });

            await Assert.ThrowsAsync<SocketException>(() => listener.StartAsync(CancellationToken.None));
            Assert.Equal(0, published);
            Assert.Equal(StorageNntpListenerState.Stopped, listener.State);
            await harness.Service.StopAsync(CancellationToken.None);

            Assert.Equal(before, Ids(harness.Client));
            Assert.Equal(0, harness.Client.CreateCallCount);
            Assert.Equal(0, harness.Client.DeleteCallCount);
        }

        [Fact]
        public async Task Failure_after_one_created_record_deletes_only_that_record()
        {
            var harness = CreateHarness(IntruderAddresses);
            Seed(harness, "healthy-a", "A", "198.18.0.66", "healthy-aaaa", "AAAA", "2001:db8::10");
            harness.Client.CreateFailType = CloudflareDnsRecordTypes.AAAA;
            await harness.Service.StartAsync(CancellationToken.None);

            await Assert.ThrowsAsync<CloudflareDnsException>(() =>
                harness.Service.PublishAfterListenerBoundAsync(CancellationToken.None));
            await harness.Service.StopAsync(CancellationToken.None);

            var left = harness.Client.Snapshot();
            Assert.Contains(left, static record => record.Id == "healthy-a" && record.Content == "198.18.0.66");
            Assert.Contains(left, static record => record.Id == "healthy-aaaa" && record.Content == "2001:db8::10");
            Assert.DoesNotContain(left, static record => record.Content == "198.18.0.67");
            Assert.DoesNotContain(left, static record => record.Content == "2001:db8::11");
        }

        [Fact]
        public async Task Failure_after_registration_completes_removes_the_owning_instance_records()
        {
            var harness = CreateHarness(HealthyAddresses);
            var publisher = new FakeApplicationService(
                "StorageNntpListener",
                onStart: cancellationToken => harness.Service.PublishAfterListenerBoundAsync(cancellationToken));
            var later = new FakeApplicationService(
                "later",
                onStart: _ => throw new InvalidOperationException("later failed"));
            var manager = new ApplicationServiceManager(
                [new CloudflareDnsReconciliationApplicationService(harness.Service), publisher, later],
                StorageServerTestOptions.CreateValid(),
                NullLogger<ApplicationServiceManager>.Instance);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(CancellationToken.None));

            Assert.Equal("later failed", error.Message);
            Assert.Empty(harness.Client.Snapshot());
            Assert.True(harness.Client.CreateCallCount > 0);
            Assert.True(harness.Client.DeleteCallCount > 0);
        }

        [Fact]
        public async Task Graceful_shutdown_of_the_owning_instance_removes_its_records()
        {
            var harness = CreateHarness(HealthyAddresses);
            var listener = new FakeApplicationService(
                "StorageNntpListener",
                onStart: cancellationToken => harness.Service.PublishAfterListenerBoundAsync(cancellationToken));
            var manager = new ApplicationServiceManager(
                [new CloudflareDnsReconciliationApplicationService(harness.Service), listener],
                StorageServerTestOptions.CreateValid(),
                NullLogger<ApplicationServiceManager>.Instance);

            await manager.StartAsync(CancellationToken.None);
            Assert.NotEmpty(harness.Client.Snapshot());

            await manager.StopAsync(CancellationToken.None);

            Assert.Empty(harness.Client.Snapshot());
        }

        [Fact]
        public async Task Second_instance_bind_failure_leaves_the_healthy_instance_records()
        {
            var client = new FakeCloudflareDnsClient();
            var owner = CreateHarness(HealthyAddresses, client);
            var intruder = CreateHarness(IntruderAddresses, client);
            var port = StorageServerTestOptions.GetFreeTcpPort();
            await owner.Service.StartAsync(CancellationToken.None);
            await using var ownerListener = CreateListener(
                port,
                owner.Service.PublishAfterListenerBoundAsync);
            await ownerListener.StartAsync(CancellationToken.None);
            var owned = Ids(client);
            Assert.NotEmpty(owned);

            await intruder.Service.StartAsync(CancellationToken.None);
            var published = 0;
            await using var intruderListener = CreateListener(
                port,
                cancellationToken =>
                {
                    Interlocked.Increment(ref published);
                    return intruder.Service.PublishAfterListenerBoundAsync(cancellationToken);
                });
            await Assert.ThrowsAsync<SocketException>(() => intruderListener.StartAsync(CancellationToken.None));
            await intruder.Service.StopAsync(CancellationToken.None);

            Assert.Equal(0, published);
            Assert.Equal(StorageNntpListenerState.Running, ownerListener.State);
            Assert.Equal(owned, Ids(client));

            await ownerListener.StopAsync(CancellationToken.None);
            await owner.Service.StopAsync(CancellationToken.None);
            Assert.Empty(client.Snapshot());
        }

        [Fact]
        public async Task Cleanup_is_idempotent_and_does_not_remove_another_instance_records()
        {
            var client = new FakeCloudflareDnsClient();
            var owner = CreateHarness(HealthyAddresses, client);
            var other = CreateHarness(IntruderAddresses, client);
            await owner.Service.StartAsync(CancellationToken.None);
            await owner.Service.PublishAfterListenerBoundAsync(CancellationToken.None);
            var owned = Ids(client);

            await other.Service.StartAsync(CancellationToken.None);
            await other.Service.StopAsync(CancellationToken.None);
            Assert.Equal(owned, Ids(client));

            var deletesAfterOther = client.DeleteCallCount;
            await owner.Service.StopAsync(CancellationToken.None);
            Assert.Empty(client.Snapshot());
            var deletesAfterOwner = client.DeleteCallCount;
            Assert.True(deletesAfterOwner > deletesAfterOther);

            await owner.Service.StopAsync(CancellationToken.None);
            Assert.Equal(deletesAfterOwner, client.DeleteCallCount);
            Assert.Empty(client.Snapshot());
        }

        [Fact]
        public async Task Failure_after_only_an_aaaa_record_is_created_removes_that_record()
        {
            var harness = CreateHarness(IntruderAddresses);
            harness.Client.Seed(Record("kept-a", "A", harness.Fqdn, "198.18.0.67", ttl: 300));
            harness.Client.MutationsBeforeListFailure = 1;
            harness.Client.FailListAfterMutationsException = new CloudflareDnsException("list failed after create");
            await harness.Service.StartAsync(CancellationToken.None);

            await Assert.ThrowsAsync<CloudflareDnsException>(() =>
                harness.Service.PublishAfterListenerBoundAsync(CancellationToken.None));
            await harness.Service.StopAsync(CancellationToken.None);

            var left = harness.Client.Snapshot();
            Assert.Contains(left, static record => record.Id == "kept-a" && record.Content == "198.18.0.67");
            Assert.DoesNotContain(left, static record => record.Type == "AAAA");
        }

        [Fact]
        public async Task Failure_after_a_managed_update_restores_the_previous_ttl_and_removes_the_created_aaaa()
        {
            var harness = CreateHarness(IntruderAddresses);
            harness.Client.Seed(Record("kept-a", "A", harness.Fqdn, "198.18.0.67", ttl: 120));
            harness.Client.MutationsBeforeListFailure = 2;
            harness.Client.FailListAfterMutationsException = new CloudflareDnsException("list failed after update");
            await harness.Service.StartAsync(CancellationToken.None);

            await Assert.ThrowsAsync<CloudflareDnsException>(() =>
                harness.Service.PublishAfterListenerBoundAsync(CancellationToken.None));
            await harness.Service.StopAsync(CancellationToken.None);

            var left = harness.Client.Snapshot();
            var kept = Assert.Single(left);
            Assert.Equal("kept-a", kept.Id);
            Assert.Equal(120, kept.Ttl);
        }

        [Fact]
        public async Task Failure_after_stale_deletion_begins_does_not_remove_the_desired_record()
        {
            var harness = CreateHarness([IPAddress.Parse("198.18.0.67")]);
            harness.Client.Seed(Record("stale-a", "A", harness.Fqdn, "198.18.0.66", ttl: 300));
            harness.Client.DeleteException = new CloudflareDnsException("delete failed");
            await harness.Service.StartAsync(CancellationToken.None);

            await Assert.ThrowsAsync<CloudflareDnsException>(() =>
                harness.Service.PublishAfterListenerBoundAsync(CancellationToken.None));
            await harness.Service.StopAsync(CancellationToken.None);

            var left = harness.Client.Snapshot();
            Assert.Contains(left, static record => record.Id == "stale-a");
            Assert.Contains(left, static record => record.Content == "198.18.0.67");
        }

        [Fact]
        public async Task Overlapped_stop_without_ownership_does_not_remove_the_publisher_records()
        {
            var client = new FakeCloudflareDnsClient();
            var owner = CreateHarness(HealthyAddresses, client);
            var other = CreateHarness(IntruderAddresses, client);
            await owner.Service.StartAsync(CancellationToken.None);
            await other.Service.StartAsync(CancellationToken.None);

            await Task.WhenAll(
                owner.Service.PublishAfterListenerBoundAsync(CancellationToken.None),
                other.Service.StopAsync(CancellationToken.None));

            Assert.NotEmpty(client.Snapshot());
            await owner.Service.StopAsync(CancellationToken.None);
            Assert.Empty(client.Snapshot());
            await owner.Service.StopAsync(CancellationToken.None);
            Assert.Empty(client.Snapshot());
        }

        private static DnsHarness CreateHarness(IPAddress[] addresses, FakeCloudflareDnsClient? client = null)
        {
            client ??= new FakeCloudflareDnsClient();
            var options = StorageServerTestOptions.CreateValid();
            options.BindAddress = addresses.Select(static address => address.ToString()).ToArray();
            var acme = StorageServerTestOptions.CreateValidAcme(options);
            var resolver = new BindAddressResolver(
                new AssignedLocalIpAddressAssignee(addresses),
                NullLogger<BindAddressResolver>.Instance);
            var reconciler = new CloudflareDnsReconciler(
                client,
                Options.Create(acme),
                NullLogger<CloudflareDnsReconciler>.Instance);
            var service = new CloudflareDnsReconciliationService(
                Options.Create(acme),
                resolver,
                reconciler,
                NullLogger<CloudflareDnsReconciliationService>.Instance,
                new ListenerBoundDnsPublicationLease());
            return new DnsHarness(client, service, acme.Fqdn);
        }

        private static void Seed(
            DnsHarness harness,
            string firstId,
            string firstType,
            string firstContent,
            string secondId,
            string secondType,
            string secondContent)
        {
            harness.Client.Seed(
                Record(firstId, firstType, harness.Fqdn, firstContent),
                Record(secondId, secondType, harness.Fqdn, secondContent));
        }

        private static CloudflareDnsRecord Record(string id, string type, string fqdn, string content, int ttl = 300) =>
            new()
            {
                Id = id,
                Type = type,
                Name = fqdn,
                Content = content,
                Ttl = ttl,
                Proxied = false,
            };

        private static StorageNntpListenerService CreateListener(int port, Func<CancellationToken, Task> publish)
        {
            var identity = StorageServerTestOptions.CreateValid();
            identity.BindAddress = ["127.0.0.1"];
            identity.BindPortTls = port;
            var acme = StorageServerTestOptions.CreateValidAcme(identity);
            var runtime = StorageServerRuntimeOptionsFactory.Create(identity, acme);
            var readiness = new AcmeCertificateReadiness();
            readiness.MarkReady();
            return new StorageNntpListenerService(
                runtime,
                TestListenerCertificates.CreatePublishedProvider(),
                readiness,
                NullLogger<StorageNntpListenerService>.Instance,
                publishAfterBind: publish);
        }

        private static LoopbackPort BindLoopback()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new LoopbackPort(listener, ((IPEndPoint)listener.LocalEndpoint!).Port);
        }

        private static string[] Ids(FakeCloudflareDnsClient client) =>
            client.Snapshot().Select(static record => record.Id).Order(StringComparer.Ordinal).ToArray();

        private static string[] Ids(IReadOnlyList<CloudflareDnsRecord> records) =>
            records.Select(static record => record.Id).Order(StringComparer.Ordinal).ToArray();

        private sealed class DnsHarness(FakeCloudflareDnsClient client, CloudflareDnsReconciliationService service, string fqdn)
        {
            public FakeCloudflareDnsClient Client { get; } = client;

            public CloudflareDnsReconciliationService Service { get; } = service;

            public string Fqdn { get; } = fqdn;
        }

        private sealed class LoopbackPort(TcpListener listener, int port) : IDisposable
        {
            public int Port { get; } = port;

            public void Dispose() => listener.Stop();
        }
    }
}

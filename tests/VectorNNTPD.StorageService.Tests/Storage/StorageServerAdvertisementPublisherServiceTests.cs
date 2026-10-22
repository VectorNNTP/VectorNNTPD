using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using VectorNNTPD.Common.Messaging.Cache;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Storage;
using VectorNNTPD.StorageService.Tests.Fixtures;
using VectorNNTPD.StorageService.Tests.Logging;
using VectorNNTPD.StorageService.Tests.TestDoubles;

namespace VectorNNTPD.StorageService.Tests.Storage
{

    public sealed class StorageServerAdvertisementPublisherServiceTests
    {
        [Fact]
        public async Task StartAsync_DeclaresBroadcastExchange_AndPublishesExpectedPayload()
        {
            var factory = new RecordingStorageServerRabbitMqConnectionFactory();
            await using var rabbit = CreateRabbitMq(factory);
            var capacity = new FixedStorageCapacityReader(10_000, 4_000, 6_000);
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-29T12:00:00Z"));
            var runtime = StorageServerRuntimeOptionsFactory.Create(StorageServerTestOptions.CreateValid());
            var publisher = new StorageServerAdvertisementPublisherService(
                rabbit,
                runtime,
                capacity,
                NullLogger<StorageServerAdvertisementPublisherService>.Instance,
                time);

            await rabbit.StartAsync(CancellationToken.None);
            await publisher.StartAsync(CancellationToken.None);

            var connection = factory.LastConnection!;
            Assert.NotEmpty(connection.TopologyChannels);
            Assert.Contains(
                connection.TopologyChannels.SelectMany(static c => c.Exchanges),
                static e => e.Name == CacheFleetTopology.BroadcastExchangeName
                    && e.Type == "fanout"
                    && e.Durable
                    && !e.AutoDelete);

            await WaitForAsync(() => publisher.PublishedCount >= 1, TimeSpan.FromSeconds(2));
            var channel = Assert.Single(connection.PublishChannels);
            Assert.DoesNotContain(channel.Publications, static publication => IsLifecycle(publication));
            var publication = Assert.Single(channel.Publications);
            Assert.Equal(CacheFleetTopology.BroadcastExchangeName, publication.Exchange);
            Assert.Equal(CacheFleetTopology.BroadcastExchangeName, publication.RoutingKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(publication.RoutingKey);
            Assert.Equal(CacheFleetTopology.AdvertisementExpirationMilliseconds, publication.ExpirationMilliseconds);
            Assert.Equal(StorageServerAdvertisementWireProtocol.JsonContentType, publication.ContentType);
            Assert.Equal(runtime.Fqdn, publication.AppId);
            Assert.False(publication.Persistent);
            Assert.False(publication.Mandatory);
            Assert.True(StorageServerAdvertisementWireProtocol.TryParseV1(
                publication.Body.Span,
                out var advertisement,
                out _));
            Assert.NotNull(advertisement);
            Assert.Equal(runtime.ServerId, advertisement.ServerId);
            Assert.Equal(runtime.Fqdn, advertisement.Fqdn);
            Assert.Equal(10_000, advertisement.TotalBytes);
            Assert.Equal(4_000, advertisement.UsedBytes);
            Assert.Equal(6_000, advertisement.AvailableBytes);
            Assert.Equal(runtime.BindPortTls, advertisement.VatpPort);

            time.Advance(CacheFleetTopology.AdvertisementInterval);
            await WaitForAsync(() => publisher.PublishedCount >= 2, TimeSpan.FromSeconds(2));

            await publisher.StopAsync(CancellationToken.None);
            Assert.Equal(1, channel.DisposeCount);
            var draining = Assert.Single(
                channel.Publications,
                static publication => IsLifecycle(publication, StorageServerLifecycleState.Draining));
            AssertLifecyclePublication(draining, runtime);
            Assert.True(LifecycleTimestamp(draining) > advertisement!.Timestamp);
        }

        [Fact]
        public async Task NoSpoolCapacity_PublishesZeroBytes_OnCacheBroadcast_WithThePlacementPort()
        {
            var factory = new RecordingStorageServerRabbitMqConnectionFactory();
            await using var rabbit = CreateRabbitMq(factory);
            var runtime = StorageServerRuntimeOptionsFactory.Create(StorageServerTestOptions.CreateValid());
            var publisher = new StorageServerAdvertisementPublisherService(
                rabbit,
                runtime,
                new NoSpoolCapacityReader(),
                NullLogger<StorageServerAdvertisementPublisherService>.Instance,
                new FakeTimeProvider(DateTimeOffset.Parse("2026-09-29T12:00:00Z")));

            await rabbit.StartAsync(CancellationToken.None);
            await publisher.StartAsync(CancellationToken.None);
            await WaitForAsync(() => publisher.PublishedCount >= 1, TimeSpan.FromSeconds(2));
            await publisher.StopAsync(CancellationToken.None);

            var channel = Assert.Single(factory.LastConnection!.PublishChannels);
            var heartbeat = Assert.Single(channel.Publications, static publication => !IsLifecycle(publication));
            Assert.Equal(CacheFleetTopology.BroadcastExchangeName, heartbeat.Exchange);
            Assert.Equal(CacheFleetTopology.BroadcastExchangeName, heartbeat.RoutingKey);
            Assert.True(StorageServerAdvertisementWireProtocol.TryParseV1(
                heartbeat.Body.Span,
                out var advertisement,
                out var reason));
            Assert.True(string.IsNullOrEmpty(reason));
            Assert.NotNull(advertisement);
            Assert.Equal(0, advertisement.TotalBytes);
            Assert.Equal(0, advertisement.UsedBytes);
            Assert.Equal(0, advertisement.AvailableBytes);
            Assert.Equal(runtime.BindPortTls, advertisement.VatpPort);
            Assert.Equal(runtime.ServerId, advertisement.ServerId);
            Assert.Equal(runtime.Fqdn, advertisement.Fqdn);
        }

        [Fact]
        public async Task StopAsync_EndsPublishLoop_WithoutOrphanedExecution()
        {
            var factory = new RecordingStorageServerRabbitMqConnectionFactory();
            await using var rabbit = CreateRabbitMq(factory);
            var time = new FakeTimeProvider();
            var publisher = new StorageServerAdvertisementPublisherService(
                rabbit,
                StorageServerRuntimeOptionsFactory.Create(StorageServerTestOptions.CreateValid()),
                new FixedStorageCapacityReader(1, 0, 1),
                NullLogger<StorageServerAdvertisementPublisherService>.Instance,
                time);

            await rabbit.StartAsync(CancellationToken.None);
            await publisher.StartAsync(CancellationToken.None);
            var loop = publisher.Execution;
            Assert.NotNull(loop);

            await publisher.StopAsync(CancellationToken.None);
            Assert.True(loop.IsCompleted);
            Assert.Null(publisher.Execution);
            await publisher.StopAsync(CancellationToken.None);
            var channel = Assert.Single(factory.LastConnection!.PublishChannels);
            Assert.Single(channel.Publications, static publication => IsLifecycle(publication, StorageServerLifecycleState.Draining));
        }

        [Fact]
        public async Task Startup_PublishesAdvertisements_AndNoReady_ThenOneDraining()
        {
            var factory = new RecordingStorageServerRabbitMqConnectionFactory();
            await using var rabbit = CreateRabbitMq(factory);
            var sink = new CollectingSink();
            using var loggerFactory = CreateLoggerFactory(sink);
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-29T12:00:00Z"));
            var publisher = new StorageServerAdvertisementPublisherService(
                rabbit,
                StorageServerRuntimeOptionsFactory.Create(StorageServerTestOptions.CreateValid()),
                new FixedStorageCapacityReader(1, 0, 1),
                loggerFactory.CreateLogger<StorageServerAdvertisementPublisherService>(),
                time);

            await rabbit.StartAsync(CancellationToken.None);
            await publisher.StartAsync(CancellationToken.None);
            await WaitForAsync(() => publisher.PublishedCount >= 1, TimeSpan.FromSeconds(2));

            var channel = Assert.Single(factory.LastConnection!.PublishChannels);
            Assert.DoesNotContain(channel.Publications, static publication => IsLifecycle(publication));
            Assert.Contains(channel.Publications, static publication => !IsLifecycle(publication));
            Assert.DoesNotContain(sink.Events, static log => IsEvent(log, 2903));

            await publisher.StopAsync(CancellationToken.None);
            Assert.Single(channel.Publications, static publication => IsLifecycle(publication, StorageServerLifecycleState.Draining));
            Assert.DoesNotContain(
                channel.Publications,
                static publication => System.Text.Encoding.UTF8.GetString(publication.Body.Span).Contains("\"state\":\"Ready\"", StringComparison.Ordinal));
            Assert.Single(sink.Events, static log => IsEvent(log, 2904) && log.Level == LogEventLevel.Information);
        }

        [Fact]
        public async Task DrainingPublishFailure_DoesNotBlockShutdown()
        {
            var factory = new RecordingStorageServerRabbitMqConnectionFactory();
            await using var rabbit = CreateRabbitMq(factory);
            var sink = new CollectingSink();
            using var loggerFactory = CreateLoggerFactory(sink);
            var publisher = new StorageServerAdvertisementPublisherService(
                rabbit,
                StorageServerRuntimeOptionsFactory.Create(StorageServerTestOptions.CreateValid()),
                new FixedStorageCapacityReader(8, 1, 7),
                loggerFactory.CreateLogger<StorageServerAdvertisementPublisherService>(),
                new FakeTimeProvider(DateTimeOffset.Parse("2026-09-29T12:00:00Z")));
            await rabbit.StartAsync(CancellationToken.None);
            factory.LastConnection!.BeforePublish = (publication, _) =>
                IsLifecycle(publication, StorageServerLifecycleState.Draining)
                    ? Task.FromException(new IOException("draining-publish-failed"))
                    : Task.CompletedTask;
            await publisher.StartAsync(CancellationToken.None);
            await publisher.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

            var channel = Assert.Single(factory.LastConnection.PublishChannels);
            Assert.Equal(1, channel.DisposeCount);
            Assert.Null(publisher.Execution);
            Assert.DoesNotContain(channel.Publications, static publication => IsLifecycle(publication, StorageServerLifecycleState.Draining));
            Assert.Contains(sink.Events, static log => IsEvent(log, 2905));
            Assert.DoesNotContain(sink.Events, static log => IsEvent(log, 2904));
        }

        [Fact]
        public async Task DrainingPublishHang_StopsWithinTheAnnouncementBound()
        {
            var factory = new RecordingStorageServerRabbitMqConnectionFactory();
            await using var rabbit = CreateRabbitMq(factory);
            var publisher = new StorageServerAdvertisementPublisherService(
                rabbit,
                StorageServerRuntimeOptionsFactory.Create(StorageServerTestOptions.CreateValid()),
                new FixedStorageCapacityReader(8, 1, 7),
                NullLogger<StorageServerAdvertisementPublisherService>.Instance,
                new FakeTimeProvider(DateTimeOffset.Parse("2026-09-29T12:00:00Z")));
            var hung = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await rabbit.StartAsync(CancellationToken.None);
            factory.LastConnection!.BeforePublish = (publication, cancellationToken) =>
                IsLifecycle(publication, StorageServerLifecycleState.Draining)
                    ? hung.Task.WaitAsync(cancellationToken)
                    : Task.CompletedTask;
            await publisher.StartAsync(CancellationToken.None);
            await publisher.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, Assert.Single(factory.LastConnection.PublishChannels).DisposeCount);
            Assert.Null(publisher.Execution);
        }

        private static RabbitMqService CreateRabbitMq(IRabbitMqConnectionFactory factory)
        {
            var options = new RabbitMqOptions
            {
                Hosts = ["127.0.0.1"],
                Port = 5672,
                VirtualHost = "/",
                EnableSsl = false,
                Username = "guest",
                Password = "guest",
                PoolReconnectBaseDelayMs = 50,
                PoolReconnectMaxDelayMs = 50,
            };
            return new RabbitMqService(
                factory,
                Options.Create(options),
                new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTPD.StorageService:cache01.usenet.ninja"),
                NullLogger<RabbitMqService>.Instance);
        }

        private static void AssertLifecyclePublication(RabbitMqConfirmedPublication publication, StorageServerRuntimeOptions runtime)
        {
            Assert.Equal(CacheFleetTopology.BroadcastExchangeName, publication.Exchange);
            Assert.Equal(CacheFleetTopology.AdvertisementExpirationMilliseconds, publication.ExpirationMilliseconds);
            Assert.False(publication.Persistent);
            Assert.False(publication.Mandatory);
            Assert.Equal(runtime.Fqdn, publication.AppId);
            Assert.True(StorageServerLifecycleWireProtocol.TryParseV1(
                publication.Body.Span,
                out var announcement,
                out _,
                out var recognized));
            Assert.True(recognized);
            Assert.NotNull(announcement);
            Assert.Equal(runtime.ServerId, announcement.ServerId);
            Assert.Equal(runtime.Fqdn, announcement.Fqdn);
            Assert.Equal(runtime.BindPortTls, announcement.VatpPort);
        }

        private static bool IsLifecycle(RabbitMqConfirmedPublication publication, StorageServerLifecycleState state) =>
            LifecycleState(publication) == state;

        private static bool IsLifecycle(RabbitMqConfirmedPublication publication) =>
            StorageServerLifecycleWireProtocol.TryParseV1(
                publication.Body.Span,
                out _,
                out _,
                out var recognized)
            && recognized;

        private static StorageServerLifecycleState? LifecycleState(RabbitMqConfirmedPublication publication)
        {
            if (!StorageServerLifecycleWireProtocol.TryParseV1(
                    publication.Body.Span,
                    out var announcement,
                    out _,
                    out _))
            {
                return null;
            }

            return announcement!.State;
        }

        private static DateTimeOffset LifecycleTimestamp(RabbitMqConfirmedPublication publication)
        {
            Assert.True(StorageServerLifecycleWireProtocol.TryParseV1(
                publication.Body.Span,
                out var announcement,
                out _,
                out _));
            return announcement!.Timestamp;
        }

        private static SerilogLoggerFactory CreateLoggerFactory(ILogEventSink sink)
        {
            var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
            return new SerilogLoggerFactory(logger, dispose: true);
        }

        private static bool IsEvent(LogEvent logEvent, int eventId) =>
            logEvent.Properties.TryGetValue("EventId", out var value)
            && value.ToString().Contains(eventId.ToString(), StringComparison.Ordinal);

        private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("Condition was not met before timeout.");
                }

                await Task.Delay(10);
            }
        }
    }

    internal sealed class FixedStorageCapacityReader : IStorageCapacityReader
    {
        private readonly StorageCapacitySnapshot _snapshot;

        public FixedStorageCapacityReader(long total, long used, long available) =>
            _snapshot = new StorageCapacitySnapshot(total, used, available);

        public StorageCapacitySnapshot Read() => _snapshot;
    }
}

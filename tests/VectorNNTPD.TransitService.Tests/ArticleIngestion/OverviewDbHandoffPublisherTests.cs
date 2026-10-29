using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using VectorNNTPD.TransitService.ArticleIngestion.OverviewDb;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.RabbitMq;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.RabbitMq;
using VectorNNTPD.TransitService.Tests.TestDoubles;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.TransitService.Tests.ArticleIngestion;

/// <summary>OverviewDB async publisher-confirm and AMQP property contracts.</summary>
public sealed class OverviewDbHandoffPublisherTests
{
    [Fact]
    public async Task PublishAsync_SetsRequiredAmqpProperties_AndUsesDefaultExchange()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        await rabbit.StartAsync(CancellationToken.None);

        var nntpd = TestHostFactory.CreateValidOptions();
        nntpd.ArticleIngestion = new ArticleIngestionOptions { OverviewDbPublisherBatchSize = 10 };
        await using var publisher = new OverviewDbHandoffPublisher(
            rabbit,
            Options.Create(nntpd),
            Options.Create(RabbitMqOptionsTests.CreateValid()));

        await publisher.PublishAsync(new OverviewDbWorkItem("overview-one"u8.ToArray(), "<one@test>"), CancellationToken.None);
        await publisher.PublishAsync(new OverviewDbWorkItem("overview-two"u8.ToArray(), "<two@test>"), CancellationToken.None);

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (publisher.OutstandingCount > 0)
        {
            wait.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, wait.Token);
        }

        var connection = factory.LastConnection ?? throw new InvalidOperationException("Expected a connected fake.");
        Assert.Empty(connection.RpcChannels);
        Assert.Empty(connection.PublishChannels);
        var channel = Assert.Single(connection.AsyncConfirmPublishChannels);
        Assert.Equal(2, channel.Publications.Count);

        Assert.All(
            channel.Publications,
            publication =>
            {
                Assert.Equal(OverviewDbTopology.DefaultExchange, publication.Exchange);
                Assert.Equal(OverviewDbTopology.QueueName, publication.RoutingKey);
                Assert.Equal(nntpd.Fqdn, publication.AppId);
                Assert.Equal(OverviewDbTopology.ExpirationMilliseconds, publication.Expiration);
                Assert.True(publication.Persistent);
                Assert.True(publication.Mandatory);
                Assert.True(Guid.TryParse(publication.MessageId, out var messageId));
                Assert.NotEqual(Guid.Empty, messageId);
            });

        Assert.NotEqual(channel.Publications[0].MessageId, channel.Publications[1].MessageId);
        Assert.Equal("overview-one"u8.ToArray(), channel.Publications[0].Body);
        Assert.Equal("overview-two"u8.ToArray(), channel.Publications[1].Body);
    }

    [Fact]
    public void CreateHandoffProperties_SetsPerMessageExpirationAndSequenceHeader()
    {
        var properties = RabbitMqClientAsyncConfirmPublishChannel.CreateHandoffProperties(
            "11111111-1111-1111-1111-111111111111",
            "nntpd01.usenet.ninja",
            OverviewDbTopology.ExpirationMilliseconds,
            publishSequenceNumber: 42);

        Assert.Equal("2000", properties.Expiration);
        Assert.Equal("11111111-1111-1111-1111-111111111111", properties.MessageId);
        Assert.Equal("nntpd01.usenet.ninja", properties.AppId);
        Assert.Equal(DeliveryModes.Persistent, properties.DeliveryMode);
        Assert.True(properties.Persistent);
        Assert.NotNull(properties.Headers);
        // UInt64 is not a valid AMQP table value in RabbitMQ.Client 7.2.2.
        Assert.IsType<string>(properties.Headers[Constants.PublishSequenceNumberHeader]);
        Assert.Equal("42", properties.Headers[Constants.PublishSequenceNumberHeader]);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(42UL)]
    [InlineData(9_007_199_254_740_992UL)] // 2^53 — past float mantissa exactness
    [InlineData(ulong.MaxValue)]
    public void PublishSequenceHeader_RoundTripsLosslessThroughAmqpSupportedRepresentations(
        ulong sequence)
    {
        var formatted = RabbitMqClientAsyncConfirmPublishChannel.FormatPublishSequenceNumberHeader(
            sequence);
        Assert.Equal(sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), formatted);

        var properties = RabbitMqClientAsyncConfirmPublishChannel.CreateHandoffProperties(
            "11111111-1111-1111-1111-111111111111",
            "nntpd01.usenet.ninja",
            OverviewDbTopology.ExpirationMilliseconds,
            sequence);
        Assert.True(OverviewDbHandoffPublisher.TryReadPublishSequence(properties, out var fromString));
        Assert.Equal(sequence, fromString);

        // Broker Basic.Return typically echoes longstr ('S') as byte[].
        var asBytes = System.Text.Encoding.ASCII.GetBytes(formatted);
        Assert.True(
            OverviewDbHandoffPublisher.TryParsePublishSequenceNumberHeader(asBytes, out var fromBytes));
        Assert.Equal(sequence, fromBytes);

        // RabbitMQ.Client HandleReturn also accepts long when the value fits.
        if (sequence <= long.MaxValue)
        {
            Assert.True(
                OverviewDbHandoffPublisher.TryParsePublishSequenceNumberHeader(
                    (long)sequence,
                    out var fromLong));
            Assert.Equal(sequence, fromLong);
        }
    }

    [Fact]
    public void PublishSequenceHeader_UInt64IsNotUsedAsAmqpTableValue()
    {
        var properties = RabbitMqClientAsyncConfirmPublishChannel.CreateHandoffProperties(
            "11111111-1111-1111-1111-111111111111",
            "nntpd01.usenet.ninja",
            OverviewDbTopology.ExpirationMilliseconds,
            ulong.MaxValue);

        var header = Assert.IsType<string>(
            properties.Headers![Constants.PublishSequenceNumberHeader]);
        Assert.Equal("18446744073709551615", header);
        Assert.NotEqual(typeof(ulong), header.GetType());
    }

    [Fact]
    public async Task BasicReturn_WithStringSequenceHeader_CorrelatesCorrectWorkItem()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        await rabbit.StartAsync(CancellationToken.None);
        factory.LastConnection!.ConfigureAsyncConfirmPublishChannel = c => c.AutoAck = false;

        await using var publisher = new OverviewDbHandoffPublisher(
            rabbit,
            Options.Create(TestHostFactory.CreateValidOptions()),
            Options.Create(RabbitMqOptionsTests.CreateValid()));

        var first = new OverviewDbWorkItem("one"u8.ToArray(), "<one@test>");
        var second = new OverviewDbWorkItem("two"u8.ToArray(), "<two@test>");
        await publisher.PublishAsync(first, CancellationToken.None);
        await publisher.PublishAsync(second, CancellationToken.None);

        var channel = Assert.Single(factory.LastConnection.AsyncConfirmPublishChannels);
        // Return only sequence 2 (string header via FakeRabbitMq); ack both.
        await channel.RaiseReturnAsync(2, second.Payload);
        await channel.RaiseAckAsync(1, multiple: false);
        await channel.RaiseAckAsync(2, multiple: false);

        Assert.True(publisher.TryDequeuePublishFailure(out var failed));
        Assert.Same(second, failed);
        Assert.False(publisher.TryDequeuePublishFailure(out _));
        Assert.Equal(0, publisher.OutstandingCount);
    }

    [Fact]
    public async Task PublishAsync_DoesNotWaitForConfirm_BeforeReturning()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        await rabbit.StartAsync(CancellationToken.None);

        var connection = factory.LastConnection!;
        connection.ConfigureAsyncConfirmPublishChannel = channel =>
        {
            channel.AutoAck = true;
            channel.AutoAckDelay = TimeSpan.FromMilliseconds(50);
        };

        var nntpd = TestHostFactory.CreateValidOptions();
        nntpd.ArticleIngestion = new ArticleIngestionOptions { OverviewDbPublisherBatchSize = 100 };
        await using var publisher = new OverviewDbHandoffPublisher(
            rabbit,
            Options.Create(nntpd),
            Options.Create(RabbitMqOptionsTests.CreateValid()));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            await publisher.PublishAsync(
                new OverviewDbWorkItem([(byte)i], $"<{i}@test>"),
                CancellationToken.None);
        }

        sw.Stop();
        Assert.True(
            sw.Elapsed < TimeSpan.FromMilliseconds(200),
            $"PublishAsync serialized behind confirms: {sw.Elapsed.TotalMilliseconds:F0} ms");
        Assert.True(publisher.OutstandingCount > 0);

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (publisher.OutstandingCount > 0)
        {
            wait.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, wait.Token);
        }
    }

    [Fact]
    public async Task OutstandingWindow_BoundsUnconfirmedPublishes()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        await rabbit.StartAsync(CancellationToken.None);

        var connection = factory.LastConnection!;
        connection.ConfigureAsyncConfirmPublishChannel = channel =>
        {
            channel.AutoAck = false;
        };

        var nntpd = TestHostFactory.CreateValidOptions();
        nntpd.ArticleIngestion = new ArticleIngestionOptions { OverviewDbPublisherBatchSize = 2 };
        await using var publisher = new OverviewDbHandoffPublisher(
            rabbit,
            Options.Create(nntpd),
            Options.Create(RabbitMqOptionsTests.CreateValid()));

        await publisher.PublishAsync(new OverviewDbWorkItem("a"u8.ToArray(), "<a@test>"), CancellationToken.None);
        await publisher.PublishAsync(new OverviewDbWorkItem("b"u8.ToArray(), "<b@test>"), CancellationToken.None);
        Assert.Equal(2, publisher.OutstandingCount);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => publisher.PublishAsync(new OverviewDbWorkItem("c"u8.ToArray(), "<c@test>"), cts.Token));

        var channel = Assert.Single(connection.AsyncConfirmPublishChannels);
        await channel.RaiseAckAsync(2, multiple: true);
        Assert.Equal(0, publisher.OutstandingCount);
    }

    [Fact]
    public async Task MultipleAck_SettlesAllSequencesUpToDeliveryTag()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        await rabbit.StartAsync(CancellationToken.None);
        factory.LastConnection!.ConfigureAsyncConfirmPublishChannel = c => c.AutoAck = false;

        var nntpd = TestHostFactory.CreateValidOptions();
        nntpd.ArticleIngestion = new ArticleIngestionOptions { OverviewDbPublisherBatchSize = 10 };
        await using var publisher = new OverviewDbHandoffPublisher(
            rabbit,
            Options.Create(nntpd),
            Options.Create(RabbitMqOptionsTests.CreateValid()));

        await publisher.PublishAsync(new OverviewDbWorkItem("1"u8.ToArray(), "<1@test>"), CancellationToken.None);
        await publisher.PublishAsync(new OverviewDbWorkItem("2"u8.ToArray(), "<2@test>"), CancellationToken.None);
        await publisher.PublishAsync(new OverviewDbWorkItem("3"u8.ToArray(), "<3@test>"), CancellationToken.None);
        Assert.Equal(3, publisher.OutstandingCount);

        var channel = Assert.Single(factory.LastConnection.AsyncConfirmPublishChannels);
        await channel.RaiseAckAsync(3, multiple: true);
        Assert.Equal(0, publisher.OutstandingCount);
    }

    [Fact]
    public async Task Nack_SurfacesFailureForRequeue()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        await rabbit.StartAsync(CancellationToken.None);
        factory.LastConnection!.ConfigureAsyncConfirmPublishChannel = c => c.AutoAck = false;

        await using var publisher = new OverviewDbHandoffPublisher(
            rabbit,
            Options.Create(TestHostFactory.CreateValidOptions()),
            Options.Create(RabbitMqOptionsTests.CreateValid()));

        var item = new OverviewDbWorkItem("nack"u8.ToArray(), "<nack@test>");
        await publisher.PublishAsync(item, CancellationToken.None);
        var channel = Assert.Single(factory.LastConnection.AsyncConfirmPublishChannels);
        await channel.RaiseNackAsync(1, multiple: false);

        Assert.True(publisher.TryDequeuePublishFailure(out var failed));
        Assert.Same(item, failed);
        Assert.Equal(0, publisher.OutstandingCount);
    }

    [Fact]
    public async Task BasicReturn_ThenAck_SurfacesFailure_NotSilentSuccess()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        await rabbit.StartAsync(CancellationToken.None);
        factory.LastConnection!.ConfigureAsyncConfirmPublishChannel = c => c.AutoAck = false;

        await using var publisher = new OverviewDbHandoffPublisher(
            rabbit,
            Options.Create(TestHostFactory.CreateValidOptions()),
            Options.Create(RabbitMqOptionsTests.CreateValid()));

        var item = new OverviewDbWorkItem("ret"u8.ToArray(), "<ret@test>");
        await publisher.PublishAsync(item, CancellationToken.None);
        var channel = Assert.Single(factory.LastConnection.AsyncConfirmPublishChannels);
        await channel.RaiseReturnAsync(1, item.Payload);
        await channel.RaiseAckAsync(1, multiple: false);

        Assert.True(publisher.TryDequeuePublishFailure(out var failed));
        Assert.Same(item, failed);
        Assert.False(publisher.TryDequeuePublishFailure(out _));
    }

    [Fact]
    public async Task AbandonOutstanding_ReleasesWindow_WithoutInfiniteRetry()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        await rabbit.StartAsync(CancellationToken.None);
        factory.LastConnection!.ConfigureAsyncConfirmPublishChannel = c => c.AutoAck = false;

        var nntpd = TestHostFactory.CreateValidOptions();
        nntpd.ArticleIngestion = new ArticleIngestionOptions { OverviewDbPublisherBatchSize = 1 };
        await using var publisher = new OverviewDbHandoffPublisher(
            rabbit,
            Options.Create(nntpd),
            Options.Create(RabbitMqOptionsTests.CreateValid()));

        await publisher.PublishAsync(new OverviewDbWorkItem("x"u8.ToArray(), "<x@test>"), CancellationToken.None);
        Assert.Equal(1, publisher.OutstandingCount);
        publisher.AbandonOutstanding();
        Assert.Equal(0, publisher.OutstandingCount);

        await publisher.PublishAsync(new OverviewDbWorkItem("y"u8.ToArray(), "<y@test>"), CancellationToken.None);
        Assert.Equal(1, publisher.OutstandingCount);
    }

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
}

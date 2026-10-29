using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.ArticleIngestion.OverviewDb;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.RabbitMq;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.RabbitMq;
using VectorNNTPD.ReaderService.Tests.TestDoubles;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.ReaderService.Tests.ArticleIngestion;

/// <summary>Async outstanding-confirmation window contracts for OverviewDB publishing.</summary>
public sealed class OverviewDbPublishConcurrencyTests
{
    [Fact]
    public async Task OutstandingWindow_AllowsPipelinedPublishes_OnSingleChannel()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        await rabbit.StartAsync(CancellationToken.None);

        factory.LastConnection!.ConfigureAsyncConfirmPublishChannel = channel =>
        {
            channel.AutoAck = true;
            channel.AutoAckDelay = TimeSpan.FromMilliseconds(18);
        };

        var nntpd = TestHostFactory.CreateValidOptions();
        nntpd.ArticleIngestion = new ArticleIngestionOptions { OverviewDbPublisherBatchSize = 100 };
        await using var publisher = new OverviewDbHandoffPublisher(
            rabbit,
            Options.Create(nntpd),
            Options.Create(RabbitMqOptionsTests.CreateValid()));

        const int count = 50;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            await publisher.PublishAsync(
                new OverviewDbWorkItem([(byte)i], $"<{i}@test>"),
                CancellationToken.None);
        }

        sw.Stop();

        // Serial confirm model would need >= 50 * 18ms = 900ms. Pipelined write path must be far lower.
        Assert.True(
            sw.Elapsed < TimeSpan.FromMilliseconds(count * 18 / 2),
            $"Pipelined publish took {sw.Elapsed.TotalMilliseconds:F0} ms; expected well under serial confirm ceiling.");

        Assert.Single(factory.LastConnection.AsyncConfirmPublishChannels);
        Assert.Equal(count, factory.LastConnection.AsyncConfirmPublishChannels[0].Publications.Count);

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (publisher.OutstandingCount > 0)
        {
            wait.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, wait.Token);
        }
    }

    private static RabbitMqService CreateRabbitMqService(FakeRabbitMqConnectionFactory factory)
    {
        var options = RabbitMqOptionsTests.CreateValid();
        options.PoolReconnectBaseDelayMs = 50;
        options.PoolReconnectMaxDelayMs = 50;
        return new RabbitMqService(
            factory,
            Options.Create(options),
            new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTPD.ReaderService:nntpd01.usenet.ninja"),
            NullLogger<RabbitMqService>.Instance);
    }
}

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.ReaderService.Tests.PostFilter;

public sealed class PostFilterRejectionEvidenceQueueTests
{
    [Fact]
    public void TryEnqueue_DropsWhenFull_AndCounts()
    {
        var queue = new PostFilterRejectionEvidenceQueue(capacity: 1);
        Assert.True(queue.TryEnqueue(Evidence("closed")));
        Assert.False(queue.TryEnqueue(Evidence("denied")));
        Assert.Equal(1, queue.Count);
        Assert.Equal(1, queue.Dropped);
    }

    [Fact]
    public async Task Writer_PersistsAndCountsSuccess()
    {
        var factory = new FakeNntpDbConnectionFactory();
        await using var nntpDb = CreateDb(factory);
        await nntpDb.StartAsync(CancellationToken.None);
        var queue = new PostFilterRejectionEvidenceQueue();
        var service = new PostFilterRejectionEvidenceService(
            nntpDb,
            queue,
            NullLogger<PostFilterRejectionEvidenceService>.Instance);
        await service.StartAsync(CancellationToken.None);
        Assert.True(queue.TryEnqueue(Evidence("spam")));
        await WaitForAsync(() => queue.Written == 1);
        Assert.Equal(1, queue.Written);
        Assert.Equal("spam", Assert.Single(factory.Connections.SelectMany(static connection => connection.Rejections)).Reason);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Writer_DatabaseFailure_DoesNotThrowToProducer()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            InsertPostFilterRejectionException = new NntpDbUnavailableException("down"),
        };
        await using var nntpDb = CreateDb(factory);
        await nntpDb.StartAsync(CancellationToken.None);
        var queue = new PostFilterRejectionEvidenceQueue();
        var service = new PostFilterRejectionEvidenceService(
            nntpDb,
            queue,
            NullLogger<PostFilterRejectionEvidenceService>.Instance);
        await service.StartAsync(CancellationToken.None);
        Assert.True(queue.TryEnqueue(Evidence("closed")));
        await WaitForAsync(() => queue.WriteFailures == 1);
        Assert.Equal(0, queue.Written);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stop_DrainsQueuedEvidence()
    {
        var factory = new FakeNntpDbConnectionFactory();
        await using var nntpDb = CreateDb(factory);
        await nntpDb.StartAsync(CancellationToken.None);
        var queue = new PostFilterRejectionEvidenceQueue();
        var service = new PostFilterRejectionEvidenceService(
            nntpDb,
            queue,
            NullLogger<PostFilterRejectionEvidenceService>.Instance);
        await service.StartAsync(CancellationToken.None);
        Assert.True(queue.TryEnqueue(Evidence("closed")));
        Assert.True(queue.TryEnqueue(Evidence("denied")));
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(2, factory.Connections.Sum(static connection => connection.InsertPostFilterRejectionCount));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void DisabledQueue_DoesNotChangeDecision()
    {
        Assert.True(DisabledPostFilterRejectionEvidenceQueue.Instance.TryEnqueue(Evidence("closed")));
    }

    private static NntpDbService CreateDb(FakeNntpDbConnectionFactory factory) =>
        new(
            factory,
            Options.Create(new NntpDbOptions
            {
                ConnectionString = TestHostFactory.TestNntpDbConnectionString,
                StartupTimeout = TimeSpan.FromSeconds(15),
            }),
            NullLogger<NntpDbService>.Instance);

    private static PostFilterRejectionEvidence Evidence(string reason) =>
        new(
            DateTimeOffset.UtcNow,
            policyRevision: 2,
            accountName: "poster",
            sourceAddress: IPAddress.Loopback,
            artType: ArticleType.Default,
            messageId: "<a@example.com>",
            articleSize: 4,
            stage: PostFilterStage.Gate,
            reason: reason,
            spamAssassinStatus: null,
            spamAssassinScore: null,
            spamAssassinThreshold: null,
            articlePayload: "test"u8.ToArray());

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }
}

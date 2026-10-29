using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.Tests.Fixtures;

namespace VectorNNTPD.ReaderService.Tests.PostFilter;

public sealed class PostFilterPolicyServiceTests
{
    [Fact]
    public async Task StartAsync_LoadsNntpDbPolicy_AndPublishesSnapshot()
    {
        var repository = new InMemoryPostFilterPolicyRepository
        {
            Record = InMemoryPostFilterPolicyRepository.Create(
                new PostFilterOptions { Gate = PostFilterGateState.Closed },
                revision: 7),
        };
        await using var service = CreateService(repository);
        await service.StartAsync(CancellationToken.None);
        Assert.True(service.HasSnapshot);
        Assert.Equal(PostFilterGateState.Closed, service.Current.Gate);
        Assert.Equal(7, service.Current.Revision);
        Assert.Equal(1, repository.LoadCount);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_MissingPolicy_ThrowsAndDoesNotPublish()
    {
        var repository = new InMemoryPostFilterPolicyRepository();
        await using var service = CreateService(repository);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.Contains("nntppostfiltercurrent", ex.Message, StringComparison.Ordinal);
        Assert.False(service.HasSnapshot);
    }

    [Fact]
    public async Task StartAsync_NntpDbUnavailable_ThrowsAndDoesNotPublish()
    {
        var repository = new InMemoryPostFilterPolicyRepository
        {
            Exception = new NntpDbUnavailableException("down"),
        };
        await using var service = CreateService(repository);
        await Assert.ThrowsAsync<NntpDbUnavailableException>(() => service.StartAsync(CancellationToken.None));
        Assert.False(service.HasSnapshot);
    }

    [Fact]
    public async Task StartAsync_InvalidPolicy_ThrowsAndDoesNotPublish()
    {
        var repository = new InMemoryPostFilterPolicyRepository
        {
            Record = InMemoryPostFilterPolicyRepository.Create(
                new PostFilterOptions
                {
                    SpamAssassin = new PostFilterSpamAssassinOptions
                    {
                        Enabled = true,
                        Hosts = ["127.0.0.1"],
                    },
                }),
        };
        await using var service = CreateService(repository);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.False(service.HasSnapshot);
    }

    [Fact]
    public async Task Refresh_RevisionChange_PublishesAtomically()
    {
        var repository = new InMemoryPostFilterPolicyRepository
        {
            Record = InMemoryPostFilterPolicyRepository.Create(
                new PostFilterOptions { Gate = PostFilterGateState.Closed },
                revision: 1),
        };
        var time = new FakeTimeProvider();
        await using var service = CreateService(repository, time, TimeSpan.FromSeconds(60));
        await service.StartAsync(CancellationToken.None);
        Assert.Equal(1, service.Current.Revision);

        repository.Record = InMemoryPostFilterPolicyRepository.Create(
            new PostFilterOptions { Gate = PostFilterGateState.Active },
            revision: 2);
        time.Advance(TimeSpan.FromSeconds(60));
        await WaitUntilAsync(() => service.Current.Revision == 2);
        Assert.Equal(PostFilterGateState.Active, service.Current.Gate);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RefreshFailure_RetainsLastKnownGood()
    {
        var repository = new InMemoryPostFilterPolicyRepository
        {
            Record = InMemoryPostFilterPolicyRepository.Create(
                new PostFilterOptions { Gate = PostFilterGateState.Closed },
                revision: 1),
        };
        var time = new FakeTimeProvider();
        await using var service = CreateService(repository, time, TimeSpan.FromSeconds(60));
        await service.StartAsync(CancellationToken.None);

        repository.Exception = new NntpDbUnavailableException("down");
        time.Advance(TimeSpan.FromSeconds(60));
        await Task.Yield();
        await Task.Yield();
        Assert.Equal(1, service.Current.Revision);
        Assert.Equal(PostFilterGateState.Closed, service.Current.Gate);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Refresh_RecoversWhenNntpDbReturns()
    {
        var repository = new InMemoryPostFilterPolicyRepository
        {
            Record = InMemoryPostFilterPolicyRepository.Create(
                new PostFilterOptions { Gate = PostFilterGateState.Closed },
                revision: 1),
        };
        var time = new ControllableTimeProvider();
        await using var service = CreateService(repository, time, TimeSpan.FromSeconds(60));
        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => time.HasPendingTimer);

        repository.Exception = new NntpDbUnavailableException("down");
        time.Advance(TimeSpan.FromSeconds(60));
        await WaitUntilAsync(() => time.HasPendingTimer);
        Assert.Equal(1, service.Current.Revision);

        repository.Exception = null;
        repository.Record = InMemoryPostFilterPolicyRepository.Create(
            new PostFilterOptions { Gate = PostFilterGateState.Active },
            revision: 2);
        time.Advance(TimeSpan.FromSeconds(60));
        await WaitUntilAsync(() => service.Current.Revision == 2);
        Assert.Equal(PostFilterGateState.Active, service.Current.Gate);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ClusterConsumers_ConvergeOnSameRevision()
    {
        var repository = new InMemoryPostFilterPolicyRepository
        {
            Record = InMemoryPostFilterPolicyRepository.Create(
                new PostFilterOptions { Gate = PostFilterGateState.Disabled },
                revision: 1),
        };
        var time = new FakeTimeProvider();
        await using var first = CreateService(repository, time, TimeSpan.FromSeconds(60));
        await using var second = CreateService(repository, time, TimeSpan.FromSeconds(60));
        await first.StartAsync(CancellationToken.None);
        await second.StartAsync(CancellationToken.None);

        repository.Record = InMemoryPostFilterPolicyRepository.Create(
            new PostFilterOptions { Gate = PostFilterGateState.Closed },
            revision: 4);
        time.Advance(TimeSpan.FromSeconds(60));
        await WaitUntilAsync(() => first.Current.Revision == 4 && second.Current.Revision == 4);
        Assert.Equal(PostFilterGateState.Closed, first.Current.Gate);
        Assert.Equal(PostFilterGateState.Closed, second.Current.Gate);
        await first.StopAsync(CancellationToken.None);
        await second.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Refresh_SameRevision_DoesNotRepublish()
    {
        var repository = new InMemoryPostFilterPolicyRepository
        {
            Record = InMemoryPostFilterPolicyRepository.Create(
                new PostFilterOptions { Gate = PostFilterGateState.Disabled },
                revision: 1),
        };
        var time = new FakeTimeProvider();
        await using var service = CreateService(repository, time, TimeSpan.FromSeconds(60));
        await service.StartAsync(CancellationToken.None);
        var published = service.Current;

        repository.Record = InMemoryPostFilterPolicyRepository.Create(
            new PostFilterOptions { Gate = PostFilterGateState.Closed },
            revision: 1);
        time.Advance(TimeSpan.FromSeconds(60));
        await Task.Yield();
        await Task.Yield();
        Assert.Same(published, service.Current);
        Assert.Equal(PostFilterGateState.Disabled, service.Current.Gate);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Evaluate_DoesNotQueryRepository_AfterSnapshotPublished()
    {
        var repository = new InMemoryPostFilterPolicyRepository
        {
            Record = InMemoryPostFilterPolicyRepository.Create(new PostFilterOptions()),
        };
        await using var service = CreateService(repository);
        await service.StartAsync(CancellationToken.None);
        var loads = repository.LoadCount;

        repository.Exception = new NntpDbUnavailableException("must-not-run");
        var snapshot = service.Current;
        Assert.Equal(PostFilterGateState.Disabled, snapshot.Gate);
        Assert.Equal(loads, repository.LoadCount);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Post_UsesPublishedSnapshot_WhenRepositoryIsUnavailable()
    {
        var repository = new InMemoryPostFilterPolicyRepository
        {
            Record = InMemoryPostFilterPolicyRepository.Create(new PostFilterOptions()),
        };
        await using var service = CreateService(repository);
        await service.StartAsync(CancellationToken.None);
        repository.Exception = new NntpDbUnavailableException("must-not-run");
        var loads = repository.LoadCount;

        var quota = new PolicyRecordingQuotaStore();
        var queue = PostFilterPostHarness.NewQueue();
        await using var duplex = new PostFilterPostDuplex();
        await PostFilterPostHarness.PostAsync(
            duplex,
            duplex.CreateSession(
                queue,
                PostFilterPostHarness.CreateFilter(service.Current, quota)),
            "240 Article received OK");
        Assert.Equal(1, queue.Count);
        Assert.Equal(loads, repository.LoadCount);
        await service.StopAsync(CancellationToken.None);
    }

    private static PostFilterPolicyService CreateService(
        IPostFilterPolicyRepository repository,
        TimeProvider? time = null,
        TimeSpan? interval = null) =>
        new(
            repository,
            NullLogger<PostFilterPolicyService>.Instance,
            time ?? TimeProvider.System,
            interval ?? TimeSpan.FromHours(1));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}

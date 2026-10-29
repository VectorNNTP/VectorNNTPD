using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.ReaderService.Tests.PostFilter;

public sealed class PostFilterPolicyRepositoryTests
{
    [Fact]
    public async Task LoadAsync_MapsFactoryRecord()
    {
        var options = new PostFilterOptions
        {
            Gate = PostFilterGateState.Active,
            DeniedAccounts = ["poster"],
            Quota = new PostFilterQuotaOptions { MaxMessagesShort = 100 },
        };
        var factory = new FakeNntpDbConnectionFactory
        {
            PostFilterPolicy = InMemoryPostFilterPolicyRepository.Create(options, revision: 3),
        };
        var nntpDb = CreateStartedDb(factory);
        await nntpDb.StartAsync(CancellationToken.None);
        var repository = new MySqlPostFilterPolicyRepository(
            nntpDb,
            NullLogger<MySqlPostFilterPolicyRepository>.Instance);

        var record = await repository.LoadAsync();
        Assert.Equal(3, record.Revision);
        Assert.Equal(PostFilterGateState.Active, record.Options.Gate);
        Assert.Equal(["poster"], record.Options.DeniedAccounts);
        Assert.Equal(100, record.Options.Quota.MaxMessagesShort);
        Assert.Equal(1, factory.Connections.Sum(static connection => connection.QueryPostFilterPolicyCount));
        await nntpDb.DisposeAsync();
    }

    [Fact]
    public async Task LoadAsync_MissingCurrent_ThrowsWithoutInventingPolicy()
    {
        var factory = new FakeNntpDbConnectionFactory { PostFilterPolicy = null };
        var nntpDb = CreateStartedDb(factory);
        await nntpDb.StartAsync(CancellationToken.None);
        var repository = new MySqlPostFilterPolicyRepository(
            nntpDb,
            NullLogger<MySqlPostFilterPolicyRepository>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.LoadAsync().AsTask());
        Assert.Contains("nntppostfiltercurrent", ex.Message, StringComparison.Ordinal);
        await nntpDb.DisposeAsync();
    }

    [Fact]
    public async Task LoadAsync_Unavailable_Propagates()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            QueryPostFilterPolicyException = new NntpDbUnavailableException("down"),
        };
        var nntpDb = CreateStartedDb(factory);
        await nntpDb.StartAsync(CancellationToken.None);
        var repository = new MySqlPostFilterPolicyRepository(
            nntpDb,
            NullLogger<MySqlPostFilterPolicyRepository>.Instance);

        await Assert.ThrowsAsync<NntpDbUnavailableException>(() => repository.LoadAsync().AsTask());
        await nntpDb.DisposeAsync();
    }

    [Fact]
    public async Task LoadAsync_PublishedRevision_IgnoresOtherRevisionCollections()
    {
        var factory = new FakeNntpDbConnectionFactory();
        factory.PostFilterRevisions[11] = InMemoryPostFilterPolicyRepository.Create(
            new PostFilterOptions
            {
                Gate = PostFilterGateState.Closed,
                DeniedAccounts = ["old-poster"],
            },
            revision: 11);
        factory.PostFilterRevisions[12] = InMemoryPostFilterPolicyRepository.Create(
            new PostFilterOptions
            {
                Gate = PostFilterGateState.Active,
                DeniedAccounts = ["new-poster"],
                AllowlistedCidrs = ["10.0.0.0/8"],
                RejectArtTypes = ["Binary"],
                SpamAssassin = new PostFilterSpamAssassinOptions
                {
                    ExcludeArtTypes = ["YEncoded"],
                    Hosts = ["127.0.0.1"],
                },
            },
            revision: 12);
        factory.PostFilterCurrentRevision = 12;
        var nntpDb = CreateStartedDb(factory);
        await nntpDb.StartAsync(CancellationToken.None);
        var repository = new MySqlPostFilterPolicyRepository(
            nntpDb,
            NullLogger<MySqlPostFilterPolicyRepository>.Instance);

        var record = await repository.LoadAsync();
        Assert.Equal(12, record.Revision);
        Assert.Equal(PostFilterGateState.Active, record.Options.Gate);
        Assert.Equal(["new-poster"], record.Options.DeniedAccounts);
        Assert.DoesNotContain("old-poster", record.Options.DeniedAccounts);
        Assert.Equal(["10.0.0.0/8"], record.Options.AllowlistedCidrs);
        Assert.Equal(["Binary"], record.Options.RejectArtTypes);
        Assert.Equal(["127.0.0.1"], record.Options.SpamAssassin.Hosts);
        await nntpDb.DisposeAsync();
    }

    [Fact]
    public async Task LoadAsync_RevisionAdvance_ReturnsNewRevision()
    {
        var factory = new FakeNntpDbConnectionFactory();
        factory.PostFilterRevisions[1] = InMemoryPostFilterPolicyRepository.Create(
            new PostFilterOptions { Gate = PostFilterGateState.Disabled },
            revision: 1);
        factory.PostFilterRevisions[2] = InMemoryPostFilterPolicyRepository.Create(
            new PostFilterOptions { Gate = PostFilterGateState.Closed },
            revision: 2);
        factory.PostFilterCurrentRevision = 1;
        var nntpDb = CreateStartedDb(factory);
        await nntpDb.StartAsync(CancellationToken.None);
        var repository = new MySqlPostFilterPolicyRepository(
            nntpDb,
            NullLogger<MySqlPostFilterPolicyRepository>.Instance);

        var first = await repository.LoadAsync();
        Assert.Equal(1, first.Revision);
        factory.PostFilterCurrentRevision = 2;
        var second = await repository.LoadAsync();
        Assert.Equal(2, second.Revision);
        Assert.Equal(PostFilterGateState.Closed, second.Options.Gate);
        await nntpDb.DisposeAsync();
    }

    [Fact]
    public async Task TwoConsumers_SeeTheSamePublishedRevision()
    {
        var factory = new FakeNntpDbConnectionFactory();
        factory.PostFilterRevisions[4] = InMemoryPostFilterPolicyRepository.Create(
            new PostFilterOptions { Gate = PostFilterGateState.Closed },
            revision: 4);
        factory.PostFilterCurrentRevision = 4;
        var nntpDb = CreateStartedDb(factory);
        await nntpDb.StartAsync(CancellationToken.None);
        var first = new MySqlPostFilterPolicyRepository(
            nntpDb,
            NullLogger<MySqlPostFilterPolicyRepository>.Instance);
        var second = new MySqlPostFilterPolicyRepository(
            nntpDb,
            NullLogger<MySqlPostFilterPolicyRepository>.Instance);

        var left = await first.LoadAsync();
        var right = await second.LoadAsync();
        Assert.Equal(4, left.Revision);
        Assert.Equal(4, right.Revision);
        Assert.Equal(left.Options.Gate, right.Options.Gate);
        await nntpDb.DisposeAsync();
    }

    [Fact]
    public void Queries_BindOnePublishedRevision()
    {
        Assert.Contains("FROM nntppostfiltercurrent", NntpPostFilterQueries.SelectCurrentRevision, StringComparison.Ordinal);
        Assert.Contains("policy_id = 1", NntpPostFilterQueries.SelectCurrentRevision, StringComparison.Ordinal);
        Assert.Contains("FROM nntppostfilterpolicy", NntpPostFilterQueries.SelectPolicy, StringComparison.Ordinal);
        Assert.Contains("WHERE revision = @revision", NntpPostFilterQueries.SelectPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("policy_id = 1", NntpPostFilterQueries.SelectPolicy, StringComparison.Ordinal);
        Assert.Contains("WHERE revision = @revision", NntpPostFilterQueries.SelectAccounts, StringComparison.Ordinal);
        Assert.Contains("WHERE revision = @revision", NntpPostFilterQueries.SelectCidrs, StringComparison.Ordinal);
        Assert.Contains("WHERE revision = @revision", NntpPostFilterQueries.SelectArtTypes, StringComparison.Ordinal);
        Assert.Contains("WHERE revision = @revision", NntpPostFilterQueries.SelectHosts, StringComparison.Ordinal);
    }

    [Fact]
    public void Record_RejectsNonPositiveRevision()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PostFilterPolicyRecord(0, DateTimeOffset.UtcNow, new PostFilterOptions()));
    }

    private static NntpDbService CreateStartedDb(FakeNntpDbConnectionFactory factory) =>
        new(
            factory,
            Options.Create(new NntpDbOptions
            {
                ConnectionString = TestHostFactory.TestNntpDbConnectionString,
                StartupTimeout = TimeSpan.FromSeconds(15),
            }),
            NullLogger<NntpDbService>.Instance);
}

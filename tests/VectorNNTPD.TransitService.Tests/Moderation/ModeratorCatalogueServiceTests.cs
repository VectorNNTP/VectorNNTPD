using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.TransitService.Moderation;
using VectorNNTPD.TransitService.NntpDb;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.TestDoubles;

namespace VectorNNTPD.TransitService.Tests.Moderation;

public sealed class ModeratorCatalogueServiceTests
{
    [Fact]
    public async Task StartAsync_LoadsEnabledRows_InModeratorIdOrder()
    {
        var repository = new MemoryNntpModeratorRepository();
        repository.Add(new MemoryModeratorRecord
        {
            ModeratorId = 2,
            GroupPattern = "later.*",
            ModeratorAddress = "later@example.com",
            AccountName = "later",
        });
        repository.Add(new MemoryModeratorRecord
        {
            ModeratorId = 1,
            GroupPattern = "first.*",
            ModeratorAddress = "first@example.com",
            AccountName = "first",
        });
        repository.Add(new MemoryModeratorRecord
        {
            ModeratorId = 3,
            GroupPattern = "disabled.*",
            ModeratorAddress = "disabled@example.com",
            AccountName = "disabled",
            Enabled = false,
        });

        await using var catalogue = Create(repository);
        await catalogue.StartAsync(CancellationToken.None);

        Assert.Equal(1, repository.QueryCount);
        Assert.True(catalogue.HasSnapshot);
        Assert.Equal(2, catalogue.Current.Count);
        Assert.True(catalogue.Current.TryResolve("first.group"u8, out var first));
        Assert.Equal("first@example.com", first.Address);
        Assert.False(catalogue.Current.TryResolve("disabled.group"u8, out _));
        await catalogue.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_FailsHard_WhenRepositoryFails()
    {
        var repository = new MemoryNntpModeratorRepository
        {
            Exception = new NntpDbUnavailableException("down"),
        };
        await using var catalogue = Create(repository);
        await Assert.ThrowsAsync<NntpDbUnavailableException>(() => catalogue.StartAsync(CancellationToken.None));
        Assert.False(catalogue.HasSnapshot);
    }

    [Fact]
    public async Task Refresh_Failure_RetainsLastKnownGood()
    {
        var repository = new MemoryNntpModeratorRepository();
        repository.Add(new MemoryModeratorRecord
        {
            ModeratorId = 1,
            GroupPattern = "group.a",
            ModeratorAddress = "a@example.com",
            AccountName = "mod-a",
        });
        var clock = new ControllableTimeProvider();
        await using var catalogue = Create(repository, clock, TimeSpan.FromMinutes(5));
        await catalogue.StartAsync(CancellationToken.None);
        var original = catalogue.Current;

        repository.Exception = new NntpDbUnavailableException("down");
        clock.Advance(TimeSpan.FromMinutes(5));
        await WaitUntilAsync(() => repository.QueryCount >= 2);

        Assert.Same(original, catalogue.Current);
        Assert.True(catalogue.Current.TryResolve("group.a"u8, out _));
        await catalogue.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Refresh_Success_AtomicallyReplacesCurrent()
    {
        var repository = new MemoryNntpModeratorRepository();
        repository.Add(new MemoryModeratorRecord
        {
            ModeratorId = 1,
            GroupPattern = "old.*",
            ModeratorAddress = "old@example.com",
            AccountName = "old",
        });
        var clock = new ControllableTimeProvider();
        await using var catalogue = Create(repository, clock, TimeSpan.FromMinutes(5));
        await catalogue.StartAsync(CancellationToken.None);
        var original = catalogue.Current;

        repository.Add(new MemoryModeratorRecord
        {
            ModeratorId = 2,
            GroupPattern = "new.*",
            ModeratorAddress = "new@example.com",
            AccountName = "new",
        });
        clock.Advance(TimeSpan.FromMinutes(5));
        await WaitUntilAsync(() => !ReferenceEquals(original, catalogue.Current));

        Assert.True(catalogue.Current.TryResolve("new.group"u8, out var identity));
        Assert.Equal("new@example.com", identity.Address);
        await catalogue.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConcurrentPublish_IsSafe()
    {
        await using var catalogue = Create(new MemoryNntpModeratorRepository());
        var first = ModeratorTestSnapshot.Create(("a.*", "a@example.com", "a"));
        var second = ModeratorTestSnapshot.Create(("b.*", "b@example.com", "b"));
        Parallel.For(0, 32, i => catalogue.PublishForTests(i % 2 == 0 ? first : second));
        Assert.True(catalogue.HasSnapshot);
        Assert.True(
            catalogue.Current.TryResolve("a.group"u8, out _)
            || catalogue.Current.TryResolve("b.group"u8, out _));
    }

    [Fact]
    public void QueryConstant_SelectsEnabledRowsByModeratorId()
    {
        Assert.Contains("FROM nntpmoderators", NntpModeratorQueries.SelectEnabledModerators, StringComparison.Ordinal);
        Assert.Contains("is_enabled = 'Y'", NntpModeratorQueries.SelectEnabledModerators, StringComparison.Ordinal);
        Assert.Contains("ORDER BY moderator_id", NntpModeratorQueries.SelectEnabledModerators, StringComparison.Ordinal);
        Assert.Contains("group_pattern", NntpModeratorQueries.SelectEnabledModerators, StringComparison.Ordinal);
        Assert.Contains("moderator_address", NntpModeratorQueries.SelectEnabledModerators, StringComparison.Ordinal);
        Assert.Contains("account_name", NntpModeratorQueries.SelectEnabledModerators, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FakeNntpDb_MapsEnabledRowsAndBackendFailure()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            Moderators =
            [
                new NntpModeratorRow(1, "comp.foo.*", "moderator@example.org", "MODERATOR01"),
            ],
        };
        await using var connection = (FakeNntpDbConnection)await factory.OpenAsync("Server=test;", CancellationToken.None);
        var rows = await connection.QueryEnabledModeratorsAsync(CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.Equal(1, row.ModeratorId);
        Assert.Equal("comp.foo.*", row.GroupPattern);
        Assert.Equal("moderator@example.org", row.ModeratorAddress);
        Assert.Equal("MODERATOR01", row.AccountName);

        connection.QueryModeratorsException = new NntpDbUnavailableException("down");
        await Assert.ThrowsAsync<NntpDbUnavailableException>(
            async () => await connection.QueryEnabledModeratorsAsync(CancellationToken.None));
    }

    private static ModeratorCatalogueService Create(
        INntpModeratorRepository repository,
        TimeProvider? clock = null,
        TimeSpan? interval = null) =>
        new(
            repository,
            NullLogger<ModeratorCatalogueService>.Instance,
            clock ?? TimeProvider.System,
            interval ?? ModeratorCatalogueService.RefreshInterval);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }
}

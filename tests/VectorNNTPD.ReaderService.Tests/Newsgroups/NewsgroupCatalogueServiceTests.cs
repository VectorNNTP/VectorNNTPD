using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Newsgroups;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.ReaderService.Tests.Newsgroups;

public sealed class NewsgroupCatalogueServiceTests
{
    [Fact]
    public async Task StartAsync_LoadsCompleteSnapshot_BeforeReturning()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            Newsgroups =
            [
                new NntpGroupRow("comp.risks", "Risks", 10, 2, (byte)'m'),
                new NntpGroupRow("misc.test", "Testing", 5, 1, (byte)'y'),
            ],
        };
        var db = CreateDb(factory);
        await db.StartAsync(CancellationToken.None);
        await using var catalogue = CreateCatalogue(db);

        await catalogue.StartAsync(CancellationToken.None);

        Assert.True(catalogue.HasSnapshot);
        var snapshot = catalogue.Current;
        Assert.Equal(2, snapshot.Groups.Count);
        Assert.Equal("comp.risks", snapshot.Groups[0].GroupName);
        Assert.Equal("misc.test", snapshot.Groups[1].GroupName);
        Assert.Equal(1, factory.QueryNewsgroupsCount);
        Assert.Equal(1, Assert.Single(factory.Connections.Skip(1)).DisposeCount);
        await catalogue.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_FailsHard_WhenQueryFails()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            QueryNewsgroupsException = new NntpDbUnavailableException("down"),
        };
        var db = CreateDb(factory);
        await db.StartAsync(CancellationToken.None);
        await using var catalogue = CreateCatalogue(db);

        await Assert.ThrowsAsync<NntpDbUnavailableException>(() => catalogue.StartAsync(CancellationToken.None));
        Assert.False(catalogue.HasSnapshot);
    }

    [Fact]
    public async Task StartAsync_FailsHard_WhenRowIsMalformed()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            Newsgroups = [new NntpGroupRow("", "desc", 1, 1, (byte)'y')],
        };
        var db = CreateDb(factory);
        await db.StartAsync(CancellationToken.None);
        await using var catalogue = CreateCatalogue(db);

        await Assert.ThrowsAsync<NewsgroupCatalogueException>(() => catalogue.StartAsync(CancellationToken.None));
        Assert.False(catalogue.HasSnapshot);
    }

    [Fact]
    public async Task StartAsync_PreservesExtendedPostingStatuses()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            Newsgroups =
            [
                new NntpGroupRow("group.closed", "", 4, 1, (byte)'x'),
                new NntpGroupRow("group.junk", "", 5, 2, (byte)'j'),
            ],
        };
        var db = CreateDb(factory);
        await db.StartAsync(CancellationToken.None);
        await using var catalogue = CreateCatalogue(db);

        await catalogue.StartAsync(CancellationToken.None);

        var snapshot = catalogue.Current;
        Assert.Equal(NewsgroupPostingStatus.NoPostingOrPeerArticles, snapshot.Groups[0].PostingStatus);
        Assert.Equal(NewsgroupPostingStatus.PeerOnly, snapshot.Groups[1].PostingStatus);
        Assert.True(snapshot.Groups[0].ListActiveLine.Span.SequenceEqual("group.closed 4 1 x\r\n"u8));
        Assert.True(snapshot.Groups[1].ListActiveLine.Span.SequenceEqual("group.junk 5 2 j\r\n"u8));
        await catalogue.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_FailsHard_WhenEqualsAliasStatus()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            Newsgroups = [new NntpGroupRow("alias.group", "", 1, 1, (byte)'=')],
        };
        var db = CreateDb(factory);
        await db.StartAsync(CancellationToken.None);
        await using var catalogue = CreateCatalogue(db);

        await Assert.ThrowsAsync<NewsgroupCatalogueException>(() => catalogue.StartAsync(CancellationToken.None));
        Assert.False(catalogue.HasSnapshot);
    }

    [Fact]
    public async Task Refresh_Success_AtomicallyReplacesCurrent()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            Newsgroups = [new NntpGroupRow("old.group", "", 1, 1, (byte)'y')],
        };
        var db = CreateDb(factory);
        await db.StartAsync(CancellationToken.None);
        var clock = new ControllableTimeProvider();
        await using var catalogue = CreateCatalogue(db, clock, TimeSpan.FromMinutes(5));
        await catalogue.StartAsync(CancellationToken.None);
        var first = catalogue.Current;

        factory.Newsgroups = [new NntpGroupRow("new.group", "", 8, 3, (byte)'n')];
        clock.Advance(TimeSpan.FromMinutes(5));
        await WaitUntilAsync(() =>
        {
            var current = catalogue.Current;
            return !ReferenceEquals(current, first)
                && current.Groups.Count == 1
                && current.Groups[0].GroupName == "new.group";
        });

        var second = catalogue.Current;
        Assert.NotSame(first, second);
        Assert.Equal("old.group", Assert.Single(first.Groups).GroupName);
        Assert.Equal("new.group", Assert.Single(second.Groups).GroupName);
        await catalogue.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Refresh_Failure_RetainsPreviousSnapshot()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            Newsgroups = [new NntpGroupRow("keep.group", "", 2, 1, (byte)'y')],
        };
        var db = CreateDb(factory);
        await db.StartAsync(CancellationToken.None);
        var clock = new ControllableTimeProvider();
        await using var catalogue = CreateCatalogue(db, clock, TimeSpan.FromMinutes(5));
        await catalogue.StartAsync(CancellationToken.None);
        var first = catalogue.Current;

        factory.QueryNewsgroupsException = new NntpDbUnavailableException("down");
        clock.Advance(TimeSpan.FromMinutes(5));
        await WaitUntilAsync(() => factory.QueryNewsgroupsCount >= 2);

        Assert.Same(first, catalogue.Current);
        Assert.Equal("keep.group", Assert.Single(catalogue.Current.Groups).GroupName);
        await catalogue.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Refresh_DoesNotOverlap()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            Newsgroups = [new NntpGroupRow("g", "", 1, 1, (byte)'y')],
        };
        var db = CreateDb(factory);
        await db.StartAsync(CancellationToken.None);
        var clock = new ControllableTimeProvider();
        await using var catalogue = CreateCatalogue(db, clock, TimeSpan.FromMinutes(5));
        await catalogue.StartAsync(CancellationToken.None);
        Assert.Equal(1, factory.QueryNewsgroupsCount);

        factory.BlockQueryNewsgroups = new TaskCompletionSource();
        factory.QueryNewsgroupsStarted = new TaskCompletionSource();
        clock.Advance(TimeSpan.FromMinutes(5));
        await factory.QueryNewsgroupsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(catalogue.IsRefreshing);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, factory.QueryNewsgroupsCount);

        factory.BlockQueryNewsgroups.SetResult();
        await WaitUntilAsync(() => !catalogue.IsRefreshing);
        await catalogue.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Current_CanBeReadConcurrently_WhileRefreshPublishes()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            Newsgroups = [new NntpGroupRow("first.group", "", 1, 1, (byte)'y')],
        };
        var db = CreateDb(factory);
        await db.StartAsync(CancellationToken.None);
        var clock = new ControllableTimeProvider();
        await using var catalogue = CreateCatalogue(db, clock, TimeSpan.FromMinutes(5));
        await catalogue.StartAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var snapshot = catalogue.Current;
                _ = snapshot.ListActiveComplete.Length;
                _ = snapshot.ListNewsgroupsComplete.Length;
            }
        }, cts.Token)).ToArray();

        factory.Newsgroups = [new NntpGroupRow("second.group", "", 3, 1, (byte)'n')];
        clock.Advance(TimeSpan.FromMinutes(5));
        await WaitUntilAsync(() => factory.QueryNewsgroupsCount >= 2);
        cts.Cancel();
        try
        {
            await Task.WhenAll(readers);
        }
        catch (OperationCanceledException ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
            // Readers observe the test timeout token.
        }

        Assert.Equal("second.group", Assert.Single(catalogue.Current.Groups).GroupName);
        await catalogue.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_CancelsInFlightRefresh()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            Newsgroups = [new NntpGroupRow("g", "", 1, 1, (byte)'y')],
        };
        var db = CreateDb(factory);
        await db.StartAsync(CancellationToken.None);
        var clock = new ControllableTimeProvider();
        await using var catalogue = CreateCatalogue(db, clock, TimeSpan.FromMinutes(5));
        await catalogue.StartAsync(CancellationToken.None);

        factory.BlockQueryNewsgroups = new TaskCompletionSource();
        factory.QueryNewsgroupsStarted = new TaskCompletionSource();
        clock.Advance(TimeSpan.FromMinutes(5));
        await factory.QueryNewsgroupsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = catalogue.StopAsync(CancellationToken.None);
        factory.BlockQueryNewsgroups.SetCanceled();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static NntpDbService CreateDb(FakeNntpDbConnectionFactory factory)
    {
        return new NntpDbService(
            factory,
            Options.Create(new NntpDbOptions
            {
                ConnectionString = TestHostFactory.TestNntpDbConnectionString,
                StartupTimeout = TimeSpan.FromSeconds(15),
            }),
            NullLogger<NntpDbService>.Instance);
    }

    private static NewsgroupCatalogueService CreateCatalogue(
        NntpDbService db,
        TimeProvider? clock = null,
        TimeSpan? interval = null)
    {
        return new NewsgroupCatalogueService(
            db,
            NullLogger<NewsgroupCatalogueService>.Instance,
            clock ?? TimeProvider.System,
            interval ?? NewsgroupCatalogueService.RefreshInterval);
    }

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

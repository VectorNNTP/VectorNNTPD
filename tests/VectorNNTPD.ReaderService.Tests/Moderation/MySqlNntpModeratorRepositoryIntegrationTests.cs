using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.Moderation;
using VectorNNTPD.ReaderService.Tests.Fixtures;

namespace VectorNNTPD.ReaderService.Tests.Moderation;

/// <summary>
/// Live MySQL coverage for <see cref="MySqlNntpModeratorRepository"/>.
/// Skips unless <c>VECTORNNTP_NNTPDB_INTEGRATION</c> is set. Fails when that
/// variable is set and MySQL is unreachable.
/// </summary>
public sealed class MySqlNntpModeratorRepositoryIntegrationTests
    : IClassFixture<MySqlModeratorIntegrationFixture>
{
    private readonly MySqlModeratorIntegrationFixture _fixture;

    public MySqlNntpModeratorRepositoryIntegrationTests(MySqlModeratorIntegrationFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;
    }

    [NntpDbIntegrationFact]
    public async Task LoadsEnabledModeratorsFromRealMySql()
    {
        var ours = await LoadOursAsync();

        AssertMapped(ours, _fixture.Static);
        AssertMapped(ours, _fixture.Template);
        AssertMapped(ours, _fixture.SharedFirst);
        AssertMapped(ours, _fixture.SharedSecond);
        AssertMapped(ours, _fixture.Boundary);
        Assert.Equal(5, ours.Count);
    }

    [NntpDbIntegrationFact]
    public async Task ExcludesDisabledModeratorsFromRealMySql()
    {
        var ours = await LoadOursAsync();

        Assert.DoesNotContain(ours, row => row.ModeratorId == _fixture.Disabled.ModeratorId);
        Assert.DoesNotContain(
            ours,
            row => string.Equals(row.AccountName, _fixture.Disabled.AccountName, StringComparison.Ordinal)
                && string.Equals(row.GroupPattern, _fixture.Disabled.GroupPattern, StringComparison.Ordinal));
    }

    [NntpDbIntegrationFact]
    public async Task PreservesModeratorIdOrdering()
    {
        var ours = await LoadOursAsync();

        for (var i = 1; i < ours.Count; i++)
        {
            Assert.True(
                ours[i - 1].ModeratorId < ours[i].ModeratorId,
                "Enabled nntpmoderators rows must be returned in moderator_id ASC.");
        }

        var shared = ours
            .Where(row => string.Equals(row.GroupPattern, _fixture.SharedPattern, StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, shared.Length);
        Assert.Equal(Math.Min(_fixture.SharedFirst.ModeratorId, _fixture.SharedSecond.ModeratorId), shared[0].ModeratorId);
        Assert.Equal(Math.Max(_fixture.SharedFirst.ModeratorId, _fixture.SharedSecond.ModeratorId), shared[1].ModeratorId);
        Assert.NotEqual(shared[0].ModeratorAddress, shared[1].ModeratorAddress);
    }

    [NntpDbIntegrationFact]
    public async Task MapsCharAccountNameCorrectly()
    {
        var ours = await LoadOursAsync();

        var shortName = Find(ours, _fixture.Static);
        Assert.Equal("TESTMOD01", shortName.AccountName);
        Assert.Equal(9, shortName.AccountName.Length);
        Assert.False(shortName.AccountName.EndsWith(' '));

        var fullWidth = Find(ours, _fixture.Boundary);
        Assert.Equal(_fixture.LongAccountName, fullWidth.AccountName);
        Assert.Equal(32, fullWidth.AccountName.Length);
        Assert.False(fullWidth.AccountName.EndsWith(' '));
    }

    [NntpDbIntegrationFact]
    public async Task MapsTemplateModeratorAddressCorrectly()
    {
        var ours = await LoadOursAsync();
        var template = Find(ours, _fixture.Template);

        Assert.Equal("moderator-%s@example.org", template.ModeratorAddress);
        Assert.Contains("%s", template.ModeratorAddress, StringComparison.Ordinal);
        Assert.Equal("TESTMOD02", template.AccountName);
    }

    [NntpDbIntegrationFact]
    public async Task MapsBoundaryLengthsFromRealMySql()
    {
        var ours = await LoadOursAsync();
        var boundary = Find(ours, _fixture.Boundary);

        Assert.Equal(255, boundary.GroupPattern.Length);
        Assert.Equal(_fixture.Boundary.GroupPattern, boundary.GroupPattern);
        Assert.Equal(320, boundary.ModeratorAddress.Length);
        Assert.Equal(_fixture.LongModeratorAddress, boundary.ModeratorAddress);
        Assert.Equal(_fixture.LongAccountName, boundary.AccountName);
    }

    [NntpDbIntegrationFact]
    public async Task CataloguePublishesMappedRowsFromRealMySql()
    {
        RequireConfigured();
        var repository = _fixture.Repository!;
        var rows = await repository.GetEnabledAsync(CancellationToken.None);
        var ours = _fixture.Ours(rows);
        var snapshot = ModeratorSnapshot.Create(ours);

        Assert.Equal(5, snapshot.Count);
        Assert.True(snapshot.IsAuthenticatedModerator("TESTMOD01"));
        Assert.True(snapshot.IsAuthenticatedModerator("TESTMOD02"));
        Assert.True(snapshot.IsAuthenticatedModerator(_fixture.LongAccountName));
        Assert.False(snapshot.IsAuthenticatedModerator("TESTMOD03"));

        Assert.True(snapshot.TryResolve(GroupBytes(_fixture.PatternPrefix + ".static.child"), out var staticIdentity));
        Assert.Equal("moderator@example.org", staticIdentity.Address);
        Assert.Equal("TESTMOD01", staticIdentity.Username);

        Assert.True(snapshot.TryResolve(GroupBytes(_fixture.PatternPrefix + ".template.child"), out var templateIdentity));
        var expectedTemplate = "moderator-"
            + (_fixture.PatternPrefix + ".template.child").Replace('.', '-')
            + "@example.org";
        Assert.Equal(expectedTemplate, templateIdentity.Address);
        Assert.Equal("TESTMOD02", templateIdentity.Username);

        var sharedGroup = _fixture.PatternPrefix + ".shared.child";
        Assert.True(snapshot.TryResolve(GroupBytes(sharedGroup), out var sharedIdentity));
        var firstShared = _fixture.SharedFirst.ModeratorId < _fixture.SharedSecond.ModeratorId
            ? _fixture.SharedFirst
            : _fixture.SharedSecond;
        Assert.Equal(firstShared.ModeratorAddress, sharedIdentity.Address);
        Assert.Equal(firstShared.AccountName, sharedIdentity.Username);

        Assert.False(snapshot.TryResolve(GroupBytes(_fixture.PatternPrefix + ".disabled.child"), out _));

        await using var catalogue = new ModeratorCatalogueService(
            repository,
            NullLogger<ModeratorCatalogueService>.Instance,
            TimeProvider.System,
            TimeSpan.FromHours(1));
        await catalogue.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(catalogue.Current.Count >= 5);
            Assert.True(catalogue.IsAuthenticatedModerator("TESTMOD01"));
            Assert.True(catalogue.IsAuthenticatedModerator(_fixture.LongAccountName));
            Assert.False(catalogue.IsAuthenticatedModerator("TESTMOD03"));
        }
        finally
        {
            await catalogue.StopAsync(CancellationToken.None);
        }
    }

    [NntpDbIntegrationFact]
    public async Task GetEnabledAsync_HonorsCancellation()
    {
        RequireConfigured();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _fixture.Repository!.GetEnabledAsync(cts.Token).AsTask());
    }

    private async Task<IReadOnlyList<NntpModeratorRow>> LoadOursAsync()
    {
        RequireConfigured();
        var rows = await _fixture.Repository!.GetEnabledAsync(CancellationToken.None);
        return _fixture.Ours(rows);
    }

    private void RequireConfigured()
    {
        Assert.True(_fixture.IsConfigured, NntpDbIntegration.SkipReason);
        Assert.NotNull(_fixture.Repository);
    }

    private static void AssertMapped(IReadOnlyList<NntpModeratorRow> rows, SeededModeratorRow expected)
    {
        var actual = Find(rows, expected);
        Assert.Equal(expected.ModeratorId, actual.ModeratorId);
        Assert.Equal(expected.GroupPattern, actual.GroupPattern);
        Assert.Equal(expected.ModeratorAddress, actual.ModeratorAddress);
        Assert.Equal(expected.AccountName, actual.AccountName);
    }

    private static NntpModeratorRow Find(IReadOnlyList<NntpModeratorRow> rows, SeededModeratorRow expected)
    {
        var match = rows.SingleOrDefault(row => row.ModeratorId == expected.ModeratorId);
        Assert.NotNull(match);
        return match;
    }

    private static byte[] GroupBytes(string group) => Encoding.ASCII.GetBytes(group);
}

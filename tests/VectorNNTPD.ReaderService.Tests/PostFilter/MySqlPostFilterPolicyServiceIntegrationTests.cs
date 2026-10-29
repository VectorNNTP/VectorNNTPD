using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.Tests.Fixtures;

namespace VectorNNTPD.ReaderService.Tests.PostFilter;

[Collection("NntpDbPostFilter")]
public sealed class MySqlPostFilterPolicyServiceIntegrationTests
{
    private readonly MySqlPostFilterPolicyIntegrationFixture _fixture;

    public MySqlPostFilterPolicyServiceIntegrationTests(MySqlPostFilterPolicyIntegrationFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;
    }

    [NntpDbIntegrationFact]
    public async Task LiveRepository_RefreshLastKnownGoodAndRecovery()
    {
        RequireReady();
        var first = _fixture.NextRevision();
        var second = _fixture.NextRevision();
        var third = _fixture.NextRevision();
        await _fixture.InsertDisabledRevisionAsync(first);
        await _fixture.InsertArtTypeAsync(first, NntpPostFilterQueries.ListKindSaExclude, "YEncoded");
        await _fixture.PublishAsync(first);

        var time = new FakeTimeProvider();
        await using var service = new PostFilterPolicyService(
            _fixture.Repository!,
            NullLogger<PostFilterPolicyService>.Instance,
            time,
            TimeSpan.FromSeconds(60));
        await service.StartAsync(CancellationToken.None);
        Assert.Equal(first, service.Current.Revision);
        Assert.Equal(PostFilterGateState.Disabled, service.Current.Gate);

        await _fixture.InsertDisabledRevisionAsync(
            second,
            command => command.CommandText = command.CommandText.Replace(
                "'Disabled'",
                "'Active'",
                StringComparison.Ordinal));
        await _fixture.InsertArtTypeAsync(second, NntpPostFilterQueries.ListKindSaExclude, "YEncoded");
        await _fixture.PublishAsync(second);
        time.Advance(TimeSpan.FromSeconds(60));
        await WaitUntilAsync(() => service.Current.Revision == second);
        Assert.Equal(PostFilterGateState.Active, service.Current.Gate);
        var snapshotBeforeFailure = service.Current;

        await _fixture.ExecuteRawAsync("RENAME TABLE nntppostfiltercurrent TO nntppostfiltercurrent_hidden");
        try
        {
            time.Advance(TimeSpan.FromSeconds(60));
            await Task.Delay(50);
            Assert.Same(snapshotBeforeFailure, service.Current);
            Assert.Equal(second, service.Current.Revision);
            Assert.Equal(PostFilterGateState.Active, service.Current.Gate);
        }
        finally
        {
            await _fixture.ExecuteRawAsync("RENAME TABLE nntppostfiltercurrent_hidden TO nntppostfiltercurrent");
        }

        await _fixture.InsertDisabledRevisionAsync(third);
        await _fixture.InsertArtTypeAsync(third, NntpPostFilterQueries.ListKindSaExclude, "YEncoded");
        await _fixture.PublishAsync(third);
        time.Advance(TimeSpan.FromSeconds(60));
        await WaitUntilAsync(() => service.Current.Revision == third);
        Assert.Equal(PostFilterGateState.Disabled, service.Current.Gate);
        Assert.NotSame(snapshotBeforeFailure, service.Current);
        await service.StopAsync(CancellationToken.None);
    }

    private void RequireReady()
    {
        if (_fixture.IsConfigured)
        {
            return;
        }

        if (NntpDbIntegration.TryGetConnectionString() is null)
        {
            return;
        }

        Assert.Fail(_fixture.SkipReason ?? "PostFilter MySQL fixture is not configured.");
    }

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

using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.Tests.Fixtures;

namespace VectorNNTPD.ReaderService.Tests.PostFilter;

/// <summary>
/// Live MySQL variant of the production-policy matrix. Each case persists a
/// revision, loads it through <see cref="MySqlPostFilterPolicyRepository"/>,
/// publishes <see cref="PostFilterPolicyService.Current"/>, then runs the
/// existing POST harness.
/// </summary>
[Collection("NntpDbPostFilter")]
public sealed class MySqlPostFilterProductionPolicyMatrixTests
{
    private readonly MySqlPostFilterPolicyIntegrationFixture _fixture;

    public MySqlPostFilterProductionPolicyMatrixTests(MySqlPostFilterPolicyIntegrationFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;
    }

    public static TheoryData<string> Scenarios
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var scenario in PostFilterProductionPolicyMatrix.All)
            {
                data.Add(scenario.Name);
            }

            return data;
        }
    }

    [NntpDbIntegrationTheory]
    [MemberData(nameof(Scenarios))]
    public async Task ProductionMatrix_UsesMySqlRepositoryAndPolicyService(string scenario)
    {
        RequireReady();
        await using var policy = new MySqlPostFilterMatrixPolicy(_fixture);
        var loadsBefore = policy.RepositoryLoadCount;
        await PostFilterProductionPolicyMatrix.RunAsync(scenario, policy);
        Assert.True(
            policy.UsesPolicyService,
            scenario + " did not publish through PostFilterPolicyService.");
        Assert.True(
            policy.RepositoryLoadCount > loadsBefore,
            scenario + " did not load through MySqlPostFilterPolicyRepository.");
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
}

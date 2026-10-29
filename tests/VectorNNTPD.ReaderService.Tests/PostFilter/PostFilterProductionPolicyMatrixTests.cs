using VectorNNTPD.ReaderService.Configuration;

namespace VectorNNTPD.ReaderService.Tests.PostFilter;

/// <summary>
/// POST-boundary production-policy matrix for PostFilter v1.
/// Test-local snapshots only; does not change production <c>Gate=Disabled</c>.
/// </summary>
public sealed class PostFilterProductionPolicyMatrixTests
{
    private static readonly IPostFilterMatrixPolicy Policy = new InMemoryPostFilterMatrixPolicy();

    [Fact]
    public Task Scenario01_DisabledGate_KeepsExistingPostPath() =>
        PostFilterProductionPolicyMatrix.Scenario01_DisabledGate_KeepsExistingPostPath(Policy);

    [Fact]
    public Task Scenario02_ClosedGate_Returns441_WithoutReserveOrSpamAssassin() =>
        PostFilterProductionPolicyMatrix.Scenario02_ClosedGate_Returns441_WithoutReserveOrSpamAssassin(Policy);

    [Fact]
    public Task Scenario03_AccountAndCidrDeny_Returns441_WithoutReserve() =>
        PostFilterProductionPolicyMatrix.Scenario03_AccountAndCidrDeny_Returns441_WithoutReserve(Policy);

    [Fact]
    public Task Scenario04_ArtTypeDeny_Returns441_WithoutReserve() =>
        PostFilterProductionPolicyMatrix.Scenario04_ArtTypeDeny_Returns441_WithoutReserve(Policy);

    [Fact]
    public Task Scenario05_Allowlist_SkipsSpamAssassin_KeepsDenyAndQuota() =>
        PostFilterProductionPolicyMatrix.Scenario05_Allowlist_SkipsSpamAssassin_KeepsDenyAndQuota(Policy);

    [Fact]
    public Task Scenario06_MessageCeiling_SecondPostReturns441_WithoutSpamAssassin() =>
        PostFilterProductionPolicyMatrix.Scenario06_MessageCeiling_SecondPostReturns441_WithoutSpamAssassin(Policy);

    [Fact]
    public Task Scenario07_ByteCeiling_UsesArticleRecordArtSize() =>
        PostFilterProductionPolicyMatrix.Scenario07_ByteCeiling_UsesArticleRecordArtSize(Policy);

    [Fact]
    public Task Scenario08_IdenticalCeiling_IsBodyHashNotArtHash() =>
        PostFilterProductionPolicyMatrix.Scenario08_IdenticalCeiling_IsBodyHashNotArtHash(Policy);

    [Fact]
    public Task Scenario09_AccountIsolation_SameBodyDoesNotShareQuota() =>
        PostFilterProductionPolicyMatrix.Scenario09_AccountIsolation_SameBodyDoesNotShareQuota(Policy);

    [Fact]
    public Task Scenario10_CrossNode_SameAccountSharesInMemoryQuota() =>
        PostFilterProductionPolicyMatrix.Scenario10_CrossNode_SameAccountSharesInMemoryQuota(Policy);

    [Fact]
    public Task Scenario11_EligibleHam_CallsSpamAssassin_CommitsAndReturns240() =>
        PostFilterProductionPolicyMatrix.Scenario11_EligibleHam_CallsSpamAssassin_CommitsAndReturns240(Policy);

    [Fact]
    public Task Scenario12_SpamResult_Returns441_ReleasesAndDoesNotRemember() =>
        PostFilterProductionPolicyMatrix.Scenario12_SpamResult_Returns441_ReleasesAndDoesNotRemember(Policy);

    [Fact]
    public Task Scenario13_ArticleTooLargeForSpamAssassin_IsEligibilitySkip() =>
        PostFilterProductionPolicyMatrix.Scenario13_ArticleTooLargeForSpamAssassin_IsEligibilitySkip(Policy);

    [Fact]
    public Task Scenario14_ExcludedArtType_SkipsSpamAssassin() =>
        PostFilterProductionPolicyMatrix.Scenario14_ExcludedArtType_SkipsSpamAssassin(Policy);

    [Fact]
    public Task Scenario15_AllowlistedAccount_BypassesSpamAssassinOnly() =>
        PostFilterProductionPolicyMatrix.Scenario15_AllowlistedAccount_BypassesSpamAssassinOnly(Policy);

    [Theory]
    [InlineData(PostFilterSpamOnFailure.Reject, "441 Posting failed", false)]
    [InlineData(PostFilterSpamOnFailure.Accept, "240 Article received OK", true)]
    public Task Scenario16And17_SpamAssassinOnFailure(
        PostFilterSpamOnFailure onFailure,
        string expected,
        bool enqueued) =>
        PostFilterProductionPolicyMatrix.Scenario16And17_SpamAssassinOnFailure(
            Policy,
            onFailure,
            expected,
            enqueued);

    [Fact]
    public Task Scenario18_RedisUnavailableAtReserve_FailsClosedWithoutSpamAssassin() =>
        PostFilterProductionPolicyMatrix.Scenario18_RedisUnavailableAtReserve_FailsClosedWithoutSpamAssassin(Policy);

    [Fact]
    public Task Scenario19_CommitUnavailableAfterAdmit_KeepsQueueAnd240_WithoutRelease() =>
        PostFilterProductionPolicyMatrix.Scenario19_CommitUnavailableAfterAdmit_KeepsQueueAnd240_WithoutRelease(Policy);

    [Fact]
    public Task Scenario20_TryAdmitFailure_ReleasesReservation() =>
        PostFilterProductionPolicyMatrix.Scenario20_TryAdmitFailure_ReleasesReservation(Policy);

    [Fact]
    public Task Scenario21_CancellationAfterReserve_ReleasesWithNone() =>
        PostFilterProductionPolicyMatrix.Scenario21_CancellationAfterReserve_ReleasesWithNone(Policy);

    [Fact]
    public Task Scenario22_CompletePath_ReservesScansAdmitsCommitsRemembers() =>
        PostFilterProductionPolicyMatrix.Scenario22_CompletePath_ReservesScansAdmitsCommitsRemembers(Policy);

    [Fact]
    public Task Scenario23And24_ArticleRecordUnchanged_AndScanIsDisposable() =>
        PostFilterProductionPolicyMatrix.Scenario23And24_ArticleRecordUnchanged_AndScanIsDisposable(Policy);
}

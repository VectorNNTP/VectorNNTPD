using System.Text;
using VectorNNTPD.ReaderService.Newsgroups;
using VectorNNTPD.ReaderService.Session.Commands.Posting;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.Session;

public sealed class CatalogueNewsgroupPostingPolicyTests
{
    [Fact]
    public void Allowed_IsAccepted()
    {
        var evaluation = Evaluate(["misc.test"], Status("misc.test", NewsgroupPostingStatus.Allowed));
        Assert.Equal(NewsgroupCatalogStatus.Accepted, evaluation.Status);
        Assert.Null(evaluation.Failure);
    }

    [Fact]
    public void Prohibited_IsRejected()
    {
        AssertRejected(["no.post"], "posting prohibited", Status("no.post", NewsgroupPostingStatus.Prohibited));
    }

    [Fact]
    public void Moderated_IsRequiresModeration_ApprovedIsNotPolicyInput()
    {
        var policy = Policy(Status("mod.test", NewsgroupPostingStatus.Moderated));
        var evaluation = policy.Evaluate(["mod.test"]);
        Assert.Equal(NewsgroupCatalogStatus.RequiresModeration, evaluation.Status);
        Assert.Null(evaluation.Failure);
        Assert.Equal(["mod.test"], evaluation.ModeratedGroupNames);
    }

    [Fact]
    public void Closed_X_IsRejected()
    {
        AssertRejected(["closed.test"], "newsgroup closed", Status("closed.test", NewsgroupPostingStatus.NoPostingOrPeerArticles));
    }

    [Fact]
    public void PeerOnly_J_IsRejected()
    {
        AssertRejected(["peer.only"], "local posting not accepted", Status("peer.only", NewsgroupPostingStatus.PeerOnly));
    }

    [Fact]
    public void UnknownGroup_IsRejected()
    {
        AssertRejected(["unknown.group"], "unknown newsgroup", Status("misc.test", NewsgroupPostingStatus.Allowed));
    }

    [Fact]
    public void Multiple_AllAllowed_IsAccepted()
    {
        var evaluation = Evaluate(
            ["misc.test", "comp.test"],
            Status("misc.test", NewsgroupPostingStatus.Allowed),
            Status("comp.test", NewsgroupPostingStatus.Allowed));
        Assert.Equal(NewsgroupCatalogStatus.Accepted, evaluation.Status);
    }

    [Fact]
    public void Multiple_OneUnknown_IsRejected()
    {
        AssertRejected(
            ["misc.test", "unknown.group"],
            "unknown newsgroup",
            Status("misc.test", NewsgroupPostingStatus.Allowed));
    }

    [Fact]
    public void Multiple_OneProhibited_IsRejected()
    {
        AssertRejected(
            ["misc.test", "no.post"],
            "posting prohibited",
            Status("misc.test", NewsgroupPostingStatus.Allowed),
            Status("no.post", NewsgroupPostingStatus.Prohibited));
    }

    [Fact]
    public void Multiple_OneModerated_IsRequiresModeration()
    {
        var evaluation = Evaluate(
            ["misc.test", "mod.test"],
            Status("misc.test", NewsgroupPostingStatus.Allowed),
            Status("mod.test", NewsgroupPostingStatus.Moderated));
        Assert.Equal(NewsgroupCatalogStatus.RequiresModeration, evaluation.Status);
        Assert.Equal(["mod.test"], evaluation.ModeratedGroupNames);
    }

    [Fact]
    public void Multiple_TwoModerated_PreservesNewsgroupsOrder()
    {
        var evaluation = Evaluate(
            ["mod.b", "misc.test", "mod.a"],
            Status("misc.test", NewsgroupPostingStatus.Allowed),
            Status("mod.a", NewsgroupPostingStatus.Moderated),
            Status("mod.b", NewsgroupPostingStatus.Moderated));
        Assert.Equal(NewsgroupCatalogStatus.RequiresModeration, evaluation.Status);
        Assert.Equal(["mod.b", "mod.a"], evaluation.ModeratedGroupNames);
    }

    [Fact]
    public void Multiple_OneClosed_IsRejected()
    {
        AssertRejected(
            ["misc.test", "closed.test"],
            "newsgroup closed",
            Status("misc.test", NewsgroupPostingStatus.Allowed),
            Status("closed.test", NewsgroupPostingStatus.NoPostingOrPeerArticles));
    }

    [Fact]
    public void Multiple_OnePeerOnly_IsRejected()
    {
        AssertRejected(
            ["misc.test", "peer.only"],
            "local posting not accepted",
            Status("misc.test", NewsgroupPostingStatus.Allowed),
            Status("peer.only", NewsgroupPostingStatus.PeerOnly));
    }

    [Fact]
    public void MixedCase_MatchesSnapshotIgnoreCase()
    {
        var evaluation = Evaluate(["MISC.TEST"], Status("misc.test", NewsgroupPostingStatus.Allowed));
        Assert.Equal(NewsgroupCatalogStatus.Accepted, evaluation.Status);
    }

    [Fact]
    public void CaseVariantDuplicateTokens_AreBothLookedUpOnOneSnapshot()
    {
        var catalogue = new StaticNewsgroupCatalogue(Snapshot(Status("misc.test", NewsgroupPostingStatus.Allowed)));
        var policy = new CatalogueNewsgroupPostingPolicy(catalogue);
        var evaluation = policy.Evaluate(["misc.test", "Misc.Test"]);
        Assert.Equal(NewsgroupCatalogStatus.Accepted, evaluation.Status);
        Assert.Equal(1, catalogue.CurrentReadCount);
    }

    [Fact]
    public void Evaluate_CapturesCurrentOnce()
    {
        var first = Snapshot(
            Status("misc.test", NewsgroupPostingStatus.Allowed),
            Status("comp.test", NewsgroupPostingStatus.Allowed));
        var catalogue = new PublishOnReadCatalogue(first, NewsgroupSnapshot.Empty);
        var policy = new CatalogueNewsgroupPostingPolicy(catalogue);
        var evaluation = policy.Evaluate(["misc.test", "comp.test"]);
        Assert.Equal(NewsgroupCatalogStatus.Accepted, evaluation.Status);
        Assert.Equal(1, catalogue.Reads);
    }

    [Fact]
    public void EmptyGroupList_IsRejected()
    {
        var evaluation = Policy(Status("misc.test", NewsgroupPostingStatus.Allowed))
            .Evaluate([]);
        Assert.Equal(NewsgroupCatalogStatus.Rejected, evaluation.Status);
        Assert.Equal(PostingFailureCategory.InvalidNewsgroups, evaluation.Failure!.Value.Category);
    }

    [Fact]
    public void TryValidate_MalformedNewsgroups_DoesNotConsultCatalogue()
    {
        var catalogue = new StaticNewsgroupCatalogue(Snapshot(Status("misc.test", NewsgroupPostingStatus.Allowed)));
        var policy = new CatalogueNewsgroupPostingPolicy(catalogue);
        var parsed = Parse(Build("misc.test,,comp.test"));
        Assert.False(PostArticleValidator.TryValidate(parsed, Now, policy, out var failure));
        Assert.Equal(PostingFailureCategory.InvalidNewsgroups, failure.Category);
        Assert.Equal(0, catalogue.CurrentReadCount);
    }

    [Fact]
    public void TryValidate_EmptyNewsgroups_DoesNotConsultCatalogue()
    {
        var catalogue = new StaticNewsgroupCatalogue(Snapshot(Status("misc.test", NewsgroupPostingStatus.Allowed)));
        var policy = new CatalogueNewsgroupPostingPolicy(catalogue);
        var parsed = Parse(Build(""));
        Assert.False(PostArticleValidator.TryValidate(parsed, Now, policy, out var failure));
        Assert.Equal(PostingFailureCategory.InvalidNewsgroups, failure.Category);
        Assert.Equal(0, catalogue.CurrentReadCount);
    }

    [Fact]
    public void TryValidate_WhitespaceSeparatedGroups_AreAcceptedWhenBothAllowed()
    {
        var policy = Policy(
            Status("misc.test", NewsgroupPostingStatus.Allowed),
            Status("comp.test", NewsgroupPostingStatus.Allowed));
        var parsed = Parse(Build("misc.test, comp.test"));
        Assert.True(PostArticleValidator.TryValidate(parsed, Now, policy, out _));
        Assert.Equal(["misc.test", "comp.test"], parsed.Newsgroups);
    }

    [Fact]
    public void TryValidate_InvalidCharacters_AreRejectedBeforeLookup()
    {
        var catalogue = new StaticNewsgroupCatalogue(Snapshot(Status("misc.test", NewsgroupPostingStatus.Allowed)));
        var policy = new CatalogueNewsgroupPostingPolicy(catalogue);
        var parsed = Parse(Build("misc test"));
        Assert.False(PostArticleValidator.TryValidate(parsed, Now, policy, out var failure));
        Assert.Equal(PostingFailureCategory.InvalidNewsgroups, failure.Category);
        Assert.Equal(0, catalogue.CurrentReadCount);
    }

    [Fact]
    public void TryValidate_UnknownGroup_IsPolicyRejected()
    {
        var policy = Policy(Status("misc.test", NewsgroupPostingStatus.Allowed));
        var parsed = Parse(Build("unknown.group"));
        Assert.False(PostArticleValidator.TryValidate(parsed, Now, policy, out var failure));
        Assert.Equal(PostingFailureCategory.PolicyRejected, failure.Category);
        Assert.Equal("unknown newsgroup", failure.Detail);
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static void AssertRejected(string[] groups, string detail, params NewsgroupDefinition[] definitions)
    {
        var evaluation = Evaluate(groups, definitions);
        Assert.Equal(NewsgroupCatalogStatus.Rejected, evaluation.Status);
        Assert.Equal(PostingFailureCategory.PolicyRejected, evaluation.Failure!.Value.Category);
        Assert.Equal(detail, evaluation.Failure.Value.Detail);
    }

    private static NewsgroupPostingEvaluation Evaluate(string[] groups, params NewsgroupDefinition[] definitions) =>
        Policy(definitions).Evaluate(groups);

    private static CatalogueNewsgroupPostingPolicy Policy(params NewsgroupDefinition[] definitions) =>
        new(new StaticNewsgroupCatalogue(Snapshot(definitions)));

    private static NewsgroupSnapshot Snapshot(params NewsgroupDefinition[] definitions) =>
        NewsgroupSnapshot.Create(definitions);

    private static NewsgroupDefinition Status(string name, NewsgroupPostingStatus status) =>
        new(name, string.Empty, 2, 1, status);

    private static ParsedPostArticle Parse(string article)
    {
        Assert.True(
            PostHeaderParser.TryParse(Encoding.ASCII.GetBytes(article), out var parsed, out var failure),
            failure.Detail);
        Assert.NotNull(parsed);
        return parsed!;
    }

    private static string Build(string newsgroups)
    {
        return
            "Date: 25 Sep 2026 12:00:00 +0000\r\n" +
            "From: poster@example.com\r\n" +
            "Newsgroups: " + newsgroups + "\r\n" +
            "Subject: test\r\n" +
            "Message-ID: <ok@example.com>\r\n" +
            "\r\n" +
            "body\r\n";
    }

    private sealed class PublishOnReadCatalogue : INewsgroupCatalogue
    {
        private readonly StaticNewsgroupCatalogue _inner;
        private readonly NewsgroupSnapshot _replacement;

        public PublishOnReadCatalogue(NewsgroupSnapshot first, NewsgroupSnapshot replacement)
        {
            _inner = new StaticNewsgroupCatalogue(first);
            _replacement = replacement;
        }

        public int Reads => _inner.CurrentReadCount;

        public NewsgroupSnapshot Current
        {
            get
            {
                var snapshot = _inner.Current;
                _inner.Publish(_replacement);
                return snapshot;
            }
        }
    }
}

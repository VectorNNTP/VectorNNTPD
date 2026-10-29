using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles.Processing;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Session.Commands.Posting;

namespace VectorNNTPD.TransitService.Tests.ArticleIngestion;

public sealed class IngressNewsReasonsTests
{
    [Fact]
    public void ForParseFailure_MapsSourceBackedCauses()
    {
        Assert.Equal(
            IngressNewsReasons.YEncodingInvalid,
            IngressNewsReasons.ForParseFailure(NntpArticleParseFailureCode.YEncDecodingFailed));
        Assert.Equal(
            IngressNewsReasons.MessageIdInvalid,
            IngressNewsReasons.ForParseFailure(NntpArticleParseFailureCode.InvalidMessageId));
        Assert.Equal(
            IngressNewsReasons.DateInvalid,
            IngressNewsReasons.ForParseFailure(NntpArticleParseFailureCode.MissingOrInvalidDate));
        Assert.Equal(
            IngressNewsReasons.ArticleTooLarge,
            IngressNewsReasons.ForParseFailure(NntpArticleParseFailureCode.ArticleTooLarge));
    }

    [Fact]
    public void ForExistingRejectDetail_MapsIhaveAndTakethisStrings()
    {
        Assert.Equal(
            IngressNewsReasons.ArticleTooLarge,
            IngressNewsReasons.ForExistingRejectDetail("rejected too large"));
        Assert.Equal(
            IngressNewsReasons.ArticleTypeNotPermitted,
            IngressNewsReasons.ForExistingRejectDetail("rejected article type"));
        Assert.Equal(
            IngressNewsReasons.QueueCapacityExceeded,
            IngressNewsReasons.ForExistingRejectDetail("rejected exceeds queue budget"));
        Assert.Equal(
            IngressNewsReasons.YEncodingInvalid,
            IngressNewsReasons.ForExistingRejectDetail("rejected article record YEncDecodingFailed"));
        Assert.Equal(
            IngressNewsReasons.MessageIdInvalid,
            IngressNewsReasons.ForExistingRejectDetail("rejected article record InvalidMessageId"));
        Assert.Equal(
            IngressNewsReasons.DateInvalid,
            IngressNewsReasons.ForExistingRejectDetail("rejected article record MissingOrInvalidDate"));
    }

    [Fact]
    public void ForPostingFailure_KeepsPostFilterClosed_AndMapsKnownCauses()
    {
        Assert.Equal(
            "closed",
            IngressNewsReasons.ForPostingFailure(new PostingFailure(PostingFailureCategory.PolicyRejected, "closed")));
        Assert.Equal(
            IngressNewsReasons.ArticleTooLarge,
            IngressNewsReasons.ForPostingFailure(
                new PostingFailure(PostingFailureCategory.ArticleTooLarge, "max article size exceeded")));
        Assert.Equal(
            IngressNewsReasons.ArticleTypeNotPermitted,
            IngressNewsReasons.ForPostingFailure(
                new PostingFailure(PostingFailureCategory.PolicyRejected, "arttype-capability")));
        Assert.Equal(
            IngressNewsReasons.QueueCapacityExceeded,
            IngressNewsReasons.ForPostingFailure(
                new PostingFailure(PostingFailureCategory.PersistenceFailure, nameof(ArticleEnqueueResult.Rejected))));
        Assert.Equal(
            IngressNewsReasons.YEncodingInvalid,
            IngressNewsReasons.ForPostingFailure(
                new PostingFailure(
                    PostingFailureCategory.PolicyRejected,
                    NntpArticleParseFailureCode.YEncDecodingFailed.ToString())));
        Assert.Equal(
            IngressNewsReasons.NewsgroupNotCarried,
            IngressNewsReasons.ForPostingFailure(
                new PostingFailure(PostingFailureCategory.PolicyRejected, "unknown newsgroup")));
    }

    [Fact]
    public void WithGroups_JoinsAlreadySelectedNames_WithoutInventingThem()
    {
        Assert.Equal(
            "newsgroup not carried",
            IngressNewsReasons.WithGroups(IngressNewsReasons.NewsgroupNotCarried, []));
        Assert.Equal(
            "newsgroup not carried: alt.example.foo",
            IngressNewsReasons.WithGroups(IngressNewsReasons.NewsgroupNotCarried, ["alt.example.foo"]));
        Assert.Equal(
            "peer-only: alt.foo, alt.bar",
            IngressNewsReasons.WithGroups(IngressNewsReasons.PeerOnly, ["alt.foo", "alt.bar"]));
    }

    [Fact]
    public void ForArticleRecord_UsesParseCodeNotEnumName()
    {
        var created = ArticleRecordCreateResult.RejectedParse(NntpArticleParseFailureCode.YEncDecodingFailed);
        Assert.Equal(IngressNewsReasons.YEncodingInvalid, IngressNewsReasons.ForArticleRecord(in created));
        Assert.Equal(
            IngressNewsReasons.DateInvalid,
            IngressNewsReasons.ForMaterializeFailure(NntpArticleCanonicalFailureCode.MissingSelectedDateHeader));
    }
}

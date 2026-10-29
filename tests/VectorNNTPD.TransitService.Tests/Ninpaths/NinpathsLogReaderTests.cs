using System.Text;
using VectorNNTPD.TransitService.Ninpaths;

namespace VectorNNTPD.TransitService.Tests.Ninpaths;

public sealed class NinpathsLogReaderTests
{
    [Fact]
    public void EmptyFile_YieldsNoArticles()
    {
        var stats = Read("");
        Assert.Equal(0, stats.TotalArticles);
        Assert.Equal(0, stats.SiteCount);
    }

    [Fact]
    public void SingleHopPath_ChopsTheOnlyElement()
    {
        var stats = Read("Path: only\n");
        Assert.Equal(1, stats.TotalArticles);
        Assert.Equal(0, stats.SiteCount);
    }

    [Fact]
    public void MultiHopPath_RegistersAllButTheLastHop()
    {
        var stats = Read("Path: a!b!c\n");
        Assert.Equal(1, stats.TotalArticles);
        Assert.Equal(2, stats.SiteCount);
        Assert.True(stats.TryGetSentTo("a", out var a));
        Assert.True(stats.TryGetSentTo("b", out var b));
        Assert.False(stats.TryGetSentTo("c", out _));
        Assert.Equal(1, a);
        Assert.Equal(1, b);
        Assert.True(stats.TryGetRelation("a", "b", out var tally));
        Assert.Equal(1, tally);
    }

    [Fact]
    public void NotForMail_IsDroppedAsTheFinalElement()
    {
        var stats = Read("Path: news.example!peer.example!not-for-mail\n");
        Assert.Equal(1, stats.TotalArticles);
        Assert.True(stats.TryGetSentTo("news.example", out _));
        Assert.True(stats.TryGetSentTo("peer.example", out _));
        Assert.False(stats.TryGetSentTo("not-for-mail", out _));
    }

    [Fact]
    public void RepeatedSite_IncrementsSentTo_WithoutSelfRelation()
    {
        var stats = Read("Path: a!a!b\n");
        Assert.Equal(1, stats.TotalArticles);
        Assert.Equal(1, stats.SiteCount);
        Assert.True(stats.TryGetSentTo("a", out var sent));
        Assert.Equal(2, sent);
        Assert.False(stats.TryGetRelation("a", "a", out _));
        Assert.False(stats.TryGetSentTo("b", out _));
    }

    [Fact]
    public void MultipleUnrelatedPaths_AccumulateSitesAndRelations()
    {
        var stats = Read("Path: a!b!z\nPath: c!d!z\n");
        Assert.Equal(2, stats.TotalArticles);
        Assert.Equal(4, stats.SiteCount);
        Assert.True(stats.TryGetRelation("a", "b", out var ab));
        Assert.True(stats.TryGetRelation("c", "d", out var cd));
        Assert.Equal(1, ab);
        Assert.Equal(1, cd);
        Assert.False(stats.TryGetRelation("a", "c", out _));
    }

    [Fact]
    public void WhitespaceTerminatesThePath_AndDoesNotSkipLeadingSpaceAfterPrefix()
    {
        var stats = Read("Path: a!b!c extra\nPath:  skipped-as-empty!x\n");
        Assert.Equal(2, stats.TotalArticles);
        Assert.True(stats.TryGetSentTo("a", out _));
        Assert.True(stats.TryGetSentTo("b", out _));
        Assert.False(stats.TryGetSentTo("c", out _));
        Assert.False(stats.TryGetSentTo("skipped-as-empty", out _));
    }

    [Fact]
    public void PathPrefixIsCaseInsensitive()
    {
        var stats = Read("path: a!b!c\nPATH: d!e!f\n");
        Assert.Equal(2, stats.TotalArticles);
        Assert.True(stats.TryGetSentTo("a", out _));
        Assert.True(stats.TryGetSentTo("d", out _));
    }

    [Fact]
    public void EmptyPathLine_CountsAnArticleWithoutSites()
    {
        var stats = Read("Path: \n");
        Assert.Equal(1, stats.TotalArticles);
        Assert.Equal(0, stats.SiteCount);
    }

    [Fact]
    public void LineWithoutNewlineAtEof_IsBogus()
    {
        var stats = Read("Path: a!b!c");
        Assert.Equal(0, stats.TotalArticles);
        Assert.Equal(0, stats.SiteCount);
    }

    [Fact]
    public void ConsecutiveBangs_AreSkipped()
    {
        var stats = Read("Path: a!!b!z\n");
        Assert.Equal(1, stats.TotalArticles);
        Assert.True(stats.TryGetRelation("a", "b", out var tally));
        Assert.Equal(1, tally);
        Assert.False(stats.TryGetSentTo(string.Empty, out _));
    }

    [Fact]
    public void OverlongHostToken_AbortsRestOfPath_ButStillCountsTheArticle()
    {
        var longHop = new string('x', NinpathsConstants.MaxHostChars + 1);
        var stats = Read($"Path: keep!{longHop}!later!end\n");
        Assert.Equal(1, stats.TotalArticles);
        Assert.True(stats.TryGetSentTo("keep", out var sent));
        Assert.Equal(1, sent);
        Assert.False(stats.TryGetSentTo("later", out _));
        Assert.False(stats.TryGetSentTo(longHop, out _));
    }

    [Fact]
    public void MaxHostCharsToken_IsAccepted()
    {
        var hop = new string('y', NinpathsConstants.MaxHostChars);
        var stats = Read($"Path: {hop}!end\n");
        Assert.Equal(1, stats.TotalArticles);
        Assert.True(stats.TryGetSentTo(hop, out var sent));
        Assert.Equal(1, sent);
    }

    [Fact]
    public void SiteNamesAreCaseSensitive()
    {
        var stats = Read("Path: News!news!end\n");
        Assert.Equal(2, stats.SiteCount);
        Assert.True(stats.TryGetSentTo("News", out _));
        Assert.True(stats.TryGetSentTo("news", out _));
    }

    [Fact]
    public void LargeSyntheticStream_DoesNotRetainPerArticleState()
    {
        var builder = new StringBuilder(capacity: 80 * 20_000);
        for (var i = 0; i < 20_000; i++)
        {
            builder.Append("Path: site-one!site-two!not-for-mail\n");
        }

        var stats = Read(builder.ToString());
        Assert.Equal(20_000, stats.TotalArticles);
        Assert.Equal(2, stats.SiteCount);
        Assert.True(stats.TryGetSentTo("site-one", out var one));
        Assert.True(stats.TryGetSentTo("site-two", out var two));
        Assert.Equal(20_000, one);
        Assert.Equal(20_000, two);
        Assert.True(stats.TryGetRelation("site-one", "site-two", out var tally));
        Assert.Equal(20_000, tally);
    }

    [Fact]
    public void FgetsWindowWithoutWhitespace_SkipsTheNextWindowThatHasWhitespace()
    {
        var bogus = new string('z', NinpathsConstants.MaxFgetsChars);
        var stats = Read(bogus + "\nPath: a!b!c\n");
        Assert.Equal(1, stats.TotalArticles);
        Assert.True(stats.TryGetSentTo("a", out _));
        Assert.True(stats.TryGetSentTo("b", out _));
    }

    private static NinpathsStatistics Read(string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        using var stream = new MemoryStream(bytes, writable: false);
        return NinpathsLogReader.Read(stream);
    }
}

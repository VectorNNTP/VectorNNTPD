using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.Tests.Transit;

/// <summary>
/// newsfeeds(5) / INN uwildmat_poison matching matrix.
/// Source: https://www.eyrie.org/~eagle/software/inn/docs/libinn-uwildmat.html
/// and https://www.eyrie.org/~eagle/software/inn/docs/newsfeeds.html
/// </summary>
public sealed class NewsfeedsPatternTests
{
    [Theory]
    [InlineData("*", "comp.lang.c", NewsfeedsPatternDecision.Match)]
    [InlineData("comp.*", "comp.lang.c", NewsfeedsPatternDecision.Match)]
    [InlineData("comp.*", "comp.sources.unix", NewsfeedsPatternDecision.Match)]
    [InlineData("comp.*", "alt.comp", NewsfeedsPatternDecision.NoMatch)]
    [InlineData("comp.sources.*", "comp.sources.unix", NewsfeedsPatternDecision.Match)]
    [InlineData("comp.sources.*", "comp.sources", NewsfeedsPatternDecision.NoMatch)]
    [InlineData("comp.sources*", "comp.sources", NewsfeedsPatternDecision.Match)]
    [InlineData("comp.sources*", "comp.sources.unix", NewsfeedsPatternDecision.Match)]
    [InlineData("comp.sources*", "comp.sourcesfoo", NewsfeedsPatternDecision.Match)]
    [InlineData("comp.sources*", "comp.lang.c", NewsfeedsPatternDecision.NoMatch)]
    [InlineData("comp.*,!comp.sources.*", "comp.lang.c", NewsfeedsPatternDecision.Match)]
    [InlineData("comp.*,!comp.sources.*", "comp.sources.unix", NewsfeedsPatternDecision.Exclude)]
    [InlineData("alt.*,@alt.binaries.warez,misc.*", "alt.fan", NewsfeedsPatternDecision.Match)]
    [InlineData("alt.*,@alt.binaries.warez,misc.*", "alt.binaries.warez", NewsfeedsPatternDecision.Poison)]
    [InlineData("alt.*,@alt.binaries.warez,misc.*", "misc.test", NewsfeedsPatternDecision.Match)]
    [InlineData("!control", "control", NewsfeedsPatternDecision.Exclude)]
    [InlineData("!control", "control.cancel", NewsfeedsPatternDecision.NoMatch)]
    [InlineData("!control.*", "control.cancel", NewsfeedsPatternDecision.Exclude)]
    [InlineData("!control.*", "control", NewsfeedsPatternDecision.NoMatch)]
    [InlineData("news.*,!news.misc", "news.misc", NewsfeedsPatternDecision.Exclude)]
    [InlineData("news.*,!news.misc,*.misc", "news.misc", NewsfeedsPatternDecision.Match)]
    [InlineData("comp.*,news.*", "news.misc", NewsfeedsPatternDecision.Match)]
    [InlineData("exact.group", "exact.group", NewsfeedsPatternDecision.Match)]
    [InlineData("exact.group", "exact.group.extra", NewsfeedsPatternDecision.NoMatch)]
    [InlineData("a", "aa", NewsfeedsPatternDecision.NoMatch)]
    [InlineData("comp.?", "comp.x", NewsfeedsPatternDecision.Match)]
    [InlineData("comp.?", "comp.xy", NewsfeedsPatternDecision.NoMatch)]
    [InlineData("comp.[abc]", "comp.a", NewsfeedsPatternDecision.Match)]
    [InlineData("comp.[^a]", "comp.b", NewsfeedsPatternDecision.Match)]
    [InlineData("comp.[^a]", "comp.a", NewsfeedsPatternDecision.NoMatch)]
    public void Evaluate_MatchesNewsfeedsSemantics(string expression, string group, NewsfeedsPatternDecision expected)
    {
        Assert.True(NewsfeedsPattern.TryParse(expression, out var pattern, out var error), error);
        Assert.NotNull(pattern);
        Assert.Equal(expected, pattern.Evaluate(group));
        Assert.Equal(expected == NewsfeedsPatternDecision.Match, pattern.MatchesNewsgroup(group));
    }

    [Fact]
    public void EvaluateArticle_PoisonWinsOverOtherMatchedGroups()
    {
        Assert.True(NewsfeedsPattern.TryParse("alt.*,@alt.binaries.warez,misc.*", out var pattern, out _));
        Assert.Equal(
            NewsfeedsPatternDecision.Poison,
            pattern!.EvaluateArticle(["misc.test", "alt.binaries.warez"]));
        Assert.Equal(
            NewsfeedsPatternDecision.Match,
            pattern.EvaluateArticle(["misc.test", "alt.fan"]));
        Assert.Equal(
            NewsfeedsPatternDecision.NoMatch,
            pattern.EvaluateArticle(["rec.sport"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData(",")]
    [InlineData("comp.*,")]
    [InlineData(",comp.*")]
    [InlineData("!")]
    [InlineData("@")]
    [InlineData("comp.[abc")]
    [InlineData(@"comp.\")]
    public void TryParse_RejectsEmptyOrInvalidExpressions(string expression)
    {
        Assert.False(NewsfeedsPattern.TryParse(expression, out var pattern, out var error));
        Assert.Null(pattern);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryParse_AcceptsEscapedComma()
    {
        Assert.True(NewsfeedsPattern.TryParse(@"foo\,bar", out var pattern, out var error), error);
        Assert.True(pattern!.MatchesNewsgroup("foo,bar"));
        Assert.False(pattern.MatchesNewsgroup("foo"));
    }
}

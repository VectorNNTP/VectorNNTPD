using VectorNNTPD.TransitService.RabbitMq.ArticleWork;

namespace VectorNNTPD.TransitService.Tests.RabbitMq.ArticleWork;

/// <summary>
/// Regression: definitive provider <c>430</c> / <see cref="ArticleWorkOutcome.ArticleNotFound"/>
/// must not schedule another backbone attempt.
/// </summary>
public sealed class ArticleWorkAggregatePolicyTests
{
    [Fact]
    public void ArticleNotFound_is_attempt_terminal_and_does_not_try_next_backbone()
    {
        Assert.True(ArticleWorkAggregatePolicy.IsAttemptTerminal(ArticleWorkOutcome.ArticleNotFound));
        Assert.False(ArticleWorkAggregatePolicy.ShouldTryNextBackbone(ArticleWorkOutcome.ArticleNotFound));
        Assert.False(ArticleWorkAggregatePolicy.IsLookupSuccess(ArticleWorkOutcome.ArticleNotFound));
    }

    [Fact]
    public void InvalidArticle_and_InvalidRequest_may_try_next_backbone()
    {
        Assert.True(ArticleWorkAggregatePolicy.IsAttemptTerminal(ArticleWorkOutcome.InvalidArticle));
        Assert.True(ArticleWorkAggregatePolicy.ShouldTryNextBackbone(ArticleWorkOutcome.InvalidArticle));
        Assert.True(ArticleWorkAggregatePolicy.IsAttemptTerminal(ArticleWorkOutcome.InvalidRequest));
        Assert.True(ArticleWorkAggregatePolicy.ShouldTryNextBackbone(ArticleWorkOutcome.InvalidRequest));
    }

    [Fact]
    public void Success_completes_lookup_without_trying_next()
    {
        Assert.True(ArticleWorkAggregatePolicy.IsLookupSuccess(ArticleWorkOutcome.Success));
        Assert.True(ArticleWorkAggregatePolicy.IsAttemptTerminal(ArticleWorkOutcome.Success));
        Assert.False(ArticleWorkAggregatePolicy.ShouldTryNextBackbone(ArticleWorkOutcome.Success));
    }

}

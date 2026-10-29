using VectorNNTPD.Common.Messaging.Cache;
using VectorNNTPD.TransitService.RabbitMq;

namespace VectorNNTPD.TransitService.Tests.RabbitMq;

public sealed class RabbitMqTopologyNamesTests
{
    [Theory]
    [InlineData("  BackFiller.Abavia  ", "backfiller.abavia")]
    [InlineData("  cache.requests  ", "cache.requests")]
    [InlineData(" cache.requests ", "cache.requests")]
    [InlineData("Abavia", "abavia")]
    public void Normalize_TrimsAndUsesInvariantLowerCase(string input, string expected)
    {
        Assert.Equal(expected, RabbitMqTopologyNames.Normalize(input));
    }

    [Fact]
    public void CreateFanoutQuorumBinding_NormalizesExchangeQueueAndRoutingKey()
    {
        var endpoint = RabbitMqArticleRetrievalEndpoints.CreateFanoutQuorumBinding("  BackFiller.Abavia  ");
        Assert.Equal("backfiller.abavia", endpoint.ExchangeName);
        Assert.Equal("backfiller.abavia", endpoint.QueueName);
        Assert.Equal("backfiller.abavia", endpoint.RoutingKey);
        Assert.Equal(endpoint.ExchangeName, endpoint.QueueName);
        Assert.Equal(endpoint.QueueName, endpoint.RoutingKey);
        Assert.True(endpoint.QueueDurable);
        Assert.False(endpoint.QueueExclusive);
        Assert.False(endpoint.QueueAutoDelete);
        Assert.True(endpoint.QueueArguments.TryGetValue(
            RabbitMqArticleRetrievalEndpoints.QueueTypeArgumentName,
            out var queueType));
        Assert.Equal(RabbitMqArticleRetrievalEndpoints.QuorumQueueType, queueType);
        Assert.Single(endpoint.QueueArguments);
        Assert.DoesNotContain("grabbers.", endpoint.ExchangeName, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredTopology_HasNoSharedCacheRequestsQuorumQueue()
    {
        Assert.Empty(ArticleRetrievalTopology.Required);
        Assert.Equal(CacheFleetTopology.RequestsExchangeName, CacheRequestsTopology.ExchangeName);
        Assert.DoesNotContain("grabbers.", CacheRequestsTopology.ExchangeName, StringComparison.Ordinal);
        Assert.DoesNotContain("backfiller.storage", CacheRequestsTopology.ExchangeName, StringComparison.Ordinal);
    }
}

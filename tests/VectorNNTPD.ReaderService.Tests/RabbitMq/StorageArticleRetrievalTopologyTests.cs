using VectorNNTPD.Common.Messaging.Cache;
using VectorNNTPD.ReaderService.RabbitMq;

namespace VectorNNTPD.ReaderService.Tests.RabbitMq;

public sealed class StorageArticleRetrievalTopologyTests
{
    [Fact]
    public void CacheRequests_IsFanoutExchangeOnly_NotSharedQuorumQueue()
    {
        Assert.Equal("cache.requests", StorageArticleRetrievalTopology.EntityName);
        Assert.Equal("cache.requests", StorageArticleRetrievalTopology.ExchangeName);
        Assert.Equal(CacheFleetTopology.RequestsExchangeName, CacheRequestsTopology.ExchangeName);
        Assert.Equal("fanout", CacheRequestsTopology.ExchangeTypeName);
        Assert.True(CacheRequestsTopology.ExchangeDurable);
        Assert.False(CacheRequestsTopology.ExchangeAutoDelete);
        Assert.Equal("1000", CacheRequestsTopology.ExpirationMilliseconds);
        Assert.Empty(ArticleRetrievalTopology.Required);
        Assert.DoesNotContain("backfiller.storage", StorageArticleRetrievalTopology.EntityName, StringComparison.Ordinal);
    }

    [Fact]
    public void StorageEndpoint_IsNotABackFillerProvider()
    {
        Assert.Equal(12, BackfillArticleRetrievalTopology.Providers.Length);
        Assert.DoesNotContain("storage", BackfillArticleRetrievalTopology.Providers, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Storage", StorageArticleRetrievalTopology.Backbone);
        Assert.All(
            BackfillArticleRetrievalTopology.Definitions,
            static definition =>
                Assert.NotEqual(CacheFleetTopology.RequestsExchangeName, definition.ExchangeName));
    }

    [Fact]
    public void StorageServerRequestQueues_ArePerInstance_AndIndependent()
    {
        var q1 = CacheFleetTopology.BuildStorageServerRequestQueueName("cache01.usenet.ninja");
        var q2 = CacheFleetTopology.BuildStorageServerRequestQueueName("cache02.usenet.ninja");
        var q3 = CacheFleetTopology.BuildStorageServerRequestQueueName("cache03.usenet.ninja");
        Assert.Equal("cache.cache01.usenet.ninja", q1);
        Assert.Equal("cache.cache02.usenet.ninja", q2);
        Assert.Equal("cache.cache03.usenet.ninja", q3);
        Assert.NotEqual(q1, q2);
        Assert.NotEqual(q2, q3);
        Assert.NotEqual(
            CacheFleetTopology.BuildNntpdBroadcastQueueName("nntpd01.usenet.ninja"),
            q1);
    }
}

using VectorNNTPD.Common.Messaging.Cache;
using RabbitMQ.Client;

namespace VectorNNTPD.TransitService.RabbitMq
{
    /// <summary>
    /// NNTPD-owned <c>cache.requests</c> fleet article-lookup fanout exchange.
    /// </summary>
    /// <remarks>
    /// There is no shared <c>cache.requests</c> work queue. Each StorageServer binds an
    /// ephemeral <c>cache.&lt;storage-fqdn&gt;</c> queue. Per-NNTPD reply queues for lookup
    /// responses are owned by <c>StorageArticleLookupService</c>.
    /// </remarks>
    internal static class CacheRequestsTopology
    {
        /// <summary>Fanout exchange that carries "who has article X?" lookups.</summary>
        internal const string ExchangeName = CacheFleetTopology.RequestsExchangeName;

        /// <summary>Exchange type.</summary>
        internal const string ExchangeTypeName = ExchangeType.Fanout;

        /// <summary>Whether the exchange survives broker restart.</summary>
        internal const bool ExchangeDurable = true;

        /// <summary>Whether the exchange auto-deletes when unused.</summary>
        internal const bool ExchangeAutoDelete = false;

        /// <summary>Per-message AMQP expiration for lookup requests/responses.</summary>
        internal const string ExpirationMilliseconds = CacheFleetTopology.LookupExpirationMilliseconds;
    }

    /// <summary>
    /// NNTPD-owned <c>cache.broadcast</c> fanout exchange declaration constants.
    /// </summary>
    /// <remarks>
    /// Per-instance ephemeral consume queues are declared by
    /// <see cref="Storage.StorageServerFleetConsumerService"/>, not by
    /// <see cref="RabbitMqTopologyService"/>.
    /// </remarks>
    internal static class CacheBroadcastTopology
    {
        /// <summary>Fanout exchange that carries StorageServer advertisements.</summary>
        internal const string ExchangeName = CacheFleetTopology.BroadcastExchangeName;

        /// <summary>Exchange type.</summary>
        internal const string ExchangeTypeName = ExchangeType.Fanout;

        /// <summary>Whether the exchange survives broker restart.</summary>
        internal const bool ExchangeDurable = true;

        /// <summary>Whether the exchange auto-deletes when unused.</summary>
        internal const bool ExchangeAutoDelete = false;

        /// <summary>Per-message AMQP expiration for advertisements.</summary>
        internal const string ExpirationMilliseconds = CacheFleetTopology.AdvertisementExpirationMilliseconds;

        /// <summary>Builds <c>cache.{nntpdFqdn}</c> after normalization.</summary>
        internal static string BuildInstanceQueueName(string nntpdFqdn) =>
            CacheFleetTopology.BuildNntpdBroadcastQueueName(nntpdFqdn);
    }
}

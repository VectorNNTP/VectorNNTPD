using System.Text;

namespace VectorNNTPD.ReaderService.RabbitMq.ArticleWork
{
    /// <summary>AMQP transport constants for article-work RPC publications.</summary>
    internal static class ArticleWorkRpcAmqp
    {
        /// <summary>AMQP header/property name carrying the logical lookup UUID.</summary>
        internal const string RequestIdPropertyName = "RequestId";

        /// <summary>
        /// Documented BackFiller <em>response</em> AMQP <c>Expiration</c> (milliseconds).
        /// NNTPD work <em>requests</em> omit Expiration so RabbitMQ may buffer ready work while
        /// consumers are busy. A one-second request TTL silently discarded accepted lookups and
        /// conflicted with the five-second <see cref="ArticleWorkRpcTiming.LookupDeadline"/>.
        /// Leftover replies on exclusive ReplyTo still use this value on the BackFiller publish path.
        /// NNTPD does not delete or purge queues.
        /// </summary>
        internal const string ResponseExpirationMilliseconds = "1000";

        /// <summary>
        /// Obsolete alias retained only for transitional call sites that assert response TTL.
        /// Prefer <see cref="ResponseExpirationMilliseconds"/>. Not applied to request publishes.
        /// </summary>
        internal const string ExpirationMilliseconds = ResponseExpirationMilliseconds;

        /// <summary>Creates the AMQP header table that carries <see cref="RequestIdPropertyName"/>.</summary>
        internal static Dictionary<string, object?> CreateRequestIdHeaders(string requestId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [RequestIdPropertyName] = requestId,
            };
        }

        /// <summary>Reads the logical lookup UUID from AMQP headers, if present.</summary>
        internal static string? ReadRequestId(IDictionary<string, object?>? headers)
        {
            if (headers is null
                || !headers.TryGetValue(RequestIdPropertyName, out var value)
                || value is null)
            {
                return null;
            }

            return value switch
            {
                string text => text,
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                ReadOnlyMemory<byte> memory => Encoding.UTF8.GetString(memory.Span),
                _ => null,
            };
        }
    }
}

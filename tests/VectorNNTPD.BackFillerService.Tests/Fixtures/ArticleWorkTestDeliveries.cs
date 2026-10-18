using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.Common.Messaging.RabbitMq;
using System.Text;


namespace VectorNNTPD.BackFillerService.Tests.Fixtures
{
    /// <summary>Canonical Article Work request/response JSON and RabbitMQ delivery builders shared across tests.</summary>
    internal static class ArticleWorkTestDeliveries
    {
        /// <summary>Protocol-valid work request JSON aligned with parser golden tests.</summary>
        internal const string CanonicalRequestJson =
            """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews"}""";

        /// <summary>Secondary valid example with a different backbone and request id.</summary>
        internal const string ProtocolValidExampleJson =
            """{"version":1,"requestId":"d0648b54-b1b8-4717-95e1-7b31bf7fd1bd","messageId":"<abc@example.invalid>","backbone":"Eweka"}""";

        /// <summary>Known-invalid payload exercising unsupported version and empty message id.</summary>
        internal const string ProtocolInvalidExampleJson =
            """{"version":2,"requestId":"not-a-guid","messageId":"","backbone":"WrongBackbone"}""";

        /// <summary>Request id embedded in <see cref="CanonicalRequestJson"/>.</summary>
        internal const string CanonicalRequestId = "7c1cb8a0-95f9-4c13-8e53-339773e3afaa";
        /// <summary>Default AMQP correlation id on synthetic deliveries.</summary>
        internal const string CanonicalCorrelationId = "61d4b5f6-8b24-4c14-8d40-741a378abfc8";
        /// <summary>Default reply-to queue for response publication tests.</summary>
        internal const string CanonicalReplyTo = "nnrpd.rpc.responses";
        /// <summary>Message id shared with canonical article fixtures.</summary>
        internal const string CanonicalMessageId = "<12345@example.invalid>";
        /// <summary>Endpoint FQDN on success responses.</summary>
        internal const string CanonicalFqdn = "backfiller01.usenet.ninja";
        /// <summary>Listener port on success responses.</summary>
        internal const int CanonicalListenerPort = 119;
        /// <summary>BLAKE3 ArticleId hex for <see cref="CanonicalMessageId"/>.</summary>
        internal const string CanonicalArticleIdHex =
            "dcab316ba0e91c6abbad8d5759bff207932dbe9168c88954c6dd9240b4a6da14";
        /// <summary>Terminal success JSON aligned with wire-protocol examples.</summary>
        internal const string CanonicalSuccessResponseJson =
            """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews","outcome":"Success","fqdn":"backfiller01.usenet.ninja","listenerPort":119,"articleId":"dcab316ba0e91c6abbad8d5759bff207932dbe9168c88954c6dd9240b4a6da14"}""";
        /// <summary>Terminal not-found JSON aligned with wire-protocol examples.</summary>
        internal const string CanonicalNotFoundResponseJson =
            """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews","outcome":"ArticleNotFound","error":"No article with that message-id"}""";

        /// <summary>
        /// Wraps UTF-8 JSON in a synthetic manual-ack delivery with canonical routing and headers.
        /// Does not publish to a broker or invoke the consumer pipeline.
        /// </summary>
        /// <param name="json">Article Work request body.</param>
        /// <param name="correlationId">AMQP correlation id property.</param>
        /// <param name="replyTo">Reply-to queue for response routing tests.</param>
        /// <param name="contentType">Content type property; defaults to parser JSON type.</param>
        /// <param name="requestIdHeader">Optional request-id header mirroring the JSON requestId.</param>
        /// <param name="deliveryTag">AMQP delivery tag for ack/nack tests.</param>
        /// <param name="generation">Channel generation stamped on the delivery.</param>

        internal static RabbitMqManualAckDelivery Create(
            string json,
            string? correlationId = CanonicalCorrelationId,
            string? replyTo = CanonicalReplyTo,
            string? contentType = ArticleWorkRequestParser.JsonContentType,
            string? requestIdHeader = CanonicalRequestId,
            ulong deliveryTag = 7,
            long generation = 1)
        {
            return new RabbitMqManualAckDelivery(
                deliveryTag,
                Encoding.UTF8.GetBytes(json),
                correlationId,
                replyTo,
                contentType,
                requestIdHeader,
                Redelivered: false,
                RoutingKey: "backfiller.giganews",
                Exchange: "backfiller.giganews",
                ConsumerTag: "ctag-test",
                generation);
        }

        /// <summary>
        /// Synthetic delivery using <see cref="CanonicalRequestJson"/> and default correlation metadata.
        /// </summary>
        /// <param name="deliveryTag">AMQP delivery tag for ack/nack tests.</param>
        /// <param name="generation">Channel generation stamped on the delivery.</param>
        /// <param name="requestIdHeader">Optional request-id header override.</param>

        internal static RabbitMqManualAckDelivery Canonical(
            ulong deliveryTag = 7,
            long generation = 1,
            string? requestIdHeader = CanonicalRequestId) =>
            Create(CanonicalRequestJson, deliveryTag: deliveryTag, generation: generation, requestIdHeader: requestIdHeader);

        /// <summary>
        /// Polls every 10 ms until the predicate is true or the timeout elapses.
        /// Throws when the timeout token fires; does not swallow exceptions from the predicate.
        /// </summary>
        /// <param name="predicate">Condition polled until it returns true.</param>
        /// <param name="timeout">Maximum wait before cancellation.</param>

        internal static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
        {
            using var safety = new CancellationTokenSource(timeout);
            while (!predicate())
            {
                await Task.Delay(10, safety.Token).ConfigureAwait(false);
            }
        }
    }
}

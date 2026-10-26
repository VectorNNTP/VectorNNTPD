using System.Text.Encodings.Web;
using System.Text.Json;
using VectorNNTPD.Common.Messaging.Cache;
using System.Buffers;
using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.BackFillerService.ArticleWork
{
    /// <summary>
    /// Compact UTF-8 JSON contract for Article Work v1 responses.
    /// Property names are exact: version, requestId, messageId, backbone, outcome, fqdn, listenerPort, articleId, error.
    /// </summary>
    internal static class ArticleWorkResponseWireProtocol
    {
        /// <summary>Application protocol version.</summary>
        private const int CurrentVersion = 1;

        /// <summary>AMQP content type.</summary>
        internal const string JsonContentType = "application/json";

        /// <summary>AMQP header carrying the logical request UUID.</summary>
        private const string RequestIdHeaderName = "RequestId";

        /// <summary>
        /// AMQP per-message TTL in milliseconds for Article Work <em>responses</em> only.
        /// </summary>
        /// <remarks>
        /// Applied when BackFiller publishes a terminal reply to the caller's exclusive ReplyTo queue.
        /// Leftover replies should not linger if the caller has already abandoned the correlation.
        /// This is RabbitMQ message TTL on the reply path, not a processing deadline for a work
        /// delivery already handed to a consumer, and not the provider-queue ready-message TTL
        /// declared as <c>x-message-ttl</c> via <c>RabbitMQ:ArticleWorkQueueMessageTtlMilliseconds</c>.
        /// Work-request publishes must not carry this reply-path expiration; queue-level TTL is a
        /// separate broker policy argument on <c>backfiller.{backbone}</c>.
        /// </remarks>
        internal const string ExpirationMilliseconds = "1000";

        /// <summary>Serializes one validated v1 response. Does not embed article bytes.</summary>
        /// <param name="intent">
        /// Pipeline intent. Success carries <c>fqdn</c>, <c>listenerPort</c>, and <c>articleId</c>.
        /// Other publishable outcomes carry <c>error</c> and omit those success fields.
        /// </param>
        /// <returns>Compact UTF-8 JSON with the property names declared by this type.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="intent"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <paramref name="intent"/> is not a publishable v1 response.
        /// <see cref="ArticleWorkOutcome.Cancelled"/> is rejected (cancel settles by NACK-requeue without a terminal reply).
        /// Success requires a canonical FQDN from <see cref="VatpEndpointFields.IsCanonicalFqdn"/>,
        /// a port accepted by <see cref="VatpEndpointFields.IsCanonicalPort"/>, and a 64-character lowercase
        /// hexadecimal article id, and must not include <c>error</c>. Other publishable outcomes require a
        /// non-empty <c>error</c> and must not include success fields.
        /// </exception>
        internal static byte[] SerializeV1(ArticleWorkResponseIntent intent)
        {
            ArgumentNullException.ThrowIfNull(intent);
            Validate(intent);

            var writer = new ArrayBufferWriter<byte>();
            using var json = new Utf8JsonWriter(writer, new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            json.WriteStartObject();
            json.WriteNumber("version", CurrentVersion);
            if (intent.RequestId.HasValue)
            {
                json.WriteString("requestId", intent.RequestId.Value);
            }
            else
            {
                json.WriteNull("requestId");
            }

            if (intent.MessageId is null)
            {
                json.WriteNull("messageId");
            }
            else
            {
                json.WriteString("messageId", intent.MessageId);
            }

            if (intent.Backbone is null)
            {
                json.WriteNull("backbone");
            }
            else
            {
                json.WriteString("backbone", intent.Backbone);
            }

            json.WriteString("outcome", OutcomeName(intent.Outcome));
            if (intent.Outcome == ArticleWorkOutcome.Success)
            {
                json.WriteString("fqdn", intent.Fqdn);
                json.WriteNumber("listenerPort", intent.ListenerPort!.Value);
                json.WriteString("articleId", intent.ArticleIdHex);
            }
            else
            {
                json.WriteString("error", intent.Error);
            }

            json.WriteEndObject();
            json.Flush();
            return writer.WrittenSpan.ToArray();
        }

        /// <summary>Returns the protocol outcome name written to the <c>outcome</c> property.</summary>
        /// <param name="outcome">Classified outcome to encode.</param>
        /// <returns>
        /// Wire <c>outcome</c> name for a publishable terminal response.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <paramref name="outcome"/> is not a publishable terminal response.
        /// </exception>
        internal static string OutcomeName(ArticleWorkOutcome outcome) =>
            outcome switch
            {
                ArticleWorkOutcome.Success => "Success",
                ArticleWorkOutcome.ArticleNotFound => "ArticleNotFound",
                ArticleWorkOutcome.InvalidArticle => "InvalidArticle",
                ArticleWorkOutcome.InvalidRequest => "InvalidRequest",
                ArticleWorkOutcome.ProviderFailure => "ProviderFailure",
                ArticleWorkOutcome.UnexpectedFailure => "UnexpectedFailure",
                ArticleWorkOutcome.RetentionRejected => "RetentionRejected",
                _ => throw new InvalidOperationException($"Outcome '{outcome}' is not a publishable terminal response."),
            };

        /// <summary>
        /// Rejects an intent that <see cref="SerializeV1"/> must not write.
        /// </summary>
        /// <param name="intent">Intent already known to be non-null.</param>
        /// <exception cref="InvalidOperationException">Thrown when a required field is missing or a forbidden field is present.</exception>
        /// <remarks>
        /// Does not inspect AMQP <c>CorrelationId</c> or <c>ReplyTo</c>. Those are enforced by the publisher.
        /// <see cref="ArticleWorkOutcome.InvalidRequest"/> may omit request id, message id, and backbone.
        /// A present request id must not be <see cref="Guid.Empty"/>.
        /// </remarks>
        private static void Validate(ArticleWorkResponseIntent intent)
        {
            switch (intent.Outcome)
            {
                case ArticleWorkOutcome.Success:
                    RequireIdentity(intent);
                    if (!VatpEndpointFields.IsCanonicalFqdn(intent.Fqdn))
                    {
                        throw new InvalidOperationException("Success response requires a lowercase dotted DNS fqdn.");
                    }

                    if (intent.ListenerPort is not { } listenerPort || !VatpEndpointFields.IsCanonicalPort(listenerPort))
                    {
                        throw new InvalidOperationException("Success response requires a listenerPort in the range 1–65535.");
                    }

                    if (string.IsNullOrWhiteSpace(intent.ArticleIdHex)
                        || !ArticleId.TryParseLowerHex(intent.ArticleIdHex, out _))
                    {
                        throw new InvalidOperationException(
                            "Success response requires a 64-character lowercase hexadecimal articleId.");
                    }

                    if (intent.Error is not null)
                    {
                        throw new InvalidOperationException("Success response must not include error.");
                    }

                    return;

                case ArticleWorkOutcome.ArticleNotFound:
                case ArticleWorkOutcome.InvalidArticle:
                case ArticleWorkOutcome.ProviderFailure:
                case ArticleWorkOutcome.UnexpectedFailure:
                case ArticleWorkOutcome.RetentionRejected:
                    RequireIdentity(intent);
                    if (intent.Fqdn is not null || intent.ListenerPort is not null)
                    {
                        throw new InvalidOperationException("Terminal failure response must not include fqdn or listenerPort.");
                    }

                    if (intent.ArticleIdHex is not null)
                    {
                        throw new InvalidOperationException("Terminal failure response must not include articleId.");
                    }

                    if (string.IsNullOrWhiteSpace(intent.Error))
                    {
                        throw new InvalidOperationException("Terminal failure response requires a non-empty error.");
                    }

                    return;

                case ArticleWorkOutcome.InvalidRequest:
                    if (intent.RequestId is { } empty && empty == Guid.Empty)
                    {
                        throw new InvalidOperationException("InvalidRequest must not use Guid.Empty.");
                    }

                    if (intent.Fqdn is not null || intent.ListenerPort is not null)
                    {
                        throw new InvalidOperationException("InvalidRequest must not include fqdn or listenerPort.");
                    }

                    if (intent.ArticleIdHex is not null)
                    {
                        throw new InvalidOperationException("InvalidRequest must not include articleId.");
                    }

                    if (string.IsNullOrWhiteSpace(intent.Error))
                    {
                        throw new InvalidOperationException("InvalidRequest requires a non-empty error.");
                    }

                    return;

                default:
                    throw new InvalidOperationException($"Outcome '{intent.Outcome}' is not a publishable terminal response.");
            }
        }

        /// <summary>
        /// Requires a non-empty request id, message id, and backbone for outcomes other than <see cref="ArticleWorkOutcome.InvalidRequest"/>.
        /// </summary>
        /// <param name="intent">Success, article-not-found, or invalid-article intent.</param>
        /// <exception cref="InvalidOperationException">Thrown when any of those identities is missing or the request id is <see cref="Guid.Empty"/>.</exception>
        private static void RequireIdentity(ArticleWorkResponseIntent intent)
        {
            if (intent.RequestId is not { } requestId || requestId == Guid.Empty)
            {
                throw new InvalidOperationException("Terminal non-InvalidRequest response requires requestId.");
            }

            if (string.IsNullOrWhiteSpace(intent.MessageId))
            {
                throw new InvalidOperationException("Terminal non-InvalidRequest response requires messageId.");
            }

            if (string.IsNullOrWhiteSpace(intent.Backbone))
            {
                throw new InvalidOperationException("Terminal non-InvalidRequest response requires backbone.");
            }
        }
    }
}

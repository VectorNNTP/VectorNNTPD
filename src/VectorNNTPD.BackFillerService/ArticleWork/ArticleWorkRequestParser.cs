using System.Text.Json;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Articles.Validation;
using System.Text;

namespace VectorNNTPD.BackFillerService.ArticleWork
{
    /// <summary>
    /// Parses one consumed delivery into a validated Article Work request.
    /// </summary>
    /// <remarks>
    /// Property names are case-sensitive. Unknown JSON fields are ignored. Identities are
    /// never synthesized. AMQP <c>RequestId</c> is not a JSON field: when present, it must
    /// match JSON <c>requestId</c>; when absent, the protocol still accepts the delivery
    /// (required AMQP fields are only <c>CorrelationId</c> and <c>ReplyTo</c>).
    /// The body is read once with <see cref="Utf8JsonReader"/>. Duplicate top-level
    /// <c>version</c>, <c>requestId</c>, and <c>backbone</c> properties keep the last value.
    /// Duplicate <c>messageId</c> properties keep the first value. A JSON null <c>messageId</c>
    /// is treated as absent.
    /// </remarks>
    internal static class ArticleWorkRequestParser
    {
        /// <summary>Supported application protocol versions.</summary>
        private const int CurrentVersion = 1;

        /// <summary>Stack buffer for an ordinary <c>requestId</c>. Longer values use <see cref="Utf8JsonReader.GetString"/>.</summary>
        private const int RequestIdStackBytes = 128;

        /// <summary>Required AMQP content type when the property is present.</summary>
        internal const string JsonContentType = "application/json";

        /// <summary>
        /// Parses <paramref name="delivery"/> against the consuming backbone context.
        /// </summary>
        /// <param name="delivery">Consumed AMQP delivery.</param>
        /// <param name="consumingBackbone">Queue/session backbone context.</param>
        /// <param name="maxPayloadBytes">Maximum accepted application body size. Must be at least 1.</param>
        /// <returns>A valid request or an <see cref="ArticleWorkOutcome.InvalidRequest"/> failure. Does not throw for a rejected payload.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="consumingBackbone"/> is null or whitespace.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxPayloadBytes"/> is less than 1.</exception>
        internal static ArticleWorkParseResult Parse(
            in RabbitMqManualAckDelivery delivery,
            string consumingBackbone,
            int maxPayloadBytes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(consumingBackbone);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadBytes, 1);

            if (delivery.Body.Length > maxPayloadBytes)
            {
                return ArticleWorkParseResult.Invalid(
                    "RabbitMQ article-work payload exceeds WorkRequestMaxPayloadBytes.",
                    default);
            }

            if (!string.IsNullOrEmpty(delivery.ContentType)
                && !string.Equals(delivery.ContentType, JsonContentType, StringComparison.Ordinal))
            {
                return ArticleWorkParseResult.Invalid(
                    "RabbitMQ delivery ContentType must be application/json when present.",
                    default);
            }

            if (delivery.Body.IsEmpty)
            {
                return ArticleWorkParseResult.Invalid("RabbitMQ article-work payload was empty.", default);
            }

            PayloadFields fields;
            try
            {
                fields = ReadPayload(delivery.Body.Span);
            }
            catch (JsonException)
            {
                return ArticleWorkParseResult.Invalid("RabbitMQ article-work payload was not valid JSON.", default);
            }

            if (!fields.IsObject)
            {
                return ArticleWorkParseResult.Invalid("RabbitMQ article-work payload must be a JSON object.", default);
            }

            var requestId = fields.RequestId;
            var messageId = fields.MessageId;
            var rawMessageId = fields.MessageIdPropertySeen ? messageId ?? "\u0001" : null;
            var backbone = fields.Backbone;
            var identities = new ArticleWorkParsedIdentities(requestId, messageId, backbone);

            if (!fields.HasVersion)
            {
                return ArticleWorkParseResult.Invalid(
                    "RabbitMQ article-work payload is missing required integer property 'version'.",
                    identities);
            }

            if (fields.Version != CurrentVersion)
            {
                return ArticleWorkParseResult.Invalid(
                    $"RabbitMQ article-work payload uses unsupported version '{fields.Version}'.",
                    identities);
            }

            if (requestId is null)
            {
                return ArticleWorkParseResult.Invalid(
                    "RabbitMQ article-work payload contains missing or invalid 'requestId'.",
                    identities);
            }

            if (string.IsNullOrWhiteSpace(rawMessageId))
            {
                return ArticleWorkParseResult.Invalid(
                    "RabbitMQ article-work payload contains missing or invalid 'messageId'.",
                    identities with { MessageId = null });
            }

            if (messageId is null)
            {
                return ArticleWorkParseResult.Invalid(
                    "RabbitMQ article-work payload 'messageId' is not a canonical NNTP Message-ID.",
                    identities with { MessageId = null });
            }

            if (backbone is null)
            {
                return ArticleWorkParseResult.Invalid(
                    "RabbitMQ article-work payload contains missing or invalid 'backbone'.",
                    identities);
            }

            if (!string.Equals(backbone, consumingBackbone, StringComparison.OrdinalIgnoreCase))
            {
                return ArticleWorkParseResult.Invalid(
                    "RabbitMQ article-work payload backbone does not match the consuming queue backbone context.",
                    identities);
            }

            if (string.IsNullOrWhiteSpace(delivery.CorrelationId))
            {
                return ArticleWorkParseResult.Invalid(
                    "RabbitMQ delivery is missing required AMQP CorrelationId property.",
                    identities);
            }

            if (string.IsNullOrWhiteSpace(delivery.ReplyTo))
            {
                return ArticleWorkParseResult.Invalid(
                    "RabbitMQ delivery is missing required AMQP ReplyTo property.",
                    identities);
            }

            if (string.IsNullOrWhiteSpace(delivery.RequestIdHeader))
            {
                return ArticleWorkParseResult.Valid(new ArticleWorkRequest(
                    CurrentVersion,
                    requestId.Value,
                    messageId,
                    backbone));
            }

            if (!Guid.TryParse(delivery.RequestIdHeader, out var headerId)
                || headerId == Guid.Empty
                || headerId != requestId.Value)
            {
                return ArticleWorkParseResult.Invalid(
                    "RabbitMQ AMQP RequestId header does not match JSON requestId.",
                    identities);
            }

            return ArticleWorkParseResult.Valid(new ArticleWorkRequest(
                CurrentVersion,
                requestId.Value,
                messageId,
                backbone));
        }

        /// <summary>Fields taken from one complete JSON value.</summary>
        /// <param name="IsObject"><see langword="true"/> when the root value is a JSON object.</param>
        /// <param name="HasVersion"><see langword="true"/> when the last top-level <c>version</c> is an integer.</param>
        /// <param name="Version">That integer when <paramref name="HasVersion"/> is <see langword="true"/>; otherwise zero.</param>
        /// <param name="RequestId">The last top-level <c>requestId</c> when it is a non-empty GUID.</param>
        /// <param name="MessageIdPropertySeen">
        /// <see langword="true"/> when the first top-level <c>messageId</c> was present and was not JSON null.
        /// </param>
        /// <param name="MessageId">The accepted ASCII Message-ID from that first property; otherwise <see langword="null"/>.</param>
        /// <param name="Backbone">The last top-level <c>backbone</c> string when it is not blank.</param>
        private readonly record struct PayloadFields(
            bool IsObject,
            bool HasVersion,
            int Version,
            Guid? RequestId,
            bool MessageIdPropertySeen,
            string? MessageId,
            string? Backbone);

        /// <summary>Reads one JSON value. Malformed, truncated, or trailing JSON throws <see cref="JsonException"/>.</summary>
        /// <param name="json">UTF-8 payload bytes. The span is not retained.</param>
        /// <returns>Root-object fields, or a default result when the complete value is not an object.</returns>
        private static PayloadFields ReadPayload(ReadOnlySpan<byte> json)
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read())
            {
                throw new JsonException();
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                SkipValueIfContainer(ref reader);
                if (reader.Read())
                {
                    throw new JsonException();
                }

                return default;
            }

            var version = default(VersionRead);
            Guid? requestId = null;
            var messageIdLocked = false;
            var messageId = default(MessageIdRead);
            string? backbone = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                {
                    if (reader.Read())
                    {
                        throw new JsonException();
                    }

                    return new PayloadFields(
                        true,
                        version.HasVersion,
                        version.Version,
                        requestId,
                        messageId.PropertySeen,
                        messageId.MessageId,
                        backbone);
                }

                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                {
                    continue;
                }

                var versionName = reader.ValueTextEquals("version"u8);
                var requestName = reader.ValueTextEquals("requestId"u8);
                var messageName = reader.ValueTextEquals("messageId"u8);
                var backboneName = reader.ValueTextEquals("backbone"u8);
                if (!reader.Read())
                {
                    throw new JsonException();
                }

                if (versionName)
                {
                    ApplyVersion(ref reader, ref version);
                }
                else if (requestName)
                {
                    requestId = ReadRequestId(ref reader);
                }
                else if (messageName)
                {
                    if (messageIdLocked)
                    {
                        SkipValueIfContainer(ref reader);
                    }
                    else
                    {
                        messageIdLocked = true;
                        ApplyMessageId(ref reader, ref messageId);
                    }
                }
                else if (backboneName)
                {
                    backbone = ReadBackbone(ref reader);
                }
                else
                {
                    SkipValueIfContainer(ref reader);
                }
            }

            throw new JsonException();
        }

        /// <summary>Last top-level integer <c>version</c>. A later non-integer clears <see cref="HasVersion"/>.</summary>
        private struct VersionRead
        {
            /// <summary><see langword="true"/> when the current value is an integer.</summary>
            public bool HasVersion;

            /// <summary>The accepted integer, or zero when <see cref="HasVersion"/> is <see langword="false"/>.</summary>
            public int Version;
        }

        /// <summary>Applies the current top-level <c>version</c> value. A non-integer clears any earlier integer.</summary>
        /// <param name="reader">Reader positioned on the property value.</param>
        /// <param name="version">Version state updated in place.</param>
        private static void ApplyVersion(ref Utf8JsonReader reader, ref VersionRead version)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var parsed))
            {
                version.HasVersion = true;
                version.Version = parsed;
                return;
            }

            version.HasVersion = false;
            version.Version = 0;
            SkipValueIfContainer(ref reader);
        }

        /// <summary>Reads the current top-level <c>requestId</c> string as a non-empty GUID.</summary>
        /// <param name="reader">Reader positioned on the property value. The parser constructs it from one span.</param>
        /// <returns>The GUID, or <see langword="null"/> when the value is missing, blank, not a GUID, or <see cref="Guid.Empty"/>.</returns>
        /// <remarks>
        /// A reader over one span has <see cref="Utf8JsonReader.HasValueSequence"/> false, so
        /// <see cref="Utf8JsonReader.ValueSpan"/> is the complete raw value. When that flag is true,
        /// <see cref="Utf8JsonReader.ValueSpan"/> is only the first segment and <see cref="Utf8JsonReader.ValueSequence"/>
        /// holds the rest, so those values use <see cref="Utf8JsonReader.GetString"/>.
        /// JSON escapes decode to no more UTF-8 bytes than their raw form, so a raw value that fits
        /// <see cref="RequestIdStackBytes"/> is copied on the stack. A longer value uses
        /// <see cref="Guid.TryParse(string?, out Guid)"/> on the decoded string.
        /// </remarks>
        private static Guid? ReadRequestId(ref Utf8JsonReader reader)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                SkipValueIfContainer(ref reader);
                return null;
            }

            if (!reader.HasValueSequence && reader.ValueSpan.Length <= RequestIdStackBytes)
            {
                Span<byte> bytes = stackalloc byte[RequestIdStackBytes];
                int written;
                if (!reader.ValueIsEscaped)
                {
                    reader.ValueSpan.CopyTo(bytes);
                    written = reader.ValueSpan.Length;
                }
                else
                {
                    written = reader.CopyString(bytes);
                }

                Span<char> chars = stackalloc char[written];
                var charCount = Encoding.UTF8.GetChars(bytes[..written], chars);
                var text = chars[..charCount];
                if (text.IsWhiteSpace() || !Guid.TryParse(text, out var parsed) || parsed == Guid.Empty)
                {
                    return null;
                }

                return parsed;
            }

            var owned = reader.GetString();
            if (string.IsNullOrWhiteSpace(owned) || !Guid.TryParse(owned, out var guid) || guid == Guid.Empty)
            {
                return null;
            }

            return guid;
        }

        /// <summary>First top-level <c>messageId</c>. JSON null leaves <see cref="PropertySeen"/> false.</summary>
        private struct MessageIdRead
        {
            /// <summary><see langword="true"/> when the value was not JSON null.</summary>
            public bool PropertySeen;

            /// <summary>The accepted ASCII Message-ID, or <see langword="null"/> when the value is not a valid token.</summary>
            public string? MessageId;
        }

        /// <summary>
        /// Applies the first top-level <c>messageId</c> value. JSON null leaves the property unseen.
        /// </summary>
        /// <param name="reader">Reader positioned on the property value.</param>
        /// <param name="messageId">Message-ID state updated in place. The caller does not invoke this for a later duplicate.</param>
        private static void ApplyMessageId(ref Utf8JsonReader reader, ref MessageIdRead messageId)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return;
            }

            messageId.PropertySeen = true;
            if (reader.TokenType != JsonTokenType.String)
            {
                SkipValueIfContainer(ref reader);
                return;
            }

            Span<byte> buffer = stackalloc byte[NntpMessageIdValidation.MaxMessageIdLength];
            int written;
            if (!reader.ValueIsEscaped)
            {
                var value = reader.ValueSpan;
                if (value.Length > buffer.Length)
                {
                    return;
                }

                value.CopyTo(buffer);
                written = value.Length;
            }
            else
            {
                try
                {
                    written = reader.CopyString(buffer);
                }
                catch (ArgumentException)
                {
                    return;
                }
            }

            var token = buffer[..written];
            if (NntpMessageIdValidation.IsValidMessageId(token))
            {
                messageId.MessageId = Encoding.ASCII.GetString(token);
            }
        }

        /// <summary>Reads the current top-level <c>backbone</c> string.</summary>
        /// <param name="reader">Reader positioned on the property value.</param>
        /// <returns>The unescaped string when it is not blank; otherwise <see langword="null"/>.</returns>
        private static string? ReadBackbone(ref Utf8JsonReader reader)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                SkipValueIfContainer(ref reader);
                return null;
            }

            var text = reader.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        /// <summary>Consumes an object or array value. Primitive values stay on the current token.</summary>
        /// <param name="reader">Reader positioned on a property value or a non-object root.</param>
        private static void SkipValueIfContainer(ref Utf8JsonReader reader)
        {
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }
        }
    }
}

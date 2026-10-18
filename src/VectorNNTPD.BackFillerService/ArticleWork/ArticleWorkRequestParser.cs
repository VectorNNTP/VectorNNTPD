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
    /// </remarks>
    internal static class ArticleWorkRequestParser
    {
        /// <summary>Supported application protocol versions.</summary>
        private const int CurrentVersion = 1;

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

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(delivery.Body);
            }
            catch (JsonException)
            {
                return ArticleWorkParseResult.Invalid("RabbitMQ article-work payload was not valid JSON.", default);
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return ArticleWorkParseResult.Invalid("RabbitMQ article-work payload must be a JSON object.", default);
                }

                _ = TryReadGuid(root, "requestId", out var requestId);
                var messageIdRead = TryReadValidatedMessageId(delivery.Body.Span);
                var messageId = messageIdRead.MessageId;
                var rawMessageId = messageIdRead.PropertySeen ? messageId ?? "\u0001" : null;
                _ = TryReadString(root, "backbone", out var rawBackbone);
                var backbone = !string.IsNullOrWhiteSpace(rawBackbone) ? rawBackbone : null;
                var identities = new ArticleWorkParsedIdentities(requestId, messageId, backbone);

                if (!TryReadInt32(root, "version", out var version))
                {
                    return ArticleWorkParseResult.Invalid(
                        "RabbitMQ article-work payload is missing required integer property 'version'.",
                        identities);
                }

                if (version != CurrentVersion)
                {
                    return ArticleWorkParseResult.Invalid(
                        $"RabbitMQ article-work payload uses unsupported version '{version}'.",
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
                    return ArticleWorkParseResult.Valid(new ArticleWorkRequest(
                        CurrentVersion,
                        requestId.Value,
                        messageId,
                        backbone));
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
        }

        /// <summary>
        /// Validates the top-level <c>messageId</c> JSON string from the original payload bytes.
        /// </summary>
        /// <param name="json">Article-work request body.</param>
        /// <returns>The accepted ASCII Message-ID when the property is a valid token.</returns>
        private static MessageIdRead TryReadValidatedMessageId(ReadOnlySpan<byte> json)
        {
            Span<byte> buffer = stackalloc byte[NntpMessageIdValidation.MaxMessageIdLength];
            switch (TryCopyTopLevelJsonString(json, "messageId"u8, buffer, out var length))
            {
                case TopLevelJsonString.Copied:
                    var token = buffer[..length];
                    return NntpMessageIdValidation.IsValidMessageId(token)
                        ? new MessageIdRead(true, Encoding.ASCII.GetString(token))
                        : new MessageIdRead(true, null);
                case TopLevelJsonString.NotString:
                case TopLevelJsonString.TooLong:
                    return new MessageIdRead(true, null);
                default:
                    return new MessageIdRead(false, null);
            }
        }

        /// <summary>Outcome of reading the top-level <c>messageId</c> string.</summary>
        /// <param name="PropertySeen">
        /// <see langword="true"/> when a top-level <c>messageId</c> property was present, including null, non-string, and overlong values.
        /// <see langword="false"/> when the property was absent.
        /// </param>
        /// <param name="MessageId">
        /// The accepted ASCII Message-ID when the property was a valid token; otherwise <see langword="null"/>.
        /// </param>
        private readonly record struct MessageIdRead(bool PropertySeen, string? MessageId);

        /// <summary>How a top-level JSON string copy finished. Callers treat only <see cref="Copied"/> as a candidate token.</summary>
        private enum TopLevelJsonString
        {
            /// <summary>The property was absent, or the payload was not a JSON object.</summary>
            Missing,

            /// <summary>The property was present and JSON null.</summary>
            Null,

            /// <summary>The property was present and was not a JSON string.</summary>
            NotString,

            /// <summary>The property was a string longer than the destination buffer.</summary>
            TooLong,

            /// <summary>The unescaped string bytes were copied into the destination.</summary>
            Copied,
        }

        /// <summary>Copies one top-level JSON string property into <paramref name="destination"/> without a heap allocation.</summary>
        /// <param name="json">UTF-8 JSON object. A payload that is not an object is <see cref="TopLevelJsonString.Missing"/>.</param>
        /// <param name="propertyName">Exact top-level property name. Nested properties are skipped.</param>
        /// <param name="destination">Buffer that receives the unescaped string bytes.</param>
        /// <param name="written">Bytes copied when the result is <see cref="TopLevelJsonString.Copied"/>; otherwise zero.</param>
        /// <returns>Why the copy stopped. <see cref="TopLevelJsonString.TooLong"/> means the string did not fit <paramref name="destination"/>.</returns>
        private static TopLevelJsonString TryCopyTopLevelJsonString(
            ReadOnlySpan<byte> json,
            ReadOnlySpan<byte> propertyName,
            Span<byte> destination,
            out int written)
        {
            written = 0;
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return TopLevelJsonString.Missing;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                {
                    return TopLevelJsonString.Missing;
                }

                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                {
                    continue;
                }

                var match = reader.ValueTextEquals(propertyName);
                if (!reader.Read())
                {
                    return TopLevelJsonString.Missing;
                }

                if (!match)
                {
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    {
                        reader.Skip();
                    }

                    continue;
                }

                if (reader.TokenType == JsonTokenType.Null)
                {
                    return TopLevelJsonString.Null;
                }

                if (reader.TokenType != JsonTokenType.String)
                {
                    return TopLevelJsonString.NotString;
                }

                if (!reader.ValueIsEscaped)
                {
                    var value = reader.ValueSpan;
                    if (value.Length > destination.Length)
                    {
                        return TopLevelJsonString.TooLong;
                    }

                    value.CopyTo(destination);
                    written = value.Length;
                    return TopLevelJsonString.Copied;
                }

                try
                {
                    written = reader.CopyString(destination);
                    return TopLevelJsonString.Copied;
                }
                catch (ArgumentException)
                {
                    written = 0;
                    return TopLevelJsonString.TooLong;
                }
            }

            return TopLevelJsonString.Missing;
        }

        /// <summary>Reads a case-sensitive JSON number property as a 32-bit integer.</summary>
        /// <param name="root">Payload object.</param>
        /// <param name="name">Exact property name.</param>
        /// <param name="value">Parsed value when the result is <see langword="true"/>; otherwise zero.</param>
        /// <returns>
        /// <see langword="true"/> when <paramref name="name"/> exists, is a JSON number, and fits <see cref="int"/>.
        /// </returns>
        private static bool TryReadInt32(JsonElement root, string name, out int value)
        {
            value = 0;
            return root.TryGetProperty(name, out var property)
                   && property.ValueKind == JsonValueKind.Number
                   && property.TryGetInt32(out value);
        }

        /// <summary>Reads a case-sensitive JSON string property without treating empty text as missing.</summary>
        /// <param name="root">Payload object.</param>
        /// <param name="name">Exact property name.</param>
        /// <param name="value">The string when the result is <see langword="true"/>; otherwise null.</param>
        /// <returns>
        /// <see langword="true"/> when <paramref name="name"/> exists, is a JSON string, and is not JSON null.
        /// </returns>
        private static bool TryReadString(JsonElement root, string name, out string? value)
        {
            value = null;
            if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            value = property.GetString();
            return value is not null;
        }

        /// <summary>Reads a case-sensitive JSON string property as a non-empty GUID.</summary>
        /// <param name="root">Payload object.</param>
        /// <param name="name">Exact property name.</param>
        /// <param name="value">The GUID when the result is <see langword="true"/>; otherwise null.</param>
        /// <returns>
        /// <see langword="false"/> when the property is missing, not a string, blank, not a GUID, or <see cref="Guid.Empty"/>.
        /// </returns>
        private static bool TryReadGuid(JsonElement root, string name, out Guid? value)
        {
            value = null;
            if (!TryReadString(root, name, out var text) || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (!Guid.TryParse(text, out var parsed) || parsed == Guid.Empty)
            {
                return false;
            }

            value = parsed;
            return true;
        }
    }
}

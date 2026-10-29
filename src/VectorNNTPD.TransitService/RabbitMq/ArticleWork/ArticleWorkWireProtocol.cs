using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Messaging.Cache;
using VectorNNTPD.Common.Articles.Validation;

namespace VectorNNTPD.TransitService.RabbitMq.ArticleWork
{
    /// <summary>
    /// Canonical compact System.Text.Json contract for BackFiller article-work v1 request and response bodies.
    /// </summary>
    /// <remarks>
    /// Property names are <c>version</c>, <c>requestId</c>, <c>messageId</c>, <c>backbone</c>,
    /// and on responses <c>outcome</c>, <c>fqdn</c>, <c>vatpPort</c>, <c>articleId</c>, and <c>error</c>.
    /// A response that includes <c>uri</c> is rejected. AMQP <c>CorrelationId</c>,
    /// <c>ReplyTo</c>, and <c>Expiration</c> are never JSON fields. JSON <c>requestId</c> must
    /// match the AMQP <c>RequestId</c> property. Serialization writes compact UTF-8 with no indentation.
    /// </remarks>
    internal static partial class ArticleWorkWireProtocol
    {
        /// <summary>Current application protocol version.</summary>
        internal const int CurrentVersion = 1;

        /// <summary>AMQP <c>ContentType</c> required by the BackFiller RPC contract.</summary>
        internal const string JsonContentType = "application/json";

        /// <summary>Serializes a version-1 request into compact UTF-8 JSON.</summary>
        /// <param name="request">Application fields to write. Must be a concrete valid v1 request.</param>
        /// <returns>UTF-8 bytes containing only the four canonical request properties.</returns>
        internal static byte[] SerializeRequestV1(ArticleWorkRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Version != CurrentVersion)
            {
                throw new InvalidOperationException($"Unsupported article-work request version '{request.Version}'.");
            }

            if (request.RequestId == Guid.Empty)
            {
                throw new InvalidOperationException("Canonical request serialization requires a concrete non-empty requestId.");
            }

            if (string.IsNullOrWhiteSpace(request.MessageId))
            {
                throw new InvalidOperationException("Canonical request serialization requires a canonical non-empty messageId.");
            }

            if (string.IsNullOrWhiteSpace(request.Backbone))
            {
                throw new InvalidOperationException("Canonical request serialization requires a non-empty backbone.");
            }

            var writer = new ArrayBufferWriter<byte>();
            using var jsonWriter = new Utf8JsonWriter(writer, new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            jsonWriter.WriteStartObject();
            jsonWriter.WriteNumber("version", request.Version);
            jsonWriter.WriteString("requestId", request.RequestId);
            jsonWriter.WriteString("messageId", request.MessageId);
            jsonWriter.WriteString("backbone", request.Backbone);
            jsonWriter.WriteEndObject();
            jsonWriter.Flush();
            return writer.WrittenSpan.ToArray();
        }

        /// <summary>Attempts to parse and validate a version-1 response payload.</summary>
        /// <param name="payload">UTF-8 JSON body.</param>
        /// <param name="response">Parsed response when validation succeeds.</param>
        /// <param name="reason">Rejection reason when parsing fails.</param>
        /// <returns><see langword="true"/> when the payload is a valid v1 response.</returns>
        internal static bool TryParseResponseV1(
            ReadOnlySpan<byte> payload,
            out ArticleWorkResponse? response,
            out string reason)
        {
            response = null;
            reason = "Response payload is empty.";
            if (payload.IsEmpty)
            {
                return false;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(payload.ToArray());
            }
            catch (JsonException)
            {
                reason = "Response payload was not valid JSON.";
                return false;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    reason = "Response payload must be a JSON object.";
                    return false;
                }

                if (!root.TryGetProperty("version", out var versionElement)
                    || versionElement.ValueKind != JsonValueKind.Number
                    || !versionElement.TryGetInt32(out var version))
                {
                    reason = "Response payload is missing required integer property 'version'.";
                    return false;
                }

                if (version != CurrentVersion)
                {
                    reason = $"Unsupported response version '{version}'.";
                    return false;
                }

                if (!TryReadRequiredString(root, "outcome", out var outcomeText) || outcomeText is null)
                {
                    reason = "Response payload is missing required 'outcome'.";
                    return false;
                }

                if (!TryParseOutcome(outcomeText, out var outcome))
                {
                    reason = $"Unsupported response outcome '{outcomeText}'.";
                    return false;
                }

                if (!TryReadOptionalGuid(root, "requestId", out var requestId, out reason))
                {
                    return false;
                }

                if (!TryReadMessageId(payload, out var messageId, out reason))
                {
                    return false;
                }

                if (!TryReadOptionalString(root, "backbone", out var backbone, out reason))
                {
                    return false;
                }

                if (root.TryGetProperty("uri", out _))
                {
                    reason = "Response payload must not include 'uri'.";
                    return false;
                }

                var hasFqdn = root.TryGetProperty("fqdn", out var fqdnElement);
                var hasVatpPort = root.TryGetProperty("vatpPort", out var vatpPortElement);
                var hasError = root.TryGetProperty("error", out var errorElement);
                var hasArticleId = root.TryGetProperty("articleId", out var articleIdElement);
                string? fqdn = null;
                int? vatpPort = null;
                string? error = null;
                ArticleId? articleId = null;
                if (hasFqdn && !TryReadStringValue(fqdnElement, "fqdn", out fqdn, out reason))
                {
                    return false;
                }

                if (hasVatpPort)
                {
                    if (vatpPortElement.ValueKind != JsonValueKind.Number || !vatpPortElement.TryGetInt32(out var parsedPort))
                    {
                        reason = "Response payload property 'vatpPort' must be an integer.";
                        return false;
                    }

                    vatpPort = parsedPort;
                }

                if (hasError && !TryReadStringValue(errorElement, "error", out error, out reason))
                {
                    return false;
                }

                if (hasArticleId)
                {
                    if (!TryReadStringValue(articleIdElement, "articleId", out var articleIdText, out reason))
                    {
                        return false;
                    }

                    if (articleIdText is not null)
                    {
                        if (!ArticleId.TryParseLowerHex(articleIdText, out var parsedArticleId))
                        {
                            reason = "Response payload property 'articleId' is not a 64-character lowercase hexadecimal ArticleId.";
                            return false;
                        }

                        articleId = parsedArticleId;
                    }
                }

                if (!TryValidateOutcomeContract(
                        outcome,
                        requestId,
                        messageId,
                        backbone,
                        hasFqdn,
                        fqdn,
                        hasVatpPort,
                        vatpPort,
                        hasArticleId,
                        articleId,
                        hasError,
                        error,
                        out reason))
                {
                    return false;
                }

                response = new ArticleWorkResponse(
                    version,
                    requestId,
                    messageId,
                    backbone,
                    outcome,
                    fqdn,
                    vatpPort,
                    articleId,
                    error);
                reason = string.Empty;
                return true;
            }
        }

        private static bool TryValidateOutcomeContract(
            ArticleWorkOutcome outcome,
            Guid? requestId,
            string? messageId,
            string? backbone,
            bool hasFqdn,
            string? fqdn,
            bool hasVatpPort,
            int? vatpPort,
            bool hasArticleId,
            ArticleId? articleId,
            bool hasError,
            string? error,
            out string reason)
        {
            reason = string.Empty;
            if (outcome == ArticleWorkOutcome.Success)
            {
                if (!requestId.HasValue || requestId.Value == Guid.Empty)
                {
                    reason = "Success response payload requires a concrete non-empty 'requestId'.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(messageId))
                {
                    reason = "Success response payload requires a canonical non-empty 'messageId'.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(backbone))
                {
                    reason = "Success response payload requires a non-empty 'backbone'.";
                    return false;
                }

                if (!hasFqdn || !VatpEndpointFields.IsCanonicalFqdn(fqdn))
                {
                    reason = "Success response payload requires a lowercase dotted DNS 'fqdn'.";
                    return false;
                }

                if (!hasVatpPort || vatpPort is not int port || !VatpEndpointFields.IsCanonicalPort(port))
                {
                    reason = "Success response payload requires a 'vatpPort' in the range 1–65535.";
                    return false;
                }

                if (!hasArticleId || articleId is null)
                {
                    reason = "Success response payload requires a concrete 'articleId'.";
                    return false;
                }

                if (hasError)
                {
                    reason = "Success response payload must not include 'error'.";
                    return false;
                }

                return true;
            }

            if (outcome is ArticleWorkOutcome.ArticleNotFound or ArticleWorkOutcome.InvalidArticle)
            {
                if (!requestId.HasValue || requestId.Value == Guid.Empty)
                {
                    reason = "Terminal non-invalid-request response payload requires a concrete non-empty 'requestId'.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(messageId))
                {
                    reason = "Terminal non-invalid-request response payload requires a canonical non-empty 'messageId'.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(backbone))
                {
                    reason = "Terminal non-invalid-request response payload requires a non-empty 'backbone'.";
                    return false;
                }

                if (hasFqdn || hasVatpPort)
                {
                    reason = "Terminal failure response payload must not include 'fqdn' or 'vatpPort'.";
                    return false;
                }

                if (hasArticleId)
                {
                    reason = "Terminal failure response payload must not include 'articleId'.";
                    return false;
                }

                if (!hasError || string.IsNullOrWhiteSpace(error))
                {
                    reason = "Terminal failure response payload requires a non-empty 'error'.";
                    return false;
                }

                return true;
            }

            if (requestId.HasValue && requestId.Value == Guid.Empty)
            {
                reason = "InvalidRequest payload must not use Guid.Empty sentinel request identity.";
                return false;
            }

            if (messageId is not null && string.IsNullOrWhiteSpace(messageId))
            {
                reason = "InvalidRequest payload messageId, when provided, must be canonical and non-empty.";
                return false;
            }

            if (backbone is not null && string.IsNullOrWhiteSpace(backbone))
            {
                reason = "InvalidRequest payload backbone, when provided, must be non-empty.";
                return false;
            }

            if (hasFqdn || hasVatpPort)
            {
                reason = "InvalidRequest payload must not include 'fqdn' or 'vatpPort'.";
                return false;
            }

            if (hasArticleId)
            {
                reason = "InvalidRequest payload must not include 'articleId'.";
                return false;
            }

            if (!hasError || string.IsNullOrWhiteSpace(error))
            {
                reason = "InvalidRequest payload requires a non-empty 'error'.";
                return false;
            }

            return true;
        }

        private static bool TryParseOutcome(string outcome, out ArticleWorkOutcome parsed)
        {
            if (string.Equals(outcome, nameof(ArticleWorkOutcome.Success), StringComparison.Ordinal))
            {
                parsed = ArticleWorkOutcome.Success;
                return true;
            }

            if (string.Equals(outcome, nameof(ArticleWorkOutcome.ArticleNotFound), StringComparison.Ordinal))
            {
                parsed = ArticleWorkOutcome.ArticleNotFound;
                return true;
            }

            if (string.Equals(outcome, nameof(ArticleWorkOutcome.InvalidArticle), StringComparison.Ordinal))
            {
                parsed = ArticleWorkOutcome.InvalidArticle;
                return true;
            }

            if (string.Equals(outcome, nameof(ArticleWorkOutcome.InvalidRequest), StringComparison.Ordinal))
            {
                parsed = ArticleWorkOutcome.InvalidRequest;
                return true;
            }

            parsed = default;
            return false;
        }

        private static bool TryReadRequiredString(JsonElement root, string propertyName, out string? value)
        {
            value = null;
            return root.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String
                && (value = property.GetString()) is not null;
        }

        private static bool TryReadOptionalGuid(
            JsonElement root,
            string propertyName,
            out Guid? value,
            out string reason)
        {
            value = null;
            reason = string.Empty;
            if (!root.TryGetProperty(propertyName, out var property))
            {
                reason = $"Response payload is missing required '{propertyName}'.";
                return false;
            }

            if (property.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (property.ValueKind != JsonValueKind.String)
            {
                reason = $"Response payload property '{propertyName}' must be a JSON string or null.";
                return false;
            }

            var text = property.GetString();
            if (string.IsNullOrWhiteSpace(text) || !Guid.TryParse(text, out var parsed) || parsed == Guid.Empty)
            {
                reason = $"Response payload property '{propertyName}' is not a concrete GUID.";
                return false;
            }

            value = parsed;
            return true;
        }

        /// <summary>
        /// Reads the top-level <c>messageId</c> property from the original UTF-8 payload and validates those bytes.
        /// </summary>
        /// <param name="json">Response payload. Already known to be a JSON object.</param>
        /// <param name="messageId">ASCII text of a valid Message-ID, or null when the property is JSON null.</param>
        /// <param name="reason">Rejection text when the result is <see langword="false"/>.</param>
        /// <returns><see langword="false"/> when the property is missing, not a string, or not a valid Message-ID.</returns>
        private static bool TryReadMessageId(ReadOnlySpan<byte> json, out string? messageId, out string reason)
        {
            messageId = null;
            reason = string.Empty;
            Span<byte> buffer = stackalloc byte[NntpMessageIdValidation.MaxMessageIdLength];
            switch (TryCopyTopLevelJsonString(json, "messageId"u8, buffer, out var length))
            {
                case TopLevelJsonString.Missing:
                    reason = "Response payload is missing required 'messageId'.";
                    return false;
                case TopLevelJsonString.Null:
                    return true;
                case TopLevelJsonString.NotString:
                    reason = "Response payload property 'messageId' must be a JSON string or null.";
                    return false;
                case TopLevelJsonString.TooLong:
                    reason = "Response payload property 'messageId' must be a canonical Message-ID.";
                    return false;
                case TopLevelJsonString.Copied:
                    var token = buffer[..length];
                    if (length == 0 || !NntpMessageIdValidation.IsValidMessageId(token))
                    {
                        reason = "Response payload property 'messageId' must be a canonical Message-ID.";
                        return false;
                    }

                    messageId = System.Text.Encoding.ASCII.GetString(token);
                    return true;
                default:
                    reason = "Response payload is missing required 'messageId'.";
                    return false;
            }
        }

        private enum TopLevelJsonString
        {
            Missing,
            Null,
            NotString,
            TooLong,
            Copied,
        }

        /// <summary>Copies one top-level JSON string property into <paramref name="destination"/> without allocating.</summary>
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

        private static bool TryReadOptionalString(
            JsonElement root,
            string propertyName,
            out string? value,
            out string reason)
        {
            value = null;
            reason = string.Empty;
            if (!root.TryGetProperty(propertyName, out var property))
            {
                reason = $"Response payload is missing required '{propertyName}'.";
                return false;
            }

            return TryReadStringValue(property, propertyName, out value, out reason);
        }

        private static bool TryReadStringValue(
            JsonElement property,
            string propertyName,
            out string? value,
            out string reason)
        {
            value = null;
            reason = string.Empty;
            switch (property.ValueKind)
            {
                case JsonValueKind.Null:
                    return true;
                case JsonValueKind.String:
                    value = property.GetString();
                    if (value is null)
                    {
                        reason = $"Response payload property '{propertyName}' string value was null.";
                        return false;
                    }

                    return true;
                default:
                    reason = $"Response payload property '{propertyName}' must be a JSON string or null.";
                    return false;
            }
        }
    }
}

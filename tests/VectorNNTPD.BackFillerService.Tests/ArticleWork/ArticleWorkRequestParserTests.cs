using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using System.Text;
using System.Text.Json;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork
{
    /// <summary>
    /// Article work request JSON and AMQP header contract: byte-oriented body parsing, schema validation, and bounded size rejection.
    /// </summary>
    public sealed class ArticleWorkRequestParserTests
    {
        /// <summary>
        /// Default <c>maxPayloadBytes</c> used unless a test overrides the limit for boundary coverage.
        /// </summary>
        private const int MaxPayload = 1024;

        /// <summary>
        /// Canonical fixture JSON with matching delivery backbone parses to version 1 identities without mutation.
        /// </summary>
        [Fact]
        public void Canonical_protocol_payload_is_valid()
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                MaxPayload);

            Assert.True(parsed.IsValid);
            Assert.NotNull(parsed.Request);
            Assert.Equal(1, parsed.Request.Version);
            Assert.Equal(Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId), parsed.Request.RequestId);
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalMessageId, parsed.Request.MessageId);
            Assert.Equal("Giganews", parsed.Request.Backbone);
        }

        /// <summary>
        /// Protocol documentation valid example accepts when the consumer backbone parameter matches routing context.
        /// </summary>
        [Fact]
        public void Protocol_valid_example_is_accepted()
        {
            var delivery = ArticleWorkTestDeliveries.Create(
                ArticleWorkTestDeliveries.ProtocolValidExampleJson,
                requestIdHeader: "d0648b54-b1b8-4717-95e1-7b31bf7fd1bd");

            var parsed = ArticleWorkRequestParser.Parse(delivery, "Eweka", MaxPayload);

            Assert.True(parsed.IsValid);
            Assert.Equal("<abc@example.invalid>", parsed.Request!.MessageId);
            Assert.Equal("Eweka", parsed.Request.Backbone);
        }

        /// <summary>
        /// Extra JSON properties and wrong-case correlation fields in the body are ignored; required camelCase fields still validate.
        /// </summary>
        [Fact]
        public void Unknown_json_fields_are_ignored()
        {
            var json =
                """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews","extra":true,"CorrelationId":"ignore"}""";

            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "Giganews",
                MaxPayload);

            Assert.True(parsed.IsValid);
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalMessageId, parsed.Request!.MessageId);
        }

        /// <summary>
        /// PascalCase property names fail validation; failure identities omit request id when version is missing.
        /// </summary>
        [Fact]
        public void Property_names_are_case_sensitive()
        {
            var json =
                """{"Version":1,"RequestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","MessageId":"<12345@example.invalid>","Backbone":"Giganews"}""";

            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("version", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Null(parsed.Failure.Identities.RequestId);
        }

        /// <summary>
        /// Non-JSON body yields invalid request with JSON syntax reason and no parsed identities.
        /// </summary>
        [Fact]
        public void Malformed_json_is_invalid_request()
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create("{not-json"),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("not valid JSON", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Null(parsed.Failure.Identities.RequestId);
        }

        /// <summary>
        /// Empty body is rejected before field validation.
        /// </summary>
        [Fact]
        public void Empty_payload_is_invalid_request()
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(string.Empty),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("empty", parsed.Failure!.Reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// Missing <c>version</c> fails closed; partial identity may still be extracted for diagnostics.
        /// </summary>
        [Fact]
        public void Missing_version_is_invalid_request()
        {
            var json =
                """{"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews"}""";

            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("'version'", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Equal(Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId), parsed.Failure.Identities.RequestId);
        }

        /// <summary>
        /// Protocol invalid example (unsupported version / backbone mismatch) rejects with partial failure identities per schema rules.
        /// </summary>
        [Fact]
        public void Protocol_invalid_example_is_rejected_for_unsupported_version()
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(ArticleWorkTestDeliveries.ProtocolInvalidExampleJson),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("unsupported version", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Null(parsed.Failure.Identities.RequestId);
            Assert.Null(parsed.Failure.Identities.MessageId);
            Assert.Equal("WrongBackbone", parsed.Failure.Identities.Backbone);
        }

        /// <summary>
        /// Absent <c>requestId</c> is invalid; parser does not invent a guid.
        /// </summary>
        [Fact]
        public void Missing_request_id_is_invalid_and_not_synthesized()
        {
            var json =
                """{"version":1,"messageId":"<12345@example.invalid>","backbone":"Giganews"}""";

            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("requestId", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Null(parsed.Failure.Identities.RequestId);
        }

        /// <summary>
        /// Empty string and nil UUID request ids are both rejected without identity in failure payload.
        /// </summary>
        [Fact]
        public void Empty_and_nil_request_ids_are_invalid()
        {
            var empty = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    """{"version":1,"requestId":"","messageId":"<12345@example.invalid>","backbone":"Giganews"}"""),
                "Giganews",
                MaxPayload);
            var nil = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    """{"version":1,"requestId":"00000000-0000-0000-0000-000000000000","messageId":"<12345@example.invalid>","backbone":"Giganews"}"""),
                "Giganews",
                MaxPayload);

            Assert.False(empty.IsValid);
            Assert.False(nil.IsValid);
            Assert.Null(empty.Failure!.Identities.RequestId);
            Assert.Null(nil.Failure!.Identities.RequestId);
        }

        /// <summary>
        /// Missing or unbracketed message ids fail without auto-wrapping or normalizing wire form.
        /// </summary>
        [Fact]
        public void Missing_and_invalid_message_ids_are_rejected_without_normalization()
        {
            var missing = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","backbone":"Giganews"}"""),
                "Giganews",
                MaxPayload);
            var unbracketed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"12345@example.invalid","backbone":"Giganews"}"""),
                "Giganews",
                MaxPayload);

            Assert.False(missing.IsValid);
            Assert.False(unbracketed.IsValid);
            Assert.Null(missing.Failure!.Identities.MessageId);
            Assert.Null(unbracketed.Failure!.Identities.MessageId);
        }

        /// <summary>
        /// Accepted message-id string equals JSON literal bytes (case and brackets preserved).
        /// </summary>
        [Fact]
        public void Exact_message_id_is_preserved()
        {
            const string wire = "<AbC@Example.INVALID>";
            var json =
                $$"""{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"{{wire}}","backbone":"Giganews"}""";

            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "Giganews",
                MaxPayload);

            Assert.True(parsed.IsValid);
            Assert.Equal(wire, parsed.Request!.MessageId);
            Assert.NotEqual(wire.ToLowerInvariant(), parsed.Request.MessageId);
        }

        /// <summary>
        /// JSON without <c>backbone</c> is invalid; failure identities omit backbone.
        /// </summary>
        [Fact]
        public void Missing_backbone_is_invalid_request()
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>"}"""),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("backbone", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Null(parsed.Failure.Identities.Backbone);
        }

        /// <summary>
        /// JSON backbone must match the consumer's expected backbone parameter (ordinal ignore case for equality only).
        /// </summary>
        [Fact]
        public void Backbone_mismatch_is_invalid_request()
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Canonical(),
                "Eweka",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("does not match", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Equal("Giganews", parsed.Failure.Identities.Backbone);
        }

        /// <summary>
        /// Backbone comparison ignores case but stored value remains the JSON string literal.
        /// </summary>
        [Fact]
        public void Backbone_match_is_ordinal_ignore_case_and_preserves_json_value()
        {
            var json =
                """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"giganews"}""";

            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "Giganews",
                MaxPayload);

            Assert.True(parsed.IsValid);
            Assert.Equal("giganews", parsed.Request!.Backbone);
        }

        /// <summary>
        /// AMQP correlation id header is mandatory for valid requests.
        /// </summary>
        [Fact]
        public void Missing_correlation_id_is_invalid_request()
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    ArticleWorkTestDeliveries.CanonicalRequestJson,
                    correlationId: null),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("CorrelationId", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Equal(Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId), parsed.Failure.Identities.RequestId);
        }

        /// <summary>
        /// Blank reply-to routing target fails validation.
        /// </summary>
        [Fact]
        public void Missing_reply_to_is_invalid_request()
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    ArticleWorkTestDeliveries.CanonicalRequestJson,
                    replyTo: " "),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("ReplyTo", parsed.Failure!.Reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// Absent or empty Content-Type is allowed; non-JSON content type when present is rejected.
        /// </summary>
        [Fact]
        public void ContentType_is_required_only_when_present()
        {
            var absent = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    ArticleWorkTestDeliveries.CanonicalRequestJson,
                    contentType: null),
                "Giganews",
                MaxPayload);
            var empty = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    ArticleWorkTestDeliveries.CanonicalRequestJson,
                    contentType: string.Empty),
                "Giganews",
                MaxPayload);
            var wrong = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    ArticleWorkTestDeliveries.CanonicalRequestJson,
                    contentType: "text/plain"),
                "Giganews",
                MaxPayload);

            Assert.True(absent.IsValid);
            Assert.True(empty.IsValid);
            Assert.False(wrong.IsValid);
            Assert.Contains("ContentType", wrong.Failure!.Reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// Optional RequestId header must match body request id when supplied; absent header is allowed.
        /// </summary>
        [Fact]
        public void RequestId_header_is_required_only_when_present()
        {
            var absent = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Canonical(requestIdHeader: null),
                "Giganews",
                MaxPayload);
            var mismatch = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Canonical(requestIdHeader: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                "Giganews",
                MaxPayload);
            var empty = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Canonical(requestIdHeader: " "),
                "Giganews",
                MaxPayload);

            Assert.True(absent.IsValid);
            Assert.False(mismatch.IsValid);
            Assert.True(empty.IsValid);
            Assert.Contains("RequestId", mismatch.Failure!.Reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// Parsed request id remains distinct from AMQP correlation id and delivery tag strings.
        /// </summary>
        [Fact]
        public void RequestId_is_distinct_from_correlation_id_and_delivery_tag()
        {
            var delivery = ArticleWorkTestDeliveries.Canonical(deliveryTag: 99);
            var parsed = ArticleWorkRequestParser.Parse(delivery, "Giganews", MaxPayload);

            Assert.True(parsed.IsValid);
            Assert.NotEqual(parsed.Request!.RequestId.ToString(), delivery.CorrelationId);
            Assert.NotEqual(parsed.Request.RequestId.ToString(), delivery.DeliveryTag.ToString());
            Assert.NotEqual(delivery.CorrelationId, delivery.DeliveryTag.ToString());
        }

        /// <summary>
        /// Size check runs first: oversized canonical body fails without extracting JSON identities.
        /// </summary>
        [Fact]
        public void Oversized_payload_is_invalid_without_json_identities()
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                maxPayloadBytes: 8);

            Assert.False(parsed.IsValid);
            Assert.Contains("WorkRequestMaxPayloadBytes", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Null(parsed.Failure.Identities.RequestId);
        }

        /// <summary>
        /// Message-id acceptance aligns with shared NNTP message-id rules (length, brackets, IPv6 literal form).
        /// </summary>
        [Fact]
        public void Message_id_uses_common_validator()
        {
            Assert.True(ParseMessageId("<a@b>").IsValid);
            Assert.True(ParseMessageId("<foo.bar@example.com>").IsValid);
            Assert.True(ParseMessageId("<foo@[IPv6:2001:db8::1]>").IsValid);
            Assert.Equal("<a@b>", ParseMessageId("<a@b>").Request!.MessageId);
            Assert.True(ParseMessageId("<" + new string('a', 246) + "@b>").IsValid);

            Assert.False(ParseMessageId("<x>").IsValid);
            Assert.False(ParseMessageId("<@>").IsValid);
            Assert.False(ParseMessageId("<@b>").IsValid);
            Assert.False(ParseMessageId("<x@@y>").IsValid);
            Assert.False(ParseMessageId("<double..dot@example.com>").IsValid);
            Assert.False(ParseMessageId("<a b@c>").IsValid);
            Assert.False(ParseMessageId("<" + new string('a', 247) + "@b>").IsValid);
        }

        /// <summary>
        /// Parser reads delivery body bytes directly; UTF-8 decode for logging is not required for successful parse and must not mutate body length.
        /// </summary>
        [Fact]
        public void Parser_does_not_decode_body_as_string_before_json()
        {
            var body = Encoding.UTF8.GetBytes(ArticleWorkTestDeliveries.CanonicalRequestJson);
            var delivery = new VectorNNTPD.Common.Messaging.RabbitMq.RabbitMqManualAckDelivery(
                1,
                body,
                ArticleWorkTestDeliveries.CanonicalCorrelationId,
                ArticleWorkTestDeliveries.CanonicalReplyTo,
                ArticleWorkRequestParser.JsonContentType,
                ArticleWorkTestDeliveries.CanonicalRequestId,
                false,
                "backfiller.giganews",
                "backfiller.giganews",
                "ctag",
                1);

            var parsed = ArticleWorkRequestParser.Parse(delivery, "Giganews", MaxPayload);
            Assert.True(parsed.IsValid);
            Assert.Equal(body.Length, delivery.Body.Length);
        }

        /// <summary>
        /// Regression test: oversized message must be rejected before JSON parsing,
        /// with a bounded, specific diagnostic reason, never by reparsinig with an unbounded limit.
        /// </summary>
        [Fact]
        public void Oversized_payload_rejection_reason_is_bounded_and_specific()
        {
            // The rejection reason must come from the initial size check, not a second parse.
            // This prevents unbounded JSON parsing on an error path.
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                maxPayloadBytes: 8);

            Assert.False(parsed.IsValid);
            var reason = parsed.Failure!.Reason;
            Assert.NotNull(reason);
            Assert.Contains("WorkRequestMaxPayloadBytes", reason, StringComparison.Ordinal);
            // The reason must not require parsing the entire body; it must be deterministic from size check alone.
            Assert.DoesNotContain("JSON", reason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("schema", reason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("field", reason, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Boundary test: maximum + 1 byte is rejected; maximum byte is accepted for further parsing.
        /// </summary>
        [Theory]
        [InlineData(1023, true)]  // At maximum - 1
        [InlineData(1024, true)]  // At maximum
        [InlineData(1025, false)] // At maximum + 1
        public void Payload_size_boundary_is_enforced(int payloadSize, bool shouldBeValid)
        {
            const int configuredMaximum = 1024;
            var json = ArticleWorkTestDeliveries.CanonicalRequestJson;
            // Pad the backbone field to reach the desired size
            var padding = new string(' ', Math.Max(0, payloadSize - json.Length));
            var paddedJson = json.Replace("\"Giganews\"", $"\"{padding}Giganews\"");
            var body = Encoding.UTF8.GetBytes(paddedJson);

            var delivery = new VectorNNTPD.Common.Messaging.RabbitMq.RabbitMqManualAckDelivery(
                1,
                body,
                ArticleWorkTestDeliveries.CanonicalCorrelationId,
                ArticleWorkTestDeliveries.CanonicalReplyTo,
                ArticleWorkRequestParser.JsonContentType,
                ArticleWorkTestDeliveries.CanonicalRequestId,
                false,
                "backfiller.giganews",
                "backfiller.giganews",
                "ctag",
                1);

            var parsed = ArticleWorkRequestParser.Parse(delivery, "Giganews", configuredMaximum);

            if (shouldBeValid)
            {
                // At or below maximum, parsing should continue past size check.
                // It may fail for other reasons (schema, content), but not for size.
                Assert.DoesNotContain("WorkRequestMaxPayloadBytes", parsed.Failure?.Reason ?? string.Empty, StringComparison.Ordinal);
            }
            else
            {
                // Above maximum, must be rejected on size.
                Assert.False(parsed.IsValid);
                Assert.Contains("WorkRequestMaxPayloadBytes", parsed.Failure!.Reason, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Regression test: malformed JSON within the size limit must produce a distinct error from oversized,
        /// not be mistaken for an unbounded reparse scenario.
        /// </summary>
        [Fact]
        public void Malformed_json_within_size_limit_produces_distinct_error()
        {
            var json = "{invalid-json";
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("not valid JSON", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("WorkRequestMaxPayloadBytes", parsed.Failure.Reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// Content type and the AMQP request-id header are optional. The JSON identities still come from the body.
        /// </summary>
        [Fact]
        public void Optional_content_type_and_request_id_header_may_be_omitted()
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    ArticleWorkTestDeliveries.CanonicalRequestJson,
                    contentType: null,
                    requestIdHeader: null),
                "Giganews",
                MaxPayload);

            Assert.True(parsed.IsValid);
            Assert.Equal(1, parsed.Request!.Version);
            Assert.Equal(Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId), parsed.Request.RequestId);
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalMessageId, parsed.Request.MessageId);
            Assert.Equal("Giganews", parsed.Request.Backbone);
        }

        /// <summary>
        /// A <c>requestId</c> longer than the stack buffer is accepted when <see cref="Guid.TryParse(string, out Guid)"/> accepts it.
        /// Whitespace-only text and other long non-GUID strings stay invalid.
        /// </summary>
        [Fact]
        public void Long_request_ids_follow_guid_try_parse()
        {
            const string canonical = ArticleWorkTestDeliveries.CanonicalRequestId;
            var padded = new string(' ', 100) + canonical + new string(' ', 100);
            var expandedX = "{0x" + new string('0', 80) + "7c1cb8a0,0x95f9,0x4c13,{0x8e,0x53,0x33,0x97,0x73,0xe3,0xaf,0xaa}}";
            var spacedX = "{0x7c1cb8a0," + new string(' ', 80) + "0x95f9,0x4c13,{0x8e,0x53,0x33,0x97,0x73,0xe3,0xaf,0xaa}}";
            var invalid = new string('z', 160);

            AssertAcceptedRequestId(padded);
            AssertAcceptedRequestId(expandedX);
            AssertAcceptedRequestId(spacedX);
            AssertRejectedRequestId(invalid);
            AssertRejectedRequestId(" \t");
        }

        /// <summary>
        /// Duplicate <c>version</c>, <c>requestId</c>, and <c>backbone</c> keep the last value.
        /// Duplicate <c>messageId</c> keeps the first value.
        /// </summary>
        [Fact]
        public void Duplicate_properties_keep_last_value_except_message_id()
        {
            var lastVersion = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    """{"version":9,"version":1,"requestId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<first@example.invalid>","messageId":"<second@example.invalid>","backbone":"Other","backbone":"Giganews"}"""),
                "Giganews",
                MaxPayload);
            var clearedVersion = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    """{"version":1,"version":"1","requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews"}"""),
                "Giganews",
                MaxPayload);
            var clearedRequest = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","requestId":"not-a-guid","messageId":"<12345@example.invalid>","backbone":"Giganews"}"""),
                "Giganews",
                MaxPayload);

            Assert.True(lastVersion.IsValid);
            Assert.Equal(Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId), lastVersion.Request!.RequestId);
            Assert.Equal("<first@example.invalid>", lastVersion.Request.MessageId);
            Assert.Equal("Giganews", lastVersion.Request.Backbone);

            Assert.False(clearedVersion.IsValid);
            Assert.Contains("missing required integer property 'version'", clearedVersion.Failure!.Reason, StringComparison.Ordinal);
            Assert.Equal(Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId), clearedVersion.Failure.Identities.RequestId);

            Assert.False(clearedRequest.IsValid);
            Assert.Contains("missing or invalid 'requestId'", clearedRequest.Failure!.Reason, StringComparison.Ordinal);
            Assert.Null(clearedRequest.Failure.Identities.RequestId);
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalMessageId, clearedRequest.Failure.Identities.MessageId);
        }

        /// <summary>
        /// A JSON null <c>messageId</c> is absent, including when a later duplicate would have been valid.
        /// A non-string value is present and invalid.
        /// </summary>
        [Fact]
        public void Null_message_id_is_absent_and_non_string_message_id_is_not_canonical()
        {
            var absent = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":null,"messageId":"<12345@example.invalid>","backbone":"Giganews"}"""),
                "Giganews",
                MaxPayload);
            var nonString = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":false,"backbone":"Giganews"}"""),
                "Giganews",
                MaxPayload);

            Assert.False(absent.IsValid);
            Assert.Contains("missing or invalid 'messageId'", absent.Failure!.Reason, StringComparison.Ordinal);
            Assert.Null(absent.Failure.Identities.MessageId);
            Assert.Equal(Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId), absent.Failure.Identities.RequestId);

            Assert.False(nonString.IsValid);
            Assert.Contains("not a canonical NNTP Message-ID", nonString.Failure!.Reason, StringComparison.Ordinal);
            Assert.Null(nonString.Failure.Identities.MessageId);
        }

        /// <summary>
        /// Nested objects and arrays are not root fields, including when their names match required properties.
        /// </summary>
        [Fact]
        public void Nested_objects_and_arrays_do_not_supply_root_properties()
        {
            var json =
                """{"extra":{"version":2,"requestId":"not-a-guid","messageId":"nested","backbone":"Other"},"items":[1,{"version":9}],"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews"}""";

            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "Giganews",
                MaxPayload);

            Assert.True(parsed.IsValid);
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalMessageId, parsed.Request!.MessageId);
            Assert.Equal("Giganews", parsed.Request.Backbone);
        }

        /// <summary>
        /// A complete non-object root is a schema error. Trailing content after any complete value is invalid JSON.
        /// </summary>
        [Theory]
        [InlineData("[]", "must be a JSON object")]
        [InlineData("null", "must be a JSON object")]
        [InlineData("1", "must be a JSON object")]
        [InlineData("\"text\"", "must be a JSON object")]
        [InlineData("[][]", "not valid JSON")]
        [InlineData("1 2", "not valid JSON")]
        [InlineData("{\"version\":1} {\"version\":1}", "not valid JSON")]
        public void Wrong_roots_and_trailing_values_are_classified(string json, string reason)
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains(reason, parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Null(parsed.Failure.Identities.RequestId);
            Assert.Null(parsed.Failure.Identities.MessageId);
            Assert.Null(parsed.Failure.Identities.Backbone);
        }

        /// <summary>
        /// Truncated objects and arrays are invalid even when every required field appeared before the cut.
        /// </summary>
        [Theory]
        [InlineData("{\"version\":1")]
        [InlineData("{\"version\":")]
        [InlineData("[")]
        [InlineData("{\"version\":1,\"requestId\":\"7c1cb8a0-95f9-4c13-8e53-339773e3afaa\",\"messageId\":\"<12345@example.invalid>\",\"backbone\":\"Giganews\"")]
        public void Truncated_json_is_not_a_valid_request(string json)
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("not valid JSON", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Null(parsed.Failure.Identities.RequestId);
        }

        /// <summary>
        /// Wrong JSON types for required properties use the same reasons as a missing property of that type.
        /// </summary>
        [Fact]
        public void Wrong_property_types_are_rejected()
        {
            var version = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    """{"version":1.5,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews"}"""),
                "Giganews",
                MaxPayload);
            var requestId = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    """{"version":1,"requestId":1,"messageId":"<12345@example.invalid>","backbone":"Giganews"}"""),
                "Giganews",
                MaxPayload);
            var backbone = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":["Giganews"]}"""),
                "Giganews",
                MaxPayload);
            var emptyMessage = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"","backbone":"Giganews"}"""),
                "Giganews",
                MaxPayload);

            Assert.Contains("missing required integer property 'version'", version.Failure!.Reason, StringComparison.Ordinal);
            Assert.Contains("missing or invalid 'requestId'", requestId.Failure!.Reason, StringComparison.Ordinal);
            Assert.Contains("missing or invalid 'backbone'", backbone.Failure!.Reason, StringComparison.Ordinal);
            Assert.Contains("not a canonical NNTP Message-ID", emptyMessage.Failure!.Reason, StringComparison.Ordinal);
            Assert.Equal(Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId), version.Failure.Identities.RequestId);
            Assert.Null(requestId.Failure.Identities.RequestId);
            Assert.Null(backbone.Failure.Identities.Backbone);
        }

        /// <summary>
        /// JSON string escapes are unescaped before validation. Property-name escapes use the same names.
        /// </summary>
        [Fact]
        public void Escaped_strings_and_property_names_are_unescaped()
        {
            var json =
                """{"\u0076ersion":1,"requestId":"\u0037c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"\u003c12345@example.invalid\u003e","backbone":"\u0047iganews"}""";

            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "Giganews",
                MaxPayload);

            Assert.True(parsed.IsValid);
            Assert.Equal(Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId), parsed.Request!.RequestId);
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalMessageId, parsed.Request.MessageId);
            Assert.Equal("Giganews", parsed.Request.Backbone);
        }

        /// <summary>
        /// Non-ASCII backbone text is preserved as the unescaped JSON string and still participates in matching.
        /// </summary>
        [Fact]
        public void Unicode_backbone_is_preserved()
        {
            var json =
                """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"b\u00e4ck"}""";

            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "bäck",
                MaxPayload);

            Assert.True(parsed.IsValid);
            Assert.Equal("bäck", parsed.Request!.Backbone);
        }

        /// <summary>
        /// Comments and trailing commas are rejected as invalid JSON.
        /// </summary>
        [Theory]
        [InlineData("""{"version":1,}""")]
        [InlineData("""{"version":1 /* note */}""")]
        [InlineData("   \n\t")]
        public void Comments_trailing_commas_and_whitespace_only_payloads_are_invalid_json(string json)
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("not valid JSON", parsed.Failure!.Reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// A payload of exactly the configured maximum is parsed. One byte over that maximum is rejected before JSON is read.
        /// </summary>
        [Fact]
        public void Exact_maximum_payload_is_parsed_and_oversized_malformed_json_is_not()
        {
            var canonical = Encoding.UTF8.GetBytes(ArticleWorkTestDeliveries.CanonicalRequestJson);
            var exact = new byte[MaxPayload];
            canonical.CopyTo(exact, 0);
            exact.AsSpan(canonical.Length).Fill((byte)' ');
            var exactParsed = ArticleWorkRequestParser.Parse(Delivery(exact), "Giganews", MaxPayload);

            var oversized = new byte[MaxPayload + 1];
            oversized[0] = (byte)'{';
            var oversizedParsed = ArticleWorkRequestParser.Parse(Delivery(oversized), "Giganews", MaxPayload);

            Assert.True(exactParsed.IsValid);
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalMessageId, exactParsed.Request!.MessageId);
            Assert.False(oversizedParsed.IsValid);
            Assert.Contains("WorkRequestMaxPayloadBytes", oversizedParsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("not valid JSON", oversizedParsed.Failure.Reason, StringComparison.Ordinal);
            Assert.Null(oversizedParsed.Failure.Identities.RequestId);
            Assert.Null(oversizedParsed.Failure.Identities.MessageId);
            Assert.Null(oversizedParsed.Failure.Identities.Backbone);
        }

        /// <summary>
        /// A UTF-8 BOM is not JSON. <see cref="JsonDocument"/> rejects it, and the parser reports the same invalid-JSON result.
        /// </summary>
        [Fact]
        public void Utf8_bom_is_not_valid_json()
        {
            var canonical = Encoding.UTF8.GetBytes(ArticleWorkTestDeliveries.CanonicalRequestJson);
            var body = new byte[canonical.Length + 3];
            body[0] = 0xEF;
            body[1] = 0xBB;
            body[2] = 0xBF;
            canonical.CopyTo(body.AsSpan(3));

            var documentRejected = false;
            try
            {
                using var document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                documentRejected = true;
            }

            Assert.True(documentRejected);

            var parsed = ArticleWorkRequestParser.Parse(Delivery(body), "Giganews", MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("not valid JSON", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Null(parsed.Failure.Identities.RequestId);
        }

        /// <summary>
        /// Nesting beyond the JSON maximum of 64 is invalid JSON. The root object counts, so 63 nested objects are the deepest accepted extra value.
        /// </summary>
        [Fact]
        public void Excessive_nesting_is_invalid_json()
        {
            var accepted = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(NestedExtra(63) + CanonicalFields),
                "Giganews",
                MaxPayload);
            var rejected = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(NestedExtra(64) + CanonicalFields),
                "Giganews",
                MaxPayload);

            Assert.True(accepted.IsValid);
            Assert.False(rejected.IsValid);
            Assert.Contains("not valid JSON", rejected.Failure!.Reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// Builds a delivery whose body is the given bytes and whose other properties are the canonical fixture values.
        /// </summary>
        /// <param name="body">Application payload bytes.</param>
        /// <returns>A synthetic manual-ack delivery.</returns>
        private static VectorNNTPD.Common.Messaging.RabbitMq.RabbitMqManualAckDelivery Delivery(byte[] body)
        {
            return new VectorNNTPD.Common.Messaging.RabbitMq.RabbitMqManualAckDelivery(
                1,
                body,
                ArticleWorkTestDeliveries.CanonicalCorrelationId,
                ArticleWorkTestDeliveries.CanonicalReplyTo,
                ArticleWorkRequestParser.JsonContentType,
                ArticleWorkTestDeliveries.CanonicalRequestId,
                false,
                "backfiller.giganews",
                "backfiller.giganews",
                "ctag",
                1);
        }

        /// <summary>Required canonical fields, without a leading comma, used after an extra property.</summary>
        private const string CanonicalFields =
            "\"version\":1,\"requestId\":\"7c1cb8a0-95f9-4c13-8e53-339773e3afaa\",\"messageId\":\"<12345@example.invalid>\",\"backbone\":\"Giganews\"}";

        /// <summary>Builds <c>{"extra":</c> followed by <paramref name="depth"/> nested objects.</summary>
        /// <param name="depth">Number of nested objects, including the outermost extra value.</param>
        /// <returns>A JSON prefix that is not yet closed.</returns>
        private static string NestedExtra(int depth)
        {
            var json = new StringBuilder("{\"extra\":");
            for (var i = 0; i < depth; i++)
            {
                json.Append("{\"a\":");
            }

            json.Append('1');
            for (var i = 0; i < depth; i++)
            {
                json.Append('}');
            }

            json.Append(',');
            return json.ToString();
        }

        /// <summary>
        /// Accepts <paramref name="requestId"/> when the original string predicate accepts it, and requires the payload to stay under the size cap.
        /// </summary>
        /// <param name="requestId">JSON string value whose UTF-8 form is longer than the stack buffer.</param>
        private static void AssertAcceptedRequestId(string requestId)
        {
            Assert.True(Encoding.UTF8.GetByteCount(requestId) > 128);
            Assert.False(IsRejectedRequestId(requestId));
            Assert.True(Guid.TryParse(requestId, out var expected));

            var json = RequestJson(requestId);
            Assert.True(Encoding.UTF8.GetByteCount(json) < MaxPayload);

            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "Giganews",
                MaxPayload);

            Assert.True(parsed.IsValid);
            Assert.Equal(expected, parsed.Request!.RequestId);
        }

        /// <summary>
        /// Rejects <paramref name="requestId"/> when the original string predicate rejects it.
        /// </summary>
        /// <param name="requestId">JSON string value that is blank or not a non-empty GUID.</param>
        private static void AssertRejectedRequestId(string requestId)
        {
            Assert.True(IsRejectedRequestId(requestId));

            var json = RequestJson(requestId);
            Assert.True(Encoding.UTF8.GetByteCount(json) < MaxPayload);

            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("missing or invalid 'requestId'", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Null(parsed.Failure.Identities.RequestId);
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalMessageId, parsed.Failure.Identities.MessageId);
            Assert.Equal("Giganews", parsed.Failure.Identities.Backbone);
        }

        /// <summary>Original <c>requestId</c> rejection predicate.</summary>
        /// <param name="requestId">Decoded JSON string.</param>
        /// <returns><see langword="true"/> when the original parser rejected the text.</returns>
        private static bool IsRejectedRequestId(string requestId)
        {
            return string.IsNullOrWhiteSpace(requestId)
                || !Guid.TryParse(requestId, out var guid)
                || guid == Guid.Empty;
        }

        /// <summary>Builds a v1 payload whose only varying field is <paramref name="requestId"/>.</summary>
        /// <param name="requestId">Decoded request id inserted as a JSON string.</param>
        /// <returns>UTF-16 JSON text.</returns>
        private static string RequestJson(string requestId)
        {
            return "{\"version\":1,\"requestId\":" + JsonSerializer.Serialize(requestId)
                + ",\"messageId\":\"<12345@example.invalid>\",\"backbone\":\"Giganews\"}";
        }

        /// <summary>
        /// Parses a minimal JSON envelope with the given message-id for validator matrix checks.
        /// </summary>
        /// <param name="messageId">Wire message-id literal embedded in JSON.</param>
        /// <returns>Parse result from <see cref="ArticleWorkRequestParser.Parse"/>.</returns>
        private static ArticleWorkParseResult ParseMessageId(string messageId)
        {
            var json = "{\"version\":1,\"requestId\":\"7c1cb8a0-95f9-4c13-8e53-339773e3afaa\",\"messageId\":\""
                + messageId
                + "\",\"backbone\":\"Giganews\"}";
            return ArticleWorkRequestParser.Parse(ArticleWorkTestDeliveries.Create(json), "Giganews", 8192);
        }
    }
}

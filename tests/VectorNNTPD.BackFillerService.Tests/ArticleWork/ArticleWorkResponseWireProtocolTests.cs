using System.Text.Json;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using System.Text;

namespace VectorNNTPD.BackFillerService.Tests.ArticleWork
{
    /// <summary>
    /// v1 response JSON wire shape: canonical examples, field presence rules, and serializer rejection of non-terminal outcomes.
    /// </summary>
    public sealed class ArticleWorkResponseWireProtocolTests
    {
        /// <summary>
        /// Success serialization matches the canonical protocol bytes and excludes uri/error/payload fields.
        /// </summary>
        [Fact]
        public void Success_json_matches_the_canonical_protocol_example()
        {
            var json = ArticleWorkResponseWireProtocol.SerializeV1(SuccessIntent());

            Assert.Equal(ArticleWorkTestDeliveries.CanonicalSuccessResponseJson, Encoding.UTF8.GetString(json));
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.Equal(JsonValueKind.Object, root.ValueKind);
            Assert.Equal(1, root.GetProperty("version").GetInt32());
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalRequestId, root.GetProperty("requestId").GetString());
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalMessageId, root.GetProperty("messageId").GetString());
            Assert.Equal("Giganews", root.GetProperty("backbone").GetString());
            Assert.Equal("Success", root.GetProperty("outcome").GetString());
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalFqdn, root.GetProperty("fqdn").GetString());
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalListenerPort, root.GetProperty("listenerPort").GetInt32());
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalArticleIdHex, root.GetProperty("articleId").GetString());
            Assert.False(root.TryGetProperty("uri", out _));
            Assert.False(root.TryGetProperty("error", out _));
            Assert.DoesNotContain("payload", Encoding.UTF8.GetString(json), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Terminal failure (not found) matches canonical JSON and omits success-only endpoint properties.
        /// </summary>
        [Fact]
        public void Terminal_failure_json_matches_the_canonical_protocol_example()
        {
            var json = ArticleWorkResponseWireProtocol.SerializeV1(new ArticleWorkResponseIntent(
                ArticleWorkOutcome.ArticleNotFound,
                Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId),
                ArticleWorkTestDeliveries.CanonicalMessageId,
                "Giganews",
                ArticleWorkTestDeliveries.CanonicalCorrelationId,
                ArticleWorkTestDeliveries.CanonicalReplyTo,
                "No article with that message-id"));

            Assert.Equal(ArticleWorkTestDeliveries.CanonicalNotFoundResponseJson, Encoding.UTF8.GetString(json));
            using var document = JsonDocument.Parse(json);
            Assert.False(document.RootElement.TryGetProperty("uri", out _));
            Assert.False(document.RootElement.TryGetProperty("fqdn", out _));
            Assert.False(document.RootElement.TryGetProperty("listenerPort", out _));
            Assert.False(document.RootElement.TryGetProperty("articleId", out _));
            Assert.Equal("No article with that message-id", document.RootElement.GetProperty("error").GetString());
        }

        /// <summary>
        /// InvalidRequest may serialize null request/message/backbone identities and never emits fqdn/listenerPort/uri.
        /// </summary>
        [Fact]
        public void InvalidRequest_may_emit_null_identities_and_never_includes_endpoint_fields()
        {
            var json = ArticleWorkResponseWireProtocol.SerializeV1(new ArticleWorkResponseIntent(
                ArticleWorkOutcome.InvalidRequest,
                null,
                null,
                null,
                ArticleWorkTestDeliveries.CanonicalCorrelationId,
                ArticleWorkTestDeliveries.CanonicalReplyTo,
                "Malformed JSON."));

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.Equal(JsonValueKind.Null, root.GetProperty("requestId").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("messageId").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("backbone").ValueKind);
            Assert.Equal("InvalidRequest", root.GetProperty("outcome").GetString());
            Assert.False(root.TryGetProperty("uri", out _));
            Assert.False(root.TryGetProperty("fqdn", out _));
            Assert.False(root.TryGetProperty("listenerPort", out _));
            Assert.Equal("Malformed JSON.", root.GetProperty("error").GetString());
        }

        /// <summary>
        /// Serialized property order and names match the protocol camelCase field set for success.
        /// </summary>
        [Fact]
        public void Property_names_are_exact_camelCase_protocol_fields()
        {
            using var document = JsonDocument.Parse(ArticleWorkResponseWireProtocol.SerializeV1(SuccessIntent()));
            var names = document.RootElement.EnumerateObject().Select(static property => property.Name).ToArray();
            Assert.Equal(["version", "requestId", "messageId", "backbone", "outcome", "fqdn", "listenerPort", "articleId"], names);
        }

        /// <summary>
        /// Success serialization rejects fqdn values that are not a plain host label (no URL, wrong case pattern, empty).
        /// </summary>
        /// <param name="fqdn">Candidate fqdn field.</param>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("nodots")]
        [InlineData("Backfiller01.usenet.ninja")]
        [InlineData("https" + "://" + "backfiller01.usenet.ninja:119/dcab316ba0e91c6abbad8d5759bff207932dbe9168c88954c6dd9240b4a6da14")]
        public void Success_rejects_invalid_fqdn(string? fqdn)
        {
            var intent = SuccessIntent() with { Fqdn = fqdn };
            Assert.Throws<InvalidOperationException>(() => ArticleWorkResponseWireProtocol.SerializeV1(intent));
        }

        /// <summary>
        /// Success serialization requires a positive 16-bit listener port.
        /// </summary>
        /// <param name="port">Candidate listener port.</param>
        [Theory]
        [InlineData(null)]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(65536)]
        public void Success_rejects_invalid_listener_port(int? port)
        {
            var intent = SuccessIntent() with { ListenerPort = port };
            Assert.Throws<InvalidOperationException>(() => ArticleWorkResponseWireProtocol.SerializeV1(intent));
        }

        /// <summary>
        /// Success intent without article id hex fails at serialize time.
        /// </summary>
        [Fact]
        public void Success_rejects_missing_article_id()
        {
            var intent = SuccessIntent() with { ArticleIdHex = null };
            Assert.Throws<InvalidOperationException>(() => ArticleWorkResponseWireProtocol.SerializeV1(intent));
        }

        /// <summary>
        /// Serialized articleId hex parses back to the same <see cref="VectorNNTPD.Common.Articles.ArticleId"/> value.
        /// </summary>
        [Fact]
        public void Success_article_id_round_trips_exactly()
        {
            var json = ArticleWorkResponseWireProtocol.SerializeV1(SuccessIntent());
            using var document = JsonDocument.Parse(json);
            var hex = document.RootElement.GetProperty("articleId").GetString();
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalArticleIdHex, hex);
            Assert.True(VectorNNTPD.Common.Articles.ArticleId.TryParseLowerHex(hex, out var parsed));
            Assert.Equal(
                VectorNNTPD.Common.Articles.ArticleId.ParseLowerHex(ArticleWorkTestDeliveries.CanonicalArticleIdHex),
                parsed);
        }

        /// <summary>
        /// Retryable/non-terminal outcomes must not produce v1 terminal response JSON (serializer throws).
        /// </summary>
        /// <param name="outcomeName">Non-terminal outcome name.</param>
        [Theory]
        [InlineData(nameof(ArticleWorkOutcome.ProviderFailure))]
        [InlineData(nameof(ArticleWorkOutcome.Cancelled))]
        [InlineData(nameof(ArticleWorkOutcome.UnexpectedFailure))]
        [InlineData(nameof(ArticleWorkOutcome.RetentionRejected))]
        public void Retryable_outcomes_are_not_serializable_terminal_responses(string outcomeName)
        {
            var outcome = Enum.Parse<ArticleWorkOutcome>(outcomeName);
            var intent = new ArticleWorkResponseIntent(
                outcome,
                Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId),
                ArticleWorkTestDeliveries.CanonicalMessageId,
                "Giganews",
                ArticleWorkTestDeliveries.CanonicalCorrelationId,
                ArticleWorkTestDeliveries.CanonicalReplyTo,
                "retry");

            Assert.Throws<InvalidOperationException>(() => ArticleWorkResponseWireProtocol.SerializeV1(intent));
        }

        /// <summary>
        /// Canonical success response intent matching protocol documentation fixtures.
        /// </summary>
        /// <returns>Intent passed to <see cref="ArticleWorkResponseWireProtocol.SerializeV1"/>.</returns>
        private static ArticleWorkResponseIntent SuccessIntent() =>
            new(
                ArticleWorkOutcome.Success,
                Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId),
                ArticleWorkTestDeliveries.CanonicalMessageId,
                "Giganews",
                ArticleWorkTestDeliveries.CanonicalCorrelationId,
                ArticleWorkTestDeliveries.CanonicalReplyTo,
                Error: null,
                ArticleWorkTestDeliveries.CanonicalFqdn,
                ArticleWorkTestDeliveries.CanonicalListenerPort,
                ArticleWorkTestDeliveries.CanonicalArticleIdHex);
    }
}

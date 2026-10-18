using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Retention
{
    /// <summary>Canonical retain/claim byte identity and request-id cancel semantics.</summary>
    public sealed class ArticleRetentionCanonicalTests
    {
        /// <summary>Fixed UTC anchor for manual time in retain/claim tests.</summary>
        private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        /// <summary>Claim bytes match retained canonical artdata size accounting.</summary>

        [Fact]
        public void RetainCanonical_then_claim_returns_the_retained_bytes()
        {
            var time = new ManualTimeProvider(Start);
            var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.Create("<canonical@example.test>");
            var retained = authority.RetainCanonical(
                prepared.MessageId,
                prepared.RequestId,
                prepared.Record,
                prepared.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.Retained, retained.Kind);

            using var claim = ArticleRetentionAuthorityTests.Claim(authority, prepared.MessageId, prepared.ArtData);
            Assert.Equal(prepared.Record.ArtSize, claim.ArticleBytes.Length);
            Assert.Equal(prepared.Record.ArtSize, authority.RetainedPayloadBytes);
        }
        /// <summary>AlreadyPresent keeps first canonical bytes despite differing second payload.</summary>

        [Fact]
        public void AlreadyPresent_cannot_create_divergent_retained_representations()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var first = RetentionTestArticles.Create("<already@example.test>", "first-canonical\r\n");
            var second = RetentionTestArticles.Create("<already@example.test>", "second-canonical\r\n");
            Assert.NotEqual(first.ArtData, second.ArtData);

            Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
                first.MessageId, first.RequestId, first.Record, first.SelectedDateHeaderName).Kind);
            Assert.Equal(ArticleRetentionKind.AlreadyPresent, authority.RetainCanonical(
                second.MessageId, second.RequestId, second.Record, second.SelectedDateHeaderName).Kind);

            Assert.Equal(first.Record.ArtSize, authority.RetainedPayloadBytes);
            Assert.Equal(1, authority.RetainedCount);

            using var claim = ArticleRetentionAuthorityTests.Claim(authority, first.MessageId, first.ArtData);
            Assert.False(claim.ArticleBytes.Span.SequenceEqual(second.ArtData.AsSpan()));
            Assert.Equal(first.Record.ArtSize, authority.RetainedPayloadBytes);
        }
        /// <summary>RetainedPayloadBytes equals claimed buffer length.</summary>

        [Fact]
        public void Retention_byte_accounting_equals_claimed_artdata()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<bytes@example.test>", "accounted\r\n");
            Assert.Equal(prepared.Record.ArtSize, authority.RetainedPayloadBytes);

            using var claim = ArticleRetentionAuthorityTests.Claim(authority, prepared.MessageId, prepared.ArtData);
            Assert.Equal(claim.ArticleBytes.Length, authority.RetainedPayloadBytes);
            Assert.Equal(prepared.Record.ArtSize, claim.ArticleBytes.Length);
        }
        /// <summary>TTL expiry clears bytes and count; claim NotFound.</summary>

        [Fact]
        public void Expired_entries_are_released_and_claim_is_rejected()
        {
            var time = new ManualTimeProvider(Start);
            var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(30));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<expired@example.test>");
            Assert.Equal(prepared.Record.ArtSize, authority.RetainedPayloadBytes);

            time.Advance(TimeSpan.FromSeconds(30));
            ArticleRetentionAuthorityTests.AssertNotFound(authority, prepared.MessageId);
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, authority.RetainedCount);
        }
        /// <summary>Second request id is AlreadyPresent with same ArtId.</summary>

        [Fact]
        public void Multiple_request_ids_keep_the_first_article()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var first = RetentionTestArticles.Create("<reqids@example.test>", "body-a\r\n");
            var second = RetentionTestArticles.Create("<reqids@example.test>", "body-b\r\n");
            Assert.Equal(first.Record.ArtId, second.Record.ArtId);

            Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
                first.MessageId, first.RequestId, first.Record, first.SelectedDateHeaderName).Kind);
            Assert.Equal(ArticleRetentionKind.AlreadyPresent, authority.RetainCanonical(
                second.MessageId, second.RequestId, second.Record, second.SelectedDateHeaderName).Kind);

            using var claim = ArticleRetentionAuthorityTests.Claim(authority, first.MessageId, first.ArtData);
            Assert.True(authority.TryCancelPendingRequest(first.RequestId));
            Assert.True(authority.TryCancelPendingRequest(second.RequestId));
        }
        /// <summary>Cancel succeeds once per request id; claim still works.</summary>

        [Fact]
        public void TryCancelPendingRequest_removes_only_that_capability()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<cancel-pending@example.test>");

            Assert.True(authority.TryCancelPendingRequest(prepared.RequestId));
            Assert.False(authority.TryCancelPendingRequest(prepared.RequestId));
            using var claim = ArticleRetentionAuthorityTests.Claim(authority, prepared.MessageId, prepared.ArtData);
        }
    }
}

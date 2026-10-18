using System.Runtime.InteropServices;
using System.Text;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Retention
{
    /// <summary>
    /// Delivery claim commit/rollback, TTL interaction, framing rules, and FIFO pin during claim.
    /// </summary>
    public sealed class ArticleDeliveryClaimTests
    {
        /// <summary>Fixed clock anchor for TTL and expiry scenarios.</summary>
        private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        /// <summary>Commit after claim removes retention entry and subtracts bytes exactly once.</summary>

        [Fact]
        public void Commit_removes_the_entry_and_subtracts_bytes_once()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var before = authority.RetainedPayloadBytes;

            var claimed = authority.TryClaimForDelivery(Id("<a@b>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, claimed.Kind);
            var claim = claimed.Claim;
            Assert.NotNull(claim);
            Assert.True(claim!.ArticleBytes.Span.SequenceEqual(prepared.Record.ArtData.Span));
            Assert.Equal("<a@b>", claim.MessageId);
            Assert.Equal(before, authority.RetainedPayloadBytes);
            Assert.Equal(1, authority.RetainedCount);

            claim.Commit();
            claim.Dispose();

            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(ArticleDeliveryClaimKind.NotFound, authority.TryClaimForDelivery(Id("<a@b>")).Kind);
            Assert.False(authority.TryCancelPendingRequest(prepared.RequestId));
        }
        /// <summary>Dispose without commit leaves bytes and allows a second claim.</summary>

        [Fact]
        public void Rollback_leaves_an_unexpired_entry_claimable()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var before = authority.RetainedPayloadBytes;

            var first = authority.TryClaimForDelivery(Id("<a@b>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, first.Kind);
            first.Claim!.Dispose();
            first.Claim.Dispose();

            Assert.Equal(before, authority.RetainedPayloadBytes);
            Assert.Equal(1, authority.RetainedCount);
            var second = authority.TryClaimForDelivery(Id("<a@b>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, second.Kind);
            Assert.True(second.Claim!.ArticleBytes.Span.SequenceEqual(prepared.Record.ArtData.Span));
            second.Claim.Dispose();
        }
        /// <summary>Commit clears all request-id capabilities tied to the message id.</summary>

        [Fact]
        public void Commit_clears_every_request_id_on_the_entry()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var first = RetentionTestArticles.Create("<a@b>", "one\r\n");
            var second = RetentionTestArticles.Create("<a@b>", "two-bytes\r\n");
            Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
                "<a@b>", first.RequestId, first.Record, first.SelectedDateHeaderName).Kind);
            Assert.Equal(ArticleRetentionKind.AlreadyPresent, authority.RetainCanonical(
                "<a@b>", second.RequestId, second.Record, second.SelectedDateHeaderName).Kind);

            var claimed = authority.TryClaimForDelivery(Id("<a@b>"));
            claimed.Claim!.Commit();

            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.False(authority.TryCancelPendingRequest(first.RequestId));
            Assert.False(authority.TryCancelPendingRequest(second.RequestId));
        }
        /// <summary>Concurrent delivery returns InDelivery until the first claim is disposed.</summary>

        [Fact]
        public void A_second_claim_is_rejected_until_the_first_is_released()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var first = authority.TryClaimForDelivery(Id("<a@b>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, first.Kind);

            var second = authority.TryClaimForDelivery(Id("<a@b>"));
            Assert.Equal(ArticleDeliveryClaimKind.InDelivery, second.Kind);
            Assert.Null(second.Claim);
            Assert.Equal(1, authority.RetainedCount);
            Assert.True(authority.TryCancelPendingRequest(prepared.RequestId));

            first.Claim!.Dispose();
            var third = authority.TryClaimForDelivery(Id("<a@b>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, third.Kind);
            Assert.True(third.Claim!.ArticleBytes.Span.SequenceEqual(prepared.Record.ArtData.Span));
            third.Claim.Dispose();
        }
        /// <summary>TTL sweep during claim zeroes authority bytes; commit does not restore index.</summary>

        [Fact]
        public void Ttl_during_a_claim_keeps_the_captured_bytes_and_does_not_resurrect_the_entry()
        {
            var time = new ManualTimeProvider(Start);
            var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var claimed = authority.TryClaimForDelivery(Id("<a@b>"));
            var captured = claimed.Claim!.ArticleBytes.ToArray();

            time.Advance(TimeSpan.FromSeconds(10));
            Assert.True(authority.SweepExpired() > 0);
            Assert.True(claimed.Claim.ArticleBytes.Span.SequenceEqual(captured));
            Assert.Equal(0, authority.RetainedPayloadBytes);

            claimed.Claim.Commit();
            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(ArticleDeliveryClaimKind.NotFound, authority.TryClaimForDelivery(Id("<a@b>")).Kind);
            Assert.True(claimed.Claim.ArticleBytes.Span.SequenceEqual(prepared.Record.ArtData.Span));
        }
        /// <summary>Dispose after TTL sweep leaves NotFound and zero counts.</summary>

        [Fact]
        public void Rollback_after_ttl_does_not_restore_the_entry()
        {
            var time = new ManualTimeProvider(Start);
            var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var claim = authority.TryClaimForDelivery(Id("<a@b>")).Claim;
            time.Advance(TimeSpan.FromSeconds(10));
            Assert.True(authority.SweepExpired() > 0);

            claim!.Dispose();

            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(ArticleDeliveryClaimKind.NotFound, authority.TryClaimForDelivery(Id("<a@b>")).Kind);
        }
        /// <summary>Open claim blocks FIFO reclaim until released.</summary>

        [Fact]
        public void An_active_claim_pins_fifo_reclaim_after_the_last_request_id_is_cancelled()
        {
            var time = new ManualTimeProvider(Start);
            var first = RetentionTestArticles.Create("<a@b>", "same-body\r\n");
            var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: first.Record.ArtSize);
            Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
                "<a@b>", first.RequestId, first.Record, first.SelectedDateHeaderName).Kind);
            Assert.True(authority.TryCancelPendingRequest(first.RequestId));

            var claim = authority.TryClaimForDelivery(Id("<a@b>")).Claim;
            Assert.NotNull(claim);
            var second = RetentionTestArticles.Create("<c@d>", "same-body\r\n");
            var blocked = authority.RetainCanonical(
                "<c@d>", second.RequestId, second.Record, second.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.CapacityUnavailable, blocked.Kind);
            Assert.Equal(first.Record.ArtSize, authority.RetainedPayloadBytes);
            Assert.Equal(1, authority.RetainedCount);

            claim!.Dispose();
            var admitted = authority.RetainCanonical(
                "<c@d>", second.RequestId, second.Record, second.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.Retained, admitted.Kind);
            Assert.Equal(second.Record.ArtSize, authority.RetainedPayloadBytes);
            Assert.Equal(ArticleDeliveryClaimKind.NotFound, authority.TryClaimForDelivery(Id("<a@b>")).Kind);
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, authority.TryClaimForDelivery(Id("<c@d>")).Kind);
        }
        /// <summary>Authority dispose during claim keeps captured bytes; further claims throw ObjectDisposedException.</summary>

        [Fact]
        public async Task Dispose_during_a_claim_does_not_resurrect_or_double_release()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var claim = authority.TryClaimForDelivery(Id("<a@b>")).Claim;
            var captured = claim!.ArticleBytes.ToArray();

            await authority.DisposeAsync();
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, authority.RetainedCount);
            Assert.True(claim.ArticleBytes.Span.SequenceEqual(captured));

            claim.Commit();
            claim.Dispose();
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.True(claim.ArticleBytes.Span.SequenceEqual(prepared.Record.ArtData.Span));
            Assert.Throws<ObjectDisposedException>(() => authority.TryClaimForDelivery(Id("<a@b>")));
        }
        /// <summary>BeginShutdown does not block delivery claim on existing retained artdata.</summary>

        [Fact]
        public void Begin_shutdown_still_allows_a_claim()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            authority.BeginShutdown();
            var claimed = authority.TryClaimForDelivery(Id("<a@b>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, claimed.Kind);
            claimed.Claim!.Dispose();
        }
        /// <summary>Non-CRLF-terminated retained payload returns NotFrameable without mutating store.</summary>

        [Fact]
        public void An_article_that_does_not_end_in_crlf_is_not_claimed()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>", "unterminated");
            Assert.False(prepared.Record.ArtData.Span.EndsWith("\r\n"u8));
            var before = prepared.Record.ArtData.ToArray();

            var claimed = authority.TryClaimForDelivery(Id("<a@b>"));
            Assert.Equal(ArticleDeliveryClaimKind.NotFrameable, claimed.Kind);
            Assert.Null(claimed.Claim);
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(before.Length, authority.RetainedPayloadBytes);
            Assert.True(prepared.Record.ArtData.Span.SequenceEqual(before));
            Assert.True(authority.TryCancelPendingRequest(prepared.RequestId));
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(before.Length, authority.RetainedPayloadBytes);
        }
        /// <summary>Interior bare CR/LF yields NotFrameable while retention bytes stay unchanged.</summary>

        [Theory]
        [InlineData("plain\nmid.dot\r\n")]
        [InlineData("plain\rmid.dot\r\n")]
        public void Interior_bare_cr_or_lf_is_not_frameable_when_the_article_ends_in_crlf(string body)
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>", body);
            var before = prepared.Record.ArtData.ToArray();
            Assert.True(before.AsSpan().EndsWith("\r\n"u8));

            var claimed = authority.TryClaimForDelivery(Id("<a@b>"));
            Assert.Equal(ArticleDeliveryClaimKind.NotFrameable, claimed.Kind);
            Assert.Null(claimed.Claim);
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(before.Length, authority.RetainedPayloadBytes);
            Assert.True(prepared.Record.ArtData.Span.SequenceEqual(before));

            var again = authority.TryClaimForDelivery(Id("<a@b>"));
            Assert.Equal(ArticleDeliveryClaimKind.NotFrameable, again.Kind);
            Assert.Equal(before.Length, authority.RetainedPayloadBytes);

            var other = RetentionTestArticles.RetainPrepared(authority, "<c@d>", "valid\r\n");
            var delivered = authority.TryClaimForDelivery(Id("<c@d>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, delivered.Kind);
            Assert.True(delivered.Claim!.ArticleBytes.Span.SequenceEqual(other.Record.ArtData.Span));
            delivered.Claim.Dispose();
            Assert.Equal(2, authority.RetainedCount);
        }
        /// <summary>Valid CRLF framing returns Claimed bytes identical to stored artdata.</summary>

        [Theory]
        [InlineData("alpha\r\nbeta\r\n")]
        [InlineData("\r\n")]
        [InlineData("above\r\n\r\nbelow\r\n")]
        public void Crlf_lines_and_empty_lines_can_be_claimed_without_rewriting_bytes(string body)
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>", body);
            var before = prepared.Record.ArtData.ToArray();
            Assert.True(before.AsSpan().EndsWith("\r\n"u8));

            var claimed = authority.TryClaimForDelivery(Id("<a@b>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, claimed.Kind);
            Assert.True(claimed.Claim!.ArticleBytes.Span.SequenceEqual(before));
            Assert.True(prepared.Record.ArtData.Span.SequenceEqual(before));
            claimed.Claim.Dispose();
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(before.Length, authority.RetainedPayloadBytes);
        }
        /// <summary>Commit at TTL boundary removes entry once without double byte subtraction.</summary>

        [Fact]
        public void Commit_at_exact_expiry_without_a_sweep_removes_the_entry_once()
        {
            SettleAtExactExpiry(commit: true);
        }
        /// <summary>Rollback at TTL boundary removes entry once; idempotent dispose/commit.</summary>

        [Fact]
        public void Rollback_at_exact_expiry_without_a_sweep_removes_the_entry_once()
        {
            SettleAtExactExpiry(commit: false);
        }

        /// <summary>Shared exact-TTL boundary scenario; commit or rollback via <paramref name="commit"/>.</summary>
        private static void SettleAtExactExpiry(bool commit)
        {
            var time = new ManualTimeProvider(Start);
            var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var sibling = Guid.NewGuid();
            Assert.Equal(ArticleRetentionKind.AlreadyPresent, authority.RetainCanonical(
                "<a@b>", sibling, prepared.Record, prepared.SelectedDateHeaderName).Kind);
            var claimed = authority.TryClaimForDelivery(Id("<a@b>"));
            var claim = claimed.Claim;
            Assert.NotNull(claim);
            var captured = claim!.ArticleBytes.ToArray();

            time.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(prepared.Record.ArtSize, authority.RetainedPayloadBytes);
            if (commit)
            {
                claim.Commit();
            }

            claim.Dispose();
            claim.Dispose();
            if (commit)
            {
                claim.Commit();
            }

            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(ArticleDeliveryClaimKind.NotFound, authority.TryClaimForDelivery(Id("<a@b>")).Kind);
            Assert.False(authority.TryCancelPendingRequest(prepared.RequestId));
            Assert.False(authority.TryCancelPendingRequest(sibling));
            Assert.Equal(0, authority.SweepExpired());
            Assert.True(claim.ArticleBytes.Span.SequenceEqual(captured));
            Assert.True(claim.ArticleBytes.Span.SequenceEqual(prepared.Record.ArtData.Span));
        }

        /// <summary>
        /// A later admission of the same Message-ID keeps the first entry, including an unframeable bit.
        /// </summary>
        [Fact]
        public void AlreadyPresent_keeps_the_admitted_frameability()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            RetentionTestArticles.RetainPrepared(authority, "<a@b>", "plain\nmid");
            var replacement = RetentionTestArticles.Create("<a@b>");

            var admitted = authority.RetainCanonical(
                replacement.MessageId,
                replacement.RequestId,
                replacement.Record,
                replacement.SelectedDateHeaderName);

            Assert.Equal(ArticleRetentionKind.AlreadyPresent, admitted.Kind);
            var claimed = authority.TryClaimForDelivery(Id("<a@b>"));
            Assert.Equal(ArticleDeliveryClaimKind.NotFrameable, claimed.Kind);
            Assert.Null(claimed.Claim);
            Assert.Equal(1, authority.RetainedCount);
        }

        /// <summary>
        /// Byte values above 127 map to the same char the dictionary stored. UTF-8 of that char does not.
        /// </summary>
        [Fact]
        public void Delivery_key_maps_each_octet_to_the_same_char_without_case_folding()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.Create("<a@b>");
            const string key = "\u00E9";
            Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
                key, prepared.RequestId, prepared.Record, prepared.SelectedDateHeaderName).Kind);

            var mapped = authority.TryClaimForDelivery([(byte)key[0]]);
            Assert.Equal(ArticleDeliveryClaimKind.NotFrameable, mapped.Kind);
            Assert.Null(mapped.Claim);

            var utf8 = authority.TryClaimForDelivery(Encoding.UTF8.GetBytes(key));
            Assert.Equal(ArticleDeliveryClaimKind.NotFound, utf8.Kind);

            var ascii = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            RetentionTestArticles.RetainPrepared(ascii, "<a@b>");
            Assert.Equal(ArticleDeliveryClaimKind.NotFound, ascii.TryClaimForDelivery(Id("<A@B>")).Kind);
            var exact = ascii.TryClaimForDelivery(Id("<a@b>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, exact.Kind);
            exact.Claim!.Dispose();
        }

        /// <summary>Two callers cannot both own the same entry.</summary>
        [Fact]
        public async Task Concurrent_callers_cannot_both_claim_one_entry()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            using var started = new Barrier(2);
            var kinds = new ArticleDeliveryClaimKind[2];
            var claims = new ArticleRetentionAuthority.ArticleDeliveryClaim?[2];
            var tasks = new Task[2];
            for (var i = 0; i < 2; i++)
            {
                var index = i;
                tasks[i] = Task.Run(() =>
                {
                    started.SignalAndWait(TimeSpan.FromSeconds(5));
                    var result = authority.TryClaimForDelivery(Id("<a@b>"));
                    kinds[index] = result.Kind;
                    claims[index] = result.Claim;
                });
            }

            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, kinds.Count(kind => kind == ArticleDeliveryClaimKind.Claimed));
            Assert.Equal(1, kinds.Count(kind => kind == ArticleDeliveryClaimKind.InDelivery));
            Assert.Equal(1, claims.Count(claim => claim is not null));
            foreach (var claim in claims)
            {
                claim?.Dispose();
            }
        }

        /// <summary>
        /// A byte changed after admission is still rejected, and the failed scan does not keep the claim.
        /// </summary>
        [Fact]
        public void A_post_admission_mutation_is_rejected_and_releases_the_claim()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            Assert.True(MemoryMarshal.TryGetArray(prepared.Record.ArtData, out ArraySegment<byte> segment));
            var body = prepared.Record.ArtData.Span.IndexOf("body\r\n"u8);
            Assert.True(body > 0);
            segment.Array![segment.Offset + body] = 0;

            var first = authority.TryClaimForDelivery(Id("<a@b>"));
            var second = authority.TryClaimForDelivery(Id("<a@b>"));

            Assert.Equal(ArticleDeliveryClaimKind.NotFrameable, first.Kind);
            Assert.Null(first.Claim);
            Assert.Equal(ArticleDeliveryClaimKind.NotFrameable, second.Kind);
            Assert.Null(second.Claim);
            Assert.Equal(1, authority.RetainedCount);
        }

        /// <summary>
        /// The Message-ID key is built before the retention lock, and the article scan is after the claim is published.
        /// </summary>
        [Fact]
        public void TryClaimForDelivery_scans_only_after_the_claim_lock_is_released()
        {
            var source = File.ReadAllText(FindAuthoritySource());
            var start = source.IndexOf("public ArticleDeliveryClaimResult TryClaimForDelivery", StringComparison.Ordinal);
            var end = source.IndexOf("private void CompleteDelivery", start, StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start);
            var method = source[start..end];
            var key = method.IndexOf("CreateDeliveryKey", StringComparison.Ordinal);
            var gate = method.IndexOf("lock (_gate)", StringComparison.Ordinal);
            var published = method.IndexOf("_deliveryClaims.Add", StringComparison.Ordinal);
            var scan = method.IndexOf("CanonicalArticleFraming.Analyze(", StringComparison.Ordinal);
            Assert.True(key >= 0 && gate > key);
            Assert.True(published > gate && scan > published);
            Assert.DoesNotContain("CanonicalArticleFraming.Analyze(", method[..published], StringComparison.Ordinal);
        }

        /// <summary>Locates the retention authority source from the test output directory.</summary>
        private static string FindAuthoritySource()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = string.Concat(
                    directory.FullName,
                    Path.DirectorySeparatorChar,
                    "src",
                    Path.DirectorySeparatorChar,
                    "VectorNNTPD.BackFillerService",
                    Path.DirectorySeparatorChar,
                    "Retention",
                    Path.DirectorySeparatorChar,
                    "ArticleRetentionAuthority.cs");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("ArticleRetentionAuthority.cs was not found.");
        }

        /// <summary>ASCII message-id octets for TryClaimForDelivery.</summary>
        private static byte[] Id(string messageId) => Encoding.ASCII.GetBytes(messageId);
    }
}

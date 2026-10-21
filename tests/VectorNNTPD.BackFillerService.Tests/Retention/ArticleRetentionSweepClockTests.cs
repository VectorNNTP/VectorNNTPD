using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Retention
{
    /// <summary>
    /// Retention TTL sweep behaviour when wall-clock admission timestamps are not monotonic (issue #189).
    /// </summary>
    public sealed class ArticleRetentionSweepClockTests
    {
        /// <summary>Fixed UTC anchor for explicit expiry arithmetic.</summary>
        private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

        /// <summary>
        /// Issue #189: after a backward clock step, a later-admitted entry can expire before an earlier
        /// insertion. The sweep must remove the expired later entry without waiting for the earlier one.
        /// </summary>
        [Fact]
        public async Task Sweep_removes_later_expired_entry_after_backward_clock_step()
        {
            var ttl = TimeSpan.FromHours(1);
            var time = new ManualTimeProvider(Start);
            await using var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: ttl);

            var articleA = RetentionTestArticles.RetainPrepared(authority, "<a-189@example.test>", "aaaa\r\n");
            time.Advance(TimeSpan.FromMinutes(30));
            var articleB = RetentionTestArticles.RetainPrepared(authority, "<b-189@example.test>", "bbbbbb\r\n");

            // Backward step so C is admitted earlier on the clock than A, but later in insertion order.
            time.SetUtcNow(Start.AddMinutes(-30));
            var articleC = RetentionTestArticles.RetainPrepared(authority, "<c-189@example.test>", "cc\r\n");

            // C expires at Start+30m; A expires at Start+1h; B expires at Start+90m.
            time.SetUtcNow(Start.AddMinutes(30));
            var beforeBytes = authority.RetainedPayloadBytes;
            Assert.Equal(
                articleA.Record.ArtSize + articleB.Record.ArtSize + articleC.Record.ArtSize,
                beforeBytes);

            var released = authority.SweepExpired();

            Assert.Equal(articleC.Record.ArtSize, released);
            Assert.Equal(beforeBytes - articleC.Record.ArtSize, authority.RetainedPayloadBytes);
            Assert.Equal(2, authority.RetainedCount);
            ArticleRetentionAuthorityTests.AssertNotFound(authority, "<c-189@example.test>");
            using var claimA = ArticleRetentionAuthorityTests.Claim(authority, "<a-189@example.test>", articleA.ArtData);
            using var claimB = ArticleRetentionAuthorityTests.Claim(authority, "<b-189@example.test>", articleB.ArtData);
        }

        /// <summary>
        /// One sweep removes every expired entry even when unexpired neighbors sit between them in insertion order.
        /// Admission times are chosen so no intermediate retain expires the drop entries early via reclaim.
        /// </summary>
        [Fact]
        public async Task Sweep_removes_multiple_expired_entries_separated_by_unexpired_neighbors()
        {
            var ttl = TimeSpan.FromMinutes(10);
            var time = new ManualTimeProvider(Start);
            await using var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: ttl);

            var keepEarly = RetentionTestArticles.RetainPrepared(authority, "<keep-early@example.test>", "keep1\r\n");
            // dropFirst expires at Start+5m; still live when keepMiddle is admitted at Start.
            time.SetUtcNow(Start.AddMinutes(-5));
            var dropFirst = RetentionTestArticles.RetainPrepared(authority, "<drop-first@example.test>", "d1\r\n");
            time.SetUtcNow(Start);
            var keepMiddle = RetentionTestArticles.RetainPrepared(authority, "<keep-middle@example.test>", "keep22\r\n");
            time.SetUtcNow(Start.AddMinutes(-3));
            var dropSecond = RetentionTestArticles.RetainPrepared(authority, "<drop-second@example.test>", "d22\r\n");

            // dropFirst at Start+5m, dropSecond at Start+7m, keep* at Start+10m.
            time.SetUtcNow(Start.AddMinutes(7));
            var beforeBytes = authority.RetainedPayloadBytes;
            var expectedReleased = dropFirst.Record.ArtSize + dropSecond.Record.ArtSize;
            Assert.Equal(
                keepEarly.Record.ArtSize + keepMiddle.Record.ArtSize + expectedReleased,
                beforeBytes);

            var released = authority.SweepExpired();

            Assert.Equal(expectedReleased, released);
            Assert.Equal(beforeBytes - expectedReleased, authority.RetainedPayloadBytes);
            Assert.Equal(2, authority.RetainedCount);
            ArticleRetentionAuthorityTests.AssertNotFound(authority, "<drop-first@example.test>");
            ArticleRetentionAuthorityTests.AssertNotFound(authority, "<drop-second@example.test>");
            using var claimKeepEarly = ArticleRetentionAuthorityTests.Claim(
                authority,
                "<keep-early@example.test>",
                keepEarly.ArtData);
            using var claimKeepMiddle = ArticleRetentionAuthorityTests.Claim(
                authority,
                "<keep-middle@example.test>",
                keepMiddle.ArtData);
            Assert.Equal(0, authority.SweepExpired());
        }

        /// <summary>An entry whose ExpiresUtc equals the sweep clock is removed; a still-future entry is kept.</summary>
        [Fact]
        public async Task Sweep_expires_at_exact_deadline_and_retains_future_entries()
        {
            var ttl = TimeSpan.FromSeconds(10);
            var time = new ManualTimeProvider(Start);
            await using var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: ttl);

            var due = RetentionTestArticles.RetainPrepared(authority, "<due@example.test>", "due\r\n");
            time.Advance(TimeSpan.FromSeconds(5));
            var future = RetentionTestArticles.RetainPrepared(authority, "<future@example.test>", "future\r\n");

            time.SetUtcNow(Start.AddSeconds(10));
            var released = authority.SweepExpired();

            Assert.Equal(due.Record.ArtSize, released);
            Assert.Equal(future.Record.ArtSize, authority.RetainedPayloadBytes);
            Assert.Equal(1, authority.RetainedCount);
            ArticleRetentionAuthorityTests.AssertNotFound(authority, "<due@example.test>");
            using var claim = ArticleRetentionAuthorityTests.Claim(authority, "<future@example.test>", future.ArtData);
        }

        /// <summary>
        /// FIFO capacity reclaim still evicts the oldest unpinned insertion, not the earliest ExpiresUtc.
        /// </summary>
        [Fact]
        public async Task Fifo_reclaim_still_follows_insertion_order_after_backward_clock_admissions()
        {
            var ttl = TimeSpan.FromHours(1);
            var time = new ManualTimeProvider(Start);
            var one = RetentionTestArticles.Create("<fifo-one@example.test>", "11111\r\n");
            var two = RetentionTestArticles.Create("<fifo-two@example.test>", "22222\r\n");
            var three = RetentionTestArticles.Create("<fifo-three@example.test>", "333\r\n");
            var maxBytes = one.Record.ArtSize + two.Record.ArtSize;
            await using var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: maxBytes, ttl: ttl);

            Assert.Equal(
                ArticleRetentionKind.Retained,
                authority.RetainCanonical(one.MessageId, one.RequestId, one.Record, one.SelectedDateHeaderName).Kind);

            time.SetUtcNow(Start.AddMinutes(-30));
            Assert.Equal(
                ArticleRetentionKind.Retained,
                authority.RetainCanonical(two.MessageId, two.RequestId, two.Record, two.SelectedDateHeaderName).Kind);

            // Unpin oldest insertion (one). Two stays pinned by its openable RequestId.
            Assert.True(authority.TryCancelPendingRequest(one.RequestId));

            time.SetUtcNow(Start);
            var third = authority.RetainCanonical(
                three.MessageId,
                three.RequestId,
                three.Record,
                three.SelectedDateHeaderName);

            Assert.Equal(ArticleRetentionKind.Retained, third.Kind);
            ArticleRetentionAuthorityTests.AssertNotFound(authority, "<fifo-one@example.test>");
            using var stillHeld = ArticleRetentionAuthorityTests.Claim(authority, "<fifo-two@example.test>", two.ArtData);
            Assert.Equal(two.Record.ArtSize + three.Record.ArtSize, authority.RetainedPayloadBytes);
        }
    }
}

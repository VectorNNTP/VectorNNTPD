using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using System.Text;

namespace VectorNNTPD.BackFillerService.Tests.Retention
{
    /// <summary>
    /// ArticleRetentionAuthority retain, capacity, TTL, shutdown, and concurrency invariants.
    /// </summary>
    public sealed class ArticleRetentionAuthorityTests
    {
        /// <summary>Fixed clock anchor for ManualTimeProvider in retention tests.</summary>
        private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        /// <summary>Retained canonical exposes fqdn, listener port, identity, and claimable artdata.</summary>

        [Fact]
        public void RetainCanonical_then_open_exposes_owned_artdata_and_endpoint()
        {
            var time = new ManualTimeProvider(Start);
            var authority = Create(time, maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.Create("<a@b>");
            var retained = authority.RetainCanonical(
                "<a@b>",
                prepared.RequestId,
                prepared.Record,
                prepared.SelectedDateHeaderName);

            Assert.Equal(ArticleRetentionKind.Retained, retained.Kind);
            Assert.Equal("backfiller.test", retained.Fqdn);
            Assert.Equal(1190, retained.ListenerPort);
            Assert.Equal(prepared.Record.ArtId.ToLowerHexString(), retained.Identity!.Value.ArticleIdHex);
            Assert.Equal(prepared.Record.ArtSize, authority.RetainedPayloadBytes);
            Assert.Equal(1, authority.RetainedCount);

            using var claim = Claim(authority, "<a@b>", prepared.ArtData);
            Assert.Equal("<a@b>", claim.MessageId);
        }
        /// <summary>Second retain is AlreadyPresent with first-wins artdata and single byte count.</summary>

        [Fact]
        public void Duplicate_message_id_is_first_wins_and_does_not_double_count()
        {
            var authority = Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var first = RetentionTestArticles.Create("<a@b>", "one\r\n");
            var second = RetentionTestArticles.Create("<a@b>", "two-bytes\r\n");
            Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
                "<a@b>", first.RequestId, first.Record, first.SelectedDateHeaderName).Kind);
            var duplicate = authority.RetainCanonical(
                "<a@b>", second.RequestId, second.Record, second.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.AlreadyPresent, duplicate.Kind);
            Assert.Equal(first.Record.ArtSize, authority.RetainedPayloadBytes);
            Assert.Equal(1, authority.RetainedCount);

            using var claim = Claim(authority, "<a@b>", first.ArtData);
            Assert.False(claim.ArticleBytes.Span.SequenceEqual(second.ArtData.AsSpan()));
        }
        /// <summary>After TTL, claim is NotFound and sweep returns zero.</summary>

        [Fact]
        public void Claim_after_ttl_is_rejected_and_sweep_finds_nothing()
        {
            var time = new ManualTimeProvider(Start);
            var authority = Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            _ = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            time.Advance(TimeSpan.FromSeconds(10));
            AssertNotFound(authority, "<a@b>");

            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, authority.SweepExpired());
            Assert.Equal(0, authority.RetainedCount);
        }
        /// <summary>Re-retain after expiry creates a new generation not removed by prior expiry sweep.</summary>

        [Fact]
        public void Sweep_does_not_remove_a_newer_generation_of_the_same_identity()
        {
            var time = new ManualTimeProvider(Start);
            var authority = Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(5));
            _ = RetentionTestArticles.RetainPrepared(authority, "<a@b>", "old\r\n");
            time.Advance(TimeSpan.FromSeconds(5));
            AssertNotFound(authority, "<a@b>");

            var newer = RetentionTestArticles.RetainPrepared(authority, "<a@b>", "new-payload\r\n");
            Assert.Equal(0, authority.SweepExpired());
            using var claim = Claim(authority, "<a@b>", newer.ArtData);
        }
        /// <summary>InvalidPayload rejection reports existing byte total without mutating retained entries.</summary>

        [Fact]
        public void Invalid_payload_rejection_reports_current_retained_total_and_leaves_state_unchanged()
        {
            var fit = RetentionTestArticles.Create("<fit@b>", "x\r\n");
            var authority = Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
                "<fit@b>", fit.RequestId, fit.Record, fit.SelectedDateHeaderName).Kind);
            Assert.Equal(fit.Record.ArtSize, authority.RetainedPayloadBytes);

            var invalidSource = RetentionTestArticles.Create("<invalid@b>", "invalid-candidate\r\n");
            Assert.NotEqual(invalidSource.Record.ArtSize, fit.Record.ArtSize);
            var invalid = CreateRecordWithParseStatus(invalidSource.Record, ArticleParseStatus.None);

            var rejected = authority.RetainCanonical(
                "<invalid@b>", invalidSource.RequestId, invalid, invalidSource.SelectedDateHeaderName);

            Assert.Equal(ArticleRetentionKind.InvalidPayload, rejected.Kind);
            Assert.Null(rejected.Fqdn);
            Assert.Null(rejected.ListenerPort);
            Assert.Equal(fit.Record.ArtSize, rejected.RetainedPayloadBytes);
            Assert.Equal(0, rejected.ReleasedPayloadBytes);
            Assert.Equal(fit.Record.ArtSize, authority.RetainedPayloadBytes);
            Assert.Equal(1, authority.RetainedCount);
            using var claim = Claim(authority, "<fit@b>", fit.ArtData);
        }
        /// <summary>PayloadExceedsCapacity leaves prior retains intact.</summary>

        [Fact]
        public void Payload_exceeds_capacity_rejection_reports_current_retained_total_and_leaves_state_unchanged()
        {
            var fit = RetentionTestArticles.Create("<fit-cap@b>", "x\r\n");
            var authority = Create(new ManualTimeProvider(Start), maxBytes: fit.Record.ArtSize + 1);
            Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
                "<fit-cap@b>", fit.RequestId, fit.Record, fit.SelectedDateHeaderName).Kind);
            Assert.Equal(fit.Record.ArtSize, authority.RetainedPayloadBytes);

            var over = RetentionTestArticles.Create("<big@b>", "longer-body\r\n");
            Assert.True(over.Record.ArtSize > fit.Record.ArtSize + 1);
            Assert.NotEqual(over.Record.ArtSize, fit.Record.ArtSize);
            var rejected = authority.RetainCanonical(
                "<big@b>", over.RequestId, over.Record, over.SelectedDateHeaderName);

            Assert.Equal(ArticleRetentionKind.PayloadExceedsCapacity, rejected.Kind);
            Assert.True(rejected.IsCapacityRejected);
            Assert.Null(rejected.Fqdn);
            Assert.Null(rejected.ListenerPort);
            Assert.Equal(fit.Record.ArtSize, rejected.RetainedPayloadBytes);
            Assert.Equal(0, rejected.ReleasedPayloadBytes);
            Assert.Equal(fit.Record.ArtSize, authority.RetainedPayloadBytes);
            Assert.Equal(1, authority.RetainedCount);
            using var claim = Claim(authority, "<fit-cap@b>", fit.ArtData);
        }
        /// <summary>FIFO reclaims unpinned oldest to admit a third entry while pinned remains.</summary>

        [Fact]
        public void Insufficient_remaining_capacity_reclaims_unpinned_oldest_then_admits()
        {
            var one = RetentionTestArticles.Create("<one@b>", "11111\r\n");
            var two = RetentionTestArticles.Create("<two@b>", "22222\r\n");
            var three = RetentionTestArticles.Create("<three@b>", "333\r\n");
            var maxBytes = one.Record.ArtSize + two.Record.ArtSize;
            var authority = Create(new ManualTimeProvider(Start), maxBytes: maxBytes);
            Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
                "<one@b>", one.RequestId, one.Record, one.SelectedDateHeaderName).Kind);
            Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
                "<two@b>", two.RequestId, two.Record, two.SelectedDateHeaderName).Kind);
            // Unpin oldest so FIFO reclaim may take it; two stays pinned by its Success RequestId.
            Assert.True(authority.TryCancelPendingRequest(one.RequestId));

            var third = authority.RetainCanonical(
                "<three@b>", three.RequestId, three.Record, three.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.Retained, third.Kind);
            AssertNotFound(authority, "<one@b>");
            using var stillHeld = Claim(authority, "<two@b>", two.ArtData);

            Assert.Equal(two.Record.ArtSize + three.Record.ArtSize, authority.RetainedPayloadBytes);
        }
        /// <summary>All pinned entries yield CapacityUnavailable without eviction.</summary>

        [Fact]
        public void Insufficient_remaining_capacity_with_all_pinned_rejects_without_evicting()
        {
            var one = RetentionTestArticles.Create("<one-pin@b>", "11111\r\n");
            var two = RetentionTestArticles.Create("<two-pin@b>", "22222\r\n");
            var three = RetentionTestArticles.Create("<three-pin@b>", "333\r\n");
            var maxBytes = one.Record.ArtSize + two.Record.ArtSize;
            var authority = Create(new ManualTimeProvider(Start), maxBytes: maxBytes);
            Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
                "<one-pin@b>", one.RequestId, one.Record, one.SelectedDateHeaderName).Kind);
            Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
                "<two-pin@b>", two.RequestId, two.Record, two.SelectedDateHeaderName).Kind);

            var rejected = authority.RetainCanonical(
                "<three-pin@b>", three.RequestId, three.Record, three.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.CapacityUnavailable, rejected.Kind);
            using var claimOne = Claim(authority, "<one-pin@b>", one.ArtData);
            using var claimTwo = Claim(authority, "<two-pin@b>", two.ArtData);
            Assert.Equal(one.Record.ArtSize + two.Record.ArtSize, authority.RetainedPayloadBytes);
        }
        /// <summary>BeginShutdown rejects new retains; existing claim still succeeds.</summary>

        [Fact]
        public void Shutdown_rejects_insert_and_existing_open_still_works_until_dispose()
        {
            var authority = Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var keep = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            authority.BeginShutdown();
            var later = RetentionTestArticles.Create("<b@c>");
            Assert.Equal(ArticleRetentionKind.ShuttingDown, authority.RetainCanonical(
                "<b@c>", later.RequestId, later.Record, later.SelectedDateHeaderName).Kind);
            using var claim = Claim(authority, "<a@b>", keep.ArtData);
        }
        /// <summary>DisposeAsync clears state and throws on subsequent RetainCanonical.</summary>

        [Fact]
        public async Task Dispose_releases_entries_and_rejects_further_use()
        {
            var authority = Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            _ = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            await authority.DisposeAsync();
            var next = RetentionTestArticles.Create("<b@c>");
            Assert.Throws<ObjectDisposedException>(() => authority.RetainCanonical(
                "<b@c>", next.RequestId, next.Record, next.SelectedDateHeaderName));
        }
        /// <summary>Parallel retains and claims preserve total byte accounting.</summary>

        [Fact]
        public async Task Concurrent_insert_and_open_keep_exact_accounting()
        {
            var authority = Create(new ManualTimeProvider(Start), maxBytes: 16 * 1024 * 1024);
            var prepared = Enumerable.Range(0, 32)
                .Select(i => RetentionTestArticles.Create($"<id{i}@b>", $"body-{i}\r\n"))
                .ToArray();
            var inserts = prepared.Select(async item =>
            {
                await Task.Yield();
                return authority.RetainCanonical(
                    item.MessageId,
                    item.RequestId,
                    item.Record,
                    item.SelectedDateHeaderName);
            });
            var results = await Task.WhenAll(inserts);
            Assert.Equal(32, results.Count(static r => r.Kind == ArticleRetentionKind.Retained));
            Assert.Equal(prepared.Sum(static p => p.Record.ArtSize), authority.RetainedPayloadBytes);

            var claims = prepared.Select(async item =>
            {
                await Task.Yield();
                using var claim = Claim(authority, item.MessageId, item.ArtData);
                return claim.MessageId;
            });
            var claimed = await Task.WhenAll(claims);
            Assert.Equal(32, claimed.Length);
        }
        /// <summary>Claim racing TTL expiry returns NotFound with zero bytes.</summary>

        [Fact]
        public async Task Claim_while_expiry_occurs_does_not_return_expired_bytes()
        {
            var time = new ManualTimeProvider(Start);
            var authority = Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(1));
            _ = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var claimTask = Task.Run(async () =>
            {
                started.TrySetResult();
                await release.Task.ConfigureAwait(false);
                return authority.TryClaimForDelivery(Encoding.ASCII.GetBytes("<a@b>"));
            });
            await started.Task;
            time.Advance(TimeSpan.FromSeconds(1));
            release.TrySetResult();
            var result = await claimTask;
            Assert.Equal(ArticleDeliveryClaimKind.NotFound, result.Kind);
            Assert.Equal(0, authority.RetainedPayloadBytes);
        }
        /// <summary>Concurrent same message id retains yield one Retained and exact byte total.</summary>

        [Fact]
        public async Task Duplicate_identity_race_keeps_one_entry_and_exact_bytes()
        {
            var authority = Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var candidates = Enumerable.Range(0, 16)
                .Select(i => RetentionTestArticles.Create("<same@b>", $"body-{i}\r\n"))
                .ToArray();
            var tasks = candidates.Select(async item =>
            {
                await Task.Yield();
                return authority.RetainCanonical(
                    "<same@b>",
                    item.RequestId,
                    item.Record,
                    item.SelectedDateHeaderName);
            });
            var results = await Task.WhenAll(tasks);
            Assert.Equal(1, results.Count(static r => r.Kind == ArticleRetentionKind.Retained));
            Assert.Equal(15, results.Count(static r => r.Kind == ArticleRetentionKind.AlreadyPresent));
            Assert.Equal(1, authority.RetainedCount);
            var winner = Assert.Single(results, static r => r.Kind == ArticleRetentionKind.Retained);
            Assert.Equal(winner.RetainedPayloadBytes, authority.RetainedPayloadBytes);
        }
        /// <summary>Claim during shutdown still serves artdata; new retains rejected.</summary>

        [Fact]
        public void Shutdown_during_open_still_returns_a_live_entry()
        {
            var authority = Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<a@b>");
            using (var claim = Claim(authority, "<a@b>", prepared.ArtData))
            {
                authority.BeginShutdown();
                Assert.True(claim.ArticleBytes.Span.SequenceEqual(prepared.ArtData.AsSpan()));
            }

            var later = RetentionTestArticles.Create("<c@d>");
            Assert.Equal(ArticleRetentionKind.ShuttingDown, authority.RetainCanonical(
                "<c@d>", later.RequestId, later.Record, later.SelectedDateHeaderName).Kind);
        }
        /// <summary>Retention sources must not use blocking wait patterns in hot paths.</summary>

        [Fact]
        public void Production_retention_code_does_not_block_synchronously()
        {
            var directory = FindSourceDirectory();
            foreach (var text in Directory.GetFiles(directory, "*.cs").Select(static file => File.ReadAllText(file)))
            {
                Assert.DoesNotContain("Thread.Sleep", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".GetAwaiter().GetResult()", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".Wait();", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".Result;", text, StringComparison.Ordinal);
            }
        }

        /// <summary>Clones a record with an overridden parse status for invalid-payload scenarios.</summary>
        private static ArticleRecord CreateRecordWithParseStatus(ArticleRecord source, ArticleParseStatus parseStatus) =>
            new(
                source.ArtId,
                source.ArtHash,
                source.ArtType,
                source.ArtLines,
                source.CanonicalUtc,
                parseStatus,
                source.ArtData.ToArray(),
                source.Fields);

        /// <summary>Asserts Claimed and returns the claim when bytes match <paramref name="expected"/>.</summary>
        internal static ArticleRetentionAuthority.ArticleDeliveryClaim Claim(
            ArticleRetentionAuthority authority,
            string messageId,
            ReadOnlyMemory<byte> expected)
        {
            var result = authority.TryClaimForDelivery(Encoding.ASCII.GetBytes(messageId));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, result.Kind);
            Assert.NotNull(result.Claim);
            Assert.True(result.Claim.ArticleBytes.Span.SequenceEqual(expected.Span));
            return result.Claim;
        }

        /// <summary>Asserts TryClaimForDelivery returns NotFound for the message id.</summary>
        internal static void AssertNotFound(ArticleRetentionAuthority authority, string messageId) =>
            Assert.Equal(
                ArticleDeliveryClaimKind.NotFound,
                authority.TryClaimForDelivery(Encoding.ASCII.GetBytes(messageId)).Kind);

        /// <summary>Constructs authority with test endpoint, manual time, and retention limits.</summary>
        internal static ArticleRetentionAuthority Create(
            TimeProvider time,
            int maxBytes,
            TimeSpan? ttl = null,
            TimeSpan? sweep = null,
            int maxOpenableRequestIdsPerArticle = 16) =>
            new(
                new BackFillerArticleRetentionRuntimeOptions(
                    maxBytes,
                    ttl ?? TimeSpan.FromSeconds(60),
                    sweep ?? TimeSpan.FromSeconds(1),
                    maxOpenableRequestIdsPerArticle),
                "backfiller.test",
                1190,
                time,
                NullLogger.Instance);

        /// <summary>Locates src/VectorNNTPD.BackFillerService/Retention for static source scans.</summary>
        private static string FindSourceDirectory()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = RelativePath.Under(directory.FullName, "src", "VectorNNTPD.BackFillerService", "Retention");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate src/VectorNNTPD.BackFillerService/Retention.");
        }
    }
}

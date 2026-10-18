using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Listener;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;

namespace VectorNNTPD.BackFillerService.Tests.Retention
{
    /// <summary>
    /// Proves a rented canonical buffer returns to its pool once, and only after every consumer releases it.
    /// </summary>
    public sealed class CanonicalArticleBufferRetentionTests
    {
        /// <summary>UTC anchor for expiry tests.</summary>
        private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

        /// <summary>
        /// Article Work rents from the handler's pool and does not return that array while retention still holds it.
        /// </summary>
        [Fact]
        public async Task Production_handler_rents_and_keeps_the_buffer_until_retention_releases_it()
        {
            var pool = new TrackingBytePool();
            var authority = ArticleRetentionAuthorityTests.Create(
                new ManualTimeProvider(Start),
                maxBytes: 1024 * 1024);
            var parser = new NntpArticleParser("backfiller.test");
            var handler = new ProviderArticleWorkHandler(
                new PayloadRetriever(ArticleWorkTestArticles.Valid()),
                authority,
                parser,
                sharedConfiguration: null,
                serverId: 0,
                logger: null,
                canonicalBufferPool: pool);

            var result = await handler.HandleAsync(WorkItem(ArticleWorkTestDeliveries.CanonicalMessageId), CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
            Assert.Equal(1, pool.RentCount);
            Assert.Equal(0, pool.ReturnCount);
            Assert.Equal(1, authority.RetainedCount);
            var claimed = authority.TryClaimForDelivery(Encoding.ASCII.GetBytes(ArticleWorkTestDeliveries.CanonicalMessageId));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, claimed.Kind);
            claimed.Claim!.Dispose();
            Assert.Equal(0, pool.ReturnCount);

            await authority.DisposeAsync();
            Assert.Equal(1, pool.ReturnCount);
            Assert.Equal(0, pool.Outstanding);
        }

        /// <summary>A Message-ID mismatch returns the rented buffer and retains nothing.</summary>
        [Fact]
        public async Task Rejected_handler_article_returns_the_rented_buffer()
        {
            var pool = new TrackingBytePool();
            await using var authority = ArticleRetentionAuthorityTests.Create(
                new ManualTimeProvider(Start),
                maxBytes: 1024 * 1024);
            var handler = new ProviderArticleWorkHandler(
                new PayloadRetriever(ArticleWorkTestArticles.Valid()),
                authority,
                new NntpArticleParser("backfiller.test"),
                sharedConfiguration: null,
                serverId: 0,
                logger: null,
                canonicalBufferPool: pool);

            var result = await handler.HandleAsync(WorkItem("<other@example.invalid>"), CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.InvalidArticle, result.Outcome);
            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(1, pool.RentCount);
            Assert.Equal(1, pool.ReturnCount);
            Assert.Equal(0, pool.Outstanding);
        }

        /// <summary>Expiry returns the retained buffer once, and not while a claim still reads it.</summary>
        [Fact]
        public async Task Expiry_returns_the_buffer_once_after_the_claim_settles()
        {
            var pool = new TrackingBytePool();
            var time = new ManualTimeProvider(Start);
            await using var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 2 * 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            var record = Admit(authority, pool, "<expire@example.test>", "body\r\n");
            var image = record.ArtData.ToArray();
            var claimed = authority.TryClaimForDelivery(Encoding.ASCII.GetBytes("<expire@example.test>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, claimed.Kind);
            Assert.True(claimed.Claim!.ArticleBytes.Span.SequenceEqual(image));

            time.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(image.Length, authority.SweepExpired());
            Assert.Equal(0, pool.ReturnCount);
            Assert.True(claimed.Claim.ArticleBytes.Span.SequenceEqual(image));
            Assert.Equal(0, authority.RetainedCount);

            claimed.Claim.Dispose();
            claimed.Claim.Dispose();
            Assert.Equal(1, pool.ReturnCount);
            Assert.Equal(0, pool.Outstanding);
            Assert.True(record.ArtData.IsEmpty);
            Assert.Equal(ArticleDeliveryClaimKind.NotFound, authority.TryClaimForDelivery(Encoding.ASCII.GetBytes("<expire@example.test>")).Kind);
        }

        /// <summary>FIFO reclaim of an unpinned entry returns that entry's buffer once.</summary>
        [Fact]
        public void Fifo_reclaim_returns_the_evicted_buffer_once()
        {
            var pool = new TrackingBytePool();
            var first = Create(pool, "<one@example.test>", "11111\r\n");
            var second = Create(pool, "<two@example.test>", "22222\r\n");
            var third = Create(pool, "<three@example.test>", "333\r\n");
            var authority = ArticleRetentionAuthorityTests.Create(
                new ManualTimeProvider(Start),
                maxBytes: first.Record.ArtSize + second.Record.ArtSize);
            Assert.Equal(ArticleRetentionKind.Retained, Retain(authority, first).Kind);
            Assert.Equal(ArticleRetentionKind.Retained, Retain(authority, second).Kind);
            first.Record.ReleaseUnusedCanonicalBuffer();
            second.Record.ReleaseUnusedCanonicalBuffer();
            Assert.True(authority.TryCancelPendingRequest(first.RequestId));

            var admitted = Retain(authority, third);
            third.Record.ReleaseUnusedCanonicalBuffer();

            Assert.Equal(ArticleRetentionKind.Retained, admitted.Kind);
            Assert.Equal(1, pool.ReturnCount);
            Assert.Equal(2, pool.Outstanding);
            Assert.Equal(second.Record.ArtSize + third.Record.ArtSize, authority.RetainedPayloadBytes);
            Assert.Equal(ArticleDeliveryClaimKind.NotFound, authority.TryClaimForDelivery(Encoding.ASCII.GetBytes("<one@example.test>")).Kind);
            var held = authority.TryClaimForDelivery(Encoding.ASCII.GetBytes("<two@example.test>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, held.Kind);
            Assert.True(held.Claim!.ArticleBytes.Span.SequenceEqual(second.Record.ArtData.Span));
            held.Claim.Dispose();
            Assert.Equal(1, pool.ReturnCount);
        }

        /// <summary>
        /// A committed delivery returns the buffer only after the claim settles, and a second settle does not return it again.
        /// </summary>
        [Fact]
        public void Committed_delivery_returns_the_buffer_when_the_claim_settles()
        {
            var pool = new TrackingBytePool();
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var record = Admit(authority, pool, "<deliver@example.test>", "body\r\n");
            var image = record.ArtData.ToArray();
            var claimed = authority.TryClaimForDelivery(Encoding.ASCII.GetBytes("<deliver@example.test>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, claimed.Kind);
            Assert.True(claimed.Claim!.ArticleBytes.Span.SequenceEqual(image));
            Assert.Equal(0, pool.ReturnCount);

            claimed.Claim.Commit();
            Assert.Equal(1, pool.ReturnCount);
            Assert.True(record.ArtData.IsEmpty);
            claimed.Claim.Dispose();
            claimed.Claim.Commit();
            Assert.Equal(1, pool.ReturnCount);
            Assert.Equal(0, pool.Outstanding);
            Assert.Equal(0, authority.RetainedPayloadBytes);
        }

        /// <summary>A reader that still holds a claim observes the original bytes while another caller expires the entry.</summary>
        [Fact]
        public async Task Concurrent_expiry_cannot_return_the_buffer_while_a_claim_reads_it()
        {
            var pool = new TrackingBytePool();
            var time = new ManualTimeProvider(Start);
            var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(10));
            var record = Admit(authority, pool, "<race@example.test>", "body\r\n");
            var image = record.ArtData.ToArray();
            var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reader = Task.Run(async () =>
            {
                var claimed = authority.TryClaimForDelivery(Encoding.ASCII.GetBytes("<race@example.test>"));
                Assert.Equal(ArticleDeliveryClaimKind.Claimed, claimed.Kind);
                reading.SetResult();
                await expired.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(claimed.Claim!.ArticleBytes.Span.SequenceEqual(image));
                Assert.Equal(0, pool.ReturnCount);
                claimed.Claim.Dispose();
            });

            await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(image.Length, authority.SweepExpired());
            Assert.Equal(0, pool.ReturnCount);
            expired.SetResult();
            await reader.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, pool.ReturnCount);
            Assert.Equal(0, pool.Outstanding);
            Assert.True(record.ArtData.IsEmpty);
            Assert.Equal(
                ArticleDeliveryClaimKind.NotFound,
                authority.TryClaimForDelivery(Encoding.ASCII.GetBytes("<race@example.test>")).Kind);
        }

        /// <summary>
        /// A duplicate admission returns only the rejected buffer. Capacity rejection does not return the stored article.
        /// </summary>
        [Fact]
        public void Failed_publication_returns_only_the_rejected_buffer()
        {
            var pool = new TrackingBytePool();
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var stored = Create(pool, "<stored@example.test>", "body\r\n");
            Assert.Equal(ArticleRetentionKind.Retained, Retain(authority, stored).Kind);
            stored.Record.ReleaseUnusedCanonicalBuffer();
            var duplicate = Create(pool, "<stored@example.test>", "other\r\n");
            Assert.Equal(ArticleRetentionKind.AlreadyPresent, Retain(authority, duplicate).Kind);
            duplicate.Record.ReleaseUnusedCanonicalBuffer();
            duplicate.Record.ReleaseUnusedCanonicalBuffer();
            Assert.Equal(1, pool.ReturnCount);
            Assert.Equal(1, pool.Outstanding);

            var again = Create(pool, "<again@example.test>", "body\r\n");
            var tiny = ArticleRetentionAuthorityTests.Create(
                new ManualTimeProvider(Start),
                maxBytes: again.Record.ArtSize);
            Assert.Equal(ArticleRetentionKind.Retained, Retain(tiny, again).Kind);
            again.Record.ReleaseUnusedCanonicalBuffer();
            var overflow = Create(pool, "<other@example.test>", "body\r\n");
            Assert.Equal(ArticleRetentionKind.CapacityUnavailable, Retain(tiny, overflow).Kind);
            overflow.Record.ReleaseUnusedCanonicalBuffer();
            Assert.Equal(2, pool.ReturnCount);
            Assert.True(MemoryMarshal.TryGetArray(again.Record.ArtData, out var segment));
            Assert.True(segment.Array!.Length > again.Record.ArtSize);
            Assert.Equal(again.Record.ArtSize, tiny.RetainedPayloadBytes);
            var claimed = tiny.TryClaimForDelivery(Encoding.ASCII.GetBytes("<again@example.test>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, claimed.Kind);
            Assert.True(claimed.Claim!.ArticleBytes.Span.SequenceEqual(again.Record.ArtData.Span));
            claimed.Claim.Dispose();
            Assert.Equal(2, pool.ReturnCount);
        }

        /// <summary>
        /// A failure after any publication step removes that step. The creator still owns the buffer.
        /// </summary>
        /// <param name="stage">Publication step that throws.</param>
        [Theory]
        [InlineData(ArticleRetentionAuthority.PublicationFaultAfterLink)]
        [InlineData(ArticleRetentionAuthority.PublicationFaultAfterMessageId)]
        [InlineData(ArticleRetentionAuthority.PublicationFaultAfterArticleId)]
        [InlineData(ArticleRetentionAuthority.PublicationFaultAfterAccounting)]
        [InlineData(ArticleRetentionAuthority.PublicationFaultAfterLease)]
        public void Publication_fault_rolls_back_indexes_accounting_and_the_buffer_lease(string stage)
        {
            var pool = new TrackingBytePool();
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var created = Create(pool, "<fault@example.test>", "body\r\n");
            var image = created.Record.ArtData.ToArray();
            ArticleRetentionAuthority.PublicationFaultStage = stage;
            try
            {
                var thrown = Assert.Throws<InvalidOperationException>(() => Retain(authority, created));
                Assert.Contains(stage, thrown.Message, StringComparison.Ordinal);
            }
            finally
            {
                ArticleRetentionAuthority.PublicationFaultStage = null;
            }

            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, pool.ReturnCount);
            Assert.True(created.Record.ArtData.Span.SequenceEqual(image));
            Assert.Equal(
                ArticleDeliveryClaimKind.NotFound,
                authority.TryClaimForDelivery(Encoding.ASCII.GetBytes("<fault@example.test>")).Kind);

            Assert.Equal(ArticleRetentionKind.Retained, Retain(authority, created).Kind);
            created.Record.ReleaseUnusedCanonicalBuffer();
            Assert.Equal(0, pool.ReturnCount);
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(created.Record.ArtSize, authority.RetainedPayloadBytes);
            var claimed = authority.TryClaimForDelivery(Encoding.ASCII.GetBytes("<fault@example.test>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, claimed.Kind);
            Assert.True(claimed.Claim!.ArticleBytes.Span.SequenceEqual(image));
            claimed.Claim.Dispose();
            Assert.Equal(0, pool.ReturnCount);
        }

        /// <summary>
        /// Article Work releases the creator after a failed retain. Rollback has already dropped the retention lease,
        /// so that release returns the buffer and leaves no entry.
        /// </summary>
        [Fact]
        public async Task Handler_returns_the_buffer_when_publication_throws()
        {
            var pool = new TrackingBytePool();
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var handler = new ProviderArticleWorkHandler(
                new PayloadRetriever(ArticleWorkTestArticles.Valid()),
                authority,
                new NntpArticleParser("backfiller.test"),
                sharedConfiguration: null,
                serverId: 0,
                logger: null,
                canonicalBufferPool: pool);
            ArticleRetentionAuthority.PublicationFaultStage = ArticleRetentionAuthority.PublicationFaultAfterLease;
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await handler.HandleAsync(WorkItem(ArticleWorkTestDeliveries.CanonicalMessageId), CancellationToken.None));
            }
            finally
            {
                ArticleRetentionAuthority.PublicationFaultStage = null;
            }

            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(1, pool.RentCount);
            Assert.Equal(1, pool.ReturnCount);
            Assert.Equal(0, pool.Outstanding);
            Assert.Equal(
                ArticleDeliveryClaimKind.NotFound,
                authority.TryClaimForDelivery(Encoding.ASCII.GetBytes(ArticleWorkTestDeliveries.CanonicalMessageId)).Kind);
        }

        /// <summary>
        /// FIFO reclaim sees an in-delivery entry and leaves it. Commit then returns that buffer once.
        /// </summary>
        [Fact]
        public async Task In_delivery_entry_survives_fifo_and_commit_returns_its_buffer_once()
        {
            var pool = new TrackingBytePool();
            var first = Create(pool, "<pinned@example.test>", "11111\r\n");
            await using var authority = ArticleRetentionAuthorityTests.Create(
                new ManualTimeProvider(Start),
                maxBytes: first.Record.ArtSize);
            Assert.Equal(ArticleRetentionKind.Retained, Retain(authority, first).Kind);
            first.Record.ReleaseUnusedCanonicalBuffer();
            var claimed = authority.TryClaimForDelivery(Encoding.ASCII.GetBytes("<pinned@example.test>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, claimed.Kind);
            var image = claimed.Claim!.ArticleBytes.ToArray();

            var competing = Task.Run(() =>
            {
                var second = Create(pool, "<second@example.test>", "22222\r\n");
                var rejected = Retain(authority, second);
                Assert.Equal(ArticleRetentionKind.CapacityUnavailable, rejected.Kind);
                second.Record.ReleaseUnusedCanonicalBuffer();
            });

            await competing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(claimed.Claim.ArticleBytes.Span.SequenceEqual(image));
            Assert.Equal(1, pool.ReturnCount);
            claimed.Claim.Commit();
            claimed.Claim.Dispose();
            Assert.Equal(2, pool.ReturnCount);
            Assert.Equal(0, pool.Outstanding);
            Assert.Equal(
                ArticleDeliveryClaimKind.NotFound,
                authority.TryClaimForDelivery(Encoding.ASCII.GetBytes("<pinned@example.test>")).Kind);
        }

        /// <summary>Two claims settle on different threads. Each buffer returns once.</summary>
        [Fact]
        public async Task Concurrent_claim_settlement_returns_each_buffer_once()
        {
            var pool = new TrackingBytePool();
            await using var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var first = Admit(authority, pool, "<left@example.test>", "11111\r\n");
            var second = Admit(authority, pool, "<right@example.test>", "22222\r\n");
            var left = authority.TryClaimForDelivery(Encoding.ASCII.GetBytes("<left@example.test>"));
            var right = authority.TryClaimForDelivery(Encoding.ASCII.GetBytes("<right@example.test>"));
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, left.Kind);
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, right.Kind);
            var leftImage = left.Claim!.ArticleBytes.ToArray();
            var rightImage = right.Claim!.ArticleBytes.ToArray();

            var commit = Task.Run(() =>
            {
                Assert.True(left.Claim.ArticleBytes.Span.SequenceEqual(leftImage));
                left.Claim.Commit();
            });
            var dispose = Task.Run(() =>
            {
                Assert.True(right.Claim.ArticleBytes.Span.SequenceEqual(rightImage));
                right.Claim.Dispose();
            });
            await commit.WaitAsync(TimeSpan.FromSeconds(5));
            await dispose.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, pool.ReturnCount);
            Assert.Equal(1, pool.Outstanding);
            Assert.True(first.ArtData.IsEmpty);
            Assert.False(second.ArtData.IsEmpty);
            right.Claim.Commit();
            Assert.Equal(1, pool.ReturnCount);
        }

        /// <summary>
        /// The listener commits only after the body write returns. While that write is outstanding the entry and the rented array stay live.
        /// </summary>
        [Fact]
        public async Task Listener_holds_the_rented_buffer_until_the_article_write_completes()
        {
            var pool = new TrackingBytePool();
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var record = Admit(authority, pool, "<a@b>", "body\r\n");
            var image = record.ArtData.ToArray();
            var transport = new BlockingBodyTransport("ARTICLE <a@b>\r\n"u8.ToArray(), blockOnWrite: 3);
            var running = new NntpCacheListenerSession(transport, authority, new ManualTimeProvider(Start)).RunAsync(CancellationToken.None);

            await transport.BodyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(0, pool.ReturnCount);
            Assert.True(record.ArtData.Span.SequenceEqual(image));
            Assert.False(transport.Written.AsSpan().IndexOf(image) >= 0);

            transport.ReleaseBody.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(1, pool.ReturnCount);
            Assert.Equal(0, pool.Outstanding);
            Assert.True(transport.Written.AsSpan().IndexOf(image) >= 0);
            await authority.DisposeAsync();
            Assert.Equal(1, pool.ReturnCount);
        }

        /// <summary>
        /// Cancelling the body write finishes that write before the claim is rolled back. Retention still owns the buffer.
        /// </summary>
        [Fact]
        public async Task Listener_cancellation_does_not_return_the_buffer_while_the_write_is_outstanding()
        {
            var pool = new TrackingBytePool();
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var record = Admit(authority, pool, "<a@b>", "body\r\n");
            var image = record.ArtData.ToArray();
            var transport = new BlockingBodyTransport("ARTICLE <a@b>\r\n"u8.ToArray(), blockOnWrite: 3);
            using var cts = new CancellationTokenSource();
            var running = new NntpCacheListenerSession(transport, authority, new ManualTimeProvider(Start)).RunAsync(cts.Token);

            await transport.BodyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(0, pool.ReturnCount);
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(0, pool.ReturnCount);
            Assert.True(record.ArtData.Span.SequenceEqual(image));
            Assert.False(transport.Written.AsSpan().IndexOf(image) >= 0);

            await authority.DisposeAsync();
            Assert.Equal(1, pool.ReturnCount);
            Assert.Equal(0, pool.Outstanding);
        }

        /// <summary>
        /// A dot-leading header is a separate write. The rented body slice stays leased until that later write finishes.
        /// </summary>
        [Fact]
        public async Task Listener_holds_the_rented_buffer_until_the_split_body_write_completes()
        {
            var pool = new TrackingBytePool();
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var record = Admit(authority, pool, "<a@b>", "BODY-MARK\r\n", ".X-Dot: kept\r\n");
            var image = record.ArtData.ToArray();
            var bodyStart = record.Framing.BodyStart;
            Assert.True(bodyStart > 0);
            var body = image.AsSpan(bodyStart).ToArray();
            Assert.Contains("BODY-MARK\r\n"u8, body);
            var transport = new BlockingBodyTransport("ARTICLE <a@b>\r\n"u8.ToArray(), blockOnWrite: 4);
            var running = new NntpCacheListenerSession(transport, authority, new ManualTimeProvider(Start)).RunAsync(CancellationToken.None);

            await transport.BodyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(0, pool.ReturnCount);
            Assert.True(record.ArtData.Span.SequenceEqual(image));
            Assert.Contains("..X-Dot: kept\r\n"u8, transport.Written);
            Assert.DoesNotContain("BODY-MARK\r\n"u8, transport.Written);

            transport.ReleaseBody.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(1, pool.ReturnCount);
            Assert.Equal(1, CountSequence(transport.Written, body));
            Assert.True(transport.Written.AsSpan()[^5..].SequenceEqual("\r\n.\r\n"u8));
            Assert.DoesNotContain("\r\n.\r\n"u8, image);
            await authority.DisposeAsync();
            Assert.Equal(1, pool.ReturnCount);
        }

        /// <summary>
        /// Cancelling the retained-body write does not return the rented array, and the bytes already written stay a prefix.
        /// </summary>
        [Fact]
        public async Task Listener_cancellation_during_the_split_body_write_releases_the_buffer_once()
        {
            var pool = new TrackingBytePool();
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var record = Admit(authority, pool, "<a@b>", "BODY-MARK\r\n", ".X-Dot: kept\r\n");
            var image = record.ArtData.ToArray();
            var transport = new BlockingBodyTransport("ARTICLE <a@b>\r\n"u8.ToArray(), blockOnWrite: 4);
            using var cts = new CancellationTokenSource();
            var running = new NntpCacheListenerSession(transport, authority, new ManualTimeProvider(Start)).RunAsync(cts.Token);

            await transport.BodyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, pool.ReturnCount);
            Assert.True(record.ArtData.Span.SequenceEqual(image));
            Assert.DoesNotContain("BODY-MARK\r\n"u8, transport.Written);
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(0, pool.ReturnCount);
            Assert.True(record.ArtData.Span.SequenceEqual(image));
            Assert.Equal(0, CountSequence(transport.Written, image.AsSpan(record.Framing.BodyStart).ToArray()));

            await authority.DisposeAsync();
            Assert.Equal(1, pool.ReturnCount);
            Assert.Equal(0, pool.Outstanding);
        }

        /// <summary>
        /// A short body write is retried from the unsent suffix. The rented array stays live across both writes and the body appears once.
        /// </summary>
        [Fact]
        public async Task Listener_partial_body_write_does_not_release_or_repeat_the_retained_slice()
        {
            var pool = new TrackingBytePool();
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var record = Admit(authority, pool, "<a@b>", "BODY-MARK\r\n", ".X-Dot: kept\r\n");
            var image = record.ArtData.ToArray();
            var body = image.AsSpan(record.Framing.BodyStart).ToArray();
            var transport = new PartialBodyTransport("ARTICLE <a@b>\r\n"u8.ToArray(), body);
            var running = new NntpCacheListenerSession(transport, authority, new ManualTimeProvider(Start)).RunAsync(CancellationToken.None);

            await transport.BodyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, pool.ReturnCount);
            Assert.True(record.ArtData.Span.SequenceEqual(image));
            Assert.Equal(0, CountSequence(transport.Written, body));

            transport.ReleaseBody.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, pool.ReturnCount);
            Assert.Equal(1, CountSequence(transport.Written, body));
            Assert.True(transport.Written.AsSpan()[^5..].SequenceEqual("\r\n.\r\n"u8));
            await authority.DisposeAsync();
            Assert.Equal(1, pool.ReturnCount);
        }

        /// <summary>Counts non-overlapping occurrences of <paramref name="needle"/> in <paramref name="haystack"/>.</summary>
        private static int CountSequence(byte[] haystack, byte[] needle)
        {
            var count = 0;
            var span = haystack.AsSpan();
            var index = 0;
            while (index <= span.Length - needle.Length)
            {
                var found = span[index..].IndexOf(needle);
                if (found < 0)
                {
                    break;
                }

                count++;
                index += found + needle.Length;
            }

            return count;
        }

        /// <summary>Retains a pooled record and drops the creator reference, matching Article Work after a successful retain.</summary>
        private static ArticleRecord Admit(
            ArticleRetentionAuthority authority,
            TrackingBytePool pool,
            string messageId,
            string body,
            string extraHeaders = "")
        {
            var created = Create(pool, messageId, body, extraHeaders);
            Assert.Equal(ArticleRetentionKind.Retained, Retain(authority, created).Kind);
            created.Record.ReleaseUnusedCanonicalBuffer();
            Assert.False(created.Record.ArtData.IsEmpty);
            return created.Record;
        }

        /// <summary>Builds a pooled record without retaining it.</summary>
        private static Prepared Create(TrackingBytePool pool, string messageId, string body, string extraHeaders = "")
        {
            var created = ArticleRecordFactory.TryCreate(
                new NntpArticleParser("backfiller.test"),
                Article(messageId, body, extraHeaders),
                ArticlePathMode.Traverse,
                pool);
            Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
            return new Prepared(messageId, Guid.NewGuid(), created.Record, created.SelectedDateHeaderName);
        }

        /// <summary>Retains <paramref name="prepared"/> without dropping its creator reference.</summary>
        private static ArticleRetentionResult Retain(ArticleRetentionAuthority authority, Prepared prepared) =>
            authority.RetainCanonical(
                prepared.MessageId,
                prepared.RequestId,
                prepared.Record,
                prepared.SelectedDateHeaderName);

        /// <summary>Destuffed article bytes for one retention test.</summary>
        private static byte[] Article(string messageId, string body, string extraHeaders = "") =>
            Encoding.ASCII.GetBytes(
                "Path: peer.example\r\n"
                + "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n"
                + "Message-ID: " + messageId + "\r\n"
                + "Newsgroups: alt.test\r\n"
                + "From: user@example.test\r\n"
                + "Subject: s\r\n"
                + extraHeaders
                + "\r\n"
                + body);

        /// <summary>Work item whose settlement lease is not exercised by these tests.</summary>
        private static ArticleWorkItem WorkItem(string messageId)
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            return new ArticleWorkItem(
                new ArticleWorkRequest(1, Guid.NewGuid(), messageId, "audit"),
                "corr",
                "reply",
                new ArticleWorkSettlementLease(channel, 1, 1));
        }

        /// <summary>Prepared pooled article.</summary>
        /// <param name="MessageId">Message-ID used as the retention key.</param>
        /// <param name="RequestId">Openable request id.</param>
        /// <param name="Record">Canonical record.</param>
        /// <param name="SelectedDateHeaderName">Date header selected by the parser.</param>
        private readonly record struct Prepared(
            string MessageId,
            Guid RequestId,
            ArticleRecord Record,
            NntpArticleHeaderName SelectedDateHeaderName);

        /// <summary>Gives the handler one article payload and reports retrieval success.</summary>
        private sealed class PayloadRetriever(byte[] payload) : INntpArticleRetriever
        {
            /// <inheritdoc />
            public Task<ArticleRetrievalResult> RetrieveAsync(
                ArticleWorkItem item,
                int maxArticleBytes,
                Func<ReadOnlyMemory<byte>, ArticleRecordCreateResult> consumePayload,
                CancellationToken cancellationToken)
            {
                _ = consumePayload(payload);
                return Task.FromResult(ArticleRetrievalResult.Retrieved(220, "article follows"));
            }
        }

        /// <summary>
        /// Delays the third write, which is the article body, and reads that memory only after the delay.
        /// </summary>
        private sealed class BlockingBodyTransport : ICacheListenerTransport
        {
            private readonly byte[] _inbound;
            private readonly List<byte> _written = [];
            private readonly int _blockOnWrite;
            private int _offset;
            private int _writeCalls;

            /// <summary>Feeds <paramref name="inbound"/> as the client command and pauses write number <paramref name="blockOnWrite"/>.</summary>
            /// <param name="inbound">Command bytes, including CRLF.</param>
            /// <param name="blockOnWrite">1-based write that waits for <see cref="ReleaseBody"/> before reading its buffer.</param>
            internal BlockingBodyTransport(byte[] inbound, int blockOnWrite)
            {
                _inbound = inbound;
                _blockOnWrite = blockOnWrite;
            }

            /// <summary>Bytes copied from writes that have already returned.</summary>
            internal byte[] Written => _written.ToArray();

            /// <summary>Set when the body write has started and has not yet read the buffer.</summary>
            internal TaskCompletionSource BodyEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Releases the body write so it can read and copy the buffer.</summary>
            internal TaskCompletionSource ReleaseBody { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <inheritdoc />
            public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                if (_offset >= _inbound.Length || buffer.IsEmpty)
                {
                    return ValueTask.FromResult(0);
                }

                var count = Math.Min(buffer.Length, _inbound.Length - _offset);
                _inbound.AsSpan(_offset, count).CopyTo(buffer.Span);
                _offset += count;
                return ValueTask.FromResult(count);
            }

            /// <inheritdoc />
            public async ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
            {
                _writeCalls++;
                if (_writeCalls == _blockOnWrite)
                {
                    BodyEntered.TrySetResult();
                    await ReleaseBody.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                for (var i = 0; i < buffer.Length; i++)
                {
                    _written.Add(buffer.Span[i]);
                }

                return buffer.Length;
            }

            /// <inheritdoc />
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        /// <summary>Accepts the first half of the retained body only after <see cref="ReleaseBody"/>, then the remainder.</summary>
        private sealed class PartialBodyTransport : ICacheListenerTransport
        {
            private readonly byte[] _inbound;
            private readonly byte[] _body;
            private readonly List<byte> _written = [];
            private int _offset;
            private bool _pausedBody;

            /// <summary>Feeds <paramref name="inbound"/> and pauses the write that begins with <paramref name="body"/>.</summary>
            /// <param name="inbound">Command bytes, including CRLF.</param>
            /// <param name="body">Retained body slice. The paused write must start with these bytes.</param>
            internal PartialBodyTransport(byte[] inbound, byte[] body)
            {
                _inbound = inbound;
                _body = body;
            }

            /// <summary>Bytes copied from writes that have already returned.</summary>
            internal byte[] Written => _written.ToArray();

            /// <summary>Set when the body write has started and has not yet read the buffer.</summary>
            internal TaskCompletionSource BodyEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Releases the first partial body write.</summary>
            internal TaskCompletionSource ReleaseBody { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <inheritdoc />
            public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                if (_offset >= _inbound.Length || buffer.IsEmpty)
                {
                    return ValueTask.FromResult(0);
                }

                var count = Math.Min(buffer.Length, _inbound.Length - _offset);
                _inbound.AsSpan(_offset, count).CopyTo(buffer.Span);
                _offset += count;
                return ValueTask.FromResult(count);
            }

            /// <inheritdoc />
            public async ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
            {
                if (!_pausedBody && buffer.Length >= _body.Length && buffer.Span.StartsWith(_body))
                {
                    _pausedBody = true;
                    BodyEntered.TrySetResult();
                    await ReleaseBody.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var count = Math.Max(1, buffer.Length / 2);
                    if (count >= buffer.Length)
                    {
                        count = buffer.Length - 1;
                    }

                    buffer.Span[..count].CopyTo(Append(count));
                    return count;
                }

                buffer.Span.CopyTo(Append(buffer.Length));
                return buffer.Length;
            }

            /// <summary>Reserves <paramref name="count"/> bytes at the end of the captured output.</summary>
            private Span<byte> Append(int count)
            {
                var start = _written.Count;
                for (var i = 0; i < count; i++)
                {
                    _written.Add(0);
                }

                return CollectionsMarshal.AsSpan(_written).Slice(start, count);
            }

            /// <inheritdoc />
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        /// <summary>Pool that rents a longer array and throws if an array is returned twice.</summary>
        private sealed class TrackingBytePool : ArrayPool<byte>
        {
            private readonly ConcurrentDictionary<byte[], byte> _outstanding = new(ReferenceEqualityComparer.Instance);
            private int _rents;
            private int _returns;

            /// <summary>Number of <see cref="Rent"/> calls.</summary>
            public int RentCount => Volatile.Read(ref _rents);

            /// <summary>Number of successful returns.</summary>
            public int ReturnCount => Volatile.Read(ref _returns);

            /// <summary>Arrays rented and not yet returned.</summary>
            public int Outstanding => _outstanding.Count;

            /// <inheritdoc />
            public override byte[] Rent(int minimumLength)
            {
                Interlocked.Increment(ref _rents);
                var buffer = GC.AllocateUninitializedArray<byte>(checked(minimumLength + 64));
                if (!_outstanding.TryAdd(buffer, 0))
                {
                    throw new InvalidOperationException("The rented array was already outstanding.");
                }

                return buffer;
            }

            /// <inheritdoc />
            public override void Return(byte[] array, bool clearArray = false)
            {
                if (!_outstanding.TryRemove(array, out _))
                {
                    throw new InvalidOperationException("The array was returned twice or was not rented here.");
                }

                Interlocked.Increment(ref _returns);
                array.AsSpan().Fill(0xA5);
            }
        }
    }
}

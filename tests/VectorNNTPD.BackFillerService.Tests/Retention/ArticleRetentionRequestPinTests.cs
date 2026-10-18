using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using System.Text;

namespace VectorNNTPD.BackFillerService.Tests.Retention
{
    /// <summary>
    /// An openable RequestId pins an entry against FIFO reclaim until cancel, TTL, or committed delivery.
    /// </summary>
    public sealed class ArticleRetentionRequestPinTests
    {
        /// <summary>Fixed UTC anchor for pin and FIFO reclaim tests.</summary>
        private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

        /// <summary>Upper bound for waiting on pin-related async coordination.</summary>
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
        /// <summary>Open request id pins entry against equal-size FIFO pressure.</summary>

        [Fact]
        public void PublishedSuccess_IsPinnedAgainstFifoEviction()
        {
            var pinned = RetentionTestArticles.Create("<pin0@ex>", "same-body\r\n");
            var pressure = RetentionTestArticles.Create("<prs0@ex>", "same-body\r\n");
            Assert.Equal(pinned.Record.ArtSize, pressure.Record.ArtSize);
            var authority = ArticleRetentionAuthorityTests.Create(
                new ManualTimeProvider(Start),
                maxBytes: pinned.Record.ArtSize);
            Assert.Equal(
                ArticleRetentionKind.Retained,
                authority.RetainCanonical(
                    pinned.MessageId, pinned.RequestId, pinned.Record, pinned.SelectedDateHeaderName).Kind);

            var rejected = authority.RetainCanonical(
                pressure.MessageId, pressure.RequestId, pressure.Record, pressure.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.CapacityUnavailable, rejected.Kind);
            Assert.Equal(pinned.Record.ArtSize, authority.RetainedPayloadBytes);
            using var claim = ArticleRetentionAuthorityTests.Claim(authority, pinned.MessageId, pinned.ArtData);
        }
        /// <summary>After cancel, equal-size admission evicts unpinned entry.</summary>

        [Fact]
        public void UnpinnedEntry_RemainsFifoEvictable()
        {
            var unpinned = RetentionTestArticles.Create("<unp0@ex>", "same-body\r\n");
            var next = RetentionTestArticles.Create("<nxt0@ex>", "same-body\r\n");
            Assert.Equal(unpinned.Record.ArtSize, next.Record.ArtSize);
            var authority = ArticleRetentionAuthorityTests.Create(
                new ManualTimeProvider(Start),
                maxBytes: unpinned.Record.ArtSize);
            Assert.Equal(
                ArticleRetentionKind.Retained,
                authority.RetainCanonical(
                    unpinned.MessageId, unpinned.RequestId, unpinned.Record, unpinned.SelectedDateHeaderName).Kind);
            Assert.True(authority.TryCancelPendingRequest(unpinned.RequestId));

            var admitted = authority.RetainCanonical(
                next.MessageId, next.RequestId, next.Record, next.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.Retained, admitted.Kind);
            Assert.Equal(next.Record.ArtSize, authority.RetainedPayloadBytes);
            ArticleRetentionAuthorityTests.AssertNotFound(authority, unpinned.MessageId);
            using var claim = ArticleRetentionAuthorityTests.Claim(authority, next.MessageId, next.ArtData);
        }
        /// <summary>FIFO skips pinned oldest and evicts unpinned newer.</summary>

        [Fact]
        public void PinnedOldest_UnpinnedNewer_IsSkipped()
        {
            var pinned = RetentionTestArticles.Create("<old0@ex>", "body-aa\r\n");
            var unpinned = RetentionTestArticles.Create("<new0@ex>", "body-bb\r\n");
            var incoming = RetentionTestArticles.Create("<inc0@ex>", "body-bb\r\n");
            Assert.Equal(unpinned.Record.ArtSize, incoming.Record.ArtSize);
            var maxBytes = pinned.Record.ArtSize + unpinned.Record.ArtSize;
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: maxBytes);

            Assert.Equal(
                ArticleRetentionKind.Retained,
                authority.RetainCanonical(
                    pinned.MessageId, pinned.RequestId, pinned.Record, pinned.SelectedDateHeaderName).Kind);
            Assert.Equal(
                ArticleRetentionKind.Retained,
                authority.RetainCanonical(
                    unpinned.MessageId, unpinned.RequestId, unpinned.Record, unpinned.SelectedDateHeaderName).Kind);
            Assert.True(authority.TryCancelPendingRequest(unpinned.RequestId));

            var admitted = authority.RetainCanonical(
                incoming.MessageId, incoming.RequestId, incoming.Record, incoming.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.Retained, admitted.Kind);

            using (var claim = ArticleRetentionAuthorityTests.Claim(authority, pinned.MessageId, pinned.ArtData))
            {
                Assert.Equal(pinned.MessageId, claim.MessageId);
            }

            ArticleRetentionAuthorityTests.AssertNotFound(authority, unpinned.MessageId);
            Assert.Equal(pinned.Record.ArtSize + incoming.Record.ArtSize, authority.RetainedPayloadBytes);
        }
        /// <summary>When all entries pinned, admission returns CapacityUnavailable.</summary>

        [Fact]
        public void AllCandidatesPinned_ReturnsCapacityUnavailable()
        {
            var one = RetentionTestArticles.Create("<one0@ex>", "body-aa\r\n");
            var two = RetentionTestArticles.Create("<two0@ex>", "body-bb\r\n");
            var three = RetentionTestArticles.Create("<thr0@ex>", "body-cc\r\n");
            Assert.Equal(one.Record.ArtSize, two.Record.ArtSize);
            Assert.Equal(two.Record.ArtSize, three.Record.ArtSize);
            var maxBytes = one.Record.ArtSize + two.Record.ArtSize;
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: maxBytes);
            Assert.Equal(
                ArticleRetentionKind.Retained,
                authority.RetainCanonical(one.MessageId, one.RequestId, one.Record, one.SelectedDateHeaderName).Kind);
            Assert.Equal(
                ArticleRetentionKind.Retained,
                authority.RetainCanonical(two.MessageId, two.RequestId, two.Record, two.SelectedDateHeaderName).Kind);

            var rejected = authority.RetainCanonical(
                three.MessageId, three.RequestId, three.Record, three.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.CapacityUnavailable, rejected.Kind);
            Assert.Equal(one.Record.ArtSize + two.Record.ArtSize, authority.RetainedPayloadBytes);
            using var claimOne = ArticleRetentionAuthorityTests.Claim(authority, one.MessageId, one.ArtData);
            using var claimTwo = ArticleRetentionAuthorityTests.Claim(authority, two.MessageId, two.ArtData);
        }
        /// <summary>Entry stays pinned until every request id is cancelled.</summary>

        [Fact]
        public void MultipleRequestIdsKeepEntryPinned_UntilTheLastIsCancelled()
        {
            var seed = RetentionTestArticles.Create("<tmul@ex>", "body-aa\r\n");
            var tight = ArticleRetentionAuthorityTests.Create(
                new ManualTimeProvider(Start),
                maxBytes: seed.Record.ArtSize);
            Assert.Equal(
                ArticleRetentionKind.Retained,
                tight.RetainCanonical(seed.MessageId, seed.RequestId, seed.Record, seed.SelectedDateHeaderName).Kind);
            var second = RetentionTestArticles.Create("<tmul@ex>", "body-bb\r\n");
            Assert.Equal(
                ArticleRetentionKind.AlreadyPresent,
                tight.RetainCanonical(second.MessageId, second.RequestId, second.Record, second.SelectedDateHeaderName).Kind);

            Assert.True(tight.TryCancelPendingRequest(seed.RequestId));
            var blocked = RetentionTestArticles.Create("<tblk@ex>", "body-aa\r\n");
            Assert.Equal(seed.Record.ArtSize, blocked.Record.ArtSize);
            Assert.Equal(
                ArticleRetentionKind.CapacityUnavailable,
                tight.RetainCanonical(blocked.MessageId, blocked.RequestId, blocked.Record, blocked.SelectedDateHeaderName).Kind);

            Assert.True(tight.TryCancelPendingRequest(second.RequestId));
            var replacement = RetentionTestArticles.Create("<trep@ex>", "body-aa\r\n");
            Assert.Equal(seed.Record.ArtSize, replacement.Record.ArtSize);
            Assert.Equal(
                ArticleRetentionKind.Retained,
                tight.RetainCanonical(
                    replacement.MessageId, replacement.RequestId, replacement.Record, replacement.SelectedDateHeaderName).Kind);
            Assert.Equal(replacement.Record.ArtSize, tight.RetainedPayloadBytes);
        }
        /// <summary>Cancel last pin allows replacement retain.</summary>

        [Fact]
        public void CancelLastRequestId_MakesEntryEvictable()
        {
            var pinned = RetentionTestArticles.Create("<can0@ex>", "same-body\r\n");
            var replacement = RetentionTestArticles.Create("<can1@ex>", "same-body\r\n");
            Assert.Equal(pinned.Record.ArtSize, replacement.Record.ArtSize);
            var authority = ArticleRetentionAuthorityTests.Create(
                new ManualTimeProvider(Start),
                maxBytes: pinned.Record.ArtSize);
            Assert.Equal(
                ArticleRetentionKind.Retained,
                authority.RetainCanonical(
                    pinned.MessageId, pinned.RequestId, pinned.Record, pinned.SelectedDateHeaderName).Kind);
            Assert.True(authority.TryCancelPendingRequest(pinned.RequestId));

            Assert.Equal(
                ArticleRetentionKind.Retained,
                authority.RetainCanonical(
                    replacement.MessageId, replacement.RequestId, replacement.Record, replacement.SelectedDateHeaderName).Kind);
            ArticleRetentionAuthorityTests.AssertNotFound(authority, pinned.MessageId);
        }
        /// <summary>TTL sweep removes pinned entry and clears request id.</summary>

        [Fact]
        public void TtlExpiry_RemovesPinnedEntry()
        {
            var time = new ManualTimeProvider(Start);
            var authority = ArticleRetentionAuthorityTests.Create(
                time,
                maxBytes: 1024 * 1024,
                ttl: TimeSpan.FromSeconds(5));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<ttl-pin@b>");
            time.Advance(TimeSpan.FromSeconds(5));
            Assert.True(authority.SweepExpired() > 0);
            ArticleRetentionAuthorityTests.AssertNotFound(authority, prepared.MessageId);
            Assert.False(authority.TryCancelPendingRequest(prepared.RequestId));
            Assert.Equal(0, authority.RetainedPayloadBytes);
        }
        /// <summary>Concurrent claim and retain keeps claim and blocks reclaim.</summary>

        [Fact]
        public async Task ConcurrentClaimAndReclaim_DoesNotDropAnOpenableEntry()
        {
            const string body = "same-body\r\n";
            var prepared = RetentionTestArticles.Create("<copen@ex>", body);
            var pressure = RetentionTestArticles.Create("<cpres@ex>", body);
            Assert.Equal(prepared.Record.ArtSize, pressure.Record.ArtSize);
            var authority = ArticleRetentionAuthorityTests.Create(
                new ManualTimeProvider(Start),
                maxBytes: prepared.Record.ArtSize);
            Assert.Equal(
                ArticleRetentionKind.Retained,
                authority.RetainCanonical(
                    prepared.MessageId, prepared.RequestId, prepared.Record, prepared.SelectedDateHeaderName).Kind);

            using var barrier = new Barrier(2);
            var claimTask = Task.Run(() =>
            {
                barrier.SignalAndWait(TestTimeout);
                return authority.TryClaimForDelivery(Encoding.ASCII.GetBytes(prepared.MessageId));
            });
            var reclaimTask = Task.Run(() =>
            {
                barrier.SignalAndWait(TestTimeout);
                return authority.RetainCanonical(
                    pressure.MessageId, pressure.RequestId, pressure.Record, pressure.SelectedDateHeaderName);
            });

            await Task.WhenAll(claimTask, reclaimTask).WaitAsync(TestTimeout);
            var claim = await claimTask;
            var reclaim = await reclaimTask;
            Assert.Equal(ArticleDeliveryClaimKind.Claimed, claim.Kind);
            Assert.True(claim.Claim!.ArticleBytes.Span.SequenceEqual(prepared.ArtData.AsSpan()));
            Assert.Equal(ArticleRetentionKind.CapacityUnavailable, reclaim.Kind);
            claim.Claim.Dispose();
        }
        /// <summary>Concurrent AlreadyPresent attach and reclaim respects pin semantics.</summary>

        [Fact]
        public async Task ConcurrentAlreadyPresentAndReclaim_AttachPinsAtomically()
        {
            const string body = "same-body\r\n";
            var first = RetentionTestArticles.Create("<att0@ex>", body);
            var authority = ArticleRetentionAuthorityTests.Create(
                new ManualTimeProvider(Start),
                maxBytes: first.Record.ArtSize);
            Assert.Equal(
                ArticleRetentionKind.Retained,
                authority.RetainCanonical(first.MessageId, first.RequestId, first.Record, first.SelectedDateHeaderName).Kind);
            Assert.True(authority.TryCancelPendingRequest(first.RequestId));

            var attach = RetentionTestArticles.Create("<att0@ex>", body);
            var pressure = RetentionTestArticles.Create("<prs0@ex>", body);
            Assert.Equal(first.Record.ArtSize, pressure.Record.ArtSize);
            using var barrier = new Barrier(2);
            var attachTask = Task.Run(() =>
            {
                barrier.SignalAndWait(TestTimeout);
                return authority.RetainCanonical(
                    attach.MessageId, attach.RequestId, attach.Record, attach.SelectedDateHeaderName);
            });
            var reclaimTask = Task.Run(() =>
            {
                barrier.SignalAndWait(TestTimeout);
                return authority.RetainCanonical(
                    pressure.MessageId, pressure.RequestId, pressure.Record, pressure.SelectedDateHeaderName);
            });

            await Task.WhenAll(attachTask, reclaimTask).WaitAsync(TestTimeout);
            var attachResult = await attachTask;
            var reclaimResult = await reclaimTask;

            if (attachResult.Kind == ArticleRetentionKind.AlreadyPresent)
            {
                using var claim = ArticleRetentionAuthorityTests.Claim(authority, attach.MessageId, first.ArtData);
                Assert.Equal(ArticleRetentionKind.CapacityUnavailable, reclaimResult.Kind);
            }
            else if (attachResult.Kind == ArticleRetentionKind.Retained)
            {
                using var claim = ArticleRetentionAuthorityTests.Claim(authority, attach.MessageId, attach.ArtData);
            }
            else
            {
                Assert.Equal(ArticleRetentionKind.Retained, reclaimResult.Kind);
                using var claim = ArticleRetentionAuthorityTests.Claim(authority, pressure.MessageId, pressure.ArtData);
            }
        }
    }
}

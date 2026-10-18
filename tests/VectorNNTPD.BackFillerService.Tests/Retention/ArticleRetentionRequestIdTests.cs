using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Retention
{
    /// <summary>
    /// Regressions for bounded multi-RequestId capabilities on one retained ArticleRecord.
    /// </summary>
    public sealed class ArticleRetentionRequestIdTests
    {
        /// <summary>Fixed UTC anchor shared by concurrent retain/cancel scenarios.</summary>
        private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

        /// <summary>Upper bound for waiting on background retention work in async tests.</summary>
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
        /// <summary>Two request ids attach; delivery serves first artdata.</summary>

        [Fact]
        public void BothRequestIdsRemainAttached_AndDeliveryReturnsTheFirstArticle()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var a = RetentionTestArticles.RetainPrepared(authority, "<lifecycle-both@example.test>", "body-a\r\n");
            var b = RetentionTestArticles.Create("<lifecycle-both@example.test>", "body-b\r\n");
            Assert.Equal(
                ArticleRetentionKind.AlreadyPresent,
                authority.RetainCanonical(b.MessageId, b.RequestId, b.Record, b.SelectedDateHeaderName).Kind);

            using (var claim = ArticleRetentionAuthorityTests.Claim(authority, a.MessageId, a.ArtData))
            {
                Assert.False(claim.ArticleBytes.Span.SequenceEqual(b.ArtData.AsSpan()));
            }

            Assert.True(authority.TryCancelPendingRequest(a.RequestId));
            Assert.True(authority.TryCancelPendingRequest(b.RequestId));
            Assert.Equal(1, authority.RetainedCount);
        }
        /// <summary>Barrier-synchronized retains yield one winner and both request ids cancel.</summary>

        [Fact]
        public async Task ConcurrentArticleWork_SameMessageId_KeepsOneArticleAndBothRequestIds()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var first = RetentionTestArticles.Create("<concurrent-aw@example.test>", "first-bytes\r\n");
            var second = RetentionTestArticles.Create("<concurrent-aw@example.test>", "second-bytes\r\n");
            Assert.NotEqual(first.ArtData, second.ArtData);

            using var barrier = new Barrier(2);
            var results = new ArticleRetentionResult[2];
            var tasks = new[]
            {
                Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    results[0] = authority.RetainCanonical(
                        first.MessageId, first.RequestId, first.Record, first.SelectedDateHeaderName);
                }),
                Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    results[1] = authority.RetainCanonical(
                        second.MessageId, second.RequestId, second.Record, second.SelectedDateHeaderName);
                }),
            };

            await Task.WhenAll(tasks).WaitAsync(TestTimeout);

            Assert.Equal(1, results.Count(static r => r.Kind == ArticleRetentionKind.Retained));
            Assert.Equal(1, results.Count(static r => r.Kind == ArticleRetentionKind.AlreadyPresent));
            Assert.Equal(1, authority.RetainedCount);

            var winnerArtData = results[0].Kind == ArticleRetentionKind.Retained ? first.ArtData : second.ArtData;
            using var claim = ArticleRetentionAuthorityTests.Claim(authority, first.MessageId, winnerArtData);
            Assert.True(authority.TryCancelPendingRequest(first.RequestId));
            Assert.True(authority.TryCancelPendingRequest(second.RequestId));
            Assert.Equal(winnerArtData.Length, authority.RetainedPayloadBytes);
        }
        /// <summary>TTL clears all request ids and retention counts.</summary>

        [Fact]
        public void TtlExpiry_ClearsAllRequestIdsForEntry()
        {
            var time = new ManualTimeProvider(Start);
            var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(5));
            var a = RetentionTestArticles.RetainPrepared(authority, "<ttl-multi@example.test>");
            var b = RetentionTestArticles.Create("<ttl-multi@example.test>");
            Assert.Equal(
                ArticleRetentionKind.AlreadyPresent,
                authority.RetainCanonical(b.MessageId, b.RequestId, b.Record, b.SelectedDateHeaderName).Kind);

            time.Advance(TimeSpan.FromSeconds(5));
            ArticleRetentionAuthorityTests.AssertNotFound(authority, a.MessageId);
            Assert.False(authority.TryCancelPendingRequest(a.RequestId));
            Assert.False(authority.TryCancelPendingRequest(b.RequestId));
            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(0, authority.RetainedPayloadBytes);
        }
        /// <summary>OpenableRequestIdLimitExceeded leaves attached ids cancelable.</summary>

        [Fact]
        public void BoundExceeded_DoesNotRevokeExistingRequestIds()
        {
            const int bound = 3;
            var authority = ArticleRetentionAuthorityTests.Create(
                new ManualTimeProvider(Start),
                maxBytes: 1024 * 1024,
                maxOpenableRequestIdsPerArticle: bound);

            var first = RetentionTestArticles.RetainPrepared(authority, "<bound@example.test>", "shared\r\n");
            var attached = new List<Guid> { first.RequestId };
            for (var i = 1; i < bound; i++)
            {
                var next = RetentionTestArticles.Create("<bound@example.test>", "shared\r\n");
                Assert.Equal(
                    ArticleRetentionKind.AlreadyPresent,
                    authority.RetainCanonical(next.MessageId, next.RequestId, next.Record, next.SelectedDateHeaderName).Kind);
                attached.Add(next.RequestId);
            }

            var overflow = RetentionTestArticles.Create("<bound@example.test>", "shared\r\n");
            var rejected = authority.RetainCanonical(
                overflow.MessageId,
                overflow.RequestId,
                overflow.Record,
                overflow.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.OpenableRequestIdLimitExceeded, rejected.Kind);
            Assert.True(rejected.IsCapacityRejected);
            Assert.False(rejected.IsAvailable);
            Assert.False(authority.TryCancelPendingRequest(overflow.RequestId));

            using var claim = ArticleRetentionAuthorityTests.Claim(authority, first.MessageId, first.ArtData);
            foreach (var requestId in attached)
            {
                Assert.True(authority.TryCancelPendingRequest(requestId));
            }

            Assert.Equal(first.Record.ArtSize, authority.RetainedPayloadBytes);
            Assert.Equal(1, authority.RetainedCount);
        }
        /// <summary>AlreadyPresent retains first art size and bytes on claim.</summary>

        [Fact]
        public void FirstWinsArtData_Unchanged()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var a = RetentionTestArticles.Create("<first-wins@example.test>", "canonical-one\r\n");
            var b = RetentionTestArticles.Create("<first-wins@example.test>", "canonical-two-longer\r\n");
            Assert.NotEqual(a.ArtData, b.ArtData);

            Assert.Equal(
                ArticleRetentionKind.Retained,
                authority.RetainCanonical(a.MessageId, a.RequestId, a.Record, a.SelectedDateHeaderName).Kind);
            Assert.Equal(
                ArticleRetentionKind.AlreadyPresent,
                authority.RetainCanonical(b.MessageId, b.RequestId, b.Record, b.SelectedDateHeaderName).Kind);

            Assert.Equal(a.Record.ArtSize, authority.RetainedPayloadBytes);
            using var claim = ArticleRetentionAuthorityTests.Claim(authority, a.MessageId, a.ArtData);
            Assert.False(claim.ArticleBytes.Span.SequenceEqual(b.ArtData.AsSpan()));
        }
        /// <summary>Pipeline requeue on publish failure leaves article claimable.</summary>

        [Fact]
        public async Task PublishFailure_Requeue_LeavesTheArticleDeliverable()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var requestId = Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId);
            var prepared = RetentionTestArticles.Create(
                ArticleWorkTestDeliveries.CanonicalMessageId,
                "payload\r\n");
            Assert.Equal(
                ArticleRetentionKind.Retained,
                authority.RetainCanonical(
                    prepared.MessageId,
                    requestId,
                    prepared.Record,
                    prepared.SelectedDateHeaderName).Kind);

            var publisher = new RecordingArticleWorkResponsePublisher { CompletesSuccessPublication = false };
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.Success,
                Fqdn = ArticleWorkTestDeliveries.CanonicalFqdn,
                ListenerPort = ArticleWorkTestDeliveries.CanonicalListenerPort,
                ArticleId = prepared.Record.ArtId,
            };
            var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, maxPayloadBytes: 64 * 1024);
            var channel = new FakeBackFillerRabbitMqChannel(generation: 1);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                channelStillCurrent: static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Success, outcome);
            Assert.Empty(publisher.Published);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);

            using var claim = ArticleRetentionAuthorityTests.Claim(authority, prepared.MessageId, prepared.ArtData);
            Assert.True(authority.TryCancelPendingRequest(requestId));
        }
    }
}

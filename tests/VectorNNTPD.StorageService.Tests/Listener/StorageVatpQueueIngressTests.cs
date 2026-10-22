using System.Buffers;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Transport.ArticleTransfer;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.StorageService.Listener;
using VectorNNTPD.StorageService;

namespace VectorNNTPD.StorageService.Tests.Listener
{

    public sealed class StorageVatpQueueIngressTests
    {
        private const string CacheFqdn = "cache01.usenet.ninja";
        private static readonly TimeSpan Safety = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task Store_enqueues_the_original_article_without_this_servers_path_hop()
        {
            var inbound = Build("<store-path@seg.test>", "body-line\r\n");
            var queue = new AcceptedArticleQueue();
            await using var harness = await Harness.StartAsync(queue);
            var frames = await harness.StoreAndReadAsync(inbound);
            Assert.Equal(VatpStoreOutcome.Accepted, Outcome(frames));

            var delivery = await queue.DequeueAsync(CancellationToken.None);
            Assert.True(delivery.HasArticle);
            Assert.Equal(inbound.Record.ArtId, delivery.Article.ArtId);
            Assert.Equal(inbound.Record.ArtHash, delivery.Article.ArtHash);
            Assert.Equal(inbound.Record.ArtSize, delivery.Article.ArtSize);
            Assert.True(delivery.Article.ArtData.Span.SequenceEqual(inbound.Record.ArtData.Span));
            var queued = Encoding.ASCII.GetString(delivery.Article.ArtData.Span);
            Assert.Equal(0, CountHop(queued, CacheFqdn));
            Assert.Contains("Path: ", queued, StringComparison.Ordinal);
            Assert.Contains("body-line\r\n", queued, StringComparison.Ordinal);
            Assert.Contains("news.usenet.ninja", queued, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Store_of_bytes_that_already_contain_this_hop_prepends_it_again()
        {
            var inbound = Build("<store-again-hop@seg.test>", "kept-body\r\n", CacheFqdn);
            var inboundText = Encoding.ASCII.GetString(inbound.Record.ArtData.Span);
            Assert.Equal(1, CountHop(inboundText, CacheFqdn));

            var queue = new AcceptedArticleQueue();
            await using var harness = await Harness.StartAsync(queue);
            Assert.Equal(VatpStoreOutcome.Accepted, Outcome(await harness.StoreAndReadAsync(inbound)));

            var delivery = await queue.DequeueAsync(CancellationToken.None);
            Assert.True(delivery.HasArticle);
            var queued = Encoding.ASCII.GetString(delivery.Article.ArtData.Span);
            Assert.True(delivery.Article.ArtData.Span.SequenceEqual(inbound.Record.ArtData.Span));
            Assert.Equal(1, CountHop(queued, CacheFqdn));
            Assert.Equal(Body(inboundText), Body(queued));
            Assert.Contains("news.usenet.ninja", queued, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Second_store_of_the_same_bytes_enqueues_another_unmodified_copy()
        {
            var inbound = Build("<store-again@seg.test>", "body\r\n");
            var queue = new AcceptedArticleQueue();
            await using var harness = await Harness.StartAsync(queue);
            Assert.Equal(VatpStoreOutcome.Accepted, Outcome(await harness.StoreAndReadAsync(inbound)));
            Assert.Equal(VatpStoreOutcome.Accepted, Outcome(await harness.StoreAndReadAsync(inbound, streamId: 2)));

            var first = await queue.DequeueAsync(CancellationToken.None);
            var second = await queue.DequeueAsync(CancellationToken.None);
            Assert.True(first.HasArticle && second.HasArticle);
            Assert.True(first.Article.ArtData.Span.SequenceEqual(inbound.Record.ArtData.Span));
            Assert.True(second.Article.ArtData.Span.SequenceEqual(inbound.Record.ArtData.Span));
            Assert.Equal(0, CountHop(Encoding.ASCII.GetString(first.Article.ArtData.Span), CacheFqdn));
            Assert.Equal(0, CountHop(Encoding.ASCII.GetString(second.Article.ArtData.Span), CacheFqdn));
        }

        [Fact]
        public async Task Result_is_sent_before_persistence_finishes()
        {
            var inbound = Build("<result-before-publish@seg.test>", "body\r\n");
            var queue = new AcceptedArticleQueue();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var processor = new BlockingProcessor(release.Task);
            var worker = new AcceptedArticleWorker(
                queue,
                processor,
                NullLogger<AcceptedArticleWorker>.Instance,
                new SpoolPersistenceOptions { MinWorkers = 1, MaxWorkers = 1 },
                new VectorNNTPD.StorageService.Storage.SpoolPersistence.SpoolPressure(),
                new VectorNNTPD.StorageService.Storage.SpoolPersistence.SpoolPersistenceCounters(),
                TimeProvider.System);
            await worker.StartAsync(CancellationToken.None);
            try
            {
                await using var harness = await Harness.StartAsync(queue);
                var frames = await harness.StoreAndReadAsync(inbound).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(VatpStoreOutcome.Accepted, Outcome(frames));
                Assert.Equal(0, processor.Finished);
            }
            finally
            {
                release.TrySetResult();
                await worker.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Hash_id_and_size_mismatches_do_not_enqueue()
        {
            var real = Build("<store-real@seg.test>", "body\r\n");
            var other = Build("<store-other@seg.test>", "body\r\n");
            var queue = new AcceptedArticleQueue();

            await using var idMismatch = await Harness.StartAsync(queue);
            await idMismatch.SendRawAsync(StoreFrame(other.Record.ArtId, 1));
            await idMismatch.SendArticleBodyAsync(real, 1);
            Assert.Equal(VatpErrorCode.ArticleIdMismatch, FailCode(await idMismatch.ReadUntilAsync(static frames =>
                frames.Any(frame => frame.Header.Type == VatpFrameType.Fail))));

            await using var hashMismatch = await Harness.StartAsync(queue);
            await hashMismatch.SendStoreAsync(real, corruptLastDataByte: true);
            Assert.Equal(VatpErrorCode.ArtHashMismatch, FailCode(await hashMismatch.ReadUntilAsync(static frames =>
                frames.Any(frame => frame.Header.Type == VatpFrameType.Fail))));

            await using var sizeMismatch = await Harness.StartAsync(queue);
            await sizeMismatch.SendRawAsync(StoreFrame(real.Record.ArtId, 1));
            await sizeMismatch.SendRawAsync(MetaFrame(real, 1));
            var shortPayload = real.Record.ArtData.Slice(0, Math.Max(1, real.Record.ArtSize / 2));
            await sizeMismatch.SendRawAsync(VatpFrameEncoder.ToSingleBuffer(
                VatpFrameEncoder.EncodeData(1, shortPayload, fin: true)));
            await sizeMismatch.SendRawAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeEnd(1)));
            Assert.Equal(VatpErrorCode.ArtSizeMismatch, FailCode(await sizeMismatch.ReadUntilAsync(static frames =>
                frames.Any(frame => frame.Header.Type == VatpFrameType.Fail))));
            Assert.Equal(0, queue.ReservedBytes);
            Assert.Equal(0, queue.AccountedBytes);

            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await queue.DequeueAsync(timeout.Token));
        }

        [Fact]
        public async Task Oversized_meta_is_rejected_before_enqueue()
        {
            var built = Build("<store-big@seg.test>", "body\r\n");
            var queue = new AcceptedArticleQueue();
            await using var harness = await Harness.StartAsync(queue);
            await harness.SendRawAsync(StoreFrame(built.Record.ArtId, 1));
            var meta = MetaFrame(built, 1);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
                meta.AsSpan(VatpProtocol.HeaderLengthBytes + 12, 4),
                ArticleResourceLimits.MaxArticleBytes + 1);
            await harness.SendRawAsync(meta);
            Assert.Equal(VatpErrorCode.ArticleTooLarge, FailCode(await harness.ReadUntilAsync(static frames =>
                frames.Any(frame => frame.Header.Type == VatpFrameType.Fail))));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await queue.DequeueAsync(timeout.Token));
        }

        [Fact]
        public async Task Completed_queue_rejects_the_store_without_dropping_a_queued_article()
        {
            var built = Build("<store-closed@seg.test>", "body\r\n");
            var queue = new AcceptedArticleQueue();
            queue.Complete();
            await using var harness = await Harness.StartAsync(queue);
            await harness.SendRawAsync(StoreFrame(built.Record.ArtId, 1));
            await harness.SendRawAsync(MetaFrame(built, 1));
            Assert.Equal(VatpErrorCode.Cancelled, FailCode(await harness.ReadUntilAsync(static frames =>
                frames.Any(frame => frame.Header.Type == VatpFrameType.Fail))));
            Assert.Equal(0, queue.ReservedBytes);
            Assert.Equal(0, harness.BodiesAllocated);
            var empty = await queue.DequeueAsync(CancellationToken.None);
            Assert.False(empty.HasArticle);
        }

        [Fact]
        public async Task Two_sessions_enqueue_distinct_articles()
        {
            var queue = new AcceptedArticleQueue();
            var first = Build("<store-a@seg.test>", "alpha\r\n");
            var second = Build("<store-b@seg.test>", "beta\r\n");
            await using var left = await Harness.StartAsync(queue);
            await using var right = await Harness.StartAsync(queue);
            var leftTask = left.StoreAndReadAsync(first);
            var rightTask = right.StoreAndReadAsync(second, streamId: 3);
            var leftFrames = await leftTask;
            var rightFrames = await rightTask;
            Assert.Equal(VatpStoreOutcome.Accepted, Outcome(leftFrames));
            Assert.Equal(VatpStoreOutcome.Accepted, Outcome(rightFrames));

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < 2; i++)
            {
                var delivery = await queue.DequeueAsync(CancellationToken.None);
                Assert.True(delivery.HasArticle);
                seen.Add(Encoding.ASCII.GetString(delivery.Article.ArtData.Span));
            }

            Assert.Contains(seen, static text => text.Contains("alpha\r\n", StringComparison.Ordinal));
            Assert.Contains(seen, static text => text.Contains("beta\r\n", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Windows_cover_every_payload_byte_and_precede_result()
        {
            var body = new StringBuilder(300_000);
            for (var line = 0; line < 3_000; line++)
            {
                body.Append('x', 98).Append("\r\n");
            }

            var built = Build("<store-window@seg.test>", body.ToString());
            var queue = new AcceptedArticleQueue();
            await using var harness = await Harness.StartAsync(queue);
            await harness.SendRawAsync(StoreFrame(built.Record.ArtId, 1));
            await harness.SendRawAsync(MetaFrame(built, 1));

            var art = built.Record.ArtData;
            var offset = 0;
            while (offset < art.Length)
            {
                var length = Math.Min(64 * 1024, art.Length - offset);
                var fin = offset + length == art.Length;
                await harness.SendRawAsync(VatpFrameEncoder.ToSingleBuffer(
                    VatpFrameEncoder.EncodeData(1, art.Slice(offset, length), fin)));
                offset += length;
            }

            await harness.SendRawAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeEnd(1)));
            var frames = await harness.ReadUntilAsync(static received =>
                received.Any(frame => frame.Header.Type == VatpFrameType.Result));

            long credit = 0;
            var admissionGrant = 0L;
            var resultSeen = false;
            foreach (var frame in frames)
            {
                if (frame.Header.Type == VatpFrameType.Window)
                {
                    Assert.False(resultSeen);
                    Assert.Equal(1u, frame.Header.StreamId);
                    Assert.True(VatpControlPayload.TryDecodeWindow(frame.Payload, out var add));
                    if (admissionGrant == 0)
                    {
                        admissionGrant = add;
                    }
                    else
                    {
                        credit += add;
                    }
                }

                if (frame.Header.Type == VatpFrameType.Result)
                {
                    resultSeen = true;
                    Assert.Equal(VatpStoreOutcome.Accepted, frame.Payload[0]);
                }
            }

            Assert.Equal(ArticleTransferLimits.Default.InitialStreamWindowBytes, admissionGrant);
            Assert.Equal(built.Record.ArtSize, credit);
            Assert.Equal(1, queue.EnqueuedCount);
        }

        [Fact]
        public void Default_receive_stream_starts_at_the_production_window()
        {
            var built = Build("<store-default-window@seg.test>", "body\r\n");
            var stream = new ArticleTransferReceiveStream(1, Guid.NewGuid(), built.Record.ArtId);
            Assert.Equal(1024L * 1024, stream.ReceiveCredit);
            Assert.Equal(1024 * 1024, ArticleTransferLimits.Default.InitialStreamWindowBytes);
        }

        [Fact]
        public async Task Explicit_initial_window_reaches_the_receive_stream_and_refunds_each_chunk()
        {
            const int initial = 1024 * 1024;
            var limits = new ArticleTransferLimits { InitialStreamWindowBytes = initial };
            var built = Build("<store-wide-window@seg.test>", "body-line\r\n");
            var stream = new ArticleTransferReceiveStream(1, Guid.NewGuid(), built.Record.ArtId, limits);
            Assert.Equal((long)initial, stream.ReceiveCredit);

            var queue = new AcceptedArticleQueue();
            await using var harness = await Harness.StartAsync(queue, limits);
            var frames = await harness.StoreAndReadAsync(built);
            Assert.Equal(VatpStoreOutcome.Accepted, Outcome(frames));
            long refunded = 0;
            var sawGrant = false;
            foreach (var frame in frames)
            {
                if (frame.Header.Type != VatpFrameType.Window)
                {
                    continue;
                }

                Assert.True(VatpControlPayload.TryDecodeWindow(frame.Payload, out var add));
                if (!sawGrant)
                {
                    Assert.Equal((uint)initial, add);
                    sawGrant = true;
                    continue;
                }

                Assert.True(add <= 64 * 1024);
                refunded += add;
            }

            Assert.True(sawGrant);
            Assert.Equal(built.Record.ArtSize, refunded);
            Assert.Equal(1, queue.EnqueuedCount);
        }

        [Fact]
        public async Task Full_queue_withholds_body_credit_until_a_dequeue_frees_capacity()
        {
            var first = Build("<admit-held@seg.test>", "alpha-body\r\n");
            var queue = new AcceptedArticleQueue(first.Record.ArtSize, TimeProvider.System);
            await using var held = await Harness.StartAsync(queue);
            Assert.Equal(VatpStoreOutcome.Accepted, Outcome(await held.StoreAndReadAsync(first)));

            var second = Build("<admit-wait@seg.test>", "beta-body\r\n");
            await using var waiting = await Harness.StartAsync(queue);
            await waiting.SendRawAsync(StoreFrame(second.Record.ArtId, 1));
            await waiting.SendRawAsync(MetaFrame(second, 1));
            var credit = waiting.ReadUntilAsync(static frames =>
                frames.Any(static frame => frame.Header.Type is VatpFrameType.Window or VatpFrameType.Fail or VatpFrameType.Result));
            await WaitUntilAsync(() => queue.AdmissionWaiters == 1);
            Assert.Equal(0, queue.ReservedBytes);
            Assert.Equal(first.Record.ArtSize, queue.AccountedBytes);
            Assert.Equal(0, waiting.BodiesAllocated);
            Assert.False(credit.IsCompleted);

            Assert.True((await queue.DequeueAsync(CancellationToken.None)).HasArticle);
            var granted = await credit.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(VatpFrameType.Window, granted[0].Header.Type);
            Assert.Equal(1, waiting.BodiesAllocated);
            Assert.Equal(second.Record.ArtSize, queue.ReservedBytes);
            Assert.Equal(0, queue.QueuedBytes);

            await waiting.SendRawAsync(VatpFrameEncoder.ToSingleBuffer(
                VatpFrameEncoder.EncodeData(1, second.Record.ArtData, fin: true)));
            await waiting.SendRawAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeEnd(1)));
            var result = await waiting.ReadUntilAsync(static frames =>
                frames.Any(static frame => frame.Header.Type == VatpFrameType.Result));
            Assert.Equal(VatpStoreOutcome.Accepted, Outcome(result));
            Assert.Equal(0, queue.ReservedBytes);
            Assert.Equal(second.Record.ArtSize, queue.QueuedBytes);
            var delivery = await queue.DequeueAsync(CancellationToken.None);
            Assert.True(delivery.Article.ArtData.Span.SequenceEqual(second.Record.ArtData.Span));
        }

        [Fact]
        public async Task Body_sent_before_queue_admission_is_rejected_without_allocating_it()
        {
            var first = Build("<admit-full@seg.test>", "alpha-body\r\n");
            var queue = new AcceptedArticleQueue(first.Record.ArtSize, TimeProvider.System);
            await using var held = await Harness.StartAsync(queue);
            Assert.Equal(VatpStoreOutcome.Accepted, Outcome(await held.StoreAndReadAsync(first)));

            var second = Build("<admit-early@seg.test>", "beta-body\r\n");
            await using var early = await Harness.StartAsync(queue);
            await early.SendStoreAsync(second);
            var frames = await early.ReadUntilAsync(static received =>
                received.Any(static frame => frame.Header.Type is VatpFrameType.Fail or VatpFrameType.Result));
            Assert.Equal(VatpErrorCode.FlowControlViolation, FailCode(frames));
            Assert.Equal(1, queue.EnqueuedCount);
            Assert.Equal(0, queue.ReservedBytes);
            Assert.Equal(0, queue.AdmissionWaiters);
            Assert.Equal(0, early.BodiesAllocated);
        }

        [Fact]
        public async Task Cancel_before_body_admission_releases_the_waiter()
        {
            var first = Build("<admit-cancel-held@seg.test>", "alpha-body\r\n");
            var queue = new AcceptedArticleQueue(first.Record.ArtSize, TimeProvider.System);
            await using var held = await Harness.StartAsync(queue);
            Assert.Equal(VatpStoreOutcome.Accepted, Outcome(await held.StoreAndReadAsync(first)));
            var second = Build("<admit-cancel@seg.test>", "beta-body\r\n");
            await using var waiting = await Harness.StartAsync(queue);
            await waiting.SendRawAsync(StoreFrame(second.Record.ArtId, 1));
            await waiting.SendRawAsync(MetaFrame(second, 1));
            await WaitUntilAsync(() => queue.AdmissionWaiters == 1);
            await waiting.SendRawAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeCancel(1)));
            var frames = await waiting.ReadUntilAsync(static received =>
                received.Any(static frame => frame.Header.Type == VatpFrameType.Fail));
            Assert.Equal(VatpErrorCode.Cancelled, FailCode(frames));
            await WaitUntilAsync(() => queue.AdmissionWaiters == 0);
            Assert.Equal(0, queue.ReservedBytes);
            Assert.Equal(first.Record.ArtSize, queue.AccountedBytes);
        }

        [Fact]
        public async Task Article_larger_than_the_queue_budget_is_rejected_before_the_body()
        {
            var built = Build("<admit-oversize@seg.test>", "too-big-for-budget\r\n");
            var queue = new AcceptedArticleQueue(8, TimeProvider.System);
            await using var harness = await Harness.StartAsync(queue);
            await harness.SendRawAsync(StoreFrame(built.Record.ArtId, 1));
            await harness.SendRawAsync(MetaFrame(built, 1));
            var frames = await harness.ReadUntilAsync(static received =>
                received.Any(static frame => frame.Header.Type == VatpFrameType.Result));
            Assert.Equal(VatpStoreOutcome.RejectedCapacity, Outcome(frames));
            Assert.Equal(0, queue.EnqueuedCount);
            Assert.Equal(0, queue.ReservedBytes);
            Assert.Equal(0, harness.BodiesAllocated);
        }

        [Fact]
        public async Task Two_sessions_cannot_both_reserve_the_last_free_slot()
        {
            var held = Build("<admit-shared-held@seg.test>", "held-body\r\n");
            var queue = new AcceptedArticleQueue(held.Record.ArtSize, TimeProvider.System);
            await using var owner = await Harness.StartAsync(queue);
            Assert.Equal(VatpStoreOutcome.Accepted, Outcome(await owner.StoreAndReadAsync(held)));

            var left = Build("<admit-shared-aaaa@seg.test>", "peer-body\r\n");
            var right = Build("<admit-shared-bbbb@seg.test>", "peer-body\r\n");
            Assert.Equal(held.Record.ArtSize, left.Record.ArtSize);
            Assert.Equal(held.Record.ArtSize, right.Record.ArtSize);
            await using var first = await Harness.StartAsync(queue);
            await using var second = await Harness.StartAsync(queue);
            await first.SendRawAsync(StoreFrame(left.Record.ArtId, 1));
            await first.SendRawAsync(MetaFrame(left, 1));
            await second.SendRawAsync(StoreFrame(right.Record.ArtId, 1));
            await second.SendRawAsync(MetaFrame(right, 1));
            await WaitUntilAsync(() => queue.AdmissionWaiters == 2);
            Assert.Equal(0, queue.ReservedBytes);

            var firstCredit = first.ReadUntilAsync(static frames =>
                frames.Any(static frame => frame.Header.Type is VatpFrameType.Window or VatpFrameType.Fail or VatpFrameType.Result));
            var secondCredit = second.ReadUntilAsync(static frames =>
                frames.Any(static frame => frame.Header.Type is VatpFrameType.Window or VatpFrameType.Fail or VatpFrameType.Result));
            Assert.True((await queue.DequeueAsync(CancellationToken.None)).HasArticle);
            var winner = await Task.WhenAny(firstCredit, secondCredit).WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => queue.AdmissionWaiters == 1 && queue.ReservedBytes == held.Record.ArtSize);
            Assert.True(winner.IsCompletedSuccessfully);
            Assert.Equal(VatpFrameType.Window, (await winner)[0].Header.Type);
            Assert.Equal(1, (firstCredit.IsCompleted ? 1 : 0) + (secondCredit.IsCompleted ? 1 : 0));
            Assert.Equal(0, queue.QueuedBytes);
        }

        [Fact]
        public async Task Disconnect_during_body_reception_releases_the_reservation()
        {
            var built = Build("<admit-drop@seg.test>", new string('x', 128) + "\r\n");
            var queue = new AcceptedArticleQueue(built.Record.ArtSize, TimeProvider.System);
            var session = await Harness.StartAsync(queue);
            await session.SendRawAsync(StoreFrame(built.Record.ArtId, 1));
            await session.SendRawAsync(MetaFrame(built, 1));
            var granted = await session.ReadUntilAsync(static frames =>
                frames.Any(static frame => frame.Header.Type == VatpFrameType.Window));
            Assert.Equal(VatpFrameType.Window, granted[0].Header.Type);
            Assert.Equal(1, session.BodiesAllocated);
            Assert.Equal(built.Record.ArtSize, queue.ReservedBytes);

            var chunk = built.Record.ArtData.Slice(0, 1);
            await session.SendRawAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeData(1, chunk, fin: false)));
            await session.ReadUntilAsync(static frames =>
                frames.Any(static frame => frame.Header.Type == VatpFrameType.Window));
            Assert.Equal(built.Record.ArtSize, queue.ReservedBytes);
            Assert.Equal(0, queue.QueuedBytes);

            await session.DisposeAsync();
            Assert.Equal(0, queue.ReservedBytes);
            Assert.Equal(0, queue.AccountedBytes);
            Assert.Equal(0, queue.AdmissionWaiters);

            await using var retry = await Harness.StartAsync(queue);
            Assert.Equal(VatpStoreOutcome.Accepted, Outcome(await retry.StoreAndReadAsync(built)));
            Assert.Equal(0, queue.ReservedBytes);
            Assert.Equal(built.Record.ArtSize, queue.QueuedBytes);
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!condition())
            {
                await Task.Delay(5, timeout.Token);
            }
        }

        [Fact]
        public async Task Initial_window_smaller_than_a_chunk_is_a_flow_control_failure()
        {
            var limits = new ArticleTransferLimits { InitialStreamWindowBytes = 16 };
            var built = Build("<store-tight-window@seg.test>", "body-line\r\n");
            Assert.True(built.Record.ArtSize > limits.InitialStreamWindowBytes);
            var queue = new AcceptedArticleQueue();
            await using var harness = await Harness.StartAsync(queue, limits);
            await harness.SendStoreAsync(built);
            Assert.Equal(VatpErrorCode.FlowControlViolation, FailCode(await harness.ReadUntilAsync(static frames =>
                frames.Any(frame => frame.Header.Type == VatpFrameType.Fail))));
            Assert.Equal(0, queue.EnqueuedCount);
        }

        [Fact]
        public void Initial_window_above_the_max_credit_is_rejected()
        {
            var built = Build("<store-over-window@seg.test>", "body\r\n");
            var limits = new ArticleTransferLimits
            {
                InitialStreamWindowBytes = (int)ArticleTransferLimits.Default.MaxStreamCreditBytes + 1,
            };
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ArticleTransferReceiveStream(1, Guid.NewGuid(), built.Record.ArtId, limits));
        }

        [Fact]
        public async Task Split_reads_enqueue_a_large_article()
        {
            var body = new StringBuilder(768_000);
            for (var line = 0; line < 7_680; line++)
            {
                body.Append('y', 98).Append("\r\n");
            }

            var built = Build("<store-split@seg.test>", body.ToString());
            var framed = EncodeStore(built, 1);

            await AssertChunkedEnqueueAsync(built, framed, chunkSize: 7);
            await AssertChunkedEnqueueAsync(built, framed, chunkSize: 16 * 1024);
            await AssertChunkedEnqueueAsync(built, framed, chunkSize: framed.Length);
            await AssertExactFrameReadsAsync(built);
        }

        [Fact]
        public async Task Interleaved_streams_split_across_reads_both_enqueue()
        {
            var first = Build("<store-left@seg.test>", "left-body\r\n");
            var second = Build("<store-right@seg.test>", "right-body\r\n");
            var framed = EncodeStore(first, 1).Concat(EncodeStore(second, 2)).ToArray();
            var queue = new AcceptedArticleQueue();
            await using var harness = await Harness.StartAsync(queue);
            await harness.Transport.ClientWriteChunkedAsync(framed, 5);
            var frames = await harness.ReadUntilAsync(static received =>
                received.Count(static frame => frame.Header.Type == VatpFrameType.Result) == 2);

            Assert.Equal(2, frames.Count(static frame => frame.Header.Type == VatpFrameType.Result && frame.Payload[0] == VatpStoreOutcome.Accepted));
            Assert.Equal(2, queue.EnqueuedCount);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < 2; i++)
            {
                var delivery = await queue.DequeueAsync(CancellationToken.None);
                Assert.True(delivery.HasArticle);
                seen.Add(Encoding.ASCII.GetString(delivery.Article.ArtData.Span));
            }

            Assert.Contains(seen, static text => text.Contains("left-body\r\n", StringComparison.Ordinal));
            Assert.Contains(seen, static text => text.Contains("right-body\r\n", StringComparison.Ordinal));
        }

        private static async Task AssertChunkedEnqueueAsync(Built built, byte[] framed, int chunkSize)
        {
            var queue = new AcceptedArticleQueue();
            await using var harness = await Harness.StartAsync(queue);
            await harness.Transport.ClientWriteChunkedAsync(framed, chunkSize);
            var frames = await harness.ReadUntilAsync(static received =>
                received.Any(frame => frame.Header.Type == VatpFrameType.Result));
            Assert.Equal(VatpStoreOutcome.Accepted, Outcome(frames));
            var delivery = await queue.DequeueAsync(CancellationToken.None);
            Assert.True(delivery.HasArticle);
            Assert.True(delivery.Article.ArtData.Span.SequenceEqual(built.Record.ArtData.Span));
            Assert.Equal(1, queue.EnqueuedCount);
        }

        private static async Task AssertExactFrameReadsAsync(Built built)
        {
            var queue = new AcceptedArticleQueue();
            await using var harness = await Harness.StartAsync(queue);
            foreach (var frame in EnumerateStoreFrames(built, 1))
            {
                await harness.SendRawAsync(frame);
            }

            var frames = await harness.ReadUntilAsync(static received =>
                received.Any(frame => frame.Header.Type == VatpFrameType.Result));
            Assert.Equal(VatpStoreOutcome.Accepted, Outcome(frames));
            var delivery = await queue.DequeueAsync(CancellationToken.None);
            Assert.True(delivery.HasArticle);
            Assert.True(delivery.Article.ArtData.Span.SequenceEqual(built.Record.ArtData.Span));
        }

        private static byte[] EncodeStore(Built built, uint streamId)
        {
            var parts = EnumerateStoreFrames(built, streamId);
            var length = 0;
            foreach (var part in parts)
            {
                length += part.Length;
            }

            var framed = new byte[length];
            var offset = 0;
            foreach (var part in parts)
            {
                part.CopyTo(framed.AsSpan(offset));
                offset += part.Length;
            }

            return framed;
        }

        private static List<byte[]> EnumerateStoreFrames(Built built, uint streamId)
        {
            var parts = new List<byte[]>
            {
                StoreFrame(built.Record.ArtId, streamId),
                MetaFrame(built, streamId),
            };
            var art = built.Record.ArtData;
            var offset = 0;
            while (offset < art.Length)
            {
                var length = Math.Min(64 * 1024, art.Length - offset);
                var fin = offset + length == art.Length;
                parts.Add(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeData(streamId, art.Slice(offset, length), fin)));
                offset += length;
            }

            parts.Add(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeEnd(streamId)));
            return parts;
        }

        private static int CountHop(string article, string hop) =>
            article.Split(hop, StringSplitOptions.None).Length - 1;

        private static string Body(string article)
        {
            var split = article.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            Assert.True(split >= 0);
            return article[(split + 4)..];
        }

        private static byte Outcome(IReadOnlyList<Decoded> frames) =>
            frames.Last(static frame => frame.Header.Type == VatpFrameType.Result).Payload[0];

        private static VatpErrorCode FailCode(IReadOnlyList<Decoded> frames)
        {
            var payload = frames.Last(static frame => frame.Header.Type == VatpFrameType.Fail).Payload;
            Assert.True(VatpControlPayload.TryDecodeFail(payload, out var error, out _));
            return error;
        }

        private static Built Build(string messageId, string body, string ingressFqdn = "nntpd01.usenet.ninja")
        {
            var parser = new NntpArticleParser(ingressFqdn);
            var text = new StringBuilder()
                .Append("Path: peer.example\r\n")
                .Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n")
                .Append("Message-ID: ").Append(messageId).Append("\r\n")
                .Append("Newsgroups: alt.test\r\n")
                .Append("From: user@example.test\r\n")
                .Append("Subject: vatp\r\n")
                .Append("\r\n")
                .Append(body);
            var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(text.ToString()));
            Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
            return new Built(created.Record, created.SelectedDateHeaderName);
        }

        private static byte[] StoreFrame(ArticleId articleId, uint streamId)
        {
            var id = new byte[ArticleId.Length];
            articleId.CopyTo(id);
            return VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeStore(streamId, id));
        }

        private static byte[] MetaFrame(Built built, uint streamId)
        {
            var meta = ArticleCanonicalTransferMeta.FromRecord(built.Record, built.SelectedDateHeaderName);
            return VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeMeta(streamId, VatpMetaCodec.Encode(in meta)));
        }

        private readonly record struct Built(ArticleRecord Record, NntpArticleHeaderName SelectedDateHeaderName);

        private sealed class BlockingProcessor : IAcceptedArticleProcessor
        {
            private readonly Task _release;

            public BlockingProcessor(Task release) => _release = release;

            public int Finished { get; private set; }

            public void EnsureReady()
            {
            }

            public async ValueTask<AcceptedArticleProcessResult> ProcessAsync(
                AcceptedArticle article,
                CancellationToken cancellationToken)
            {
                await _release.WaitAsync(cancellationToken).ConfigureAwait(false);
                Finished++;
                return AcceptedArticleProcessResult.Completed;
            }
        }

        private readonly record struct Decoded(VatpFrameHeader Header, byte[] Payload);

        private sealed class Harness : IAsyncDisposable
        {
            private readonly CancellationTokenSource _cts = new();
            private readonly StorageVatpSession _session;
            private readonly Task _run;

            private Harness(PipeTransport transport, StorageVatpSession session)
            {
                Transport = transport;
                _session = session;
                _run = session.RunAsync(_cts.Token);
            }

            public PipeTransport Transport { get; }

            /// <summary>Article buffers this session allocated after admission.</summary>
            public long BodiesAllocated => Volatile.Read(ref _session.StoreBodiesAllocated);

            public static async Task<Harness> StartAsync(IAcceptedArticleQueue queue, ArticleTransferLimits? limits = null)
            {
                var transport = new PipeTransport();
                var session = new StorageVatpSession(
                    transport,
                    NullStorageArticleOpenBoundary.Instance,
                    new StorageServerListenerRuntimeOptions(262144, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), 8),
                    NullLogger.Instance,
                    limits,
                    acceptedArticles: queue,
                    storeAdmission: new StoreAssemblyAdmission(8));
                var harness = new Harness(transport, session);
                await harness.Transport.ClientWriteAsync(VatpFrameEncoder.ToSingleBuffer(
                    VatpFrameEncoder.EncodeHello(VatpProtocol.DefaultMaxFramePayload)));
                _ = await harness.ReadUntilAsync(static frames => frames.Any(frame => frame.Header.Type == VatpFrameType.Hello));
                return harness;
            }

            public async Task SendStoreAsync(Built built, uint streamId = 1, bool corruptLastDataByte = false)
            {
                await SendRawAsync(StoreFrame(built.Record.ArtId, streamId));
                await SendArticleBodyAsync(built, streamId, corruptLastDataByte);
            }

            public async Task SendArticleBodyAsync(Built built, uint streamId, bool corruptLastDataByte = false)
            {
                await SendRawAsync(MetaFrame(built, streamId));
                var payload = built.Record.ArtData.ToArray();
                if (corruptLastDataByte)
                {
                    payload[^1] ^= 0xFF;
                }

                await SendRawAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeData(streamId, payload, fin: true)));
                await SendRawAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeEnd(streamId)));
            }

            public Task SendRawAsync(byte[] frame) => Transport.ClientWriteAsync(frame);

            public async Task<IReadOnlyList<Decoded>> StoreAndReadAsync(Built built, uint streamId = 1)
            {
                await SendRawAsync(StoreFrame(built.Record.ArtId, streamId));
                await SendArticleBodyAsync(built, streamId);
                return await ReadUntilAsync(static frames => frames.Any(frame => frame.Header.Type == VatpFrameType.Result));
            }

            public async Task<IReadOnlyList<Decoded>> ReadUntilAsync(Func<IReadOnlyList<Decoded>, bool> done)
            {
                using var timeout = new CancellationTokenSource(Safety);
                var buffer = new List<byte>();
                var frames = new List<Decoded>();
                while (!done(frames))
                {
                    var chunk = await Transport.ReadServerAsync(timeout.Token);
                    buffer.AddRange(chunk);
                    var offset = 0;
                    while (offset < buffer.Count)
                    {
                        var parsed = VatpFrameParser.ParseOneFrame(buffer.ToArray(), VatpProtocol.DefaultMaxFramePayload);
                        if (parsed.Status == VatpFrameParseStatus.Incomplete)
                        {
                            break;
                        }

                        Assert.Equal(VatpFrameParseStatus.Success, parsed.Status);
                        var frame = parsed.Frame!.Value;
                        var payload = new byte[frame.Payload.Length];
                        frame.Payload.CopyTo(payload);
                        frames.Add(new Decoded(frame.Header, payload));
                        offset += checked((int)parsed.ConsumedBytes);
                        buffer.RemoveRange(0, checked((int)parsed.ConsumedBytes));
                    }
                }

                return frames;
            }

            public async ValueTask DisposeAsync()
            {
                await _cts.CancelAsync();
                try
                {
                    await _run;
                }
                catch (OperationCanceledException ex)
                {
                    ExpectedException.Ignore(ex);
                }

                _cts.Dispose();
                await Transport.DisposeAsync();
            }
        }

        private sealed class PipeTransport : IStorageVatpTransport
        {
            private readonly Channel<byte[]> _toServer = Channel.CreateUnbounded<byte[]>();
            private readonly Channel<byte[]> _toClient = Channel.CreateUnbounded<byte[]>();
            private byte[] _pending = [];
            private int _pendingOffset;

            public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                if (_pendingOffset >= _pending.Length)
                {
                    _pending = await _toServer.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    _pendingOffset = 0;
                }

                var count = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
                _pending.AsSpan(_pendingOffset, count).CopyTo(buffer.Span);
                _pendingOffset += count;
                return count;
            }

            public async ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
            {
                await _toClient.Writer.WriteAsync(buffer.ToArray(), cancellationToken).ConfigureAwait(false);
                return buffer.Length;
            }

            public Task ClientWriteAsync(byte[] frame) => _toServer.Writer.WriteAsync(frame).AsTask();

            public async Task ClientWriteChunkedAsync(byte[] frame, int chunkSize)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
                var offset = 0;
                while (offset < frame.Length)
                {
                    var count = Math.Min(chunkSize, frame.Length - offset);
                    var slice = new byte[count];
                    frame.AsSpan(offset, count).CopyTo(slice);
                    await _toServer.Writer.WriteAsync(slice).ConfigureAwait(false);
                    offset += count;
                }
            }

            public Task<byte[]> ReadServerAsync(CancellationToken cancellationToken) =>
                _toClient.Reader.ReadAsync(cancellationToken).AsTask();

            public ValueTask DisposeAsync()
            {
                _toServer.Writer.TryComplete();
                _toClient.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }
        }
    }
}

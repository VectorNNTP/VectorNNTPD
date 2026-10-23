using VectorNNTPD.Common.Articles;
using VectorNNTPD.StorageService.Ingress;

namespace VectorNNTPD.StorageService.Tests.Ingress
{

    public sealed class AcceptedArticleQueueTests
    {
        [Fact]
        public async Task Concurrent_producers_and_consumers_deliver_each_article_once()
        {
            var queue = new AcceptedArticleQueue();
            const int count = 200;
            var produced = Enumerable.Range(0, count).Select(Create).ToArray();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var producers = produced.Select(async article =>
            {
                await gate.Task;
                Assert.True(queue.TryEnqueue(article));
            }).ToArray();

            var seen = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
            var consumers = Enumerable.Range(0, 4).Select(async _ =>
            {
                await gate.Task;
                while (seen.Count < count)
                {
                    var delivery = await queue.DequeueAsync(CancellationToken.None);
                    if (!delivery.HasArticle)
                    {
                        return;
                    }

                    Assert.True(seen.TryAdd(delivery.Article.ArtSize, 0));
                }
            }).ToArray();

            gate.TrySetResult();
            await Task.WhenAll(producers);
            queue.Complete();
            await Task.WhenAll(consumers);
            Assert.Equal(count, seen.Count);
        }

        [Fact]
        public async Task Counters_track_enqueue_and_dequeue_without_copying_the_buffer()
        {
            var bytes = new byte[] { 9, 8, 7 };
            var queue = new AcceptedArticleQueue();
            Assert.Equal(0, queue.EnqueuedCount);
            Assert.Equal(0, queue.DequeuedCount);
            Assert.Equal(0, queue.QueuedBytes);

            Assert.True(queue.TryEnqueue(new AcceptedArticle(default, 1, bytes.Length, bytes)));
            Assert.Equal(1, queue.EnqueuedCount);
            Assert.Equal(0, queue.DequeuedCount);
            Assert.Equal(bytes.Length, queue.QueuedBytes);

            var delivery = await queue.DequeueAsync(CancellationToken.None);
            Assert.True(delivery.HasArticle);
            Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(delivery.Article.ArtData, out var segment));
            Assert.Same(bytes, segment.Array);
            Assert.Equal(1, queue.EnqueuedCount);
            Assert.Equal(1, queue.DequeuedCount);
            Assert.Equal(0, queue.QueuedBytes);

            queue.Complete();
            Assert.False(queue.TryEnqueue(new AcceptedArticle(default, 1, bytes.Length, bytes)));
            Assert.Equal(1, queue.EnqueuedCount);
            var empty = await queue.DequeueAsync(CancellationToken.None);
            Assert.False(empty.HasArticle);
            Assert.Equal(1, queue.DequeuedCount);
        }

        [Fact]
        public async Task Dequeue_returns_the_same_buffer_the_producer_enqueued()
        {
            var bytes = new byte[] { 1, 2, 3, 4 };
            var queue = new AcceptedArticleQueue();
            Assert.True(queue.TryEnqueue(new AcceptedArticle(default, 9, bytes.Length, bytes)));
            var delivery = await queue.DequeueAsync(CancellationToken.None);
            Assert.True(delivery.HasArticle);
            Assert.True(delivery.Article.ArtData.Span.SequenceEqual(bytes));
            Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(delivery.Article.ArtData, out var segment));
            Assert.Same(bytes, segment.Array);
        }

        [Fact]
        public async Task Complete_unblocks_a_waiting_consumer_and_rejects_later_enqueue()
        {
            var queue = new AcceptedArticleQueue();
            var waiting = queue.DequeueAsync(CancellationToken.None);
            queue.Complete();
            var delivery = await waiting;
            Assert.False(delivery.HasArticle);
            Assert.False(queue.TryEnqueue(Create(1)));
        }

        [Fact]
        public async Task Cancelling_a_dequeue_does_not_lose_a_later_article()
        {
            var queue = new AcceptedArticleQueue();
            using var cts = new CancellationTokenSource();
            var waiting = queue.DequeueAsync(cts.Token);
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
            var article = Create(7);
            Assert.True(queue.TryEnqueue(article));
            var delivery = await queue.DequeueAsync(CancellationToken.None);
            Assert.True(delivery.HasArticle);
            Assert.Equal(article.ArtSize, delivery.Article.ArtSize);
            Assert.Equal(article.ArtHash, delivery.Article.ArtHash);
        }

        [Fact]
        public async Task Byte_budget_rejects_an_enqueue_until_a_dequeue_frees_space()
        {
            var queue = new AcceptedArticleQueue(10, TimeProvider.System);
            var first = new AcceptedArticle(default, 1, 6, new byte[6]);
            var second = new AcceptedArticle(default, 1, 6, new byte[6]);
            Assert.True(queue.TryEnqueue(first));
            Assert.False(queue.TryEnqueue(second));
            Assert.Equal(6, queue.QueuedBytes);
            Assert.Equal(1, queue.QueuedCount);

            var delivery = await queue.DequeueAsync(CancellationToken.None);
            Assert.True(delivery.HasArticle);
            Assert.True(queue.TryEnqueue(second));
            Assert.Equal(6, queue.QueuedBytes);
        }

        [Fact]
        public void TryDequeue_releases_the_byte_budget_and_does_not_wait()
        {
            var queue = new AcceptedArticleQueue(10, TimeProvider.System);
            var first = new AcceptedArticle(default, 1, 6, new byte[6]);
            var second = new AcceptedArticle(default, 1, 6, new byte[6]);
            Assert.False(queue.TryDequeue(out _));
            Assert.Equal(0, queue.DequeuedCount);

            Assert.True(queue.TryEnqueue(first));
            Assert.False(queue.TryEnqueue(second));
            Assert.True(queue.TryDequeue(out var delivery));
            Assert.True(delivery.HasArticle);
            Assert.Equal(6, delivery.Article.ArtSize);
            Assert.Equal(0, queue.QueuedBytes);
            Assert.Equal(1, queue.DequeuedCount);
            Assert.True(queue.TryEnqueue(second));
            Assert.Equal(6, queue.QueuedBytes);

            queue.Complete();
            Assert.True(queue.TryDequeue(out var drained));
            Assert.True(drained.HasArticle);
            Assert.False(queue.TryDequeue(out _));
            Assert.Equal(0, queue.QueuedBytes);
        }

        [Fact]
        public async Task TryDequeue_and_DequeueAsync_do_not_lose_articles_under_concurrency()
        {
            var queue = new AcceptedArticleQueue();
            const int count = 500;
            var seen = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
            using var started = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var consumers = Enumerable.Range(0, 4).Select(async index =>
            {
                while (seen.Count < count && !started.IsCancellationRequested)
                {
                    if ((index & 1) == 0 && queue.TryDequeue(out var ready))
                    {
                        Assert.True(seen.TryAdd(ready.Article.ArtSize, 0));
                        continue;
                    }

                    try
                    {
                        var delivery = await queue.DequeueAsync(started.Token);
                        if (delivery.HasArticle)
                        {
                            Assert.True(seen.TryAdd(delivery.Article.ArtSize, 0));
                        }
                    }
                    catch (OperationCanceledException) when (started.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }).ToArray();

            for (var i = 0; i < count; i++)
            {
                Assert.True(queue.TryEnqueue(Create(i)));
            }

            queue.Complete();
            await Task.WhenAll(consumers);
            Assert.Equal(count, seen.Count);
            Assert.Equal(0, queue.QueuedBytes);
            Assert.Equal(count, queue.DequeuedCount);
        }

        [Fact]
        public void Reserve_then_commit_does_not_double_count_bytes()
        {
            var queue = new AcceptedArticleQueue(1_000, TimeProvider.System);
            Assert.True(queue.TryReserve(600, out var reservation));
            Assert.Equal(600, queue.ReservedBytes);
            Assert.Equal(0, queue.QueuedBytes);
            Assert.False(queue.TryReserve(600, out _));
            var article = Create(599);
            Assert.True(queue.TryCommit(reservation, article));
            Assert.Equal(0, queue.ReservedBytes);
            Assert.Equal(article.ArtSize, queue.QueuedBytes);
            Assert.Equal(article.ArtSize, queue.AccountedBytes);
            Assert.False(queue.TryCommit(reservation, article));
            Assert.Equal(article.ArtSize, queue.AccountedBytes);
        }

        [Fact]
        public async Task Dequeue_wakes_the_oldest_waiter_and_a_later_waiter_stays_blocked()
        {
            var queue = new AcceptedArticleQueue(100, TimeProvider.System);
            Assert.True(queue.TryReserve(80, out var held));
            var first = queue.ReserveAsync(80, CancellationToken.None);
            var second = queue.ReserveAsync(40, CancellationToken.None);
            await WaitUntilAsync(() => queue.AdmissionWaiters == 2);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            queue.Release(held);
            var granted = await first.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(granted);
            Assert.Equal(80, granted.Value.Bytes);
            Assert.False(second.IsCompleted);
            Assert.Equal(80, queue.ReservedBytes);
            queue.Release(granted.Value);
            var next = await second.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(40, next!.Value.Bytes);
        }

        [Fact]
        public async Task Cancelling_a_waiter_that_does_not_fit_admits_the_next_waiter_that_does()
        {
            var queue = new AcceptedArticleQueue(100, TimeProvider.System);
            Assert.True(queue.TryReserve(60, out var held));
            using var cancel = new CancellationTokenSource();
            var blocked = queue.ReserveAsync(50, cancel.Token);
            var fits = queue.ReserveAsync(30, CancellationToken.None);
            await WaitUntilAsync(() => queue.AdmissionWaiters == 2);
            Assert.False(fits.IsCompleted);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await blocked);
            var granted = await fits.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(granted);
            Assert.Equal(30, granted.Value.Bytes);
            Assert.Equal(90, queue.AccountedBytes);
            Assert.Equal(0, queue.AdmissionWaiters);
            queue.Release(held);
            queue.Release(granted.Value);
            Assert.Equal(0, queue.AccountedBytes);
        }

        [Fact]
        public async Task A_larger_head_waiter_blocks_a_smaller_waiter_that_would_fit()
        {
            var queue = new AcceptedArticleQueue(100, TimeProvider.System);
            Assert.True(queue.TryReserve(50, out var held));
            var head = queue.ReserveAsync(70, CancellationToken.None);
            var smaller = queue.ReserveAsync(40, CancellationToken.None);
            await WaitUntilAsync(() => queue.AdmissionWaiters == 2);
            Assert.False(head.IsCompleted);
            Assert.False(smaller.IsCompleted);
            Assert.Equal(50, queue.AccountedBytes);
            queue.Release(held);
            var granted = await head.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(70, granted!.Value.Bytes);
            Assert.False(smaller.IsCompleted);
            Assert.Equal(70, queue.ReservedBytes);
            queue.Release(granted.Value);
            var next = await smaller.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(40, next!.Value.Bytes);
            queue.Release(next.Value);
            Assert.Equal(0, queue.AccountedBytes);
            Assert.Equal(0, queue.AdmissionWaiters);
        }

        [Fact]
        public async Task Concurrent_reservations_never_exceed_the_byte_budget()
        {
            var queue = new AcceptedArticleQueue(250, TimeProvider.System);
            var tasks = Enumerable.Range(0, 8)
                .Select(_ => queue.ReserveAsync(100, CancellationToken.None).AsTask())
                .ToArray();
            await WaitUntilAsync(() => queue.AdmissionWaiters == 6);
            Assert.Equal(200, queue.AccountedBytes);
            var released = new HashSet<long>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (released.Count < 8)
            {
                timeout.Token.ThrowIfCancellationRequested();
                Assert.InRange(queue.AccountedBytes, 0, 250);
                foreach (var task in tasks)
                {
                    if (!task.IsCompletedSuccessfully)
                    {
                        continue;
                    }

                    var reservation = await task;
                    if (reservation is not { } granted || !released.Add(granted.Id))
                    {
                        continue;
                    }

                    queue.Release(granted);
                }

                await Task.Delay(1, timeout.Token);
            }

            Assert.Equal(0, queue.AccountedBytes);
            Assert.Equal(0, queue.AdmissionWaiters);
        }

        [Fact]
        public async Task Cancellation_and_shutdown_release_waiters_without_a_reservation()
        {
            var queue = new AcceptedArticleQueue(10, TimeProvider.System);
            Assert.True(queue.TryReserve(10, out _));
            using var cts = new CancellationTokenSource();
            var waiting = queue.ReserveAsync(10, cts.Token);
            await WaitUntilAsync(() => queue.AdmissionWaiters == 1);
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
            Assert.Equal(0, queue.AdmissionWaiters);
            Assert.Equal(10, queue.AccountedBytes);

            var blocked = queue.ReserveAsync(10, CancellationToken.None);
            await WaitUntilAsync(() => queue.AdmissionWaiters == 1);
            queue.Complete();
            Assert.Null(await blocked);
            Assert.Equal(0, queue.AdmissionWaiters);
            Assert.Equal(10, queue.ReservedBytes);
        }

        [Fact]
        public void TryEnqueue_counts_reserved_bytes_against_the_same_budget()
        {
            var queue = new AcceptedArticleQueue(1_000, TimeProvider.System);
            Assert.True(queue.TryReserve(600, out var reservation));
            Assert.False(queue.TryEnqueue(Create(499)));
            Assert.Equal(600, queue.ReservedBytes);
            Assert.Equal(0, queue.QueuedBytes);
            queue.Release(reservation);
            Assert.Equal(0, queue.AccountedBytes);
            queue.Release(reservation);
            Assert.Equal(0, queue.AccountedBytes);
        }

        [Fact]
        public void Commit_of_a_different_length_releases_the_reservation()
        {
            var queue = new AcceptedArticleQueue(1_000, TimeProvider.System);
            Assert.True(queue.TryReserve(100, out var reservation));
            Assert.False(queue.TryCommit(reservation, Create(50)));
            Assert.Equal(0, queue.ReservedBytes);
            Assert.Equal(0, queue.QueuedBytes);
            Assert.Equal(0, queue.EnqueuedCount);
        }

        [Fact]
        public void Commit_after_shutdown_releases_the_reservation_without_queueing()
        {
            var queue = new AcceptedArticleQueue(1_000, TimeProvider.System);
            Assert.True(queue.TryReserve(100, out var reservation));
            queue.Complete();
            Assert.Equal(100, queue.ReservedBytes);
            Assert.False(queue.TryCommit(reservation, Create(99)));
            Assert.Equal(0, queue.ReservedBytes);
            Assert.Equal(0, queue.QueuedBytes);
            Assert.Equal(0, queue.EnqueuedCount);
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!condition())
            {
                await Task.Delay(5, timeout.Token);
            }
        }

        private static AcceptedArticle Create(int size)
        {
            var bytes = new byte[size + 1];
            bytes[0] = (byte)(size % 251);
            return new AcceptedArticle(default, (ulong)size, bytes.Length, bytes);
        }
    }
}

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.StorageService.Storage.SpoolPersistence;

namespace VectorNNTPD.StorageService.Tests.Ingress
{
    /// <summary>
    /// Graceful-shutdown drain contract: every article accepted before queue completion is awaited,
    /// including in-flight persistence, and a failed persist fails stop.
    /// </summary>
    public sealed class AcceptedArticleWorkerDrainTests
    {
        private const string CacheFqdn = "cache01.usenet.ninja";
        private static readonly TimeSpan Safety = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task Stop_drains_multiple_queued_articles_to_the_processor()
        {
            var queue = new AcceptedArticleQueue();
            var processed = new List<string>();
            var processor = new RecordingProcessor(article =>
            {
                lock (processed)
                {
                    processed.Add(Encoding.ASCII.GetString(article.ArtData.Span));
                }

                return AcceptedArticleProcessResult.Completed;
            });
            var worker = new AcceptedArticleWorker(queue, processor, NullLogger<AcceptedArticleWorker>.Instance);
            await worker.StartAsync(CancellationToken.None);

            Assert.True(queue.TryEnqueue(ToQueued(Build("<drain-a@seg.test>", "alpha\r\n"))));
            Assert.True(queue.TryEnqueue(ToQueued(Build("<drain-b@seg.test>", "beta\r\n"))));
            Assert.True(queue.TryEnqueue(ToQueued(Build("<drain-c@seg.test>", "gamma\r\n"))));
            queue.Complete();

            await worker.StopAsync(CancellationToken.None);

            Assert.Equal(3, processor.Completed);
            Assert.Equal(0, queue.QueuedCount);
            Assert.Equal(0, queue.QueuedBytes);
            Assert.Equal(0, queue.ReservedBytes);
            Assert.Equal(0, worker.ActiveWorkers);
            Assert.True(worker.Execution!.IsCompletedSuccessfully);
            lock (processed)
            {
                Assert.Contains(processed, static text => text.Contains("alpha\r\n", StringComparison.Ordinal));
                Assert.Contains(processed, static text => text.Contains("beta\r\n", StringComparison.Ordinal));
                Assert.Contains(processed, static text => text.Contains("gamma\r\n", StringComparison.Ordinal));
            }
        }

        [Fact]
        public async Task Stop_waits_for_an_in_flight_article_before_returning()
        {
            var queue = new AcceptedArticleQueue();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var processor = new RecordingProcessor(async (article, cancellationToken) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return AcceptedArticleProcessResult.Completed;
            });
            var worker = new AcceptedArticleWorker(
                queue,
                processor,
                NullLogger<AcceptedArticleWorker>.Instance,
                new SpoolPersistenceOptions { MinWorkers = 1, MaxWorkers = 1 });
            await worker.StartAsync(CancellationToken.None);
            Assert.True(queue.TryEnqueue(ToQueued(Build("<drain-inflight@seg.test>", "held-body\r\n"))));
            await entered.Task.WaitAsync(Safety);

            var stopping = worker.StopAsync(CancellationToken.None);
            Assert.False(stopping.IsCompleted);
            await Task.Delay(50);
            Assert.False(stopping.IsCompleted);
            Assert.Equal(0, processor.Completed);

            release.TrySetResult();
            await stopping.WaitAsync(Safety);
            Assert.Equal(1, processor.Completed);
            Assert.True(worker.Execution!.IsCompletedSuccessfully);
        }

        [Fact]
        public void Complete_rejects_commit_of_a_reservation_taken_before_completion()
        {
            var queue = new AcceptedArticleQueue(maxQueuedBytes: 1024, TimeProvider.System);
            Assert.True(queue.TryReserve(64, out var reservation));
            queue.Complete();
            Assert.False(queue.TryCommit(reservation, CreateExact(64)));
            Assert.Equal(0, queue.QueuedCount);
            Assert.Equal(0, queue.ReservedBytes);
        }

        [Fact]
        public async Task Stop_throws_when_a_processor_reports_failed()
        {
            var queue = new AcceptedArticleQueue();
            var worker = new AcceptedArticleWorker(
                queue,
                new RecordingProcessor(_ => AcceptedArticleProcessResult.Failed),
                NullLogger<AcceptedArticleWorker>.Instance);
            await worker.StartAsync(CancellationToken.None);
            Assert.True(queue.TryEnqueue(ToQueued(Build("<drain-fail@seg.test>", "fail-body\r\n"))));
            queue.Complete();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.StopAsync(CancellationToken.None));
            Assert.Contains("failed or abandoned", ex.Message, StringComparison.Ordinal);
            Assert.True(worker.Execution!.IsCompletedSuccessfully);
        }

        [Fact]
        public async Task Host_cancellation_during_in_flight_work_fails_stop_after_abandon()
        {
            var queue = new AcceptedArticleQueue();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var processor = new RecordingProcessor(async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return AcceptedArticleProcessResult.Completed;
            });
            var worker = new AcceptedArticleWorker(
                queue,
                processor,
                NullLogger<AcceptedArticleWorker>.Instance,
                new SpoolPersistenceOptions { MinWorkers = 1, MaxWorkers = 1 });
            await worker.StartAsync(CancellationToken.None);
            Assert.True(queue.TryEnqueue(ToQueued(Build("<drain-cancel@seg.test>", "cancel-body\r\n"))));
            await entered.Task.WaitAsync(Safety);

            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.StopAsync(stop.Token));
            Assert.Contains("did not finish", ex.Message, StringComparison.Ordinal);
            Assert.Equal(0, processor.Completed);
        }

        [Fact]
        public async Task Stop_with_empty_queue_completes_promptly()
        {
            var queue = new AcceptedArticleQueue();
            var worker = new AcceptedArticleWorker(
                queue,
                new RecordingProcessor(_ => AcceptedArticleProcessResult.Completed),
                NullLogger<AcceptedArticleWorker>.Instance);
            await worker.StartAsync(CancellationToken.None);

            using var stop = new CancellationTokenSource(Safety);
            await worker.StopAsync(stop.Token);
            Assert.True(worker.Execution!.IsCompletedSuccessfully);
            Assert.Equal(0, worker.ActiveWorkers);
            Assert.Equal(0, queue.QueuedBytes);
            Assert.Equal(0, queue.ReservedBytes);
        }

        [Fact]
        public async Task Stop_completes_the_queue_when_the_listener_has_not()
        {
            var queue = new AcceptedArticleQueue();
            var worker = new AcceptedArticleWorker(
                queue,
                new RecordingProcessor(_ => AcceptedArticleProcessResult.Completed),
                NullLogger<AcceptedArticleWorker>.Instance);
            await worker.StartAsync(CancellationToken.None);
            Assert.False(queue.IsCompleted);
            await worker.StopAsync(CancellationToken.None);
            Assert.True(queue.IsCompleted);
        }

        private static AcceptedArticle ToQueued(Built inbound) =>
            new(inbound.Record.ArtId, inbound.Record.ArtHash, inbound.Record.ArtSize, inbound.Record.ArtData);

        private static AcceptedArticle CreateExact(int size)
        {
            var data = new byte[size];
            Array.Fill(data, (byte)'z');
            return new AcceptedArticle(ArticleId.FromMessageId("<exact@seg.test>"u8), 1, size, data);
        }

        private static Built Build(string messageId, string body)
        {
            var parser = new NntpArticleParser(CacheFqdn);
            var text = new StringBuilder()
                .Append("Path: peer.example\r\n")
                .Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n")
                .Append("Message-ID: ").Append(messageId).Append("\r\n")
                .Append("Newsgroups: alt.test\r\n")
                .Append("From: user@example.test\r\n")
                .Append("Subject: drain\r\n")
                .Append("\r\n")
                .Append(body);
            var created = ArticleRecordFactory.TryCreate(
                parser,
                Encoding.ASCII.GetBytes(text.ToString()),
                ArticlePathMode.Normalize);
            Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
            return new Built(created.Record);
        }

        private readonly record struct Built(ArticleRecord Record);

        private sealed class RecordingProcessor : IAcceptedArticleProcessor
        {
            private readonly Func<AcceptedArticle, CancellationToken, Task<AcceptedArticleProcessResult>> _handler;

            public RecordingProcessor(Func<AcceptedArticle, AcceptedArticleProcessResult> handler)
                : this((article, _) => Task.FromResult(handler(article)))
            {
            }

            public RecordingProcessor(Func<AcceptedArticle, CancellationToken, Task<AcceptedArticleProcessResult>> handler)
            {
                _handler = handler;
            }

            public int Completed { get; private set; }

            public void EnsureReady()
            {
            }

            public async ValueTask<AcceptedArticleProcessResult> ProcessAsync(
                AcceptedArticle article,
                CancellationToken cancellationToken)
            {
                var result = await _handler(article, cancellationToken).ConfigureAwait(false);
                if (result == AcceptedArticleProcessResult.Completed)
                {
                    Completed++;
                }

                return result;
            }
        }
    }
}

using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.StorageService.Ingress;

namespace VectorNNTPD.StorageService.Tests.Ingress
{

    public sealed class AcceptedArticleWorkerTests
    {
        private const string CacheFqdn = "cache01.usenet.ninja";
        private static readonly TimeSpan Safety = TimeSpan.FromSeconds(2);

        [Fact]
        public async Task Worker_applies_the_path_hop_and_discards_the_rewritten_article()
        {
            var inbound = Build("<worker-path@seg.test>", "body-line\r\n");
            var queue = new AcceptedArticleQueue();
            var seen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var processor = new AcceptedArticlePathProcessor(
                CacheFqdn,
                NullLogger<AcceptedArticlePathProcessor>.Instance,
                bytes => seen.TrySetResult(bytes.ToArray()));
            var worker = new AcceptedArticleWorker(
                queue,
                processor,
                NullLogger<AcceptedArticleWorker>.Instance);

            await worker.StartAsync(CancellationToken.None);
            Assert.True(queue.TryEnqueue(ToQueued(inbound)));
            var rewritten = await seen.Task.WaitAsync(Safety);
            var text = Encoding.ASCII.GetString(rewritten);
            Assert.Contains("Path: cache01.usenet.ninja!", text, StringComparison.Ordinal);
            Assert.Contains("body-line\r\n", text, StringComparison.Ordinal);
            Assert.Equal(1, CountHop(text, CacheFqdn));
            Assert.Contains("news.usenet.ninja", text, StringComparison.Ordinal);
            Assert.False(rewritten.AsSpan().SequenceEqual(inbound.Record.ArtData.Span));

            queue.Complete();
            await worker.Execution!.WaitAsync(Safety);
            Assert.True(worker.Execution.IsCompletedSuccessfully);
            await worker.StopAsync(CancellationToken.None);
        }

        [Fact]
        public async Task Worker_prepends_the_hop_again_when_it_is_already_present()
        {
            var inbound = Build("<worker-again@seg.test>", "kept-body\r\n", CacheFqdn);
            var inboundText = Encoding.ASCII.GetString(inbound.Record.ArtData.Span);
            Assert.Equal(1, CountHop(inboundText, CacheFqdn));
            var queue = new AcceptedArticleQueue();
            var seen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new AcceptedArticleWorker(
                queue,
                new AcceptedArticlePathProcessor(
                    CacheFqdn,
                    NullLogger<AcceptedArticlePathProcessor>.Instance,
                    bytes => seen.TrySetResult(bytes.ToArray())),
                NullLogger<AcceptedArticleWorker>.Instance);

            await worker.StartAsync(CancellationToken.None);
            Assert.True(queue.TryEnqueue(ToQueued(inbound)));
            var text = Encoding.ASCII.GetString(await seen.Task.WaitAsync(Safety));
            Assert.Equal(2, CountHop(text, CacheFqdn));
            Assert.Equal(Body(inboundText), Body(text));
            queue.Complete();
            await worker.Execution!.WaitAsync(Safety);
        }

        [Fact]
        public async Task Worker_processes_articles_in_enqueue_order()
        {
            var first = Build("<worker-one@seg.test>", "alpha\r\n");
            var second = Build("<worker-two@seg.test>", "beta\r\n");
            var queue = new AcceptedArticleQueue();
            var seen = new List<string>();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new AcceptedArticleWorker(
                queue,
                new AcceptedArticlePathProcessor(
                    CacheFqdn,
                    NullLogger<AcceptedArticlePathProcessor>.Instance,
                    bytes =>
                    {
                        lock (seen)
                        {
                            seen.Add(Encoding.ASCII.GetString(bytes.Span));
                            if (seen.Count == 2)
                            {
                                done.TrySetResult();
                            }
                        }
                    }),
                NullLogger<AcceptedArticleWorker>.Instance);

            await worker.StartAsync(CancellationToken.None);
            Assert.True(queue.TryEnqueue(ToQueued(first)));
            Assert.True(queue.TryEnqueue(ToQueued(second)));
            await done.Task.WaitAsync(Safety);
            Assert.Contains("alpha\r\n", seen[0], StringComparison.Ordinal);
            Assert.Contains("beta\r\n", seen[1], StringComparison.Ordinal);
            queue.Complete();
            await worker.Execution!.WaitAsync(Safety);
        }

        [Fact]
        public async Task Worker_continues_after_a_rejected_article_and_a_thrown_article()
        {
            var good = Build("<worker-good@seg.test>", "good-body\r\n");
            var queue = new AcceptedArticleQueue();
            var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new AcceptedArticleWorker(
                queue,
                new FlakyThenPathProcessor(
                    new AcceptedArticlePathProcessor(
                        CacheFqdn,
                        NullLogger<AcceptedArticlePathProcessor>.Instance,
                        bytes => seen.TrySetResult(Encoding.ASCII.GetString(bytes.Span)))),
                NullLogger<AcceptedArticleWorker>.Instance);

            await worker.StartAsync(CancellationToken.None);
            Assert.True(queue.TryEnqueue(new AcceptedArticle(default, 1, 1, new byte[] { (byte)'x' })));
            Assert.True(queue.TryEnqueue(new AcceptedArticle(default, 1, 1, new byte[] { (byte)'y' })));
            Assert.True(queue.TryEnqueue(ToQueued(good)));
            var text = await seen.Task.WaitAsync(Safety);
            Assert.Contains("good-body\r\n", text, StringComparison.Ordinal);
            Assert.Contains("Path: cache01.usenet.ninja!", text, StringComparison.Ordinal);
            queue.Complete();
            await worker.Execution!.WaitAsync(Safety);
            Assert.True(worker.Execution.IsCompletedSuccessfully);
        }

        [Fact]
        public void Path_rewrite_matches_the_canonical_factory_bytes()
        {
            var inbound = Build("<rewrite-match@seg.test>", "body-line\r\nsecond\r\n");
            var processor = new AcceptedArticlePathProcessor(CacheFqdn, NullLogger.Instance);
            Assert.True(processor.TryRewrite(ToQueued(inbound), out var rewritten));
            var created = ArticleRecordFactory.TryCreate(
                new NntpArticleParser(CacheFqdn),
                inbound.Record.ArtData,
                ArticlePathMode.Traverse);
            Assert.True(created.IsAccepted);
            Assert.True(created.Record.ArtData.Span.SequenceEqual(rewritten.Span));
            Assert.Equal(inbound.Record.ArtId, created.Record.ArtId);

            var wrong = new AcceptedArticle(
                ArticleId.FromMessageId("<other@seg.test>"u8),
                inbound.Record.ArtHash,
                inbound.Record.ArtSize,
                inbound.Record.ArtData);
            Assert.False(processor.TryRewrite(wrong, out _));
        }

        [Fact]
        public void Rented_path_rewrite_matches_the_exact_buffer_and_is_returned()
        {
            var inbound = Build("<rewrite-rent@seg.test>", "body-line\r\nsecond\r\n");
            var processor = new AcceptedArticlePathProcessor(CacheFqdn, NullLogger.Instance);
            Assert.True(processor.TryRewrite(ToQueued(inbound), out var exact));
            Assert.True(processor.TryRewriteRented(ToQueued(inbound), out var rented, out var length));
            Assert.NotNull(rented);
            Assert.True(exact.Span.SequenceEqual(rented.AsSpan(0, length)));
            AcceptedArticlePathProcessor.ReturnRewriteBuffer(rented);
            Assert.True(processor.TryRewriteRented(ToQueued(inbound), out var again, out var againLength));
            Assert.True(exact.Span.SequenceEqual(again.AsSpan(0, againLength)));
            AcceptedArticlePathProcessor.ReturnRewriteBuffer(again!);
        }

        [Fact]
        public async Task Worker_exits_when_the_queue_is_completed()
        {
            var queue = new AcceptedArticleQueue();
            queue.Complete();
            var worker = new AcceptedArticleWorker(
                queue,
                new AcceptedArticlePathProcessor(CacheFqdn, NullLogger<AcceptedArticlePathProcessor>.Instance),
                NullLogger<AcceptedArticleWorker>.Instance);

            await worker.StartAsync(CancellationToken.None);
            await worker.Execution!.WaitAsync(Safety);
            Assert.True(worker.Execution.IsCompletedSuccessfully);
            await worker.StopAsync(CancellationToken.None);
        }

        [Fact]
        public async Task Worker_stop_unblocks_an_empty_queue()
        {
            var queue = new AcceptedArticleQueue();
            var worker = new AcceptedArticleWorker(
                queue,
                new AcceptedArticlePathProcessor(CacheFqdn, NullLogger<AcceptedArticlePathProcessor>.Instance),
                NullLogger<AcceptedArticleWorker>.Instance);

            await worker.StartAsync(CancellationToken.None);
            using var stop = new CancellationTokenSource(Safety);
            await worker.StopAsync(stop.Token);
            Assert.True(worker.Execution!.IsCompletedSuccessfully);
        }

        private static AcceptedArticle ToQueued(Built inbound) =>
            new(inbound.Record.ArtId, inbound.Record.ArtHash, inbound.Record.ArtSize, inbound.Record.ArtData);

        private static int CountHop(string article, string hop) =>
            article.Split(hop, StringSplitOptions.None).Length - 1;

        private static string Body(string article)
        {
            var split = article.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            Assert.True(split >= 0);
            return article[(split + 4)..];
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
            return new Built(created.Record);
        }

        private readonly record struct Built(ArticleRecord Record);

        private sealed class FlakyThenPathProcessor(AcceptedArticlePathProcessor inner) : IAcceptedArticleProcessor
        {
            private int _calls;

            public void EnsureReady() => inner.EnsureReady();

            public ValueTask<AcceptedArticleProcessResult> ProcessAsync(
                AcceptedArticle article,
                CancellationToken cancellationToken)
            {
                var call = Interlocked.Increment(ref _calls);
                if (call == 2)
                {
                    throw new InvalidOperationException("recoverable article failure");
                }

                return inner.ProcessAsync(article, cancellationToken);
            }
        }
    }
}

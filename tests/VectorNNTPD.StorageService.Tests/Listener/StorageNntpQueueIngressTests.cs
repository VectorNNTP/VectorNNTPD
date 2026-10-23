using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.StorageService.Listener;
using VectorNNTPD.StorageService;
using VectorNNTPD.StorageService.Storage.SegmentPools;
using VectorNNTPD.StorageService.Storage.SpoolPersistence;
using VectorNNTPD.StorageService.Storage.SpoolShards;

namespace VectorNNTPD.StorageService.Tests.Listener
{
    /// <summary>
    /// In-process NNTP TAKETHIS ingress against <see cref="StorageNntpFeedSession"/> over a pipe transport.
    /// </summary>
    public sealed class StorageNntpQueueIngressTests
    {
        private const string CacheFqdn = "cache01.usenet.ninja";
        private static readonly TimeSpan Safety = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task TakeThis_enqueues_the_original_article_without_this_servers_path_hop()
        {
            var inbound = Build("<store-path@seg.test>", "body-line\r\n");
            var queue = new AcceptedArticleQueue();
            await using var harness = await Harness.StartAsync(queue);
            Assert.Equal(239, await harness.TakeThisAsync(inbound));

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
        public async Task Second_takeThis_of_the_same_bytes_enqueues_another_unmodified_copy()
        {
            var inbound = Build("<store-again@seg.test>", "body\r\n");
            var queue = new AcceptedArticleQueue();
            await using var harness = await Harness.StartAsync(queue);
            Assert.Equal(239, await harness.TakeThisAsync(inbound));
            Assert.Equal(239, await harness.TakeThisAsync(inbound));

            var first = await queue.DequeueAsync(CancellationToken.None);
            var second = await queue.DequeueAsync(CancellationToken.None);
            Assert.True(first.HasArticle && second.HasArticle);
            Assert.True(first.Article.ArtData.Span.SequenceEqual(inbound.Record.ArtData.Span));
            Assert.True(second.Article.ArtData.Span.SequenceEqual(inbound.Record.ArtData.Span));
        }

        [Fact]
        public async Task Status_239_is_sent_before_persistence_finishes()
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
                Assert.Equal(239, await harness.TakeThisAsync(inbound).WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.Equal(0, processor.Finished);
            }
            finally
            {
                release.TrySetResult();
                await worker.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Message_id_mismatch_is_rejected_without_enqueue()
        {
            var real = Build("<store-real@seg.test>", "body\r\n");
            var queue = new AcceptedArticleQueue();
            await using var harness = await Harness.StartAsync(queue);
            Assert.Equal(439, await harness.TakeThisRawAsync("<store-other@seg.test>", real.Record.ArtData));
            Assert.Equal(0, queue.EnqueuedCount);
            Assert.Equal(0, queue.ReservedBytes);
            Assert.Equal(0, queue.AccountedBytes);
        }

        [Fact]
        public async Task Malformed_article_is_rejected_without_enqueue()
        {
            var queue = new AcceptedArticleQueue();
            await using var harness = await Harness.StartAsync(queue);
            var junk = Encoding.ASCII.GetBytes("not-an-article\r\n");
            Assert.Equal(439, await harness.TakeThisRawAsync("<bad-article@seg.test>", junk));
            Assert.Equal(0, queue.EnqueuedCount);
        }

        [Fact]
        public async Task Completed_queue_returns_400_and_closes()
        {
            var built = Build("<store-closed@seg.test>", "body\r\n");
            var queue = new AcceptedArticleQueue();
            queue.Complete();
            await using var harness = await Harness.StartAsync(queue);
            Assert.Equal(400, await harness.TakeThisAsync(built));
            Assert.Equal(0, queue.ReservedBytes);
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
            var leftTask = left.TakeThisAsync(first);
            var rightTask = right.TakeThisAsync(second);
            Assert.Equal(239, await leftTask);
            Assert.Equal(239, await rightTask);

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
        public async Task Full_queue_blocks_on_reserve_until_dequeue_then_accepts()
        {
            var first = Build("<admit-held@seg.test>", "alpha-body\r\n");
            var queue = new AcceptedArticleQueue(first.Record.ArtSize, TimeProvider.System);
            await using var held = await Harness.StartAsync(queue);
            Assert.Equal(239, await held.TakeThisAsync(first));

            var second = Build("<admit-wait@seg.test>", "beta-body\r\n");
            await using var waiting = await Harness.StartAsync(queue);
            var pending = waiting.TakeThisAsync(second);
            await WaitUntilAsync(() => queue.AdmissionWaiters == 1);
            Assert.Equal(0, queue.ReservedBytes);
            Assert.Equal(first.Record.ArtSize, queue.AccountedBytes);
            Assert.False(pending.IsCompleted);

            Assert.True((await queue.DequeueAsync(CancellationToken.None)).HasArticle);
            Assert.Equal(239, await pending.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, queue.ReservedBytes);
            Assert.Equal(second.Record.ArtSize, queue.QueuedBytes);
            var delivery = await queue.DequeueAsync(CancellationToken.None);
            Assert.True(delivery.Article.ArtData.Span.SequenceEqual(second.Record.ArtData.Span));
        }

        [Fact]
        public async Task Article_larger_than_queue_budget_is_rejected()
        {
            var built = Build("<admit-oversize@seg.test>", "too-big-for-budget\r\n");
            var queue = new AcceptedArticleQueue(8, TimeProvider.System);
            await using var harness = await Harness.StartAsync(queue);
            Assert.Equal(439, await harness.TakeThisAsync(built));
            Assert.Equal(0, queue.EnqueuedCount);
            Assert.Equal(0, queue.ReservedBytes);
        }

        [Fact]
        public async Task Disconnect_mid_transfer_does_not_enqueue()
        {
            var built = Build("<admit-drop@seg.test>", new string('x', 128) + "\r\n");
            var queue = new AcceptedArticleQueue(built.Record.ArtSize, TimeProvider.System);
            var session = await Harness.StartAsync(queue);
            await session.SendRawAsync(Encoding.ASCII.GetBytes("TAKETHIS <admit-drop@seg.test>\r\n"));
            await session.SendRawAsync(Encoding.ASCII.GetBytes("Path: peer.example\r\n"));
            await session.DisposeAsync();

            Assert.Equal(0, queue.EnqueuedCount);
            Assert.Equal(0, queue.ReservedBytes);
            Assert.Equal(0, queue.AccountedBytes);
            Assert.Equal(0, queue.AdmissionWaiters);

            await using var retry = await Harness.StartAsync(queue);
            Assert.Equal(239, await retry.TakeThisAsync(built));
            Assert.Equal(built.Record.ArtSize, queue.QueuedBytes);
        }

        [Fact]
        public async Task Dot_stuffed_article_enqueues_with_matching_art_id()
        {
            var inbound = Build("<dot-stuff@seg.test>", "..leading\r\n.middle\r\n");
            var queue = new AcceptedArticleQueue();
            await using var harness = await Harness.StartAsync(queue);
            Assert.Equal(239, await harness.TakeThisAsync(inbound));
            var delivery = await queue.DequeueAsync(CancellationToken.None);
            Assert.True(delivery.HasArticle);
            Assert.Equal(inbound.Record.ArtId, delivery.Article.ArtId);
            Assert.Contains("..leading\r\n", Encoding.ASCII.GetString(delivery.Article.ArtData.Span), StringComparison.Ordinal);
            Assert.Contains(".middle\r\n", Encoding.ASCII.GetString(delivery.Article.ArtData.Span), StringComparison.Ordinal);
        }

        [Fact]
        public async Task TakeThis_dot_bodies_persist_canonical_body_with_storage_path_hop()
        {
            // Wire TAKETHIS (transport-stuffed) → queue CanonicalV1 → worker Traverse → spool file.
            var inbound = Build(
                "<wire-to-spool-dots@seg.test>",
                "Hello\r\n.leading\r\n..leading\r\n...leading\r\n");
            var expectedBody = Body(Encoding.ASCII.GetString(inbound.Record.ArtData.Span));
            Assert.Equal("Hello\r\n..leading\r\n...leading\r\n....leading\r\n", expectedBody);

            var root = Path.Join(Path.GetTempPath(), "vectornntp-wire-spool-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(root);
            var queue = new AcceptedArticleQueue();
            AcceptedArticleWorker? worker = null;
            BlockDeviceMonitorService? monitor = null;
            try
            {
                var probe = new StaticFreeSpaceProbe();
                probe.Set(root, total: 100_000_000_000, available: 80_000_000_000);
                var catalog = new SegmentPoolCatalog();
                catalog.Publish([new SegmentPoolDevice("block:a", [root])]);
                monitor = new BlockDeviceMonitorService(
                    catalog,
                    new RisingCounters(),
                    NullLogger<BlockDeviceMonitorService>.Instance,
                    TimeSpan.FromMilliseconds(20),
                    TimeProvider.System,
                    probe);
                await monitor.StartAsync(CancellationToken.None);
                await WaitUntilAsync(() => monitor.Observations.Space.ContainsKey(root));

                var processor = new SpoolArticlePersistenceProcessor(
                    CacheFqdn,
                    catalog,
                    monitor,
                    new SpoolWriteGate(),
                    new SpoolArticleIdentityGate(),
                    new SpoolHealth(TimeProvider.System),
                    new SpoolPressure(),
                    new SpoolArticlePublisher(new FileSpoolArticleFiles()),
                    new SpoolPersistenceCounters(),
                    new StorageServerOptions { SpoolPersistence = new SpoolPersistenceOptions() },
                    NullLogger.Instance,
                    TimeProvider.System);
                worker = new AcceptedArticleWorker(
                    queue,
                    processor,
                    NullLogger<AcceptedArticleWorker>.Instance,
                    new SpoolPersistenceOptions { MinWorkers = 1, MaxWorkers = 1 },
                    new SpoolPressure(),
                    new SpoolPersistenceCounters(),
                    TimeProvider.System);
                await worker.StartAsync(CancellationToken.None);

                await using var harness = await Harness.StartAsync(queue);
                Assert.Equal(239, await harness.TakeThisAsync(inbound).WaitAsync(Safety));

                var spoolPath = SpoolArticlePath.CombineArticle(root, inbound.Record.ArtId);
                await WaitUntilAsync(() => File.Exists(spoolPath));
                var stored = Encoding.ASCII.GetString(File.ReadAllBytes(spoolPath));
                Assert.StartsWith($"Path: {CacheFqdn}!", HeaderLine(stored, "Path"), StringComparison.Ordinal);
                Assert.Equal(expectedBody, Body(stored));
                Assert.DoesNotContain(".....\r\n", stored, StringComparison.Ordinal);
            }
            finally
            {
                if (worker is not null)
                {
                    queue.Complete();
                    await worker.StopAsync(CancellationToken.None);
                }

                if (monitor is not null)
                {
                    await monitor.StopAsync(CancellationToken.None);
                }

                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        [Fact]
        public async Task Pipelined_second_takeThis_with_large_residue_still_enqueues()
        {
            // After the first terminator, leftover must hold the next command plus article bytes
            // even when that residue exceeds the 512-octet command-line limit.
            var first = Build("<pipe-first@seg.test>", "first-body\r\n");
            var second = Build("<pipe-second@seg.test>", new string('y', 700) + "\r\n");
            var queue = new AcceptedArticleQueue();
            await using var harness = await Harness.StartAsync(queue);

            var firstId = Encoding.ASCII.GetString(first.Record.MessageId.ToArray());
            var secondId = Encoding.ASCII.GetString(second.Record.MessageId.ToArray());
            var secondCommand = Encoding.ASCII.GetBytes("TAKETHIS " + secondId + "\r\n");
            var secondBody = DotStuff(second.Logical);
            Assert.True(
                secondCommand.Length + secondBody.Length + 3 > 512,
                "Residue after the first terminator must exceed the 512-octet command buffer.");

            // Complete the first article's body, then deliver terminator + entire second TAKETHIS in one
            // chunk so the payload reader must PreserveLeftover a residue larger than 512 octets.
            await harness.SendRawAsync(Encoding.ASCII.GetBytes("TAKETHIS " + firstId + "\r\n"));
            await harness.SendRawAsync(DotStuff(first.Logical));
            var afterFirst = new byte[3 + secondCommand.Length + secondBody.Length + 3];
            ".\r\n"u8.CopyTo(afterFirst);
            secondCommand.CopyTo(afterFirst.AsSpan(3));
            secondBody.CopyTo(afterFirst.AsSpan(3 + secondCommand.Length));
            ".\r\n"u8.CopyTo(afterFirst.AsSpan(3 + secondCommand.Length + secondBody.Length));
            await harness.SendRawAsync(afterFirst);

            var firstStatus = await harness.ReadStatusAsync();
            var secondStatus = await harness.ReadStatusAsync();
            Assert.Equal((239, 239), (firstStatus, secondStatus));

            var a = await queue.DequeueAsync(CancellationToken.None);
            var b = await queue.DequeueAsync(CancellationToken.None);
            Assert.True(a.HasArticle && b.HasArticle);
            var ids = new HashSet<ArticleId> { a.Article.ArtId, b.Article.ArtId };
            Assert.Contains(first.Record.ArtId, ids);
            Assert.Contains(second.Record.ArtId, ids);
        }

        [Fact]
        public async Task Split_article_payload_writes_still_enqueue()
        {
            var built = Build("<store-split@seg.test>", "chunked-body\r\n");
            var queue = new AcceptedArticleQueue();
            await using var harness = await Harness.StartAsync(queue);
            var messageId = Encoding.ASCII.GetString(built.Record.MessageId.ToArray());
            await harness.SendRawAsync(Encoding.ASCII.GetBytes("TAKETHIS " + messageId + "\r\n"));
            var stuffed = DotStuff(built.Logical);
            await harness.Transport.ClientWriteChunkedAsync(stuffed, 5);
            await harness.SendRawAsync(".\r\n"u8.ToArray());
            Assert.Equal(239, await harness.ReadStatusAsync());
            var delivery = await queue.DequeueAsync(CancellationToken.None);
            Assert.True(delivery.HasArticle);
            Assert.Equal(built.Record.ArtId, delivery.Article.ArtId);
            Assert.Contains("chunked-body\r\n", Encoding.ASCII.GetString(delivery.Article.ArtData.Span), StringComparison.Ordinal);
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!condition())
            {
                await Task.Delay(5, timeout.Token);
            }
        }

        private static int CountHop(string article, string hop) =>
            article.Split(hop, StringSplitOptions.None).Length - 1;

        private static string Body(string article)
        {
            var split = article.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            Assert.True(split >= 0);
            return article[(split + 4)..];
        }

        private static string HeaderLine(string article, string name)
        {
            var prefix = name + ": ";
            var start = article.IndexOf(prefix, StringComparison.Ordinal);
            Assert.True(start >= 0, name);
            var end = article.IndexOf("\r\n", start, StringComparison.Ordinal);
            Assert.True(end > start);
            return article[start..end];
        }

        private static byte[] EncodeTakeThis(Built built)
        {
            var messageId = built.Record.MessageId.ToArray();
            var command = Encoding.ASCII.GetBytes("TAKETHIS ");
            var crlf = "\r\n"u8.ToArray();
            var stuffed = DotStuff(built.Logical);
            var terminator = ".\r\n"u8.ToArray();
            var framed = new byte[command.Length + messageId.Length + crlf.Length + stuffed.Length + terminator.Length];
            var offset = 0;
            command.CopyTo(framed.AsSpan(offset));
            offset += command.Length;
            messageId.CopyTo(framed.AsSpan(offset));
            offset += messageId.Length;
            crlf.CopyTo(framed.AsSpan(offset));
            offset += crlf.Length;
            stuffed.CopyTo(framed.AsSpan(offset));
            offset += stuffed.Length;
            terminator.CopyTo(framed.AsSpan(offset));
            return framed;
        }

        private static byte[] DotStuff(ReadOnlySpan<byte> article)
        {
            using var ms = new MemoryStream(article.Length + 16);
            var atLineStart = true;
            for (var i = 0; i < article.Length; i++)
            {
                var b = article[i];
                if (atLineStart && b == (byte)'.')
                {
                    ms.WriteByte((byte)'.');
                }

                ms.WriteByte(b);
                atLineStart = b == (byte)'\n';
            }

            return ms.ToArray();
        }

        private static Built Build(string messageId, string body, string ingressFqdn = "nntpd01.usenet.ninja")
        {
            var parser = new NntpArticleParser(CacheFqdn);
            var path = string.Equals(ingressFqdn, CacheFqdn, StringComparison.Ordinal)
                ? CacheFqdn + "!peer.example"
                : "peer.example";
            var text = new StringBuilder()
                .Append("Path: ").Append(path).Append("\r\n")
                .Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n")
                .Append("Message-ID: ").Append(messageId).Append("\r\n")
                .Append("Newsgroups: alt.test\r\n")
                .Append("From: user@example.test\r\n")
                .Append("Subject: nntp\r\n")
                .Append("\r\n")
                .Append(body);
            var logical = Encoding.ASCII.GetBytes(text.ToString());
            var created = ArticleRecordFactory.TryCreate(
                parser,
                logical,
                ArticlePathMode.Normalize);
            Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
            return new Built(created.Record, logical);
        }

        private readonly record struct Built(ArticleRecord Record, byte[] Logical);

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

        private sealed class Harness : IAsyncDisposable
        {
            private readonly CancellationTokenSource _cts = new();
            private readonly StorageNntpFeedSession _session;
            private readonly Task _run;
            private readonly List<byte> _pendingResponse = [];

            private Harness(PipeTransport transport, StorageNntpFeedSession session)
            {
                Transport = transport;
                _session = session;
                _run = session.RunAsync(_cts.Token);
            }

            public PipeTransport Transport { get; }

            public static async Task<Harness> StartAsync(IAcceptedArticleQueue queue)
            {
                var transport = new PipeTransport();
                var session = new StorageNntpFeedSession(
                    transport,
                    new StorageServerListenerRuntimeOptions(262144, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), 8),
                    NullLogger.Instance,
                    CacheFqdn,
                    acceptedArticles: queue,
                    assemblyAdmission: new StoreAssemblyAdmission(8));
                var harness = new Harness(transport, session);
                var greeting = await harness.ReadStatusAsync();
                Assert.Equal(200, greeting);
                await harness.SendRawAsync("MODE STREAM\r\n"u8.ToArray());
                Assert.Equal(203, await harness.ReadStatusAsync());
                return harness;
            }

            public async Task<int> TakeThisAsync(Built built)
            {
                var messageId = built.Record.MessageId.ToArray();
                return await TakeThisRawAsync(Encoding.ASCII.GetString(messageId), built.Logical);
            }

            public async Task<int> TakeThisRawAsync(string messageId, ReadOnlyMemory<byte> logicalArticle)
            {
                var command = Encoding.ASCII.GetBytes("TAKETHIS " + messageId + "\r\n");
                await SendRawAsync(command);
                await SendRawAsync(DotStuff(logicalArticle.Span));
                await SendRawAsync(".\r\n"u8.ToArray());
                return await ReadStatusAsync();
            }

            public Task SendRawAsync(byte[] frame) => Transport.ClientWriteAsync(frame);

            public async Task<int> ReadStatusAsync()
            {
                using var timeout = new CancellationTokenSource(Safety);
                while (true)
                {
                    for (var i = 0; i < _pendingResponse.Count - 1; i++)
                    {
                        if (_pendingResponse[i] == (byte)'\r' && _pendingResponse[i + 1] == (byte)'\n')
                        {
                            var line = Encoding.ASCII.GetString(_pendingResponse.GetRange(0, i).ToArray());
                            _pendingResponse.RemoveRange(0, i + 2);
                            Assert.True(line.Length >= 3);
                            return ((line[0] - '0') * 100) + ((line[1] - '0') * 10) + (line[2] - '0');
                        }
                    }

                    var chunk = await Transport.ReadServerAsync(timeout.Token);
                    _pendingResponse.AddRange(chunk);
                }
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
                catch (EndOfStreamException ex)
                {
                    ExpectedException.Ignore(ex);
                }

                _cts.Dispose();
                await Transport.DisposeAsync();
                await _session.DisposeAsync();
            }
        }

        private sealed class PipeTransport : IStorageListenerTransport
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

        private sealed class StaticFreeSpaceProbe : ISpoolFreeSpaceProbe
        {
            private readonly Dictionary<string, (long Total, long Available)> _space = new(StringComparer.Ordinal);

            public void Set(string path, long total, long available) => _space[path] = (total, available);

            public bool TryRead(string spoolRoot, out long totalBytes, out long availableBytes)
            {
                if (!_space.TryGetValue(spoolRoot, out var sample))
                {
                    totalBytes = 0;
                    availableBytes = 0;
                    return false;
                }

                totalBytes = sample.Total;
                availableBytes = sample.Available;
                return true;
            }
        }

        private sealed class RisingCounters : IBlockDeviceCounterSource
        {
            private ulong _value = 1000;

            public bool TryRead(string deviceId, out BlockDeviceCounters counters, out string? error)
            {
                _value += 1000;
                counters = new BlockDeviceCounters(_value, _value, _value, _value, _value, _value, LatencyAvailable: true);
                error = null;
                return true;
            }
        }
    }
}

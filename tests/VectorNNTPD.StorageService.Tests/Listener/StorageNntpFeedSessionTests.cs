using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Core;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.StorageService.Listener;

namespace VectorNNTPD.StorageService.Tests.Listener
{
    public sealed class StorageNntpFeedSessionTests
    {
        private const string CacheFqdn = "cache01.usenet.ninja";
        private static readonly TimeSpan Safety = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task TakeThis_enqueues_article_and_returns_239()
        {
            var wire = BuildWireArticle("<take-ok@seg.test>", "body-line\r\n");
            var queue = new AcceptedArticleQueue();
            await using var harness = await Harness.StartAsync(queue);

            var status = await harness.TakeThisAsync(wire.MessageId, wire.Stuffed).WaitAsync(Safety);
            Assert.StartsWith("239 ", status, StringComparison.Ordinal);
            Assert.Contains(wire.MessageId, status, StringComparison.Ordinal);

            var delivery = await queue.DequeueAsync(CancellationToken.None).AsTask().WaitAsync(Safety);
            Assert.True(delivery.HasArticle);
            Assert.Equal(wire.ArtId, delivery.Article.ArtId);
            Assert.True(delivery.Article.ArtData.Span.SequenceEqual(wire.Canonical.Span));
            Assert.Equal(0, CountHop(Encoding.ASCII.GetString(delivery.Article.ArtData.Span), CacheFqdn));
        }

        [Fact]
        public async Task TakeThis_239_is_sent_before_persistence_finishes()
        {
            var wire = BuildWireArticle("<take-before-persist@seg.test>", "body\r\n");
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
                var status = await harness.TakeThisAsync(wire.MessageId, wire.Stuffed).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.StartsWith("239 ", status, StringComparison.Ordinal);
                Assert.Equal(0, processor.Finished);
            }
            finally
            {
                release.TrySetResult();
                await worker.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task TakeThis_message_id_mismatch_returns_439_and_does_not_enqueue()
        {
            var wire = BuildWireArticle("<take-real@seg.test>", "body\r\n");
            var queue = new AcceptedArticleQueue();
            await using var harness = await Harness.StartAsync(queue);

            var status = await harness.TakeThisAsync("<take-other@seg.test>", wire.Stuffed).WaitAsync(Safety);
            Assert.StartsWith("439 ", status, StringComparison.Ordinal);
            Assert.False(queue.TryDequeue(out _));
        }

        [Fact]
        public async Task Disconnect_during_article_does_not_enqueue()
        {
            var wire = BuildWireArticle("<take-cut@seg.test>", "body\r\n");
            var queue = new AcceptedArticleQueue();
            await using var harness = await Harness.StartAsync(queue);

            await harness.WriteClientAsync(Encoding.ASCII.GetBytes($"TAKETHIS {wire.MessageId}\r\n"));
            await harness.WriteClientAsync(wire.Stuffed[..Math.Min(32, wire.Stuffed.Length)]);
            await harness.DisposeTransportAsync();

            await Task.Delay(50);
            Assert.False(queue.TryDequeue(out _));
        }

        [Fact]
        public async Task Concurrent_sessions_enqueue_without_cross_contamination()
        {
            var left = BuildWireArticle("<take-left@seg.test>", "left-body\r\n");
            var right = BuildWireArticle("<take-right@seg.test>", "right-body\r\n");
            var queue = new AcceptedArticleQueue();
            await using var a = await Harness.StartAsync(queue);
            await using var b = await Harness.StartAsync(queue);

            var leftTask = a.TakeThisAsync(left.MessageId, left.Stuffed);
            var rightTask = b.TakeThisAsync(right.MessageId, right.Stuffed);
            var leftStatus = await leftTask.WaitAsync(Safety);
            var rightStatus = await rightTask.WaitAsync(Safety);
            Assert.StartsWith("239 ", leftStatus, StringComparison.Ordinal);
            Assert.StartsWith("239 ", rightStatus, StringComparison.Ordinal);

            var first = await queue.DequeueAsync(CancellationToken.None).AsTask().WaitAsync(Safety);
            var second = await queue.DequeueAsync(CancellationToken.None).AsTask().WaitAsync(Safety);
            Assert.True(first.HasArticle && second.HasArticle);
            var ids = new HashSet<ArticleId> { first.Article.ArtId, second.Article.ArtId };
            Assert.Contains(left.ArtId, ids);
            Assert.Contains(right.ArtId, ids);
        }

        [Fact]
        public async Task Unsupported_commands_return_500_without_mode_or_capabilities()
        {
            var queue = new AcceptedArticleQueue();
            await using var harness = await Harness.StartAsync(queue);
            Assert.StartsWith("500 ", await harness.CommandAsync("CAPABILITIES\r\n").WaitAsync(Safety), StringComparison.Ordinal);
            Assert.StartsWith("500 ", await harness.CommandAsync("MODE STREAM\r\n").WaitAsync(Safety), StringComparison.Ordinal);
            Assert.StartsWith("500 ", await harness.CommandAsync("CHECK <want@seg.test>\r\n").WaitAsync(Safety), StringComparison.Ordinal);
            Assert.StartsWith("205 ", await harness.CommandAsync("QUIT\r\n").WaitAsync(Safety), StringComparison.Ordinal);
        }

        private static int CountHop(string article, string hop)
        {
            var marker = hop + "!";
            var count = 0;
            var index = 0;
            while ((index = article.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += marker.Length;
            }

            return count;
        }

        private static BuiltWire BuildWireArticle(string messageId, string body, string ingressFqdn = "nntpd01.usenet.ninja")
        {
            var text =
                $"Path: {ingressFqdn}!not-for-mail\r\n" +
                $"From: demo@example.com\r\n" +
                $"Newsgroups: misc.test\r\n" +
                $"Subject: test\r\n" +
                $"Date: Fri, 23 Oct 2026 12:00:00 +0000\r\n" +
                $"Message-ID: {messageId}\r\n" +
                "\r\n" +
                body;
            var destuffed = Encoding.ASCII.GetBytes(text);
            var parser = new NntpArticleParser(CacheFqdn);
            var created = ArticleRecordFactory.TryCreate(parser, destuffed, ArticlePathMode.Normalize);
            Assert.True(created.IsAccepted);
            var record = created.Record;
            // Wire form for TAKETHIS: destuffed article with body lines dot-stuffed for transfer.
            var stuffed = StuffForWire(destuffed);
            return new BuiltWire(messageId, record.ArtId, record.ArtData, stuffed);
        }

        private static byte[] StuffForWire(ReadOnlySpan<byte> destuffed)
        {
            // Destuffed input already has CRLF lines; double leading dots in body for NNTP transfer.
            var headerEnd = destuffed.IndexOf("\r\n\r\n"u8);
            Assert.True(headerEnd >= 0);
            var bodyStart = headerEnd + 4;
            var output = new List<byte>(destuffed.Length + 16);
            output.AddRange(destuffed[..bodyStart].ToArray());
            var body = destuffed[bodyStart..];
            var offset = 0;
            while (offset < body.Length)
            {
                var lineEnd = body[offset..].IndexOf("\r\n"u8);
                ReadOnlySpan<byte> line;
                int advance;
                if (lineEnd < 0)
                {
                    line = body[offset..];
                    advance = line.Length;
                }
                else
                {
                    line = body.Slice(offset, lineEnd);
                    advance = lineEnd + 2;
                }

                if (line.Length > 0 && line[0] == (byte)'.')
                {
                    output.Add((byte)'.');
                }

                output.AddRange(line.ToArray());
                if (lineEnd >= 0)
                {
                    output.Add((byte)'\r');
                    output.Add((byte)'\n');
                }

                offset += advance;
            }

            output.Add((byte)'.');
            output.Add((byte)'\r');
            output.Add((byte)'\n');
            return output.ToArray();
        }

        private readonly record struct BuiltWire(
            string MessageId,
            ArticleId ArtId,
            ReadOnlyMemory<byte> Canonical,
            byte[] Stuffed);

        private sealed class BlockingProcessor : IAcceptedArticleProcessor
        {
            private readonly Task _release;
            public int Finished;

            public BlockingProcessor(Task release) => _release = release;

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
            private readonly Task _run;
            private readonly PipeTransport _transport;

            private Harness(PipeTransport transport, StorageNntpFeedSession session)
            {
                _transport = transport;
                _run = session.RunAsync(_cts.Token);
            }

            public static async Task<Harness> StartAsync(IAcceptedArticleQueue queue)
            {
                var transport = new PipeTransport();
                var session = new StorageNntpFeedSession(
                    transport,
                    new StorageServerListenerRuntimeOptions(
                        262144,
                        TimeSpan.FromSeconds(30),
                        TimeSpan.FromSeconds(30),
                        8,
                        TimeSpan.FromSeconds(300)),
                    NullLogger.Instance,
                    CacheFqdn,
                    acceptedArticles: queue,
                    assemblyAdmission: new StoreAssemblyAdmission(8));
                var harness = new Harness(transport, session);
                var greeting = await harness.ReadLineAsync().WaitAsync(Safety);
                Assert.StartsWith("200 ", greeting, StringComparison.Ordinal);
                return harness;
            }

            public Task WriteClientAsync(byte[] bytes) => _transport.ClientWriteAsync(bytes);

            public async Task DisposeTransportAsync()
            {
                await _transport.DisposeAsync();
            }

            public async Task<string> CommandAsync(string command)
            {
                await _transport.ClientWriteAsync(Encoding.ASCII.GetBytes(command));
                return await ReadLineAsync();
            }

            public async Task<string> TakeThisAsync(string messageId, byte[] stuffedArticleIncludingTerminator)
            {
                await _transport.ClientWriteAsync(Encoding.ASCII.GetBytes($"TAKETHIS {messageId}\r\n"));
                await _transport.ClientWriteAsync(stuffedArticleIncludingTerminator);
                return await ReadLineAsync();
            }

            public async Task<string> ReadLineAsync()
            {
                using var timeout = new CancellationTokenSource(Safety);
                var buffer = new List<byte>();
                while (true)
                {
                    var chunk = await _transport.ReadServerAsync(timeout.Token);
                    buffer.AddRange(chunk);
                    for (var i = 0; i < buffer.Count - 1; i++)
                    {
                        if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n')
                        {
                            var line = Encoding.ASCII.GetString(buffer.GetRange(0, i).ToArray());
                            buffer.RemoveRange(0, i + 2);
                            // Capabilities multiline: keep reading until terminator line if 101.
                            if (line.StartsWith("101 ", StringComparison.Ordinal))
                            {
                                while (true)
                                {
                                    while (buffer.Count < 2)
                                    {
                                        buffer.AddRange(await _transport.ReadServerAsync(timeout.Token));
                                    }

                                    var end = -1;
                                    for (var j = 0; j < buffer.Count - 1; j++)
                                    {
                                        if (buffer[j] == (byte)'\r' && buffer[j + 1] == (byte)'\n')
                                        {
                                            end = j;
                                            break;
                                        }
                                    }

                                    if (end < 0)
                                    {
                                        buffer.AddRange(await _transport.ReadServerAsync(timeout.Token));
                                        continue;
                                    }

                                    var next = Encoding.ASCII.GetString(buffer.GetRange(0, end).ToArray());
                                    buffer.RemoveRange(0, end + 2);
                                    if (next == ".")
                                    {
                                        return line;
                                    }
                                }
                            }

                            return line;
                        }
                    }
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
                catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                {
                    ExpectedException.Ignore(ex);
                }

                _cts.Dispose();
                await _transport.DisposeAsync();
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

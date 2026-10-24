using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Core;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.StorageService.Listener;
using VectorNNTPD.StorageService.Storage.ArticleIndex;
using VectorNNTPD.StorageService.Storage.SpoolPersistence;
using VectorNNTPD.StorageService.Storage.SpoolShards;
using VectorNNTPD.StorageService.Tests.Storage;

namespace VectorNNTPD.StorageService.Tests.Listener
{
    /// <summary>
    /// ARTICLE &lt;message-id&gt; retrieval from indexed CanonicalV1 spool files over the Storage NNTP session.
    /// </summary>
    public sealed class StorageNntpArticleRetrievalTests
    {
        private const string CacheFqdn = "cache01.usenet.ninja";
        private static readonly TimeSpan Safety = TimeSpan.FromSeconds(15);

        [Fact]
        public async Task Article_returns_220_with_headers_body_crlf_and_single_terminator()
        {
            await using var env = await IndexedEnv.CreateAsync();
            var stored = env.StoreCanonical("<article-ok@seg.test>", "body-line\r\n");
            await using var harness = await Harness.StartAsync(env.Index);

            var response = await harness.ArticleAsync(stored.MessageId).WaitAsync(Safety);
            Assert.StartsWith("220 0 " + stored.MessageId + " article follows\r\n", response.StatusLine, StringComparison.Ordinal);
            Assert.True(response.Article.AsSpan().EndsWith(".\r\n"u8));
            Assert.Equal(1, CountTerminator(response.Article));
            var block = response.Article.AsSpan(0, response.Article.Length - 3);
            Assert.True(block.SequenceEqual(stored.Canonical.Span));
        }

        [Theory]
        [InlineData("plain\r\n")]
        [InlineData(".one\r\n")]
        [InlineData("..two\r\n")]
        [InlineData("...three\r\n")]
        public async Task Article_preserves_canonical_body_dot_stuffing_without_restuffing(string logicalBodyLine)
        {
            await using var env = await IndexedEnv.CreateAsync();
            // Logical body line (as a reader would see after one destuff). Materialize stores CanonicalV1 stuffing once.
            var stored = env.StoreFromLogical("<dots@seg.test>", logicalBodyLine);
            await using var harness = await Harness.StartAsync(env.Index);

            var response = await harness.ArticleAsync(stored.MessageId).WaitAsync(Safety);
            Assert.StartsWith("220 ", response.StatusLine, StringComparison.Ordinal);
            var block = response.Article.AsSpan(0, response.Article.Length - 3);
            Assert.True(block.SequenceEqual(stored.Canonical.Span));
            // Body must still carry exactly the CanonicalV1 stuffing; a second pass would add another leading dot.
            var bodyStart = CanonicalArticleFraming.Analyze(block).BodyStart;
            Assert.True(bodyStart >= 0);
            Assert.True(block[bodyStart..].SequenceEqual(stored.Canonical.Span[bodyStart..]));
        }

        [Fact]
        public async Task Article_stuffs_dot_leading_header_on_the_wire_only()
        {
            await using var env = await IndexedEnv.CreateAsync();
            var logical =
                "Path: peer!not-for-mail\r\n" +
                "From: demo@example.com\r\n" +
                "Newsgroups: misc.test\r\n" +
                "Subject: dots\r\n" +
                "Date: Fri, 23 Oct 2026 12:00:00 +0000\r\n" +
                "Message-ID: <dot-header@seg.test>\r\n" +
                ".X-Dot: kept\r\n" +
                "\r\n" +
                ".only\r\n";
            var stored = env.StoreFromLogicalBytes("<dot-header@seg.test>", Encoding.ASCII.GetBytes(logical));
            Assert.Contains(".X-Dot: kept\r\n"u8, stored.Canonical.Span);
            Assert.DoesNotContain("..X-Dot:"u8, stored.Canonical.Span);
            Assert.Contains("..only\r\n"u8, stored.Canonical.Span);

            await using var harness = await Harness.StartAsync(env.Index);
            var response = await harness.ArticleAsync(stored.MessageId).WaitAsync(Safety);
            Assert.StartsWith("220 ", response.StatusLine, StringComparison.Ordinal);
            var block = response.Article.AsSpan(0, response.Article.Length - 3);
            Assert.Contains("..X-Dot: kept\r\n"u8, block);
            Assert.Contains("..only\r\n"u8, block);
            Assert.DoesNotContain("...only"u8, block);
        }

        [Fact]
        public async Task Article_missing_message_id_returns_430()
        {
            await using var env = await IndexedEnv.CreateAsync();
            await using var harness = await Harness.StartAsync(env.Index);
            var status = await harness.CommandAsync("ARTICLE <missing@seg.test>\r\n").WaitAsync(Safety);
            Assert.StartsWith("430 ", status, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Article_invalid_syntax_and_message_id_return_501()
        {
            await using var env = await IndexedEnv.CreateAsync();
            await using var harness = await Harness.StartAsync(env.Index);
            Assert.StartsWith("501 ", await harness.CommandAsync("ARTICLE\r\n").WaitAsync(Safety), StringComparison.Ordinal);
            Assert.StartsWith("501 ", await harness.CommandAsync("ARTICLE not-an-id\r\n").WaitAsync(Safety), StringComparison.Ordinal);
            Assert.StartsWith("501 ", await harness.CommandAsync("ARTICLE <a@b> extra\r\n").WaitAsync(Safety), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Article_index_hit_with_missing_file_returns_430_not_220()
        {
            await using var env = await IndexedEnv.CreateAsync();
            var stored = env.StoreCanonical("<gone@seg.test>", "body\r\n");
            File.Delete(stored.Path);
            await using var harness = await Harness.StartAsync(env.Index);
            var status = await harness.CommandAsync($"ARTICLE {stored.MessageId}\r\n").WaitAsync(Safety);
            Assert.StartsWith("430 ", status, StringComparison.Ordinal);
            Assert.DoesNotContain("220 ", status, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Article_unframeable_payload_returns_403_before_any_220()
        {
            await using var env = await IndexedEnv.CreateAsync();
            var messageId = "<bad-frame@seg.test>";
            var articleId = ArticleId.FromMessageId(Encoding.ASCII.GetBytes(messageId));
            // Bare LF makes CanonicalArticleFraming reject before any multiline write.
            var path = ArticleIndexTestSupport.PlacePayload(env.Spool, articleId, "Subject: s\n\nbody\n");
            await env.Index.InsertStoredAsync(articleId, env.Spool, 1, CancellationToken.None);

            await using var harness = await Harness.StartAsync(env.Index);
            var raw = await harness.ArticleRawAsync(messageId).WaitAsync(Safety);
            Assert.DoesNotContain("220 "u8, raw);
            Assert.Contains("403 Article not available\r\n"u8, raw);
            Assert.True(File.Exists(path));
        }

        [Fact]
        public async Task Article_works_immediately_without_mode_negotiation()
        {
            await using var env = await IndexedEnv.CreateAsync();
            var stored = env.StoreCanonical("<no-mode@seg.test>", "body\r\n");
            await using var harness = await Harness.StartAsync(env.Index);

            Assert.StartsWith(
                "500 ",
                await harness.CommandAsync("MODE STREAM\r\n").WaitAsync(Safety),
                StringComparison.Ordinal);
            Assert.StartsWith(
                "500 ",
                await harness.CommandAsync("CAPABILITIES\r\n").WaitAsync(Safety),
                StringComparison.Ordinal);

            var response = await harness.ArticleAsync(stored.MessageId).WaitAsync(Safety);
            Assert.StartsWith("220 ", response.StatusLine, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Quit_closes_cleanly_on_minimal_command_surface()
        {
            await using var env = await IndexedEnv.CreateAsync();
            await using var harness = await Harness.StartAsync(env.Index);
            Assert.StartsWith("205 ", await harness.CommandAsync("QUIT\r\n").WaitAsync(Safety), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Successful_article_increments_access_count_missing_does_not()
        {
            await using var env = await IndexedEnv.CreateAsync();
            var stored = env.StoreCanonical("<access-count@seg.test>", "body\r\n");
            Assert.True(env.Index.TryGetIndexedRecord(stored.ArtId, out var before));
            Assert.Equal(1, before.AccessCount);

            await using var harness = await Harness.StartAsync(env.Index);
            var ok = await harness.ArticleAsync(stored.MessageId).WaitAsync(Safety);
            Assert.StartsWith("220 ", ok.StatusLine, StringComparison.Ordinal);
            // Touch runs after the terminator write; wait for the index commit rather than racing the client read.
            await WaitForAccessCountAsync(env.Index, stored.ArtId, expected: 2).WaitAsync(Safety);

            var missing = await harness.CommandAsync("ARTICLE <never-stored@seg.test>\r\n").WaitAsync(Safety);
            Assert.StartsWith("430 ", missing, StringComparison.Ordinal);
            Assert.True(env.Index.TryGetIndexedRecord(stored.ArtId, out var unchanged));
            Assert.Equal(2, unchanged.AccessCount);
        }

        private static async Task WaitForAccessCountAsync(IArticleIndex index, ArticleId articleId, long expected)
        {
            while (true)
            {
                if (index.TryGetIndexedRecord(articleId, out var record) && record.AccessCount == expected)
                {
                    return;
                }

                await Task.Delay(5).ConfigureAwait(false);
            }
        }

        [Fact]
        public async Task TakeThis_239_behaviour_is_unchanged_alongside_article()
        {
            var queue = new AcceptedArticleQueue();
            await using var env = await IndexedEnv.CreateAsync();
            await using var harness = await Harness.StartAsync(env.Index, queue);
            var wire = BuildWireArticle("<take-still@seg.test>", "body\r\n");
            var status = await harness.TakeThisAsync(wire.MessageId, wire.Stuffed).WaitAsync(Safety);
            Assert.StartsWith("239 ", status, StringComparison.Ordinal);
            Assert.True(queue.TryDequeue(out var delivery));
            Assert.True(delivery.HasArticle);
        }

        [Fact]
        public async Task Article_cancel_during_retrieval_releases_resources()
        {
            await using var env = await IndexedEnv.CreateAsync();
            var stored = env.StoreCanonical("<cancel@seg.test>", "body\r\n");
            await using var harness = await Harness.StartAsync(env.Index);
            await harness.WriteClientAsync(Encoding.ASCII.GetBytes($"ARTICLE {stored.MessageId}\r\n"));
            await harness.DisposeTransportAsync();
            await Task.Delay(50);
            // Final file remains readable; session tear-down must not delete or lock it permanently.
            Assert.True(File.Exists(stored.Path));
            await using FileStream reopen = new(
                stored.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Assert.True(reopen.Length > 0);
        }

        [Fact]
        public async Task Concurrent_article_reads_and_atomic_publish_do_not_serve_temp_files()
        {
            await using var env = await IndexedEnv.CreateAsync();
            var first = env.StoreCanonical("<concurrent-a@seg.test>", "a\r\n");
            await using var a = await Harness.StartAsync(env.Index);
            await using var b = await Harness.StartAsync(env.Index);

            var readA = a.ArticleAsync(first.MessageId);
            var publisher = new SpoolArticlePublisher(new FileSpoolArticleFiles());
            var secondId = ArticleId.FromMessageId("<concurrent-b@seg.test>"u8);
            var secondCanonical = BuildCanonical("<concurrent-b@seg.test>", "b\r\n");
            var publish = Task.Run(() =>
            {
                var result = publisher.Publish(env.Spool, secondId, secondCanonical.Span, CancellationToken.None);
                Assert.Equal(SpoolPublishKind.Stored, result.Kind);
            });
            await publish.WaitAsync(Safety);
            await env.Index.InsertStoredAsync(secondId, env.Spool, 2, CancellationToken.None);

            var responseA = await readA.WaitAsync(Safety);
            Assert.StartsWith("220 ", responseA.StatusLine, StringComparison.Ordinal);
            Assert.True(responseA.Article.AsSpan(0, responseA.Article.Length - 3).SequenceEqual(first.Canonical.Span));

            var responseB = await b.ArticleAsync("<concurrent-b@seg.test>").WaitAsync(Safety);
            Assert.StartsWith("220 ", responseB.StatusLine, StringComparison.Ordinal);
            Assert.True(responseB.Article.AsSpan(0, responseB.Article.Length - 3).SequenceEqual(secondCanonical.Span));

            foreach (var file in Directory.EnumerateFiles(env.Spool, "*", SearchOption.AllDirectories))
            {
                Assert.DoesNotContain(".writing-", file, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static int CountTerminator(byte[] article)
        {
            var count = 0;
            for (var i = 0; i < article.Length - 2; i++)
            {
                if (article[i] == (byte)'.' && article[i + 1] == (byte)'\r' && article[i + 2] == (byte)'\n')
                {
                    if (i == 0 || article[i - 1] == (byte)'\n')
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static ReadOnlyMemory<byte> BuildCanonical(string messageId, string body)
        {
            var text =
                $"Path: nntpd01.usenet.ninja!not-for-mail\r\n" +
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
            Assert.True(created.IsAccepted, created.ParseFailure.ToString());
            return created.Record.ArtData;
        }

        private static BuiltWire BuildWireArticle(string messageId, string body)
        {
            var text =
                $"Path: nntpd01.usenet.ninja!not-for-mail\r\n" +
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
            var stuffed = StuffForWire(destuffed);
            return new BuiltWire(messageId, created.Record.ArtId, created.Record.ArtData, stuffed);
        }

        private static byte[] StuffForWire(ReadOnlySpan<byte> destuffed)
        {
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

        private readonly record struct StoredArticle(
            string MessageId,
            ArticleId ArtId,
            ReadOnlyMemory<byte> Canonical,
            string Path);

        private readonly record struct ArticleResponse(string StatusLine, byte[] Article);

        private sealed class IndexedEnv : IAsyncDisposable
        {
            private IndexedEnv(string root, string spool, ArticleIndexService index)
            {
                Root = root;
                Spool = spool;
                Index = index;
            }

            public string Root { get; }
            public string Spool { get; }
            public ArticleIndexService Index { get; }

            public static async Task<IndexedEnv> CreateAsync()
            {
                var root = ArticleIndexTestSupport.NewDirectory();
                var spool = Path.Join(root, "spool");
                Directory.CreateDirectory(spool);
                var index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
                return new IndexedEnv(root, spool, index);
            }

            public StoredArticle StoreCanonical(string messageId, string body)
            {
                var canonical = BuildCanonical(messageId, body);
                return StoreBytes(messageId, canonical);
            }

            public StoredArticle StoreFromLogical(string messageId, string logicalBodyLine) =>
                StoreFromLogicalBytes(
                    messageId,
                    Encoding.ASCII.GetBytes(
                        $"Path: nntpd01.usenet.ninja!not-for-mail\r\n" +
                        $"From: demo@example.com\r\n" +
                        $"Newsgroups: misc.test\r\n" +
                        $"Subject: test\r\n" +
                        $"Date: Fri, 23 Oct 2026 12:00:00 +0000\r\n" +
                        $"Message-ID: {messageId}\r\n" +
                        "\r\n" +
                        logicalBodyLine));

            public StoredArticle StoreFromLogicalBytes(string messageId, byte[] logical)
            {
                var parser = new NntpArticleParser(CacheFqdn);
                var created = ArticleRecordFactory.TryCreate(parser, logical, ArticlePathMode.Normalize);
                Assert.True(created.IsAccepted, created.ParseFailure.ToString());
                return StoreBytes(messageId, created.Record.ArtData);
            }

            private StoredArticle StoreBytes(string messageId, ReadOnlyMemory<byte> canonical)
            {
                var articleId = ArticleId.FromMessageId(Encoding.ASCII.GetBytes(messageId));
                var path = SpoolArticlePath.CombineArticle(Spool, articleId);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, canonical.ToArray());
                Index.InsertStoredAsync(articleId, Spool, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                Assert.True(Index.TryGetRetrievableArticle(articleId, out _));
                return new StoredArticle(messageId, articleId, canonical, path);
            }

            public async ValueTask DisposeAsync()
            {
                await ArticleIndexTestSupport.StopAsync(Index);
                ArticleIndexTestSupport.Delete(Root);
            }
        }

        private sealed class Harness : IAsyncDisposable
        {
            private readonly CancellationTokenSource _cts = new();
            private readonly Task _run;
            private readonly PipeTransport _transport;
            private readonly List<byte> _unread = [];

            private Harness(PipeTransport transport, StorageNntpFeedSession session)
            {
                _transport = transport;
                _run = session.RunAsync(_cts.Token);
            }

            public static async Task<Harness> StartAsync(
                IArticleIndex index,
                IAcceptedArticleQueue? queue = null)
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
                    acceptedArticles: queue ?? new AcceptedArticleQueue(),
                    assemblyAdmission: new StoreAssemblyAdmission(8),
                    articlePresent: id =>
                    {
                        try
                        {
                            return index.IsReady && index.Contains(id);
                        }
                        catch (InvalidOperationException)
                        {
                            return false;
                        }
                    },
                    resolveRetrievableArticle: id =>
                    {
                        try
                        {
                            return index.IsReady && index.TryGetRetrievableArticle(id, out var path)
                                ? path
                                : null;
                        }
                        catch (InvalidOperationException)
                        {
                            return null;
                        }
                    },
                    afterArticleDelivered: async (id, cancellationToken) =>
                    {
                        if (!index.TryGetIndexedRecord(id, out var record))
                        {
                            return;
                        }

                        await index.TouchAsync(
                                id,
                                record.SpoolDirectory,
                                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                cancellationToken)
                            .ConfigureAwait(false);
                    });
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

            public async Task<ArticleResponse> ArticleAsync(string messageId)
            {
                await _transport.ClientWriteAsync(Encoding.ASCII.GetBytes($"ARTICLE {messageId}\r\n"));
                var status = await ReadLineAsync();
                if (!status.StartsWith("220 ", StringComparison.Ordinal))
                {
                    return new ArticleResponse(status + "\r\n", []);
                }

                var article = await ReadMultilineAsync();
                return new ArticleResponse(status + "\r\n", article);
            }

            public async Task<byte[]> ArticleRawAsync(string messageId)
            {
                await _transport.ClientWriteAsync(Encoding.ASCII.GetBytes($"ARTICLE {messageId}\r\n"));
                using var timeout = new CancellationTokenSource(Safety);
                var buffer = new List<byte>();
                while (buffer.Count < 8)
                {
                    buffer.AddRange(await _transport.ReadServerAsync(timeout.Token));
                }

                // Drain a short response; unframeable path writes only a status line.
                while (true)
                {
                    var span = CollectionsMarshal.AsSpan(buffer);
                    var crlf = IndexOfCrlf(span);
                    if (crlf >= 0)
                    {
                        return buffer.GetRange(0, crlf + 2).ToArray();
                    }

                    buffer.AddRange(await _transport.ReadServerAsync(timeout.Token));
                }
            }

            public async Task<string> ReadLineAsync()
            {
                using var timeout = new CancellationTokenSource(Safety);
                while (true)
                {
                    var crlf = IndexOfCrlf(CollectionsMarshal.AsSpan(_unread));
                    if (crlf >= 0)
                    {
                        var line = Encoding.ASCII.GetString(_unread.GetRange(0, crlf).ToArray());
                        _unread.RemoveRange(0, crlf + 2);
                        return line;
                    }

                    _unread.AddRange(await _transport.ReadServerAsync(timeout.Token));
                }
            }

            private async Task<byte[]> ReadMultilineAsync()
            {
                using var timeout = new CancellationTokenSource(Safety);
                var output = new List<byte>();
                while (true)
                {
                    while (_unread.Count < 2)
                    {
                        _unread.AddRange(await _transport.ReadServerAsync(timeout.Token));
                    }

                    var crlf = IndexOfCrlf(CollectionsMarshal.AsSpan(_unread));
                    if (crlf < 0)
                    {
                        _unread.AddRange(await _transport.ReadServerAsync(timeout.Token));
                        continue;
                    }

                    var lineLength = crlf + 2;
                    output.AddRange(_unread.GetRange(0, lineLength));
                    var isTerminator = crlf == 1 && _unread[0] == (byte)'.';
                    _unread.RemoveRange(0, lineLength);
                    if (isTerminator)
                    {
                        return output.ToArray();
                    }
                }
            }

            private static int IndexOfCrlf(ReadOnlySpan<byte> span)
            {
                for (var i = 0; i < span.Length - 1; i++)
                {
                    if (span[i] == (byte)'\r' && span[i + 1] == (byte)'\n')
                    {
                        return i;
                    }
                }

                return -1;
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

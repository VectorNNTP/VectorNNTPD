using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.StorageService.Listener;

namespace VectorNNTPD.StorageService.Tests.Listener
{
    /// <summary>
    /// Pre-admission TAKETHIS resource safety: size limits, assembly permits, and receive deadlines.
    /// </summary>
    public sealed class StorageNntpIngressResourceTests
    {
        private const string CacheFqdn = "cache01.usenet.ninja";
        private static readonly TimeSpan Safety = TimeSpan.FromSeconds(10);

        [Fact]
        public void Assembly_limit_tracks_queue_budget_and_connection_ceiling()
        {
            var maxArticle = ArticleResourceLimits.MaxArticleBytes;
            Assert.Equal(1, StoreAssemblyAdmission.LimitFor(1, 1L << 30));
            Assert.Equal(2, StoreAssemblyAdmission.LimitFor(1024, 2L * maxArticle));
            Assert.Equal(204, StoreAssemblyAdmission.LimitFor(1024, 1L << 30));
            Assert.Equal(1024, StoreAssemblyAdmission.LimitFor(1024, 100L * maxArticle * 1024));
        }

        [Fact]
        public async Task Article_just_under_max_size_is_accepted_with_239()
        {
            var built = BuildSized("<under-max@seg.test>", ArticleResourceLimits.MaxArticleBytes - 256);
            Assert.True(built.Logical.Length < ArticleResourceLimits.MaxArticleBytes);
            var queue = new AcceptedArticleQueue(1L << 30, TimeProvider.System);
            var gate = new StoreAssemblyAdmission(4);
            await using var harness = await Harness.StartAsync(queue, gate);
            Assert.Equal(239, await harness.TakeThisAsync(built));
            Assert.Equal(0, gate.InFlight);
            Assert.Equal(1, queue.QueuedCount);
            Assert.Equal(0, queue.InFlightBytes);
        }

        [Fact]
        public async Task Article_exceeding_max_size_is_rejected_without_239()
        {
            var queue = new AcceptedArticleQueue(1L << 30, TimeProvider.System);
            var gate = new StoreAssemblyAdmission(4);
            await using var harness = await Harness.StartAsync(queue, gate);
            var status = await harness.TakeThisOversizedAsync("<over-max@seg.test>", ArticleResourceLimits.MaxArticleBytes + 1);
            Assert.Equal(400, status);
            Assert.Equal(0, queue.EnqueuedCount);
            Assert.Equal(0, queue.AccountedBytes);
            Assert.Equal(0, gate.InFlight);
        }

        [Fact]
        public async Task Dot_stuffed_body_cannot_bypass_destuffed_size_limit()
        {
            // Wire lines start with '.' so destuffing removes one octet per line; still over the ceiling.
            var queue = new AcceptedArticleQueue(1L << 30, TimeProvider.System);
            var gate = new StoreAssemblyAdmission(2);
            await using var harness = await Harness.StartAsync(queue, gate);
            var status = await harness.TakeThisDotStuffedOversizedAsync(
                "<stuff-bypass@seg.test>",
                ArticleResourceLimits.MaxArticleBytes + 64);
            Assert.Equal(400, status);
            Assert.Equal(0, queue.EnqueuedCount);
            Assert.Equal(0, gate.InFlight);
        }

        [Fact]
        public async Task Assembly_permit_is_released_when_admission_is_blocked()
        {
            var built = Build("<asm-release@seg.test>", "body\r\n");
            var queue = new AcceptedArticleQueue(built.Record.ArtSize, TimeProvider.System);
            Assert.True(queue.TryEnqueue(built.AsAccepted()));
            var gate = new StoreAssemblyAdmission(1);
            await using var first = await Harness.StartAsync(queue, gate);
            await using var second = await Harness.StartAsync(queue, gate);

            var waiting = second.TakeThisAsync(Build("<asm-wait@seg.test>", "other\r\n"));
            await Task.Delay(40);
            Assert.False(waiting.IsCompleted);
            Assert.Equal(1, gate.InFlight);

            Assert.True(queue.TryDequeue(out var held));
            queue.ReleaseProcessed(held.Article.ArtSize);
            Assert.Equal(239, await waiting.WaitAsync(Safety));
            Assert.Equal(0, gate.InFlight);
        }

        [Fact]
        public async Task Concurrent_assemblies_cannot_exceed_admission_cap()
        {
            var gate = new StoreAssemblyAdmission(1);
            var queue = new AcceptedArticleQueue(1L << 20, TimeProvider.System);
            await using var first = await Harness.StartAsync(queue, gate);
            await using var second = await Harness.StartAsync(queue, gate);

            Assert.True(gate.TryEnter());
            try
            {
                Assert.Equal(439, await second.TakeThisAsync(Build("<cap-full@seg.test>", "x\r\n")));
                Assert.Equal(0, queue.EnqueuedCount);
            }
            finally
            {
                gate.Exit();
            }

            Assert.Equal(0, gate.InFlight);
        }

        [Fact]
        public async Task Incomplete_body_hits_overall_receive_timeout_and_releases_assembly()
        {
            var queue = new AcceptedArticleQueue(1L << 20, TimeProvider.System);
            var gate = new StoreAssemblyAdmission(2);
            await using var harness = await Harness.StartAsync(
                queue,
                gate,
                articleReceiveTimeout: TimeSpan.FromMilliseconds(80));
            await harness.SendRawAsync(Encoding.ASCII.GetBytes("TAKETHIS <slow-body@seg.test>\r\n"));
            await harness.SendRawAsync(Encoding.ASCII.GetBytes("Message-ID: <slow-body@seg.test>\r\n"));
            var status = await harness.ReadStatusAsync();
            Assert.Equal(400, status);
            Assert.Equal(0, queue.EnqueuedCount);
            Assert.Equal(0, gate.InFlight);
        }

        [Fact]
        public async Task Client_disconnect_during_receive_releases_assembly_without_239()
        {
            var queue = new AcceptedArticleQueue(1L << 20, TimeProvider.System);
            var gate = new StoreAssemblyAdmission(2);
            await using var harness = await Harness.StartAsync(queue, gate);
            await harness.SendRawAsync(Encoding.ASCII.GetBytes("TAKETHIS <eof@seg.test>\r\n"));
            await harness.SendRawAsync(Encoding.ASCII.GetBytes("Message-ID: <eof@seg.test>\r\n\r\npartial"));
            await harness.Transport.CompleteClientAsync();
            await harness.WaitSessionEndedAsync();
            Assert.Equal(0, queue.EnqueuedCount);
            Assert.Equal(0, gate.InFlight);
        }

        [Fact]
        public async Task Incomplete_headers_hit_overall_receive_timeout()
        {
            var queue = new AcceptedArticleQueue(1L << 20, TimeProvider.System);
            var gate = new StoreAssemblyAdmission(2);
            await using var harness = await Harness.StartAsync(
                queue,
                gate,
                articleReceiveTimeout: TimeSpan.FromMilliseconds(80));
            await harness.SendRawAsync(Encoding.ASCII.GetBytes("TAKETHIS <slow-hdr@seg.test>\r\n"));
            await harness.SendRawAsync(Encoding.ASCII.GetBytes("Message-ID: <slow-hdr@seg.test>\r\n"));
            // No blank line / body / terminator — overall deadline must fire without progress resets helping.
            var status = await harness.ReadStatusAsync();
            Assert.Equal(400, status);
            Assert.Equal(0, queue.EnqueuedCount);
            Assert.Equal(0, gate.InFlight);
        }

        [Fact]
        public async Task Cancellation_while_waiting_for_queue_reservation_releases_pre_admission_resources()
        {
            var incoming = Build("<cancel-reserve@seg.test>", "wait\r\n");
            var queue = new AcceptedArticleQueue(incoming.Record.ArtSize, TimeProvider.System);
            Assert.True(queue.TryReserve(incoming.Record.ArtSize, out var blocker));
            Assert.Equal(incoming.Record.ArtSize, queue.AccountedBytes);

            var gate = new StoreAssemblyAdmission(2);
            await using var harness = await Harness.StartAsync(queue, gate);
            var pending = harness.TakeThisAsync(incoming);
            await WaitUntilAsync(() => gate.InFlight == 1 && queue.AdmissionWaiters == 1);

            await harness.CancelSessionAsync();
            await Assert.ThrowsAnyAsync<Exception>(async () => await pending.WaitAsync(Safety));
            await harness.WaitSessionEndedAsync();

            Assert.Equal(0, gate.InFlight);
            Assert.Equal(0, queue.AdmissionWaiters);
            Assert.Equal(0, queue.QueuedCount);
            Assert.Equal(incoming.Record.ArtSize, queue.ReservedBytes);
            queue.Release(blocker);
            Assert.Equal(0, queue.AccountedBytes);
        }

        [Fact]
        public async Task Successful_239_leaves_article_owned_by_queue_after_client_disconnect()
        {
            var built = Build("<post-239@seg.test>", "keep\r\n");
            var queue = new AcceptedArticleQueue(1L << 20, TimeProvider.System);
            var gate = new StoreAssemblyAdmission(2);
            await using var harness = await Harness.StartAsync(queue, gate);
            Assert.Equal(239, await harness.TakeThisAsync(built));
            Assert.Equal(1, queue.QueuedCount);
            Assert.Equal(built.Record.ArtSize, queue.AccountedBytes);
            Assert.Equal(0, gate.InFlight);

            await harness.Transport.CompleteClientAsync();
            try
            {
                await harness.WaitSessionEndedAsync();
            }
            catch (Exception ex) when (ex is ChannelClosedException or EndOfStreamException)
            {
            }

            Assert.Equal(1, queue.QueuedCount);
            Assert.Equal(built.Record.ArtSize, queue.AccountedBytes);
            Assert.True(queue.TryDequeue(out var delivery));
            Assert.Equal(built.Record.ArtId, delivery.Article.ArtId);
            queue.ReleaseProcessed(delivery.Article.ArtSize);
            Assert.Equal(0, queue.AccountedBytes);
        }

        [Fact]
        public async Task Assembly_cap_reject_discards_body_without_239_or_queue_admission()
        {
            var gate = new StoreAssemblyAdmission(1);
            Assert.True(gate.TryEnter());
            try
            {
                var queue = new AcceptedArticleQueue(1L << 20, TimeProvider.System);
                await using var harness = await Harness.StartAsync(queue, gate);
                // Large-ish body discarded without an assembly permit (scratch-only drain).
                var status = await harness.TakeThisAsync(BuildSized("<discard-cap@seg.test>", 256 * 1024));
                Assert.Equal(439, status);
                Assert.Equal(0, queue.EnqueuedCount);
                Assert.Equal(0, queue.AccountedBytes);
                Assert.Equal(1, gate.InFlight);
            }
            finally
            {
                gate.Exit();
            }
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Safety;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("condition was not met");
                }

                await Task.Delay(10);
            }
        }

        private static Built Build(string messageId, string body)
        {
            var parser = new NntpArticleParser(CacheFqdn);
            var text = "Path: peer.example\r\nDate: Fri, 23 Aug 2024 07:30:10 +0000\r\nMessage-ID: "
                + messageId
                + "\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\nSubject: ingress\r\n\r\n"
                + body;
            var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(text));
            Assert.True(created.IsAccepted);
            return new Built(created.Record, created.Record.ArtData);
        }

        private static Built BuildSized(string messageId, int targetLogicalBytes)
        {
            var parser = new NntpArticleParser(CacheFqdn);
            var header = "Path: peer.example\r\nDate: Fri, 23 Aug 2024 07:30:10 +0000\r\nMessage-ID: "
                + messageId
                + "\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\nSubject: sized\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            Assert.True(targetLogicalBytes > headerBytes.Length + 4);
            var body = new StringBuilder(targetLogicalBytes - headerBytes.Length);
            // Keep body lines under MaxArticleLineBytes so the size fixture is not rejected for line length.
            const int lineContent = 64;
            while (headerBytes.Length + body.Length + 2 < targetLogicalBytes)
            {
                var remaining = targetLogicalBytes - headerBytes.Length - body.Length - 2;
                var n = Math.Min(lineContent, remaining);
                body.Append('x', n);
                body.Append("\r\n");
            }

            var bytes = Encoding.ASCII.GetBytes(header + body);
            var created = ArticleRecordFactory.TryCreate(parser, bytes, ArticlePathMode.Normalize);
            Assert.True(created.IsAccepted, "sized fixture must parse");
            Assert.True(created.Record.ArtSize <= ArticleResourceLimits.MaxArticleBytes);
            return new Built(created.Record, created.Record.ArtData);
        }

        private readonly record struct Built(ArticleRecord Record, ReadOnlyMemory<byte> Logical)
        {
            public AcceptedArticle AsAccepted() =>
                new(Record.ArtId, Record.ArtHash, Record.ArtSize, Record.ArtData);
        }

        private sealed class Harness : IAsyncDisposable
        {
            private readonly CancellationTokenSource _cts = new();
            private readonly Task _run;
            private readonly List<byte> _pendingResponse = [];

            private Harness(PipeTransport transport, StorageNntpFeedSession session)
            {
                Transport = transport;
                _run = session.RunAsync(_cts.Token);
            }

            public PipeTransport Transport { get; }

            public Task CancelSessionAsync() => _cts.CancelAsync();

            public static async Task<Harness> StartAsync(
                IAcceptedArticleQueue queue,
                StoreAssemblyAdmission gate,
                TimeSpan? articleReceiveTimeout = null)
            {
                var transport = new PipeTransport();
                var session = new StorageNntpFeedSession(
                    transport,
                    new StorageServerListenerRuntimeOptions(
                        262144,
                        TimeSpan.FromSeconds(30),
                        TimeSpan.FromSeconds(30),
                        8,
                        articleReceiveTimeout ?? TimeSpan.FromSeconds(300)),
                    NullLogger.Instance,
                    CacheFqdn,
                    acceptedArticles: queue,
                    assemblyAdmission: gate);
                var harness = new Harness(transport, session);
                Assert.Equal(200, await harness.ReadStatusAsync());
                return harness;
            }

            public async Task<int> TakeThisAsync(Built built)
            {
                var messageId = Encoding.ASCII.GetString(built.Record.MessageId.ToArray());
                return await TakeThisRawAsync(messageId, built.Logical);
            }

            public async Task<int> TakeThisOversizedAsync(string messageId, int destuffedBytes)
            {
                await SendRawAsync(Encoding.ASCII.GetBytes("TAKETHIS " + messageId + "\r\n"));
                // Minimal headers then a body large enough that destuffed assembly exceeds the ceiling
                // before a terminator arrives.
                var prefix = Encoding.ASCII.GetBytes(
                    "Path: peer.example\r\nDate: Fri, 23 Aug 2024 07:30:10 +0000\r\nMessage-ID: "
                    + messageId
                    + "\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\nSubject: over\r\n\r\n");
                await SendRawAsync(prefix);
                var remaining = destuffedBytes - prefix.Length + 8;
                var chunk = new byte[64 * 1024];
                chunk.AsSpan().Fill((byte)'y');
                while (remaining > 0)
                {
                    var n = Math.Min(remaining, chunk.Length);
                    await SendRawAsync(chunk.AsSpan(0, n).ToArray());
                    remaining -= n;
                }

                await SendRawAsync(".\r\n"u8.ToArray());
                return await ReadStatusAsync();
            }

            public async Task<int> TakeThisDotStuffedOversizedAsync(string messageId, int destuffedBytes)
            {
                await SendRawAsync(Encoding.ASCII.GetBytes("TAKETHIS " + messageId + "\r\n"));
                var prefix = Encoding.ASCII.GetBytes(
                    "Path: peer.example\r\nDate: Fri, 23 Aug 2024 07:30:10 +0000\r\nMessage-ID: "
                    + messageId
                    + "\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\nSubject: stuff\r\n\r\n");
                await SendRawAsync(prefix);
                var remaining = destuffedBytes - prefix.Length + 16;
                // Each wire line "..xxxx\r\n" destuffs to ".xxxx\r\n".
                var line = Encoding.ASCII.GetBytes(".." + new string('z', 60) + "\r\n");
                while (remaining > 0)
                {
                    await SendRawAsync(line);
                    remaining -= line.Length - 1;
                }

                await SendRawAsync(".\r\n"u8.ToArray());
                return await ReadStatusAsync();
            }

            public async Task<int> TakeThisRawAsync(string messageId, ReadOnlyMemory<byte> logicalArticle)
            {
                await SendRawAsync(Encoding.ASCII.GetBytes("TAKETHIS " + messageId + "\r\n"));
                await SendRawAsync(DotStuff(logicalArticle.Span));
                await SendRawAsync(".\r\n"u8.ToArray());
                return await ReadStatusAsync();
            }

            public Task SendRawAsync(byte[] frame) => Transport.ClientWriteAsync(frame);

            public async Task WaitSessionEndedAsync()
            {
                using var timeout = new CancellationTokenSource(Safety);
                try
                {
                    await _run.WaitAsync(timeout.Token);
                }
                catch (Exception ex) when (ex is ChannelClosedException or EndOfStreamException or OperationCanceledException)
                {
                }
            }

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
                catch (Exception ex) when (ex is OperationCanceledException
                    or EndOfStreamException
                    or ChannelClosedException)
                {
                }

                await Transport.DisposeAsync();
            }

            private static byte[] DotStuff(ReadOnlySpan<byte> logical)
            {
                var output = new List<byte>(logical.Length + 16);
                var atLineStart = true;
                for (var i = 0; i < logical.Length; i++)
                {
                    var b = logical[i];
                    if (atLineStart && b == (byte)'.')
                    {
                        output.Add((byte)'.');
                    }

                    output.Add(b);
                    atLineStart = b is (byte)'\n';
                }

                return output.ToArray();
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

            public Task CompleteClientAsync()
            {
                _toServer.Writer.TryComplete();
                return Task.CompletedTask;
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

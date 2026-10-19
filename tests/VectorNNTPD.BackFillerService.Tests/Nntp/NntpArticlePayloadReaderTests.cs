using VectorNNTPD.BackFillerService.Nntp;

namespace VectorNNTPD.BackFillerService.Tests.Nntp
{
    /// <summary>
    /// NNTP multiline article payload reader: dot-stuffing, terminators, size limits, cancellation, and payload callback lifetime.
    /// </summary>

    public sealed class NntpArticlePayloadReaderTests
    {
        /// <summary>
        /// Leading dot-stuffing on a body line decodes to one stored dot per RFC 3977 multiline rules.
        /// </summary>

        [Fact]
        public async Task DoubledDotAtLineStart_StoresOneDot()
        {
            var payload = await ReadAsync("..foo\r\n.\r\n"u8.ToArray());
            Assert.Equal(".foo\r\n"u8.ToArray(), payload);
        }

        /// <summary>
        /// A line whose first byte is the stuffing dot loses that framing dot in the returned payload.
        /// </summary>

        [Fact]
        public async Task SingleDotAtLineStart_DropsTheFramingDot()
        {
            var payload = await ReadAsync(".foo\r\n.\r\n"u8.ToArray());
            Assert.Equal("foo\r\n"u8.ToArray(), payload);
        }

        /// <summary>
        /// Two leading dots on a line decode to a single dot after destuffing.
        /// </summary>

        [Fact]
        public async Task DoubleDot_BecomesOneDot()
        {
            var payload = await ReadAsync("..foo\r\n.\r\n"u8.ToArray());
            Assert.Equal(".foo\r\n"u8.ToArray(), payload);
        }

        /// <summary>
        /// Three leading dots decode to two dots (one stuffing prefix removed).
        /// </summary>

        [Fact]
        public async Task TripleDot_BecomesTwoDots()
        {
            var payload = await ReadAsync("...bar\r\n.\r\n"u8.ToArray());
            Assert.Equal("..bar\r\n"u8.ToArray(), payload);
        }

        /// <summary>
        /// A body line that is only `..` decodes to `.\r\n`; the multiline terminator is not included in payload bytes.
        /// </summary>

        [Fact]
        public async Task StuffedSingleDotDataLine_BecomesOneDot_AndOmitsTerminator()
        {
            var payload = await ReadAsync("..\r\n.\r\n"u8.ToArray());
            Assert.Equal(".\r\n"u8.ToArray(), payload);
        }

        /// <summary>
        /// Dot-stuffing applies when lines end with LF-only; the framing dot is still stripped.
        /// </summary>

        [Fact]
        public async Task LfOnlySingleDotLine_DropsTheFramingDot()
        {
            var payload = await ReadAsync(".foo\n.\n"u8.ToArray());
            Assert.Equal("foo\n"u8.ToArray(), payload);
        }

        /// <summary>
        /// Blank lines and mixed LF/CRLF body content survive destuffing unchanged.
        /// </summary>

        [Fact]
        public async Task EmptyBodyLine_IsPreservedBesideStuffedDots()
        {
            var payload = await ReadAsync("\r\n.x\r\n\n.\r\n"u8.ToArray());
            Assert.Equal("\r\nx\r\n\n"u8.ToArray(), payload);
        }

        /// <summary>
        /// Chunked reads match a contiguous read for mixed stuffed lines and the terminator.
        /// </summary>

        [Fact]
        public async Task SplitLeadingDotsAndTerminator_MatchContiguousRead()
        {
            var wire = ".a\r\n..b\r\n...c\r\n.\r\n"u8.ToArray();
            var expected = "a\r\n.b\r\n..c\r\n"u8.ToArray();
            Assert.Equal(expected, await ReadAsync(wire));
            Assert.Equal(expected, await ReadAsync(wire, chunk: 1));
            Assert.Equal(expected, await ReadAsync(wire, chunk: 2));
        }

        /// <summary>
        /// A dot not at line start is not stuffing and is copied verbatim.
        /// </summary>

        [Fact]
        public async Task DotLaterInLine_IsStored()
        {
            var payload = await ReadAsync("a.b\r\n.\r\n"u8.ToArray());
            Assert.Equal("a.b\r\n"u8.ToArray(), payload);
        }

        /// <summary>
        /// Mixed line endings in the body are preserved; only line-start stuffing rules apply.
        /// </summary>

        [Fact]
        public async Task CrlfLfCrAndEmptyLine_ArePreserved()
        {
            var payload = await ReadAsync("a\r\n\r\nb\nc\rd\r\n.\r\n"u8.ToArray());
            Assert.Equal("a\r\n\r\nb\nc\rd\r\n"u8.ToArray(), payload);
        }

        /// <summary>
        /// Stream ending before a complete terminator throws EndOfStreamException instead of returning partial payload.
        /// </summary>

        [Fact]
        public async Task FinalFragmentWithoutLineEnding_IsNotATerminator()
        {
            await Assert.ThrowsAsync<EndOfStreamException>(() => ReadAsync("abc.\r\n"u8.ToArray()));
        }

        /// <summary>
        /// When the last body line ends with CR immediately before the terminator, that CR remains in the payload.
        /// </summary>

        [Fact]
        public async Task FinalLineWithoutCrlf_PreservesTrailingCr()
        {
            var payload = await ReadAsync("last\r.\r\n"u8.ToArray());
            Assert.Equal("last\r"u8.ToArray(), payload);
        }

        /// <summary>
        /// Large payloads with embedded dots match between single-read and byte-at-a-time chunked reads.
        /// </summary>

        [Fact]
        public async Task OrdinaryRunLargerThanFourKibibytes_MatchesChunkedRead()
        {
            var payload = new byte[8192];
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(32 + (i % 90));
            }

            payload[100] = (byte)'\n';
            payload[101] = (byte)'.';
            payload[102] = (byte)'x';
            payload[200] = (byte)'.';
            payload[^2] = (byte)'\r';
            payload[^1] = (byte)'\n';
            var framed = new byte[payload.Length + 4];
            payload.AsSpan(..101).CopyTo(framed);
            framed[101] = (byte)'.';
            payload.AsSpan(101).CopyTo(framed.AsSpan(102));
            framed[^3] = (byte)'.';
            framed[^2] = (byte)'\r';
            framed[^1] = (byte)'\n';

            var contiguous = await ReadAsync(framed);
            var split = await ReadAsync(framed, chunk: 1);
            Assert.Equal(payload, contiguous);
            Assert.Equal(payload, split);
        }

        /// <summary>
        /// CRLF-terminated body ends before the lone-dot terminator line.
        /// </summary>

        [Fact]
        public async Task TerminatorAfterCrLf_OmitsTerminator()
        {
            var payload = await ReadAsync("abc\r\n.\r\n"u8.ToArray());
            Assert.Equal("abc\r\n"u8.ToArray(), payload);
        }

        /// <summary>
        /// LF-only terminator line is recognized and excluded from payload.
        /// </summary>

        [Fact]
        public async Task LfTerminator_OmitsTerminator()
        {
            var payload = await ReadAsync("abc\n.\n"u8.ToArray());
            Assert.Equal("abc\n"u8.ToArray(), payload);
        }

        /// <summary>
        /// An immediate terminator yields zero-length payload bytes.
        /// </summary>

        [Fact]
        public async Task EmptyPayload_IsEmptyArray()
        {
            var payload = await ReadAsync(".\r\n"u8.ToArray());
            Assert.Empty(payload);
        }

        /// <summary>
        /// Destuffed payload whose size equals maxBytes is accepted.
        /// </summary>

        [Fact]
        public async Task ExactlyMaxBytes_Succeeds()
        {
            var payload = new byte[32];
            payload.AsSpan(..30).Fill((byte)'x');
            payload[30] = (byte)'\r';
            payload[31] = (byte)'\n';
            var framed = new byte[payload.Length + 3];
            payload.CopyTo(framed);
            framed[^3] = (byte)'.';
            framed[^2] = (byte)'\r';
            framed[^1] = (byte)'\n';
            var actual = await ReadAsync(framed, maxBytes: payload.Length);
            Assert.Equal(payload, actual);
        }

        /// <summary>
        /// Payload at the configured 5 MiB maximum succeeds without truncation.
        /// </summary>

        [Fact]
        public async Task ExactlyConfiguredMaximum_Succeeds()
        {
            const int maxBytes = 5 * 1024 * 1024;
            var payload = new byte[maxBytes];
            payload.AsSpan(..^2).Fill((byte)'x');
            payload[^2] = (byte)'\r';
            payload[^1] = (byte)'\n';
            var framed = new byte[payload.Length + 3];
            payload.CopyTo(framed);
            framed[^3] = (byte)'.';
            framed[^2] = (byte)'\r';
            framed[^1] = (byte)'\n';
            var actual = await ReadAsync(framed, maxBytes);
            Assert.Equal(maxBytes, actual.Length);
            Assert.Equal(payload, actual);
        }

        /// <summary>
        /// One byte over the configured maximum throws InvalidOperationException referencing MaxArticleBytes.
        /// </summary>

        [Fact]
        public async Task OnePastConfiguredMaximum_Throws()
        {
            const int maxBytes = 5 * 1024 * 1024;
            var payload = new byte[maxBytes + 1];
            payload.AsSpan(..^2).Fill((byte)'y');
            payload[^2] = (byte)'\r';
            payload[^1] = (byte)'\n';
            var framed = new byte[payload.Length + 3];
            payload.CopyTo(framed);
            framed[^3] = (byte)'.';
            framed[^2] = (byte)'\r';
            framed[^1] = (byte)'\n';
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(framed, maxBytes));
            Assert.Contains("MaxArticleBytes", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Custom maxBytes ceiling is enforced before returning oversize payload.
        /// </summary>

        [Fact]
        public async Task OnePastMaxBytes_ThrowsAndDoesNotReturnPayload()
        {
            var payload = new byte[33];
            payload.AsSpan(..31).Fill((byte)'x');
            payload[31] = (byte)'\r';
            payload[32] = (byte)'\n';
            var framed = new byte[payload.Length + 3];
            payload.CopyTo(framed);
            framed[^3] = (byte)'.';
            framed[^2] = (byte)'\r';
            framed[^1] = (byte)'\n';
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(framed, maxBytes: 32));
            Assert.Contains("MaxArticleBytes", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Stream ending before a valid terminator throws EndOfStreamException.
        /// </summary>

        [Fact]
        public async Task EndOfStreamBeforeTerminator_Throws()
        {
            await Assert.ThrowsAsync<EndOfStreamException>(() => ReadAsync("no-terminator\r\n"u8.ToArray()));
        }

        /// <summary>
        /// Canceled token during read surfaces OperationCanceledException.
        /// </summary>

        [Fact]
        public async Task Cancellation_Throws()
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ReadAsync("abc\r\n.\r\n"u8.ToArray(), cancellationToken: cts.Token));
        }

        /// <summary>
        /// Read timeout on a stalled stream surfaces OperationCanceledException from the reader timeout path.
        /// </summary>

        [Fact]
        public async Task Timeout_Throws()
        {
            var stream = new HoldStream([(byte)'a']);
            var reader = new NntpStreamReader(stream, 1024);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                reader.ReadArticlePayloadAsync(1024, TimeSpan.FromMilliseconds(50), CancellationToken.None));
        }

        /// <summary>
        /// Dot classification at chunk boundaries matches contiguous reads for mixed stuffed and normal lines.
        /// </summary>

        [Fact]
        public async Task SplitDotClassification_MatchesContiguousRead()
        {
            var wire = "..x\r\n.y\r\na.b\r\n.\r\n"u8.ToArray();
            var contiguous = await ReadAsync(wire);
            var split = await ReadAsync(wire, chunk: 1);
            Assert.Equal(contiguous, split);
            Assert.Equal(".x\r\ny\r\na.b\r\n"u8.ToArray(), split);
        }

        /// <summary>
        /// After a failed read, the next article read starts without bytes from the aborted article.
        /// </summary>

        [Fact]
        public async Task SecondArticleAfterFailure_DoesNotIncludePartialBytes()
        {
            var stream = new FailThenServeStream("PARTIAL"u8.ToArray(), "ok\r\n.\r\n"u8.ToArray());
            var reader = new NntpStreamReader(stream, 1024);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                reader.ReadArticlePayloadAsync(1024, TimeSpan.FromSeconds(5), CancellationToken.None));
            var payload = await reader.ReadArticlePayloadAsync(1024, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal("ok\r\n"u8.ToArray(), payload);
        }

        /// <summary>
        /// Consumer callback observes full first-article bytes before internal buffers are reused.
        /// </summary>

        [Fact]
        public async Task Callback_ReceivesExactPayload_BeforeScratchIsReused()
        {
            var first = "aaaa\r\n.\r\n"u8.ToArray();
            var second = "bbbb\r\n.\r\n"u8.ToArray();
            var wire = new byte[first.Length + second.Length];
            first.CopyTo(wire);
            second.CopyTo(wire.AsSpan(first.Length));
            var reader = new NntpStreamReader(new MemoryStream(wire, writable: false), 1024);
            ReadOnlyMemory<byte> exposed = default;
            var seen = false;
            var marker = await reader.ReadArticlePayloadAsync(
                1024,
                TimeSpan.FromSeconds(5),
                memory =>
                {
                    seen = true;
                    exposed = memory;
                    Assert.Equal("aaaa\r\n"u8.ToArray(), memory.ToArray());
                    return memory.Length;
                },
                CancellationToken.None);

            Assert.True(seen);
            Assert.Equal(6, marker);
            var next = await reader.ReadArticlePayloadAsync(1024, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal("bbbb\r\n"u8.ToArray(), next);
            Assert.Equal("bbbb\r\n"u8.ToArray(), exposed.ToArray());
        }

        /// <summary>
        /// Callback exception does not prevent reading a subsequent well-formed article on the same reader.
        /// </summary>

        [Fact]
        public async Task Callback_Exception_StillAllowsTheNextArticle()
        {
            var first = "aaaa\r\n.\r\n"u8.ToArray();
            var second = "ok\r\n.\r\n"u8.ToArray();
            var wire = new byte[first.Length + second.Length];
            first.CopyTo(wire);
            second.CopyTo(wire.AsSpan(first.Length));
            var reader = new NntpStreamReader(new MemoryStream(wire, writable: false), 1024);
            var called = false;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                reader.ReadArticlePayloadAsync(
                    1024,
                    TimeSpan.FromSeconds(5),
                    Fail,
                    CancellationToken.None));

            int Fail(ReadOnlyMemory<byte> memory)
            {
                called = true;
                Assert.Equal("aaaa\r\n"u8.ToArray(), memory.ToArray());
                throw new InvalidOperationException("parse failed");
            }

            Assert.True(called);
            Assert.Equal("parse failed", error.Message);
            var next = await reader.ReadArticlePayloadAsync(1024, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal("ok\r\n"u8.ToArray(), next);
        }

        /// <summary>
        /// Oversize or canceled reads do not invoke the payload callback.
        /// </summary>

        [Fact]
        public async Task Callback_IsNotInvoked_WhenPayloadExceedsMaximumOrIsCancelled()
        {
            var payload = new byte[33];
            payload.AsSpan(..31).Fill((byte)'x');
            payload[31] = (byte)'\r';
            payload[32] = (byte)'\n';
            var framed = new byte[payload.Length + 3];
            payload.CopyTo(framed);
            framed[^3] = (byte)'.';
            framed[^2] = (byte)'\r';
            framed[^1] = (byte)'\n';
            var oversizeCalled = false;
            var oversize = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new NntpStreamReader(new MemoryStream(framed, writable: false), 1024).ReadArticlePayloadAsync(
                    32,
                    TimeSpan.FromSeconds(5),
                    _ =>
                    {
                        oversizeCalled = true;
                        return 0;
                    },
                    CancellationToken.None));
            Assert.Contains("MaxArticleBytes", oversize.Message, StringComparison.Ordinal);
            Assert.False(oversizeCalled);

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            var cancelCalled = false;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new NntpStreamReader(new MemoryStream("abc\r\n.\r\n"u8.ToArray(), writable: false), 1024)
                    .ReadArticlePayloadAsync(
                        1024,
                        TimeSpan.FromSeconds(5),
                        _ =>
                        {
                            cancelCalled = true;
                            return 0;
                        },
                        cts.Token));
            Assert.False(cancelCalled);
        }

        /// <summary>
        /// Sequential articles on one reader are destuffed independently.
        /// </summary>

        [Fact]
        public async Task TwoArticlesOnOneReader_AreIndependent()
        {
            var first = "one\r\n.\r\n"u8.ToArray();
            var second = "..two\r\n.\r\n"u8.ToArray();
            var wire = new byte[first.Length + second.Length];
            first.CopyTo(wire);
            second.CopyTo(wire.AsSpan(first.Length));
            var reader = new NntpStreamReader(new MemoryStream(wire, writable: false), 1024);
            var a = await reader.ReadArticlePayloadAsync(1024, TimeSpan.FromSeconds(5), CancellationToken.None);
            var b = await reader.ReadArticlePayloadAsync(1024, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal("one\r\n"u8.ToArray(), a);
            Assert.Equal(".two\r\n"u8.ToArray(), b);
        }

        /// <summary>
        /// Runs wire bytes through <see cref="NntpStreamReader"/> with optional chunk size, maxBytes, and cancellation for tests.
        /// </summary>
        /// <param name="wire">Framed multiline article bytes including terminator.</param>
        /// <param name="maxBytes">Maximum destuffed payload size passed to the reader.</param>
        /// <param name="chunk">Maximum bytes per stream read; <see cref="int.MaxValue"/> uses a single read.</param>
        /// <param name="cancellationToken">Cancellation forwarded to the reader.</param>
        /// <returns>Destuffed article payload bytes.</returns>
        private static async Task<byte[]> ReadAsync(
            byte[] wire,
            int maxBytes = 1024 * 1024,
            int chunk = int.MaxValue,
            CancellationToken cancellationToken = default)
        {
            Stream stream = chunk == int.MaxValue
                ? new MemoryStream(wire, writable: false)
                : new ChunkStream(wire, chunk);
            using var reader = new NntpStreamReader(stream, 1024);
            return await reader.ReadArticlePayloadAsync(maxBytes, TimeSpan.FromSeconds(5), cancellationToken);
        }

        /// <summary>
        /// Test stream that returns at most a fixed number of bytes per read to exercise incremental parsing.
        /// </summary>

        private sealed class ChunkStream(byte[] data, int chunk) : Stream
        {
            /// <summary>Next unread index in <c>data</c>; EOF is signaled when it reaches the buffer length.</summary>
            private int _pos;

            /// <inheritdoc />
            public override bool CanRead => true;

            /// <inheritdoc />
            public override bool CanSeek => false;

            /// <inheritdoc />
            public override bool CanWrite => false;

            /// <inheritdoc />
            public override long Length => data.Length;

            /// <inheritdoc />
            public override long Position { get => _pos; set => throw new NotSupportedException(); }

            /// <inheritdoc />
            public override void Flush()
            {
            }

            /// <inheritdoc />
            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            /// <summary>Copies at most <paramref name="chunk"/> bytes per call so the reader sees partial frames.</summary>
            /// <param name="buffer">Destination span.</param>
            /// <returns>Bytes copied, or zero at EOF.</returns>
            public override int Read(Span<byte> buffer)
            {
                if (_pos >= data.Length)
                {
                    return 0;
                }

                var n = Math.Min(chunk, Math.Min(buffer.Length, data.Length - _pos));
                data.AsSpan(_pos, n).CopyTo(buffer);
                _pos += n;
                return n;
            }

            /// <inheritdoc />
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ValueTask<int>(Read(buffer.Span));
            }

            /// <inheritdoc />
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void SetLength(long value) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>
        /// Test stream that delivers a prefix then blocks until canceled, simulating a stalled peer.
        /// </summary>

        private sealed class HoldStream(byte[] prefix) : Stream
        {
            /// <summary>Bytes already returned from <c>prefix</c>; further reads wait on cancellation.</summary>
            private int _pos;

            /// <inheritdoc />
            public override bool CanRead => true;

            /// <inheritdoc />
            public override bool CanSeek => false;

            /// <inheritdoc />
            public override bool CanWrite => false;

            /// <inheritdoc />
            public override long Length => prefix.Length;

            /// <inheritdoc />
            public override long Position { get => _pos; set => throw new NotSupportedException(); }

            /// <inheritdoc />
            public override void Flush()
            {
            }

            /// <inheritdoc />
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            /// <summary>Drains <c>prefix</c> then awaits until <paramref name="cancellationToken"/> is signaled.</summary>
            /// <param name="buffer">Destination memory.</param>
            /// <param name="cancellationToken">Cancels the infinite wait after the prefix is exhausted.</param>
            /// <returns>Bytes copied from the prefix, or zero if canceled during the wait.</returns>
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                if (_pos < prefix.Length)
                {
                    var n = Math.Min(buffer.Length, prefix.Length - _pos);
                    prefix.AsSpan(_pos, n).CopyTo(buffer.Span);
                    _pos += n;
                    return n;
                }

                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return 0;
            }

            /// <inheritdoc />
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void SetLength(long value) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>
        /// Test stream that throws after partial bytes then serves a complete second article.
        /// </summary>

        private sealed class FailThenServeStream(byte[] partial, byte[] article) : Stream
        {
            /// <summary>0 while serving <c>partial</c>; 1 after <see cref="OperationCanceledException"/> simulates disconnect before the second payload.</summary>
            private int _phase;

            /// <summary>Read cursor within the active phase buffer.</summary>
            private int _pos;

            /// <inheritdoc />
            public override bool CanRead => true;

            /// <inheritdoc />
            public override bool CanSeek => false;

            /// <inheritdoc />
            public override bool CanWrite => false;

            /// <inheritdoc />
            public override long Length => partial.Length + article.Length;

            /// <inheritdoc />
            public override long Position { get => 0; set => throw new NotSupportedException(); }

            /// <inheritdoc />
            public override void Flush()
            {
            }

            /// <inheritdoc />
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            /// <summary>Emits <c>partial</c>, throws <see cref="OperationCanceledException"/> at EOF of partial, then streams <c>article</c> on subsequent reads.</summary>
            /// <param name="buffer">Destination memory.</param>
            /// <param name="cancellationToken">Honored before each read attempt.</param>
            /// <returns>Bytes copied from the current phase, or zero when the second phase is exhausted.</returns>
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_phase == 0)
                {
                    if (_pos < partial.Length)
                    {
                        var n = Math.Min(buffer.Length, partial.Length - _pos);
                        partial.AsSpan(_pos, n).CopyTo(buffer.Span);
                        _pos += n;
                        return new ValueTask<int>(n);
                    }

                    _phase = 1;
                    _pos = 0;
                    throw new OperationCanceledException();
                }

                if (_pos >= article.Length)
                {
                    return new ValueTask<int>(0);
                }

                var count = Math.Min(buffer.Length, article.Length - _pos);
                article.AsSpan(_pos, count).CopyTo(buffer.Span);
                _pos += count;
                return new ValueTask<int>(count);
            }

            /// <inheritdoc />
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void SetLength(long value) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}

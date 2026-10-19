using System.Text;
using VectorNNTPD.BackFillerService.Nntp;

namespace VectorNNTPD.BackFillerService.Tests.Nntp
{
    /// <summary>
    /// Status-line framing and numeric parsing. The line scratch stays on the reader and text is decoded only on request.
    /// </summary>
    public sealed class NntpStatusLineTests
    {
        /// <summary>An empty stream is EOF, the same result the previous null line used.</summary>
        [Fact]
        public async Task Empty_stream_returns_eof()
        {
            var reader = Reader([]);
            Assert.Equal(-1, await ReadAsync(reader));
        }

        /// <summary>Bytes without a line feed fail as a partial line, and a later empty read is EOF.</summary>
        [Fact]
        public async Task Eof_after_partial_line_throws_and_the_next_read_is_eof()
        {
            var reader = Reader("partial"u8.ToArray());
            var error = await Assert.ThrowsAsync<EndOfStreamException>(() => ReadAsync(reader));
            Assert.Contains("mid-line", error.Message, StringComparison.Ordinal);
            Assert.Equal(-1, await ReadAsync(reader));
        }

        /// <summary>LF and CRLF both yield the same line bytes.</summary>
        [Theory]
        [InlineData("alpha\n")]
        [InlineData("alpha\r\n")]
        public async Task Lf_and_crlf_strip_to_the_same_line(string wire)
        {
            var reader = Reader(Encoding.ASCII.GetBytes(wire));
            Assert.Equal(5, await ReadAsync(reader));
            Assert.Equal("alpha"u8.ToArray(), reader.ConsumedLine.ToArray());
        }

        /// <summary>
        /// CRLF split across reads is one delimiter. A CR that the next byte does not complete stays in the line.
        /// A bare LF is still a delimiter.
        /// </summary>
        [Fact]
        public async Task Split_crlf_is_stripped_and_a_bare_cr_stays()
        {
            var split = Reader(new ChunkedStream(["220 text\r"u8.ToArray(), "\nnext\n"u8.ToArray()]));
            Assert.Equal(8, await ReadAsync(split));
            Assert.Equal("220 text"u8.ToArray(), split.ConsumedLine.ToArray());
            Assert.Equal(4, await ReadAsync(split));
            Assert.Equal("next"u8.ToArray(), split.ConsumedLine.ToArray());

            var embedded = Reader(new ChunkedStream(["220\r"u8.ToArray(), "x\n"u8.ToArray()]));
            Assert.Equal(5, await ReadAsync(embedded));
            Assert.Equal("220\rx"u8.ToArray(), embedded.ConsumedLine.ToArray());

            var lineFeed = Reader(new ChunkedStream(["220\n"u8.ToArray()]));
            Assert.Equal(3, await ReadAsync(lineFeed));
            Assert.Equal("220"u8.ToArray(), lineFeed.ConsumedLine.ToArray());

            var empty = Reader(new ChunkedStream(["\r"u8.ToArray(), "\n"u8.ToArray()]));
            Assert.Equal(0, await ReadAsync(empty));

            var exact = Reader(new ChunkedStream(["abc\r"u8.ToArray(), "\n"u8.ToArray()]));
            Assert.Equal(3, await ReadAsync(exact, maxBytes: 3));
            Assert.Equal("abc"u8.ToArray(), exact.ConsumedLine.ToArray());

            var overlong = Reader(new ChunkedStream(["abcd\r"u8.ToArray(), "\nxyz\n"u8.ToArray()]));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(overlong, maxBytes: 3));
            Assert.Contains("maximum length", error.Message, StringComparison.Ordinal);
            Assert.Equal(4, await ReadAsync(overlong, maxBytes: 8));
            Assert.Equal("abcd"u8.ToArray(), overlong.ConsumedLine.ToArray());
            Assert.Equal(3, await ReadAsync(overlong, maxBytes: 8));
            Assert.Equal("xyz"u8.ToArray(), overlong.ConsumedLine.ToArray());

            var partial = Reader(new ChunkedStream(["a\r"u8.ToArray()]));
            var end = await Assert.ThrowsAsync<EndOfStreamException>(() => ReadAsync(partial));
            Assert.Contains("mid-line", end.Message, StringComparison.Ordinal);
            Assert.Equal(-1, await ReadAsync(partial));
        }

        /// <summary>
        /// The multiline terminator is one dot after CRLF removal, including when the CR and LF arrive in different reads.
        /// A CR that is not followed by LF stays in the line and is not treated as the terminator.
        /// </summary>
        [Fact]
        public async Task Split_crlf_multiline_terminator_is_a_single_dot()
        {
            using (var unsplitStream = new ChunkedStream([".\r\n"u8.ToArray()]))
            using (var unsplit = Reader(unsplitStream))
            {
                Assert.Equal(1, await ReadAsync(unsplit));
                Assert.Equal("."u8.ToArray(), unsplit.ConsumedLine.ToArray());
                Assert.True(NntpProtocolIo.IsMultilineTerminator(unsplit.ConsumedLine.Span));
            }

            using (var splitStream = new ChunkedStream([".\r"u8.ToArray(), "\n"u8.ToArray()]))
            using (var split = Reader(splitStream))
            {
                Assert.Equal(1, await ReadAsync(split));
                Assert.Equal("."u8.ToArray(), split.ConsumedLine.ToArray());
                Assert.True(NntpProtocolIo.IsMultilineTerminator(split.ConsumedLine.Span));
            }

            using (var followedStream = new ChunkedStream([".\r"u8.ToArray(), "\n201 ready\r\n"u8.ToArray()]))
            using (var followed = Reader(followedStream))
            {
                Assert.Equal(1, await ReadAsync(followed));
                Assert.True(NntpProtocolIo.IsMultilineTerminator(followed.ConsumedLine.Span));
                Assert.Equal(9, await ReadAsync(followed));
                Assert.Equal("201 ready"u8.ToArray(), followed.ConsumedLine.ToArray());
                Assert.False(NntpProtocolIo.IsMultilineTerminator(followed.ConsumedLine.Span));
            }

            using (var ordinaryStream = new ChunkedStream(["VERSION 2\r"u8.ToArray(), "\n.\r\n"u8.ToArray()]))
            using (var ordinary = Reader(ordinaryStream))
            {
                Assert.Equal(9, await ReadAsync(ordinary));
                Assert.Equal("VERSION 2"u8.ToArray(), ordinary.ConsumedLine.ToArray());
                Assert.False(NntpProtocolIo.IsMultilineTerminator(ordinary.ConsumedLine.Span));
                Assert.Equal(1, await ReadAsync(ordinary));
                Assert.True(NntpProtocolIo.IsMultilineTerminator(ordinary.ConsumedLine.Span));
            }

            using (var pendingStream = new ChunkedStream([".\r"u8.ToArray(), "x\n"u8.ToArray()]))
            using (var pending = Reader(pendingStream))
            {
                Assert.Equal(3, await ReadAsync(pending));
                Assert.Equal(".\rx"u8.ToArray(), pending.ConsumedLine.ToArray());
                Assert.False(NntpProtocolIo.IsMultilineTerminator(pending.ConsumedLine.Span));
            }

            using (var incompleteStream = new ChunkedStream([".\r"u8.ToArray()]))
            using (var incomplete = Reader(incompleteStream))
            {
                var end = await Assert.ThrowsAsync<EndOfStreamException>(() => ReadAsync(incomplete));
                Assert.Contains("mid-line", end.Message, StringComparison.Ordinal);
                Assert.Equal(-1, await ReadAsync(incomplete));
            }

            using (var capabilitiesStream = new ChunkedStream(
            [
                "101 Capability list follows\r\nVERSION 2\r\n.\r"u8.ToArray(),
                "\n201 ready\r\n"u8.ToArray(),
            ]))
            using (var capabilities = Reader(capabilitiesStream))
            {
                Assert.Equal(27, await ReadAsync(capabilities));
                Assert.Equal("101 Capability list follows"u8.ToArray(), capabilities.ConsumedLine.ToArray());
                Assert.Equal(9, await ReadAsync(capabilities));
                Assert.Equal("VERSION 2"u8.ToArray(), capabilities.ConsumedLine.ToArray());
                Assert.Equal(1, await ReadAsync(capabilities));
                Assert.Equal("."u8.ToArray(), capabilities.ConsumedLine.ToArray());
                Assert.True(NntpProtocolIo.IsMultilineTerminator(capabilities.ConsumedLine.Span));
                Assert.Equal(9, await ReadAsync(capabilities));
                Assert.Equal("201 ready"u8.ToArray(), capabilities.ConsumedLine.ToArray());
                Assert.Equal(0, capabilities.BufferedByteCount);
            }
        }

        /// <summary>A line of exactly the maximum length is accepted. One more byte is rejected before the delimiter is consumed.</summary>
        [Fact]
        public async Task Maximum_length_is_accepted_and_one_extra_byte_is_rejected()
        {
            var accepted = Reader("abc\n"u8.ToArray());
            Assert.Equal(3, await ReadAsync(accepted, maxBytes: 3));
            Assert.Equal("abc"u8.ToArray(), accepted.ConsumedLine.ToArray());

            var rejected = Reader("abcd\nxyz\n"u8.ToArray());
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(rejected, maxBytes: 3));
            Assert.Contains("maximum length", error.Message, StringComparison.Ordinal);
            Assert.Equal(4, await ReadAsync(rejected, maxBytes: 8));
            Assert.Equal("abcd"u8.ToArray(), rejected.ConsumedLine.ToArray());
            Assert.Equal(3, await ReadAsync(rejected, maxBytes: 8));
            Assert.Equal("xyz"u8.ToArray(), rejected.ConsumedLine.ToArray());
        }

        /// <summary>One socket read that contains two lines leaves the second line buffered.</summary>
        [Fact]
        public async Task Multiple_lines_in_one_read_preserve_the_remainder()
        {
            var stream = new CountingStream("one\r\ntwo\r\n"u8.ToArray());
            var reader = Reader(stream);
            Assert.Equal(3, await ReadAsync(reader));
            Assert.Equal("one"u8.ToArray(), reader.ConsumedLine.ToArray());
            Assert.True(reader.BufferedByteCount > 0);
            Assert.Equal(3, await ReadAsync(reader));
            Assert.Equal("two"u8.ToArray(), reader.ConsumedLine.ToArray());
            Assert.Equal(1, stream.Reads);
        }

        /// <summary>A line past the initial scratch still reads, and a later short line reuses that capacity.</summary>
        [Fact]
        public async Task Growth_then_a_shorter_line_reuses_capacity()
        {
            var longLine = new byte[300];
            Array.Fill(longLine, (byte)'x');
            var wire = new byte[longLine.Length + 1 + 3];
            longLine.CopyTo(wire);
            wire[longLine.Length] = (byte)'\n';
            "ok\n"u8.CopyTo(wire.AsSpan(longLine.Length + 1));
            var reader = Reader(wire);

            Assert.Equal(300, await ReadAsync(reader));
            var capacity = reader.LineBufferCapacity;
            Assert.InRange(capacity, 300, 16 * 1024);
            Assert.Equal(2, await ReadAsync(reader));
            Assert.Equal("ok"u8.ToArray(), reader.ConsumedLine.ToArray());
            Assert.Equal(capacity, reader.LineBufferCapacity);
        }

        /// <summary>Holding the previous line memory observes the next read overwrite it.</summary>
        [Fact]
        public async Task Next_read_overwrites_the_previous_line_view()
        {
            var reader = Reader("aaaa\nbbbb\n"u8.ToArray());
            Assert.Equal(4, await ReadAsync(reader));
            var captured = reader.ConsumedLine;
            var copy = captured.ToArray();
            Assert.Equal(4, await ReadAsync(reader));
            Assert.Equal("aaaa"u8.ToArray(), copy);
            Assert.Equal("bbbb"u8.ToArray(), captured.ToArray());
        }

        /// <summary>Cancellation before any byte leaves a later read able to take the following line.</summary>
        [Fact]
        public async Task Cancellation_before_data_leaves_the_reader_able_to_read()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stream = new HoldThenDataStream("ok\n"u8.ToArray(), release);
            var reader = Reader(stream);
            using var cancelled = new CancellationTokenSource();
            var pending = ReadAsync(reader, cancellationToken: cancelled.Token);
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancelled.Cancel();
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(2, await ReadAsync(reader));
            Assert.Equal("ok"u8.ToArray(), reader.ConsumedLine.ToArray());
        }

        /// <summary>A timeout cancellation is the reader's operation-cancelled result and does not consume a later line.</summary>
        [Fact]
        public async Task Timeout_does_not_consume_a_later_line()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stream = new HoldThenDataStream("ok\n"u8.ToArray(), release);
            var reader = Reader(stream);
            var pending = reader.ReadLineAsync(64, TimeSpan.FromMilliseconds(50), CancellationToken.None);
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            release.TrySetResult();
            Assert.Equal(2, await ReadAsync(reader, timeout: TimeSpan.FromSeconds(2)));
            Assert.Equal("ok"u8.ToArray(), reader.ConsumedLine.ToArray());
        }

        /// <summary>Numeric parsing keeps the previous code, separator, and ASCII-replacement rules.</summary>
        [Fact]
        public void Numeric_parse_preserves_codes_separators_and_high_bytes()
        {
            AssertParsed("220", 220, "");
            AssertParsed("220 ", 220, "");
            AssertParsed("220 text", 220, "text");
            AssertParsed("220\ttext", 220, "\ttext");
            AssertParsed("220-text", 220, "-text");
            AssertParsed("099", 99, "");
            AssertParsed("000", 0, "");
            AssertParsed("999 rest", 999, "rest");
            Assert.False(NntpProtocolIo.TryParseStatus("22"u8, out _, out _));
            Assert.False(NntpProtocolIo.TryParseStatus("22a"u8, out _, out _));
            Assert.False(NntpProtocolIo.TryParseStatus("abc"u8, out _, out _));
            Assert.False(NntpProtocolIo.TryParseStatus([], out _, out _));

            var high = new byte[] { (byte)'2', (byte)'2', (byte)'0', (byte)' ', 0xFF };
            Assert.True(NntpProtocolIo.TryParseStatus(high, out var highCode, out var highText));
            Assert.Equal(220, highCode);
            Assert.Equal("?", NntpProtocolIo.DecodeStatusText(highText));

            var line = "220 0 <id@example.invalid> article follows"u8;
            Assert.Equal("0 <id@example.invalid> article follows", NntpProtocolIo.DecodeStatusText(line[4..]));
            var decodedBefore = GC.GetAllocatedBytesForCurrentThread();
            var decoded = NntpProtocolIo.DecodeStatusText(line[4..]);
            var decodedBytes = GC.GetAllocatedBytesForCurrentThread() - decodedBefore;
            Assert.Equal("0 <id@example.invalid> article follows", decoded);
            Assert.True(decodedBytes > 0);
        }

        /// <summary>Checks one status line's code and decoded text.</summary>
        private static void AssertParsed(string wire, int code, string text)
        {
            Assert.True(NntpProtocolIo.TryParseStatus(Encoding.ASCII.GetBytes(wire), out var parsed, out var bytes));
            Assert.Equal(code, parsed);
            Assert.Equal(text, NntpProtocolIo.DecodeStatusText(bytes));
        }

        private static NntpStreamReader Reader(byte[] wire) => Reader(new MemoryStream(wire, writable: false));

        private static NntpStreamReader Reader(Stream stream) => new(stream, 1024);

        private static Task<int> ReadAsync(
            NntpStreamReader reader,
            int maxBytes = 16 * 1024,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) =>
            reader.ReadLineAsync(maxBytes, timeout ?? TimeSpan.FromSeconds(2), cancellationToken);

        private sealed class ChunkedStream(byte[][] chunks) : Stream
        {
            private int _index;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                if (_index >= chunks.Length)
                {
                    return 0;
                }

                var chunk = chunks[_index++];
                chunk.CopyTo(buffer);
                return chunk.Length;
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
                new(Read(buffer.Span));

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class CountingStream(byte[] data) : Stream
        {
            private int _pos;

            public int Reads { get; private set; }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => data.Length;

            public override long Position
            {
                get => _pos;
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                Reads++;
                if (_pos >= data.Length)
                {
                    return 0;
                }

                var n = Math.Min(buffer.Length, data.Length - _pos);
                data.AsSpan(_pos, n).CopyTo(buffer);
                _pos += n;
                return n;
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
                new(Read(buffer.Span));

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class HoldThenDataStream(byte[] data, TaskCompletionSource release) : Stream
        {
            private int _phase;
            private int _pos;

            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => data.Length;

            public override long Position
            {
                get => _pos;
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                if (_phase == 0)
                {
                    _phase = 1;
                    Entered.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return 0;
                }

                if (_pos >= data.Length)
                {
                    return 0;
                }

                var n = Math.Min(buffer.Length, data.Length - _pos);
                data.AsSpan(_pos, n).CopyTo(buffer.Span);
                _pos += n;
                return n;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}

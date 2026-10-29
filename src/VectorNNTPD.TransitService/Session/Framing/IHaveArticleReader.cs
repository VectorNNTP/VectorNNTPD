using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;

namespace VectorNNTPD.TransitService.Session.Framing
{
    /// <summary>
    /// IHAVE article receive: locate <c>CRLF . CRLF</c> and copy stuffed wire octets into one owned buffer.
    /// </summary>
    /// <remarks>
    /// IHAVE is not pipelined (RFC 3977 §6.3.2). TAKETHIS STREAM receive reuses this reader
    /// on the session RX task so the queued payload is one owned stuffed-wire copy
    /// (<c>OwnedWireBuffer.Take()</c>). After that take, Pipe sequences are not retained
    /// and the buffer is handed to <see cref="VectorNNTPD.TransitService.Session.TakeThisPipeline"/>.
    /// This reader does not build an article record. The retained buffer is stuffed
    /// wire with the terminator omitted. The size ceiling is the destuffed article:
    /// one leading stuffing dot removed per line, content CRLF included, terminator
    /// excluded, using <see cref="NntpArticleDestuffer.DestuffedLineBytes(ReadOnlySpan{byte})"/>.
    /// A line that would exceed that ceiling stops further retention; the reader still
    /// consumes through the terminator and returns <see cref="NntpMultilineReadStatus.TooLarge"/>
    /// with an empty payload. IHAVE destuff and <c>ArticleRecordFactory</c> run after
    /// this read, before queue admission. Pipeline workers never call this reader.
    /// </remarks>
    public static class IHaveArticleReader
    {
        /// <summary>Initial owned-buffer capacity when the incoming article size is unknown.</summary>
        public const int InitialCapacity = 64 * 1024;

        /// <summary>
        /// Reads one IHAVE article from <paramref name="reader"/> as owned stuffed wire.
        /// </summary>
        /// <param name="reader">Connection input. Advanced through the article terminator.</param>
        /// <param name="maxArticleBytes">
        /// Destuffed article ceiling. Stuffing dots and the terminator are not counted.
        /// </param>
        /// <param name="cancellationToken">Cancels the pipe read.</param>
        /// <returns>
        /// Stuffed wire without the terminator when the destuffed size fits; otherwise
        /// <see cref="NntpMultilineReadStatus.TooLarge"/> or <see cref="NntpMultilineReadStatus.Incomplete"/>
        /// with an empty payload.
        /// </returns>
        public static async ValueTask<IHaveArticleReadResult> ReadAsync(
            PipeReader reader,
            int maxArticleBytes,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(reader);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxArticleBytes, 1);
            var session = new ReadSession(maxArticleBytes);
            return await session.RunAsync(reader, cancellationToken).ConfigureAwait(false);
        }

        private sealed class ReadSession(int maxArticleBytes)
        {
            private readonly OwnedWireBuffer _article = new(InitialCapacity);
            private int _pipeReads;
            private bool _exceeded;
            private int _destuffed;
            private int _lineStart;

            public async ValueTask<IHaveArticleReadResult> RunAsync(
                PipeReader reader,
                CancellationToken cancellationToken)
            {
                var started = Stopwatch.GetTimestamp();
                while (true)
                {
                    var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    _pipeReads++;
                    var unread = result.Buffer;
                    var examined = unread.End;
                    var atStart = _article.Written == 0;

                    if (NntpDelimiterSearch.TryFindArticleTerminator(
                            unread,
                            atStart,
                            out var payloadBytes,
                            out var consumedBytes))
                    {
                        if (payloadBytes > 0)
                        {
                            Append(unread.Slice(0, payloadBytes));
                        }

                        unread = unread.Slice(consumedBytes);
                        reader.AdvanceTo(unread.Start, unread.Start);
                        return Finish(
                            _exceeded ? NntpMultilineReadStatus.TooLarge : NntpMultilineReadStatus.Completed,
                            started);
                    }

                    var hold = (int)Math.Min(NntpDelimiterSearch.ArticleTerminatorLookbehind, unread.Length);
                    var copyBytes = (int)unread.Length - hold;
                    if (copyBytes > 0)
                    {
                        Append(unread.Slice(0, copyBytes));
                        unread = unread.Slice(copyBytes);
                    }

                    reader.AdvanceTo(unread.Start, examined);
                    if (result.IsCompleted)
                    {
                        return Finish(NntpMultilineReadStatus.Incomplete, started);
                    }
                }
            }

            private void Append(ReadOnlySequence<byte> bytes)
            {
                if (_exceeded || bytes.IsEmpty)
                {
                    return;
                }

                foreach (var segment in bytes)
                {
                    AppendSpan(segment.Span);
                    if (_exceeded)
                    {
                        return;
                    }
                }
            }

            private void AppendSpan(ReadOnlySpan<byte> span)
            {
                var offset = 0;
                while (offset < span.Length && !_exceeded)
                {
                    if (_article.Written > _lineStart
                        && _article.EndsWithCr
                        && span[offset] == (byte)'\n')
                    {
                        _article.AppendRaw(span.Slice(offset, 1));
                        offset++;
                        TryCommitLine();
                        continue;
                    }

                    var slice = span[offset..];
                    var crlf = slice.IndexOf("\r\n"u8);
                    if (crlf < 0)
                    {
                        TryAppendOpenLine(slice);
                        return;
                    }

                    _article.AppendRaw(slice[..(crlf + 2)]);
                    offset += crlf + 2;
                    TryCommitLine();
                }
            }

            private void TryAppendOpenLine(ReadOnlySpan<byte> slice)
            {
                if (slice.IsEmpty)
                {
                    return;
                }

                var lineLength = (_article.Written - _lineStart) + slice.Length;
                var prospective = NntpArticleDestuffer.DestuffedLineBytes(lineLength, LineStartsWithDot(slice));
                if (_destuffed + prospective > maxArticleBytes)
                {
                    RejectOpenLine();
                    return;
                }

                _article.AppendRaw(slice);
            }

            private void TryCommitLine()
            {
                var contentLength = _article.Written - _lineStart - 2;
                var add = NntpArticleDestuffer.DestuffedLineBytes(_article.LineAt(_lineStart, contentLength));
                if (_destuffed + add > maxArticleBytes)
                {
                    RejectOpenLine();
                    return;
                }

                _destuffed += add;
                _lineStart = _article.Written;
            }

            private void RejectOpenLine()
            {
                _article.Truncate(_lineStart);
                _exceeded = true;
            }

            private bool LineStartsWithDot(ReadOnlySpan<byte> upcoming)
            {
                if (_article.Written > _lineStart)
                {
                    return _article.FirstAt(_lineStart) == (byte)'.';
                }

                return upcoming.Length > 0 && upcoming[0] == (byte)'.';
            }

            private IHaveArticleReadResult Finish(NntpMultilineReadStatus status, long started)
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                if (status != NntpMultilineReadStatus.Completed)
                {
                    return new IHaveArticleReadResult(
                        status,
                        ReadOnlyMemory<byte>.Empty,
                        new IHaveReceiveMetrics(_pipeReads, 0, elapsed));
                }

                var payload = _article.Take();
                return new IHaveArticleReadResult(
                    status,
                    payload,
                    new IHaveReceiveMetrics(_pipeReads, payload.Length, elapsed));
            }
        }

        private sealed class OwnedWireBuffer
        {
            private byte[] _buffer;
            private int _written;

            public OwnedWireBuffer(int initialCapacity)
            {
                _buffer = new byte[initialCapacity];
            }

            public int Written => _written;

            public bool EndsWithCr => _written > 0 && _buffer[_written - 1] == (byte)'\r';

            public byte FirstAt(int index) => _buffer[index];

            public ReadOnlySpan<byte> LineAt(int start, int length) => _buffer.AsSpan(start, length);

            public void Truncate(int written) => _written = written;

            public void AppendRaw(ReadOnlySpan<byte> bytes)
            {
                if (bytes.IsEmpty)
                {
                    return;
                }

                Ensure(_written + bytes.Length);
                bytes.CopyTo(_buffer.AsSpan(_written, bytes.Length));
                _written += bytes.Length;
            }

            public ReadOnlyMemory<byte> Take()
            {
                var buffer = _buffer;
                var length = _written;
                _buffer = [];
                _written = 0;
                return buffer.AsMemory(0, length);
            }

            private void Ensure(int needed)
            {
                if (_buffer.Length >= needed)
                {
                    return;
                }

                var next = _buffer.Length < 256 ? 256 : _buffer.Length * 2;
                if (next < needed)
                {
                    next = needed;
                }

                var grown = new byte[next];
                if (_written > 0)
                {
                    _buffer.AsSpan(0, _written).CopyTo(grown);
                }

                _buffer = grown;
            }
        }
    }
}

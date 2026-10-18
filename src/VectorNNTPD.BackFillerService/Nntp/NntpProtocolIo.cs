using System.Text;
using System.Buffers;

namespace VectorNNTPD.BackFillerService.Nntp
{
    /// <summary>
    /// Byte-oriented helpers for NNTP status lines, ASCII command fields, capability labels, and dot-stuffed lines.
    /// </summary>
    internal static class NntpProtocolIo
    {
        /// <summary>CRLF sequence appended to outbound NNTP commands.</summary>
        internal static readonly byte[] Crlf = [(byte)'\r', (byte)'\n'];

        /// <summary>Pre-encoded <c>DATE</c> command, including the terminating CRLF.</summary>
        internal static readonly byte[] DateCommand = "DATE\r\n"u8.ToArray();

        /// <summary>
        /// Pre-encoded <c>ARTICLE </c> prefix. The caller appends the message-id bytes and <see cref="Crlf"/>.
        /// </summary>
        internal static readonly byte[] ArticlePrefix = "ARTICLE "u8.ToArray();

        /// <summary>
        /// Pre-encoded <c>AUTHINFO USER </c> prefix. The caller appends the username and <see cref="Crlf"/>.
        /// </summary>
        internal static readonly byte[] AuthInfoUserPrefix = "AUTHINFO USER "u8.ToArray();

        /// <summary>
        /// Pre-encoded <c>AUTHINFO PASS </c> prefix. The caller appends the password and <see cref="Crlf"/>.
        /// </summary>
        internal static readonly byte[] AuthInfoPassPrefix = "AUTHINFO PASS "u8.ToArray();

        /// <summary>Pre-encoded <c>CAPABILITIES</c> command, including the terminating CRLF.</summary>
        internal static readonly byte[] CapabilitiesCommand = "CAPABILITIES\r\n"u8.ToArray();

        /// <summary>Pre-encoded <c>STARTTLS</c> command, including the terminating CRLF.</summary>
        internal static readonly byte[] StartTlsCommand = "STARTTLS\r\n"u8.ToArray();

        /// <summary>ASCII label <c>STARTTLS</c> passed to <see cref="CapabilityLabelEquals"/>.</summary>
        internal static readonly byte[] StartTlsCapability = "STARTTLS"u8.ToArray();

        /// <summary>
        /// Local ceiling on CAPABILITIES body lines read before the session fails closed.
        /// RFC 3977 §5.2 defines the command, not this count.
        /// </summary>
        internal const int MaxCapabilityLines = 256;

        /// <summary>
        /// Parses one status line into a three-digit code and the remainder of the line.
        /// </summary>
        /// <param name="line">Status line with the LF or CRLF already removed.</param>
        /// <param name="code">Parsed code when the line begins with three ASCII digits; otherwise 0.</param>
        /// <param name="text">
        /// ASCII decoding of the bytes after the code. One leading space is skipped.
        /// Empty when the line is only the code, or when that space is the last byte.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when <paramref name="line"/> begins with three ASCII digits.
        /// A separator other than space is left in <paramref name="text"/>.
        /// Otherwise <see langword="false"/>, <paramref name="code"/> is 0, and <paramref name="text"/> is empty.
        /// </returns>
        /// <remarks>
        /// The code is not restricted to 100–599. Bytes above 127 are decoded with <see cref="Encoding.ASCII"/> replacement.
        /// </remarks>
        internal static bool TryParseStatus(ReadOnlySpan<byte> line, out int code, out string text)
        {
            code = 0;
            text = string.Empty;
            if (line.Length < 3
                || !IsDigit(line[0])
                || !IsDigit(line[1])
                || !IsDigit(line[2]))
            {
                return false;
            }

            code = ((line[0] - (byte)'0') * 100) + ((line[1] - (byte)'0') * 10) + (line[2] - (byte)'0');
            if (line.Length == 3)
            {
                return true;
            }

            var start = 3;
            if (line[3] == (byte)' ')
            {
                start = 4;
            }

            text = start < line.Length
                ? Encoding.ASCII.GetString(line[start..])
                : string.Empty;
            return true;
        }

        /// <summary>Encodes <paramref name="value"/> as ASCII when every character is in the 0–127 range.</summary>
        /// <param name="value">Text to encode. CR and LF are accepted.</param>
        /// <param name="bytes">
        /// Encoded bytes when the method returns <see langword="true"/>; otherwise an empty array.
        /// </param>
        /// <returns>
        /// <see langword="false"/> when any character is above 127. <paramref name="bytes"/> is then empty and nothing is encoded.
        /// </returns>
        internal static bool TryEncodeAscii(string value, out byte[] bytes)
        {
            bytes = [];
            foreach (var ch in value)
            {
                if (ch > 127)
                {
                    return false;
                }
            }

            bytes = Encoding.ASCII.GetBytes(value);
            return true;
        }

        /// <summary>
        /// Returns whether the first capability token on <paramref name="line"/> equals
        /// <paramref name="label"/> using ASCII case-insensitive comparison.
        /// </summary>
        /// <param name="line">One capability line with its line ending removed. Leading spaces are skipped.</param>
        /// <param name="label">Expected label. Compared as a whole token; later capability arguments are ignored.</param>
        /// <returns>
        /// <see langword="false"/> when no token remains or the token length differs.
        /// Only ASCII <c>a</c>–<c>z</c> are folded.
        /// </returns>
        internal static bool CapabilityLabelEquals(ReadOnlySpan<byte> line, ReadOnlySpan<byte> label)
        {
            var start = 0;
            while (start < line.Length && line[start] == (byte)' ')
            {
                start++;
            }

            if (start >= line.Length)
            {
                return false;
            }

            var end = start;
            while (end < line.Length && line[end] != (byte)' ')
            {
                end++;
            }

            var token = line[start..end];
            if (token.Length != label.Length)
            {
                return false;
            }

            for (var i = 0; i < token.Length; i++)
            {
                if (ToAsciiUpper(token[i]) != ToAsciiUpper(label[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Returns whether <paramref name="line"/> is a multiline terminator: a single <c>.</c>.</summary>
        /// <param name="line">One response line with LF or CRLF already removed.</param>
        /// <returns><see langword="true"/> only when the span length is 1 and that byte is <c>.</c>.</returns>
        internal static bool IsMultilineTerminator(ReadOnlySpan<byte> line) =>
            line.Length == 1 && line[0] == (byte)'.';

        /// <summary>
        /// Drops one leading dot from a non-terminator line that begins with <c>.</c>.
        /// </summary>
        /// <param name="line">One multiline line without its line ending.</param>
        /// <returns>A span aliasing <paramref name="line"/>. No copy is made.</returns>
        /// <remarks>
        /// RFC 3977 §3.1.1: a line that is only <c>.</c> is the terminator and is not destuffed here.
        /// Every other line that begins with <c>.</c> loses exactly that one framing dot.
        /// Used for capability lines. ARTICLE payload destuffing is done by <see cref="NntpStreamReader"/>.
        /// </remarks>
        internal static ReadOnlySpan<byte> DestuffDotLine(ReadOnlySpan<byte> line) =>
            line.Length > 1 && line[0] == (byte)'.'
                ? line[1..]
                : line;

        /// <summary>Folds ASCII <c>a</c>–<c>z</c> to uppercase. Every other byte is returned unchanged.</summary>
        /// <param name="value">Byte to fold.</param>
        /// <returns>The folded byte.</returns>
        private static byte ToAsciiUpper(byte value) =>
            value is >= (byte)'a' and <= (byte)'z' ? (byte)(value - 32) : value;

        /// <summary>
        /// Returns whether <paramref name="article"/> contains <c>CRLF CRLF</c> or <c>LF LF</c>.
        /// </summary>
        /// <param name="article">Destuffed ARTICLE payload.</param>
        /// <returns>
        /// <see langword="true"/> when either sequence occurs anywhere in the payload.
        /// The search does not otherwise validate article syntax.
        /// </returns>
        internal static bool HasHeaderBodySeparator(ReadOnlySpan<byte> article)
        {
            var crlfcrlf = "\r\n\r\n"u8;
            var lflf = "\n\n"u8;
            return article.IndexOf(crlfcrlf) >= 0 || article.IndexOf(lflf) >= 0;
        }

        /// <summary>Returns whether <paramref name="value"/> is an ASCII digit <c>0</c>–<c>9</c>.</summary>
        /// <param name="value">Byte to test.</param>
        /// <returns><see langword="true"/> for ASCII digits.</returns>
        private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';
    }

    /// <summary>
    /// Buffers one NNTP stream and reads LF-delimited lines and destuffed ARTICLE payloads.
    /// A CR immediately before LF is stripped. The article terminator line is not part of the payload.
    /// </summary>
    internal sealed class NntpStreamReader
    {
        /// <summary>Transport this reader pulls from. The reader does not own or dispose it.</summary>
        private readonly Stream _stream;

        /// <summary>Receive buffer. Its length is at least 1024 and at least the size requested at construction.</summary>
        private readonly byte[] _buffer;

        /// <summary>Index of the first unread byte in <see cref="_buffer"/>.</summary>
        private int _offset;

        /// <summary>Count of unread bytes beginning at <see cref="_offset"/>.</summary>
        private int _count;

        /// <summary>
        /// Reused destuff scratch. Not returned to callers.
        /// The measured corpus median is 740,474 bytes and p95 is 793,113, so the first
        /// allocation is 1 MiB when <c>maxBytes</c> allows it. A common article then needs
        /// no growth copy. Larger articles double until they fit, and growth stops at the
        /// caller's max instead of allocating that max up front.
        /// The exact-array read copies the written prefix out. The callback read exposes that
        /// prefix only until the callback returns, then the next article may overwrite it.
        /// </summary>
        private byte[] _payloadScratch = [];

        /// <summary>Valid prefix of <see cref="_payloadScratch"/> for the article being read.</summary>
        private int _payloadCount;

        /// <summary>
        /// First scratch size when the caller allows at least this many destuffed bytes.
        /// Chosen so a typical ~740 KiB article fits once. Not <c>MaxArticleBytes</c>.
        /// </summary>
        private const int PayloadScratchInitialBytes = 1024 * 1024;

        /// <summary>Creates a reader over an already connected stream.</summary>
        /// <param name="stream">Transport to read. Ownership stays with the caller.</param>
        /// <param name="receiveBufferBytes">Requested buffer size. Values below 1024 are raised to 1024.</param>
        internal NntpStreamReader(Stream stream, int receiveBufferBytes)
        {
            _stream = stream;
            _buffer = new byte[Math.Max(1024, receiveBufferBytes)];
        }

        /// <summary>Gets unread bytes already taken from the socket but not yet consumed as a line.</summary>
        internal int BufferedByteCount => _count;

        /// <summary>Reads one line, excluding the LF delimiter and a CR immediately before that LF.</summary>
        /// <param name="maxBytes">Maximum accepted line length, excluding the delimiter.</param>
        /// <param name="timeout">Budget for the whole read. Linked with <paramref name="cancellationToken"/>.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>
        /// The line bytes, or <see langword="null"/> when the stream ends before any byte of a new line.
        /// </returns>
        /// <exception cref="EndOfStreamException">The stream ends after one or more bytes of the line were buffered.</exception>
        /// <exception cref="InvalidOperationException">The line exceeds <paramref name="maxBytes"/>.</exception>
        /// <remarks>
        /// Caller cancellation and the timeout both surface as <see cref="OperationCanceledException"/> from the stream read.
        /// This method does not distinguish those causes. A bare LF is a delimiter; CRLF is not required.
        /// </remarks>
        internal async Task<byte[]?> ReadLineAsync(int maxBytes, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var builder = new ArrayBufferWriter<byte>(256);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            while (true)
            {
                if (_count == 0)
                {
                    _offset = 0;
                    _count = await _stream.ReadAsync(_buffer.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
                    if (_count == 0)
                    {
                        return builder.WrittenCount == 0 ? null : throw new EndOfStreamException("NNTP connection closed mid-line.");
                    }
                }

                var span = _buffer.AsSpan(_offset, _count);
                var newline = span.IndexOf((byte)'\n');
                if (newline < 0)
                {
                    if (builder.WrittenCount + span.Length > maxBytes)
                    {
                        throw new InvalidOperationException("NNTP status line exceeded maximum length.");
                    }

                    builder.Write(span);
                    _count = 0;
                    continue;
                }

                var lineEnd = newline;
                if (lineEnd > 0 && span[lineEnd - 1] == (byte)'\r')
                {
                    lineEnd--;
                }

                if (builder.WrittenCount + lineEnd > maxBytes)
                {
                    throw new InvalidOperationException("NNTP status line exceeded maximum length.");
                }

                builder.Write(span[..lineEnd]);
                var consumed = newline + 1;
                _offset += consumed;
                _count -= consumed;
                return builder.WrittenSpan.ToArray();
            }
        }

        /// <summary>
        /// Reads a multiline ARTICLE payload, undoing dot-stuffing and stopping before the terminating <c>.</c> line.
        /// </summary>
        /// <param name="maxBytes">Maximum destuffed payload length. The byte that would exceed it is not stored.</param>
        /// <param name="timeout">Budget for the whole payload. Linked with <paramref name="cancellationToken"/>.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The destuffed payload, excluding the terminator line.</returns>
        /// <exception cref="EndOfStreamException">The stream ends before a terminator line is recognized.</exception>
        /// <exception cref="InvalidOperationException">The destuffed payload reaches <paramref name="maxBytes"/>.</exception>
        /// <remarks>
        /// A line whose first byte is <c>.</c> and whose next bytes are LF, or CR LF, ends the payload and is not stored.
        /// Every other line that begins with <c>.</c> drops exactly that one dot (RFC 3977 §3.1.1). <c>..</c> stores <c>.</c>, and a single leading <c>.</c> stores the following bytes.
        /// When the buffer ends before a leading dot can be classified, the unread tail is kept and another read is issued.
        /// Ordinary runs are copied in bulk into a reader-owned scratch. This overload copies the written prefix into an
        /// exact array before the scratch length is cleared, so unused scratch capacity is not visible to the caller.
        /// Caller cancellation and the timeout both surface as <see cref="OperationCanceledException"/>.
        /// </remarks>
        internal Task<byte[]> ReadArticlePayloadAsync(
            int maxBytes,
            TimeSpan timeout,
            CancellationToken cancellationToken)
            => ReadArticlePayloadAsync(maxBytes, timeout, _ => DetachExactPayload(), cancellationToken);

        /// <summary>
        /// Reads a multiline ARTICLE payload and passes the destuffed scratch prefix to <paramref name="consume"/>.
        /// </summary>
        /// <typeparam name="T">Value returned by <paramref name="consume"/>.</typeparam>
        /// <param name="maxBytes">Maximum destuffed payload length. The byte that would exceed it is not stored.</param>
        /// <param name="timeout">Budget for the whole payload. Linked with <paramref name="cancellationToken"/>.</param>
        /// <param name="consume">
        /// Invoked once the terminator is recognized, with <c>_payloadScratch</c> sliced to the written prefix.
        /// The memory is valid only until this delegate returns. It must not be stored, and this delegate must not await.
        /// </param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The value returned by <paramref name="consume"/>.</returns>
        /// <exception cref="EndOfStreamException">The stream ends before a terminator line is recognized.</exception>
        /// <exception cref="InvalidOperationException">The destuffed payload reaches <paramref name="maxBytes"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="consume"/> is null.</exception>
        /// <remarks>
        /// Destuffing matches <see cref="ReadArticlePayloadAsync(int, TimeSpan, CancellationToken)"/>.
        /// This overload does not copy the prefix. The scratch length is cleared after <paramref name="consume"/> returns,
        /// including when <paramref name="consume"/> throws. A later read on this reader may overwrite the same bytes.
        /// <paramref name="consume"/> is not called when the read fails before a terminator.
        /// Caller cancellation and the timeout both surface as <see cref="OperationCanceledException"/>.
        /// </remarks>
        internal async Task<T> ReadArticlePayloadAsync<T>(
            int maxBytes,
            TimeSpan timeout,
            Func<ReadOnlyMemory<byte>, T> consume,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(consume);
            _payloadCount = 0;
            var atLineStart = true;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            try
            {
                while (true)
                {
                    if (_count == 0)
                    {
                        _offset = 0;
                        _count = await _stream.ReadAsync(_buffer.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
                        if (_count == 0)
                        {
                            throw new EndOfStreamException("NNTP article response ended before terminator line.");
                        }
                    }

                    var span = _buffer.AsSpan(_offset, _count);
                    var index = 0;
                    var needMore = false;
                    while (index < span.Length)
                    {
                        if (atLineStart && span[index] == (byte)'.')
                        {
                            if (index + 1 >= span.Length
                                || (span[index + 1] == (byte)'\r' && index + 2 >= span.Length))
                            {
                                needMore = true;
                                break;
                            }

                            var next = span[index + 1];
                            if (next == (byte)'\n')
                            {
                                Consume(index + 2);
                                return consume(_payloadScratch.AsMemory(0, _payloadCount));
                            }

                            if (next == (byte)'\r' && span[index + 2] == (byte)'\n')
                            {
                                Consume(index + 3);
                                return consume(_payloadScratch.AsMemory(0, _payloadCount));
                            }

                            // Non-terminator line: disregard exactly one leading dot.
                            index += 1;
                            atLineStart = false;
                            continue;
                        }

                        var start = index;
                        do
                        {
                            var current = span[index++];
                            atLineStart = current is (byte)'\r' or (byte)'\n';
                        }
                        while (index < span.Length && !(atLineStart && span[index] == (byte)'.'));

                        AppendPayload(span[start..index], maxBytes);
                    }

                    Consume(index);
                    if (needMore)
                    {
                        await ReadMorePreservingLeftoverAsync(timeoutCts.Token).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _payloadCount = 0;
            }
        }

        /// <summary>Moves any unread tail to the front of the buffer and reads at least one more byte.</summary>
        /// <param name="cancellationToken">Cancels the additional read. The caller passes the timeout-linked token.</param>
        /// <exception cref="EndOfStreamException">The additional read returns no bytes.</exception>
        /// <remarks>
        /// The tail is compacted only when <see cref="_offset"/> is greater than zero.
        /// The follow-up read starts at the current unread count.
        /// </remarks>
        private async Task ReadMorePreservingLeftoverAsync(CancellationToken cancellationToken)
        {
            if (_offset > 0 && _count > 0)
            {
                Buffer.BlockCopy(_buffer, _offset, _buffer, 0, _count);
                _offset = 0;
            }
            else if (_count == 0)
            {
                _offset = 0;
            }

            var additional = await _stream.ReadAsync(_buffer.AsMemory(_count), cancellationToken).ConfigureAwait(false);
            if (additional == 0)
            {
                throw new EndOfStreamException("NNTP article response ended before terminator line.");
            }

            _count += additional;
        }

        /// <summary>Drops <paramref name="count"/> bytes from the front of the unread window.</summary>
        /// <param name="count">Bytes already parsed. The caller must not pass more than <see cref="_count"/>.</param>
        private void Consume(int count)
        {
            _offset += count;
            _count -= count;
        }

        /// <summary>Appends one destuffed payload byte, or throws when the payload is already at the ceiling.</summary>
        /// <param name="value">Byte to store.</param>
        /// <param name="maxBytes">Length the scratch prefix must stay at or below.</param>
        /// <exception cref="InvalidOperationException">The prefix already contains <paramref name="maxBytes"/> bytes.</exception>
        private void AppendPayload(byte value, int maxBytes)
        {
            if (_payloadCount >= maxBytes)
            {
                throw new InvalidOperationException("NNTP article exceeded MaxArticleBytes.");
            }

            EnsurePayloadCapacity(_payloadCount + 1, maxBytes);
            _payloadScratch[_payloadCount++] = value;
        }

        /// <summary>Appends a destuffed run, or throws when the run would pass the ceiling.</summary>
        /// <param name="data">Bytes to store. A prefix that fits is stored before the exception.</param>
        /// <param name="maxBytes">Length the scratch prefix must stay at or below.</param>
        /// <exception cref="InvalidOperationException">Storing <paramref name="data"/> would pass <paramref name="maxBytes"/>.</exception>
        private void AppendPayload(ReadOnlySpan<byte> data, int maxBytes)
        {
            if (data.IsEmpty)
            {
                return;
            }

            var room = maxBytes - _payloadCount;
            if ((uint)data.Length > (uint)room)
            {
                if (room > 0)
                {
                    EnsurePayloadCapacity(_payloadCount + room, maxBytes);
                    data[..room].CopyTo(_payloadScratch.AsSpan(_payloadCount));
                    _payloadCount += room;
                }

                throw new InvalidOperationException("NNTP article exceeded MaxArticleBytes.");
            }

            EnsurePayloadCapacity(_payloadCount + data.Length, maxBytes);
            data.CopyTo(_payloadScratch.AsSpan(_payloadCount));
            _payloadCount += data.Length;
        }

        /// <summary>
        /// Grows <see cref="_payloadScratch"/> to at least <paramref name="needed"/> and at most <paramref name="maxBytes"/>.
        /// </summary>
        /// <param name="needed">Required valid-prefix capacity. Never above <paramref name="maxBytes"/>.</param>
        /// <param name="maxBytes">Caller ceiling.</param>
        private void EnsurePayloadCapacity(int needed, int maxBytes)
        {
            if ((uint)needed <= (uint)_payloadScratch.Length)
            {
                return;
            }

            var size = _payloadScratch.Length == 0
                ? Math.Min(maxBytes, PayloadScratchInitialBytes)
                : _payloadScratch.Length;
            while (size < needed)
            {
                if (size >= maxBytes || size > int.MaxValue / 2)
                {
                    size = maxBytes;
                    break;
                }

                var doubled = size * 2;
                size = doubled > maxBytes ? maxBytes : doubled;
            }

            if (size < needed)
            {
                size = needed;
            }

            var next = GC.AllocateUninitializedArray<byte>(size);
            if (_payloadCount > 0)
            {
                _payloadScratch.AsSpan(0, _payloadCount).CopyTo(next);
            }

            _payloadScratch = next;
        }

        /// <summary>Copies the written prefix into an exact array. Unused scratch capacity is not included.</summary>
        /// <returns>The destuffed payload.</returns>
        private byte[] DetachExactPayload()
        {
            var exact = GC.AllocateUninitializedArray<byte>(_payloadCount);
            if (_payloadCount > 0)
            {
                _payloadScratch.AsSpan(0, _payloadCount).CopyTo(exact);
            }

            return exact;
        }
    }
}

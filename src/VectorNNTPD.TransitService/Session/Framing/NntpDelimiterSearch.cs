using System.Buffers;

namespace VectorNNTPD.TransitService.Session.Framing
{
    /// <summary>
    /// Runtime-vectorized delimiter search for NNTP CRLF framing and multiline terminators.
    /// </summary>
    /// <remarks>
    /// Uses runtime <c>ReadOnlySpan&lt;byte&gt;.IndexOf</c> so hardware acceleration is provided
    /// by the runtime. A scalar fallback is implicit when vectorization is unavailable.
    /// Correctness does not depend on SIMD.
    /// </remarks>
    public static class NntpDelimiterSearch
    {
        /// <summary>CRLF sequence that terminates an NNTP command or article line.</summary>
        public static ReadOnlySpan<byte> Crlf => "\r\n"u8;

        /// <summary>Five-byte article terminator delimiter after a preceding content line.</summary>
        public static ReadOnlySpan<byte> FiveByteTerminator => "\r\n.\r\n"u8;

        /// <summary>Empty-article terminator at the start of a multiline block.</summary>
        public static ReadOnlySpan<byte> EmptyTerminator => ".\r\n"u8;

        /// <summary>Finds the first CRLF in <paramref name="span"/>, or -1.</summary>
        public static int IndexOfCrlf(ReadOnlySpan<byte> span) => span.IndexOf(Crlf);

        /// <summary>Finds the first <c>\r\n.\r\n</c> in <paramref name="span"/>, or -1.</summary>
        public static int IndexOfFiveByteTerminator(ReadOnlySpan<byte> span) => span.IndexOf(FiveByteTerminator);

        /// <summary>
        /// Bytes that must remain unconsumed when a STREAM article terminator is incomplete.
        /// </summary>
        /// <remarks>
        /// <c>\r\n.\r\n</c> is five octets; four may already be present at the tail of a Pipe read.
        /// </remarks>
        public const int ArticleTerminatorLookbehind = 4;

        /// <summary>
        /// Locates the STREAM article terminator in <paramref name="buffer"/> without walking CRLF lines.
        /// </summary>
        /// <param name="buffer">Unread article octets (not previously copied into the parser buffer).</param>
        /// <param name="atArticleStart">
        /// <see langword="true"/> when no article payload has been accepted yet, so a leading
        /// <c>.\r\n</c> is the empty-article terminator.
        /// </param>
        /// <param name="payloadBytes">
        /// Count of <paramref name="buffer"/> octets that are article payload (includes the last
        /// content line's CRLF; excludes the terminator).
        /// </param>
        /// <param name="consumedBytes">
        /// Count of <paramref name="buffer"/> octets to consume (payload plus terminator).
        /// </param>
        /// <returns><see langword="true"/> when a complete terminator is present.</returns>
        /// <remarks>
        /// RFC 3977 §3.1.1: a non-empty multiline block ends with the five octets CRLF "." CRLF;
        /// an empty block is "." CRLF. Payload excludes the terminating line. STREAM copies those
        /// payload octets as received (no destuff).
        /// </remarks>
        public static bool TryFindArticleTerminator(
            ReadOnlySequence<byte> buffer,
            bool atArticleStart,
            out int payloadBytes,
            out int consumedBytes)
        {
            payloadBytes = 0;
            consumedBytes = 0;

            if (atArticleStart && StartsWithEmptyTerminator(buffer))
            {
                consumedBytes = EmptyTerminator.Length;
                return true;
            }

            if (buffer.Length < FiveByteTerminator.Length)
            {
                return false;
            }

            if (buffer.IsSingleSegment)
            {
                var index = IndexOfFiveByteTerminator(buffer.FirstSpan);
                if (index < 0)
                {
                    return false;
                }

                payloadBytes = index + Crlf.Length;
                consumedBytes = index + FiveByteTerminator.Length;
                return true;
            }

            var reader = new SequenceReader<byte>(buffer);
            if (!reader.TryReadTo(out ReadOnlySequence<byte> before, FiveByteTerminator, advancePastDelimiter: true))
            {
                return false;
            }

            payloadBytes = checked((int)before.Length + Crlf.Length);
            consumedBytes = checked((int)reader.Consumed);
            return true;
        }

        /// <summary>Returns whether <paramref name="buffer"/> begins with the empty-article terminator.</summary>
        public static bool StartsWithEmptyTerminator(ReadOnlySequence<byte> buffer)
        {
            if (buffer.Length < EmptyTerminator.Length)
            {
                return false;
            }

            if (buffer.IsSingleSegment)
            {
                return buffer.FirstSpan.StartsWith(EmptyTerminator);
            }

            Span<byte> prefix = stackalloc byte[3];
            buffer.Slice(0, 3).CopyTo(prefix);
            return prefix.SequenceEqual(EmptyTerminator);
        }

        /// <summary>
        /// Reads one CRLF-terminated line from <paramref name="buffer"/> (CRLF not included).
        /// </summary>
        /// <returns><see langword="true"/> when a complete line was found.</returns>
        public static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> lineBytes)
        {
            if (buffer.IsSingleSegment)
            {
                var span = buffer.FirstSpan;
                var crlf = IndexOfCrlf(span);
                if (crlf < 0)
                {
                    lineBytes = default;
                    return false;
                }

                lineBytes = buffer.Slice(0, crlf);
                buffer = buffer.Slice(crlf + 2);
                return true;
            }

            var seqReader = new SequenceReader<byte>(buffer);
            if (!seqReader.TryReadTo(out lineBytes, Crlf))
            {
                lineBytes = default;
                return false;
            }

            buffer = buffer.Slice(seqReader.Position);
            return true;
        }

        /// <summary>Returns whether <paramref name="lineBytes"/> is the NNTP multiline terminator line.</summary>
        public static bool IsTerminatorLine(ReadOnlySequence<byte> lineBytes)
        {
            if (lineBytes.Length != 1)
            {
                return false;
            }

            return FirstByte(lineBytes) == (byte)'.';
        }

        /// <summary>Returns whether <paramref name="line"/> is the NNTP multiline terminator line.</summary>
        public static bool IsTerminatorLine(ReadOnlySpan<byte> line) =>
            line.Length == 1 && line[0] == (byte)'.';

        /// <summary>Returns the first byte of <paramref name="sequence"/>, or -1 when empty.</summary>
        public static int FirstByte(ReadOnlySequence<byte> sequence)
        {
            if (sequence.IsEmpty)
            {
                return -1;
            }

            var first = sequence.FirstSpan;
            if (!first.IsEmpty)
            {
                return first[0];
            }

            foreach (var segment in sequence)
            {
                if (!segment.IsEmpty)
                {
                    return segment.Span[0];
                }
            }

            return -1;
        }
    }
}

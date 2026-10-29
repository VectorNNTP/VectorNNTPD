using System.Buffers;

namespace VectorNNTPD.TransitService.Session.Framing
{
    /// <summary>
    /// Reconstructs NNTP multiline wire bytes from a destuffed article payload (wire-framing utility).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Destuffed payload convention (aligned with <see cref="NntpMultilineDataReader"/> ingest output).
    /// This type does not own article storage or catalogues; callers supply bytes (bench corpus, spool
    /// snapshot, peer feed, etc.).
    /// </para>
    /// <list type="bullet">
    /// <item>Terminator <c>.\r\n</c> is not part of the destuffed payload.</item>
    /// <item>Dot-stuffing is removed on ingest: a wire line starting with <c>..</c> is kept with one leading <c>.</c> removed.</item>
    /// <item>Payload lines use <c>\r\n</c> (CRLF). A final line without CRLF is still emitted with CRLF on the wire.</item>
    /// </list>
    /// <para>
    /// Re-stuffing (RFC 3977 §3.1.1): any destuffed line whose content begins with <c>.</c> gets an extra
    /// <c>.</c> prepended on the wire, then <c>\r\n</c>, then the article is terminated with <c>.\r\n</c>.
    /// </para>
    /// </remarks>
    public static class ArticleWireReconstructor
    {
        private static readonly byte[] Crlf = "\r\n"u8.ToArray();
        private static readonly byte[] Terminator = ".\r\n"u8.ToArray();

        /// <summary>
        /// Re-stuffs a de-stuffed stored article into multiline wire form (no status line).
        /// </summary>
        /// <param name="storedDestuffed">Destuffed article bytes (terminator not present).</param>
        /// <param name="includeTerminator">
        /// When <see langword="true"/>, appends the NNTP multiline terminator <c>.CRLF</c>
        /// (TX / client transfer). When <see langword="false"/>, omits it so the result matches
        /// the IHAVE/TAKETHIS ingestion-queue contract.
        /// </param>
        public static byte[] RestuffArticle(ReadOnlySpan<byte> storedDestuffed, bool includeTerminator = true)
        {
            if (storedDestuffed.IsEmpty)
            {
                return includeTerminator ? Terminator.ToArray() : [];
            }

            var estimated = EstimateRestuffedWireBytes(storedDestuffed, includeTerminator);
            var writer = new ArrayBufferWriter<byte>((int)Math.Min(estimated, int.MaxValue));
            var offset = 0;
            while (offset < storedDestuffed.Length)
            {
                var remaining = storedDestuffed[offset..];
                var crlfAt = remaining.IndexOf(Crlf);
                ReadOnlySpan<byte> line;
                if (crlfAt < 0)
                {
                    line = remaining;
                    offset = storedDestuffed.Length;
                }
                else
                {
                    line = remaining[..crlfAt];
                    offset += crlfAt + 2;
                }

                if (line.Length > 0 && line[0] == (byte)'.')
                {
                    writer.Write([(byte)'.']);
                }

                writer.Write(line);
                writer.Write(Crlf);
            }

            if (includeTerminator)
            {
                writer.Write(Terminator);
            }

            return writer.WrittenSpan.ToArray();
        }

        /// <summary>
        /// Estimates wire article bytes (restuffed body + terminator) without allocating the full buffer.
        /// </summary>
        public static long EstimateRestuffedWireBytes(
            ReadOnlySpan<byte> storedDestuffed,
            bool includeTerminator = true)
        {
            if (storedDestuffed.IsEmpty)
            {
                return includeTerminator ? Terminator.Length : 0;
            }

            long extraDots = 0;
            var offset = 0;
            while (offset < storedDestuffed.Length)
            {
                var remaining = storedDestuffed[offset..];
                var crlfAt = remaining.IndexOf(Crlf);
                ReadOnlySpan<byte> line;
                if (crlfAt < 0)
                {
                    line = remaining;
                    // Missing trailing CRLF will be added on the wire.
                    extraDots += line.Length > 0 && line[0] == (byte)'.' ? 1 : 0;
                    var withoutTerminator = storedDestuffed.Length + 2 + extraDots;
                    return includeTerminator ? withoutTerminator + Terminator.Length : withoutTerminator;
                }

                line = remaining[..crlfAt];
                if (line.Length > 0 && line[0] == (byte)'.')
                {
                    extraDots++;
                }

                offset += crlfAt + 2;
            }

            var complete = storedDestuffed.Length + extraDots;
            return includeTerminator ? complete + Terminator.Length : complete;
        }

        /// <summary>
        /// Splits a destuffed article into header block and body at the first blank line (<c>\r\n\r\n</c>).
        /// </summary>
        /// <param name="storedDestuffedArticle">Full destuffed article (headers + body; no NNTP terminator).</param>
        /// <param name="headers">Header bytes including the blank-line separator when present.</param>
        /// <param name="body">Body bytes after the blank line (maybe empty).</param>
        /// <returns>
        /// <see langword="true"/> when a blank line was found; otherwise <see langword="false"/> and
        /// <paramref name="body"/> is empty (entire payload treated as headers-only / no body).
        /// </returns>
        /// <remarks>
        /// Used by BODY TX to transmit only the body portion. Does not invent alternate separators;
        /// matches CRLF blank-line semantics of the destuffed payload convention.
        /// </remarks>
        public static bool TrySplitHeadersAndBody(
            ReadOnlySpan<byte> storedDestuffedArticle,
            out ReadOnlySpan<byte> headers,
            out ReadOnlySpan<byte> body)
        {
            var separator = "\r\n\r\n"u8;
            var at = storedDestuffedArticle.IndexOf(separator);
            if (at < 0)
            {
                headers = storedDestuffedArticle;
                body = ReadOnlySpan<byte>.Empty;
                return false;
            }

            headers = storedDestuffedArticle[..(at + separator.Length)];
            body = storedDestuffedArticle[(at + separator.Length)..];
            return true;
        }
    }
}

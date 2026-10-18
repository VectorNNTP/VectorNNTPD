namespace VectorNNTPD.Common.Articles.Processing
{
    /// <summary>
    /// Applies NNTP body dot-stuffing while Common writes the canonical retained article.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The parser sees a destuffed article. <see cref="NntpArticleCanonicalMaterializer"/> calls
    /// <see cref="CountExtraDots"/> and <see cref="WriteStuffed"/> while writing the retained buffer,
    /// and only body lines gain a stuffing dot. Header lines that begin with <c>.</c> stay unchanged here.
    /// Delivery framing may stuff those header lines later, in a separate socket image.
    /// </para>
    /// <para>
    /// Headers, including a header line that begins with <c>.</c>, and the header/body separator are copied unchanged.
    /// A body line whose first content byte is <c>.</c> gains one leading <c>.</c>. Content excludes a CR that
    /// immediately precedes LF. The NNTP multiline terminator <c>.CRLF</c> is not appended.
    /// When no body line needs a dot, the original array is returned.
    /// </para>
    /// </remarks>
    internal static class ArticleBodyWireStuffer
    {
        /// <summary>
        /// Returns the retained wire image of <paramref name="logicalArticle"/>.
        /// </summary>
        /// <param name="logicalArticle">Canonical article after date and Path edits, with body dot-stuffing removed.</param>
        /// <returns>
        /// <paramref name="logicalArticle"/> when the body has no line that begins with <c>.</c>.
        /// Otherwise, a new array with those body lines stuffed. The terminator line is not included.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="logicalArticle"/> is null.</exception>
        /// <exception cref="InvalidOperationException">The written length does not match the counted stuffing.</exception>
        internal static byte[] StuffBody(byte[] logicalArticle)
        {
            ArgumentNullException.ThrowIfNull(logicalArticle);
            var bodyStart = FindBodyStart(logicalArticle);
            var extra = CountExtraDots(logicalArticle.AsSpan(bodyStart));
            if (extra == 0)
            {
                return logicalArticle;
            }

            var wire = GC.AllocateUninitializedArray<byte>(logicalArticle.Length + extra);
            logicalArticle.AsSpan(0, bodyStart).CopyTo(wire);
            var written = bodyStart + WriteStuffed(logicalArticle.AsSpan(bodyStart), wire.AsSpan(bodyStart));

            return written != wire.Length ? throw new InvalidOperationException("Article body dot-stuffing length mismatch.") : wire;
        }

        /// <summary>Index of the first body byte, or <paramref name="article"/> length when no separator exists.</summary>
        /// <param name="article">Canonical logical article.</param>
        /// <returns>The byte index immediately after the first <c>CRLF CRLF</c> or <c>LF LF</c> separator.</returns>
        private static int FindBodyStart(ReadOnlySpan<byte> article)
        {
            var crlf = article.IndexOf("\r\n\r\n"u8);
            var lf = article.IndexOf("\n\n"u8);
            if (crlf < 0 && lf < 0)
            {
                return article.Length;
            }

            if (crlf < 0)
            {
                return lf + 2;
            }

            if (lf < 0)
            {
                return crlf + 4;
            }

            return crlf < lf ? crlf + 4 : lf + 2;
        }

        /// <summary>Counts lines in <paramref name="body"/> whose first content byte is <c>.</c>.</summary>
        /// <param name="body">Logical body, already past the header/body separator.</param>
        /// <returns>The number of stuffing dots a stuffed write of <paramref name="body"/> inserts.</returns>
        /// <exception cref="OverflowException">The dot count does not fit in an <see cref="int"/>.</exception>
        internal static int CountExtraDots(ReadOnlySpan<byte> body)
        {
            checked
            {
                var extra = 0;
                var index = 0;
                while (index < body.Length)
                {
                    if (body[index] != (byte)'.')
                    {
                        SkipPlainLines(body, ref index);
                        continue;
                    }

                    if (IsDotCrlf(body, index))
                    {
                        do
                        {
                            extra++;
                            index += 3;
                        }
                        while (IsDotCrlf(body, index));

                        continue;
                    }

                    extra++;
                    SkipThroughLineFeed(body, ref index);
                }

                return extra;
            }
        }

        /// <summary>
        /// Writes <paramref name="body"/> into <paramref name="destination"/> with one extra leading dot on each line that begins with <c>.</c>.
        /// </summary>
        /// <param name="body">Logical body, already past the header/body separator.</param>
        /// <param name="destination">
        /// Buffer of at least <paramref name="body"/> length plus <see cref="CountExtraDots"/>. Not cleared.
        /// </param>
        /// <returns>The number of bytes written.</returns>
        /// <remarks>
        /// A run of lines that do not begin with <c>.</c> is copied once.
        /// A run of <c>.</c> CR LF lines is stored as <c>..</c> CR LF without a per-line copy call.
        /// Any other line that begins with <c>.</c> still gains one leading dot and is then copied.
        /// </remarks>
        internal static int WriteStuffed(ReadOnlySpan<byte> body, Span<byte> destination)
        {
            var written = 0;
            var index = 0;
            while (index < body.Length)
            {
                if (body[index] != (byte)'.')
                {
                    var run = index;
                    SkipPlainLines(body, ref index);
                    var length = index - run;
                    body.Slice(run, length).CopyTo(destination[written..]);
                    written += length;
                    continue;
                }

                if (IsDotCrlf(body, index))
                {
                    do
                    {
                        destination[written] = (byte)'.';
                        destination[written + 1] = (byte)'.';
                        destination[written + 2] = (byte)'\r';
                        destination[written + 3] = (byte)'\n';
                        written += 4;
                        index += 3;
                    }
                    while (IsDotCrlf(body, index));

                    continue;
                }

                destination[written++] = (byte)'.';
                var start = index;
                SkipThroughLineFeed(body, ref index);
                var line = index - start;
                body.Slice(start, line).CopyTo(destination[written..]);
                written += line;
            }

            return written;
        }

        /// <summary>True when <paramref name="index"/> begins the three bytes <c>.</c> CR LF.</summary>
        /// <param name="body">Logical body.</param>
        /// <param name="index">Candidate line start.</param>
        /// <returns><see langword="true"/> when those three bytes are present.</returns>
        private static bool IsDotCrlf(ReadOnlySpan<byte> body, int index) =>
            (uint)(index + 2) < (uint)body.Length
            && body[index] == (byte)'.'
            && body[index + 1] == (byte)'\r'
            && body[index + 2] == (byte)'\n';

        /// <summary>Advances <paramref name="index"/> from a non-dot line start through the following plain lines.</summary>
        /// <param name="body">Logical body.</param>
        /// <param name="index">Line start on entry. Next dot-line start, or the buffer length, on return.</param>
        /// <remarks>A dot after the first byte of a line stays inside that line and is not treated as a new line.</remarks>
        private static void SkipPlainLines(ReadOnlySpan<byte> body, ref int index)
        {
            while (index < body.Length && body[index] != (byte)'.')
            {
                SkipThroughLineFeed(body, ref index);
            }
        }

        /// <summary>Advances <paramref name="index"/> to the first byte after this line's LF, or to the buffer length.</summary>
        /// <param name="body">Logical body.</param>
        /// <param name="index">A byte inside the current line on entry.</param>
        private static void SkipThroughLineFeed(ReadOnlySpan<byte> body, ref int index)
        {
            while (index < body.Length && body[index] != (byte)'\n')
            {
                index++;
            }

            if (index < body.Length)
            {
                index++;
            }
        }
    }
}

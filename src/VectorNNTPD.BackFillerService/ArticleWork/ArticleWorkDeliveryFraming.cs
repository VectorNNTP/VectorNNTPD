namespace VectorNNTPD.BackFillerService.ArticleWork
{
    /// <summary>
    /// Decides whether retained ArtData can be sent as an RFC 3977 multiline block, and applies
    /// header-only dot-stuffing to the bytes that are written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retained ArtData is canonical headers, the header/body separator, and a body whose lines
    /// are already dot-stuffed. The retained buffer is never rewritten. A header line that begins
    /// with <c>.</c> is stuffed only in the socket image, because a client removes one leading dot
    /// per RFC 3977 §3.1.1. Body bytes are copied unchanged so an existing stuffing dot is not added again.
    /// </para>
    /// <para>
    /// Every line in the retained buffer must end in CRLF. A bare CR, a bare LF, or NUL cannot be
    /// conveyed and is not frameable. A dot-leading line with no <c>CRLF CRLF</c> separator is also
    /// not frameable: the header region and the already-stuffed body cannot be told apart, so stuffing
    /// the whole buffer could corrupt the body.
    /// </para>
    /// </remarks>
    internal static class ArticleWorkDeliveryFraming
    {
        /// <summary>
        /// Reports whether <paramref name="artData"/> can be sent as a multiline block without changing the retained bytes.
        /// </summary>
        /// <param name="artData">Retained article. Not modified.</param>
        /// <returns>
        /// <see langword="true"/> when every line ends in CRLF, the buffer ends in CRLF, NUL is absent,
        /// and any dot-leading line sits in a header region before <c>CRLF CRLF</c>.
        /// </returns>
        internal static bool IsFrameable(ReadOnlySpan<byte> artData)
        {
            if (!HasExclusiveCrlfLines(artData))
            {
                return false;
            }

            if (TryFindBodyStart(artData, out _))
            {
                return true;
            }

            return !HasDotLeadingLine(artData);
        }

        /// <summary>
        /// Copies <paramref name="artData"/> with one extra leading dot on each header line that begins with <c>.</c>.
        /// </summary>
        /// <param name="artData">Retained article. Not modified.</param>
        /// <param name="stuffed">
        /// The socket image when at least one header line needed a dot.
        /// <see langword="null"/> when the caller should send <paramref name="artData"/> unchanged,
        /// or when <paramref name="artData"/> is not frameable.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when <paramref name="stuffed"/> holds the header-stuffed image.
        /// <see langword="false"/> when the article is not frameable, has no header/body separator,
        /// or no header line begins with <c>.</c>.
        /// </returns>
        /// <remarks>
        /// <see cref="IsFrameable"/> runs before the header region is located for stuffing and before any socket image is allocated.
        /// A header/body separator alone does not make a buffer frameable. Body bytes in a successful image are copied unchanged.
        /// </remarks>
        internal static bool TryStuffHeaderLines(ReadOnlySpan<byte> artData, out byte[]? stuffed)
        {
            stuffed = null;
            if (!IsFrameable(artData) || !TryFindBodyStart(artData, out var bodyStart))
            {
                return false;
            }

            var extra = CountDotLeadingLines(artData[..bodyStart]);
            if (extra == 0)
            {
                return false;
            }

            stuffed = GC.AllocateUninitializedArray<byte>(artData.Length + extra);
            var written = WriteDotStuffedLines(artData[..bodyStart], stuffed);
            artData[bodyStart..].CopyTo(stuffed.AsSpan(written));
            return true;
        }

        /// <summary>True when every CR is the start of CRLF, no bare LF or NUL appears, and the buffer ends in CRLF.</summary>
        /// <param name="artData">Retained article. Not modified.</param>
        /// <returns><see langword="false"/> for an empty buffer, a missing final CRLF, or an interior bare CR, bare LF, or NUL.</returns>
        private static bool HasExclusiveCrlfLines(ReadOnlySpan<byte> artData)
        {
            if (artData.Length < 2 || artData[^2] != (byte)'\r' || artData[^1] != (byte)'\n')
            {
                return false;
            }

            for (var i = 0; i < artData.Length; i++)
            {
                var value = artData[i];
                if (value is 0 or (byte)'\n')
                {
                    return false;
                }

                if (value != (byte)'\r')
                {
                    continue;
                }

                if ((uint)(i + 1) >= (uint)artData.Length || artData[i + 1] != (byte)'\n')
                {
                    return false;
                }

                i++;
            }

            return true;
        }

        /// <summary>Locates the first body byte after <c>CRLF CRLF</c>.</summary>
        /// <param name="artData">Retained article.</param>
        /// <param name="bodyStart">Index of the first body byte when the separator exists.</param>
        /// <returns><see langword="false"/> when the separator is absent.</returns>
        private static bool TryFindBodyStart(ReadOnlySpan<byte> artData, out int bodyStart)
        {
            var separator = artData.IndexOf("\r\n\r\n"u8);
            if (separator < 0)
            {
                bodyStart = 0;
                return false;
            }

            bodyStart = separator + 4;
            return true;
        }

        /// <summary>True when any CRLF line in <paramref name="artData"/> begins with <c>.</c>.</summary>
        /// <param name="artData">A CRLF-terminated region.</param>
        /// <returns><see langword="true"/> when stuffing that region would insert a dot.</returns>
        private static bool HasDotLeadingLine(ReadOnlySpan<byte> artData) =>
            CountDotLeadingLines(artData) > 0;

        /// <summary>Counts CRLF lines in <paramref name="region"/> whose first byte is <c>.</c>.</summary>
        /// <param name="region">Header region, including the separator, or a whole buffer with no separator.</param>
        /// <returns>The number of stuffing dots a header write inserts.</returns>
        private static int CountDotLeadingLines(ReadOnlySpan<byte> region)
        {
            var extra = 0;
            var index = 0;
            while (index < region.Length)
            {
                if (region[index] == (byte)'.')
                {
                    extra++;
                }

                var crlf = region[index..].IndexOf("\r\n"u8);
                if (crlf < 0)
                {
                    break;
                }

                index += crlf + 2;
            }

            return extra;
        }

        /// <summary>Writes <paramref name="region"/> with one extra leading dot on each line that begins with <c>.</c>.</summary>
        /// <param name="region">Header region whose lines end in CRLF.</param>
        /// <param name="destination">Buffer of at least <paramref name="region"/> length plus the stuffing count.</param>
        /// <returns>The number of bytes written.</returns>
        private static int WriteDotStuffedLines(ReadOnlySpan<byte> region, Span<byte> destination)
        {
            var written = 0;
            var index = 0;
            while (index < region.Length)
            {
                if (region[index] == (byte)'.')
                {
                    destination[written++] = (byte)'.';
                }

                var crlf = region[index..].IndexOf("\r\n"u8);
                var lineLength = crlf + 2;
                region.Slice(index, lineLength).CopyTo(destination[written..]);
                written += lineLength;
                index += lineLength;
            }

            return written;
        }
    }
}

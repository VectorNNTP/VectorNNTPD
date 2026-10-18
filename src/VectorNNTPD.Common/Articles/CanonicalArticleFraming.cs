namespace VectorNNTPD.Common.Articles
{
    /// <summary>
    /// Framing of one logical canonical article: whether it can be an NNTP multiline block, and where the body starts.
    /// </summary>
    /// <remarks>
    /// Computed from the logical article bytes, never from unused capacity in a rented array.
    /// <see cref="BodyStart"/> is the index of the first byte after <c>CRLF CRLF</c>, or <c>-1</c> when that separator is absent.
    /// The value is a snapshot of the span passed to <see cref="Analyze"/>. It does not track later writes to the array.
    /// </remarks>
    internal readonly struct CanonicalArticleFraming
    {
        /// <summary>Not frameable, and no header/body separator.</summary>
        internal static CanonicalArticleFraming None { get; } = new(false, -1);

        /// <summary>Stores one analysis result.</summary>
        /// <param name="isFrameable">Whether the span can be sent as a multiline block.</param>
        /// <param name="bodyStart">Index of the first body byte, or <c>-1</c> when there is no <c>CRLF CRLF</c>.</param>
        private CanonicalArticleFraming(bool isFrameable, int bodyStart)
        {
            IsFrameable = isFrameable;
            BodyStart = bodyStart;
        }

        /// <summary>
        /// Gets whether every line ends in CRLF, the span ends in CRLF, NUL is absent,
        /// and any dot-leading line sits before <c>CRLF CRLF</c>.
        /// </summary>
        internal bool IsFrameable { get; }

        /// <summary>Gets the index of the first body byte, or <c>-1</c> when the span has no <c>CRLF CRLF</c>.</summary>
        internal int BodyStart { get; }

        /// <summary>Analyzes <paramref name="artData"/> once. The span is not modified.</summary>
        /// <param name="artData">Logical canonical bytes. Slack beyond this span is ignored.</param>
        /// <returns>The framing of <paramref name="artData"/>.</returns>
        internal static CanonicalArticleFraming Analyze(ReadOnlySpan<byte> artData)
        {
            var bodyStart = FindBodyStart(artData);
            if (!HasExclusiveCrlfLines(artData))
            {
                return new CanonicalArticleFraming(false, bodyStart);
            }

            if (bodyStart >= 0)
            {
                return new CanonicalArticleFraming(true, bodyStart);
            }

            return new CanonicalArticleFraming(!HasDotLeadingLine(artData), -1);
        }

        /// <summary>True when every CR is the start of CRLF, no bare LF or NUL appears, and the span ends in CRLF.</summary>
        /// <param name="artData">Logical article. Not modified.</param>
        /// <returns><see langword="false"/> for an empty span, a missing final CRLF, or an interior bare CR, bare LF, or NUL.</returns>
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
        /// <param name="artData">Logical article.</param>
        /// <returns>The body index, or <c>-1</c> when the separator is absent.</returns>
        private static int FindBodyStart(ReadOnlySpan<byte> artData)
        {
            var separator = artData.IndexOf("\r\n\r\n"u8);
            return separator < 0 ? -1 : separator + 4;
        }

        /// <summary>True when any CRLF line in <paramref name="artData"/> begins with <c>.</c>.</summary>
        /// <param name="artData">A CRLF-terminated region.</param>
        /// <returns><see langword="true"/> when stuffing that region would insert a dot.</returns>
        private static bool HasDotLeadingLine(ReadOnlySpan<byte> artData)
        {
            var index = 0;
            while (index < artData.Length)
            {
                if (artData[index] == (byte)'.')
                {
                    return true;
                }

                var crlf = artData[index..].IndexOf("\r\n"u8);
                if (crlf < 0)
                {
                    return false;
                }

                index += crlf + 2;
            }

            return false;
        }
    }
}

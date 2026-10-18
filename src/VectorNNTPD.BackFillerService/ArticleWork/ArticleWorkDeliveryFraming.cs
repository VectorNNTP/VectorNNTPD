using System.Diagnostics.CodeAnalysis;
using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.BackFillerService.ArticleWork
{
    /// <summary>
    /// Decides whether retained ArtData can be sent as an RFC 3977 multiline block, and builds the
    /// stuffed header bytes that are written ahead of the retained body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retained ArtData is canonical headers, the header/body separator, and a body whose lines
    /// are already dot-stuffed. The retained buffer is never rewritten. A header line that begins
    /// with <c>.</c> is stuffed only in the socket image, because a client removes one leading dot
    /// per RFC 3977 §3.1.1. The body is the existing slice and is not copied into that image.
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
        /// <param name="artData">Retained article. Not modified. Only this span is examined.</param>
        /// <returns>
        /// <see langword="true"/> when every line ends in CRLF, the buffer ends in CRLF, NUL is absent,
        /// and any dot-leading line sits in a header region before <c>CRLF CRLF</c>.
        /// </returns>
        internal static bool IsFrameable(ReadOnlySpan<byte> artData) =>
            CanonicalArticleFraming.Analyze(artData).IsFrameable;

        /// <summary>
        /// Builds the dot-stuffed header image when a header line begins with <c>.</c>.
        /// </summary>
        /// <param name="artData">Retained article. Not modified.</param>
        /// <param name="stuffedHeader">
        /// The stuffed header, including the header/body separator, when at least one header line needed a dot.
        /// <see langword="null"/> when the caller should send <paramref name="artData"/> unchanged,
        /// or when <paramref name="artData"/> is not frameable.
        /// The body is not included.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when <paramref name="stuffedHeader"/> holds the stuffed header.
        /// <see langword="false"/> when the article is not frameable, has no header/body separator,
        /// or no header line begins with <c>.</c>.
        /// </returns>
        /// <remarks>
        /// <see cref="IsFrameable"/> runs before the header is copied. The body stays in <paramref name="artData"/>.
        /// </remarks>
        internal static bool TryStuffHeaderLines(
            ReadOnlySpan<byte> artData,
            [NotNullWhen(true)] out byte[]? stuffedHeader)
        {
            stuffedHeader = null;
            var framing = CanonicalArticleFraming.Analyze(artData);
            if (!framing.IsFrameable)
            {
                return false;
            }

            return TryCreateStuffedHeader(artData, framing.BodyStart, out stuffedHeader);
        }

        /// <summary>
        /// Copies <paramref name="artData"/> through <paramref name="bodyStart"/> with one extra leading dot
        /// on each header line that begins with <c>.</c>.
        /// </summary>
        /// <param name="artData">Retained article. Not modified. Bytes at and after <paramref name="bodyStart"/> are not read for stuffing.</param>
        /// <param name="bodyStart">Index of the first body byte from an earlier framing analysis of this same span.</param>
        /// <param name="stuffedHeader">The stuffed header when a dot was inserted. Otherwise <see langword="null"/>.</param>
        /// <returns><see langword="true"/> when <paramref name="stuffedHeader"/> was allocated.</returns>
        /// <remarks>
        /// Does not walk <paramref name="artData"/> past <paramref name="bodyStart"/> and does not decide frameability.
        /// The caller uses a body start taken from the same logical bytes.
        /// </remarks>
        internal static bool TryCreateStuffedHeader(
            ReadOnlySpan<byte> artData,
            int bodyStart,
            [NotNullWhen(true)] out byte[]? stuffedHeader)
        {
            stuffedHeader = null;
            if ((uint)bodyStart > (uint)artData.Length)
            {
                return false;
            }

            var header = artData[..bodyStart];
            var extra = CountDotLeadingLines(header);
            if (extra == 0)
            {
                return false;
            }

            stuffedHeader = GC.AllocateUninitializedArray<byte>(header.Length + extra);
            WriteDotStuffedLines(header, stuffedHeader);
            return true;
        }

        /// <summary>Counts CRLF lines in <paramref name="region"/> whose first byte is <c>.</c>.</summary>
        /// <param name="region">Header region, including the separator.</param>
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
        /// <param name="destination">Buffer of <paramref name="region"/> length plus the stuffing count.</param>
        private static void WriteDotStuffedLines(ReadOnlySpan<byte> region, Span<byte> destination)
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
        }
    }
}

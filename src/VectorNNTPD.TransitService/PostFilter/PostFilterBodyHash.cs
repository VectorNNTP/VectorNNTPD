using System.Globalization;
using System.IO.Hashing;
using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.TransitService.PostFilter
{
    /// <summary>XXH3-64 of canonical body octets for identical-body quota.</summary>
    /// <remarks>
    /// Does not use <see cref="ArticleRecord.ArtHash"/>. Encoded ArtType bits do not participate.
    /// </remarks>
    internal static class PostFilterBodyHash
    {
        /// <summary>ArtType bits excluded from identical-body quota.</summary>
        public const ArticleType EncodedTypes =
            ArticleType.YEncoded
            | ArticleType.Binary
            | ArticleType.UuEncode
            | ArticleType.Base64
            | ArticleType.BinHex;

        /// <summary>
        /// Returns lowercase hex XXH3-64 of the body after the first <c>CRLF CRLF</c>,
        /// or <see langword="null"/> when the article must not participate.
        /// </summary>
        public static string? TryCompute(in ArticleRecord article)
        {
            if ((article.ArtType & EncodedTypes) != 0)
            {
                return null;
            }

            var data = article.ArtData.Span;
            var separator = data.IndexOf("\r\n\r\n"u8);
            if (separator < 0)
            {
                return null;
            }

            var body = data[(separator + 4)..];
            var hash = XxHash3.HashToUInt64(body);
            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }
    }
}

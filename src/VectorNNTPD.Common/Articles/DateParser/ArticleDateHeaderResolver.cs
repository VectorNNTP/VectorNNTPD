using VectorNNTPD.Common.Articles.Parsing;

namespace VectorNNTPD.Common.Articles.DateParser
{
    /// <summary>
    /// Resolves the first usable article date header in the parser's deterministic candidate order.
    /// </summary>
    /// <remarks>
    /// The parser rejects a second <c>Date</c> header before calling this method, so a later <c>Date</c>
    /// is not a fallback. A missing or unparsable single <c>Date</c> still falls through to later names
    /// such as <c>Injection-Date</c>.
    /// </remarks>
    internal static class ArticleDateHeaderResolver
    {
        /// <summary>
        /// Date-family headers in the order they are tried: Date, Injection-Date, NNTP-Posting-Date, Posted, X-Date, Delivery-Date.
        /// Within one name, the first entry in wire order that unfolds and parses wins. A present but unparsable candidate does not stop the scan of a later name.
        /// The parser rejects a second <c>Date</c> before resolution, so this scan is not the duplicate-<c>Date</c> policy.
        /// </summary>
        private static readonly NntpArticleHeaderName[] CandidateHeaderNames =
        [
            NntpArticleHeaderName.Date,
            NntpArticleHeaderName.InjectionDate,
            NntpArticleHeaderName.NntpPostingDate,
            NntpArticleHeaderName.Posted,
            NntpArticleHeaderName.XDate,
            NntpArticleHeaderName.DeliveryDate,
        ];

        /// <summary>
        /// Tries to resolve and canonicalize one article date from known candidate headers.
        /// </summary>
        /// <param name="articleBytes">Original article buffer that owns the header bytes referenced by <paramref name="headers"/>.</param>
        /// <param name="headers">Parsed header entries in original wire order.</param>
        /// <param name="utc">Canonical UTC instant when a candidate parses successfully.</param>
        /// <param name="originalValue">Slice of the original winning header value when resolution succeeds.</param>
        /// <param name="selectedHeaderName">Known-name identity of the winning date header when resolution succeeds.</param>
        /// <param name="failure">Failure reason reported when no candidate succeeds.</param>
        /// <returns><see langword="true"/> when a candidate header produced a canonical instant.</returns>
        internal static bool TryResolve(
            ReadOnlyMemory<byte> articleBytes,
            ReadOnlySpan<NntpArticleHeaderEntry> headers,
            out DateTime utc,
            out ReadOnlyMemory<byte> originalValue,
            out NntpArticleHeaderName selectedHeaderName,
            out DateParseFailureReason failure)
        {
            utc = default;
            originalValue = default;
            selectedHeaderName = NntpArticleHeaderName.Unknown;
            failure = DateParseFailureReason.Empty;

            if (headers.IsEmpty)
            {
                return false;
            }

            var articleSpan = articleBytes.Span;
            Span<byte> unfoldedDateBuffer = stackalloc byte[DateParseOptions.Default.MaxInputLength];
            for (var i = 0; i < CandidateHeaderNames.Length; i++)
            {
                var candidate = CandidateHeaderNames[i];
                for (var j = 0; j < headers.Length; j++)
                {
                    var entry = headers[j];
                    if (entry.KnownName != candidate)
                    {
                        continue;
                    }

                    var rawDateValue = articleSpan.Slice(entry.ValueOffset, entry.ValueLength);
                    if (!NntpArticleHeaderValueUnfolder.TryUnfold(rawDateValue, unfoldedDateBuffer, out var unfoldedLength))
                    {
                        continue;
                    }

                    if (NewsDateParser.TryGetCanonicalUtc(unfoldedDateBuffer[..unfoldedLength], out utc, out failure))
                    {
                        originalValue = articleBytes.Slice(entry.ValueOffset, entry.ValueLength);
                        selectedHeaderName = candidate;
                        return true;
                    }
                }
            }

            failure = DateParseFailureReason.ParseFailed;
            return false;
        }
    }
}

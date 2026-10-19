using System.Collections;
using System.Runtime.CompilerServices;
using VectorNNTPD.Common.Articles.DateParser;
using VectorNNTPD.Common.Articles.YEnc;

namespace VectorNNTPD.Common.Articles.Parsing
{
    /// <summary>
    /// Detected article content classification produced by the parser.
    /// </summary>
    internal enum NntpArticleType
    {
        /// <summary>The parser could not confidently classify content type.</summary>
        Unknown = 0,

        /// <summary>The article appears to be ordinary textual content.</summary>
        Text = 1,

        /// <summary>The article advertises or resembles MIME multipart content.</summary>
        MimeMultipart = 2,

        /// <summary>The article body contains yEnc section markers within the detection window.</summary>
        YEnc = 3,

        /// <summary>The article advertises binary or encoded transfer content.</summary>
        BinaryEncoded = 4,

        /// <summary>The article has malformed structure preventing reliable classification.</summary>
        Malformed = 5,
    }

    /// <summary>
    /// Deterministic parser rejection or terminal parse-state classification.
    /// </summary>
    public enum NntpArticleParseFailureCode
    {
        /// <summary>Parsing completed successfully.</summary>
        None = 0,

        /// <summary>Article payload is empty.</summary>
        EmptyArticle = 1,

        /// <summary>Header/body boundary was not found within parser limits.</summary>
        MissingHeaderBodySeparator = 2,

        /// <summary>Header section exceeded configured maximum size.</summary>
        HeaderSectionTooLarge = 3,

        /// <summary>Header count exceeded configured maximum.</summary>
        TooManyHeaders = 4,

        /// <summary>Header line exceeded configured maximum line length.</summary>
        HeaderLineTooLong = 5,

        /// <summary>Header syntax is malformed.</summary>
        MalformedHeader = 6,

        /// <summary>Header continuation line appeared without a preceding header.</summary>
        MalformedHeaderContinuation = 7,

        /// <summary>Header name length exceeded configured maximum.</summary>
        HeaderNameTooLong = 8,

        /// <summary>Header value length exceeded configured maximum.</summary>
        HeaderValueTooLong = 9,

        /// <summary>Input contains NUL bytes.</summary>
        ContainsNul = 10,

        /// <summary>Input contains illegal control-byte values in header fields.</summary>
        ContainsIllegalControlByte = 11,

        /// <summary>Article does not contain a usable date header.</summary>
        MissingOrInvalidDate = 12,

        /// <summary>Message-ID is missing.</summary>
        MissingMessageId = 13,

        /// <summary>Message-ID is malformed.</summary>
        InvalidMessageId = 14,

        /// <summary>Newsgroups header is missing.</summary>
        MissingNewsgroups = 15,

        /// <summary>Newsgroups header is malformed.</summary>
        InvalidNewsgroups = 16,

        /// <summary>From header is malformed.</summary>
        InvalidFrom = 17,

        /// <summary>Path header is malformed.</summary>
        InvalidPath = 18,

        /// <summary>Duplicate Message-ID headers were found.</summary>
        DuplicateMessageId = 19,

        /// <summary>Duplicate Newsgroups headers were found.</summary>
        DuplicateNewsgroups = 20,

        /// <summary>Duplicate Path headers were found.</summary>
        DuplicatePath = 21,

        /// <summary>yEnc content was detected and validation failed.</summary>
        YEncDecodingFailed = 22,

        /// <summary>Article size exceeds configured maximum bytes.</summary>
        ArticleTooLarge = 23,

        /// <summary>Body line exceeded configured maximum line length.</summary>
        BodyLineTooLong = 24,

        /// <summary>A second <c>Date</c> header was found. Header-name matching is ASCII case-insensitive.</summary>
        DuplicateDate = 25,
    }

    /// <summary>
    /// Frequently accessed NNTP header names without allocating strings per header.
    /// </summary>
    public enum NntpArticleHeaderName
    {
        /// <summary>Header name is not one of the parser's known fast-path names.</summary>
        Unknown = 0,

        /// <summary><c>Date</c> header.</summary>
        Date = 1,

        /// <summary><c>Injection-Date</c> header.</summary>
        InjectionDate = 2,

        /// <summary><c>NNTP-Posting-Date</c> header.</summary>
        NntpPostingDate = 3,

        /// <summary><c>Posted</c> header.</summary>
        Posted = 4,

        /// <summary><c>X-Date</c> header.</summary>
        XDate = 5,

        /// <summary><c>Delivery-Date</c> header.</summary>
        DeliveryDate = 6,

        /// <summary><c>Path</c> header.</summary>
        Path = 7,

        /// <summary><c>Message-ID</c> header.</summary>
        MessageId = 8,

        /// <summary><c>Newsgroups</c> header.</summary>
        Newsgroups = 9,

        /// <summary><c>From</c> header.</summary>
        From = 10,

        /// <summary><c>Subject</c> header.</summary>
        Subject = 11,

        /// <summary><c>Content-Type</c> header.</summary>
        ContentType = 12,

        /// <summary><c>Content-Transfer-Encoding</c> header.</summary>
        ContentTransferEncoding = 13,

        /// <summary><c>References</c> header. Identified only; not semantically parsed.</summary>
        References = 14,
    }

    /// <summary>
    /// One parsed header as offsets into the original article buffer.
    /// </summary>
    /// <param name="KnownName">Known-name classifier for fast-path lookup.</param>
    /// <param name="NameOffset">Byte offset of header name start within the original article buffer.</param>
    /// <param name="NameLength">Header-name byte length.</param>
    /// <param name="ValueOffset">Byte offset of first header-value byte within the original article buffer.</param>
    /// <param name="ValueLength">Header-value byte length spanning folded continuation bytes exactly as received.</param>
    internal readonly record struct NntpArticleHeaderEntry(
        NntpArticleHeaderName KnownName,
        int NameOffset,
        int NameLength,
        int ValueOffset,
        int ValueLength);

    /// <summary>
    /// Parser limits that bound hostile-input scanning and memory use on the hot path.
    /// </summary>
    /// <param name="MaxArticleBytes">Maximum article payload size accepted by the parser.</param>
    /// <param name="MaxHeaderSectionBytes">Maximum bytes scanned while searching header/body separation.</param>
    /// <param name="MaxHeaderCount">Maximum number of header fields accepted.</param>
    /// <param name="MaxHeaderLineBytes">Maximum characters for one physical header or body line, enforced on ASCII wire bytes.</param>
    /// <param name="MaxHeaderNameBytes">Maximum bytes for one header name token.</param>
    /// <param name="MaxHeaderValueBytes">Maximum bytes in one header value span, from the trimmed first value byte through the end of its last folded continuation line.</param>
    /// <param name="YEncDetectionScanBytes">Maximum body bytes scanned for yEnc marker detection before full validation.</param>
    internal readonly record struct NntpArticleParserOptions(
        int MaxArticleBytes,
        int MaxHeaderSectionBytes,
        int MaxHeaderCount,
        int MaxHeaderLineBytes,
        int MaxHeaderNameBytes,
        int MaxHeaderValueBytes,
        int YEncDetectionScanBytes)
    {
        /// <summary>
        /// Gets the default parser limits tuned for hostile-input safety and transit workloads.
        /// </summary>
        internal static NntpArticleParserOptions Default { get; } = new(
            MaxArticleBytes: ArticleResourceLimits.MaxArticleBytes,
            MaxHeaderSectionBytes: 256 * 1024,
            MaxHeaderCount: 1024,
            MaxHeaderLineBytes: ArticleResourceLimits.MaxArticleLineBytes,
            MaxHeaderNameBytes: 128,
            MaxHeaderValueBytes: 64 * 1024,
            YEncDetectionScanBytes: 64 * 1024);
    }

    /// <summary>
    /// Inline header store used when an article has at most <see cref="Capacity"/> headers.
    /// </summary>
    [InlineArray(NntpArticleHeaderInlineStore.Capacity)]
    internal struct NntpArticleHeaderInlineStore
    {
        /// <summary>Number of header slots stored inline on <see cref="NntpArticleParseResult"/>.</summary>
        public const int Capacity = 48;

        /// <summary>
        /// Inline-array seed. The compiler repeats this slot <see cref="Capacity"/> times; each slot holds one <see cref="NntpArticleHeaderEntry"/>.
        /// </summary>
        private NntpArticleHeaderEntry _element0;
    }

    /// <summary>
    /// Complete output of one NNTP article parse operation.
    /// </summary>
    /// <remarks>
    /// Header, body, and original-header-value members are slices over the caller-supplied article buffer.
    /// They do not copy payload data and inherit that buffer's lifetime.
    /// Canonical Date and Path are exposed as value writes into caller-supplied spans, not heap strings.
    /// </remarks>
    internal readonly struct NntpArticleParseResult
    {
        /// <summary>
        /// First <see cref="NntpArticleHeaderInlineStore.Capacity"/> headers. Unused slots past <c>HeaderCount</c> are not part of the result.
        /// </summary>
        private readonly NntpArticleHeaderInlineStore _inlineHeaders;

        /// <summary>
        /// Headers past the inline capacity, or <see langword="null"/> when every header fit inline.
        /// The array may be longer than the stored count; only <c>HeaderCount</c> entries are live.
        /// </summary>
        private readonly NntpArticleHeaderEntry[]? _overflowHeaders;

        /// <summary>
        /// Parser local-identity bytes copied into the result. Lifetime is the parser instance that produced this result.
        /// </summary>
        private readonly ReadOnlyMemory<byte> _localIdentity;

        /// <summary>
        /// Initializes a parse result.
        /// </summary>
        /// <param name="isAccepted">Whether the article passed parser validation.</param>
        /// <param name="failureCode">Rejection classification when not accepted.</param>
        /// <param name="articleType">Detected article type classification.</param>
        /// <param name="articleBytes">Original article bytes supplied to the parser.</param>
        /// <param name="headerBytes">Header section bytes as a slice of <paramref name="articleBytes"/>.</param>
        /// <param name="bodyBytes">Body section bytes as a slice of <paramref name="articleBytes"/>.</param>
        /// <param name="headers">Parsed header entries in original wire order.</param>
        /// <param name="headerCount">Number of parsed headers.</param>
        /// <param name="dateFailureReason">Date parse classification when date canonicalization fails.</param>
        /// <param name="canonicalUtc">Canonical UTC instant when date resolution succeeds.</param>
        /// <param name="originalDateValue">Original date-header value bytes used by the date resolver.</param>
        /// <param name="selectedDateHeaderName">Known header identity that produced <paramref name="canonicalUtc"/>.</param>
        /// <param name="pathKind">Path rewrite classification for the application FQDN hop.</param>
        /// <param name="containsOrganizationalTracker">Whether a Path token equals the organizational tracker hostname.</param>
        /// <param name="originalPathValue">Original Path-header bytes when present.</param>
        /// <param name="originalMessageIdValue">Original Message-ID header value bytes.</param>
        /// <param name="yEncDetected">Whether yEnc markers were detected in the body scan window.</param>
        /// <param name="localIdentity">Parser local identity used for Path writes. Lifetime is the parser instance.</param>
        /// <param name="yEncValidation">yEnc validation result for the scanned body.</param>
        /// <param name="bodyLineCount">Body line count from the parser's existing body walk.</param>
        internal NntpArticleParseResult(
            bool isAccepted,
            NntpArticleParseFailureCode failureCode,
            NntpArticleType articleType,
            ReadOnlyMemory<byte> articleBytes,
            ReadOnlyMemory<byte> headerBytes,
            ReadOnlyMemory<byte> bodyBytes,
            ReadOnlySpan<NntpArticleHeaderEntry> headers,
            int headerCount,
            DateParseFailureReason dateFailureReason,
            DateTime canonicalUtc,
            ReadOnlyMemory<byte> originalDateValue,
            NntpArticleHeaderName selectedDateHeaderName,
            ArticlePathKind pathKind,
            bool containsOrganizationalTracker,
            ReadOnlyMemory<byte> originalPathValue,
            ReadOnlyMemory<byte> originalMessageIdValue,
            bool yEncDetected,
            ReadOnlyMemory<byte> localIdentity,
            YEncArticleValidationResult yEncValidation,
            int bodyLineCount = 0)
        {
            IsAccepted = isAccepted;
            FailureCode = failureCode;
            ArticleType = articleType;
            ArticleBytes = articleBytes;
            HeaderBytes = headerBytes;
            BodyBytes = bodyBytes;
            HeaderCount = headerCount;
            DateFailureReason = dateFailureReason;
            CanonicalUtc = canonicalUtc;
            OriginalDateValue = originalDateValue;
            SelectedDateHeaderName = selectedDateHeaderName;
            PathKind = pathKind;
            ContainsOrganizationalTracker = containsOrganizationalTracker;
            OriginalPathValue = originalPathValue;
            OriginalMessageIdValue = originalMessageIdValue;
            YEncDetected = yEncDetected;
            BodyLineCount = bodyLineCount;
            YEncValidation = yEncValidation == default
                ? YEncArticleValidationResult.ValidNonYEnc()
                : yEncValidation;
            _localIdentity = localIdentity;
            _overflowHeaders = null;
            _inlineHeaders = default;

            if (headerCount > NntpArticleHeaderInlineStore.Capacity)
            {
                _overflowHeaders = headers[..headerCount].ToArray();
            }
            else
            {
                var store = new NntpArticleHeaderInlineStore();
                for (var i = 0; i < headerCount; i++)
                {
                    store[i] = headers[i];
                }

                _inlineHeaders = store;
            }
        }

        /// <summary>Gets a value indicating whether the article passed parser validation.</summary>
        internal bool IsAccepted { get; }

        /// <summary>Gets the machine-readable rejection classification when <see cref="IsAccepted"/> is false.</summary>
        internal NntpArticleParseFailureCode FailureCode { get; }

        /// <summary>Gets the detected article type classification.</summary>
        internal NntpArticleType ArticleType { get; }

        /// <summary>Gets the original article bytes supplied to the parser.</summary>
        internal ReadOnlyMemory<byte> ArticleBytes { get; }

        /// <summary>Gets the header section bytes as a slice of <see cref="ArticleBytes"/>.</summary>
        internal ReadOnlyMemory<byte> HeaderBytes { get; }

        /// <summary>Gets the body section bytes as a slice of <see cref="ArticleBytes"/>.</summary>
        internal ReadOnlyMemory<byte> BodyBytes { get; }

        /// <summary>Gets the number of parsed header entries.</summary>
        internal int HeaderCount { get; }

        /// <summary>Gets parsed header entries in original wire order.</summary>
        internal NntpArticleHeaderList Headers => new(this);

        /// <summary>Gets the date parse classification when date canonicalization fails.</summary>
        internal DateParseFailureReason DateFailureReason { get; }

        /// <summary>Gets the canonical UTC instant when date resolution succeeds.</summary>
        internal DateTime CanonicalUtc { get; }

        /// <summary>Gets the original date-header value bytes used by the date resolver.</summary>
        internal ReadOnlyMemory<byte> OriginalDateValue { get; }

        /// <summary>Gets the known header identity that produced <see cref="CanonicalUtc"/>.</summary>
        internal NntpArticleHeaderName SelectedDateHeaderName { get; }

        /// <summary>Gets the Path rewrite classification for the application FQDN hop.</summary>
        private ArticlePathKind PathKind { get; }

        /// <summary>
        /// Gets a value indicating whether a Path token equals the organizational tracker hostname
        /// <c>news.usenet.ninja</c> using existing case-insensitive token rules.
        /// </summary>
        internal bool ContainsOrganizationalTracker { get; }

        /// <summary>Gets the original Path-header bytes when present.</summary>
        internal ReadOnlyMemory<byte> OriginalPathValue { get; }

        /// <summary>Gets the original Message-ID header value bytes.</summary>
        internal ReadOnlyMemory<byte> OriginalMessageIdValue { get; }

        /// <summary>Gets a value indicating whether yEnc markers were detected in the body scan window.</summary>
        internal bool YEncDetected { get; }

        /// <summary>
        /// Gets the body line count from the parser's CRLF/CR/LF walk.
        /// Empty body is 0. A final unterminated fragment counts as one line.
        /// </summary>
        internal int BodyLineCount { get; }

        /// <summary>Gets the yEnc validation result for the article body.</summary>
        internal YEncArticleValidationResult YEncValidation { get; }

        /// <summary>
        /// Returns the header entry at <paramref name="index"/>.
        /// </summary>
        /// <param name="index">Zero-based header index.</param>
        /// <returns>The stored header entry.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is outside <see cref="HeaderCount"/>.</exception>
        internal NntpArticleHeaderEntry GetHeader(int index)
        {
            if ((uint)index >= (uint)HeaderCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            if (_overflowHeaders is not null)
            {
                return _overflowHeaders[index];
            }

            var store = _inlineHeaders;
            return store[index];
        }

        /// <summary>
        /// Writes the canonical UTC RFC 5322 date as ASCII bytes.
        /// </summary>
        /// <param name="destination">Destination receiving ASCII bytes.</param>
        /// <param name="bytesWritten">Bytes written on success.</param>
        /// <returns><see langword="true"/> when a date was resolved and <paramref name="destination"/> was large enough.</returns>
        internal bool TryFormatCanonicalUtc(Span<byte> destination, out int bytesWritten)
        {
            if (CanonicalUtc == default && DateFailureReason != DateParseFailureReason.None)
            {
                bytesWritten = 0;
                return false;
            }

            if (CanonicalUtc == default && !IsAccepted && FailureCode == NntpArticleParseFailureCode.MissingOrInvalidDate)
            {
                bytesWritten = 0;
                return false;
            }

            return NewsDateParser.TryFormatCanonicalRfc5322Utc(CanonicalUtc, destination, out bytesWritten);
        }

        /// <summary>
        /// Writes the canonical Path bytes that a later materializer would produce.
        /// </summary>
        /// <param name="mode">Normalize leaves application hops unchanged. Traverse always prepends the parser FQDN.</param>
        /// <param name="destination">Destination receiving ASCII Path bytes.</param>
        /// <param name="bytesWritten">Bytes written on success.</param>
        /// <returns><see langword="true"/> when Path analysis succeeded and <paramref name="destination"/> was large enough.</returns>
        internal bool TryWriteCanonicalPath(ArticlePathMode mode, Span<byte> destination, out int bytesWritten)
            => TryWriteCanonicalPath(mode, ArticlePathCanonicalizer.OrganizationalTrackerHost, destination, out bytesWritten);

        /// <summary>
        /// Writes the canonical Path bytes using <paramref name="organizationalTrackerHost"/> as the tracker component.
        /// </summary>
        /// <param name="mode">Normalize leaves application hops unchanged. Traverse always prepends the parser FQDN.</param>
        /// <param name="organizationalTrackerHost">Tracker component captured for this article.</param>
        /// <param name="destination">Destination receiving ASCII Path bytes.</param>
        /// <param name="bytesWritten">Bytes written on success.</param>
        /// <returns><see langword="true"/> when Path analysis succeeded and <paramref name="destination"/> was large enough.</returns>
        internal bool TryWriteCanonicalPath(
            ArticlePathMode mode,
            ReadOnlySpan<byte> organizationalTrackerHost,
            Span<byte> destination,
            out int bytesWritten)
            => ArticlePathCanonicalizer.TryWriteCanonicalPath(
                OriginalPathValue.Span,
                _localIdentity.Span,
                PathKind,
                ContainsOrganizationalTracker,
                mode,
                organizationalTrackerHost,
                destination,
                out bytesWritten);

        /// <summary>
        /// Creates a rejected parse result while preserving already parsed slices and metadata.
        /// </summary>
        internal static NntpArticleParseResult Rejected(
            NntpArticleParseFailureCode failureCode,
            NntpArticleType articleType,
            ReadOnlyMemory<byte> articleBytes,
            ReadOnlyMemory<byte> headerBytes,
            ReadOnlyMemory<byte> bodyBytes,
            ReadOnlySpan<NntpArticleHeaderEntry> headers,
            int headerCount,
            ReadOnlyMemory<byte> localIdentity,
            DateParseFailureReason dateFailureReason = DateParseFailureReason.None,
            DateTime canonicalUtc = default,
            ReadOnlyMemory<byte> originalDateValue = default,
            NntpArticleHeaderName selectedDateHeaderName = NntpArticleHeaderName.Unknown,
            ArticlePathKind pathKind = ArticlePathKind.Missing,
            bool containsOrganizationalTracker = false,
            ReadOnlyMemory<byte> originalPathValue = default,
            ReadOnlyMemory<byte> originalMessageIdValue = default,
            bool yEncDetected = false,
            YEncArticleValidationResult yEncValidation = default,
            int bodyLineCount = 0)
            => new(
                isAccepted: false,
                failureCode,
                articleType,
                articleBytes,
                headerBytes,
                bodyBytes,
                headers,
                headerCount,
                dateFailureReason,
                canonicalUtc,
                originalDateValue,
                selectedDateHeaderName,
                pathKind,
                containsOrganizationalTracker,
                originalPathValue,
                originalMessageIdValue,
                yEncDetected,
                localIdentity,
                yEncValidation,
                bodyLineCount);
    }

    /// <summary>
    /// Allocation-free list view over parsed header entries stored on <see cref="NntpArticleParseResult"/>.
    /// </summary>
    internal readonly struct NntpArticleHeaderList : IReadOnlyList<NntpArticleHeaderEntry>
    {
        /// <summary>Parse result whose <c>HeaderCount</c> and <c>GetHeader</c> supply this list. Headers are not copied.</summary>
        private readonly NntpArticleParseResult _result;

        /// <summary>
        /// Wraps <paramref name="result"/> as an <see cref="IReadOnlyList{T}"/> without copying header entries.
        /// </summary>
        /// <param name="result">Parse result that owns the header table.</param>
        internal NntpArticleHeaderList(NntpArticleParseResult result)
        {
            _result = result;
        }

        /// <inheritdoc />
        public int Count => _result.HeaderCount;

        /// <inheritdoc />
        public NntpArticleHeaderEntry this[int index] => _result.GetHeader(index);

        /// <inheritdoc />
        public IEnumerator<NntpArticleHeaderEntry> GetEnumerator()
        {
            for (var i = 0; i < _result.HeaderCount; i++)
            {
                yield return _result.GetHeader(i);
            }
        }

        /// <summary>
        /// Returns the generic enumerator, which yields headers in stored order.
        /// </summary>
        /// <returns>An <see cref="IEnumerator"/> over <see cref="NntpArticleHeaderEntry"/> values from the wrapped parse result.</returns>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

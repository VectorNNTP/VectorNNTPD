using System.Buffers;
using System.Text;
using VectorNNTPD.Common.Articles.DateParser;
using VectorNNTPD.Common.Articles.Validation;
using VectorNNTPD.Common.Articles.YEnc;

namespace VectorNNTPD.Common.Articles.Parsing
{
    /// <summary>
    /// Parses untrusted NNTP article bytes into a compact structured result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Input bytes are expected to be transport-normalized article bytes received after acquisition
    /// removes NNTP terminator framing and performs transport dot-unstuffing exactly once.
    /// </para>
    /// <para>
    /// Header, body, and header-value slices in the returned result alias the caller-provided buffer
    /// and therefore inherit its lifetime.
    /// </para>
    /// <para>
    /// The parser does not download, retry, queue, publish, or rewrite the article.
    /// When a yEnc marker is detected in the configured scan window, <see cref="YEncArticleValidator"/>
    /// validates the body in place. Invalid yEnc is reported as <see cref="NntpArticleParseFailureCode.YEncDecodingFailed"/>.
    /// </para>
    /// </remarks>
    public sealed class NntpArticleParser
    {
        /// <summary>Maximum accepted Newsgroups value length.</summary>
        private const int MaxNewsgroupsLength = 4096;

        /// <summary>Maximum accepted From value length.</summary>
        private const int MaxFromLength = 2048;

        /// <summary>Inline header slots before an array-pool buffer is rented. Equal to <see cref="NntpArticleHeaderInlineStore.Capacity"/>.</summary>
        private const int InlineHeaderCapacity = NntpArticleHeaderInlineStore.Capacity;

        /// <summary>Case-sensitive body-line prefix <c>=ybegin </c>, including the trailing SP, used only for yEnc detection.</summary>
        private static ReadOnlySpan<byte> YEncBeginMarker => "=ybegin "u8;

        /// <summary>ASCII substring <c>multipart/</c> sought in Content-Type values. Matching is case-insensitive.</summary>
        private static ReadOnlySpan<byte> MultipartMarker => "multipart/"u8;

        /// <summary>ASCII substring <c>base64</c> sought in Content-Transfer-Encoding values. Matching is case-insensitive.</summary>
        private static ReadOnlySpan<byte> Base64Encoding => "base64"u8;

        /// <summary>ASCII substring <c>binary</c> sought in Content-Transfer-Encoding values. Matching is case-insensitive.</summary>
        private static ReadOnlySpan<byte> BinaryEncoding => "binary"u8;

        /// <summary>
        /// Local FQDN bytes copied at construction. Used for Path classification and canonical Path writes. Lifetime is this instance.
        /// </summary>
        private readonly byte[] _localIdentity;

        /// <summary>
        /// Parse limits after <see cref="CapOptions"/> clamps article and header-line ceilings to <see cref="ArticleResourceLimits"/>.
        /// </summary>
        private readonly NntpArticleParserOptions _options;

        /// <summary>
        /// Initializes a new parser instance with immutable local identity and parse limits.
        /// </summary>
        /// <param name="localIdentity">Local FQDN used for Path rewrite classification and canonical Path writes.</param>
        /// <param name="options">Parser guardrail options.</param>
        internal NntpArticleParser(ReadOnlySpan<byte> localIdentity, NntpArticleParserOptions options)
        {
            if (localIdentity.IsEmpty || IsAllAsciiWhitespace(localIdentity))
            {
                throw new ArgumentException("Local identity must be a non-empty FQDN.", nameof(localIdentity));
            }

            _localIdentity = localIdentity.ToArray();
            _options = CapOptions(options);
        }

        /// <summary>
        /// Initializes a new parser instance with default parser limits.
        /// </summary>
        /// <param name="localIdentity">Local FQDN used for Path rewrite classification and canonical Path writes.</param>
        internal NntpArticleParser(ReadOnlySpan<byte> localIdentity)
            : this(localIdentity, NntpArticleParserOptions.Default)
        {
        }

        /// <summary>
        /// Initializes a new parser instance from an ASCII local identity string.
        /// </summary>
        /// <param name="localIdentity">Local FQDN used for Path rewrite classification and canonical Path writes.</param>
        /// <param name="options">Parser guardrail options.</param>
        internal NntpArticleParser(string localIdentity, NntpArticleParserOptions options)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(localIdentity);
            _localIdentity = Encoding.ASCII.GetBytes(localIdentity.Trim());
            _options = CapOptions(options);
        }

        /// <summary>
        /// Initializes a new parser instance from an ASCII local identity string and default limits.
        /// </summary>
        /// <param name="localIdentity">Local FQDN used for Path rewrite classification and canonical Path writes.</param>
        public NntpArticleParser(string localIdentity)
            : this(localIdentity, NntpArticleParserOptions.Default)
        {
        }

        /// <summary>
        /// Parses one NNTP article payload and returns deterministic acceptance, rejection, and classification metadata.
        /// </summary>
        /// <param name="articleBytes">Complete article bytes after acquisition has removed ARTICLE framing and transport dot-stuffing.</param>
        /// <returns>
        /// A parse result that preserves slices into the original buffer, validates required headers,
        /// resolves Date, classifies Path rewrite, classifies content type, and validates detected yEnc sections.
        /// </returns>
        internal NntpArticleParseResult Parse(ReadOnlyMemory<byte> articleBytes)
            => Parse(articleBytes, _options.MaxArticleBytes, ArticlePathCanonicalizer.OrganizationalTrackerHost);

        /// <summary>
        /// Parses one article using one captured article-size limit and Path tracker.
        /// </summary>
        /// <param name="articleBytes">Complete article bytes after destuffing.</param>
        /// <param name="maxArticleBytes">Article-size boundary for this operation. Not clamped to a fixed ceiling.</param>
        /// <param name="organizationalTrackerHost">Path tracker component for this operation.</param>
        /// <returns>The parse result.</returns>
        internal NntpArticleParseResult Parse(
            ReadOnlyMemory<byte> articleBytes,
            int maxArticleBytes,
            ReadOnlySpan<byte> organizationalTrackerHost)
        {
            var localIdentity = (ReadOnlyMemory<byte>)_localIdentity;
            if (articleBytes.IsEmpty)
            {
                return NntpArticleParseResult.Rejected(
                    NntpArticleParseFailureCode.EmptyArticle,
                    NntpArticleType.Malformed,
                    articleBytes,
                    ReadOnlyMemory<byte>.Empty,
                    ReadOnlyMemory<byte>.Empty,
                    [],
                    0,
                    localIdentity);
            }

            if (articleBytes.Length > maxArticleBytes)
            {
                return NntpArticleParseResult.Rejected(
                    NntpArticleParseFailureCode.ArticleTooLarge,
                    NntpArticleType.Malformed,
                    articleBytes,
                    ReadOnlyMemory<byte>.Empty,
                    ReadOnlyMemory<byte>.Empty,
                    [],
                    0,
                    localIdentity);
            }

            var articleSpan = articleBytes.Span;
            Span<NntpArticleHeaderEntry> inlineHeaders = stackalloc NntpArticleHeaderEntry[InlineHeaderCapacity];
            NntpArticleHeaderEntry[]? rentedHeaders = null;
            try
            {
                var headerOutcome = TryParseHeaders(articleSpan, articleBytes, _options, inlineHeaders, ref rentedHeaders);
                var parsedHeaders = headerOutcome.Headers;
                if (!headerOutcome.Success)
                {
                    return NntpArticleParseResult.Rejected(
                        headerOutcome.FailureCode,
                        NntpArticleType.Malformed,
                        articleBytes,
                        headerOutcome.HeaderBytes,
                        headerOutcome.BodyBytes,
                        parsedHeaders,
                        parsedHeaders.Length,
                        localIdentity);
                }

                if (!TryValidateBodyLineLengths(headerOutcome.BodyBytes.Span, _options.MaxHeaderLineBytes, out var bodyLineCount, out var bodyLineFailure))
                {
                    return NntpArticleParseResult.Rejected(
                        bodyLineFailure,
                        NntpArticleType.Malformed,
                        articleBytes,
                        headerOutcome.HeaderBytes,
                        headerOutcome.BodyBytes,
                        parsedHeaders,
                        parsedHeaders.Length,
                        localIdentity);
                }

                if (!TryValidateMessageId(articleSpan, parsedHeaders, articleBytes, out var originalMessageIdValue, out var messageIdFailure))
                {
                    return NntpArticleParseResult.Rejected(
                        messageIdFailure,
                        NntpArticleType.Malformed,
                        articleBytes,
                        headerOutcome.HeaderBytes,
                        headerOutcome.BodyBytes,
                        parsedHeaders,
                        parsedHeaders.Length,
                        localIdentity,
                        originalMessageIdValue: originalMessageIdValue);
                }

                if (!TryValidateNewsgroups(articleSpan, parsedHeaders, out var newsgroupsFailure))
                {
                    return NntpArticleParseResult.Rejected(
                        newsgroupsFailure,
                        NntpArticleType.Malformed,
                        articleBytes,
                        headerOutcome.HeaderBytes,
                        headerOutcome.BodyBytes,
                        parsedHeaders,
                        parsedHeaders.Length,
                        localIdentity);
                }

                if (!TryValidateFrom(articleSpan, parsedHeaders, out var fromFailure))
                {
                    return NntpArticleParseResult.Rejected(
                        fromFailure,
                        NntpArticleType.Malformed,
                        articleBytes,
                        headerOutcome.HeaderBytes,
                        headerOutcome.BodyBytes,
                        parsedHeaders,
                        parsedHeaders.Length,
                        localIdentity);
                }

                if (HasDuplicateDateHeader(parsedHeaders))
                {
                    return NntpArticleParseResult.Rejected(
                        NntpArticleParseFailureCode.DuplicateDate,
                        NntpArticleType.Malformed,
                        articleBytes,
                        headerOutcome.HeaderBytes,
                        headerOutcome.BodyBytes,
                        parsedHeaders,
                        parsedHeaders.Length,
                        localIdentity,
                        originalMessageIdValue: originalMessageIdValue);
                }

                if (!ArticleDateHeaderResolver.TryResolve(
                        articleBytes,
                        parsedHeaders,
                        out var canonicalUtc,
                        out var originalDateValue,
                        out var selectedDateHeaderName,
                        out var dateFailure))
                {
                    return NntpArticleParseResult.Rejected(
                        NntpArticleParseFailureCode.MissingOrInvalidDate,
                        NntpArticleType.Malformed,
                        articleBytes,
                        headerOutcome.HeaderBytes,
                        headerOutcome.BodyBytes,
                        parsedHeaders,
                        parsedHeaders.Length,
                        localIdentity,
                        dateFailureReason: dateFailure,
                        originalMessageIdValue: originalMessageIdValue);
                }

                if (!TryAnalyzePath(articleSpan, parsedHeaders, articleBytes, localIdentity.Span, organizationalTrackerHost, out var pathKind, out var containsOrganizationalTracker, out var originalPathValue, out var pathFailure))
                {
                    return NntpArticleParseResult.Rejected(
                        pathFailure,
                        NntpArticleType.Malformed,
                        articleBytes,
                        headerOutcome.HeaderBytes,
                        headerOutcome.BodyBytes,
                        parsedHeaders,
                        parsedHeaders.Length,
                        localIdentity,
                        dateFailureReason: DateParseFailureReason.None,
                        canonicalUtc: canonicalUtc,
                        originalDateValue: originalDateValue,
                        selectedDateHeaderName: selectedDateHeaderName,
                        originalMessageIdValue: originalMessageIdValue);
                }

                var yEncDetected = DetectYEnc(headerOutcome.BodyBytes.Span, _options.YEncDetectionScanBytes);
                var articleType = ClassifyArticle(articleSpan, parsedHeaders, headerOutcome.BodyBytes.Span, yEncDetected);
                var yEncValidation = YEncArticleValidationResult.ValidNonYEnc();
                if (yEncDetected)
                {
                    yEncValidation = YEncArticleValidator.Validate(headerOutcome.BodyBytes.Span);
                    if (!yEncValidation.IsValid)
                    {
                        return NntpArticleParseResult.Rejected(
                            NntpArticleParseFailureCode.YEncDecodingFailed,
                            NntpArticleType.YEnc,
                            articleBytes,
                            headerOutcome.HeaderBytes,
                            headerOutcome.BodyBytes,
                            parsedHeaders,
                            parsedHeaders.Length,
                            localIdentity,
                            dateFailureReason: DateParseFailureReason.None,
                            canonicalUtc: canonicalUtc,
                            originalDateValue: originalDateValue,
                            selectedDateHeaderName: selectedDateHeaderName,
                            pathKind: pathKind,
                            containsOrganizationalTracker: containsOrganizationalTracker,
                            originalPathValue: originalPathValue,
                            originalMessageIdValue: originalMessageIdValue,
                            yEncDetected: true,
                            yEncValidation: yEncValidation);
                    }
                }

                return new NntpArticleParseResult(
                    isAccepted: true,
                    NntpArticleParseFailureCode.None,
                    articleType,
                    articleBytes,
                    headerOutcome.HeaderBytes,
                    headerOutcome.BodyBytes,
                    parsedHeaders,
                    parsedHeaders.Length,
                    DateParseFailureReason.None,
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
            finally
            {
                if (rentedHeaders is not null)
                {
                    ArrayPool<NntpArticleHeaderEntry>.Shared.Return(rentedHeaders, clearArray: true);
                }
            }
        }

        /// <summary>
        /// Clamps <see cref="NntpArticleParserOptions.MaxHeaderLineBytes"/>
        /// to <see cref="ArticleResourceLimits.MaxArticleLineBytes"/>.
        /// <see cref="NntpArticleParserOptions.MaxArticleBytes"/> is the caller's article-size policy and is not reduced.
        /// </summary>
        /// <param name="options">Caller-supplied limits.</param>
        /// <returns>A copy with the header-line field reduced when it exceeds the line ceiling.</returns>
        private static NntpArticleParserOptions CapOptions(NntpArticleParserOptions options)
            => options with
            {
                MaxHeaderLineBytes = Math.Min(options.MaxHeaderLineBytes, ArticleResourceLimits.MaxArticleLineBytes),
            };

        /// <summary>
        /// Returns whether every byte is SP or HTAB. An empty span is treated as whitespace.
        /// </summary>
        /// <param name="value">Local-identity bytes.</param>
        /// <returns><see langword="false"/> when any byte is not <c>0x20</c> or <c>0x09</c>.</returns>
        private static bool IsAllAsciiWhitespace(ReadOnlySpan<byte> value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] is not ((byte)' ' or (byte)'\t'))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Splits the header section from the body and records each header's name and trimmed value range.
        /// </summary>
        /// <param name="articleSpan">Article bytes used for scanning.</param>
        /// <param name="articleBytes">Same buffer as a memory, so returned slices keep the caller's lifetime.</param>
        /// <param name="options">Capped limits for line length, header count, name length, value length, and header-section size.</param>
        /// <param name="inlineHeaders">Stack or caller buffer of <see cref="InlineHeaderCapacity"/> slots.</param>
        /// <param name="rentedHeaders">Set when the header count exceeds the inline buffer. The caller returns it to the array pool.</param>
        /// <returns>
        /// Success includes the header slice through the blank line and the body after it.
        /// A first line with no colon that is not a continuation is success with an empty header slice and the whole article as the body.
        /// Missing a blank line fails with <see cref="NntpArticleParseFailureCode.MissingHeaderBodySeparator"/>.
        /// Folded lines extend the current value through the continuation line, including the leading whitespace.
        /// Value ranges drop leading and trailing SP and HTAB on the first physical line only.
        /// </returns>
        private static HeaderParseOutcome TryParseHeaders(
            ReadOnlySpan<byte> articleSpan,
            ReadOnlyMemory<byte> articleBytes,
            NntpArticleParserOptions options,
            Span<NntpArticleHeaderEntry> inlineHeaders,
            ref NntpArticleHeaderEntry[]? rentedHeaders)
        {
            var index = 0;
            const int headerStart = 0;
            var headerBytesScanned = 0;
            var headerCount = 0;
            var headers = inlineHeaders;

            var currentHeaderNameOffset = -1;
            var currentHeaderNameLength = 0;
            var currentHeaderValueOffset = -1;
            var currentHeaderValueEndExclusive = -1;
            var currentKnownName = NntpArticleHeaderName.Unknown;
            var currentHeaderHasValue = false;
            var firstLineChecked = false;

            while (index < articleSpan.Length)
            {
                var maxLineLength = options.MaxHeaderLineBytes;
                var lineEnd = FindLineTerminator(articleSpan, index, maxLineLength + 1);
                var lineContentEnd = lineEnd >= 0 ? lineEnd : Math.Min(articleSpan.Length, index + maxLineLength + 1);
                var lineLength = lineContentEnd - index;
                if (lineEnd < 0 && lineLength > maxLineLength)
                {
                    return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.HeaderLineTooLong, articleBytes, headerStart, index + maxLineLength, headers[..headerCount]);
                }

                if (lineLength > maxLineLength)
                {
                    return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.HeaderLineTooLong, articleBytes, headerStart, index + maxLineLength, headers[..headerCount]);
                }

                if (lineLength == 0)
                {
                    if (currentHeaderNameOffset >= 0
                        && !TryAddHeader(
                            ref headers,
                            ref headerCount,
                            ref rentedHeaders,
                            options.MaxHeaderCount,
                            new NntpArticleHeaderEntry(
                                currentKnownName,
                                currentHeaderNameOffset,
                                currentHeaderNameLength,
                                currentHeaderValueOffset,
                                currentHeaderHasValue ? currentHeaderValueEndExclusive - currentHeaderValueOffset : 0)))
                    {
                        return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.TooManyHeaders, articleBytes, headerStart, lineContentEnd, headers[..headerCount]);
                    }

                    var bodyOffset = lineEnd >= 0 ? AdvancePastTerminator(articleSpan, lineEnd) : lineContentEnd;
                    var headerLength = bodyOffset;
                    return headerLength > options.MaxHeaderSectionBytes
                        ? HeaderParseOutcome.Fail(NntpArticleParseFailureCode.HeaderSectionTooLarge, articleBytes, headerStart, bodyOffset, headers[..headerCount])
                        : HeaderParseOutcome.SuccessResult(
                            articleBytes,
                            articleBytes[..headerLength],
                            articleBytes[bodyOffset..],
                            headers[..headerCount]);
                }

                headerBytesScanned += lineLength;
                if (headerBytesScanned > options.MaxHeaderSectionBytes)
                {
                    return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.HeaderSectionTooLarge, articleBytes, headerStart, lineContentEnd, headers[..headerCount]);
                }

                var line = articleSpan.Slice(index, lineLength);
                if (ContainsIllegalHeaderControl(line))
                {
                    return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.ContainsIllegalControlByte, articleBytes, headerStart, lineContentEnd, headers[..headerCount]);
                }

                if (!firstLineChecked)
                {
                    firstLineChecked = true;
                    var firstColon = line.IndexOf((byte)':');
                    if (firstColon < 0)
                    {
                        return line[0] is (byte)' ' or (byte)'\t'
                            ? HeaderParseOutcome.Fail(NntpArticleParseFailureCode.MalformedHeaderContinuation, articleBytes, headerStart, lineContentEnd, headers[..headerCount])
                            : HeaderParseOutcome.SuccessResult(articleBytes, ReadOnlyMemory<byte>.Empty, articleBytes, []);
                    }
                }

                var continuation = line[0] is (byte)' ' or (byte)'\t';
                if (continuation)
                {
                    if (currentHeaderNameOffset < 0)
                    {
                        return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.MalformedHeaderContinuation, articleBytes, headerStart, lineContentEnd, headers[..headerCount]);
                    }

                    currentHeaderHasValue = true;
                    currentHeaderValueEndExclusive = lineContentEnd;
                    var aggregateHeaderValueLength = currentHeaderValueEndExclusive - currentHeaderValueOffset;
                    if (aggregateHeaderValueLength > options.MaxHeaderValueBytes)
                    {
                        return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.HeaderValueTooLong, articleBytes, headerStart, index + maxLineLength, headers[..headerCount]);
                    }

                    index = lineEnd >= 0 ? AdvancePastTerminator(articleSpan, lineEnd) : articleSpan.Length;
                    continue;
                }

                if (currentHeaderNameOffset >= 0)
                {
                    if (!TryAddHeader(
                            ref headers,
                            ref headerCount,
                            ref rentedHeaders,
                            options.MaxHeaderCount,
                            new NntpArticleHeaderEntry(
                                currentKnownName,
                                currentHeaderNameOffset,
                                currentHeaderNameLength,
                                currentHeaderValueOffset,
                                currentHeaderHasValue ? currentHeaderValueEndExclusive - currentHeaderValueOffset : 0)))
                    {
                        return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.TooManyHeaders, articleBytes, headerStart, lineContentEnd, headers[..headerCount]);
                    }
                }

                if (headerCount >= options.MaxHeaderCount)
                {
                    return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.TooManyHeaders, articleBytes, headerStart, lineContentEnd, headers[..headerCount]);
                }

                var colonIndex = line.IndexOf((byte)':');
                if (colonIndex < 0)
                {
                    return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.MissingHeaderBodySeparator, articleBytes, headerStart, lineContentEnd, headers[..headerCount]);
                }

                if (colonIndex == 0)
                {
                    return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.MalformedHeader, articleBytes, headerStart, lineContentEnd, headers[..headerCount]);
                }

                if (colonIndex > options.MaxHeaderNameBytes)
                {
                    return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.HeaderNameTooLong, articleBytes, headerStart, lineContentEnd, headers[..headerCount]);
                }

                var valueStartInLine = colonIndex + 1;
                while (valueStartInLine < line.Length && line[valueStartInLine] is (byte)' ' or (byte)'\t')
                {
                    valueStartInLine++;
                }

                var valueEndInLine = line.Length;
                while (valueEndInLine > valueStartInLine && line[valueEndInLine - 1] is (byte)' ' or (byte)'\t')
                {
                    valueEndInLine--;
                }

                var headerValueLength = valueEndInLine - valueStartInLine;
                if (headerValueLength > options.MaxHeaderValueBytes)
                {
                    return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.HeaderValueTooLong, articleBytes, headerStart, lineContentEnd, headers[..headerCount]);
                }

                currentHeaderNameOffset = index;
                currentHeaderNameLength = colonIndex;
                currentHeaderValueOffset = index + valueStartInLine;
                currentHeaderValueEndExclusive = index + valueEndInLine;
                currentHeaderHasValue = headerValueLength > 0;
                currentKnownName = ClassifyKnownHeaderName(articleSpan.Slice(currentHeaderNameOffset, currentHeaderNameLength));
                index = lineEnd >= 0 ? AdvancePastTerminator(articleSpan, lineEnd) : articleSpan.Length;
            }

            return HeaderParseOutcome.Fail(
                NntpArticleParseFailureCode.MissingHeaderBodySeparator,
                articleBytes,
                headerStart,
                articleBytes.Length,
                headers[..headerCount]);
        }

        /// <summary>
        /// Appends one header, renting an array-pool buffer of <paramref name="maxHeaderCount"/> when the inline span is full.
        /// </summary>
        /// <param name="headers">Current header span. Replaced with the rented buffer when growth is required.</param>
        /// <param name="headerCount">Live count. Incremented on success.</param>
        /// <param name="rentedHeaders">Previous rented buffer, returned to the pool when a new one is rented.</param>
        /// <param name="maxHeaderCount">Hard cap. The rented array is this long.</param>
        /// <param name="entry">Header to store.</param>
        /// <returns><see langword="false"/> when <paramref name="headerCount"/> is already at <paramref name="maxHeaderCount"/>.</returns>
        private static bool TryAddHeader(
            ref Span<NntpArticleHeaderEntry> headers,
            ref int headerCount,
            ref NntpArticleHeaderEntry[]? rentedHeaders,
            int maxHeaderCount,
            NntpArticleHeaderEntry entry)
        {
            if (headerCount >= maxHeaderCount)
            {
                return false;
            }

            if (headerCount >= headers.Length)
            {
                var rented = ArrayPool<NntpArticleHeaderEntry>.Shared.Rent(maxHeaderCount);
                headers.CopyTo(rented);
                if (rentedHeaders is not null)
                {
                    ArrayPool<NntpArticleHeaderEntry>.Shared.Return(rentedHeaders, clearArray: true);
                }

                rentedHeaders = rented;
                headers = rented;
            }

            headers[headerCount++] = entry;
            return true;
        }

        /// <summary>
        /// Maps a header name to a parser identity using ASCII case folding.
        /// </summary>
        /// <param name="nameBytes">Name bytes before the colon.</param>
        /// <returns>
        /// One of Date, Injection-Date, NNTP-Posting-Date, Posted, X-Date, Delivery-Date, Path, Message-ID,
        /// Newsgroups, From, Subject, Content-Type, Content-Transfer-Encoding, or References.
        /// Every other name is <see cref="NntpArticleHeaderName.Unknown"/>.
        /// </returns>
        private static NntpArticleHeaderName ClassifyKnownHeaderName(ReadOnlySpan<byte> nameBytes)
            => AsciiEqualsIgnoreCase(nameBytes, "Date"u8)
                ? NntpArticleHeaderName.Date
                : AsciiEqualsIgnoreCase(nameBytes, "Injection-Date"u8)
                    ? NntpArticleHeaderName.InjectionDate
                    : AsciiEqualsIgnoreCase(nameBytes, "NNTP-Posting-Date"u8)
                        ? NntpArticleHeaderName.NntpPostingDate
                        : AsciiEqualsIgnoreCase(nameBytes, "Posted"u8)
                            ? NntpArticleHeaderName.Posted
                            : AsciiEqualsIgnoreCase(nameBytes, "X-Date"u8)
                                ? NntpArticleHeaderName.XDate
                                : AsciiEqualsIgnoreCase(nameBytes, "Delivery-Date"u8)
                                    ? NntpArticleHeaderName.DeliveryDate
                                    : AsciiEqualsIgnoreCase(nameBytes, "Path"u8)
                                        ? NntpArticleHeaderName.Path
                                        : AsciiEqualsIgnoreCase(nameBytes, "Message-ID"u8)
                                            ? NntpArticleHeaderName.MessageId
                                            : AsciiEqualsIgnoreCase(nameBytes, "Newsgroups"u8)
                                                ? NntpArticleHeaderName.Newsgroups
                                                : AsciiEqualsIgnoreCase(nameBytes, "From"u8)
                                                    ? NntpArticleHeaderName.From
                                                    : AsciiEqualsIgnoreCase(nameBytes, "Subject"u8)
                                                        ? NntpArticleHeaderName.Subject
                                                        : AsciiEqualsIgnoreCase(nameBytes, "Content-Type"u8)
                                                            ? NntpArticleHeaderName.ContentType
                                                            : AsciiEqualsIgnoreCase(nameBytes, "Content-Transfer-Encoding"u8)
                                                                ? NntpArticleHeaderName.ContentTransferEncoding
                                                                : AsciiEqualsIgnoreCase(nameBytes, "References"u8)
                                                                    ? NntpArticleHeaderName.References
                                                                    : NntpArticleHeaderName.Unknown;

        /// <summary>
        /// Reports a second <c>Date</c> header. Folded continuation lines belong to the current header and are not a second header.
        /// </summary>
        /// <param name="headers">Parsed headers in wire order. <see cref="NntpArticleHeaderName.Date"/> is already case-folded.</param>
        /// <returns><see langword="true"/> when two or more entries have <see cref="NntpArticleHeaderName.Date"/>.</returns>
        private static bool HasDuplicateDateHeader(ReadOnlySpan<NntpArticleHeaderEntry> headers)
        {
            var found = false;
            for (var i = 0; i < headers.Length; i++)
            {
                if (headers[i].KnownName != NntpArticleHeaderName.Date)
                {
                    continue;
                }

                if (found)
                {
                    return true;
                }

                found = true;
            }

            return false;
        }

        /// <summary>
        /// Requires exactly one Message-ID whose unfolded bytes pass <see cref="NntpMessageIdValidation.IsValidMessageId(ReadOnlySpan{byte})"/>.
        /// Unfolding is header parsing. Validity is only the Common Message-ID contract.
        /// </summary>
        /// <param name="articleSpan">Article bytes.</param>
        /// <param name="headers">Parsed headers in wire order.</param>
        /// <param name="articleBytes">Buffer that owns the returned slice.</param>
        /// <param name="messageIdBytes">Original value slice, including any folded bytes, when validation succeeds. Not the unfolded form.</param>
        /// <param name="failureCode">
        /// <see cref="NntpArticleParseFailureCode.DuplicateMessageId"/>,
        /// <see cref="NntpArticleParseFailureCode.MissingMessageId"/>,
        /// <see cref="NntpArticleParseFailureCode.InvalidMessageId"/> when unfolding fails or the canonical validator rejects the value,
        /// or <see cref="NntpArticleParseFailureCode.None"/>.
        /// </param>
        /// <returns><see langword="true"/> only for a single valid Message-ID. Whitespace is not stripped before validation.</returns>
        private static bool TryValidateMessageId(
            ReadOnlySpan<byte> articleSpan,
            ReadOnlySpan<NntpArticleHeaderEntry> headers,
            ReadOnlyMemory<byte> articleBytes,
            out ReadOnlyMemory<byte> messageIdBytes,
            out NntpArticleParseFailureCode failureCode)
        {
            messageIdBytes = default;
            failureCode = NntpArticleParseFailureCode.None;

            var messageIdHeader = default(NntpArticleHeaderEntry);
            var found = false;
            for (var i = 0; i < headers.Length; i++)
            {
                var entry = headers[i];
                if (entry.KnownName != NntpArticleHeaderName.MessageId)
                {
                    continue;
                }

                if (found)
                {
                    failureCode = NntpArticleParseFailureCode.DuplicateMessageId;
                    return false;
                }

                found = true;
                messageIdHeader = entry;
            }

            if (!found)
            {
                failureCode = NntpArticleParseFailureCode.MissingMessageId;
                return false;
            }

            var rawValue = articleSpan.Slice(messageIdHeader.ValueOffset, messageIdHeader.ValueLength);
            Span<byte> unfolded = stackalloc byte[NntpMessageIdValidation.MaxMessageIdLength];
            if (!NntpArticleHeaderValueUnfolder.TryUnfold(rawValue, unfolded, out var unfoldedLength))
            {
                failureCode = NntpArticleParseFailureCode.InvalidMessageId;
                return false;
            }

            if (!NntpMessageIdValidation.IsValidMessageId(unfolded[..unfoldedLength]))
            {
                failureCode = NntpArticleParseFailureCode.InvalidMessageId;
                return false;
            }

            messageIdBytes = articleBytes.Slice(messageIdHeader.ValueOffset, messageIdHeader.ValueLength);
            return true;
        }

        /// <summary>
        /// Requires exactly one Newsgroups value of length 1 through <see cref="MaxNewsgroupsLength"/>.
        /// </summary>
        /// <param name="articleSpan">Article bytes.</param>
        /// <param name="headers">Parsed headers in wire order.</param>
        /// <param name="failureCode">
        /// <see cref="NntpArticleParseFailureCode.DuplicateNewsgroups"/>,
        /// <see cref="NntpArticleParseFailureCode.MissingNewsgroups"/>,
        /// <see cref="NntpArticleParseFailureCode.InvalidNewsgroups"/>, or <see cref="NntpArticleParseFailureCode.None"/>.
        /// </param>
        /// <returns>
        /// <see langword="false"/> for an empty token (leading, trailing, or doubled comma), a byte other than SP, HTAB, comma,
        /// or <see cref="IsPlausibleNewsgroupChar"/>, or a value whose last token has no plausible character.
        /// SP and HTAB are skipped and do not separate tokens.
        /// </returns>
        private static bool TryValidateNewsgroups(
            ReadOnlySpan<byte> articleSpan,
            ReadOnlySpan<NntpArticleHeaderEntry> headers,
            out NntpArticleParseFailureCode failureCode)
        {
            failureCode = NntpArticleParseFailureCode.None;
            var newsgroupsHeader = default(NntpArticleHeaderEntry);
            var found = false;
            for (var i = 0; i < headers.Length; i++)
            {
                var entry = headers[i];
                if (entry.KnownName != NntpArticleHeaderName.Newsgroups)
                {
                    continue;
                }

                if (found)
                {
                    failureCode = NntpArticleParseFailureCode.DuplicateNewsgroups;
                    return false;
                }

                found = true;
                newsgroupsHeader = entry;
            }

            if (!found)
            {
                failureCode = NntpArticleParseFailureCode.MissingNewsgroups;
                return false;
            }

            if (newsgroupsHeader.ValueLength is 0 or > MaxNewsgroupsLength)
            {
                failureCode = NntpArticleParseFailureCode.InvalidNewsgroups;
                return false;
            }

            var value = articleSpan.Slice(newsgroupsHeader.ValueOffset, newsgroupsHeader.ValueLength);
            var tokenHasChar = false;
            for (var i = 0; i < value.Length; i++)
            {
                var b = value[i];
                if (b == (byte)',')
                {
                    if (!tokenHasChar)
                    {
                        failureCode = NntpArticleParseFailureCode.InvalidNewsgroups;
                        return false;
                    }

                    tokenHasChar = false;
                    continue;
                }

                if (b is (byte)' ' or (byte)'\t')
                {
                    continue;
                }

                if (!IsPlausibleNewsgroupChar(b))
                {
                    failureCode = NntpArticleParseFailureCode.InvalidNewsgroups;
                    return false;
                }

                tokenHasChar = true;
            }

            if (!tokenHasChar)
            {
                failureCode = NntpArticleParseFailureCode.InvalidNewsgroups;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Checks every From header. Absence is success. Duplicates are each checked and are not themselves a failure.
        /// </summary>
        /// <param name="articleSpan">Article bytes.</param>
        /// <param name="headers">Parsed headers in wire order.</param>
        /// <param name="failureCode"><see cref="NntpArticleParseFailureCode.InvalidFrom"/> or <see cref="NntpArticleParseFailureCode.None"/>.</param>
        /// <returns>
        /// <see langword="false"/> when a From value is empty, does not unfold into <see cref="MaxFromLength"/>,
        /// has no <c>@</c> with bytes on both sides, contains a byte outside <c>0x20..0x7E</c>, or is only SP and HTAB.
        /// </returns>
        private static bool TryValidateFrom(
            ReadOnlySpan<byte> articleSpan,
            ReadOnlySpan<NntpArticleHeaderEntry> headers,
            out NntpArticleParseFailureCode failureCode)
        {
            failureCode = NntpArticleParseFailureCode.None;
            Span<byte> unfoldedFromBuffer = stackalloc byte[MaxFromLength];
            for (var i = 0; i < headers.Length; i++)
            {
                var entry = headers[i];
                if (entry.KnownName != NntpArticleHeaderName.From)
                {
                    continue;
                }

                if (entry.ValueLength == 0)
                {
                    failureCode = NntpArticleParseFailureCode.InvalidFrom;
                    return false;
                }

                var rawValue = articleSpan.Slice(entry.ValueOffset, entry.ValueLength);
                if (!NntpArticleHeaderValueUnfolder.TryUnfold(rawValue, unfoldedFromBuffer, out var unfoldedLength))
                {
                    failureCode = NntpArticleParseFailureCode.InvalidFrom;
                    return false;
                }

                var value = unfoldedFromBuffer[..unfoldedLength];
                var at = value.IndexOf((byte)'@');
                if (at <= 0 || at >= value.Length - 1)
                {
                    failureCode = NntpArticleParseFailureCode.InvalidFrom;
                    return false;
                }

                var hasPrintable = false;
                for (var c = 0; c < value.Length; c++)
                {
                    var b = value[c];
                    if (b is < 0x20 or > 0x7E)
                    {
                        failureCode = NntpArticleParseFailureCode.InvalidFrom;
                        return false;
                    }

                    if (b is not ((byte)' ' or (byte)'\t'))
                    {
                        hasPrintable = true;
                    }
                }

                if (!hasPrintable)
                {
                    failureCode = NntpArticleParseFailureCode.InvalidFrom;
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Requires at most one Path header and classifies it with path analysis.
        /// </summary>
        /// <param name="articleSpan">Article bytes.</param>
        /// <param name="headers">Parsed headers in wire order.</param>
        /// <param name="articleBytes">Buffer that owns <paramref name="originalPathValue"/>.</param>
        /// <param name="localIdentity">Parser FQDN bytes.</param>
        /// <param name="organizationalTrackerHost">Path tracker component for this article.</param>
        /// <param name="pathKind">Rewrite classification. <see cref="ArticlePathKind.Missing"/> when no Path header exists.</param>
        /// <param name="containsOrganizationalTracker">Whether a Path token equals the organizational tracker host.</param>
        /// <param name="originalPathValue">Original Path value slice when a header exists; otherwise empty.</param>
        /// <param name="failureCode"><see cref="NntpArticleParseFailureCode.DuplicatePath"/> or the code returned by path analysis.</param>
        /// <returns><see langword="false"/> for a second Path header or when path analysis rejects the value. A missing Path is success.</returns>
        private static bool TryAnalyzePath(
            ReadOnlySpan<byte> articleSpan,
            ReadOnlySpan<NntpArticleHeaderEntry> headers,
            ReadOnlyMemory<byte> articleBytes,
            ReadOnlySpan<byte> localIdentity,
            ReadOnlySpan<byte> organizationalTrackerHost,
            out ArticlePathKind pathKind,
            out bool containsOrganizationalTracker,
            out ReadOnlyMemory<byte> originalPathValue,
            out NntpArticleParseFailureCode failureCode)
        {
            pathKind = ArticlePathKind.Missing;
            containsOrganizationalTracker = false;
            originalPathValue = default;
            failureCode = NntpArticleParseFailureCode.None;

            var pathHeader = default(NntpArticleHeaderEntry);
            var found = false;
            for (var i = 0; i < headers.Length; i++)
            {
                var entry = headers[i];
                if (entry.KnownName != NntpArticleHeaderName.Path)
                {
                    continue;
                }

                if (found)
                {
                    failureCode = NntpArticleParseFailureCode.DuplicatePath;
                    return false;
                }

                found = true;
                pathHeader = entry;
            }

            var rawPath = found
                ? articleSpan.Slice(pathHeader.ValueOffset, pathHeader.ValueLength)
                : [];
            if (!ArticlePathCanonicalizer.TryAnalyze(rawPath, localIdentity, found, organizationalTrackerHost, out pathKind, out containsOrganizationalTracker, out failureCode))
            {
                return false;
            }

            if (found)
            {
                originalPathValue = articleBytes.Slice(pathHeader.ValueOffset, pathHeader.ValueLength);
            }

            return true;
        }

        /// <summary>
        /// Picks one article type. yEnc detection wins, then a Content-Type containing <c>multipart/</c>,
        /// then a Content-Transfer-Encoding containing <c>base64</c> or <c>binary</c>, then the text heuristic.
        /// </summary>
        /// <param name="articleSpan">Article bytes used to slice header values.</param>
        /// <param name="headers">Parsed headers. Substring checks are ASCII case-insensitive.</param>
        /// <param name="body">Body bytes passed to <see cref="IsLikelyTextBody"/> only when no earlier class matches.</param>
        /// <param name="yEncDetected">Result of <see cref="DetectYEnc"/>.</param>
        /// <returns>
        /// <see cref="NntpArticleType.YEnc"/>, <see cref="NntpArticleType.MimeMultipart"/>, <see cref="NntpArticleType.BinaryEncoded"/>,
        /// <see cref="NntpArticleType.Text"/>, or <see cref="NntpArticleType.Unknown"/>. Flags are not combined.
        /// </returns>
        private static NntpArticleType ClassifyArticle(
            ReadOnlySpan<byte> articleSpan,
            ReadOnlySpan<NntpArticleHeaderEntry> headers,
            ReadOnlySpan<byte> body,
            bool yEncDetected)
        {
            var mimeMultipart = false;
            var binaryTransferEncoding = false;
            for (var i = 0; i < headers.Length; i++)
            {
                var header = headers[i];
                if (header.KnownName == NntpArticleHeaderName.ContentType)
                {
                    var value = articleSpan.Slice(header.ValueOffset, header.ValueLength);
                    if (IndexOfAsciiIgnoreCase(value, MultipartMarker) >= 0)
                    {
                        mimeMultipart = true;
                    }
                }
                else if (header.KnownName == NntpArticleHeaderName.ContentTransferEncoding)
                {
                    var value = articleSpan.Slice(header.ValueOffset, header.ValueLength);
                    if (IndexOfAsciiIgnoreCase(value, Base64Encoding) >= 0 || IndexOfAsciiIgnoreCase(value, BinaryEncoding) >= 0)
                    {
                        binaryTransferEncoding = true;
                    }
                }
            }

            return yEncDetected
                ? NntpArticleType.YEnc
                : mimeMultipart
                    ? NntpArticleType.MimeMultipart
                    : binaryTransferEncoding
                        ? NntpArticleType.BinaryEncoded
                        : IsLikelyTextBody(body) ? NntpArticleType.Text : NntpArticleType.Unknown;
        }

        /// <summary>
        /// Counts body lines and rejects a line whose content exceeds <paramref name="maxLineBytes"/>.
        /// </summary>
        /// <param name="body">Body bytes after the header separator.</param>
        /// <param name="maxLineBytes">Maximum content bytes before CR or LF.</param>
        /// <param name="lineCount">
        /// Lines seen. An empty body is 0. Each CR, LF, or CRLF ends one line, and a final unterminated fragment counts as one line.
        /// </param>
        /// <param name="failureCode"><see cref="NntpArticleParseFailureCode.BodyLineTooLong"/> or <see cref="NntpArticleParseFailureCode.None"/>.</param>
        /// <returns><see langword="false"/> only when a line is too long. The count then includes that line.</returns>
        private static bool TryValidateBodyLineLengths(
            ReadOnlySpan<byte> body,
            int maxLineBytes,
            out int lineCount,
            out NntpArticleParseFailureCode failureCode)
        {
            failureCode = NntpArticleParseFailureCode.None;
            lineCount = 0;
            var position = 0;
            while (position < body.Length)
            {
                var maxScanBytes = Math.Min(maxLineBytes + 1, body.Length - position);
                var relativeEnd = body.Slice(position, maxScanBytes).IndexOfAny((byte)'\r', (byte)'\n');
                var lineEnd = relativeEnd >= 0 ? position + relativeEnd : -1;

                var lineContentEnd = lineEnd >= 0 ? lineEnd : position + maxScanBytes;
                var lineLength = lineContentEnd - position;
                if (lineLength > maxLineBytes)
                {
                    failureCode = NntpArticleParseFailureCode.BodyLineTooLong;
                    return false;
                }

                lineCount++;
                if (lineEnd < 0)
                {
                    return true;
                }

                position = AdvancePastTerminator(body, lineEnd);
            }

            return true;
        }

        /// <summary>
        /// Returns whether any line in the first <paramref name="maxScanBytes"/> of <paramref name="body"/> starts with <see cref="YEncBeginMarker"/>.
        /// </summary>
        /// <param name="body">Body bytes.</param>
        /// <param name="maxScanBytes">Scan window. Bytes past it are ignored.</param>
        /// <returns><see langword="true"/> on a case-sensitive <c>=ybegin </c> prefix. A NUL ends the current scan window as if the line had no terminator.</returns>
        private static bool DetectYEnc(ReadOnlySpan<byte> body, int maxScanBytes)
        {
            var scanLength = Math.Min(body.Length, maxScanBytes);
            var position = 0;
            while (position < scanLength)
            {
                var lineEnd = FindLineTerminator(body, position, scanLength - position);
                var lineContentEnd = lineEnd >= 0 ? lineEnd : scanLength;
                var line = body[position..lineContentEnd];
                if (line.StartsWith(YEncBeginMarker))
                {
                    return true;
                }

                if (lineEnd < 0)
                {
                    break;
                }

                position = AdvancePastTerminator(body, lineEnd);
            }

            return false;
        }

        /// <summary>
        /// Classifies the first 4096 body bytes as text unless a NUL or too many other controls appear.
        /// </summary>
        /// <param name="body">Body bytes. An empty body is text.</param>
        /// <returns>
        /// <see langword="false"/> when a NUL is present. Bytes below <c>0x09</c>, and bytes from <c>0x0E</c> through <c>0x1F</c>, are suspicious.
        /// HT, LF, VT, FF, and CR are not. A sample shorter than 16 bytes requires zero suspicious bytes; a longer sample requires fewer than one sixteenth suspicious.
        /// </returns>
        private static bool IsLikelyTextBody(ReadOnlySpan<byte> body)
        {
            var sampleLength = Math.Min(body.Length, 4096);
            if (sampleLength == 0)
            {
                return true;
            }

            var suspicious = 0;
            for (var i = 0; i < sampleLength; i++)
            {
                var b = body[i];
                if (b == 0)
                {
                    return false;
                }

                if (b is < 0x09 or (> 0x0D and < 0x20))
                {
                    suspicious++;
                }
            }

            return sampleLength < 16 ? suspicious == 0 : suspicious < (sampleLength / 16);
        }

        /// <summary>
        /// Finds the next CR or LF, stopping after <paramref name="maximumScanBytes"/> or at a NUL.
        /// </summary>
        /// <param name="buffer">Article or body bytes.</param>
        /// <param name="start">Inclusive search start.</param>
        /// <param name="maximumScanBytes">Maximum bytes examined. <see cref="int.MaxValue"/> scans through the buffer.</param>
        /// <returns>
        /// Index of the first CR or LF, <c>-2</c> when a NUL is seen first, or <c>-1</c> when the window ends without either.
        /// </returns>
        private static int FindLineTerminator(ReadOnlySpan<byte> buffer, int start, int maximumScanBytes = int.MaxValue)
        {
            var boundedScanBytes = Math.Max(0, maximumScanBytes);
            var endExclusive = boundedScanBytes == int.MaxValue
                ? buffer.Length
                : Math.Min(buffer.Length, start + boundedScanBytes);

            for (var i = start; i < endExclusive; i++)
            {
                var b = buffer[i];
                if (b == (byte)'\r' || b == (byte)'\n')
                {
                    return i;
                }

                if (b == 0)
                {
                    return -2;
                }
            }

            return -1;
        }

        /// <summary>
        /// Advances past a CR, LF, or CRLF at <paramref name="lineTerminatorIndex"/>.
        /// </summary>
        /// <param name="buffer">Article or body bytes.</param>
        /// <param name="lineTerminatorIndex">Index from <see cref="FindLineTerminator"/>, including the negative sentinels.</param>
        /// <returns>
        /// Two bytes past a CRLF, one byte past a lone CR or LF, or <c>buffer.Length</c> when the index is outside the buffer.
        /// A <c>-2</c> NUL sentinel therefore jumps to the end.
        /// </returns>
        private static int AdvancePastTerminator(ReadOnlySpan<byte> buffer, int lineTerminatorIndex)
            => lineTerminatorIndex < 0 || lineTerminatorIndex >= buffer.Length
                ? buffer.Length
                : buffer[lineTerminatorIndex] == (byte)'\r'
                  && lineTerminatorIndex + 1 < buffer.Length
                  && buffer[lineTerminatorIndex + 1] == (byte)'\n'
                    ? lineTerminatorIndex + 2
                    : lineTerminatorIndex + 1;

        /// <summary>
        /// Returns whether a header line contains NUL or a control byte other than HTAB.
        /// </summary>
        /// <param name="line">One physical header line without its terminator. CR and LF are not included.</param>
        /// <returns><see langword="true"/> for <c>0x00</c> or any byte below <c>0x20</c> except <c>0x09</c>. Bytes above <c>0x7F</c> are allowed here.</returns>
        private static bool ContainsIllegalHeaderControl(ReadOnlySpan<byte> line)
        {
            for (var i = 0; i < line.Length; i++)
            {
                var b = line[i];
                if (b == 0)
                {
                    return true;
                }

                if (b is < 0x20 and not ((byte)'\t'))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Returns whether <paramref name="b"/> may appear in a newsgroup token.</summary>
        /// <param name="b">Candidate byte.</param>
        /// <returns><see langword="true"/> for ASCII letters, digits, <c>.</c>, <c>-</c>, <c>_</c>, and <c>+</c> only.</returns>
        private static bool IsPlausibleNewsgroupChar(byte b)
            => b is (>= ((byte)'a') and <= ((byte)'z'))
                or (>= ((byte)'A') and <= ((byte)'Z'))
                or (>= ((byte)'0') and <= ((byte)'9'))
                or (byte)'.' or (byte)'-' or (byte)'_' or (byte)'+';

        /// <summary>
        /// Compares header names after folding ASCII <c>A-Z</c> to <c>a-z</c>. Other bytes are compared unchanged.
        /// </summary>
        /// <param name="left">Left name.</param>
        /// <param name="right">Right name.</param>
        /// <returns><see langword="false"/> when the lengths differ or any folded byte differs.</returns>
        private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (var i = 0; i < left.Length; i++)
            {
                if (ToLowerAscii(left[i]) != ToLowerAscii(right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Finds <paramref name="needle"/> in <paramref name="haystack"/> after folding ASCII <c>A-Z</c> to <c>a-z</c>.
        /// </summary>
        /// <param name="haystack">Header value bytes.</param>
        /// <param name="needle">Marker such as <c>multipart/</c>. An empty needle matches at index 0.</param>
        /// <returns>Index of the first match, or <c>-1</c> when the needle is longer than the haystack or does not occur.</returns>
        private static int IndexOfAsciiIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
        {
            if (needle.Length == 0)
            {
                return 0;
            }

            if (needle.Length > haystack.Length)
            {
                return -1;
            }

            var max = haystack.Length - needle.Length;
            for (var i = 0; i <= max; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (ToLowerAscii(haystack[i + j]) != ToLowerAscii(needle[j]))
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Folds ASCII <c>A-Z</c> to <c>a-z</c>. Every other byte is returned unchanged.</summary>
        /// <param name="value">Byte to fold.</param>
        /// <returns>The folded byte.</returns>
        private static byte ToLowerAscii(byte value)
            => (uint)(value - (byte)'A') <= 'Z' - 'A' ? (byte)(value + 32) : value;

        /// <summary>
        /// Header-split result held as a <see langword="ref"/> struct so <see cref="Headers"/> can be a span over the inline or rented buffer.
        /// </summary>
        private readonly ref struct HeaderParseOutcome
        {
            /// <summary>
            /// Stores one header-split outcome. Slices alias <paramref name="articleBytes"/>.
            /// </summary>
            /// <param name="success"><see langword="true"/> when a header/body split was accepted.</param>
            /// <param name="failureCode"><see cref="NntpArticleParseFailureCode.None"/> on success.</param>
            /// <param name="articleBytes">Original article buffer.</param>
            /// <param name="headerBytes">Header slice, empty when the article is treated as body-only.</param>
            /// <param name="bodyBytes">Body slice.</param>
            /// <param name="headers">Parsed header entries. Empty on failure paths that have not stored any, and on the body-only success path.</param>
            private HeaderParseOutcome(
                bool success,
                NntpArticleParseFailureCode failureCode,
                ReadOnlyMemory<byte> articleBytes,
                ReadOnlyMemory<byte> headerBytes,
                ReadOnlyMemory<byte> bodyBytes,
                ReadOnlySpan<NntpArticleHeaderEntry> headers)
            {
                Success = success;
                FailureCode = failureCode;
                ArticleBytes = articleBytes;
                HeaderBytes = headerBytes;
                BodyBytes = bodyBytes;
                Headers = headers;
            }

            /// <summary><see langword="true"/> when the header section was split from the body.</summary>
            internal bool Success { get; }

            /// <summary>Rejection code when <see cref="Success"/> is false; otherwise <see cref="NntpArticleParseFailureCode.None"/>.</summary>
            internal NntpArticleParseFailureCode FailureCode { get; }

            /// <summary>Original article buffer supplied to the split. Retained so failure slices stay inside it.</summary>
            private ReadOnlyMemory<byte> ArticleBytes { get; }

            /// <summary>Header bytes as a slice of the original article, including the blank line on success.</summary>
            internal ReadOnlyMemory<byte> HeaderBytes { get; }

            /// <summary>Body bytes as a slice of the original article, or the whole article on the body-only success path.</summary>
            internal ReadOnlyMemory<byte> BodyBytes { get; }

            /// <summary>Parsed headers in wire order. The span is valid only while the inline or rented buffer lives.</summary>
            internal ReadOnlySpan<NntpArticleHeaderEntry> Headers { get; }

            /// <summary>
            /// Builds a successful split. Header and body slices alias <paramref name="articleBytes"/>.
            /// </summary>
            /// <param name="articleBytes">Original article buffer.</param>
            /// <param name="headerBytes">Header slice.</param>
            /// <param name="bodyBytes">Body slice.</param>
            /// <param name="headers">Headers stored before the blank line.</param>
            /// <returns>An outcome with <see cref="Success"/> set and <see cref="NntpArticleParseFailureCode.None"/>.</returns>
            internal static HeaderParseOutcome SuccessResult(
                ReadOnlyMemory<byte> articleBytes,
                ReadOnlyMemory<byte> headerBytes,
                ReadOnlyMemory<byte> bodyBytes,
                ReadOnlySpan<NntpArticleHeaderEntry> headers)
                => new(true, NntpArticleParseFailureCode.None, articleBytes, headerBytes, bodyBytes, headers);

            /// <summary>
            /// Builds a failed split. The body starts at <paramref name="bodyOffset"/> clamped into <paramref name="article"/>.
            /// </summary>
            /// <param name="failureCode">Why the header section was rejected.</param>
            /// <param name="article">Original article buffer.</param>
            /// <param name="headerOffset">Start of the header slice. Callers pass 0.</param>
            /// <param name="bodyOffset">Proposed body start. Negative becomes 0; past the end becomes the article length.</param>
            /// <param name="headers">Headers accepted before the failure.</param>
            /// <returns>An outcome with <see cref="Success"/> clear. Header bytes are <c>article[headerOffset..clampedBody]</c>.</returns>
            internal static HeaderParseOutcome Fail(
                NntpArticleParseFailureCode failureCode,
                ReadOnlyMemory<byte> article,
                int headerOffset,
                int bodyOffset,
                ReadOnlySpan<NntpArticleHeaderEntry> headers)
            {
                var boundedBodyOffset = bodyOffset < 0 ? 0 : bodyOffset > article.Length ? article.Length : bodyOffset;
                return new HeaderParseOutcome(
                    false,
                    failureCode,
                    article,
                    article[headerOffset..boundedBodyOffset],
                    article[boundedBodyOffset..],
                    headers);
            }
        }
    }
}

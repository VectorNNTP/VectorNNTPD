using System.Buffers;
using System.IO.Hashing;
using VectorNNTPD.Common.Articles.DateParser;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles.Processing;
using VectorNNTPD.Common.Articles.Validation;
using VectorNNTPD.Common.Transport.ArticleTransfer;

namespace VectorNNTPD.Common.Articles
{
    /// <summary>
    /// Builds an <see cref="ArticleRecord"/> from destuffed article bytes or from a validated
    /// canonical transfer (META + stuffed ArtData).
    /// </summary>
    /// <remarks>
    /// Uses the existing Common parser, Diablo <see cref="ArticleTypeClassifier"/>, and
    /// Date/Path materializer. The materialized ArtData is headers, the separator, and a body
    /// with one stuffing dot on each line whose first content byte is <c>.</c>. The NNTP terminator
    /// is not stored. ArtHash is one-shot XXH3-64 of the logical stuffed bytes
    /// (<c>XxHash3.HashToUInt64</c>), not an incremental hash and not IEEE CRC-32.
    /// Article Work rents the canonical array from <see cref="ArrayPool{T}.Shared"/>.
    /// A null pool still allocates an exact-size array. Hash and field ranges use the logical length.
    /// The destuffed source buffer is not retained. FieldTable
    /// ranges are located on the materialized ArtData so Date/Path length changes cannot
    /// leave offsets pointing at the discarded source. Stuffing inserts bytes only in the body,
    /// so header ranges stay on the header bytes.
    /// This factory has no request Message-ID parameter and does not match an IHAVE/ARTICLE
    /// request. Request matching remains an ingestion/orchestration responsibility.
    /// NNTPD TAKETHIS and POST construct the record before queue admission.
    /// <see cref="TryCreateFromCanonicalTransfer"/> reconstructs a CanonicalV1 record from
    /// transferred META + ArtData without destuff, parse, or rematerialization.
    /// </remarks>
    public static class ArticleRecordFactory
    {
        /// <summary>
        /// Parses and materializes an article, recording a receiving-system Path hop.
        /// </summary>
        /// <param name="parser">Common article parser. Its local identity is the application FQDN.</param>
        /// <param name="destuffedArticle">Destuffed article bytes. Not retained after success. The returned ArtData is the stuffed form.</param>
        /// <returns>Accepted record referencing the stuffed ArtData buffer, or a failure classification.</returns>
        public static ArticleRecordCreateResult TryCreate(
            NntpArticleParser parser,
            ReadOnlyMemory<byte> destuffedArticle)
            => TryCreate(parser, destuffedArticle, ArticlePathMode.Traverse);

        /// <summary>
        /// Parses, classifies, and materializes destuffed article bytes into a CanonicalV1 record.
        /// </summary>
        /// <param name="parser">Common article parser. Its local identity is the application FQDN used by <see cref="ArticlePathMode.Traverse"/>.</param>
        /// <param name="destuffedArticle">Destuffed article bytes. Not retained after success. The returned ArtData is the stuffed form.</param>
        /// <param name="pathMode"><see cref="ArticlePathMode.Traverse"/> prepends the parser FQDN. <see cref="ArticlePathMode.Normalize"/> does not.</param>
        /// <param name="canonicalBufferPool">
        /// Pool for the canonical array. Null allocates an exact-size array.
        /// </param>
        /// <returns>Accepted record referencing the materialized ArtData buffer, or a failure classification.</returns>
        internal static ArticleRecordCreateResult TryCreate(
            NntpArticleParser parser,
            ReadOnlyMemory<byte> destuffedArticle,
            ArticlePathMode pathMode,
            ArrayPool<byte>? canonicalBufferPool = null)
            => TryCreate(
                parser,
                destuffedArticle,
                pathMode,
                ArticleResourceLimits.MaxArticleBytes,
                ArticlePathCanonicalizer.OrganizationalTrackerHost,
                canonicalBufferPool);

        /// <summary>
        /// Parses and materializes an article with one captured size limit and Path tracker.
        /// </summary>
        /// <param name="parser">Common article parser. Its local identity is the application FQDN.</param>
        /// <param name="destuffedArticle">Destuffed article bytes. Not retained after success. The returned ArtData is the stuffed form.</param>
        /// <param name="pathMode"><see cref="ArticlePathMode.Traverse"/> prepends the parser FQDN.</param>
        /// <param name="maxArticleBytes">Article-size boundary captured for this operation.</param>
        /// <param name="organizationalTrackerHost">Path tracker captured with <paramref name="maxArticleBytes"/>.</param>
        /// <param name="canonicalBufferPool">
        /// Pool for the canonical array. Null allocates an exact-size array.
        /// Article Work passes <see cref="ArrayPool{T}.Shared"/>.
        /// </param>
        /// <returns>Accepted record, or a failure classification.</returns>
        internal static ArticleRecordCreateResult TryCreate(
            NntpArticleParser parser,
            ReadOnlyMemory<byte> destuffedArticle,
            ArticlePathMode pathMode,
            int maxArticleBytes,
            ReadOnlySpan<byte> organizationalTrackerHost,
            ArrayPool<byte>? canonicalBufferPool = null)
        {
            ArgumentNullException.ThrowIfNull(parser);

            var parse = parser.Parse(destuffedArticle, maxArticleBytes, organizationalTrackerHost);
            if (!parse.IsAccepted)
            {
                return ArticleRecordCreateResult.RejectedParse(parse.FailureCode);
            }

            var materialize = NntpArticleCanonicalMaterializer.Materialize(
                in parse,
                pathMode,
                maxArticleBytes,
                organizationalTrackerHost,
                canonicalBufferPool);
            if (!materialize.IsAccepted || materialize.ArticleBytes is null)
            {
                return ArticleRecordCreateResult.RejectedMaterialize(materialize.FailureCode);
            }

            var artData = materialize.ArticleBytes;
            var length = materialize.CanonicalLength;
            CanonicalArticleBuffer? buffer = null;
            try
            {
                var logical = artData.AsSpan(0, length);
                var fields = ArticleFieldTable.Locate(logical, parse.SelectedDateHeaderName);
                var artId = ArticleId.FromMessageId(fields.MessageId.Slice(logical));
                var artHash = XxHash3.HashToUInt64(logical);
                var artType = ArticleTypeClassifier.Classify(parse.HeaderBytes.Span, parse.BodyBytes.Span);
                buffer = canonicalBufferPool is null
                    ? CanonicalArticleBuffer.Exact(artData)
                    : CanonicalArticleBuffer.Rented(artData, length, canonicalBufferPool);
                var record = new ArticleRecord(
                    artId,
                    artHash,
                    artType,
                    parse.BodyLineCount,
                    parse.CanonicalUtc,
                    ArticleParseStatus.CanonicalV1,
                    buffer,
                    fields);
                buffer = null;
                return ArticleRecordCreateResult.Accepted(record, parse.SelectedDateHeaderName);
            }
            catch
            {
                if (buffer is not null)
                {
                    buffer.ReleaseCreator();
                }
                else if (canonicalBufferPool is not null)
                {
                    canonicalBufferPool.Return(artData, clearArray: true);
                }

                throw;
            }
        }

        /// <summary>
        /// Reconstructs a CanonicalV1 <see cref="ArticleRecord"/> from transferred META and ArtData
        /// without destuffing, reparsing, or rematerializing.
        /// </summary>
        /// <param name="artData">Receiver-owned ArtData buffer assembled from DATA frames. Not copied.</param>
        /// <param name="meta">Decoded META describing <paramref name="artData"/>.</param>
        /// <param name="expectedArtId">ArticleId from OPEN (not from META).</param>
        /// <returns>
        /// Accepted CanonicalV1 record referencing <paramref name="artData"/>, or a transfer
        /// rejection via <see cref="ArticleRecordCreateResult.MaterializeFailure"/>.
        /// </returns>
        /// <remarks>
        /// Validates ArtSize, FieldTable bounds, RFC 5536 Message-ID syntax on the raw value
        /// octets, Message-ID → ArtId binding, ArtHash, Locate equality, CanonicalUtc from the
        /// Date range, and ArtLines sanity. Recomputes ArtType
        /// via <see cref="ArticleTypeClassifier"/> (header + ≤8 KiB body prefix). Does not
        /// recount body lines.
        /// </remarks>
        internal static ArticleRecordCreateResult TryCreateFromCanonicalTransfer(
            byte[] artData,
            in ArticleCanonicalTransferMeta meta,
            in ArticleId expectedArtId)
        {
            if (artData is null || artData.Length == 0)
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferNullArtData);
            }

            if (meta.ArtSize != artData.Length
                || meta.ArtSize < 1
                || meta.ArtSize > ArticleResourceLimits.MaxArticleBytes)
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferArtSizeMismatch);
            }

            if (!TryValidateFieldTable(meta.Fields, meta.ArtSize))
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferInvalidFieldRange);
            }

            if (!meta.Fields.MessageId.IsPresent || meta.Fields.MessageId.Length == 0)
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferMissingMessageId);
            }

            var messageId = meta.Fields.MessageId.Slice(artData);
            if (!NntpMessageIdValidation.IsValidMessageId(messageId))
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferMissingMessageId);
            }

            var artId = ArticleId.FromMessageId(messageId);
            if (artId != expectedArtId)
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferArticleIdMismatch);
            }

            var artHash = XxHash3.HashToUInt64(artData);
            if (artHash != meta.ArtHash)
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferArtHashMismatch);
            }

            if (!IsDateFamilyHeader(meta.SelectedDateHeaderName))
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferInvalidSelectedDateHeader);
            }

            var located = ArticleFieldTable.Locate(artData, meta.SelectedDateHeaderName);
            if (located != meta.Fields)
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferFieldTableMismatch);
            }

            if (!meta.Fields.Date.IsPresent || meta.Fields.Date.Length == 0)
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferInvalidDate);
            }

            if (!NewsDateParser.TryGetCanonicalUtc(meta.Fields.Date.Slice(artData), out var canonicalUtc, out _)
                || canonicalUtc == default
                || canonicalUtc.Kind != DateTimeKind.Utc)
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferInvalidDate);
            }

            if (meta.ArtLines < 0 || meta.ArtLines > meta.ArtSize)
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferInvalidArtLines);
            }

            SplitHeadersAndBody(artData, out var headers, out var body);
            var artType = ArticleTypeClassifier.Classify(headers, body);

            var record = new ArticleRecord(
                artId,
                artHash,
                artType,
                meta.ArtLines,
                canonicalUtc,
                ArticleParseStatus.CanonicalV1,
                artData,
                meta.Fields);
            return ArticleRecordCreateResult.Accepted(record, meta.SelectedDateHeaderName);
        }

        /// <summary>
        /// Returns whether <paramref name="name"/> is Date, Injection-Date, NNTP-Posting-Date, Posted, X-Date, or Delivery-Date.
        /// </summary>
        /// <param name="name">Classified header name.</param>
        /// <returns><see langword="true"/> for those six identities only.</returns>
        private static bool IsDateFamilyHeader(NntpArticleHeaderName name) =>
            name is NntpArticleHeaderName.Date
                or NntpArticleHeaderName.InjectionDate
                or NntpArticleHeaderName.NntpPostingDate
                or NntpArticleHeaderName.Posted
                or NntpArticleHeaderName.XDate
                or NntpArticleHeaderName.DeliveryDate;

        /// <summary>
        /// Requires every field range to be absent or fully inside <c>[0, artSize)</c>.
        /// </summary>
        /// <param name="fields">Header ranges proposed for the record.</param>
        /// <param name="artSize">Length of the canonical article buffer.</param>
        /// <returns><see langword="false"/> when any range fails <see cref="TryValidateRange"/>.</returns>
        private static bool TryValidateFieldTable(in ArticleFieldTable fields, int artSize)
        {
            return TryValidateRange(fields.MessageId, artSize)
                && TryValidateRange(fields.Newsgroups, artSize)
                && TryValidateRange(fields.Subject, artSize)
                && TryValidateRange(fields.From, artSize)
                && TryValidateRange(fields.Date, artSize)
                && TryValidateRange(fields.References, artSize)
                && TryValidateRange(fields.Path, artSize);
        }

        /// <summary>
        /// Accepts an absent range (<see cref="ArticleByteRange.Offset"/> <c>-1</c> and length 0) or a present range that fits in the article.
        /// </summary>
        /// <param name="range">One header-value range.</param>
        /// <param name="artSize">Length of the canonical article buffer.</param>
        /// <returns>
        /// <see langword="false"/> when a present range has a negative offset or length, or when
        /// <c>offset + length</c> exceeds <paramref name="artSize"/> using a 64-bit sum.
        /// An absent range with any other offset or length is also rejected.
        /// </returns>
        private static bool TryValidateRange(ArticleByteRange range, int artSize)
        {
            if (!range.IsPresent)
            {
                return range.Offset == -1 && range.Length == 0;
            }

            if (range.Offset < 0 || range.Length < 0)
            {
                return false;
            }

            return (long)range.Offset + range.Length <= artSize;
        }

        /// <summary>
        /// Splits on the first CRLF CRLF. The separator stays with the header slice.
        /// </summary>
        /// <param name="artData">Canonical article bytes.</param>
        /// <param name="headers">Bytes through the blank-line separator, or all of <paramref name="artData"/> when CRLF CRLF is absent.</param>
        /// <param name="body">Bytes after the separator, or empty when the separator is absent. Lone LF separators are not recognized.</param>
        private static void SplitHeadersAndBody(
            ReadOnlySpan<byte> artData,
            out ReadOnlySpan<byte> headers,
            out ReadOnlySpan<byte> body)
        {
            var separator = "\r\n\r\n"u8;
            var at = artData.IndexOf(separator);
            if (at < 0)
            {
                headers = artData;
                body = [];
                return;
            }

            headers = artData[..(at + separator.Length)];
            body = artData[(at + separator.Length)..];
        }
    }

    /// <summary>
    /// Result of one article-record creation attempt.
    /// </summary>
    public readonly struct ArticleRecordCreateResult
    {
        /// <summary>
        /// Stores the acceptance flag, record, and failure codes exactly as the factory methods pass them.
        /// </summary>
        /// <param name="isAccepted"><see langword="true"/> only for <see cref="Accepted"/>.</param>
        /// <param name="record">Canonical record when accepted; otherwise <see langword="default"/>.</param>
        /// <param name="parseFailure">Parser failure, or <see cref="NntpArticleParseFailureCode.None"/> when parse succeeded.</param>
        /// <param name="materializeFailure">Materializer or transfer failure, or <see cref="NntpArticleCanonicalFailureCode.None"/> when that stage succeeded.</param>
        /// <param name="selectedDateHeaderName">Winning Date-family name, or <see cref="NntpArticleHeaderName.Unknown"/> when not accepted.</param>
        private ArticleRecordCreateResult(
            bool isAccepted,
            ArticleRecord record,
            NntpArticleParseFailureCode parseFailure,
            NntpArticleCanonicalFailureCode materializeFailure,
            NntpArticleHeaderName selectedDateHeaderName)
        {
            IsAccepted = isAccepted;
            Record = record;
            ParseFailure = parseFailure;
            MaterializeFailure = materializeFailure;
            SelectedDateHeaderName = selectedDateHeaderName;
        }

        /// <summary>Gets a value indicating whether a CanonicalV1 record was produced.</summary>
        public bool IsAccepted { get; }

        /// <summary>Gets the constructed record when accepted; otherwise default.</summary>
        public ArticleRecord Record { get; }

        /// <summary>Gets the parser failure when construction stopped at parse.</summary>
        public NntpArticleParseFailureCode ParseFailure { get; }

        /// <summary>Gets the materializer or transfer failure when construction stopped after parse.</summary>
        public NntpArticleCanonicalFailureCode MaterializeFailure { get; }

        /// <summary>
        /// Gets the Date-family header name selected during parse (for VATP META).
        /// <see cref="NntpArticleHeaderName.Unknown"/> when not accepted.
        /// </summary>
        internal NntpArticleHeaderName SelectedDateHeaderName { get; }

        /// <summary>Creates an accepted result that carries <paramref name="record"/> (no ArtData copy).</summary>
        internal static ArticleRecordCreateResult Accepted(
            ArticleRecord record,
            NntpArticleHeaderName selectedDateHeaderName = NntpArticleHeaderName.Unknown)
            => new(true, record, NntpArticleParseFailureCode.None, NntpArticleCanonicalFailureCode.None, selectedDateHeaderName);

        /// <summary>Creates a parse-rejected result with no ArtData.</summary>
        internal static ArticleRecordCreateResult RejectedParse(NntpArticleParseFailureCode parseFailure)
            => new(false, default, parseFailure, NntpArticleCanonicalFailureCode.None, NntpArticleHeaderName.Unknown);

        /// <summary>Creates a materialize-rejected result with no ArtData.</summary>
        internal static ArticleRecordCreateResult RejectedMaterialize(NntpArticleCanonicalFailureCode materializeFailure)
            => new(false, default, NntpArticleParseFailureCode.None, materializeFailure, NntpArticleHeaderName.Unknown);
    }
}

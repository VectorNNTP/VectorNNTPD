namespace VectorNNTPD.Common.Articles
{
    /// <summary>
    /// Canonical in-memory Vector article after one-time parse, classification, and materialization.
    /// </summary>
    /// <remarks>
    /// References the canonical article buffer produced by materialization. FieldTable ranges
    /// refer to that same buffer. The buffer lifetime is managed by the producer/consumer
    /// workflow; copying this struct shares the reference and does not copy ArtData.
    /// Overview <c>:bytes</c> is <see cref="ArtSize"/>; <c>:lines</c> is <see cref="ArtLines"/>.
    /// Newsgroups is the <see cref="ArticleFieldTable.Newsgroups"/> range (no allocated group
    /// list). This type is not a serialization protocol.
    /// </remarks>
    public readonly struct ArticleRecord
    {
        /// <summary>
        /// Canonical stuffed article buffer. Field ranges index these bytes. The struct stores the reference and does not copy the array.
        /// </summary>
        private readonly byte[] _artData;

        /// <summary>
        /// Initializes a record that references <paramref name="artData"/> without copying it.
        /// </summary>
        /// <param name="artId">BLAKE3 of the Message-ID header <em>value</em> bytes in <paramref name="artData"/>.</param>
        /// <param name="artHash">XXH3-64 fingerprint of the entire <paramref name="artData"/> buffer.</param>
        /// <param name="artType">Diablo-derived classification flags.</param>
        /// <param name="artLines">Body line count for overview <c>:lines</c>.</param>
        /// <param name="canonicalUtc">Winning Date-family header as UTC.</param>
        /// <param name="parseStatus">Parse/validation state of this record, not of any request.</param>
        /// <param name="artData">Canonical stuffed article bytes whose lifetime is managed by the caller workflow. The record does not copy this array.</param>
        /// <param name="fields">Header value ranges into <paramref name="artData"/>.</param>
        internal ArticleRecord(
            ArticleId artId,
            ulong artHash,
            ArticleType artType,
            int artLines,
            DateTime canonicalUtc,
            ArticleParseStatus parseStatus,
            byte[] artData,
            ArticleFieldTable fields)
        {
            ArtId = artId;
            ArtHash = artHash;
            ArtType = artType;
            ArtLines = artLines;
            CanonicalUtc = canonicalUtc;
            ParseStatus = parseStatus;
            _artData = artData;
            Fields = fields;
        }

        /// <summary>Gets the internal article identity BLAKE3(Message-ID value bytes).</summary>
        internal ArticleId ArtId { get; }

        /// <summary>
        /// Gets the XXH3-64 fingerprint of <see cref="ArtData"/>.
        /// </summary>
        /// <remarks>
        /// Internal deterministic fingerprint of the final canonical article bytes.
        /// Not cryptographic identity and not proof of byte equality. Distinct from
        /// <see cref="ArtId"/>, which is BLAKE3 of the Message-ID value only.
        /// </remarks>
        internal ulong ArtHash { get; }

        /// <summary>Gets the stuffed retained article size; always <c>ArtData.Length</c>.</summary>
        public int ArtSize => _artData?.Length ?? 0;

        /// <summary>Gets the Diablo-derived article type flags. Not <c>NntpArticleType</c>.</summary>
        internal ArticleType ArtType { get; }

        /// <summary>
        /// Gets the body line count for overview <c>:lines</c>.
        /// Empty body is 0. Each CRLF/CR/LF-terminated line counts as one line; a final
        /// unterminated fragment also counts as one line. Uses the same walk as the parser
        /// body-length check. Body dot-stuffing adds a leading dot and does not add a line,
        /// so the count remains valid for canonical ArtData.
        /// </summary>
        public int ArtLines { get; }

        /// <summary>Gets the winning Date-family header resolved to UTC.</summary>
        internal DateTime CanonicalUtc { get; }

        /// <summary>
        /// Gets the parse/validation state of this record.
        /// <see cref="ArticleParseStatus.CanonicalV1"/> does not imply request Message-ID matching.
        /// </summary>
        public ArticleParseStatus ParseStatus { get; }

        /// <summary>
        /// Gets the canonical retained article bytes: unstuffed headers, the header/body separator,
        /// and a body whose lines that begin with <c>.</c> already have one stuffing dot. The NNTP terminator is not included.
        /// </summary>
        public ReadOnlyMemory<byte> ArtData => _artData;

        /// <summary>Gets header value ranges into <see cref="ArtData"/>.</summary>
        public ArticleFieldTable Fields { get; }

        /// <summary>Gets the Message-ID header value bytes in ArtData.</summary>
        internal ReadOnlySpan<byte> MessageId => Fields.MessageId.Slice(_artData);

        /// <summary>Gets the Newsgroups header value bytes in ArtData (ArtGroups).</summary>
        internal ReadOnlySpan<byte> Newsgroups => Fields.Newsgroups.Slice(_artData);

        /// <summary>Gets the Subject header value bytes in ArtData.</summary>
        internal ReadOnlySpan<byte> Subject => Fields.Subject.Slice(_artData);

        /// <summary>Gets the From header value bytes in ArtData.</summary>
        internal ReadOnlySpan<byte> From => Fields.From.Slice(_artData);

        /// <summary>Gets the winning Date-family header value bytes in ArtData.</summary>
        internal ReadOnlySpan<byte> Date => Fields.Date.Slice(_artData);

        /// <summary>Gets the References header value bytes in ArtData.</summary>
        internal ReadOnlySpan<byte> References => Fields.References.Slice(_artData);

        /// <summary>Gets the Path header value bytes in ArtData.</summary>
        internal ReadOnlySpan<byte> Path => Fields.Path.Slice(_artData);
    }
}

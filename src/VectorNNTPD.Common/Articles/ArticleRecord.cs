namespace VectorNNTPD.Common.Articles
{
    /// <summary>
    /// Canonical in-memory Vector article after one-time parse, classification, and materialization.
    /// </summary>
    /// <remarks>
    /// References the canonical article buffer produced by materialization. FieldTable ranges
    /// refer to that same buffer. The buffer lifetime is managed by the producer/consumer
    /// workflow; copying this struct shares the reference and does not copy ArtData.
    /// <see cref="ArtSize"/> is the logical article length. A rented backing array may be longer.
    /// Overview <c>:bytes</c> is <see cref="ArtSize"/>; <c>:lines</c> is <see cref="ArtLines"/>.
    /// Newsgroups is the <see cref="ArticleFieldTable.Newsgroups"/> range (no allocated group
    /// list). This type is not a serialization protocol.
    /// </remarks>
    public readonly struct ArticleRecord
    {
        /// <summary>
        /// Canonical stuffed article buffer. Field ranges index its logical bytes.
        /// The struct stores the reference and does not copy the array.
        /// </summary>
        private readonly CanonicalArticleBuffer? _buffer;

        /// <summary>
        /// Initializes a record that references an exact-size <paramref name="artData"/> array without copying it.
        /// </summary>
        /// <param name="artId">BLAKE3 of the Message-ID header <em>value</em> bytes in <paramref name="artData"/>.</param>
        /// <param name="artHash">XXH3-64 fingerprint of the logical article bytes.</param>
        /// <param name="artType">Diablo-derived classification flags.</param>
        /// <param name="artLines">Body line count for overview <c>:lines</c>.</param>
        /// <param name="canonicalUtc">Winning Date-family header as UTC.</param>
        /// <param name="parseStatus">Parse/validation state of this record, not of any request.</param>
        /// <param name="artData">Exact-size canonical stuffed article bytes. The record does not copy this array.</param>
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
            : this(
                artId,
                artHash,
                artType,
                artLines,
                canonicalUtc,
                parseStatus,
                CanonicalArticleBuffer.Exact(artData),
                fields)
        {
        }

        /// <summary>
        /// Initializes a record that references <paramref name="canonicalBuffer"/> without copying its array.
        /// </summary>
        /// <param name="artId">BLAKE3 of the Message-ID header <em>value</em> bytes.</param>
        /// <param name="artHash">XXH3-64 fingerprint of the logical article bytes.</param>
        /// <param name="artType">Diablo-derived classification flags.</param>
        /// <param name="artLines">Body line count for overview <c>:lines</c>.</param>
        /// <param name="canonicalUtc">Winning Date-family header as UTC.</param>
        /// <param name="parseStatus">Parse/validation state of this record, not of any request.</param>
        /// <param name="canonicalBuffer">Buffer whose logical length is the article size. Not copied.</param>
        /// <param name="fields">Header value ranges into the logical article bytes.</param>
        internal ArticleRecord(
            ArticleId artId,
            ulong artHash,
            ArticleType artType,
            int artLines,
            DateTime canonicalUtc,
            ArticleParseStatus parseStatus,
            CanonicalArticleBuffer? canonicalBuffer,
            ArticleFieldTable fields)
        {
            ArtId = artId;
            ArtHash = artHash;
            ArtType = artType;
            ArtLines = artLines;
            CanonicalUtc = canonicalUtc;
            ParseStatus = parseStatus;
            _buffer = canonicalBuffer;
            Fields = fields;
            Framing = _buffer is null
                ? CanonicalArticleFraming.None
                : CanonicalArticleFraming.Analyze(_buffer.Memory.Span);
        }

        /// <summary>Gets the internal article identity BLAKE3(Message-ID value bytes).</summary>
        internal ArticleId ArtId { get; }

        /// <summary>
        /// Gets the XXH3-64 fingerprint of the logical <see cref="ArtData"/> bytes.
        /// </summary>
        /// <remarks>
        /// Internal deterministic fingerprint of the final canonical article bytes.
        /// Not cryptographic identity and not proof of byte equality. Distinct from
        /// <see cref="ArtId"/>, which is BLAKE3 of the Message-ID value only.
        /// The fingerprint does not cover unused capacity in a rented array.
        /// </remarks>
        internal ulong ArtHash { get; }

        /// <summary>
        /// Gets the logical stuffed article size. This is <see cref="ArtData"/> length while the buffer is live.
        /// A rented array may be longer. The value remains after a pooled buffer is returned.
        /// </summary>
        public int ArtSize => _buffer?.Length ?? 0;

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
        /// Empty after the last consumer releases a pooled buffer.
        /// </summary>
        public ReadOnlyMemory<byte> ArtData => _buffer?.Memory ?? default;

        /// <summary>Gets header value ranges into <see cref="ArtData"/>.</summary>
        public ArticleFieldTable Fields { get; }

        /// <summary>
        /// Gets the framing of the logical <see cref="ArtData"/> bytes at construction.
        /// </summary>
        /// <remarks>
        /// Derived once from <see cref="ArtData"/> as it existed when this record was built.
        /// A rented array's unused capacity is not part of that span. The value is not refreshed
        /// if a caller later writes into the array.
        /// </remarks>
        internal CanonicalArticleFraming Framing { get; }

        /// <summary>Gets the Message-ID header value bytes in ArtData.</summary>
        internal ReadOnlySpan<byte> MessageId => Fields.MessageId.Slice(ArtData.Span);

        /// <summary>Gets the Newsgroups header value bytes in ArtData (ArtGroups).</summary>
        internal ReadOnlySpan<byte> Newsgroups => Fields.Newsgroups.Slice(ArtData.Span);

        /// <summary>Gets the Subject header value bytes in ArtData.</summary>
        internal ReadOnlySpan<byte> Subject => Fields.Subject.Slice(ArtData.Span);

        /// <summary>Gets the From header value bytes in ArtData.</summary>
        internal ReadOnlySpan<byte> From => Fields.From.Slice(ArtData.Span);

        /// <summary>Gets the winning Date-family header value bytes in ArtData.</summary>
        internal ReadOnlySpan<byte> Date => Fields.Date.Slice(ArtData.Span);

        /// <summary>Gets the References header value bytes in ArtData.</summary>
        internal ReadOnlySpan<byte> References => Fields.References.Slice(ArtData.Span);

        /// <summary>Gets the Path header value bytes in ArtData.</summary>
        internal ReadOnlySpan<byte> Path => Fields.Path.Slice(ArtData.Span);

        /// <summary>
        /// Acquires one consumer lease when this record's buffer still has a live owner.
        /// </summary>
        /// <param name="lease">The lease that releases that consumer. Null when this method returns <see langword="false"/>.</param>
        /// <returns>
        /// <see langword="false"/> when this record has no buffer or that buffer has already been returned to its pool.
        /// </returns>
        internal bool TryCreateBufferLease(
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CanonicalArticleBuffer.CanonicalBufferLease? lease)
        {
            if (_buffer is null)
            {
                lease = null;
                return false;
            }

            return _buffer.TryCreateLease(out lease);
        }

        /// <summary>
        /// Drops the creator reference when the caller did not transfer the record into a retained entry,
        /// or after retention has added its own reference. A second call does nothing.
        /// </summary>
        /// <remarks>
        /// This does not release a retention or delivery-claim lease. Those leases dispose themselves.
        /// </remarks>
        internal void ReleaseUnusedCanonicalBuffer() => _buffer?.ReleaseCreator();
    }
}

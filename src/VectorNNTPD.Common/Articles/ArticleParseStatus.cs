namespace VectorNNTPD.Common.Articles
{
    /// <summary>
    /// Minimal parse/validation state for an <see cref="ArticleRecord"/>.
    /// </summary>
    public enum ArticleParseStatus : byte
    {
        /// <summary>This record has not undergone the canonical Vector parse pipeline.</summary>
        None = 0,

        /// <summary>
        /// This record's own pipeline completed: canonical ArtData with body lines dot-stuffed and headers left unstuffed,
        /// Common parser validation, Diablo <c>ArticleType</c> classification,
        /// Date/Path canonicalization, ArtId bound to the Message-ID <em>value</em>,
        /// ArtHash (XXH3-64 of that ArtData), and FieldTable ranges bound to the same buffer.
        /// </summary>
        /// <remarks>
        /// Describes the <see cref="ArticleRecord"/> only. It does not mean an external
        /// IHAVE/ARTICLE request Message-ID was compared. Request matching is an
        /// ingestion/orchestration concern and is not established by
        /// <see cref="ArticleRecordFactory"/>.
        /// </remarks>
        CanonicalV1 = 1,
    }
}

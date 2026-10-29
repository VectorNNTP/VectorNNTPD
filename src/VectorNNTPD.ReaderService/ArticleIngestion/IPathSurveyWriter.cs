namespace VectorNNTPD.ReaderService.ArticleIngestion
{
    /// <summary>
    /// Persists one canonical Path observation to the durable Path-survey stream.
    /// </summary>
    /// <remarks>
    /// Production uses a dedicated Serilog pipeline
    /// (<see cref="SerilogPathSurveyWriter"/>). File lifecycle is Serilog's
    /// responsibility. Implementations must throw on write failure rather than
    /// pretending the observation was recorded. Callers treat this as an
    /// operational survey stream: a failure must not change NNTP responses or
    /// ingestion.
    /// </remarks>
    public interface IPathSurveyWriter
    {
        /// <summary>
        /// Appends one INN-style Path-survey line for <paramref name="canonicalPath"/>.
        /// </summary>
        /// <param name="canonicalPath">
        /// Canonical <c>ArticleRecord.Path</c> value bytes. Empty follows
        /// <see cref="VectorNNTPD.Common.Articles.ArticleRecord.Path"/> semantics
        /// (absent or zero-length field) and still writes a Path line.
        /// </param>
        /// <exception cref="IOException">The line could not be written.</exception>
        void Write(ReadOnlySpan<byte> canonicalPath);

        /// <summary>Flushes buffered Path lines when the implementation buffers independently of Serilog.</summary>
        void Flush();
    }
}

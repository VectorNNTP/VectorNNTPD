namespace VectorNNTPD.ReaderService.ArticleIngestion
{
    /// <summary>No-op <see cref="IPathSurveyWriter"/> for tests that do not assert Path-survey output.</summary>
    public sealed class NullPathSurveyWriter : IPathSurveyWriter
    {
        /// <summary>Gets the shared no-op instance.</summary>
        public static NullPathSurveyWriter Instance { get; } = new();

        /// <inheritdoc />
        public void Write(ReadOnlySpan<byte> canonicalPath)
        {
        }

        /// <inheritdoc />
        public void Flush()
        {
        }
    }
}

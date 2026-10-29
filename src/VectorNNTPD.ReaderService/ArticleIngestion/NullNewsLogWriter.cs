namespace VectorNNTPD.ReaderService.ArticleIngestion
{
    /// <summary>No-op <see cref="INewsLogWriter"/> for tests that do not assert news output.</summary>
    public sealed class NullNewsLogWriter : INewsLogWriter
    {
        /// <summary>Gets the shared no-op instance.</summary>
        public static NullNewsLogWriter Instance { get; } = new();

        /// <inheritdoc />
        public void Write(in NewsLogEvent evt)
        {
        }

        /// <inheritdoc />
        public void Flush()
        {
        }
    }
}

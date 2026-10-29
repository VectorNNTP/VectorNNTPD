namespace VectorNNTPD.TransitService.Logging
{
    /// <summary>
    /// No-op completed-file handler. Path-survey files are still gzip-archived.
    /// Production DI registers <c>NinpathsCompletedFileHandler</c> instead; this
    /// type remains for tests that opt out of ninpaths mail.
    /// </summary>
    public sealed class NullCompletedPathSurveyFileHandler : ICompletedPathSurveyFileHandler
    {
        /// <summary>Gets the shared no-op instance.</summary>
        public static NullCompletedPathSurveyFileHandler Instance { get; } = new();

        /// <inheritdoc />
        public void OnCompletedFile(string completedFilePath)
        {
        }
    }
}

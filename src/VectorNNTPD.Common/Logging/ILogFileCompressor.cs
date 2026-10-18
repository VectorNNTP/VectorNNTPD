namespace VectorNNTPD.Common.Logging
{
    /// <summary>
    /// Replaces one completed uncompressed log with a gzip file.
    /// </summary>
    public interface ILogFileCompressor
    {
        /// <summary>
        /// Writes <c>{sourceLogPath}.gz</c> and deletes <paramref name="sourceLogPath"/> only after that file is in place.
        /// </summary>
        /// <param name="sourceLogPath">Completed <c>.log</c> file. Not the active file.</param>
        /// <returns>
        /// <see langword="true"/> when <c>{sourceLogPath}.gz</c> exists after the attempt, including when that
        /// gzip is complete and the source could not be deleted.
        /// <see langword="false"/> when compression did not finish and the source was kept.
        /// </returns>
        bool TryCompressAndReplace(string sourceLogPath);
    }
}

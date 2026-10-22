namespace VectorNNTPD.StorageService.Storage.SpoolPersistence
{

    /// <summary>
    /// Filesystem operations used to publish one article. Paths passed here are already
    /// constrained to a spool root by <c>SpoolArticlePath.CombineUnderRoot</c>.
    /// </summary>
    internal interface ISpoolArticleFiles
    {
        /// <summary>Returns whether <paramref name="path"/> exists as a file.</summary>
        /// <param name="path">Absolute file path.</param>
        /// <returns><see langword="true"/> when a file is present.</returns>
        bool Exists(string path);

        /// <summary>Returns the length of an existing file.</summary>
        /// <param name="path">Absolute file path.</param>
        /// <returns>The length in bytes.</returns>
        long Length(string path);

        /// <summary>
        /// Compares <paramref name="path"/> to <paramref name="content"/> without allocating a second copy of the content.
        /// </summary>
        /// <param name="path">Existing file.</param>
        /// <param name="content">Bytes the caller would publish.</param>
        /// <returns><see langword="true"/> when the file has the same length and the same bytes.</returns>
        bool ContentsEqual(string path, ReadOnlySpan<byte> content);

        /// <summary>Creates <paramref name="path"/> and any missing parents.</summary>
        /// <param name="path">Absolute directory path.</param>
        void CreateDirectory(string path);

        /// <summary>Deletes <paramref name="path"/> when it exists. A missing file is success.</summary>
        /// <param name="path">Absolute file path.</param>
        void Delete(string path);

        /// <summary>
        /// Creates <paramref name="path"/> and writes <paramref name="content"/>.
        /// The file must not already exist. The write flushes to the operating system and does not
        /// request a device flush. Cancellation after the write starts still leaves a file the caller must delete.
        /// </summary>
        /// <param name="path">Absolute temp path.</param>
        /// <param name="content">Complete article bytes.</param>
        /// <param name="cancellationToken">Observed before the write and before the flush returns to the caller.</param>
        void WriteAll(string path, ReadOnlySpan<byte> content, CancellationToken cancellationToken);

        /// <summary>
        /// Publishes <paramref name="source"/> as <paramref name="destination"/> on the same directory.
        /// An existing destination is replaced. Readers do not observe the temp file's contents.
        /// </summary>
        /// <param name="source">Temp file written by <see cref="WriteAll"/>.</param>
        /// <param name="destination">Final article path.</param>
        void MoveNew(string source, string destination);
    }
}

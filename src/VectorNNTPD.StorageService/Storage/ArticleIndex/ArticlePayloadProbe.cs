namespace VectorNNTPD.StorageService.Storage.ArticleIndex
{

    /// <summary>
    /// Checks that a registered spool root can be listed before its database is loaded.
    /// </summary>
    /// <remarks>
    /// This is one directory probe per spool. It does not stat article files and does not scan article names.
    /// A missing or unreadable root fails startup and does not modify that spool's database.
    /// </remarks>
    internal static class ArticlePayloadProbe
    {
        /// <summary>
        /// Requires <paramref name="spoolDirectory"/> to be a directory this process can enumerate.
        /// </summary>
        /// <param name="spoolDirectory">Registered spool root.</param>
        /// <exception cref="InvalidOperationException">Thrown when the directory is missing or cannot be listed. The database is not modified.</exception>
        internal static void RequireReadable(string spoolDirectory)
        {
            try
            {
                if (!Directory.Exists(spoolDirectory))
                {
                    throw Unavailable(spoolDirectory, null);
                }

                using var entries = Directory.EnumerateFileSystemEntries(spoolDirectory).GetEnumerator();
                _ = entries.MoveNext();
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Unavailable(spoolDirectory, ex);
            }
        }

        /// <summary>Builds the fail-closed spool error. The database file is left unchanged.</summary>
        /// <param name="spoolDirectory">Registered spool root.</param>
        /// <param name="exception">The filesystem failure, when one was caught.</param>
        /// <returns>The startup failure.</returns>
        private static InvalidOperationException Unavailable(string spoolDirectory, Exception? exception) =>
            new(
                $"Spool '{spoolDirectory}' could not be read. Startup did not treat it as empty and did not modify its article-index database.",
                exception);
    }
}

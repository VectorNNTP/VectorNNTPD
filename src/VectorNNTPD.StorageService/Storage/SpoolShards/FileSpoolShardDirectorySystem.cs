namespace VectorNNTPD.StorageService.Storage.SpoolShards
{

    /// <summary>
    /// Creates one directory with managed filesystem APIs.
    /// An existing directory is <see cref="SpoolDirectoryCreateResult.AlreadyPresent"/> without treating
    /// permission or I/O failures as success.
    /// </summary>
    internal sealed class FileSpoolShardDirectorySystem : ISpoolShardDirectorySystem
    {
        /// <inheritdoc />
        public SpoolDirectoryCreateResult CreateDirectory(string fullPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

            if (Directory.Exists(fullPath))
            {
                return SpoolDirectoryCreateResult.AlreadyPresent;
            }

            if (File.Exists(fullPath))
            {
                throw new IOException("Path exists and is not a directory: '" + fullPath + "'.");
            }

            try
            {
                Directory.CreateDirectory(fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Concurrent creator may have won between Exists and CreateDirectory.
                // Only map that race to AlreadyPresent; keep permission and hard I/O failures.
                if (Directory.Exists(fullPath))
                {
                    return SpoolDirectoryCreateResult.AlreadyPresent;
                }

                throw new IOException("Directory.CreateDirectory failed for '" + fullPath + "'.", ex);
            }

            return SpoolDirectoryCreateResult.Created;
        }
    }
}

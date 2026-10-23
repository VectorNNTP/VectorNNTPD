namespace VectorNNTPD.StorageService.Storage.SpoolShards
{

    /// <summary>Result of one idempotent directory create.</summary>
    internal enum SpoolDirectoryCreateResult
    {
        /// <summary>The directory was created by this call.</summary>
        Created,

        /// <summary>The directory already existed and is a directory.</summary>
        AlreadyPresent,
    }

    /// <summary>
    /// Creates one shard directory. The initializer creates parents before children.
    /// An existing directory is success. An existing file is a failure.
    /// </summary>
    internal interface ISpoolShardDirectorySystem
    {
        /// <summary>Creates <paramref name="fullPath"/> or accepts it when it is already a directory.</summary>
        /// <param name="fullPath">Absolute directory path. The parent must already exist.</param>
        /// <returns>Whether the directory was new.</returns>
        /// <exception cref="IOException">Thrown when the path exists and is not a directory, or the create fails.</exception>
        SpoolDirectoryCreateResult CreateDirectory(string fullPath);
    }
}

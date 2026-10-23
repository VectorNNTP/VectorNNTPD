namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>
    /// Resolves one configured pool directory and checks existence, mount, physical identity, and writability.
    /// </summary>
    internal interface ISegmentPoolInspector
    {
        /// <summary>
        /// Inspects <paramref name="configuredPath"/>.
        /// </summary>
        /// <param name="configuredPath">Configured directory. Relative paths use <paramref name="applicationBaseDirectory"/>.</param>
        /// <param name="applicationBaseDirectory">StorageServer binary directory. Not the process working directory.</param>
        /// <param name="cancellationToken">Cancels the inspection.</param>
        /// <returns>A success or a failure that names the operation.</returns>
        SegmentPoolInspection Inspect(
            string configuredPath,
            string? applicationBaseDirectory,
            CancellationToken cancellationToken);
    }
}

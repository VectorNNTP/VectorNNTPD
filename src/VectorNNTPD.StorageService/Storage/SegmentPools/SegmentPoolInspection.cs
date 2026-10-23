namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>
    /// Result of checking one configured segment-pool directory.
    /// </summary>
    /// <param name="ConfiguredPath">Path as configured, before resolution.</param>
    /// <param name="CanonicalPath">Canonical absolute directory, or empty when resolution failed.</param>
    /// <param name="MountPoint">Mount or volume path that contains the directory, when one was identified.</param>
    /// <param name="PhysicalDeviceIds">Canonical physical-device identities. Empty when identity was not established.</param>
    /// <param name="Failure">Actionable failure, or <see langword="null"/> when the directory passed.</param>
    internal sealed record SegmentPoolInspection(
        string ConfiguredPath,
        string CanonicalPath,
        string? MountPoint,
        IReadOnlyList<string> PhysicalDeviceIds,
        string? Failure)
    {
        /// <summary>Whether this directory passed every check performed by the inspector.</summary>
        internal bool Succeeded => Failure is null;
    }
}

namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>
    /// Reads Linux block-device relationships from sysfs.
    /// </summary>
    internal interface ILinuxSysfs
    {
        /// <summary>Whether <c>/sys/dev/block/major:minor</c> exists.</summary>
        /// <param name="majorMinor">Kernel device number.</param>
        /// <returns><see langword="true"/> when the block node exists.</returns>
        bool Exists(string majorMinor);

        /// <summary>Whether the node is a partition of a parent disk.</summary>
        /// <param name="majorMinor">Kernel device number.</param>
        /// <returns><see langword="true"/> when a partition file is present.</returns>
        bool HasPartition(string majorMinor);

        /// <summary>Parent disk device number for a partition.</summary>
        /// <param name="majorMinor">Partition device number.</param>
        /// <returns>Parent <c>major:minor</c>, or <see langword="null"/> when it cannot be read.</returns>
        string? ParentMajorMinor(string majorMinor);

        /// <summary>Device numbers of device-mapper or md slaves.</summary>
        /// <param name="majorMinor">Layered device number.</param>
        /// <returns>Slave device numbers. Empty when the node has no slaves directory or no slaves.</returns>
        IReadOnlyList<string> SlaveMajorMinors(string majorMinor);

        /// <summary>Kernel block-device name, such as <c>sda</c> or <c>dm-0</c>.</summary>
        /// <param name="majorMinor">Kernel device number.</param>
        /// <returns>Device name, or empty when it cannot be read.</returns>
        string DeviceName(string majorMinor);
    }
}

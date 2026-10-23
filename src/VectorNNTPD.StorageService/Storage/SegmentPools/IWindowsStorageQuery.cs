namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>
    /// Windows volume and disk queries used by pool validation. Tests supply a fake; production uses <see cref="WindowsStorageQuery"/>.
    /// </summary>
    internal interface IWindowsStorageQuery
    {
        /// <summary>Volume mount path that contains <paramref name="path"/>.</summary>
        /// <param name="path">Existing directory.</param>
        /// <returns>The mount path, or <see langword="null"/> when the kernel does not return one.</returns>
        string? GetVolumeMountPath(string path);

        /// <summary>Resolves the volume and its physical disks.</summary>
        /// <param name="canonicalPath">Canonical directory.</param>
        /// <returns>Mount path and disk ids, or a failure that names the API and Win32 error.</returns>
        WindowsStorageNative.VolumeIdentity DescribeVolume(string canonicalPath);

        /// <summary>Final directory path after reparse points.</summary>
        /// <param name="path">Existing directory.</param>
        /// <param name="finalPath">DOS path without the extended prefix.</param>
        /// <param name="error">Diagnostic when the method returns <see langword="false"/>.</param>
        /// <returns><see langword="true"/> when the final path was read.</returns>
        bool TryGetFinalDirectory(string path, out string finalPath, out string? error);
    }

    /// <summary>Production <see cref="IWindowsStorageQuery"/>.</summary>
    internal sealed class WindowsStorageQuery : IWindowsStorageQuery
    {
        /// <inheritdoc />
        public string? GetVolumeMountPath(string path) => WindowsStorageNative.GetVolumeMountPath(path);

        /// <inheritdoc />
        public WindowsStorageNative.VolumeIdentity DescribeVolume(string canonicalPath) =>
            WindowsStorageNative.DescribeVolume(canonicalPath);

        /// <inheritdoc />
        public bool TryGetFinalDirectory(string path, out string finalPath, out string? error) =>
            WindowsStorageNative.TryGetFinalDirectory(path, out finalPath, out error);
    }
}

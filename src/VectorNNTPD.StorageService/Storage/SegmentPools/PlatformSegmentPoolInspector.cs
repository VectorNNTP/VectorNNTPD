using System.Security;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>
    /// Fail-closed pool inspector. A directory must be the mount point of a local block filesystem
    /// whose physical disks are identifiable and distinct from the operating-system disk.
    /// Missing directories are not created.
    /// </summary>
    internal sealed class PlatformSegmentPoolInspector : ISegmentPoolInspector
    {
        private readonly ISegmentPoolWriteProbe _probe;
        private readonly ILinuxSysfs _sysfs;
        private readonly IWindowsStorageQuery _windows;
        private readonly Func<string> _readMountInfo;

        /// <summary>Inspects the local operating system.</summary>
        /// <param name="probe">Read/write probe used only after mount and device checks pass.</param>
        /// <param name="sysfs">Linux sysfs reader. Unused on Windows.</param>
        public PlatformSegmentPoolInspector(ISegmentPoolWriteProbe probe, ILinuxSysfs sysfs)
            : this(probe, sysfs, new WindowsStorageQuery(), static () => File.ReadAllText("/proc/self/mountinfo"))
        {
        }

        /// <summary>Inspects with injected Windows queries and an optional Linux mount table.</summary>
        /// <param name="probe">Read/write probe.</param>
        /// <param name="sysfs">Linux sysfs reader.</param>
        /// <param name="windows">Windows volume and disk queries.</param>
        /// <param name="readMountInfo">Reads <c>/proc/self/mountinfo</c> text. Unused on Windows.</param>
        internal PlatformSegmentPoolInspector(
            ISegmentPoolWriteProbe probe,
            ILinuxSysfs sysfs,
            IWindowsStorageQuery windows,
            Func<string>? readMountInfo = null)
        {
            ArgumentNullException.ThrowIfNull(probe);
            ArgumentNullException.ThrowIfNull(sysfs);
            ArgumentNullException.ThrowIfNull(windows);
            _probe = probe;
            _sysfs = sysfs;
            _windows = windows;
            _readMountInfo = readMountInfo ?? (static () => File.ReadAllText("/proc/self/mountinfo"));
        }

        /// <inheritdoc />
        public SegmentPoolInspection Inspect(
            string configuredPath,
            string? applicationBaseDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string resolved;
            try
            {
                resolved = SegmentPoolPaths.Normalize(
                    ApplicationLocalPath.ResolveApplicationLocalPath(configuredPath, applicationBaseDirectory));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
            {
                return Fail(configuredPath, string.Empty, null, "is not a valid filesystem path: " + ex.Message);
            }

            try
            {
                if (File.Exists(resolved))
                {
                    return Fail(configuredPath, resolved, null, "is not a directory.");
                }

                var info = new DirectoryInfo(resolved);
                if (!info.Exists)
                {
                    return Fail(configuredPath, resolved, null, "does not exist.");
                }

                if ((info.Attributes & FileAttributes.Directory) == 0)
                {
                    return Fail(configuredPath, resolved, null, "is not a directory.");
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
            {
                return Fail(configuredPath, resolved, null, "is not accessible: " + ex.GetType().Name + ": " + ex.Message);
            }

            var canonical = Canonicalize(resolved, out var canonicalizeFailure);
            if (canonicalizeFailure is not null)
            {
                return Fail(configuredPath, resolved, null, canonicalizeFailure);
            }

            var located = Locate(canonical);
            if (located.Failure is not null)
            {
                return Fail(configuredPath, canonical, located.MountPoint, located.Failure);
            }

            var os = OperatingSystemDevices();
            if (os.Failure is not null)
            {
                return Fail(configuredPath, canonical, located.MountPoint, os.Failure);
            }

            var shared = located.DeviceIds.Intersect(os.DeviceIds, StringComparer.Ordinal).ToArray();
            if (shared.Length > 0)
            {
                return Fail(
                    configuredPath,
                    canonical,
                    located.MountPoint,
                    "is on the operating-system physical device '" + string.Join(",", shared) + "'.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var probeFailure = _probe.Probe(canonical, cancellationToken);
            if (probeFailure is not null)
            {
                return Fail(configuredPath, canonical, located.MountPoint, probeFailure);
            }

            return new SegmentPoolInspection(configuredPath, canonical, located.MountPoint, located.DeviceIds, null);
        }

        private string Canonicalize(string path, out string? failure)
        {
            failure = null;
            if (OperatingSystem.IsWindows())
            {
                if (!_windows.TryGetFinalDirectory(path, out var final, out var error))
                {
                    failure = error ?? "canonical directory path could not be read.";
                    return path;
                }

                return SegmentPoolPaths.Normalize(final);
            }

            try
            {
                var info = new DirectoryInfo(path);
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                return SegmentPoolPaths.Normalize(target?.FullName ?? info.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = "canonical directory path could not be read: " + ex.GetType().Name + ": " + ex.Message;
                return path;
            }
        }

        private (string? MountPoint, string[] DeviceIds, string? Failure) Locate(string canonical)
        {
            if (OperatingSystem.IsWindows())
            {
                var volumePath = _windows.GetVolumeMountPath(canonical);
                var mount = volumePath is null ? null : SegmentPoolPaths.Normalize(volumePath);
                if (mount is null || !string.Equals(mount, canonical, SegmentPoolPaths.Comparison))
                {
                    return (mount, [], "is not a dedicated mount point (resolved volume path is '" + (mount ?? "") + "').");
                }

                var volume = _windows.DescribeVolume(canonical);
                if (volume.Failure is not null)
                {
                    return (mount, [], volume.Failure);
                }

                return (mount, volume.DeviceIds, null);
            }

            if (!OperatingSystem.IsLinux())
            {
                return (null, [], "segment pool mount and physical-device checks are not available on this operating system.");
            }

            string mountInfo;
            try
            {
                mountInfo = _readMountInfo();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (null, [], "mount table could not be read: " + ex.GetType().Name + ": " + ex.Message);
            }

            var mountEntry = LinuxMountTable.FindLongestMount(mountInfo, canonical);
            if (mountEntry is null)
            {
                return (null, [], "is not a dedicated mount point (no mount contains the directory).");
            }

            if (!string.Equals(mountEntry.Value.MountPoint, canonical, StringComparison.Ordinal))
            {
                return (mountEntry.Value.MountPoint, [], "is not a dedicated mount point (resolved mount is '" + mountEntry.Value.MountPoint + "').");
            }

            if (LinuxMountTable.IsNonBlockFilesystem(mountEntry.Value.FsType))
            {
                return (mountEntry.Value.MountPoint, [], "physical device identity could not be established: filesystem type '"
                    + mountEntry.Value.FsType
                    + "' is not a local block device.");
            }

            var identity = LinuxBlockDeviceIdentity.Resolve(mountEntry.Value.MajorMinor, _sysfs);
            return (mountEntry.Value.MountPoint, identity.DeviceIds, identity.Failure);
        }

        private (string[] DeviceIds, string? Failure) OperatingSystemDevices()
        {
            if (OperatingSystem.IsWindows())
            {
                var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var volume = _windows.DescribeVolume(system);
                if (volume.Failure is not null || volume.DeviceIds.Length == 0)
                {
                    return ([], "operating-system physical device could not be established: " + (volume.Failure ?? "no disk extents."));
                }

                return (volume.DeviceIds, null);
            }

            if (!OperatingSystem.IsLinux())
            {
                return ([], "operating-system physical device could not be established on this operating system.");
            }

            string mountInfo;
            try
            {
                mountInfo = _readMountInfo();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ([], "operating-system physical device could not be established: " + ex.Message);
            }

            var root = LinuxMountTable.FindLongestMount(mountInfo, "/");
            if (root is null || root.Value.MountPoint != "/")
            {
                return ([], "operating-system physical device could not be established: the root mount is missing.");
            }

            if (LinuxMountTable.IsNonBlockFilesystem(root.Value.FsType))
            {
                return ([], "operating-system physical device could not be established: root filesystem type '"
                    + root.Value.FsType
                    + "' does not expose physical disks.");
            }

            var identity = LinuxBlockDeviceIdentity.Resolve(root.Value.MajorMinor, _sysfs);
            if (identity.Failure is not null || identity.DeviceIds.Length == 0)
            {
                return ([], "operating-system physical device could not be established: " + (identity.Failure ?? "no devices."));
            }

            return (identity.DeviceIds, null);
        }

        private static SegmentPoolInspection Fail(string configured, string canonical, string? mount, string failure) =>
            new(configured, canonical, mount, [], failure);
    }
}

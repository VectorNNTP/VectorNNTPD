using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>
    /// Windows volume, physical-disk, and disk-performance queries.
    /// Drive letters are not treated as physical disks. Virtual, Storage Spaces, iSCSI, and RAID
    /// bus types fail closed because their member disks are not identified by these calls.
    /// </summary>
    internal static partial class WindowsStorageNative
    {
        private const uint GenericRead = 0x80000000;

        /// <summary>
        /// <c>FILE_ANY_ACCESS</c>. <c>IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS</c>,
        /// <c>IOCTL_STORAGE_QUERY_PROPERTY</c>, and <c>IOCTL_DISK_PERFORMANCE</c> are defined with this access.
        /// <c>GENERIC_READ</c> asks for <c>FILE_READ_DATA</c>, which the volume ACL denies to a non-elevated token
        /// (Win32 error 5) even though the IOCTL does not read file data.
        /// </summary>
        private const uint FileAnyAccess = 0;

        private const uint FileShareReadWriteDelete = 0x1 | 0x2 | 0x4;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
        private const uint IoctlDiskPerformance = 0x00070020;
        private const uint IoctlStorageQueryProperty = 0x002D1400;
        private const int ErrorMoreData = 234;

        /// <summary>Resolved volume identity, or a failure that names what could not be established.</summary>
        /// <param name="MountPoint">Volume mount path returned by the kernel.</param>
        /// <param name="DeviceIds">Physical disk ids such as <c>disk:1</c>.</param>
        /// <param name="Failure">Actionable diagnostic, or <see langword="null"/> on success.</param>
        internal readonly record struct VolumeIdentity(string? MountPoint, string[] DeviceIds, string? Failure);

        /// <summary>
        /// Resolves the volume that contains <paramref name="canonicalPath"/> and its physical disks.
        /// </summary>
        /// <param name="canonicalPath">Canonical directory.</param>
        /// <returns>Mount path and physical disk ids, or a failure.</returns>
        internal static VolumeIdentity DescribeVolume(string canonicalPath)
        {
            if (!OperatingSystem.IsWindows())
            {
                return new VolumeIdentity(null, [], "Windows volume identity is not available on this operating system.");
            }

            var volumePath = GetVolumeMountPath(canonicalPath);
            if (volumePath is null)
            {
                return new VolumeIdentity(null, [], "volume mount path could not be read.");
            }

            var volumeName = GetVolumeName(volumePath);
            if (volumeName is null)
            {
                return new VolumeIdentity(volumePath, [], "volume name could not be read for mount '" + volumePath + "'.");
            }

            var extents = ReadDiskExtents(volumeName);
            if (extents.Failure is not null)
            {
                return new VolumeIdentity(volumePath, [], extents.Failure);
            }

            var devices = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var disk in extents.DiskNumbers)
            {
                var bus = ReadBusType(disk);
                if (bus.Failure is not null)
                {
                    return new VolumeIdentity(volumePath, [], bus.Failure);
                }

                devices.Add("disk:" + disk.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (devices.Count == 0)
            {
                return new VolumeIdentity(volumePath, [], "physical device identity could not be established: the volume has no disk extents.");
            }

            return new VolumeIdentity(volumePath, devices.ToArray(), null);
        }

        /// <summary>One <c>IOCTL_DISK_PERFORMANCE</c> result. Times are 100-nanosecond units.</summary>
        /// <param name="BytesRead">Cumulative bytes read.</param>
        /// <param name="BytesWritten">Cumulative bytes written.</param>
        /// <param name="ReadTime">Cumulative read response time in 100-nanosecond units.</param>
        /// <param name="WriteTime">Cumulative write response time in 100-nanosecond units.</param>
        /// <param name="IdleTime">Cumulative time with no outstanding I/O, in 100-nanosecond units.</param>
        /// <param name="ReadCount">Cumulative completed reads. This field is 32-bit.</param>
        /// <param name="WriteCount">Cumulative completed writes. This field is 32-bit.</param>
        /// <param name="QueueDepth">Requests outstanding at the query. Not an average.</param>
        /// <param name="QueryTime">Driver timestamp of the query, in 100-nanosecond units.</param>
        internal readonly record struct DiskPerformanceSnapshot(
            long BytesRead,
            long BytesWritten,
            long ReadTime,
            long WriteTime,
            long IdleTime,
            uint ReadCount,
            uint WriteCount,
            uint QueueDepth,
            long QueryTime);

        /// <summary>
        /// Opens <c>\\.\PhysicalDriveN</c> with desired access 0 for <c>IOCTL_DISK_PERFORMANCE</c>.
        /// The caller owns the handle. <paramref name="win32Error"/> is captured immediately when the open fails.
        /// </summary>
        /// <param name="diskNumber">Windows physical disk number.</param>
        /// <param name="handle">Open handle, or an invalid handle.</param>
        /// <param name="devicePath">Path passed to <c>CreateFileW</c>.</param>
        /// <param name="win32Error">Win32 error when <paramref name="handle"/> is invalid; otherwise 0.</param>
        internal static void OpenPerformanceHandle(uint diskNumber, out SafeFileHandle handle, out string devicePath, out int win32Error)
        {
            devicePath = @"\\.\PhysicalDrive" + diskNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            handle = CreateFile(devicePath, FileAnyAccess, FileShareReadWriteDelete, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            win32Error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        }

        /// <summary>
        /// Issues <c>IOCTL_DISK_PERFORMANCE</c> on an already open disk handle.
        /// The Win32 error is captured immediately. A failure is not a zero snapshot.
        /// </summary>
        /// <param name="handle">Disk handle opened with desired access 0.</param>
        /// <param name="devicePath">Path used in the diagnostic.</param>
        /// <param name="snapshot">Parsed counters when the method returns <see langword="true"/>.</param>
        /// <param name="error">Diagnostic when the method returns <see langword="false"/>.</param>
        /// <returns><see langword="true"/> when the IOCTL returned a snapshot.</returns>
        internal static bool TryQueryPerformance(SafeFileHandle handle, string devicePath, out DiskPerformanceSnapshot snapshot, out string? error)
        {
            snapshot = default;
            error = null;
            var size = Marshal.SizeOf<DiskPerformanceData>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!DeviceIoControl(handle, IoctlDiskPerformance, IntPtr.Zero, 0, buffer, (uint)size, out _, IntPtr.Zero))
                {
                    error = FormatWin32Failure("DeviceIoControl", devicePath, Marshal.GetLastWin32Error(), "IOCTL_DISK_PERFORMANCE");
                    return false;
                }

                var data = Marshal.PtrToStructure<DiskPerformanceData>(buffer);
                snapshot = new DiskPerformanceSnapshot(
                    data.BytesRead,
                    data.BytesWritten,
                    data.ReadTime,
                    data.WriteTime,
                    data.IdleTime,
                    data.ReadCount,
                    data.WriteCount,
                    data.QueueDepth,
                    data.QueryTime);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>Final path of a directory after reparse points, or a failure.</summary>
        /// <param name="path">Existing directory.</param>
        /// <param name="finalPath">DOS path without the <c>\\?\</c> prefix.</param>
        /// <param name="error">Diagnostic when the method returns <see langword="false"/>.</param>
        /// <returns><see langword="true"/> when the final path was read.</returns>
        internal static bool TryGetFinalDirectory(string path, out string finalPath, out string? error)
        {
            finalPath = path;
            error = null;
            using var handle = CreateFile(
                path,
                GenericRead,
                FileShareReadWriteDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                error = "directory could not be opened: Win32 error " + Marshal.GetLastWin32Error() + ".";
                return false;
            }

            var buffer = new char[1024];
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0 || length >= buffer.Length)
            {
                error = "canonical directory path could not be read: Win32 error " + Marshal.GetLastWin32Error() + ".";
                return false;
            }

            finalPath = StripExtendedPrefix(new string(buffer, 0, (int)length));
            return true;
        }

        /// <summary>Volume mount path that contains <paramref name="path"/>.</summary>
        /// <param name="path">Existing file or directory.</param>
        /// <returns>The mount path, or <see langword="null"/> when the kernel does not return one.</returns>
        internal static string? GetVolumeMountPath(string path)
        {
            var buffer = new char[512];
            if (!GetVolumePathName(path, buffer, (uint)buffer.Length))
            {
                var withSlash = SegmentPoolPaths.WithTrailingSeparator(path);
                if (!GetVolumePathName(withSlash, buffer, (uint)buffer.Length))
                {
                    return null;
                }
            }

            var text = TrimAtNull(buffer);
            return text.Length == 0 ? null : text;
        }

        private static string? GetVolumeName(string mountPath)
        {
            var mount = SegmentPoolPaths.WithTrailingSeparator(mountPath);
            var buffer = new char[128];
            if (!GetVolumeNameForVolumeMountPoint(mount, buffer, (uint)buffer.Length))
            {
                return null;
            }

            var text = TrimAtNull(buffer);
            return text.Length == 0 ? null : text;
        }

        private static (uint[] DiskNumbers, string? Failure) ReadDiskExtents(string volumeName)
        {
            var device = VolumeDevicePath(volumeName);
            if (device is null)
            {
                return ([], "volume device path could not be derived from '" + volumeName + "'.");
            }

            using var handle = CreateFile(device, FileAnyAccess, FileShareReadWriteDelete, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return ([], FormatWin32Failure("CreateFileW", device, Marshal.GetLastWin32Error(), "IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS"));
            }

            var capacity = 32;
            var buffer = Marshal.AllocHGlobal(8 + (capacity * 24));
            try
            {
                if (!DeviceIoControl(handle, IoctlVolumeGetVolumeDiskExtents, IntPtr.Zero, 0, buffer, (uint)(8 + (capacity * 24)), out _, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorMoreData)
                    {
                        return ([], FormatWin32Failure("DeviceIoControl", device, error, "IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS"));
                    }
                }

                var count = Marshal.ReadInt32(buffer);
                if (count <= 0)
                {
                    return ([], "physical device identity could not be established: the volume reported no disk extents.");
                }

                if (count > capacity)
                {
                    return ([], "physical device identity could not be established: the volume spans more than " + capacity + " disks.");
                }

                var disks = new uint[count];
                for (var i = 0; i < count; i++)
                {
                    disks[i] = (uint)Marshal.ReadInt32(buffer, 8 + (i * 24));
                }

                return (disks, null);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static (byte BusType, string? Failure) ReadBusType(uint diskNumber)
        {
            var device = @"\\.\PhysicalDrive" + diskNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var handle = CreateFile(
                device,
                FileAnyAccess,
                FileShareReadWriteDelete,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return (0, FormatWin32Failure("CreateFileW", device, Marshal.GetLastWin32Error(), "IOCTL_STORAGE_QUERY_PROPERTY"));
            }

            var query = new StoragePropertyQueryData();
            var querySize = Marshal.SizeOf<StoragePropertyQueryData>();
            var queryPtr = Marshal.AllocHGlobal(querySize);
            var output = Marshal.AllocHGlobal(512);
            try
            {
                Marshal.StructureToPtr(query, queryPtr, false);
                if (!DeviceIoControl(handle, IoctlStorageQueryProperty, queryPtr, (uint)querySize, output, 512, out var returned, IntPtr.Zero))
                {
                    return (0, FormatWin32Failure("DeviceIoControl", device, Marshal.GetLastWin32Error(), "IOCTL_STORAGE_QUERY_PROPERTY"));
                }

                if (returned < 29)
                {
                    return (0, "physical disk " + diskNumber + " bus type could not be established.");
                }

                var bus = Marshal.ReadByte(output, 28);
                var rejected = RejectedBusMessage(diskNumber, bus);
                if (rejected is not null)
                {
                    return (0, rejected);
                }

                return (bus, null);
            }
            finally
            {
                Marshal.FreeHGlobal(queryPtr);
                Marshal.FreeHGlobal(output);
            }
        }

        /// <summary>
        /// Accepts SCSI, ATA, USB, SAS, SATA, SD, NVMe, SCM, and UFS disk numbers as one OS physical disk.
        /// Fibre Channel is rejected: a LUN number does not prove independent physical media.
        /// Unknown, RAID, iSCSI, Virtual, FileBackedVirtual, and Spaces fail closed.
        /// </summary>
        /// <param name="bus">STORAGE_BUS_TYPE value.</param>
        /// <returns><see langword="true"/> when the bus type is treated as one directly attached physical disk.</returns>
        internal static bool IsAcceptedBus(byte bus) => bus is 1 or 2 or 3 or 4 or 7 or 10 or 11 or 12 or 17 or 18 or 19;

        /// <summary>Failure text for a bus type that does not prove a physical disk, or <see langword="null"/> when the bus is accepted.</summary>
        /// <param name="diskNumber">Windows physical disk number.</param>
        /// <param name="bus">STORAGE_BUS_TYPE value.</param>
        /// <returns>An actionable diagnostic, or <see langword="null"/>.</returns>
        internal static string? RejectedBusMessage(uint diskNumber, byte bus)
        {
            if (IsAcceptedBus(bus))
            {
                return null;
            }

            if (bus == 6)
            {
                return "physical device identity could not be established: disk "
                    + diskNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " bus type 6 (Fibre Channel) is a logical LUN. Separate Fibre Channel LUNs are not proven to be independent physical media.";
            }

            return "physical device identity could not be established: disk "
                + diskNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " bus type "
                + bus.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " is virtual, Storage Spaces, iSCSI, RAID, or otherwise not a verified physical disk.";
        }

        /// <summary>
        /// Formats a native failure with the operation, target, Win32 code, and the reason error 5 is not treated as success.
        /// </summary>
        /// <param name="operation">API that failed. <c>GetLastWin32Error</c> must already have been captured.</param>
        /// <param name="target">Volume or disk path passed to the API.</param>
        /// <param name="win32Error">Value captured immediately after the failing call.</param>
        /// <param name="ioctl">IOCTL the handle was opened to issue.</param>
        /// <returns>Diagnostic text.</returns>
        internal static string FormatWin32Failure(string operation, string target, int win32Error, string ioctl)
        {
            var message = operation + " failed for '" + target + "' while issuing " + ioctl + ": Win32 error " + win32Error + ".";
            if (win32Error == 5)
            {
                message += " ERROR_ACCESS_DENIED. The handle was requested with no data access (desired access 0) because "
                    + ioctl
                    + " is FILE_ANY_ACCESS. The object ACL still denied the call; this is not treated as an empty or successful disk identity.";
            }

            return message;
        }

        private static string? VolumeDevicePath(string volumeName)
        {
            const string prefix = @"\\?\Volume{";
            if (!volumeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var end = volumeName.IndexOf('}', prefix.Length);
            if (end < 0)
            {
                return null;
            }

            return @"\\.\Volume" + volumeName[(prefix.Length - 1)..(end + 1)];
        }

        private static string StripExtendedPrefix(string path)
        {
            const string unc = @"\\?\UNC\";
            const string prefix = @"\\?\";
            if (path.StartsWith(unc, StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + path[unc.Length..];
            }

            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return path[prefix.Length..];
            }

            return path;
        }

        private static string TrimAtNull(char[] buffer)
        {
            var length = Array.IndexOf(buffer, '\0');
            if (length < 0)
            {
                length = buffer.Length;
            }

            return new string(buffer, 0, length);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StoragePropertyQueryData
        {
            public uint PropertyId;
            public uint QueryType;
            public byte Additional;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DiskPerformanceData
        {
            public long BytesRead;
            public long BytesWritten;
            public long ReadTime;
            public long WriteTime;
            public long IdleTime;
            public uint ReadCount;
            public uint WriteCount;
            public uint QueueDepth;
            public uint SplitCount;
            public long QueryTime;
            public uint StorageDeviceNumber;
            public long StorageManagerName0;
            public long StorageManagerName1;
        }

        // Platform guards live on the public helpers (OperatingSystem.IsWindows). LibraryImport
        // methods omit [SupportedOSPlatform] so call sites inside those guards do not trip CA1416.
        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CreateFileW")]
        private static partial SafeFileHandle CreateFile(
            string fileName,
            uint access,
            uint share,
            nint security,
            uint disposition,
            uint flags,
            nint template);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DeviceIoControl(
            SafeFileHandle handle,
            uint code,
            nint inBuffer,
            uint inSize,
            nint outBuffer,
            uint outSize,
            out uint returned,
            nint overlapped);

        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16, EntryPoint = "GetVolumePathNameW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetVolumePathName(string fileName, [Out] char[] buffer, uint length);

        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetVolumeNameForVolumeMountPoint(string mountPoint, [Out] char[] buffer, uint length);

        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16, EntryPoint = "GetFinalPathNameByHandleW")]
        private static partial uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] buffer, uint length, uint flags);
    }
}

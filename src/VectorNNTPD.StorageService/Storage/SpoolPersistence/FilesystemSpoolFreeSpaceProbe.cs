using System.Runtime.InteropServices;

namespace VectorNNTPD.StorageService.Storage.SpoolPersistence
{

    /// <summary>Reads filesystem capacity for one spool directory. A failure leaves the outputs unchanged.</summary>
    internal interface ISpoolFreeSpaceProbe
    {
        /// <summary>Reads total and available bytes for <paramref name="spoolRoot"/>.</summary>
        /// <param name="spoolRoot">Canonical spool directory.</param>
        /// <param name="totalBytes">Filesystem capacity when the method returns true.</param>
        /// <param name="availableBytes">Bytes available to this caller when the method returns true.</param>
        /// <returns><see langword="false"/> when the platform call fails. That is not zero free space.</returns>
        bool TryRead(string spoolRoot, out long totalBytes, out long availableBytes);
    }

    /// <summary>
    /// <c>GetDiskFreeSpaceExW</c> on Windows and <c>statvfs</c> on Linux.
    /// This is filesystem capacity, not the block-device counter source.
    /// </summary>
    internal sealed class FilesystemSpoolFreeSpaceProbe : ISpoolFreeSpaceProbe
    {
        /// <inheritdoc />
        public bool TryRead(string spoolRoot, out long totalBytes, out long availableBytes)
        {
            totalBytes = 0;
            availableBytes = 0;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (!GetDiskFreeSpaceExW(spoolRoot, out var available, out var total, out _))
                    {
                        return false;
                    }

                    totalBytes = checked((long)total);
                    availableBytes = checked((long)available);
                    return totalBytes > 0;
                }

                if (OperatingSystem.IsLinux())
                {
                    if (statvfs(spoolRoot, out var stat) != 0 || stat.FragmentSize == 0)
                    {
                        return false;
                    }

                    totalBytes = checked((long)(stat.Blocks * stat.FragmentSize));
                    availableBytes = checked((long)(stat.AvailableBlocks * stat.FragmentSize));
                    return totalBytes > 0 && availableBytes >= 0 && availableBytes <= totalBytes;
                }
            }
            catch (OverflowException)
            {
                return false;
            }

            return false;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetDiskFreeSpaceExW(
            string directory,
            out ulong freeBytesAvailable,
            out ulong totalNumberOfBytes,
            out ulong totalNumberOfFreeBytes);

        [DllImport("libc", SetLastError = true)]
        private static extern int statvfs(string path, out LinuxStatVfs buffer);

        [StructLayout(LayoutKind.Sequential)]
        private struct LinuxStatVfs
        {
            public ulong BlockSize;
            public ulong FragmentSize;
            public ulong Blocks;
            public ulong FreeBlocks;
            public ulong AvailableBlocks;
            public ulong Files;
            public ulong FreeFiles;
            public ulong AvailableFiles;
            public ulong FileSystemId;
            public ulong Flags;
            public ulong NameMax;
            public int Spare0;
            public int Spare1;
            public int Spare2;
            public int Spare3;
            public int Spare4;
            public int Spare5;
        }
    }
}

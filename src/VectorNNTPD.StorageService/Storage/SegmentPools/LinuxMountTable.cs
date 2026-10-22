namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>
    /// Parses Linux <c>/proc/self/mountinfo</c> and selects the longest mount covering a path.
    /// </summary>
    internal static class LinuxMountTable
    {
        /// <summary>One mountinfo row reduced to the fields segment-pool validation needs.</summary>
        /// <param name="MajorMinor">Kernel device number, for example <c>8:0</c>.</param>
        /// <param name="MountPoint">Decoded mount point.</param>
        /// <param name="FsType">Filesystem type after the <c>-</c> separator.</param>
        internal readonly record struct Mount(string MajorMinor, string MountPoint, string FsType);

        /// <summary>
        /// Finds the longest mount whose mount point contains <paramref name="canonicalPath"/>.
        /// </summary>
        /// <param name="mountInfo">Raw mountinfo text.</param>
        /// <param name="canonicalPath">Absolute directory.</param>
        /// <returns>The longest covering mount, or <see langword="null"/> when none match.</returns>
        internal static Mount? FindLongestMount(string mountInfo, string canonicalPath)
        {
            ArgumentNullException.ThrowIfNull(mountInfo);
            ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
            Mount? best = null;
            var bestLength = -1;
            foreach (var line in mountInfo.Split('\n'))
            {
                if (!TryParse(line, out var mount))
                {
                    continue;
                }

                if (!Covers(mount.MountPoint, canonicalPath))
                {
                    continue;
                }

                if (mount.MountPoint.Length > bestLength)
                {
                    best = mount;
                    bestLength = mount.MountPoint.Length;
                }
            }

            return best;
        }

        /// <summary>Parses one mountinfo line. Optional fields before the <c>-</c> separator are skipped.</summary>
        /// <param name="line">One mountinfo line.</param>
        /// <param name="mount">Parsed mount when the method returns <see langword="true"/>.</param>
        /// <returns><see langword="true"/> when the line has a device, mount point, and filesystem type.</returns>
        internal static bool TryParse(string line, out Mount mount)
        {
            mount = default;
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var dash = -1;
            for (var i = 6; i < fields.Length; i++)
            {
                if (fields[i] == "-")
                {
                    dash = i;
                    break;
                }
            }

            if (dash < 0 || fields.Length < 5 || dash + 1 >= fields.Length)
            {
                return false;
            }

            var mountPoint = Unescape(fields[4]);
            if (mountPoint.Length == 0)
            {
                return false;
            }

            mount = new Mount(fields[2], mountPoint, fields[dash + 1]);
            return true;
        }

        /// <summary>Whether <paramref name="mountPoint"/> contains <paramref name="path"/>.</summary>
        /// <param name="mountPoint">Decoded mount point.</param>
        /// <param name="path">Absolute path.</param>
        /// <returns><see langword="true"/> when the path is the mount point or a descendant.</returns>
        internal static bool Covers(string mountPoint, string path)
        {
            if (mountPoint == "/")
            {
                return true;
            }

            if (string.Equals(path, mountPoint, StringComparison.Ordinal))
            {
                return true;
            }

            return path.StartsWith(mountPoint + "/", StringComparison.Ordinal);
        }

        /// <summary>Decodes mountinfo octal escapes such as <c>\040</c> for space.</summary>
        /// <param name="value">Raw mountinfo field.</param>
        /// <returns>Decoded text.</returns>
        internal static string Unescape(string value)
        {
            if (!value.Contains('\\', StringComparison.Ordinal))
            {
                return value;
            }

            var builder = new System.Text.StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == '\\' && i + 3 < value.Length
                    && IsOctal(value[i + 1]) && IsOctal(value[i + 2]) && IsOctal(value[i + 3]))
                {
                    var code = ((value[i + 1] - '0') * 64) + ((value[i + 2] - '0') * 8) + (value[i + 3] - '0');
                    builder.Append((char)code);
                    i += 3;
                    continue;
                }

                builder.Append(value[i]);
            }

            return builder.ToString();
        }

        /// <summary>Filesystem types that are not a dedicated local block device.</summary>
        /// <param name="fsType">mountinfo filesystem type.</param>
        /// <returns><see langword="true"/> when the type cannot prove physical-disk isolation.</returns>
        internal static bool IsNonBlockFilesystem(string fsType)
        {
            return fsType is "nfs" or "nfs4" or "cifs" or "smb3" or "smbfs" or "fuse" or "fuse.sshfs"
                or "fuseblk" or "overlay" or "tmpfs" or "proc" or "sysfs" or "devtmpfs" or "ramfs"
                or "9p" or "virtiofs" or "autofs" or "debugfs" or "tracefs" or "squashfs" or "iso9660";
        }

        private static bool IsOctal(char value) => value is >= '0' and <= '7';
    }
}

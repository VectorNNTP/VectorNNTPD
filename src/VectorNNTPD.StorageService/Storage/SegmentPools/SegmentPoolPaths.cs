namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>
    /// Canonical directory comparison for segment pools.
    /// </summary>
    internal static class SegmentPoolPaths
    {
        /// <summary>Path comparison for the current operating system.</summary>
        internal static StringComparison Comparison { get; } =
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        /// <summary>
        /// Normalizes a directory so a filesystem root keeps its trailing separator
        /// and every other directory does not.
        /// </summary>
        /// <param name="path">Absolute or relative path.</param>
        /// <returns>A fully qualified directory path.</returns>
        internal static string Normalize(string path)
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
            {
                return full;
            }

            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length == 0 || string.Equals(trimmed, trimmedRoot, Comparison))
            {
                return root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
                    ? root
                    : root + Path.DirectorySeparatorChar;
            }

            return trimmed;
        }

        /// <summary>Returns <paramref name="directory"/> with exactly one trailing separator.</summary>
        /// <param name="directory">Normalized directory.</param>
        /// <returns>Directory prefix suitable for containment checks.</returns>
        internal static string WithTrailingSeparator(string directory)
        {
            if (directory.EndsWith(Path.DirectorySeparatorChar) || directory.EndsWith(Path.AltDirectorySeparatorChar))
            {
                return directory;
            }

            return directory + Path.DirectorySeparatorChar;
        }
    }
}

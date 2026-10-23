namespace VectorNNTPD.StorageService.Storage
{

    /// <summary>
    /// Builds paths under a caller-chosen base using validated relative segments and
    /// <see cref="Path.Join(string, string)"/> (which does not drop the base for a rooted later argument).
    /// </summary>
    /// <remarks>
    /// Callers that intend <c>base/leaf</c> must pass a single relative segment (no roots, separators, or
    /// <c>.</c>/<c>..</c>). After joining, the fully qualified result must stay under the base directory.
    /// </remarks>
    internal static class StorageRelativePath
    {
        /// <summary>
        /// Validates <paramref name="relativeName"/> as one relative file-name segment and combines it under
        /// <paramref name="baseDirectory"/>.
        /// </summary>
        /// <param name="baseDirectory">Directory that must own the result.</param>
        /// <param name="relativeName">Single relative file or directory name.</param>
        /// <returns>The joined path under <paramref name="baseDirectory"/>.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="relativeName"/> is empty, rooted, contains separators, is <c>.</c>/<c>..</c>,
        /// or contains invalid file-name characters.
        /// </exception>
        /// <exception cref="InvalidOperationException">The resolved path escapes <paramref name="baseDirectory"/>.</exception>
        internal static string CombineUnder(string baseDirectory, string relativeName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
            var leaf = RequireSingleRelativeSegment(relativeName);
            // Path.Join keeps the base even if a later segment were rooted; leaf is already validated.
            var combined = Path.Join(baseDirectory, leaf);
            EnsureContained(baseDirectory, combined);
            return combined;
        }

        /// <summary>
        /// Combines several relative file-name segments under <paramref name="baseDirectory"/> in order.
        /// </summary>
        /// <param name="baseDirectory">Directory that must own the result.</param>
        /// <param name="relativeSegments">Each element must be a single relative segment.</param>
        /// <returns>The combined path.</returns>
        internal static string CombineUnder(string baseDirectory, params string[] relativeSegments)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
            ArgumentNullException.ThrowIfNull(relativeSegments);
            if (relativeSegments.Length == 0)
            {
                throw new ArgumentException("At least one relative segment is required.", nameof(relativeSegments));
            }

            var current = baseDirectory;
            foreach (var segment in relativeSegments)
            {
                current = CombineUnder(current, segment);
            }

            return current;
        }

        /// <summary>
        /// Combines a relative segment that may include characters illegal as Win32 file names
        /// (Linux <c>major:minor</c>) after the caller has already constrained the alphabet.
        /// Still rejects rooted values, separators, and <c>.</c>/<c>..</c>, then verifies containment.
        /// </summary>
        /// <param name="baseDirectory">Directory that must own the result.</param>
        /// <param name="relativeName">Single relative segment already proven safe by the caller.</param>
        /// <returns>The combined path.</returns>
        internal static string CombineUnderCheckedSegment(string baseDirectory, string relativeName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
            var leaf = RequireRelativeSegmentWithoutFileNameCharset(relativeName);
            var combined = Path.Join(baseDirectory, leaf);
            EnsureContained(baseDirectory, combined);
            return combined;
        }

        /// <summary>
        /// Returns a trimmed single relative path segment, or throws when the value could escape a base directory.
        /// </summary>
        /// <param name="name">Candidate file or directory name.</param>
        /// <returns>The trimmed segment.</returns>
        internal static string RequireSingleRelativeSegment(string name)
        {
            var trimmed = RequireRelativeSegmentWithoutFileNameCharset(name);
            if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("Path segment contains invalid file-name characters.", nameof(name));
            }

            return trimmed;
        }

        /// <summary>
        /// Ensures <paramref name="candidate"/> resolves inside <paramref name="baseDirectory"/>.
        /// </summary>
        /// <param name="baseDirectory">Owning directory.</param>
        /// <param name="candidate">Combined path to verify.</param>
        internal static void EnsureContained(string baseDirectory, string candidate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(candidate);

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
            var full = Path.GetFullPath(candidate);
            var prefix = root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, PathComparison)
                && !string.Equals(full, root, PathComparison))
            {
                throw new InvalidOperationException(
                    $"Path '{full}' is outside the required directory '{root}'.");
            }
        }

        private static string RequireRelativeSegmentWithoutFileNameCharset(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var trimmed = name.Trim();
            if (trimmed.Length == 0)
            {
                throw new ArgumentException("Path segment cannot be empty.", nameof(name));
            }

            if (trimmed is "." or "..")
            {
                throw new ArgumentException("Path segment cannot be '.' or '..'.", nameof(name));
            }

            if (Path.IsPathRooted(trimmed))
            {
                throw new ArgumentException("Path segment cannot be rooted.", nameof(name));
            }

            if (trimmed.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            {
                throw new ArgumentException("Path segment cannot contain directory separators.", nameof(name));
            }

            return trimmed;
        }

        private static StringComparison PathComparison =>
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }
}

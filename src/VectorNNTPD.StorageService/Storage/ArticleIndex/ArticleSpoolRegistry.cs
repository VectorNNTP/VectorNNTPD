using System.Security.Cryptography;

namespace VectorNNTPD.StorageService.Storage.ArticleIndex
{

    /// <summary>One registered spool and the SQLite file that stores its article metadata.</summary>
    /// <param name="Id">Stable 32-character lowercase hex identity stored in the spool root.</param>
    /// <param name="SpoolDirectory">Canonical spool root. Article payloads stay here.</param>
    /// <param name="DatabasePath">SQLite file under the configured database directory.</param>
    internal readonly record struct ArticleSpoolBinding(string Id, string SpoolDirectory, string DatabasePath);

    /// <summary>
    /// Maps each registered spool to one SQLite file under <c>DbDir</c>.
    /// The mapping is the identity file in the spool root, not the drive letter.
    /// </summary>
    /// <remarks>
    /// Disabling a spool leaves its identity file, registry row, and database on disk.
    /// A second spool that presents the same identity fails startup. An unreadable identity is not replaced.
    /// </remarks>
    internal static class ArticleSpoolRegistry
    {
        /// <summary>Resolves a database binding for every distinct spool root.</summary>
        /// <param name="databaseDirectory">Canonical database directory. Every database path stays inside it.</param>
        /// <param name="spoolDirectories">Registered spool roots in catalog order.</param>
        /// <returns>One binding per spool. Empty when nothing is registered.</returns>
        internal static IReadOnlyList<ArticleSpoolBinding> Resolve(string databaseDirectory, IReadOnlyList<string> spoolDirectories)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databaseDirectory);
            ArgumentNullException.ThrowIfNull(spoolDirectories);
            var registryPath = StorageRelativePath.CombineUnder(
                databaseDirectory,
                ArticleIndexSettings.SpoolRegistryFileName);
            var registry = ReadRegistry(registryPath);
            var bindings = new List<ArticleSpoolBinding>(spoolDirectories.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var spool in Distinct(spoolDirectories))
            {
                var id = ReadOrCreateIdentity(spool);
                if (!seen.Add(id))
                {
                    throw new InvalidOperationException(
                        $"Spool '{spool}' uses article-index identity '{id}', which another registered spool already uses.");
                }

                if (registry.TryGetValue(id, out var previous) && !PathsEqual(previous, spool))
                {
                    if (Directory.Exists(previous) && SameIdentity(previous, id))
                    {
                        throw new InvalidOperationException(
                            $"Article-index identity '{id}' is still present at '{previous}' and cannot be reassigned to '{spool}'.");
                    }

                    registry[id] = spool;
                }
                else
                {
                    registry[id] = spool;
                }

                // id is 32 lowercase hex characters; the leaf cannot be rooted or contain separators.
                var databasePath = StorageRelativePath.CombineUnder(
                    databaseDirectory,
                    "article-index-" + id + ".sqlite");

                bindings.Add(new ArticleSpoolBinding(id, spool, databasePath));
            }

            WriteRegistry(registryPath, registry);
            return bindings;
        }

        /// <summary>Reads a 32-character identity, or creates one when the spool has never had one.</summary>
        /// <param name="spoolDirectory">Canonical spool root.</param>
        /// <returns>The lowercase hex identity.</returns>
        private static string ReadOrCreateIdentity(string spoolDirectory)
        {
            if (!Directory.Exists(spoolDirectory))
            {
                throw new InvalidOperationException($"Spool directory '{spoolDirectory}' does not exist. The article index does not create spool roots.");
            }

            var path = StorageRelativePath.CombineUnder(spoolDirectory, ArticleIndexSettings.SpoolIdentityFileName);
            if (File.Exists(path))
            {
                return ReadIdentity(path);
            }

            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, id + Environment.NewLine);
            File.Move(temporary, path, overwrite: false);
            return id;
        }

        /// <summary>Reads an identity file and rejects any other contents.</summary>
        /// <param name="path">Identity file path.</param>
        /// <returns>The 32-character identity.</returns>
        private static string ReadIdentity(string path)
        {
            string text;
            try
            {
                text = File.ReadAllText(path).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Article-index identity '{path}' could not be read. It was not replaced.", ex);
            }

            if (text.Length != 32 || !IsLowerHex(text))
            {
                throw new InvalidOperationException($"Article-index identity '{path}' is not 32 lowercase hex characters. It was not replaced.");
            }

            return text;
        }

        /// <summary>Returns whether <paramref name="spoolDirectory"/> still stores <paramref name="id"/>.</summary>
        /// <param name="spoolDirectory">Previous spool root.</param>
        /// <param name="id">Identity being resolved.</param>
        /// <returns><see langword="false"/> when the old root is gone or now has a different identity.</returns>
        private static bool SameIdentity(string spoolDirectory, string id)
        {
            var path = StorageRelativePath.CombineUnder(spoolDirectory, ArticleIndexSettings.SpoolIdentityFileName);
            return File.Exists(path) && string.Equals(ReadIdentity(path), id, StringComparison.Ordinal);
        }

        /// <summary>Reads the registry. A missing file is an empty map.</summary>
        /// <param name="path">Registry path under the database directory.</param>
        /// <returns>Identity to canonical spool path.</returns>
        private static Dictionary<string, string> ReadRegistry(string path)
        {
            var registry = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!File.Exists(path))
            {
                return registry;
            }

            foreach (var line in File.ReadAllLines(path))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var split = line.IndexOf('\t');
                if (split != 32)
                {
                    throw new InvalidOperationException($"Article index registry '{path}' has an unreadable row and was not rewritten.");
                }

                var id = line[..split];
                var spool = line[(split + 1)..];
                if (!IsLowerHex(id) || spool.Length == 0)
                {
                    throw new InvalidOperationException($"Article index registry '{path}' has an unreadable row and was not rewritten.");
                }

                registry[id] = spool;
            }

            return registry;
        }

        /// <summary>Replaces the registry atomically. Rows for disabled spools are kept.</summary>
        /// <param name="path">Registry path.</param>
        /// <param name="registry">Identity to spool path.</param>
        private static void WriteRegistry(string path, Dictionary<string, string> registry)
        {
            var temporary = path + ".tmp";
            var lines = new string[registry.Count];
            var index = 0;
            foreach (var pair in registry.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                lines[index++] = pair.Key + "\t" + pair.Value;
            }

            File.WriteAllLines(temporary, lines);
            File.Move(temporary, path, overwrite: true);
        }

        /// <summary>Canonical distinct spool roots in first-seen order.</summary>
        /// <param name="spoolDirectories">Catalog paths.</param>
        /// <returns>The roots to bind.</returns>
        private static List<string> Distinct(IReadOnlyList<string> spoolDirectories)
        {
            var roots = new List<string>(spoolDirectories.Count);
            var seen = new HashSet<string>(PathComparer);
            foreach (var spool in spoolDirectories)
            {
                if (string.IsNullOrWhiteSpace(spool))
                {
                    throw new InvalidOperationException("A registered spool path is empty.");
                }

                var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(spool));
                if (seen.Add(canonical))
                {
                    roots.Add(canonical);
                }
            }

            return roots;
        }

        /// <summary>Compares canonical paths using the platform's directory comparison.</summary>
        /// <param name="left">First path.</param>
        /// <param name="right">Second path.</param>
        /// <returns><see langword="true"/> when the paths name the same directory.</returns>
        private static bool PathsEqual(string left, string right) =>
            string.Equals(left, right, PathComparison);

        /// <summary>Returns whether <paramref name="text"/> is 32 lowercase hex characters. The caller checks the length.</summary>
        /// <param name="text">Candidate identity.</param>
        /// <returns><see langword="false"/> when a character is outside <c>0-9a-f</c>.</returns>
        private static bool IsLowerHex(string text)
        {
            foreach (var character in text)
            {
                var hex = character is >= '0' and <= '9' || character is >= 'a' and <= 'f';
                if (!hex)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Path comparison used for spool identity conflicts.</summary>
        private static StringComparison PathComparison =>
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        /// <summary>Path comparer used to collapse duplicate catalog entries.</summary>
        private static StringComparer PathComparer =>
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }
}

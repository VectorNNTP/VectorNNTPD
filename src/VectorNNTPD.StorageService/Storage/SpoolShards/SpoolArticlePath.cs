using System.Collections.Concurrent;
using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.StorageService.Storage.SpoolShards
{

    /// <summary>
    /// Deterministic relative article path from the canonical 32-byte BLAKE3 <see cref="ArticleId"/>.
    /// The path does not hash the identity again and does not read the filesystem.
    /// The same identity has the same relative path on every spool.
    /// </summary>
    internal static class SpoolArticlePath
    {
        /// <summary>
        /// Normalized spool-root prefixes, including a trailing separator.
        /// One entry per distinct root string passed to <see cref="CombineArticle"/>.
        /// The cache does not store article paths.
        /// </summary>
        private static readonly ConcurrentDictionary<string, string> RootPrefixes = new(StringComparer.Ordinal);

        /// <summary>
        /// Relative path <c>{n0}/{n1}/{n2}/{64 lowercase hex}</c>.
        /// The three directory nibbles are the first three characters of the canonical lowercase hex digest.
        /// </summary>
        /// <param name="articleId">Canonical VATP article identity.</param>
        /// <returns>A relative path with no parent segments.</returns>
        internal static string RelativePath(ArticleId articleId)
        {
            return string.Create(ShardLength, articleId, static (destination, id) =>
            {
                WriteShard(destination, id, '/');
            });
        }

        /// <summary>
        /// Absolute article path under <paramref name="spoolRoot"/> for <paramref name="articleId"/>.
        /// The relative segments are the canonical lowercase hex nibbles, so the result cannot leave the root.
        /// The normalized root prefix is cached for the life of the process.
        /// </summary>
        /// <param name="spoolRoot">Absolute spool directory.</param>
        /// <param name="articleId">Canonical VATP article identity.</param>
        /// <returns>The absolute article path, using the platform directory separator.</returns>
        internal static string CombineArticle(string spoolRoot, ArticleId articleId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(spoolRoot);
            var prefix = RootPrefixes.GetOrAdd(spoolRoot, static root =>
            {
                var full = Path.GetFullPath(root);
                return full.EndsWith(Path.DirectorySeparatorChar)
                    ? full
                    : string.Concat(full, Path.DirectorySeparatorChar);
            });
            return string.Create(prefix.Length + ShardLength, (prefix, articleId), static (destination, state) =>
            {
                state.prefix.AsSpan().CopyTo(destination);
                WriteShard(destination[state.prefix.Length..], state.articleId, Path.DirectorySeparatorChar);
            });
        }

        /// <summary>
        /// Resolves a lowercase hexadecimal article id. Uppercase, the wrong length, and any non-hex character fail.
        /// </summary>
        /// <param name="articleIdHex">Exactly 64 lowercase hexadecimal characters.</param>
        /// <param name="relativePath">The relative article path when the method returns <see langword="true"/>.</param>
        /// <returns><see langword="true"/> when <paramref name="articleIdHex"/> is a canonical <see cref="ArticleId"/> encoding.</returns>
        internal static bool TryRelativePath(ReadOnlySpan<char> articleIdHex, out string relativePath)
        {
            if (!ArticleId.TryParseLowerHex(articleIdHex, out var articleId))
            {
                relativePath = string.Empty;
                return false;
            }

            relativePath = RelativePath(articleId);
            return true;
        }

        /// <summary>
        /// Combines a spool root with a resolver-produced relative path and rejects a result outside that root.
        /// </summary>
        /// <param name="spoolRoot">Absolute spool directory.</param>
        /// <param name="relativePath">Path from <see cref="RelativePath"/>.</param>
        /// <returns>The absolute article path.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the combined path leaves <paramref name="spoolRoot"/>.</exception>
        internal static string CombineUnderRoot(string spoolRoot, string relativePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(spoolRoot);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            if (relativePath.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
            {
                throw new InvalidOperationException("Article path is not a relative spool path.");
            }

            var combined = spoolRoot;
            var rest = relativePath.AsSpan();
            while (rest.Length > 0)
            {
                var slash = rest.IndexOf('/');
                var segment = slash < 0 ? rest : rest[..slash];
                if (segment.Length == 0 || segment.Length > ArticleId.HexLength)
                {
                    throw new InvalidOperationException("Article path contains an empty or oversized segment.");
                }

                for (var i = 0; i < segment.Length; i++)
                {
                    var c = segment[i];
                    if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                    {
                        throw new InvalidOperationException("Article path contains a non-hexadecimal segment.");
                    }
                }

                combined = Path.Combine(combined, segment.ToString());
                if (slash < 0)
                {
                    break;
                }

                rest = rest[(slash + 1)..];
            }

            var root = Path.GetFullPath(spoolRoot);
            var full = Path.GetFullPath(combined);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, comparison))
            {
                throw new InvalidOperationException("Article path escapes the spool root.");
            }

            return full;
        }

        /// <summary>Characters in <c>{n0}/{n1}/{n2}/{64 hex}</c>, including the three separators.</summary>
        private const int ShardLength = 6 + ArticleId.HexLength;

        /// <summary>Writes the three shard nibbles, separators, and the 64-digit lowercase hex digest.</summary>
        /// <param name="destination">Buffer of <see cref="ShardLength"/> characters.</param>
        /// <param name="articleId">Canonical article identity.</param>
        /// <param name="separator">Separator between the shard segments.</param>
        private static void WriteShard(Span<char> destination, ArticleId articleId, char separator)
        {
            Span<byte> digest = stackalloc byte[ArticleId.Length];
            articleId.CopyTo(digest);
            destination[0] = ToHex(digest[0] >> 4);
            destination[1] = separator;
            destination[2] = ToHex(digest[0] & 0x0F);
            destination[3] = separator;
            destination[4] = ToHex(digest[1] >> 4);
            destination[5] = separator;
            var index = 6;
            for (var i = 0; i < digest.Length; i++)
            {
                destination[index++] = ToHex(digest[i] >> 4);
                destination[index++] = ToHex(digest[i] & 0x0F);
            }
        }

        /// <summary>One lowercase hexadecimal digit.</summary>
        /// <param name="value">A value from 0 through 15.</param>
        /// <returns>The corresponding ASCII hex character.</returns>
        private static char ToHex(int value) => (char)(value < 10 ? '0' + value : 'a' + (value - 10));
    }
}

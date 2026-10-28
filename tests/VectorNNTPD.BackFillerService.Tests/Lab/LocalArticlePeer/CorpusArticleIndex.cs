using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    /// <summary>
    /// Maps Message-ID → relative corpus path using header-only scans. Does not retain article bodies.
    /// </summary>
    internal sealed class CorpusArticleIndex
    {
        private readonly ConcurrentDictionary<string, string> _messageIdToRelativePath =
            new(StringComparer.Ordinal);

        private CorpusArticleIndex(string corpusRoot, IReadOnlyDictionary<string, string> map, string fingerprint)
        {
            CorpusRoot = Path.GetFullPath(corpusRoot);
            FingerprintSha256 = fingerprint;
            foreach (var pair in map)
            {
                _messageIdToRelativePath[pair.Key] = pair.Value;
            }
        }

        internal string CorpusRoot { get; }

        internal int Count => _messageIdToRelativePath.Count;

        internal string FingerprintSha256 { get; }

        internal IEnumerable<string> MessageIds => _messageIdToRelativePath.Keys;

        internal bool TryResolve(string messageId, out string absolutePath)
        {
            absolutePath = "";
            if (!_messageIdToRelativePath.TryGetValue(messageId, out var relative))
            {
                return false;
            }

            absolutePath = Path.Combine(CorpusRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(absolutePath);
        }

        /// <summary>
        /// Builds an index by reading at most <paramref name="headerProbeBytes"/> from each file under
        /// <paramref name="corpusRoot"/>. Skips files without a Message-ID or header/body separator in the probe.
        /// </summary>
        internal static async Task<CorpusArticleIndex> BuildAsync(
            string corpusRoot,
            int headerProbeBytes = 16 * 1024,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(corpusRoot);
            var root = Path.GetFullPath(corpusRoot);
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException(root);
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var fingerprintMaterial = new List<string>();
            var buffer = new byte[headerProbeBytes];
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .OrderBy(static p => p, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read;
                await using (var stream = new FileStream(
                                 file,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.Read,
                                 bufferSize: 4096,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    read = await stream.ReadAsync(buffer.AsMemory(0, headerProbeBytes), cancellationToken)
                        .ConfigureAwait(false);
                }

                if (read <= 0)
                {
                    continue;
                }

                var span = buffer.AsSpan(0, read);
                if (span.IndexOf("\r\n\r\n"u8) < 0 && span.IndexOf("\n\n"u8) < 0)
                {
                    continue;
                }

                if (!TryReadMessageId(span, out var messageId))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (map.TryAdd(messageId, relative))
                {
                    fingerprintMaterial.Add($"{messageId}\t{relative}\t{new FileInfo(file).Length}");
                }
            }

            fingerprintMaterial.Sort(StringComparer.Ordinal);
            var fpBytes = Encoding.UTF8.GetBytes(string.Join('\n', fingerprintMaterial));
            var fingerprint = Convert.ToHexString(SHA256.HashData(fpBytes));
            return new CorpusArticleIndex(root, map, fingerprint);
        }

        internal async Task WriteManifestAsync(string path, CancellationToken cancellationToken)
        {
            var payload = new
            {
                CorpusRoot,
                Count,
                FingerprintSha256,
                Entries = _messageIdToRelativePath
                    .OrderBy(static p => p.Key, StringComparer.Ordinal)
                    .Select(static p => new { MessageId = p.Key, RelativePath = p.Value })
                    .Take(32)
                    .ToArray(),
                EntriesNote = "First 32 Message-IDs only; full count and fingerprint identify the corpus.",
            };
            await File.WriteAllTextAsync(
                    path,
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private static bool TryReadMessageId(ReadOnlySpan<byte> article, out string messageId)
        {
            messageId = "";
            var lineStart = 0;
            while (lineStart < article.Length)
            {
                var rest = article[lineStart..];
                var newline = rest.IndexOf((byte)'\n');
                var line = newline < 0 ? rest : rest[..newline];
                if (line.Length > 0 && line[^1] == (byte)'\r')
                {
                    line = line[..^1];
                }

                if (line.Length == 0)
                {
                    return false;
                }

                if (line.Length >= 11
                    && (line.StartsWith("Message-ID:"u8)
                        || line.StartsWith("Message-Id:"u8)
                        || StartsWithIgnoreCase(line, "message-id:"u8)))
                {
                    var value = line[11..].Trim((byte)' ');
                    messageId = Encoding.ASCII.GetString(value);
                    return messageId.Length > 0;
                }

                if (newline < 0)
                {
                    return false;
                }

                lineStart += newline + 1;
            }

            return false;
        }

        private static bool StartsWithIgnoreCase(ReadOnlySpan<byte> line, ReadOnlySpan<byte> asciiLowerPrefix)
        {
            if (line.Length < asciiLowerPrefix.Length)
            {
                return false;
            }

            for (var i = 0; i < asciiLowerPrefix.Length; i++)
            {
                var c = line[i];
                if (c >= (byte)'A' && c <= (byte)'Z')
                {
                    c = (byte)(c + 32);
                }

                if (c != asciiLowerPrefix[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}

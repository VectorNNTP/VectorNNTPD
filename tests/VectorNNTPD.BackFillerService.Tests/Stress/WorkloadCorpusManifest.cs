using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Auditable provenance for a stress workload derived from a news-log corpus.
    /// </summary>
    public sealed class WorkloadCorpusManifest
    {
        /// <summary>Absolute news-log directory used for discovery (provenance gate).</summary>
        public required string CorpusDirectory { get; init; }

        /// <summary>Number of <c>*.log</c> files scanned.</summary>
        public required int FileCount { get; init; }

        /// <summary>Total log lines examined during discovery.</summary>
        public required long LogRecordsExamined { get; init; }

        /// <summary>Lines that parsed to a canonical Message-ID.</summary>
        public required long ValidParsedIds { get; init; }

        /// <summary>Lines rejected for non-canonical Message-ID syntax.</summary>
        public required long RejectedInvalidMessageIds { get; init; }

        /// <summary>Distinct canonical ids after deduplication.</summary>
        public required long UniqueValidIds { get; init; }

        /// <summary>Count written to warmup workload file.</summary>
        public required int WarmupSelected { get; init; }

        /// <summary>Count written to main workload file.</summary>
        public required int MainSelected { get; init; }

        /// <summary>Count written to repeatable workload file.</summary>
        public required int RepeatableSelected { get; init; }

        /// <summary>Shuffle seed used when partitioning ids.</summary>
        public required int Seed { get; init; }

        /// <summary>SHA-256 hex of <c>main-message-ids.txt</c> at creation time.</summary>
        public required string MainIdsSha256 { get; init; }

        /// <summary>UTC timestamp when the manifest was written.</summary>
        public required DateTimeOffset CreatedUtc { get; init; }

        /// <summary>Optional human notes (e.g. corpus policy reminders).</summary>
        public string? Notes { get; init; }

        /// <summary>Serializes and writes the manifest JSON into the workload directory.</summary>
        public static async Task WriteAsync(
            string workloadDirectory,
            WorkloadCorpusManifest manifest,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(workloadDirectory);
            ArgumentNullException.ThrowIfNull(manifest);
            Directory.CreateDirectory(workloadDirectory);
            var path = Path.Join(workloadDirectory, StressCorpusPaths.WorkloadManifestFileName);
            await File.WriteAllTextAsync(
                    path,
                    JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>Reads manifest JSON when present; otherwise <see langword="null"/>.</summary>
        public static async Task<WorkloadCorpusManifest?> TryReadAsync(
            string workloadDirectory,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(workloadDirectory);
            var path = Path.Join(workloadDirectory, StressCorpusPaths.WorkloadManifestFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = File.OpenRead(path);
            return await JsonSerializer
                .DeserializeAsync<WorkloadCorpusManifest>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>Computes lowercase hex SHA-256 of a file (used for main workload integrity).</summary>
        public static string ComputeFileSha256(string filePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            using var stream = File.OpenRead(filePath);
            var hash = SHA256.HashData(stream);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }
    }
}

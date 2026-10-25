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
        public required string CorpusDirectory { get; init; }

        public required int FileCount { get; init; }

        public required long LogRecordsExamined { get; init; }

        public required long ValidParsedIds { get; init; }

        public required long RejectedInvalidMessageIds { get; init; }

        public required long UniqueValidIds { get; init; }

        public required int WarmupSelected { get; init; }

        public required int MainSelected { get; init; }

        public required int RepeatableSelected { get; init; }

        public required int Seed { get; init; }

        public required string MainIdsSha256 { get; init; }

        public required DateTimeOffset CreatedUtc { get; init; }

        public string? Notes { get; init; }

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

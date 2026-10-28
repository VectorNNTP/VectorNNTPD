using VectorNNTPD.BackFillerService.Tests.Stress;

namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    /// <summary>
    /// Builds stress-compatible Message-ID workload files from the local Articles corpus index.
    /// Required because news-log discovery IDs may not exist on the local peer disk.
    /// </summary>
    internal static class LocalArticlePeerWorkload
    {
        /// <summary>
        /// Writes warmup/main/repeatable ID lists and a <see cref="WorkloadCorpusManifest"/> whose
        /// <see cref="WorkloadCorpusManifest.CorpusDirectory"/> is the Articles root so
        /// <see cref="StressWorkloadGuard"/> accepts the workload when news-log env points at Articles.
        /// </summary>
        internal static async Task<(int Count, string Fingerprint)> WriteAsync(
            CorpusArticleIndex index,
            string workloadDirectory,
            int seed,
            int warmupCount = 50,
            int mainCount = 2000,
            int repeatableCount = 200,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(index);
            ArgumentException.ThrowIfNullOrWhiteSpace(workloadDirectory);
            Directory.CreateDirectory(workloadDirectory);

            var ids = index.MessageIds.OrderBy(static s => s, StringComparer.Ordinal).ToList();
            if (ids.Count == 0)
            {
                throw new InvalidOperationException("Corpus index contains no Message-IDs.");
            }

            var rng = new Random(seed);
            var shuffled = ids.ToList();
            for (var i = shuffled.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }

            warmupCount = Math.Min(warmupCount, shuffled.Count);
            mainCount = Math.Min(mainCount, shuffled.Count);
            repeatableCount = Math.Min(repeatableCount, shuffled.Count);

            var warmup = shuffled.Take(warmupCount).ToArray();
            var main = shuffled.Take(mainCount).ToArray();
            var repeatable = shuffled.Take(repeatableCount).ToArray();

            var warmupPath = Path.Join(workloadDirectory, "warmup-message-ids.txt");
            var mainPath = Path.Join(workloadDirectory, StressCorpusPaths.MainIdsAuditFileName);
            var repeatablePath = Path.Join(workloadDirectory, "repeatable-message-ids.txt");
            await File.WriteAllLinesAsync(warmupPath, warmup, cancellationToken).ConfigureAwait(false);
            await File.WriteAllLinesAsync(mainPath, main, cancellationToken).ConfigureAwait(false);
            await File.WriteAllLinesAsync(repeatablePath, repeatable, cancellationToken).ConfigureAwait(false);

            var manifest = new WorkloadCorpusManifest
            {
                CorpusDirectory = index.CorpusRoot,
                FileCount = index.Count,
                LogRecordsExamined = index.Count,
                ValidParsedIds = index.Count,
                RejectedInvalidMessageIds = 0,
                UniqueValidIds = index.Count,
                WarmupSelected = warmup.Length,
                MainSelected = main.Length,
                RepeatableSelected = repeatable.Length,
                Seed = seed,
                MainIdsSha256 = WorkloadCorpusManifest.ComputeFileSha256(mainPath),
                CreatedUtc = DateTimeOffset.UtcNow,
                Notes =
                    "Lab local ARTICLE peer workload derived from .artifacts/Articles Message-ID index. "
                    + "Not news-log discovery. Set VECTORNNTPD_BACKFILLER_E2E_STRESS_NEWSLOGS to the Articles root "
                    + "so StressWorkloadGuard provenance matches.",
            };
            await WorkloadCorpusManifest.WriteAsync(workloadDirectory, manifest, cancellationToken)
                .ConfigureAwait(false);
            return (index.Count, index.FingerprintSha256);
        }
    }
}

using System.Security.Cryptography;

namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Refuses unprovenanced or cross-corpus workload reuse for live stress runs.
    /// </summary>
    public static class StressWorkloadGuard
    {
        /// <summary>
        /// Ensures workload files exist, carry a manifest, match the configured corpus path,
        /// and that the main ID file hash matches the manifest. Does not invent Message-IDs.
        /// </summary>
        public static async Task RequireProvenancedWorkloadAsync(
            StressHarnessOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            var main = Path.Join(options.WorkloadDirectory, StressCorpusPaths.MainIdsAuditFileName);
            if (!File.Exists(main))
            {
                throw new InvalidOperationException(
                    "Workload files missing. Run Discover_workload_from_newslogs_when_enabled against the required corpus "
                    + $"({options.NewsLogDirectory}). Expected {main}. "
                    + "Stress will not invent Message-IDs or use an unprovenanced cache.");
            }

            var manifest = await WorkloadCorpusManifest.TryReadAsync(options.WorkloadDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (manifest is null)
            {
                throw new InvalidOperationException(
                    "Workload directory lacks " + StressCorpusPaths.WorkloadManifestFileName
                    + ". Stale caches without provenance are rejected. Re-run discovery from "
                    + options.NewsLogDirectory + ".");
            }

            var configured = Path.GetFullPath(options.NewsLogDirectory);
            var recorded = Path.GetFullPath(manifest.CorpusDirectory);
            if (!string.Equals(configured, recorded, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Workload provenance corpus path does not match the configured news-log directory. "
                    + $"configured={configured} manifest={recorded}. "
                    + "Re-run discovery; do not reuse another dataset.");
            }

            if (manifest.UniqueValidIds <= 0 || manifest.MainSelected <= 0)
            {
                throw new InvalidOperationException(
                    "Workload manifest records no usable Message-IDs. Re-run discovery; do not invent replacements.");
            }

            var actualHash = WorkloadCorpusManifest.ComputeFileSha256(main);
            if (!string.Equals(actualHash, manifest.MainIdsSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "main-message-ids.txt SHA-256 does not match corpus-manifest.json. "
                    + "The workload was altered after discovery. Re-run discovery from the configured corpus.");
            }
        }

        /// <summary>
        /// Verifies each <paramref name="selectedIds"/> entry is present in <paramref name="allowedIds"/>.
        /// </summary>
        public static void RequireSelectedIdsSubset(
            IEnumerable<string> selectedIds,
            IReadOnlySet<string> allowedIds)
        {
            ArgumentNullException.ThrowIfNull(selectedIds);
            ArgumentNullException.ThrowIfNull(allowedIds);
            foreach (var id in selectedIds)
            {
                if (!allowedIds.Contains(id))
                {
                    throw new InvalidOperationException(
                        "Selected Message-ID is not in the allowed corpus set: " + id);
                }
            }
        }

        /// <summary>Deterministic SHA-256 of a joined ID list (ordinal order already applied by caller).</summary>
        public static string HashIds(IEnumerable<string> ids)
        {
            ArgumentNullException.ThrowIfNull(ids);
            var joined = string.Join('\n', ids);
            var bytes = System.Text.Encoding.UTF8.GetBytes(joined);
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
    }
}

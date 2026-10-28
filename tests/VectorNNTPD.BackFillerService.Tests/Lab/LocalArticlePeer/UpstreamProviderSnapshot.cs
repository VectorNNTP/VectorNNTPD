using System.Text.Json;
using System.Text.Json.Serialization;

namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    /// <summary>
    /// Operator-captured sanitized view of published BackFiller upstream endpoints for one lab ServerId.
    /// Passwords must never be included. Used by <see cref="LocalUpstreamIsolationGate"/> before smoke.
    /// </summary>
    internal sealed record UpstreamProviderSnapshot
    {
        /// <summary>UTC capture time of the account-pool observation.</summary>
        public required DateTimeOffset CapturedUtc { get; init; }

        /// <summary>Lab BackFiller <c>ServerId</c> the rows belong to.</summary>
        public required int ServerId { get; init; }

        /// <summary>
        /// How the snapshot was obtained (e.g. <c>mysql-select</c>, <c>log-parse</c>).
        /// Empty or unknown values fail the gate.
        /// </summary>
        public required string Source { get; init; }

        /// <summary>
        /// Optional note that session pools were observed refreshed after MySQL change
        /// (e.g. log events 5601/5603). Required non-empty for smoke readiness.
        /// </summary>
        public required string PoolRefreshEvidence { get; init; }

        /// <summary>Published provider endpoints (no credentials).</summary>
        public required IReadOnlyList<UpstreamProviderEndpoint> Providers { get; init; }

        /// <summary>Deserializes a JSON snapshot file.</summary>
        internal static async Task<UpstreamProviderSnapshot> ReadAsync(string path, CancellationToken cancellationToken)
        {
            await using var stream = File.OpenRead(path);
            var snapshot = await JsonSerializer
                .DeserializeAsync(stream, UpstreamProviderSnapshotJsonContext.Default.UpstreamProviderSnapshot, cancellationToken)
                .ConfigureAwait(false);
            return snapshot
                ?? throw new InvalidOperationException("Upstream provider snapshot JSON deserialized to null: " + path);
        }

        /// <summary>Writes indented JSON (sanitized; caller must not include secrets).</summary>
        internal static async Task WriteAsync(
            string path,
            UpstreamProviderSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            await using var stream = File.Create(path);
            await JsonSerializer
                .SerializeAsync(
                    stream,
                    snapshot,
                    UpstreamProviderSnapshotJsonContext.Default.UpstreamProviderSnapshot,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>One published backbone endpoint without credentials.</summary>
    internal sealed record UpstreamProviderEndpoint
    {
        public required string Backbone { get; init; }

        public required string Host { get; init; }

        public required int Port { get; init; }

        public required bool UseTls { get; init; }
    }

    [JsonSerializable(typeof(UpstreamProviderSnapshot))]
    [JsonSerializable(typeof(UpstreamProviderEndpoint))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    internal partial class UpstreamProviderSnapshotJsonContext : JsonSerializerContext
    {
    }
}

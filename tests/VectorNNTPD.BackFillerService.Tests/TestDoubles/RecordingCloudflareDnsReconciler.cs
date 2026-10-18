
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.Common.Networking;

namespace VectorNNTPD.BackFillerService.Tests.TestDoubles
{
    /// <summary>
    /// Records Cloudflare reconcile/remove calls for lifecycle assertions.
    /// </summary>

    internal sealed class RecordingCloudflareDnsReconciler : ICloudflareDnsReconciler
    {
        /// <summary>Serializes appends to recorded call lists from concurrent reconcile/remove invocations.</summary>
        private readonly object _gate = new();

        /// <summary>Recorded <see cref="ReconcileAsync"/> zone/FQDN pairs; no Cloudflare API calls.</summary>
        public List<(string ZoneId, string Fqdn)> ReconcileCalls { get; } = [];

        /// <summary>Recorded <see cref="RemoveAllRecordsForFqdnAsync"/> zone/FQDN pairs.</summary>
        public List<(string ZoneId, string Fqdn)> RemoveCalls { get; } = [];

        /// <summary>Recorded <see cref="DeleteRecordIdsAsync"/> calls and the number of ids requested.</summary>
        public List<(string ZoneId, string Fqdn, int IdCount)> DeleteCalls { get; } = [];

        /// <summary>When set, thrown after the reconcile call is recorded; does not contact Cloudflare.</summary>
        public Exception? ReconcileException { get; set; }

        /// <summary>
        /// Whether the last recorded reconcile is treated as having deleted a pre-existing record.
        /// Tests set this before a failing reconcile when they need the compensating-cleanup branch.
        /// </summary>
        public bool DeletedPreExistingDuringLastReconcile { get; set; }

        /// <summary>
        /// Records zone and FQDN under lock, then throws <see cref="ReconcileException"/> when configured.
        /// Ignores bind addresses beyond null validation; no DNS mutation occurs.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone identifier passed through for assertions.</param>
        /// <param name="fqdn">Fully qualified name passed through for assertions.</param>
        /// <param name="desired">Resolved bind addresses required by the interface; not applied in the fake.</param>
        /// <param name="cancellationToken">Honored before recording or throwing.</param>
        public Task ReconcileAsync(
            string zoneId,
            string fqdn,
            ResolvedBindAddresses desired,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
            ArgumentNullException.ThrowIfNull(desired);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                ReconcileCalls.Add((zoneId, fqdn));
            }

            if (ReconcileException is not null)
            {
                throw ReconcileException;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Appends zone and FQDN to <see cref="RemoveCalls"/> under lock.
        /// Does not delete records or honor <paramref name="operationTimeout"/>.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone identifier passed through for assertions.</param>
        /// <param name="fqdn">Fully qualified name passed through for assertions.</param>
        /// <param name="cancellationToken">Honored before recording.</param>
        /// <param name="operationTimeout">Ignored; present only for interface compatibility.</param>
        public Task RemoveAllRecordsForFqdnAsync(
            string zoneId,
            string fqdn,
            CancellationToken cancellationToken,
            TimeSpan? operationTimeout = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                RemoveCalls.Add((zoneId, fqdn));
            }

            return Task.CompletedTask;
        }

        /// <summary>Returns no record ids. Does not contact Cloudflare.</summary>
        /// <param name="zoneId">Zone id. Not recorded.</param>
        /// <param name="fqdn">FQDN. Not recorded.</param>
        /// <param name="cancellationToken">Honored before returning.</param>
        /// <returns>An empty id list.</returns>
        public Task<IReadOnlyList<string>> ListRecordIdsAsync(
            string zoneId,
            string fqdn,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        /// <summary>Completes without deleting the supplied ids. Does not contact Cloudflare.</summary>
        /// <param name="zoneId">Zone id. Not recorded.</param>
        /// <param name="fqdn">FQDN. Not recorded.</param>
        /// <param name="recordIds">Record ids. Not deleted.</param>
        /// <param name="cancellationToken">Honored before returning.</param>
        /// <param name="operationTimeout">Ignored.</param>
        /// <returns>A completed task.</returns>
        public Task DeleteRecordIdsAsync(
            string zoneId,
            string fqdn,
            IReadOnlyCollection<string> recordIds,
            CancellationToken cancellationToken,
            TimeSpan? operationTimeout = null)
        {
            ArgumentNullException.ThrowIfNull(recordIds);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                DeleteCalls.Add((zoneId, fqdn, recordIds.Count));
            }

            return Task.CompletedTask;
        }
    }
}

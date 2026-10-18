
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.Common.Networking;

namespace VectorNNTPD.BackFillerService.Tests.TestDoubles
{
    /// <summary>
    /// <see cref="ICloudflareDnsReconciler"/> that completes without Cloudflare API calls.
    /// Used when host composition tests require the service registration but must not perform DNS I/O.
    /// </summary>
    internal sealed class NoOpCloudflareDnsReconciler : ICloudflareDnsReconciler
    {
        /// <inheritdoc />
        public Task ReconcileAsync(
            string zoneId,
            string fqdn,
            ResolvedBindAddresses desired,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        /// <summary>Reports that this double never deleted a pre-existing record.</summary>
        public bool DeletedPreExistingDuringLastReconcile => false;

        /// <inheritdoc />
        public Task RemoveAllRecordsForFqdnAsync(
            string zoneId,
            string fqdn,
            CancellationToken cancellationToken,
            TimeSpan? operationTimeout = null) =>
            Task.CompletedTask;

        /// <summary>Returns no record ids. Arguments are not observed.</summary>
        /// <param name="zoneId">Zone id. Ignored.</param>
        /// <param name="fqdn">FQDN. Ignored.</param>
        /// <param name="cancellationToken">Cancellation token. Not observed.</param>
        /// <returns>An empty id list.</returns>
        public Task<IReadOnlyList<string>> ListRecordIdsAsync(
            string zoneId,
            string fqdn,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        /// <summary>Completes without deleting the supplied ids. Arguments are not observed.</summary>
        /// <param name="zoneId">Zone id. Ignored.</param>
        /// <param name="fqdn">FQDN. Ignored.</param>
        /// <param name="recordIds">Record ids. Ignored.</param>
        /// <param name="cancellationToken">Cancellation token. Not observed.</param>
        /// <param name="operationTimeout">Optional timeout. Ignored.</param>
        /// <returns>A completed task.</returns>
        public Task DeleteRecordIdsAsync(
            string zoneId,
            string fqdn,
            IReadOnlyCollection<string> recordIds,
            CancellationToken cancellationToken,
            TimeSpan? operationTimeout = null) =>
            Task.CompletedTask;
    }
}

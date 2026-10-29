using VectorNNTPD.Common.Networking;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.Common.Cloudflare
{
    /// <summary>
    /// Application service that reconciles Cloudflare DNS on startup and removes the exact FQDN on shutdown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This host is authoritative for the entire configured <c>{Fqdn}</c> in the configured zone.
    /// <see cref="StartAsync"/> publishes A/AAAA for resolved bind addresses and fails closed on verify failure.
    /// <see cref="StopAsync"/> deletes every DNS record for the exact FQDN (all types), then verifies none remain.
    /// </para>
    /// <para>
    /// Registered first among application services so reverse-order stop runs clean-up after other services
    /// (including listeners) have stopped. Startup failure after reconcile work begins attempts clean-up
    /// so partial A/AAAA publications are not left without a recovery path. Concurrent start/stop are rejected
    /// by the application host; reconcile and clean-up share the reconciler gate.
    /// </para>
    /// <para>
    /// When an <see cref="IDnsPublicationLease"/> withholds publication, <see cref="StartAsync"/> validates
    /// and returns without calling Cloudflare. <see cref="PublishAfterListenerBoundAsync"/> publishes only
    /// after the listener has bound. A failed publication deletes record ids created by that attempt and
    /// does not remove records that were already present. <see cref="StopAsync"/> removes the FQDN only
    /// after a verified publication in this process.
    /// </para>
    /// </remarks>
    internal sealed class CloudflareDnsReconciliationService : IListenerDnsPublication
    {
        /// <summary>Maximum wall-clock budget for best-effort clean-up after a failed or cancelled startup reconcile.</summary>
        public static readonly TimeSpan FailedStartCleanupTimeout = TimeSpan.FromSeconds(15);

        /// <summary>FQDN, zone id, and bind addresses used for reconcile and cleanup.</summary>
        private readonly IOptions<AcmeCloudflareOptions> _options;

        /// <summary>Resolves the bind addresses published as A and AAAA records.</summary>
        private readonly IBindAddressResolver _bindAddressResolver;

        /// <summary>Performs the Cloudflare read/mutate/verify work.</summary>
        private readonly ICloudflareDnsReconciler _reconciler;

        /// <summary>Reconciliation-service diagnostics.</summary>
        private readonly ILogger<CloudflareDnsReconciliationService> _logger;

        /// <summary>
        /// When set and <see cref="IDnsPublicationLease.PublishDuringStart"/> is false, start does not mutate DNS.
        /// Null keeps the immediate-publication behavior used by NNTPD and BackFiller.
        /// </summary>
        private readonly IDnsPublicationLease? _publicationLease;

        /// <summary>
        /// Non-zero after a reconcile in this process returns successfully.
        /// <see cref="StopAsync"/> exchanges it back to zero and skips cleanup when it was already zero.
        /// </summary>
        private int _fqdnOwnershipActive;

        /// <summary>
        /// Initializes a new instance of the <see cref="CloudflareDnsReconciliationService"/> class.
        /// </summary>
        /// <param name="options">ACME and Cloudflare options, including the generated FQDN and zone id.</param>
        /// <param name="bindAddressResolver">Resolves addresses published as A and AAAA records.</param>
        /// <param name="reconciler">Cloudflare DNS reconciler.</param>
        /// <param name="logger">Reconciliation-service logger.</param>
        /// <param name="publicationLease">
        /// Optional lease. Null publishes during <see cref="StartAsync"/>. A lease with
        /// <see cref="IDnsPublicationLease.PublishDuringStart"/> false waits for
        /// <see cref="PublishAfterListenerBoundAsync"/>.
        /// </param>
        public CloudflareDnsReconciliationService(
            IOptions<AcmeCloudflareOptions> options,
            IBindAddressResolver bindAddressResolver,
            ICloudflareDnsReconciler reconciler,
            ILogger<CloudflareDnsReconciliationService> logger,
            IDnsPublicationLease? publicationLease = null)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(bindAddressResolver);
            ArgumentNullException.ThrowIfNull(reconciler);
            ArgumentNullException.ThrowIfNull(logger);

            _options = options;
            _bindAddressResolver = bindAddressResolver;
            _reconciler = reconciler;
            _logger = logger;
            _publicationLease = publicationLease;
        }

        /// <summary>Gets the stable service name recorded by the application service manager.</summary>
        internal static string Name => "CloudflareDnsReconciliation";

        /// <summary>Gets null. This service has no background execution after <see cref="StartAsync"/> returns.</summary>
        internal static Task? Execution => null;

        /// <summary>
        /// Publishes A and AAAA records for the resolved bind addresses and marks FQDN ownership active only after verification.
        /// </summary>
        /// <param name="cancellationToken">Cancels reconcile. Cancellation after reconcile begins attempts a bounded cleanup, then propagates.</param>
        /// <returns>A task that completes when Cloudflare has verified the desired address set.</returns>
        /// <exception cref="InvalidOperationException">
        /// The generated FQDN or zone id is missing, or bind-address resolution produced no eligible address.
        /// </exception>
        /// <remarks>
        /// A non-cancellation failure after reconcile begins also attempts cleanup of the exact FQDN.
        /// Cleanup failure is logged and does not replace the original startup exception.
        /// </remarks>
        internal async Task StartAsync(CancellationToken cancellationToken)
        {
            var options = _options.Value;
            var fqdn = options.Fqdn;
            if (string.IsNullOrWhiteSpace(fqdn))
            {
                throw new InvalidOperationException(
                    "Generated FQDN is empty; ServerId and DnsSuffix must be validated before DNS reconciliation.");
            }

            if (string.IsNullOrWhiteSpace(options.CloudFlareZoneId))
            {
                throw new InvalidOperationException(
                    $"{AcmeCloudflareOptions.CloudFlareZoneIdConfigurationKey} is required for DNS reconciliation.");
            }

            CloudflareLogMessages.StartingReconciliation(_logger, fqdn, options.CloudFlareZoneId);

            var resolved = ResolveEligibleAddresses(options);
            if (_publicationLease is { PublishDuringStart: false })
            {
                CloudflareLogMessages.PublicationDeferredUntilListenerBound(_logger, fqdn);
                return;
            }

            await PublishCoreAsync(options.CloudFlareZoneId, fqdn, resolved, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Removes every DNS record for the exact FQDN when this process completed a successful reconcile.
        /// </summary>
        /// <param name="cancellationToken">Passed to cleanup. Cooperative with the caller's shutdown budget.</param>
        /// <returns>A task that completes when cleanup is skipped or Cloudflare verifies the name is empty.</returns>
        /// <exception cref="InvalidOperationException">
        /// Ownership was active but the FQDN or zone id is now missing.
        /// </exception>
        /// <remarks>
        /// When ownership is inactive, cleanup is skipped and this method returns without calling Cloudflare.
        /// </remarks>
        internal async Task StopAsync(CancellationToken cancellationToken)
        {
            // Runs during normal shutdown and startup rollback while lifecycle may already be Stopping.
            // Do not skip clean-up merely because ownership tracking or lifecycle state changed.
            if (Interlocked.Exchange(ref _fqdnOwnershipActive, 0) == 0)
            {
                CloudflareLogMessages.CleanupSkippedOwnershipInactive(_logger);
                return;
            }

            var options = _options.Value;
            var fqdn = options.Fqdn;
            if (string.IsNullOrWhiteSpace(fqdn) || string.IsNullOrWhiteSpace(options.CloudFlareZoneId))
            {
                throw new InvalidOperationException(
                    "Cannot clean up Cloudflare DNS: FQDN or CloudFlareZoneId is missing after ownership was active.");
            }

            CloudflareLogMessages.StoppingReconciliation(_logger, fqdn);

            await _reconciler
                .RemoveAllRecordsForFqdnAsync(options.CloudFlareZoneId, fqdn, cancellationToken)
                .ConfigureAwait(false);

            CloudflareLogMessages.CleanupCompleted(_logger, fqdn);
        }

        /// <summary>
        /// Publishes A/AAAA after the caller has bound its listener, and marks FQDN ownership only after verification.
        /// </summary>
        /// <param name="cancellationToken">Cancels publication. Cancellation after the attempt begins deletes only ids created by that attempt.</param>
        /// <returns>A task that completes when Cloudflare has verified the desired address set, or when this process already owns the name.</returns>
        /// <exception cref="InvalidOperationException">The FQDN, zone, or eligible address set is missing.</exception>
        /// <remarks>
        /// A second call after ownership is active returns without another reconcile. If publication fails before
        /// ownership is set, cleanup deletes record ids that were absent before the attempt and leaves every
        /// pre-existing id in place. It does not call <see cref="ICloudflareDnsReconciler.RemoveAllRecordsForFqdnAsync"/>.
        /// </remarks>
        /// <inheritdoc />
        Task IListenerDnsPublication.PublishAfterListenerBoundAsync(CancellationToken cancellationToken) =>
            PublishAfterListenerBoundAsync(cancellationToken);

        /// <summary>
        /// Publishes A/AAAA after the listener has bound, or returns when this process already owns the FQDN.
        /// </summary>
        /// <param name="cancellationToken">Cancels publication. Cancellation after the attempt begins deletes only ids created by that attempt.</param>
        /// <exception cref="InvalidOperationException">The FQDN, zone, or eligible address set is missing.</exception>
        internal async Task PublishAfterListenerBoundAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _fqdnOwnershipActive) == 1)
            {
                return;
            }

            var (zoneId, fqdn, resolved) = ValidateForPublication();
            await PublishCoreAsync(zoneId, fqdn, resolved, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Reconciles one publication and marks ownership only after verification.
        /// A failure before pre-existing records are deleted removes only ids created by the attempt.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="fqdn">Exact FQDN.</param>
        /// <param name="resolved">Eligible addresses to publish.</param>
        /// <param name="cancellationToken">Cancels the publication.</param>
        private async Task PublishCoreAsync(
            string zoneId,
            string fqdn,
            ResolvedBindAddresses resolved,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<string>? before = null;
            try
            {
                before = await _reconciler
                    .ListRecordIdsAsync(zoneId, fqdn, cancellationToken)
                    .ConfigureAwait(false);
                await _reconciler
                    .ReconcileAsync(zoneId, fqdn, resolved, cancellationToken)
                    .ConfigureAwait(false);
                Volatile.Write(ref _fqdnOwnershipActive, 1);
                CloudflareLogMessages.ReconciliationCompleted(_logger, fqdn, resolved.All.Count);
            }
            catch (Exception ex) when (CanCompensateFailedPublication(before, ex))
            {
                await TryDeleteRecordsCreatedByThisAttemptAsync(zoneId, fqdn, before!, ex).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (before is not null && !_reconciler.DeletedPreExistingDuringLastReconcile)
            {
                await TryDeleteRecordsCreatedByThisAttemptAsync(zoneId, fqdn, before, cancellationException: true)
                    .ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Returns whether a failed publication may delete only the ids it created.
        /// </summary>
        /// <param name="before">Ids observed before reconcile. Null when the snapshot itself failed.</param>
        /// <param name="exception">The publication failure.</param>
        /// <returns>
        /// <see langword="false"/> when the snapshot is missing, the caller canceled after stale deletion began,
        /// or reconciliation already deleted a pre-existing record.
        /// </returns>
        private bool CanCompensateFailedPublication(IReadOnlyList<string>? before, Exception exception) =>
            before is not null
            && exception is not OperationCanceledException
            && !_reconciler.DeletedPreExistingDuringLastReconcile;

        /// <summary>
        /// Validates the FQDN, zone, and eligible bind addresses used for publication.
        /// </summary>
        /// <returns>The zone id, exact FQDN, and resolved addresses.</returns>
        /// <exception cref="InvalidOperationException">Any required value is missing or the address set is empty.</exception>
        private (string ZoneId, string Fqdn, ResolvedBindAddresses Resolved) ValidateForPublication()
        {
            var options = _options.Value;
            var fqdn = options.Fqdn;
            if (string.IsNullOrWhiteSpace(fqdn))
            {
                throw new InvalidOperationException(
                    "Generated FQDN is empty; ServerId and DnsSuffix must be validated before DNS reconciliation.");
            }

            if (string.IsNullOrWhiteSpace(options.CloudFlareZoneId))
            {
                throw new InvalidOperationException(
                    $"{AcmeCloudflareOptions.CloudFlareZoneIdConfigurationKey} is required for DNS reconciliation.");
            }

            return (options.CloudFlareZoneId, fqdn, ResolveEligibleAddresses(options));
        }

        /// <summary>Resolves bind addresses and fails when none are eligible for publication.</summary>
        /// <param name="options">Options whose bind addresses are resolved.</param>
        /// <returns>The eligible address set.</returns>
        /// <exception cref="InvalidOperationException">The resolved set is empty.</exception>
        private ResolvedBindAddresses ResolveEligibleAddresses(AcmeCloudflareOptions options)
        {
            var resolved = _bindAddressResolver.Resolve(options);
            if (!resolved.HasAny)
            {
                throw new InvalidOperationException(
                    "No eligible IP addresses were obtained from BindAddress. " +
                    "Wildcard binding requires at least one non-loopback, non-link-local, non-multicast " +
                    "unicast address on a local interface; explicit entries must be eligible for DNS publication. " +
                    "Startup cannot continue with an empty address set.");
            }

            return resolved;
        }

        /// <summary>
        /// Deletes record ids that appeared during a failed publication and leaves every pre-existing id.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="fqdn">Exact FQDN.</param>
        /// <param name="idsBeforeAttempt">Ids present before this process called reconcile.</param>
        /// <param name="original">Publication failure. Null when publication was canceled.</param>
        /// <param name="cancellationException"><see langword="true"/> when publication was canceled.</param>
        /// <remarks>
        /// Does not call <see cref="ICloudflareDnsReconciler.RemoveAllRecordsForFqdnAsync"/>. A list failure
        /// before the snapshot leaves <paramref name="idsBeforeAttempt"/> unused only when the caller skips
        /// this method. Cleanup failure is logged and does not replace the original exception.
        /// </remarks>
        private async Task TryDeleteRecordsCreatedByThisAttemptAsync(
            string zoneId,
            string fqdn,
            IReadOnlyList<string> idsBeforeAttempt,
            Exception? original = null,
            bool cancellationException = false)
        {
            try
            {
                CloudflareLogMessages.PostFailureCleanupAttempt(
                    _logger,
                    original,
                    fqdn,
                    cancellationException);

                using var cleanupCts = new CancellationTokenSource(FailedStartCleanupTimeout);
                var after = await _reconciler
                    .ListRecordIdsAsync(zoneId, fqdn, cleanupCts.Token)
                    .ConfigureAwait(false);
                var before = new HashSet<string>(idsBeforeAttempt, StringComparer.Ordinal);
                var created = after.Where(id => !before.Contains(id)).ToList();

                await _reconciler
                    .DeleteRecordIdsAsync(
                        zoneId,
                        fqdn,
                        created,
                        cleanupCts.Token,
                        operationTimeout: FailedStartCleanupTimeout)
                    .ConfigureAwait(false);
                CloudflareLogMessages.PostFailureCleanupVerified(_logger, fqdn);
            }
            catch (OperationCanceledException cleanupEx)
            {
                CloudflareLogMessages.PostFailureCleanupTimedOut(
                    _logger,
                    cleanupEx,
                    fqdn,
                    FailedStartCleanupTimeout);
            }
            catch (Exception cleanupEx) when (!ExceptionHelpers.IsFatal(cleanupEx))
            {
                CloudflareLogMessages.PostFailureCleanupFailed(_logger, cleanupEx, fqdn);
            }
        }
    }
}

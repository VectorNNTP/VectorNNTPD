using System.Net;
using System.Net.Sockets;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.Common.Cloudflare
{
    /// <summary>
    /// Idempotent reconciler that makes Cloudflare A/AAAA records for a single FQDN match a desired IP set,
    /// and removes every DNS record for that exact FQDN on clean-up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the exact FQDN is mutated. Unrelated hostnames and record types outside startup A/AAAA
    /// reconciliation are untouched during reconcile. Shutdown clean-up deletes all record types for the
    /// exact FQDN only (never parent, child/subdomain, or other names).
    /// Private (RFC1918 / ULA) addresses in the desired set are published intentionally.
    /// </para>
    /// <para>
    /// Cloudflare does not provide an atomic multi-record transaction. Each reconcile attempt:
    /// reads both families, creates all missing desired records across A and AAAA before deleting any stale
    /// records, then deletes stale/duplicates, then verifies exact sets (including TTL
    /// <see cref="CloudflareManagedDnsPolicy.ManagedTtl"/> and <c>proxied=false</c>).
    /// Clean-up lists all types for the exact name, deletes by record id, then verifies none remain.
    /// Concurrent reconcile/clean-up calls on the same instance are serialized. Intermediate states remain
    /// externally visible. Permanent auth/config failures fail immediately; transient/uncertain/verification
    /// failures retry with bounded backoff after re-reading remote state. HTTP 429 retries and reconciler
    /// attempt backoffs share a single operation deadline (<c>CloudFlareOperationTimeout</c>, default 2 minutes)
    /// linked with the caller token.
    /// </para>
    /// </remarks>
    internal sealed class CloudflareDnsReconciler : ICloudflareDnsReconciler
    {
        /// <summary>Maximum reconcile attempts within a single <see cref="ReconcileAsync"/> call.</summary>
        internal const int MaxAttempts = 3;

        /// <summary>Cloudflare DNS record API used for list, create, update, and delete.</summary>
        private readonly ICloudflareDnsClient _client;

        /// <summary>Supplies <see cref="AcmeCloudflareOptions.CloudFlareOperationTimeout"/> when a call does not pass its own budget.</summary>
        private readonly IOptions<AcmeCloudflareOptions> _options;

        /// <summary>Reconcile and cleanup diagnostics.</summary>
        private readonly ILogger<CloudflareDnsReconciler> _logger;

        /// <summary>
        /// Serializes <see cref="ReconcileAsync"/> and <see cref="RemoveAllRecordsForFqdnAsync"/> on this instance.
        /// The wait observes the caller token, so a canceled waiter does not enter the operation.
        /// </summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>
        /// Set when the in-flight reconcile begins deleting records that were already present.
        /// The reconciliation service reads this after <see cref="ReconcileAsync"/> throws.
        /// </summary>
        private bool _deletedPreExistingDuringLastReconcile;

        /// <summary>
        /// Gets or sets the delay function used for reconciler attempt backoff (tests may replace this).
        /// </summary>
        internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } =
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

        /// <summary>
        /// Initializes a new instance of the <see cref="CloudflareDnsReconciler"/> class.
        /// </summary>
        /// <param name="client">DNS record API client.</param>
        /// <param name="options">Options that supply the default operation timeout.</param>
        /// <param name="logger">Reconciler logger.</param>
        public CloudflareDnsReconciler(
            ICloudflareDnsClient client,
            IOptions<AcmeCloudflareOptions> options,
            ILogger<CloudflareDnsReconciler> logger)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);
            _client = client;
            _options = options;
            _logger = logger;
        }

        /// <inheritdoc />
        public bool DeletedPreExistingDuringLastReconcile => _deletedPreExistingDuringLastReconcile;

        /// <inheritdoc />
        public async Task ReconcileAsync(
            string zoneId,
            string fqdn,
            ResolvedBindAddresses desired,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
            ArgumentNullException.ThrowIfNull(desired);

            if (!desired.HasAny)
            {
                throw new InvalidOperationException(
                    "Cannot reconcile Cloudflare DNS with an empty bind-address set. " +
                    "Configure BindAddress so at least one eligible non-loopback, non-link-local IP is available.");
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _deletedPreExistingDuringLastReconcile = false;
                using var budget = CloudflareOperationBudget.Begin(
                    _options.Value.CloudFlareOperationTimeout,
                    cancellationToken,
                    out var operationToken);

                var desiredV4 = ToContentSet(desired.IPv4);
                var desiredV6 = ToContentSet(desired.IPv6);

                CloudflareLogMessages.Reconciling(
                    _logger,
                    fqdn,
                    desiredV4.Count,
                    desiredV6.Count,
                    CloudflareManagedDnsPolicy.ManagedTtl);

                Exception? lastFailure = null;

                for (var attempt = 1; attempt <= MaxAttempts; attempt++)
                {
                    operationToken.ThrowIfCancellationRequested();

                    try
                    {
                        await ReconcileAttemptAsync(zoneId, fqdn, desiredV4, desiredV6, attempt, operationToken)
                            .ConfigureAwait(false);

                        CloudflareLogMessages.ReconciliationVerified(
                            _logger,
                            fqdn,
                            attempt,
                            MaxAttempts,
                            desiredV4.Count,
                            desiredV6.Count);
                        return;
                    }
                    catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
                    {
                        CloudflareLogMessages.ReconciliationCanceled(_logger, fqdn, attempt);
                        throw;
                    }
                    catch (CloudflareDnsException ex) when (ex.IsPermanentFailure)
                    {
                        CloudflareLogMessages.ReconciliationPermanentFailure(
                            _logger,
                            ex,
                            fqdn,
                            attempt,
                            ex.FailedOperation,
                            ex.StatusCode);
                        throw;
                    }
                    catch (Exception ex) when (ex is CloudflareDnsException or InvalidOperationException)
                    {
                        lastFailure = ex;
                        var uncertain = ex is CloudflareDnsException { IsOutcomeUncertain: true };

                        CloudflareLogMessages.ReconciliationAttemptFailed(
                            _logger,
                            ex,
                            attempt,
                            MaxAttempts,
                            fqdn,
                            uncertain,
                            (ex as CloudflareDnsException)?.FailedOperation);

                        if (attempt >= MaxAttempts)
                        {
                            break;
                        }

                        var delay = GetAttemptBackoff(attempt);
                        CloudflareLogMessages.ReconciliationRetryWait(
                            _logger,
                            delay.TotalMilliseconds,
                            attempt + 1,
                            MaxAttempts,
                            fqdn);
                        await DelayRespectingBudgetAsync(delay, operationToken).ConfigureAwait(false);
                    }
                }

                throw new CloudflareDnsException(
                    $"Cloudflare DNS reconciliation for '{fqdn}' failed after {MaxAttempts} attempt(s). " +
                    "Startup must not proceed: desired A/AAAA sets were not verified. " +
                    "Remote DNS may reflect an intermediate non-atomic multi-record state; " +
                    "the next successful reconcile will re-read and converge.",
                    lastFailure ?? new InvalidOperationException("Unknown reconciliation failure."))
                {
                    IsOutcomeUncertain = lastFailure is CloudflareDnsException { IsOutcomeUncertain: true },
                    IsPermanentFailure = lastFailure is CloudflareDnsException { IsPermanentFailure: true },
                    FailedOperation = "ReconcileAsync",
                    StatusCode = (lastFailure as CloudflareDnsException)?.StatusCode,
                    CloudflareErrorCodes = (lastFailure as CloudflareDnsException)?.CloudflareErrorCodes ?? [],
                    DeletedPreExistingRecords = _deletedPreExistingDuringLastReconcile,
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public async Task RemoveAllRecordsForFqdnAsync(
            string zoneId,
            string fqdn,
            CancellationToken cancellationToken,
            TimeSpan? operationTimeout = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var timeout = operationTimeout ?? _options.Value.CloudFlareOperationTimeout;
                using var budget = CloudflareOperationBudget.Begin(timeout, cancellationToken, out var operationToken);

                CloudflareLogMessages.RemovingAllRecords(_logger, fqdn);

                Exception? lastFailure = null;

                for (var attempt = 1; attempt <= MaxAttempts; attempt++)
                {
                    operationToken.ThrowIfCancellationRequested();

                    try
                    {
                        await RemoveAllAttemptAsync(zoneId, fqdn, attempt, operationToken).ConfigureAwait(false);

                        CloudflareLogMessages.CleanupVerified(_logger, fqdn, attempt, MaxAttempts);
                        return;
                    }
                    catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
                    {
                        CloudflareLogMessages.CleanupCanceled(_logger, fqdn, attempt);
                        throw;
                    }
                    catch (CloudflareDnsException ex) when (ex.IsPermanentFailure)
                    {
                        CloudflareLogMessages.CleanupPermanentFailure(
                            _logger,
                            ex,
                            fqdn,
                            attempt,
                            ex.FailedOperation,
                            ex.StatusCode);
                        throw;
                    }
                    catch (Exception ex) when (ex is CloudflareDnsException or InvalidOperationException)
                    {
                        lastFailure = ex;
                        var uncertain = ex is CloudflareDnsException { IsOutcomeUncertain: true };

                        CloudflareLogMessages.CleanupAttemptFailed(
                            _logger,
                            ex,
                            attempt,
                            MaxAttempts,
                            fqdn,
                            uncertain,
                            (ex as CloudflareDnsException)?.FailedOperation);

                        if (attempt >= MaxAttempts)
                        {
                            break;
                        }

                        var delay = GetAttemptBackoff(attempt);
                        CloudflareLogMessages.CleanupRetryWait(
                            _logger,
                            delay.TotalMilliseconds,
                            attempt + 1,
                            MaxAttempts,
                            fqdn);
                        await DelayRespectingBudgetAsync(delay, operationToken).ConfigureAwait(false);
                    }
                }

                throw new CloudflareDnsException(
                    $"Cloudflare DNS cleanup for '{fqdn}' failed after {MaxAttempts} attempt(s). " +
                    "The FQDN is not claimed removed; remote DNS may still contain records for this exact name. " +
                    "Forced process termination may also leave records behind.",
                    lastFailure ?? new InvalidOperationException("Unknown cleanup failure."))
                {
                    IsOutcomeUncertain = lastFailure is CloudflareDnsException { IsOutcomeUncertain: true },
                    IsPermanentFailure = lastFailure is CloudflareDnsException { IsPermanentFailure: true },
                    FailedOperation = "RemoveAllRecordsForFqdnAsync",
                    StatusCode = (lastFailure as CloudflareDnsException)?.StatusCode,
                    CloudflareErrorCodes = (lastFailure as CloudflareDnsException)?.CloudflareErrorCodes ?? [],
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<string>> ListRecordIdsAsync(
            string zoneId,
            string fqdn,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var records = await ListExactFqdnRecordsAsync(zoneId, fqdn, cancellationToken).ConfigureAwait(false);
                var ids = new List<string>(records.Count);
                // codeql[cs/linq/missed-where] Gate-held list projection; Where would allocate under SemaphoreSlim.
                foreach (var record in records)
                {
                    if (!string.IsNullOrWhiteSpace(record.Id))
                    {
                        ids.Add(record.Id);
                    }
                }

                return ids;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public async Task DeleteRecordIdsAsync(
            string zoneId,
            string fqdn,
            IReadOnlyCollection<string> recordIds,
            CancellationToken cancellationToken,
            TimeSpan? operationTimeout = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
            ArgumentNullException.ThrowIfNull(recordIds);
            if (recordIds.Count == 0)
            {
                return;
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var timeout = operationTimeout ?? _options.Value.CloudFlareOperationTimeout;
                using var budget = CloudflareOperationBudget.Begin(timeout, cancellationToken, out var operationToken);
                var requested = new HashSet<string>(recordIds, StringComparer.Ordinal);
                var existing = await ListExactFqdnRecordsAsync(zoneId, fqdn, operationToken).ConfigureAwait(false);
                foreach (var record in existing)
                {
                    operationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(record.Id) || !requested.Contains(record.Id))
                    {
                        continue;
                    }

                    CloudflareLogMessages.DeletingRecordDuringCleanup(_logger, record.Type, record.Id, fqdn);
                    await _client.DeleteRecordAsync(zoneId, record.Id, operationToken).ConfigureAwait(false);
                }

                var remaining = await ListExactFqdnRecordsAsync(zoneId, fqdn, operationToken).ConfigureAwait(false);
                foreach (var record in remaining)
                {
                    if (requested.Contains(record.Id))
                    {
                        throw new CloudflareDnsException(
                            $"Cloudflare DNS cleanup for '{fqdn}' still contains record {record.Type}:{record.Id} " +
                            "after deleting only the ids created by this process.")
                        {
                            FailedOperation = "DeleteRecordIds",
                        };
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Lists exact-FQDN records, deletes each by id, then lists again and fails if any remain.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="fqdn">Exact ownership boundary.</param>
        /// <param name="attempt">One-based cleanup attempt, logged with the listed count.</param>
        /// <param name="cancellationToken">Operation token. Checked before each delete.</param>
        /// <exception cref="CloudflareDnsException">
        /// A listed record has no id, or verification still sees records. A missing id is permanent.
        /// Verification failure is not marked permanent, so the caller may retry.
        /// </exception>
        private async Task RemoveAllAttemptAsync(
            string zoneId,
            string fqdn,
            int attempt,
            CancellationToken cancellationToken)
        {
            var existing = await ListExactFqdnRecordsAsync(zoneId, fqdn, cancellationToken).ConfigureAwait(false);

            CloudflareLogMessages.CleanupAttempt(_logger, attempt, existing.Count, fqdn);

            foreach (var record in existing)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(record.Id))
                {
                    throw new CloudflareDnsException(
                        $"Cloudflare DNS cleanup for '{fqdn}' cannot delete a record without an id " +
                        $"(type={record.Type}). Failing safely without inventing identifiers")
                    {
                        FailedOperation = "Delete",
                        IsPermanentFailure = true,
                    };
                }

                CloudflareLogMessages.DeletingRecordDuringCleanup(_logger, record.Type, record.Id, fqdn);

                await _client.DeleteRecordAsync(zoneId, record.Id, cancellationToken).ConfigureAwait(false);
            }

            var remaining = await ListExactFqdnRecordsAsync(zoneId, fqdn, cancellationToken).ConfigureAwait(false);
            if (remaining.Count > 0)
            {
                var summary = string.Join(
                    ", ",
                    remaining.Select(static r => $"{r.Type}:{r.Id}").Order(StringComparer.Ordinal));

                throw new CloudflareDnsException(
                    $"Cloudflare DNS cleanup verification failed for '{fqdn}': " +
                    $"{remaining.Count} record(s) still present after delete attempts [{summary}]. " +
                    "Cleanup must not be treated as successful.")
                {
                    FailedOperation = "VerifyCleanup",
                };
            }
        }

        /// <summary>
        /// Lists every record type for <paramref name="fqdn"/> and keeps only names classified as an exact match.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="fqdn">Expected exact name.</param>
        /// <param name="cancellationToken">Cancels the list.</param>
        /// <returns>Records whose normalized name equals <paramref name="fqdn"/>.</returns>
        /// <exception cref="CloudflareDnsException">
        /// A returned name cannot be classified. That failure is permanent and deletes nothing.
        /// Names that classify as non-exact are skipped and logged.
        /// </exception>
        private async Task<IReadOnlyList<CloudflareDnsRecord>> ListExactFqdnRecordsAsync(
            string zoneId,
            string fqdn,
            CancellationToken cancellationToken)
        {
            var normalizedExpected = NormalizeFqdnName(fqdn);
            var listed = await _client
                .ListAllRecordsForNameAsync(zoneId, fqdn, cancellationToken)
                .ConfigureAwait(false);

            var exact = new List<CloudflareDnsRecord>(listed.Count);
            foreach (var record in listed)
            {
                if (!TryClassifyExactFqdn(record.Name, normalizedExpected, out var isExact))
                {
                    throw new CloudflareDnsException(
                        $"Cloudflare DNS returned an ambiguous record name for cleanup of '{fqdn}' " +
                        $"(record id={record.Id}, type={record.Type}). " +
                        "Failing safely without deleting records outside the exact-FQDN ownership boundary.")
                    {
                        FailedOperation = "ListAllRecordsForName",
                        IsPermanentFailure = true,
                    };
                }

                if (isExact)
                {
                    exact.Add(record);
                }
                else
                {
                    // API name filters can be imperfect; never delete non-exact names.
                    CloudflareLogMessages.SkippingNonExactCleanupRecord(_logger, record.Id, record.Type, fqdn);
                }
            }

            return exact;
        }

        /// <summary>
        /// Classifies whether a Cloudflare record name is exactly the expected FQDN.
        /// Returns <see langword="false"/> when the name cannot be classified safely.
        /// </summary>
        /// <param name="recordName">Name returned by Cloudflare.</param>
        /// <param name="normalizedExpected">Expected name already trimmed, de-dotted, and lowercased.</param>
        /// <param name="isExactMatch">
        /// <see langword="true"/> only when classification succeeds and the normalized names are equal.
        /// </param>
        /// <returns>
        /// <see langword="false"/> for blank input, a name that normalizes to empty, or a name containing an empty label.
        /// </returns>
        internal static bool TryClassifyExactFqdn(string? recordName, string normalizedExpected, out bool isExactMatch)
        {
            isExactMatch = false;
            if (string.IsNullOrWhiteSpace(recordName) || string.IsNullOrWhiteSpace(normalizedExpected))
            {
                return false;
            }

            var normalized = NormalizeFqdnName(recordName);
            if (normalized.Length == 0)
            {
                return false;
            }

            // Empty labels (e.g. "a..b.example") are treated as ambiguous rather than deletable.
            if (normalized.Contains("..", StringComparison.Ordinal))
            {
                return false;
            }

            isExactMatch = string.Equals(normalized, normalizedExpected, StringComparison.Ordinal);
            return true;
        }

        /// <summary>Trims <paramref name="fqdn"/>, removes one trailing dot, and lowercases it with the invariant culture.</summary>
        /// <param name="fqdn">DNS name from configuration or a Cloudflare record.</param>
        /// <returns>The normalized name used for exact comparisons.</returns>
        private static string NormalizeFqdnName(string fqdn) =>
            fqdn.Trim().TrimEnd('.').ToLowerInvariant();

        /// <summary>
        /// Copies kept records whose TTL or proxy state is not yet managed, so a failed attempt can restore them.
        /// </summary>
        /// <param name="keptA">Existing A records that match a desired address.</param>
        /// <param name="keptAaaa">Existing AAAA records that match a desired address.</param>
        /// <returns>Records that <see cref="EnsureManagedAttributesAsync"/> may update.</returns>
        private static List<(string Type, CloudflareDnsRecord Record)> RecordsNeedingManagedUpdate(
            IReadOnlyList<CloudflareDnsRecord> keptA,
            IReadOnlyList<CloudflareDnsRecord> keptAaaa)
        {
            var restore = new List<(string Type, CloudflareDnsRecord Record)>();
            CollectUnmanaged(keptA, CloudflareDnsRecordTypes.A, restore);
            CollectUnmanaged(keptAaaa, CloudflareDnsRecordTypes.AAAA, restore);
            return restore;
        }

        /// <summary>Appends records that do not already have managed TTL and proxy state.</summary>
        /// <param name="records">Kept desired records for one family.</param>
        /// <param name="type">Record type stored with each snapshot.</param>
        /// <param name="restore">Destination snapshot.</param>
        private static void CollectUnmanaged(
            IReadOnlyList<CloudflareDnsRecord> records,
            string type,
            List<(string Type, CloudflareDnsRecord Record)> restore)
        {
            // codeql[cs/linq/missed-where] Gate-held restore snapshot; Where would allocate under SemaphoreSlim.
            foreach (var record in records)
            {
                if (!CloudflareManagedDnsPolicy.MatchesManagedAttributes(record))
                {
                    restore.Add((type, record));
                }
            }
        }

        /// <summary>
        /// Writes the snapshotted TTL and proxy state back onto records this attempt updated.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="fqdn">Record name.</param>
        /// <param name="restore">Records captured before the managed-attribute update.</param>
        /// <param name="cancellationToken">Cancels the restore. A restore failure is logged and does not replace the original exception.</param>
        /// <remarks>
        /// A record whose previous TTL or proxy flag was absent cannot be restored faithfully and is left as updated.
        /// This runs only when pre-existing records have not yet been deleted.
        /// </remarks>
        private async Task RestoreManagedAttributesAsync(
            string zoneId,
            string fqdn,
            List<(string Type, CloudflareDnsRecord Record)> restore,
            CancellationToken cancellationToken)
        {
            foreach (var (type, record) in restore)
            {
                if (record.Ttl is not int ttl || record.Proxied is not bool proxied || string.IsNullOrWhiteSpace(record.Id))
                {
                    continue;
                }

                var content = NormalizeContent(record.Content, type);
                if (content is null)
                {
                    continue;
                }

                try
                {
                    await _client.UpdateRecordAsync(
                            zoneId,
                            record.Id,
                            new CloudflareDnsRecordWriteRequest
                            {
                                Type = type,
                                Name = fqdn,
                                Content = content,
                                Ttl = ttl,
                                Proxied = proxied,
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    CloudflareLogMessages.ReconciliationAttemptFailed(
                        _logger,
                        ex,
                        0,
                        MaxAttempts,
                        fqdn,
                        true,
                        "RestoreManagedAttributes");
                }
            }
        }

        /// <summary>
        /// Lists A and AAAA and requires every desired address to be present with managed attributes.
        /// Extra records are allowed; they are removed only after this check succeeds.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="fqdn">Exact FQDN.</param>
        /// <param name="desiredV4">Desired IPv4 contents.</param>
        /// <param name="desiredV6">Desired IPv6 contents.</param>
        /// <param name="cancellationToken">Cancels the lists.</param>
        private async Task VerifyDesiredPresentAsync(
            string zoneId,
            string fqdn,
            HashSet<string> desiredV4,
            HashSet<string> desiredV6,
            CancellationToken cancellationToken)
        {
            var actualA = await _client
                .ListRecordsAsync(zoneId, fqdn, CloudflareDnsRecordTypes.A, cancellationToken)
                .ConfigureAwait(false);
            var actualAaaa = await _client
                .ListRecordsAsync(zoneId, fqdn, CloudflareDnsRecordTypes.AAAA, cancellationToken)
                .ConfigureAwait(false);
            AssertDesiredPresent(fqdn, CloudflareDnsRecordTypes.A, desiredV4, actualA);
            AssertDesiredPresent(fqdn, CloudflareDnsRecordTypes.AAAA, desiredV6, actualAaaa);
        }

        /// <summary>Fails when a desired address is missing or its record is not yet managed. Extra records do not fail this check.</summary>
        /// <param name="fqdn">FQDN included in the failure message.</param>
        /// <param name="type">Address family being checked.</param>
        /// <param name="desired">Desired normalized contents.</param>
        /// <param name="actual">Records returned by the presence list.</param>
        private static void AssertDesiredPresent(
            string fqdn,
            string type,
            HashSet<string> desired,
            IReadOnlyList<CloudflareDnsRecord> actual)
        {
            foreach (var content in desired)
            {
                var match = false;
                foreach (var record in actual)
                {
                    if (!string.Equals(NormalizeContent(record.Content, type), content, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!CloudflareManagedDnsPolicy.MatchesManagedAttributes(record))
                    {
                        var detail = record.Proxied != CloudflareManagedDnsPolicy.ManagedProxied
                            ? $"record {record.Id} proxy state is not DNS-only (proxied=false)"
                            : $"record {record.Id} TTL is not {CloudflareManagedDnsPolicy.ManagedTtl}";
                        throw new CloudflareDnsException(
                            $"Cloudflare DNS verification failed for '{fqdn}' ({type}): {detail}. " +
                            "Stale records are not deleted until every desired record is managed.")
                        {
                            FailedOperation = "VerifyDesiredPresent",
                        };
                    }

                    match = true;
                    break;
                }

                if (!match)
                {
                    throw new CloudflareDnsException(
                        $"Cloudflare DNS verification failed for '{fqdn}' ({type}): " +
                        $"desired record '{content}' is not present. " +
                        "Stale records are not deleted until every desired record exists.")
                    {
                        FailedOperation = "VerifyDesiredPresent",
                    };
                }
            }
        }

        /// <summary>
        /// One non-atomic reconcile pass: read A and AAAA, create missing records, update managed TTL and proxy,
        /// delete stale, duplicate, and unparseable records, then re-read and verify.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="fqdn">Exact FQDN.</param>
        /// <param name="desiredV4">Desired IPv4 contents from <c>IpAddressEligibility.ToDnsContent</c>.</param>
        /// <param name="desiredV6">Desired IPv6 contents from <c>IpAddressEligibility.ToDnsContent</c>.</param>
        /// <param name="attempt">One-based attempt number logged with the plan counts.</param>
        /// <param name="cancellationToken">Operation token. Checked before each mutation.</param>
        private async Task ReconcileAttemptAsync(
            string zoneId,
            string fqdn,
            HashSet<string> desiredV4,
            HashSet<string> desiredV6,
            int attempt,
            CancellationToken cancellationToken)
        {
            // Stage 1: read current state for both families.
            var existingA = await _client
                .ListRecordsAsync(zoneId, fqdn, CloudflareDnsRecordTypes.A, cancellationToken)
                .ConfigureAwait(false);
            var existingAaaa = await _client
                .ListRecordsAsync(zoneId, fqdn, CloudflareDnsRecordTypes.AAAA, cancellationToken)
                .ConfigureAwait(false);

            var planA = BuildPlan(existingA, CloudflareDnsRecordTypes.A, desiredV4);
            var planAaaa = BuildPlan(existingAaaa, CloudflareDnsRecordTypes.AAAA, desiredV6);

            CloudflareLogMessages.ReconcileAttemptPlan(
                _logger,
                attempt,
                planA.Missing.Count,
                planA.Stale.Count,
                planA.DuplicateExtras.Count,
                planAaaa.Missing.Count,
                planAaaa.Stale.Count,
                planAaaa.DuplicateExtras.Count);

            // Stage 2: create all missing desired records across both families before any deletes.
            await CreateMissingAsync(zoneId, fqdn, CloudflareDnsRecordTypes.A, planA.Missing, cancellationToken)
                .ConfigureAwait(false);
            await CreateMissingAsync(zoneId, fqdn, CloudflareDnsRecordTypes.AAAA, planAaaa.Missing, cancellationToken)
                .ConfigureAwait(false);

            // Stage 3: ensure kept desired records match managed TTL and DNS-only proxy state.
            var restore = RecordsNeedingManagedUpdate(planA.KeptDesired, planAaaa.KeptDesired);
            try
            {
                await EnsureManagedAttributesAsync(zoneId, fqdn, CloudflareDnsRecordTypes.A, planA.KeptDesired, cancellationToken)
                    .ConfigureAwait(false);
                await EnsureManagedAttributesAsync(zoneId, fqdn, CloudflareDnsRecordTypes.AAAA, planAaaa.KeptDesired, cancellationToken)
                    .ConfigureAwait(false);

                // Stage 4: desired records must be present before any pre-existing record is deleted.
                await VerifyDesiredPresentAsync(zoneId, fqdn, desiredV4, desiredV6, cancellationToken)
                    .ConfigureAwait(false);

                // Stage 5: delete stale records only after the desired set is present.
                if (planA.RecordsToDelete.Count > 0 || planAaaa.RecordsToDelete.Count > 0)
                {
                    _deletedPreExistingDuringLastReconcile = true;
                }

                await DeleteRecordsAsync(zoneId, fqdn, CloudflareDnsRecordTypes.A, planA.RecordsToDelete, cancellationToken)
                    .ConfigureAwait(false);
                await DeleteRecordsAsync(zoneId, fqdn, CloudflareDnsRecordTypes.AAAA, planAaaa.RecordsToDelete, cancellationToken)
                    .ConfigureAwait(false);

                // Stage 6: re-read and verify the exact final sets.
                await VerifyAsync(zoneId, fqdn, desiredV4, desiredV6, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                if (!_deletedPreExistingDuringLastReconcile)
                {
                    await RestoreManagedAttributesAsync(zoneId, fqdn, restore, cancellationToken).ConfigureAwait(false);
                }

                throw;
            }
        }

        /// <summary>
        /// Creates one A or AAAA record per missing content, with managed TTL and DNS-only proxy.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="fqdn">Record name.</param>
        /// <param name="type"><see cref="CloudflareDnsRecordTypes.A"/> or <see cref="CloudflareDnsRecordTypes.AAAA"/>.</param>
        /// <param name="missingContents">Desired contents that had no matching record.</param>
        /// <param name="cancellationToken">Operation token. Checked before each create.</param>
        private async Task CreateMissingAsync(
            string zoneId,
            string fqdn,
            string type,
            IReadOnlyList<string> missingContents,
            CancellationToken cancellationToken)
        {
            foreach (var content in missingContents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CloudflareLogMessages.CreatingRecord(_logger, type, fqdn, content);
                await _client.CreateRecordAsync(
                        zoneId,
                        new CloudflareDnsRecordWriteRequest
                        {
                            Type = type,
                            Name = fqdn,
                            Content = content,
                            Ttl = CloudflareManagedDnsPolicy.ManagedTtl,
                            Proxied = CloudflareManagedDnsPolicy.ManagedProxied,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Updates kept desired records whose TTL or proxy flag is not the managed value.
        /// Records that already match are left unchanged.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="fqdn">Record name written on update.</param>
        /// <param name="type"><see cref="CloudflareDnsRecordTypes.A"/> or <see cref="CloudflareDnsRecordTypes.AAAA"/>.</param>
        /// <param name="keptDesired">One existing record per desired content.</param>
        /// <param name="cancellationToken">Operation token. Checked before each update.</param>
        /// <exception cref="CloudflareDnsException">
        /// A kept record's content is not a valid address for <paramref name="type"/>. That failure is permanent.
        /// </exception>
        private async Task EnsureManagedAttributesAsync(
            string zoneId,
            string fqdn,
            string type,
            IReadOnlyList<CloudflareDnsRecord> keptDesired,
            CancellationToken cancellationToken)
        {
            foreach (var record in keptDesired)
            {
                if (CloudflareManagedDnsPolicy.MatchesManagedAttributes(record))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var content = NormalizeContent(record.Content, type)
                    ?? throw new CloudflareDnsException(
                        $"Cannot update {type} record {record.Id}: content is not a valid IP.")
                    {
                        FailedOperation = "Update",
                        IsPermanentFailure = true,
                    };

                CloudflareLogMessages.UpdatingRecordManagedAttributes(
                    _logger,
                    type,
                    record.Id,
                    fqdn,
                    CloudflareManagedDnsPolicy.ManagedTtl,
                    CloudflareManagedDnsPolicy.ManagedProxied);
                await _client.UpdateRecordAsync(
                        zoneId,
                        record.Id,
                        new CloudflareDnsRecordWriteRequest
                        {
                            Type = type,
                            Name = fqdn,
                            Content = content,
                            Ttl = CloudflareManagedDnsPolicy.ManagedTtl,
                            Proxied = CloudflareManagedDnsPolicy.ManagedProxied,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>Deletes each planned record by id. Called only after desired creates have completed.</summary>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="fqdn">FQDN logged with the delete.</param>
        /// <param name="type">Record type logged with the delete.</param>
        /// <param name="records">Stale, duplicate, and unparseable records.</param>
        /// <param name="cancellationToken">Operation token. Checked before each delete.</param>
        private async Task DeleteRecordsAsync(
            string zoneId,
            string fqdn,
            string type,
            IReadOnlyList<CloudflareDnsRecord> records,
            CancellationToken cancellationToken)
        {
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CloudflareLogMessages.DeletingRecord(_logger, type, record.Id, fqdn, record.Content);
                await _client.DeleteRecordAsync(zoneId, record.Id, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Re-reads A and AAAA and requires each family to match the desired content set and managed attributes.</summary>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="fqdn">Exact FQDN.</param>
        /// <param name="desiredV4">Desired IPv4 contents.</param>
        /// <param name="desiredV6">Desired IPv6 contents.</param>
        /// <param name="cancellationToken">Cancels the verification lists.</param>
        private async Task VerifyAsync(
            string zoneId,
            string fqdn,
            HashSet<string> desiredV4,
            HashSet<string> desiredV6,
            CancellationToken cancellationToken)
        {
            var actualA = await _client
                .ListRecordsAsync(zoneId, fqdn, CloudflareDnsRecordTypes.A, cancellationToken)
                .ConfigureAwait(false);
            var actualAaaa = await _client
                .ListRecordsAsync(zoneId, fqdn, CloudflareDnsRecordTypes.AAAA, cancellationToken)
                .ConfigureAwait(false);

            AssertExactSet(fqdn, CloudflareDnsRecordTypes.A, desiredV4, actualA);
            AssertExactSet(fqdn, CloudflareDnsRecordTypes.AAAA, desiredV6, actualAaaa);
        }

        /// <summary>
        /// Fails verification unless <paramref name="actual"/> is exactly <paramref name="desired"/>:
        /// same contents, same count, DNS-only proxy, managed TTL, and parseable content.
        /// </summary>
        /// <param name="fqdn">FQDN included in the failure message.</param>
        /// <param name="type">Address family being checked.</param>
        /// <param name="desired">Desired normalized contents.</param>
        /// <param name="actual">Records returned by the verification list.</param>
        /// <exception cref="CloudflareDnsException">
        /// The set, count, proxy flag, TTL, or content does not match. The failure is not marked permanent.
        /// </exception>
        private static void AssertExactSet(
            string fqdn,
            string type,
            HashSet<string> desired,
            IReadOnlyList<CloudflareDnsRecord> actual)
        {
            var actualContents = ToObservedContentSet(actual, type);

            if (!desired.SetEquals(actualContents))
            {
                throw new CloudflareDnsException(
                    $"Cloudflare DNS verification failed for '{fqdn}' ({type}): " +
                    $"desired=[{string.Join(", ", desired.Order(StringComparer.Ordinal))}] " +
                    $"actual=[{string.Join(", ", actualContents.Order(StringComparer.Ordinal))}]. " +
                    "Reconciliation must not be treated as successful. " +
                    "External modification or incomplete mutation may have occurred.")
                {
                    FailedOperation = "Verify",
                };
            }

            if (actual.Count != desired.Count)
            {
                throw new CloudflareDnsException(
                    $"Cloudflare DNS verification failed for '{fqdn}' ({type}): " +
                    $"duplicate or extra records remain (count {actual.Count} vs desired {desired.Count}). " +
                    "Reconciliation must not be treated as successful.")
                {
                    FailedOperation = "Verify",
                };
            }

            foreach (var record in actual)
            {
                if (record.Proxied != CloudflareManagedDnsPolicy.ManagedProxied)
                {
                    throw new CloudflareDnsException(
                        $"Cloudflare DNS verification failed for '{fqdn}' ({type}): " +
                        $"record {record.Id} proxy state is not DNS-only (proxied=false). " +
                        "Reconciliation must not be treated as successful.")
                    {
                        FailedOperation = "Verify",
                    };
                }

                if (record.Ttl != CloudflareManagedDnsPolicy.ManagedTtl)
                {
                    throw new CloudflareDnsException(
                        $"Cloudflare DNS verification failed for '{fqdn}' ({type}): " +
                        $"record {record.Id} TTL is not {CloudflareManagedDnsPolicy.ManagedTtl}. " +
                        "Reconciliation must not be treated as successful.")
                    {
                        FailedOperation = "Verify",
                    };
                }

                if (NormalizeContent(record.Content, type) is null)
                {
                    throw new CloudflareDnsException(
                        $"Cloudflare DNS verification failed for '{fqdn}' ({type}): " +
                        $"record {record.Id} has unparseable content. " +
                        "Reconciliation must not be treated as successful.")
                    {
                        FailedOperation = "Verify",
                    };
                }
            }
        }

        /// <summary>
        /// Classifies existing records into missing contents, one kept record per desired content,
        /// duplicates, stale contents, and unparseable records that must be deleted.
        /// </summary>
        /// <param name="existing">Current records for one address family.</param>
        /// <param name="type">Address family used to accept or reject record content.</param>
        /// <param name="desiredContents">Desired normalized contents for that family.</param>
        /// <returns>The plan. Unparseable records are only in <see cref="FamilyPlan.RecordsToDelete"/>.</returns>
        private static FamilyPlan BuildPlan(
            IReadOnlyList<CloudflareDnsRecord> existing,
            string type,
            HashSet<string> desiredContents)
        {
            var byContent = new Dictionary<string, List<CloudflareDnsRecord>>(StringComparer.OrdinalIgnoreCase);
            var unparseable = new List<CloudflareDnsRecord>();

            foreach (var record in existing)
            {
                var contentKey = NormalizeContent(record.Content, type);
                if (contentKey is null)
                {
                    unparseable.Add(record);
                    continue;
                }

                if (!byContent.TryGetValue(contentKey, out var list))
                {
                    list = [];
                    byContent[contentKey] = list;
                }

                list.Add(record);
            }

            var missing = new List<string>();
            var keptDesired = new List<CloudflareDnsRecord>();
            var duplicateExtras = new List<CloudflareDnsRecord>();
            var stale = new List<CloudflareDnsRecord>();

            foreach (var content in desiredContents)
            {
                if (!byContent.TryGetValue(content, out var records) || records.Count == 0)
                {
                    missing.Add(content);
                    continue;
                }

                keptDesired.Add(records[0]);
                for (var i = 1; i < records.Count; i++)
                {
                    duplicateExtras.Add(records[i]);
                }
            }

            foreach (var (content, records) in byContent)
            {
                if (desiredContents.Contains(content))
                {
                    continue;
                }

                stale.AddRange(records);
            }

            var toDelete = new List<CloudflareDnsRecord>();
            toDelete.AddRange(stale);
            toDelete.AddRange(duplicateExtras);
            toDelete.AddRange(unparseable);

            return new FamilyPlan(missing, keptDesired, stale, duplicateExtras, toDelete);
        }

        /// <summary>
        /// Waits for reconciler backoff unless it exceeds the remaining <see cref="CloudflareOperationBudget"/>.
        /// </summary>
        /// <param name="delay">Delay from <see cref="GetAttemptBackoff"/>. Zero or negative returns without waiting.</param>
        /// <param name="cancellationToken">Operation token.</param>
        /// <exception cref="OperationCanceledException">
        /// The token is canceled, the budget is expired, or <paramref name="delay"/> is longer than the remaining budget.
        /// </exception>
        private async Task DelayRespectingBudgetAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloudflareOperationBudget.Current?.ThrowIfExpired(cancellationToken);

            if (delay <= TimeSpan.Zero)
            {
                return;
            }

            if (CloudflareOperationBudget.Current is { } budget && delay > budget.Remaining)
            {
                throw new OperationCanceledException(
                    "Cloudflare DNS reconciler backoff exceeds the remaining operation budget.",
                    cancellationToken);
            }

            await DelayAsync(delay, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns the delay before the next reconcile or cleanup attempt: 200 ms times <c>2^(failedAttempt-1)</c>, clamped to 200–2000 ms.
        /// </summary>
        /// <param name="failedAttempt">One-based attempt that just failed. HTTP 429 delays are chosen separately by the client.</param>
        /// <returns>The backoff delay.</returns>
        private static TimeSpan GetAttemptBackoff(int failedAttempt)
        {
            // 200ms, 400ms between reconciler attempts (HTTP 429 has its own Retry-After policy).
            var ms = 200 * Math.Pow(2, failedAttempt - 1);
            return TimeSpan.FromMilliseconds(Math.Clamp(ms, 200, 2000));
        }

        /// <summary>Converts desired addresses to the DNS content strings used for set comparison.</summary>
        /// <param name="addresses">IPv4 or IPv6 addresses for one family.</param>
        /// <returns>A case-insensitive set of <c>IpAddressEligibility.ToDnsContent</c> values.</returns>
        private static HashSet<string> ToContentSet(IReadOnlyList<IPAddress> addresses)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var address in addresses)
            {
                set.Add(IpAddressEligibility.ToDnsContent(address));
            }

            return set;
        }

        /// <summary>Collects parseable record contents for one address family. Unparseable contents are omitted.</summary>
        /// <param name="records">Records returned by Cloudflare.</param>
        /// <param name="type">Address family passed to <see cref="NormalizeContent"/>.</param>
        /// <returns>A case-insensitive set of normalized contents.</returns>
        private static HashSet<string> ToObservedContentSet(IReadOnlyList<CloudflareDnsRecord> records, string type)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // codeql[cs/linq/missed-select] Gate-held verification path; Select/Where would allocate under SemaphoreSlim.
            foreach (var record in records)
            {
                var content = NormalizeContent(record.Content, type);
                if (content is not null)
                {
                    set.Add(content);
                }
            }

            return set;
        }

        /// <summary>
        /// Parses record content as an IP and returns its canonical DNS form when the address family matches <paramref name="type"/>.
        /// </summary>
        /// <param name="content">Cloudflare record content. Null or non-IP text returns null.</param>
        /// <param name="type"><see cref="CloudflareDnsRecordTypes.A"/> or <see cref="CloudflareDnsRecordTypes.AAAA"/>.</param>
        /// <returns>The canonical content, or null when missing, unparseable, or the wrong family.</returns>
        private static string? NormalizeContent(string? content, string type)
        {
            if (!IpAddressEligibility.TryParseDnsContent(content, out var address))
            {
                return null;
            }

            if (type == CloudflareDnsRecordTypes.A && address.AddressFamily != AddressFamily.InterNetwork)
            {
                return null;
            }

            if (type == CloudflareDnsRecordTypes.AAAA && address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return null;
            }

            return IpAddressEligibility.ToDnsContent(address);
        }

        /// <summary>Per-address-family reconcile plan for one attempt.</summary>
        /// <param name="Missing">Desired contents with no current record. These are created before any delete.</param>
        /// <param name="KeptDesired">The first existing record for each desired content.</param>
        /// <param name="Stale">Records whose content is not desired.</param>
        /// <param name="DuplicateExtras">Records after the first for a desired content.</param>
        /// <param name="RecordsToDelete">Stale, duplicate, and unparseable records, in that order.</param>
        private sealed record FamilyPlan(
            IReadOnlyList<string> Missing,
            IReadOnlyList<CloudflareDnsRecord> KeptDesired,
            IReadOnlyList<CloudflareDnsRecord> Stale,
            IReadOnlyList<CloudflareDnsRecord> DuplicateExtras,
            IReadOnlyList<CloudflareDnsRecord> RecordsToDelete);
    }
}

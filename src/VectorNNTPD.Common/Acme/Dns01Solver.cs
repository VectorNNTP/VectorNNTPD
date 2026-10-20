namespace VectorNNTPD.Common.Acme
{
    /// <summary>One DNS-01 challenge to publish as a TXT record.</summary>
    /// <param name="Domain">Authorization DNS name.</param>
    /// <param name="Validation">TXT RDATA (key authorization digest).</param>
    internal sealed record Dns01ChallengeSpec(string Domain, string Validation)
    {
        /// <summary>Gets the <c>_acme-challenge.</c> record name.</summary>
        internal string RecordName =>
            "_acme-challenge." + Domain.Trim().TrimEnd('.').ToLowerInvariant();
    }

    /// <summary>Looks up TXT RDATA for authoritative visibility checks (injectable).</summary>
    internal interface IAuthoritativeTxtResolver
    {
        /// <summary>Returns TXT strings for <paramref name="name"/> (maybe empty).</summary>
        /// <param name="name">TXT owner name.</param>
        /// <param name="cancellationToken">Cancels the lookup.</param>
        /// <returns>TXT RDATA values. Empty when none are visible.</returns>
        Task<IReadOnlyList<string>> LookupTxtAsync(string name, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Creates, awaits, and cleans up ACME DNS-01 TXT records via Cloudflare with FQDN-scoped recovery state.
    /// </summary>
    internal sealed class Dns01Solver
    {
        /// <summary>TTL used for challenge TXT records (matches pyNNTPD).</summary>
        public const int ChallengeTtlSeconds = 120;

        /// <summary>Cloudflare client that creates, lists, and deletes challenge TXT records.</summary>
        private readonly Cloudflare.ICloudflareDnsClient _client;

        /// <summary>Cloudflare zone id used for every record this solver creates.</summary>
        private readonly string _zoneId;

        /// <summary>Normalized certificate FQDN that owns the recovery directory and journal events.</summary>
        private readonly string _fqdn;

        /// <summary>Resolver used by <see cref="WaitPropagatedAsync"/>.</summary>
        private readonly IAuthoritativeTxtResolver _resolver;

        /// <summary>FQDN recovery directory. <see langword="null"/> when <c>stateDir</c> was omitted and recovery files are disabled.</summary>
        private readonly string? _recoveryDir;

        /// <summary>Optional journal. Events are recorded only when it already has an active transaction, except recovery removals.</summary>
        private readonly AcmeTransactionJournal? _historyJournal;

        /// <summary>How long <see cref="WaitPropagatedAsync"/> waits. Default is 120 seconds.</summary>
        private readonly TimeSpan _propagationTimeout;

        /// <summary>Delay between visibility polls. Default is 2 seconds.</summary>
        private readonly TimeSpan _propagationInterval;

        /// <summary>TXT records created by this instance and not yet deleted.</summary>
        private readonly List<PlacedChallenge> _placed = [];

        /// <summary>Set after <see cref="RecoverAsync"/> finishes, including the no-recovery-directory path.</summary>
        private bool _recovered;

        /// <summary>Initializes a new instance of the <see cref="Dns01Solver"/> class.</summary>
        /// <param name="client">Cloudflare DNS client.</param>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="resolver">Authoritative TXT resolver.</param>
        /// <param name="fqdn">Certificate FQDN that owns this DNS-01 recovery partition.</param>
        /// <param name="stateDir">Shared ACME state root. When omitted, recovery files are disabled.</param>
        /// <param name="propagationTimeout">Visibility wait timeout.</param>
        /// <param name="propagationInterval">Visibility poll interval.</param>
        /// <param name="historyJournal">
        /// Optional persistent ACME journal. DNS create/remove events are recorded here
        /// only when a transaction is already active for this FQDN.
        /// </param>
        internal Dns01Solver(
            Cloudflare.ICloudflareDnsClient client,
            string zoneId,
            IAuthoritativeTxtResolver resolver,
            string fqdn,
            string? stateDir = null,
            TimeSpan? propagationTimeout = null,
            TimeSpan? propagationInterval = null,
            AcmeTransactionJournal? historyJournal = null)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
            ArgumentNullException.ThrowIfNull(resolver);

            _client = client;
            _zoneId = zoneId;
            _fqdn = CertificateIdentities.NormalizeFqdn(fqdn);
            _resolver = resolver;
            _historyJournal = historyJournal;
            _propagationTimeout = propagationTimeout ?? TimeSpan.FromSeconds(120);
            _propagationInterval = propagationInterval ?? TimeSpan.FromSeconds(2);

            if (stateDir is not null)
            {
                AcmePaths.EnsureCertificateIdentityLayout(stateDir, _fqdn);
                _recoveryDir = AcmePaths.Dns01RecoveryDir(stateDir, _fqdn);
                Directory.CreateDirectory(_recoveryDir);
            }
        }

        /// <summary>Gets the FQDN this solver is scoped to.</summary>
        private string Fqdn => _fqdn;

        /// <summary>Idempotent startup recovery of journalled TXT records.</summary>
        internal async Task RecoverAsync(CancellationToken cancellationToken)
        {
            if (_recoveryDir is null)
            {
                _recovered = true;
                return;
            }

            var entries = LoadJournalEntries(_recoveryDir, _fqdn);
            var failures = 0;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await RecoverEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (AcmeChallengeException)
                {
                    throw;
                }
                catch
                {
                    failures++;
                }
            }

            if (failures > 0)
            {
                throw new AcmeChallengeException("txt_recovery_failed", $"failed_entries={failures}");
            }

            _recovered = true;
        }

        /// <summary>Creates TXT records for each challenge and journals ownership.</summary>
        internal async Task PlaceAsync(
            IReadOnlyList<Dns01ChallengeSpec> challenges,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(challenges);
            if (!_recovered)
            {
                await RecoverAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                foreach (var spec in challenges)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = await PlaceOneAsync(spec, cancellationToken).ConfigureAwait(false);
                    _placed.Add(item);
                }
            }
            catch (OperationCanceledException)
            {
                await BestEffortCleanupAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
            catch (AcmeChallengeException)
            {
                await BestEffortCleanupAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await BestEffortCleanupAsync(cancellationToken).ConfigureAwait(false);
                throw new AcmeChallengeException("txt_create_failed", ex.GetType().Name);
            }
        }

        /// <summary>Waits until authoritative resolvers report each challenge TXT value.</summary>
        internal async Task WaitPropagatedAsync(
            IReadOnlyList<Dns01ChallengeSpec> challenges,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(challenges);
            var deadline = DateTimeOffset.UtcNow + _propagationTimeout;
            var pending = challenges.ToDictionary(
                static c => c.RecordName,
                static c => c.Validation,
                StringComparer.OrdinalIgnoreCase);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new AcmeChallengeException("propagation_timeout", $"remaining={pending.Count}");
                }

                var resolved = new List<string>();
                foreach (var (name, expected) in pending)
                {
                    IReadOnlyList<string> values;
                    try
                    {
                        values = await _resolver.LookupTxtAsync(name, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (AcmeChallengeException)
                    {
                        throw;
                    }
                    catch
                    {
                        continue;
                    }

                    // Exact digest compare (RFC 8555 DNS-01). Do not trim whitespace or strip quotes.
                    if (values.Contains(expected, StringComparer.Ordinal))
                    {
                        resolved.Add(name);
                    }
                }

                foreach (var name in resolved)
                {
                    pending.Remove(name);
                }

                if (pending.Count > 0)
                {
                    await Task.Delay(_propagationInterval, cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var spec in challenges)
            {
                _historyJournal?.RecordDnsChallengePropagated(spec.RecordName);
            }
        }

        /// <summary>Deletes only TXT records created by this solver (by record id).</summary>
        internal async Task CleanupAsync(CancellationToken cancellationToken)
        {
            var remaining = new List<PlacedChallenge>();
            foreach (var item in _placed.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await DeleteOwnedRecordAsync(item.ZoneId, item.RecordId, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(item.EntryId) && _recoveryDir is not null)
                    {
                        RemoveJournalFile(_recoveryDir, item.EntryId);
                    }

                    _historyJournal?.RecordDnsChallengeRemoved(
                        item.Spec.RecordName,
                        item.ZoneId,
                        item.RecordId,
                        item.Spec.Validation);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    remaining.Add(item);
                }
            }

            _placed.Clear();
            _placed.AddRange(remaining);
            if (remaining.Count > 0)
            {
                throw new AcmeChallengeException("txt_cleanup_failed", $"remaining={remaining.Count}");
            }
        }

        /// <summary>
        /// Writes a <c>creating</c> recovery file, creates the TXT record, then rewrites the file as <c>placed</c> with the Cloudflare record id.
        /// </summary>
        /// <param name="spec">Challenge name and TXT value.</param>
        /// <param name="cancellationToken">Cancels the Cloudflare create.</param>
        /// <returns>The placed record, including the recovery entry id.</returns>
        private async Task<PlacedChallenge> PlaceOneAsync(
            Dns01ChallengeSpec spec,
            CancellationToken cancellationToken)
        {
            var entryId = Guid.NewGuid().ToString("N");
            var name = spec.RecordName;
            var content = spec.Validation;

            if (_recoveryDir is not null)
            {
                WriteJournalEntry(
                    _recoveryDir,
                    new JournalEntry(
                        entryId,
                        "creating",
                        _zoneId,
                        name,
                        content,
                        RecordId: null,
                        _fqdn,
                        _historyJournal?.ActiveTransactionId));
            }

            var record = await _client.CreateRecordAsync(
                    _zoneId,
                    new Cloudflare.CloudflareDnsRecordWriteRequest
                    {
                        Type = Cloudflare.CloudflareDnsRecordTypes.TXT,
                        Name = name,
                        Content = content,
                        Ttl = ChallengeTtlSeconds,
                        Proxied = false,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (_recoveryDir is not null)
            {
                WriteJournalEntry(
                    _recoveryDir,
                    new JournalEntry(
                        entryId,
                        "placed",
                        _zoneId,
                        name,
                        content,
                        record.Id,
                        _fqdn,
                        _historyJournal?.ActiveTransactionId));
            }

            _historyJournal?.RecordDnsChallengeCreated(name, _zoneId, record.Id, content);
            return new PlacedChallenge(spec, record.Id, _zoneId, entryId);
        }

        /// <summary>
        /// Deletes a <c>placed</c> record by id, or finds <c>creating</c> records by name and content, then removes the recovery file.
        /// A <c>placed</c> entry without a record id throws category <c>malformed_journal</c>.
        /// </summary>
        /// <param name="entry">Recovery file contents.</param>
        /// <param name="cancellationToken">Cancels Cloudflare list and delete calls.</param>
        private async Task RecoverEntryAsync(JournalEntry entry, CancellationToken cancellationToken)
        {
            if (_recoveryDir is null)
            {
                return;
            }

            if (entry.Phase == "placed")
            {
                if (string.IsNullOrEmpty(entry.RecordId))
                {
                    throw new AcmeChallengeException(
                        "malformed_journal",
                        $"placed without record_id entry={entry.EntryId}");
                }

                await DeleteOwnedRecordAsync(entry.ZoneId, entry.RecordId, cancellationToken)
                    .ConfigureAwait(false);
                RemoveJournalFile(_recoveryDir, entry.EntryId);
                RecordRecoveredRemoval(entry, entry.RecordId);
                return;
            }

            var matches = await FindRecordsByContentAsync(
                    entry.ZoneId,
                    entry.Name,
                    entry.Content,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var recordId in matches)
            {
                await DeleteOwnedRecordAsync(entry.ZoneId, recordId, cancellationToken).ConfigureAwait(false);
                RecordRecoveredRemoval(entry, recordId);
            }

            RemoveJournalFile(_recoveryDir, entry.EntryId);
        }

        /// <summary>Appends a recovered-and-removed journal event, attributed to <see cref="JournalEntry.TransactionId"/> when that transaction still exists.</summary>
        /// <param name="entry">Recovery entry that was cleaned up.</param>
        /// <param name="recordId">Cloudflare record id that was deleted. <see langword="null"/> when unknown.</param>
        private void RecordRecoveredRemoval(JournalEntry entry, string? recordId)
        {
            _historyJournal?.RecordDnsChallengeRecoveredAndRemoved(
                entry.EntryId,
                entry.Name,
                entry.ZoneId,
                recordId,
                entry.Content,
                entry.TransactionId);
        }

        /// <summary>Lists TXT records at <paramref name="name"/> whose content equals <paramref name="content"/>.</summary>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="name">Record name.</param>
        /// <param name="content">Expected TXT RDATA. Comparison is ordinal.</param>
        /// <param name="cancellationToken">Cancels the list.</param>
        /// <returns>Matching Cloudflare record ids.</returns>
        private async Task<IReadOnlyList<string>> FindRecordsByContentAsync(
            string zoneId,
            string name,
            string content,
            CancellationToken cancellationToken)
        {
            var records = await _client.ListAllRecordsForNameAsync(zoneId, name, cancellationToken)
                .ConfigureAwait(false);
            return (string[])[.. records
                .Where(r =>
                    string.Equals(r.Type, Cloudflare.CloudflareDnsRecordTypes.TXT, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.Content, content, StringComparison.Ordinal))
                .Select(r => r.Id)];
        }

        /// <summary>Deletes one record. HTTP 404, a message containing <c>not found</c>, or Cloudflare error 81044 is treated as already deleted.</summary>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="recordId">Cloudflare record id.</param>
        /// <param name="cancellationToken">Cancels the delete.</param>
        private async Task DeleteOwnedRecordAsync(
            string zoneId,
            string recordId,
            CancellationToken cancellationToken)
        {
            try
            {
                await _client.DeleteRecordAsync(zoneId, recordId, cancellationToken).ConfigureAwait(false);
            }
            catch (Cloudflare.CloudflareDnsException ex) when (ex.StatusCode == 404 || IsNotFound(ex))
            {
                // Idempotent success.
            }
        }

        /// <summary>True when the message contains <c>not found</c> or Cloudflare error code 81044 is present.</summary>
        /// <param name="ex">Delete failure from the DNS client.</param>
        /// <returns><see langword="true"/> when the record is already gone.</returns>
        private static bool IsNotFound(Cloudflare.CloudflareDnsException ex) =>
            ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || ex.CloudflareErrorCodes.Contains(81044);

        /// <summary>
        /// Calls <see cref="CleanupAsync"/>. Cancellation of <paramref name="cancellationToken"/> propagates.
        /// Other cleanup failures are ignored.
        /// </summary>
        /// <param name="cancellationToken">Passed to <see cref="CleanupAsync"/>.</param>
        private async Task BestEffortCleanupAsync(CancellationToken cancellationToken)
        {
            try
            {
                await CleanupAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Logged by callers via category.
            }
        }

        /// <summary>One TXT record created by this solver and still tracked for cleanup.</summary>
        /// <param name="Spec">Challenge that produced the record.</param>
        /// <param name="RecordId">Cloudflare record id.</param>
        /// <param name="ZoneId">Cloudflare zone id.</param>
        /// <param name="EntryId">Recovery file id. Empty is not used; placement always assigns one.</param>
        private sealed record PlacedChallenge(
            Dns01ChallengeSpec Spec,
            string RecordId,
            string ZoneId,
            string EntryId);

        /// <summary>One DNS-01 recovery file. <paramref name="Phase"/> is <c>creating</c> or <c>placed</c>.</summary>
        /// <param name="EntryId">File name without <c>.json</c>.</param>
        /// <param name="Phase"><c>creating</c> before the record id is known; <c>placed</c> after.</param>
        /// <param name="ZoneId">Cloudflare zone id.</param>
        /// <param name="Name">TXT record name.</param>
        /// <param name="Content">TXT RDATA.</param>
        /// <param name="RecordId">Cloudflare id. <see langword="null"/> while creating.</param>
        /// <param name="Fqdn">Certificate FQDN that owns the file.</param>
        /// <param name="TransactionId">Journal transaction id. <see langword="null"/> when no transaction was active.</param>
        private sealed record JournalEntry(
            string EntryId,
            string Phase,
            string ZoneId,
            string Name,
            string Content,
            string? RecordId,
            string Fqdn,
            string? TransactionId);

        /// <summary>Atomically writes version-1 recovery JSON named <c>{entryId}.json</c>.</summary>
        /// <param name="journalDir">FQDN recovery directory.</param>
        /// <param name="entry">Entry to serialize. <see cref="JournalEntry.RecordId"/> may be null.</param>
        private static void WriteJournalEntry(string journalDir, JournalEntry entry)
        {
            var payload = new Dns01ChallengeJournalPayload
            {
                Version = 1,
                EntryId = entry.EntryId,
                Phase = entry.Phase,
                ZoneId = entry.ZoneId,
                Name = entry.Name,
                Content = entry.Content,
                RecordId = entry.RecordId,
                Fqdn = entry.Fqdn,
                TransactionId = entry.TransactionId,
            };
            var path = Path.Combine(journalDir, entry.EntryId + ".json");
            AtomicFile.WriteText(
                path,
                System.Text.Json.JsonSerializer.Serialize(
                    payload,
                    AcmeJsonSerializerContext.Default.Dns01ChallengeJournalPayload)
                + Environment.NewLine);
        }

        /// <summary>Deletes <c>{entryId}.json</c>. A missing file or IO failure is ignored.</summary>
        /// <param name="journalDir">FQDN recovery directory.</param>
        /// <param name="entryId">Recovery file id.</param>
        private static void RemoveJournalFile(string journalDir, string entryId) =>
            AtomicFile.TryDelete(Path.Combine(journalDir, entryId + ".json"));

        /// <summary>Reads every <c>*.json</c> file in ordinal path order. A missing directory returns an empty list.</summary>
        /// <param name="journalDir">FQDN recovery directory.</param>
        /// <param name="expectedFqdn">FQDN each file must name.</param>
        /// <returns>The parsed entries. A malformed file throws <see cref="AcmeChallengeException"/>.</returns>
        private static List<JournalEntry> LoadJournalEntries(string journalDir, string expectedFqdn)
        {
            if (!Directory.Exists(journalDir))
            {
                return [];
            }

            var entries = new List<JournalEntry>();
            foreach (var path in Directory.EnumerateFiles(journalDir, "*.json").OrderBy(static p => p, StringComparer.Ordinal))
            {
                entries.Add(ParseJournalFile(path, expectedFqdn));
            }

            return entries;
        }

        /// <summary>
        /// Parses one version-1 recovery file. The file name must match <c>entry_id</c>, the phase must be <c>creating</c> or <c>placed</c>,
        /// a placed entry must have <c>record_id</c>, and <c>fqdn</c> must equal <paramref name="expectedFqdn"/>.
        /// </summary>
        /// <param name="path">JSON file path.</param>
        /// <param name="expectedFqdn">Certificate FQDN that owns the directory.</param>
        /// <returns>The parsed entry.</returns>
        /// <exception cref="AcmeChallengeException">Thrown with category <c>malformed_journal</c> when the file is the wrong version, shape, or FQDN.</exception>
        private static JournalEntry ParseJournalFile(string path, string expectedFqdn)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                var version = root.GetProperty("version").GetInt32();
                if (version != 1)
                {
                    throw new AcmeChallengeException("malformed_journal", $"unsupported_version={version}");
                }

                var entryId = root.GetProperty("entry_id").GetString() ?? string.Empty;
                var phase = root.GetProperty("phase").GetString() ?? string.Empty;
                var zoneId = root.GetProperty("zone_id").GetString() ?? string.Empty;
                var name = root.GetProperty("name").GetString() ?? string.Empty;
                var content = root.GetProperty("content").GetString() ?? string.Empty;
                string? recordId = null;
                if (root.TryGetProperty("record_id", out var recordIdElement)
                    && recordIdElement.ValueKind != System.Text.Json.JsonValueKind.Null)
                {
                    recordId = recordIdElement.GetString();
                }

                if (Path.GetFileNameWithoutExtension(path) != entryId)
                {
                    throw new AcmeChallengeException("malformed_journal", "entry_id mismatch");
                }

                if (phase is not ("creating" or "placed"))
                {
                    throw new AcmeChallengeException("malformed_journal", $"bad_phase={phase}");
                }

                if (phase == "placed" && string.IsNullOrEmpty(recordId))
                {
                    throw new AcmeChallengeException("malformed_journal", "placed without record_id");
                }

                var fqdn = root.GetProperty("fqdn").GetString() ?? string.Empty;
                if (!string.Equals(fqdn, expectedFqdn, StringComparison.Ordinal))
                {
                    throw new AcmeChallengeException("malformed_journal", "fqdn mismatch");
                }

                string? transactionId = null;
                if (root.TryGetProperty("transaction_id", out var transactionIdElement)
                    && transactionIdElement.ValueKind != System.Text.Json.JsonValueKind.Null)
                {
                    transactionId = transactionIdElement.GetString();
                }

                return new JournalEntry(entryId, phase, zoneId, name, content, recordId, fqdn, transactionId);
            }
            catch (AcmeChallengeException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                throw new AcmeChallengeException("malformed_journal", ex.GetType().Name);
            }
        }
    }
}

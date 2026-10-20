using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace VectorNNTPD.Common.Acme
{
    /// <summary>
    /// Persistent ACME transaction journal for one certificate FQDN.
    /// </summary>
    /// <remarks>
    /// The journal is one file, <c>journal/{fqdn}.json</c>. Each certificate request
    /// is a transaction that accumulates events for the complete ACME lifecycle.
    /// Historical transactions are retained indefinitely. Same-FQDN writers are
    /// serialized in-process and across processes solely by an exclusive
    /// <see cref="FileShare.None"/> lock on <c>{journalPath}.lock</c>; waiters do not
    /// hold an in-process gate while acquiring that file lock. Distinct FQDN files
    /// do not share a lock. Writes use <see cref="AtomicFile"/> so a crash cannot
    /// leave a partial journal.
    /// </remarks>
    internal sealed class AcmeTransactionJournal
    {
        /// <summary>Current journal schema version.</summary>
        public const int SchemaVersion = 1;

        /// <summary>Transaction has started and has not finished.</summary>
        public const string StatusInProgress = "in_progress";

        /// <summary>Issuance completed and a certificate was persisted.</summary>
        public const string StatusSucceeded = "succeeded";

        /// <summary>Issuance failed.</summary>
        public const string StatusFailed = "failed";

        /// <summary>A certificate request was recorded.</summary>
        public const string EventCertificateRequestStarted = "certificate_request_started";

        /// <summary>A DNS-01 TXT record was created in Cloudflare.</summary>
        public const string EventDnsChallengeCreated = "dns_challenge_created";

        /// <summary>Authoritative resolvers reported the challenge TXT.</summary>
        public const string EventDnsChallengePropagated = "dns_challenge_propagated";

        /// <summary>ACME challenge validation was triggered.</summary>
        public const string EventAcmeValidationStarted = "acme_validation_started";

        /// <summary>The ACME order reached the ready state.</summary>
        public const string EventAcmeValidationSucceeded = "acme_validation_succeeded";

        /// <summary>A DNS-01 TXT record was deleted.</summary>
        public const string EventDnsChallengeRemoved = "dns_challenge_removed";

        /// <summary>An orphaned DNS-01 TXT record was deleted during recovery.</summary>
        public const string EventDnsChallengeRecoveredAndRemoved = "dns_challenge_recovered_and_removed";

        /// <summary>The issuer returned certificate material.</summary>
        public const string EventCertificateIssued = "certificate_issued";

        /// <summary>Certificate bytes were written to a generation directory.</summary>
        public const string EventCertificatePersisted = "certificate_persisted";

        /// <summary>The FQDN current pointer was switched to the new generation.</summary>
        public const string EventCertificatePromoted = "certificate_promoted";

        /// <summary>Issuance failed.</summary>
        public const string EventCertificateIssuanceFailed = "certificate_issuance_failed";

        /// <summary>Normalized certificate FQDN stored in and required by the journal file.</summary>
        private readonly string _fqdn;

        /// <summary>Path of <c>journal/{fqdn}.json</c>.</summary>
        private readonly string _journalPath;

        /// <summary>Exclusive lock path <c>{journalPath}.lock</c>.</summary>
        private readonly string _journalLockPath;

        /// <summary>Transaction id from the last <see cref="BeginTransaction"/> that has not been completed. <see langword="null"/> otherwise.</summary>
        private string? _activeTransactionId;

        /// <summary>Initializes a new instance of the <see cref="AcmeTransactionJournal"/> class.</summary>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <param name="fqdn">Certificate FQDN that owns this journal file.</param>
        internal AcmeTransactionJournal(string stateDir, string fqdn)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
            _fqdn = CertificateIdentities.NormalizeFqdn(fqdn);
            AcmePaths.EnsureStateLayout(stateDir);
            _journalPath = AcmePaths.TransactionJournalPath(stateDir, _fqdn);
            _journalLockPath = _journalPath + ".lock";
        }

        /// <summary>Gets the FQDN this journal is scoped to.</summary>
        private string Fqdn => _fqdn;

        /// <summary>Gets the persistent journal file path.</summary>
        internal string FilePath => _journalPath;

        /// <summary>Gets the exclusive lock file used for same-FQDN journal writes.</summary>
        internal string LockPath => _journalLockPath;

        /// <summary>Gets the transaction id that subsequent events attach to, if any.</summary>
        internal string? ActiveTransactionId => _activeTransactionId;

        /// <summary>Records a new certificate request and returns its transaction id.</summary>
        /// <param name="identifiers">Requested identifiers/SANs.</param>
        /// <param name="directoryUrl">ACME directory URL used for the request.</param>
        /// <param name="startedAt">Request timestamp.</param>
        /// <param name="cancellationToken">Cancels waiting for the journal write lock.</param>
        /// <returns>The new transaction id.</returns>
        internal string BeginTransaction(
            IReadOnlyList<string> identifiers,
            string directoryUrl,
            DateTimeOffset startedAt,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(identifiers);
            ArgumentException.ThrowIfNullOrWhiteSpace(directoryUrl);

            var transactionId = Guid.NewGuid().ToString("N");
            var started = startedAt.ToUniversalTime();
            Mutate(
                document =>
                {
                    document.Transactions.Add(new AcmeJournalTransaction
                    {
                        Id = transactionId,
                        StartedAt = started,
                        Identifiers = [.. identifiers],
                        DirectoryUrl = directoryUrl.Trim(),
                        Status = StatusInProgress,
                        Events =
                        [
                            new AcmeJournalEvent
                            {
                                Type = EventCertificateRequestStarted,
                                At = started,
                            },
                        ],
                    });
                },
                cancellationToken);
            _activeTransactionId = transactionId;
            return transactionId;
        }

        /// <summary>Records that a DNS-01 TXT record was created.</summary>
        /// <param name="recordName">Challenge record name.</param>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="recordId">Cloudflare record id.</param>
        /// <param name="txtContent">TXT RDATA.</param>
        internal void RecordDnsChallengeCreated(
            string recordName,
            string zoneId,
            string recordId,
            string txtContent) =>
            AppendEvent(new AcmeJournalEvent
            {
                Type = EventDnsChallengeCreated,
                At = DateTimeOffset.UtcNow,
                RecordName = recordName,
                ZoneId = zoneId,
                RecordId = recordId,
                TxtContent = txtContent,
            });

        /// <summary>Records that a DNS-01 TXT value became visible.</summary>
        /// <param name="recordName">Challenge record name.</param>
        internal void RecordDnsChallengePropagated(string recordName) =>
            AppendEvent(new AcmeJournalEvent
            {
                Type = EventDnsChallengePropagated,
                At = DateTimeOffset.UtcNow,
                RecordName = recordName,
            });

        /// <summary>Records that ACME challenge validation was triggered.</summary>
        internal void RecordAcmeValidationStarted() =>
            AppendEvent(new AcmeJournalEvent
            {
                Type = EventAcmeValidationStarted,
                At = DateTimeOffset.UtcNow,
            });

        /// <summary>Records that the ACME order reached the ready state.</summary>
        internal void RecordAcmeValidationSucceeded() =>
            AppendEvent(new AcmeJournalEvent
            {
                Type = EventAcmeValidationSucceeded,
                At = DateTimeOffset.UtcNow,
            });

        /// <summary>Records that a DNS-01 TXT record was deleted.</summary>
        /// <param name="recordName">Challenge record name.</param>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="recordId">Cloudflare record id.</param>
        /// <param name="txtContent">TXT RDATA.</param>
        internal void RecordDnsChallengeRemoved(
            string recordName,
            string zoneId,
            string recordId,
            string txtContent) =>
            AppendEvent(new AcmeJournalEvent
            {
                Type = EventDnsChallengeRemoved,
                At = DateTimeOffset.UtcNow,
                RecordName = recordName,
                ZoneId = zoneId,
                RecordId = recordId,
                TxtContent = txtContent,
            });

        /// <summary>
        /// Records that recovery deleted an orphaned DNS-01 TXT record.
        /// </summary>
        /// <param name="recoveryEntryId">Recovery file id.</param>
        /// <param name="recordName">Challenge record name.</param>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="recordId">Cloudflare record id when known.</param>
        /// <param name="txtContent">TXT RDATA.</param>
        /// <param name="originalTransactionId">
        /// Transaction id persisted on the recovery file. When this id exists in the
        /// journal, the event is attached to that transaction. When it is missing or
        /// unknown, the event is stored as unattributed history and is not attached to
        /// the active transaction.
        /// </param>
        internal void RecordDnsChallengeRecoveredAndRemoved(
            string recoveryEntryId,
            string recordName,
            string zoneId,
            string? recordId,
            string txtContent,
            string? originalTransactionId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(recoveryEntryId);
            ArgumentException.ThrowIfNullOrWhiteSpace(recordName);

            var journalEvent = new AcmeJournalEvent
            {
                Type = EventDnsChallengeRecoveredAndRemoved,
                At = DateTimeOffset.UtcNow,
                RecordName = recordName,
                ZoneId = zoneId,
                RecordId = recordId,
                TxtContent = txtContent,
                RecoveryEntryId = recoveryEntryId,
            };

            Mutate(document =>
            {
                if (!string.IsNullOrWhiteSpace(originalTransactionId))
                {
                    var owner = document.Transactions.FirstOrDefault(
                        item => string.Equals(item.Id, originalTransactionId, StringComparison.Ordinal));
                    if (owner is not null)
                    {
                        owner.Events.Add(journalEvent);
                        return;
                    }
                }

                document.UnattributedEvents.Add(journalEvent);
            });
        }

        /// <summary>Records that the issuer returned certificate material.</summary>
        /// <param name="material">Issued certificate material.</param>
        internal void RecordCertificateIssued(CertificateMaterial material)
        {
            ArgumentNullException.ThrowIfNull(material);
            AppendEvent(new AcmeJournalEvent
            {
                Type = EventCertificateIssued,
                At = DateTimeOffset.UtcNow,
                NotBefore = material.NotBefore.ToUniversalTime(),
                NotAfter = material.NotAfter.ToUniversalTime(),
            });
        }

        /// <summary>Records that certificate bytes were written to a generation directory.</summary>
        /// <param name="generationId">Live generation id.</param>
        /// <param name="material">Persisted certificate material.</param>
        internal void RecordCertificatePersisted(string generationId, CertificateMaterial material)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
            ArgumentNullException.ThrowIfNull(material);
            AppendEvent(new AcmeJournalEvent
            {
                Type = EventCertificatePersisted,
                At = DateTimeOffset.UtcNow,
                GenerationId = generationId,
                NotBefore = material.NotBefore.ToUniversalTime(),
                NotAfter = material.NotAfter.ToUniversalTime(),
            });
        }

        /// <summary>Records that the FQDN current pointer was updated.</summary>
        /// <param name="generationId">Promoted generation id.</param>
        internal void RecordCertificatePromoted(string generationId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
            AppendEvent(new AcmeJournalEvent
            {
                Type = EventCertificatePromoted,
                At = DateTimeOffset.UtcNow,
                GenerationId = generationId,
            });
        }

        /// <summary>Completes a request with certificate fields taken from the issued PFX.</summary>
        /// <param name="transactionId">Transaction started by <see cref="BeginTransaction"/>.</param>
        /// <param name="material">Issued certificate material.</param>
        /// <param name="generationId">Live generation that contains the certificate.</param>
        /// <param name="pfxPassword">PKCS#12 password used to inspect serial and thumbprint.</param>
        /// <param name="completedAt">Completion timestamp.</param>
        internal void CompleteSuccess(
            string transactionId,
            CertificateMaterial material,
            string generationId,
            string pfxPassword,
            DateTimeOffset completedAt)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
            ArgumentNullException.ThrowIfNull(material);
            ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
            ArgumentNullException.ThrowIfNull(pfxPassword);

            var inspected = InspectCertificate(material.PfxBytes, pfxPassword);
            var completed = completedAt.ToUniversalTime();
            Mutate(document =>
            {
                var transaction = RequireTransaction(document, transactionId);
                transaction.CompletedAt = completed;
                transaction.Status = StatusSucceeded;
                transaction.SerialNumber = inspected.SerialNumber;
                transaction.Thumbprint = inspected.Thumbprint;
                transaction.NotBefore = inspected.NotBefore ?? material.NotBefore.ToUniversalTime();
                transaction.NotAfter = inspected.NotAfter ?? material.NotAfter.ToUniversalTime();
                transaction.GenerationId = generationId;
                transaction.FailureCategory = null;
                transaction.FailureDetail = null;
            });
            if (string.Equals(_activeTransactionId, transactionId, StringComparison.Ordinal))
            {
                _activeTransactionId = null;
            }
        }

        /// <summary>Completes a request with sanitized failure information.</summary>
        /// <param name="transactionId">Transaction started by <see cref="BeginTransaction"/>.</param>
        /// <param name="failureCategory">Failure category.</param>
        /// <param name="failureDetail">Sanitized failure detail.</param>
        /// <param name="completedAt">Completion timestamp.</param>
        internal void CompleteFailure(
            string transactionId,
            string failureCategory,
            string failureDetail,
            DateTimeOffset completedAt)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(failureCategory);

            var completed = completedAt.ToUniversalTime();
            var detail = string.IsNullOrWhiteSpace(failureDetail) ? failureCategory : failureDetail;
            Mutate(document =>
            {
                var transaction = RequireTransaction(document, transactionId);
                transaction.CompletedAt = completed;
                transaction.Status = StatusFailed;
                transaction.FailureCategory = failureCategory;
                transaction.FailureDetail = detail;
                transaction.Events.Add(new AcmeJournalEvent
                {
                    Type = EventCertificateIssuanceFailed,
                    At = completed,
                    FailureCategory = failureCategory,
                    FailureDetail = detail,
                });
            });
            if (string.Equals(_activeTransactionId, transactionId, StringComparison.Ordinal))
            {
                _activeTransactionId = null;
            }
        }

        /// <summary>Loads historical transactions for this FQDN only, oldest first.</summary>
        /// <returns>The persisted transactions, or an empty list when the file does not exist.</returns>
        internal IReadOnlyList<AcmeJournalTransaction> LoadHistory()
        {
            return WithGate(() => LoadDocument().Transactions);
        }

        /// <summary>
        /// Loads recovery events that could not be attached to an original transaction.
        /// </summary>
        /// <returns>Unattributed events in write order.</returns>
        internal IReadOnlyList<AcmeJournalEvent> LoadUnattributedEvents()
        {
            return WithGate(() => LoadDocument().UnattributedEvents);
        }

        /// <summary>Appends <paramref name="journalEvent"/> to <see cref="_activeTransactionId"/>. Does nothing when no transaction is active.</summary>
        /// <param name="journalEvent">Event to store. The caller sets its type and time.</param>
        private void AppendEvent(AcmeJournalEvent journalEvent)
        {
            var transactionId = _activeTransactionId;
            if (transactionId is null)
            {
                return;
            }

            Mutate(document => RequireTransaction(document, transactionId).Events.Add(journalEvent));
        }

        /// <summary>Loads the document, applies <paramref name="mutate"/>, and writes it back under the journal lock.</summary>
        /// <param name="mutate">In-memory edit. It runs while the lock is held.</param>
        /// <param name="cancellationToken">Cancels waiting for the lock. The edit itself is synchronous.</param>
        private void Mutate(Action<AcmeJournalDocument> mutate, CancellationToken cancellationToken = default)
        {
            WithGate(
                () =>
                {
                    var document = LoadDocument();
                    mutate(document);
                    Persist(document);
                    return 0;
                },
                cancellationToken);
        }

        /// <summary>
        /// Runs <paramref name="action"/> while holding the exclusive <c>.lock</c> file for this journal path.
        /// </summary>
        /// <typeparam name="T">Result of <paramref name="action"/>.</typeparam>
        /// <param name="action">Work that reads or writes the journal. It runs on the caller thread.</param>
        /// <param name="cancellationToken">Cancels waiting for the file lock.</param>
        /// <returns>The value returned by <paramref name="action"/>.</returns>
        /// <remarks>
        /// Serialization is provided solely by <see cref="AcmeExclusiveFileLock"/> (<see cref="FileShare.None"/>).
        /// No in-process monitor is held while waiting for the cross-process lock, so one stuck or
        /// cancelled waiter cannot wedge other same-path writers in this process. The file lock is
        /// held for the complete read/modify/write performed by <paramref name="action"/>.
        /// </remarks>
        private T WithGate<T>(Func<T> action, CancellationToken cancellationToken = default)
        {
            using var fileLock = AcmeExclusiveFileLock.Acquire(_journalLockPath, cancellationToken);
            return action();
        }

        /// <summary>
        /// Reads the journal file. A missing file returns an empty version-<see cref="SchemaVersion"/> document for this FQDN.
        /// A version or FQDN mismatch throws <see cref="AcmeStorageException"/> category <c>malformed_journal</c>.
        /// </summary>
        /// <returns>The in-memory document. The caller mutates and persists it.</returns>
        private AcmeJournalDocument LoadDocument()
        {
            if (!File.Exists(_journalPath))
            {
                return new AcmeJournalDocument
                {
                    Version = SchemaVersion,
                    Fqdn = _fqdn,
                };
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_journalPath));
                var root = doc.RootElement;
                var version = root.GetProperty("version").GetInt32();
                if (version != SchemaVersion)
                {
                    throw new AcmeStorageException("malformed_journal", $"unsupported_version={version}");
                }

                var fqdn = root.GetProperty("fqdn").GetString() ?? string.Empty;
                if (!string.Equals(fqdn, _fqdn, StringComparison.Ordinal))
                {
                    throw new AcmeStorageException("malformed_journal", "fqdn mismatch");
                }

                var document = new AcmeJournalDocument
                {
                    Version = version,
                    Fqdn = fqdn,
                };
                if (root.TryGetProperty("transactions", out var transactionsElement)
                    && transactionsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in transactionsElement.EnumerateArray())
                    {
                        document.Transactions.Add(ParseTransaction(item));
                    }
                }

                if (root.TryGetProperty("unattributed_events", out var unattributedElement)
                    && unattributedElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in unattributedElement.EnumerateArray())
                    {
                        document.UnattributedEvents.Add(ParseEvent(item));
                    }
                }

                return document;
            }
            catch (AcmeStorageException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or FormatException)
            {
                throw new AcmeStorageException("malformed_journal", ex.GetType().Name);
            }
        }

        /// <summary>Writes <paramref name="document"/> as indented JSON plus a trailing newline via <see cref="AtomicFile"/>.</summary>
        /// <param name="document">Document to serialize. Timestamps are formatted as round-trip UTC strings.</param>
        private void Persist(AcmeJournalDocument document)
        {
            var transactions = new List<AcmeJournalWireTransaction>(document.Transactions.Count);
            foreach (var transaction in document.Transactions)
            {
                transactions.Add(SerializeTransaction(transaction));
            }

            var unattributed = new List<AcmeJournalWireEvent>(document.UnattributedEvents.Count);
            foreach (var journalEvent in document.UnattributedEvents)
            {
                unattributed.Add(SerializeEvent(journalEvent));
            }

            var payload = new AcmeJournalWireDocument
            {
                Version = document.Version,
                Fqdn = document.Fqdn,
                Transactions = transactions,
                UnattributedEvents = unattributed,
            };

            AtomicFile.WriteText(
                _journalPath,
                JsonSerializer.Serialize(payload, AcmeJsonSerializerContext.Default.AcmeJournalWireDocument)
                + Environment.NewLine);
        }

        /// <summary>Copies one transaction to the wire type, formatting timestamps and omitting null optional fields as null.</summary>
        /// <param name="transaction">In-memory transaction.</param>
        /// <returns>The JSON object written under <c>transactions</c>.</returns>
        private static AcmeJournalWireTransaction SerializeTransaction(AcmeJournalTransaction transaction)
        {
            var events = new List<AcmeJournalWireEvent>(transaction.Events.Count);
            foreach (var journalEvent in transaction.Events)
            {
                events.Add(SerializeEvent(journalEvent));
            }

            return new AcmeJournalWireTransaction
            {
                Id = transaction.Id,
                StartedAt = FormatTimestamp(transaction.StartedAt),
                CompletedAt = transaction.CompletedAt is { } completed ? FormatTimestamp(completed) : null,
                Identifiers = transaction.Identifiers is List<string> list
                    ? list
                    : [.. transaction.Identifiers],
                DirectoryUrl = transaction.DirectoryUrl,
                Status = transaction.Status,
                Events = events,
                SerialNumber = transaction.SerialNumber,
                Thumbprint = transaction.Thumbprint,
                NotBefore = transaction.NotBefore is { } notBefore ? FormatTimestamp(notBefore) : null,
                NotAfter = transaction.NotAfter is { } notAfter ? FormatTimestamp(notAfter) : null,
                GenerationId = transaction.GenerationId,
                FailureCategory = transaction.FailureCategory,
                FailureDetail = transaction.FailureDetail,
            };
        }

        /// <summary>Copies one event to the wire type. Null optional fields are written as JSON null.</summary>
        /// <param name="journalEvent">In-memory event.</param>
        /// <returns>The JSON object written under <c>events</c> or <c>unattributed_events</c>.</returns>
        private static AcmeJournalWireEvent SerializeEvent(AcmeJournalEvent journalEvent) =>
            new()
            {
                Type = journalEvent.Type,
                At = FormatTimestamp(journalEvent.At),
                RecordName = journalEvent.RecordName,
                ZoneId = journalEvent.ZoneId,
                RecordId = journalEvent.RecordId,
                TxtContent = journalEvent.TxtContent,
                GenerationId = journalEvent.GenerationId,
                NotBefore = journalEvent.NotBefore is { } notBefore ? FormatTimestamp(notBefore) : null,
                NotAfter = journalEvent.NotAfter is { } notAfter ? FormatTimestamp(notAfter) : null,
                FailureCategory = journalEvent.FailureCategory,
                FailureDetail = journalEvent.FailureDetail,
                RecoveryEntryId = journalEvent.RecoveryEntryId,
            };

        /// <summary>
        /// Reads one transaction object. Missing optional strings become null. A missing <c>started_at</c> becomes <see cref="DateTimeOffset.MinValue"/>.
        /// Blank identifier strings are dropped.
        /// </summary>
        /// <param name="root">One element of <c>transactions</c>.</param>
        /// <returns>The in-memory transaction.</returns>
        private static AcmeJournalTransaction ParseTransaction(JsonElement root)
        {
            var identifiers = new List<string>();
            if (root.TryGetProperty("identifiers", out var identifiersElement)
                && identifiersElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in identifiersElement.EnumerateArray())
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        identifiers.Add(value);
                    }
                }
            }

            var events = new List<AcmeJournalEvent>();
            if (root.TryGetProperty("events", out var eventsElement)
                && eventsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in eventsElement.EnumerateArray())
                {
                    events.Add(ParseEvent(item));
                }
            }

            return new AcmeJournalTransaction
            {
                Id = root.GetProperty("id").GetString() ?? string.Empty,
                StartedAt = ParseTimestamp(root, "started_at") ?? DateTimeOffset.MinValue,
                CompletedAt = ParseTimestamp(root, "completed_at"),
                Identifiers = identifiers,
                DirectoryUrl = root.GetProperty("directory_url").GetString() ?? string.Empty,
                Status = root.GetProperty("status").GetString() ?? string.Empty,
                Events = events,
                SerialNumber = ReadOptionalString(root, "serial_number"),
                Thumbprint = ReadOptionalString(root, "thumbprint"),
                NotBefore = ParseTimestamp(root, "not_before"),
                NotAfter = ParseTimestamp(root, "not_after"),
                GenerationId = ReadOptionalString(root, "generation_id"),
                FailureCategory = ReadOptionalString(root, "failure_category"),
                FailureDetail = ReadOptionalString(root, "failure_detail"),
            };
        }

        /// <summary>Reads one event object. A missing <c>at</c> becomes <see cref="DateTimeOffset.MinValue"/>. Absent or blank optional strings become null.</summary>
        /// <param name="root">One event element.</param>
        /// <returns>The in-memory event.</returns>
        private static AcmeJournalEvent ParseEvent(JsonElement root) =>
            new()
            {
                Type = root.GetProperty("type").GetString() ?? string.Empty,
                At = ParseTimestamp(root, "at") ?? DateTimeOffset.MinValue,
                RecordName = ReadOptionalString(root, "record_name"),
                ZoneId = ReadOptionalString(root, "zone_id"),
                RecordId = ReadOptionalString(root, "record_id"),
                TxtContent = ReadOptionalString(root, "txt_content"),
                GenerationId = ReadOptionalString(root, "generation_id"),
                NotBefore = ParseTimestamp(root, "not_before"),
                NotAfter = ParseTimestamp(root, "not_after"),
                FailureCategory = ReadOptionalString(root, "failure_category"),
                FailureDetail = ReadOptionalString(root, "failure_detail"),
                RecoveryEntryId = ReadOptionalString(root, "recovery_entry_id"),
            };

        /// <summary>Finds <paramref name="transactionId"/> in <paramref name="document"/>. A missing id throws category <c>missing_journal_transaction</c>.</summary>
        /// <param name="document">Loaded journal. Its FQDN must equal this instance's FQDN.</param>
        /// <param name="transactionId">Transaction id to update.</param>
        /// <returns>The matching transaction.</returns>
        private AcmeJournalTransaction RequireTransaction(AcmeJournalDocument document, string transactionId)
        {
            var transaction = document.Transactions.FirstOrDefault(
                item => string.Equals(item.Id, transactionId, StringComparison.Ordinal))
                ?? throw new AcmeStorageException("missing_journal_transaction", transactionId);

            if (!string.Equals(document.Fqdn, _fqdn, StringComparison.Ordinal))
            {
                throw new AcmeStorageException("malformed_journal", "fqdn mismatch");
            }

            return transaction;
        }

        /// <summary>Formats <paramref name="value"/> as UTC round-trip (<c>o</c>) text.</summary>
        /// <param name="value">Timestamp to convert to UTC.</param>
        /// <returns>The invariant round-trip string.</returns>
        private static string FormatTimestamp(DateTimeOffset value) =>
            value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        /// <summary>Reads a timestamp property. Missing, null, or blank text returns null. Other text is parsed as round-trip and converted to UTC.</summary>
        /// <param name="root">Object that may contain the property.</param>
        /// <param name="name">Wire property name.</param>
        /// <returns>The UTC time, or null when the property is absent or blank.</returns>
        private static DateTimeOffset? ParseTimestamp(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            var raw = element.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                .ToUniversalTime();
        }

        /// <summary>Reads a string property. Missing, null, or whitespace returns null.</summary>
        /// <param name="root">Object that may contain the property.</param>
        /// <param name="name">Wire property name.</param>
        /// <returns>The string, or null when absent or blank.</returns>
        private static string? ReadOptionalString(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            var value = element.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        /// <summary>Loads the PFX and reads serial, thumbprint, and validity. The certificate is disposed. Load failures become category <c>journal_certificate_inspect_failed</c>.</summary>
        /// <param name="pfxBytes">Issued PKCS#12 bytes.</param>
        /// <param name="password">PFX password. Not included in the exception.</param>
        /// <returns>The inspected fields. Serial and thumbprint come from <see cref="X509Certificate2"/>.</returns>
        private static CertificateInspection InspectCertificate(byte[] pfxBytes, string password)
        {
            X509Certificate2? cert = null;
            try
            {
                cert = PfxCrypto.LoadCertificate(pfxBytes, password);
                return new CertificateInspection(
                    cert.SerialNumber,
                    cert.Thumbprint,
                    cert.NotBefore.ToUniversalTime(),
                    cert.NotAfter.ToUniversalTime());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new AcmeCertificateException("journal_certificate_inspect_failed", ex.GetType().Name);
            }
            finally
            {
                cert?.Dispose();
            }
        }

        /// <summary>Leaf fields copied into a successful journal transaction.</summary>
        /// <param name="SerialNumber"><see cref="X509Certificate2.SerialNumber"/>. May be null.</param>
        /// <param name="Thumbprint"><see cref="X509Certificate2.Thumbprint"/>. May be null.</param>
        /// <param name="NotBefore">Not-before converted with <see cref="DateTime.ToUniversalTime"/>.</param>
        /// <param name="NotAfter">Not-after converted with <see cref="DateTime.ToUniversalTime"/>.</param>
        private readonly record struct CertificateInspection(
            string? SerialNumber,
            string? Thumbprint,
            DateTimeOffset? NotBefore,
            DateTimeOffset? NotAfter);
    }

    /// <summary>On-disk ACME journal document for one FQDN.</summary>
    internal sealed class AcmeJournalDocument
    {
        /// <summary>Gets or sets the journal schema version.</summary>
        internal int Version { get; set; }

        /// <summary>Gets or sets the certificate FQDN.</summary>
        internal string Fqdn { get; set; } = string.Empty;

        /// <summary>Gets the historical transactions in write order.</summary>
        internal List<AcmeJournalTransaction> Transactions { get; } = [];

        /// <summary>
        /// Gets recovery events that could not be attached to an original transaction.
        /// </summary>
        internal List<AcmeJournalEvent> UnattributedEvents { get; } = [];
    }

    /// <summary>One ACME request and its lifecycle events.</summary>
    internal sealed class AcmeJournalTransaction
    {
        /// <summary>Gets or sets the transaction id.</summary>
        internal string Id { get; set; } = string.Empty;

        /// <summary>Gets or sets when the request started.</summary>
        internal DateTimeOffset StartedAt { get; set; }

        /// <summary>Gets or sets when the request finished, if finished.</summary>
        internal DateTimeOffset? CompletedAt { get; set; }

        /// <summary>Gets or sets the requested identifiers/SANs.</summary>
        internal IReadOnlyList<string> Identifiers { get; set; } = [];

        /// <summary>Gets or sets the ACME directory URL used for the request.</summary>
        internal string DirectoryUrl { get; set; } = string.Empty;

        /// <summary>Gets or sets the request status.</summary>
        internal string Status { get; set; } = string.Empty;

        /// <summary>Gets the lifecycle events in write order.</summary>
        internal List<AcmeJournalEvent> Events { get; set; } = [];

        /// <summary>Gets or sets the issued certificate serial number when known.</summary>
        internal string? SerialNumber { get; set; }

        /// <summary>Gets or sets the issued certificate thumbprint when known.</summary>
        internal string? Thumbprint { get; set; }

        /// <summary>Gets or sets the issued certificate not-before when known.</summary>
        internal DateTimeOffset? NotBefore { get; set; }

        /// <summary>Gets or sets the issued certificate not-after when known.</summary>
        internal DateTimeOffset? NotAfter { get; set; }

        /// <summary>Gets or sets the live generation id that contains the certificate.</summary>
        internal string? GenerationId { get; set; }

        /// <summary>Gets or sets the failure category when issuance failed.</summary>
        internal string? FailureCategory { get; set; }

        /// <summary>Gets or sets sanitized failure detail when issuance failed.</summary>
        internal string? FailureDetail { get; set; }
    }

    /// <summary>One historical ACME lifecycle event.</summary>
    internal sealed class AcmeJournalEvent
    {
        /// <summary>Gets or sets the event type.</summary>
        internal string Type { get; set; } = string.Empty;

        /// <summary>Gets or sets when the event occurred.</summary>
        internal DateTimeOffset At { get; set; }

        /// <summary>Gets or sets the DNS-01 record name when applicable.</summary>
        internal string? RecordName { get; set; }

        /// <summary>Gets or sets the Cloudflare zone id when applicable.</summary>
        internal string? ZoneId { get; set; }

        /// <summary>Gets or sets the Cloudflare record id when applicable.</summary>
        internal string? RecordId { get; set; }

        /// <summary>Gets or sets the TXT RDATA when applicable.</summary>
        internal string? TxtContent { get; set; }

        /// <summary>Gets or sets the live generation id when applicable.</summary>
        internal string? GenerationId { get; set; }

        /// <summary>Gets or sets the certificate not-before when applicable.</summary>
        internal DateTimeOffset? NotBefore { get; set; }

        /// <summary>Gets or sets the certificate not-after when applicable.</summary>
        internal DateTimeOffset? NotAfter { get; set; }

        /// <summary>Gets or sets the failure category when applicable.</summary>
        internal string? FailureCategory { get; set; }

        /// <summary>Gets or sets sanitized failure detail when applicable.</summary>
        internal string? FailureDetail { get; set; }

        /// <summary>Gets or sets the DNS-01 recovery file id when applicable.</summary>
        internal string? RecoveryEntryId { get; set; }
    }
}

using VectorNNTPD.Common.Tests.TestDoubles;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.Common.Tests.Acme
{
    /// <summary>
    /// Proves the persistent ACME journal is one accumulating file per FQDN.
    /// </summary>
    public sealed class AcmeTransactionJournalTests
    {
        /// <summary>NNTPD hostname whose journal file is <c>nntpd01.usenet.ninja.json</c>.</summary>
        private const string Fqdn = "nntpd01.usenet.ninja";

        /// <summary>BackFiller hostname used to prove a second journal file and lock.</summary>
        private const string OtherFqdn = "backfiller01.usenet.ninja";

        /// <summary>ACME directory URL stored on each journal transaction.</summary>
        private const string DirectoryUrl = AcmeCloudflareOptions.DefaultAcmeDirectoryUrl;

        /// <summary>
        /// Two failed transactions for one FQDN append to a single <c>journal/{fqdn}.json</c> file,
        /// reload in order with failure categories and start/failure events, and do not create <c>journal/acme</c>.
        /// </summary>
        [Fact]
        public void One_journal_file_per_fqdn_accumulates_transactions()
        {
            using var dir = new TempStateDir();
            var journal = new AcmeTransactionJournal(dir.Path, Fqdn);
            var first = journal.BeginTransaction([Fqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            journal.CompleteFailure(first, "first_failure", "first", DateTimeOffset.UtcNow);
            var second = journal.BeginTransaction([Fqdn, CertificateIdentities.NewsHostname], DirectoryUrl, DateTimeOffset.UtcNow);
            journal.CompleteFailure(second, "second_failure", "second", DateTimeOffset.UtcNow);

            Assert.Equal(Path.Join(dir.Path, "journal", Fqdn + ".json"), journal.FilePath);
            Assert.True(File.Exists(journal.FilePath));
            Assert.Single(Directory.GetFiles(AcmePaths.JournalRoot(dir.Path), "*.json"));

            var reloaded = new AcmeTransactionJournal(dir.Path, Fqdn).LoadHistory();
            Assert.Equal(2, reloaded.Count);
            Assert.Equal(first, reloaded[0].Id);
            Assert.Equal(second, reloaded[1].Id);
            Assert.Equal(AcmeTransactionJournal.StatusFailed, reloaded[0].Status);
            Assert.Equal("first_failure", reloaded[0].FailureCategory);
            Assert.Equal("second_failure", reloaded[1].FailureCategory);
            Assert.Contains(reloaded[0].Events, static e => e.Type == AcmeTransactionJournal.EventCertificateRequestStarted);
            Assert.Contains(reloaded[0].Events, static e => e.Type == AcmeTransactionJournal.EventCertificateIssuanceFailed);
            Assert.False(Directory.Exists(Path.Join(dir.Path, "journal", "acme")));
            Assert.DoesNotContain(
                Directory.GetFiles(AcmePaths.JournalRoot(dir.Path), "*.json"),
                static path => !string.Equals(Path.GetFileName(path), Fqdn + ".json", StringComparison.Ordinal));
        }

        /// <summary>
        /// Journals for two FQDNs use different files, and each history contains only that FQDN's identifier.
        /// </summary>
        [Fact]
        public void Distinct_fqdns_use_distinct_journal_files()
        {
            using var dir = new TempStateDir();
            var nntpd = new AcmeTransactionJournal(dir.Path, Fqdn);
            var backfiller = new AcmeTransactionJournal(dir.Path, OtherFqdn);
            nntpd.BeginTransaction([Fqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            backfiller.BeginTransaction([OtherFqdn], DirectoryUrl, DateTimeOffset.UtcNow);

            Assert.NotEqual(nntpd.FilePath, backfiller.FilePath);
            Assert.Single(nntpd.LoadHistory());
            Assert.Single(backfiller.LoadHistory());
            Assert.Equal(Fqdn, nntpd.LoadHistory()[0].Identifiers.Single());
            Assert.Equal(OtherFqdn, backfiller.LoadHistory()[0].Identifiers.Single());
        }

        /// <summary>
        /// A successful issuance retains serial, thumbprint, not-after, generation id, and the issued/persisted/promoted event sequence.
        /// </summary>
        [Fact]
        public void Successful_certificate_fields_are_retained()
        {
            using var dir = new TempStateDir();
            var names = CertificateIdentities.ForFqdn(Fqdn);
            var material = TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(45));
            var journal = new AcmeTransactionJournal(dir.Path, Fqdn);
            var id = journal.BeginTransaction(names, DirectoryUrl, DateTimeOffset.UtcNow);
            journal.RecordCertificateIssued(material);
            journal.RecordCertificatePersisted("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", material);
            journal.RecordCertificatePromoted("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            journal.CompleteSuccess(
                id,
                material,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                TestCertificateFactory.Password,
                DateTimeOffset.UtcNow);

            var transaction = Assert.Single(new AcmeTransactionJournal(dir.Path, Fqdn).LoadHistory());
            Assert.Equal(AcmeTransactionJournal.StatusSucceeded, transaction.Status);
            Assert.False(string.IsNullOrWhiteSpace(transaction.SerialNumber));
            Assert.False(string.IsNullOrWhiteSpace(transaction.Thumbprint));
            Assert.NotNull(transaction.NotAfter);
            Assert.True(transaction.NotAfter > DateTimeOffset.UtcNow);
            Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", transaction.GenerationId);
            Assert.Equal(
                new[]
                {
                    AcmeTransactionJournal.EventCertificateRequestStarted,
                    AcmeTransactionJournal.EventCertificateIssued,
                    AcmeTransactionJournal.EventCertificatePersisted,
                    AcmeTransactionJournal.EventCertificatePromoted,
                },
                transaction.Events.Select(static e => e.Type).ToArray());
        }

        /// <summary>
        /// <see cref="AcmeTransactionJournal.BeginTransaction"/> waits on the FQDN journal lock and cancels
        /// without creating the journal file while another holder owns <see cref="AcmeTransactionJournal.LockPath"/>.
        /// After that holder releases the lock, a later begin is recorded.
        /// </summary>
        /// <returns>A task that completes after the cancelled begin and the subsequent successful begin are observed.</returns>
        [Fact]
        public async Task Same_fqdn_journal_writes_wait_for_an_independent_lock_holder()
        {
            using var dir = new TempStateDir();
            var journal = new AcmeTransactionJournal(dir.Path, Fqdn);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            using (AcmeExclusiveFileLock.Acquire(journal.LockPath))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => Task.Run(
                        () => journal.BeginTransaction([Fqdn], DirectoryUrl, DateTimeOffset.UtcNow, cts.Token),
                        CancellationToken.None));
                Assert.False(File.Exists(journal.FilePath));
            }

            var id = journal.BeginTransaction([Fqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            Assert.Equal(id, Assert.Single(journal.LoadHistory()).Id);
        }

        /// <summary>
        /// Holding the NNTPD journal lock does not block a BackFiller journal begin, and the NNTPD journal file stays absent.
        /// </summary>
        [Fact]
        public void Different_fqdn_journal_locks_are_independent()
        {
            using var dir = new TempStateDir();
            var nntpd = new AcmeTransactionJournal(dir.Path, Fqdn);
            var backfiller = new AcmeTransactionJournal(dir.Path, OtherFqdn);
            using var nntpdLock = AcmeExclusiveFileLock.Acquire(nntpd.LockPath);
            var backfillerId = backfiller.BeginTransaction([OtherFqdn], DirectoryUrl, DateTimeOffset.UtcNow);

            Assert.Equal(backfillerId, Assert.Single(backfiller.LoadHistory()).Id);
            Assert.False(File.Exists(nntpd.FilePath));
            Assert.NotEqual(nntpd.LockPath, backfiller.LockPath);
        }

        /// <summary>
        /// Three completed transactions remain in one JSON file in begin order, and the journal directory still contains a single JSON file.
        /// </summary>
        [Fact]
        public void Three_transactions_remain_cumulative_in_one_file()
        {
            using var dir = new TempStateDir();
            var journal = new AcmeTransactionJournal(dir.Path, Fqdn);
            var first = journal.BeginTransaction([Fqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            journal.CompleteFailure(first, "one", "one", DateTimeOffset.UtcNow);
            var second = journal.BeginTransaction([Fqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            journal.CompleteFailure(second, "two", "two", DateTimeOffset.UtcNow);
            var third = journal.BeginTransaction([Fqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            journal.CompleteFailure(third, "three", "three", DateTimeOffset.UtcNow);

            var history = new AcmeTransactionJournal(dir.Path, Fqdn).LoadHistory();
            Assert.Equal([first, second, third], history.Select(static item => item.Id).ToArray());
            Assert.Single(Directory.GetFiles(AcmePaths.JournalRoot(dir.Path), "*.json"));
        }

        /// <summary>
        /// A failed lock acquisition (path is a directory) propagates immediately and does not prevent a later
        /// same-path journal write once the lock path is usable again.
        /// </summary>
        [Fact]
        public void Failed_lock_acquisition_does_not_wedge_later_same_path_writers()
        {
            using var dir = new TempStateDir();
            var journal = new AcmeTransactionJournal(dir.Path, Fqdn);
            Directory.CreateDirectory(journal.LockPath);

            Assert.Throws<UnauthorizedAccessException>(
                () => journal.BeginTransaction([Fqdn], DirectoryUrl, DateTimeOffset.UtcNow, CancellationToken.None));
            Assert.False(File.Exists(journal.FilePath));

            Directory.Delete(journal.LockPath);
            var id = journal.BeginTransaction([Fqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            Assert.Equal(id, Assert.Single(journal.LoadHistory()).Id);
        }

        /// <summary>
        /// Concurrent same-FQDN journal begins remain serialized: both transactions are retained with distinct ids
        /// and no corruption of the single journal file.
        /// </summary>
        /// <returns>A task that completes after both concurrent begins finish.</returns>
        [Fact]
        public async Task Concurrent_same_path_journal_writers_remain_serialized()
        {
            using var dir = new TempStateDir();
            var journal = new AcmeTransactionJournal(dir.Path, Fqdn);
            using var barrier = new Barrier(2);

            var first = Task.Run(() =>
            {
                barrier.SignalAndWait();
                return journal.BeginTransaction([Fqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            });
            var second = Task.Run(() =>
            {
                barrier.SignalAndWait();
                return journal.BeginTransaction([Fqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            });

            var ids = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, ids.Distinct(StringComparer.Ordinal).Count());

            var history = new AcmeTransactionJournal(dir.Path, Fqdn).LoadHistory();
            Assert.Equal(2, history.Count);
            Assert.Contains(ids[0], history.Select(static t => t.Id));
            Assert.Contains(ids[1], history.Select(static t => t.Id));
            Assert.All(history, static t => Assert.Equal(AcmeTransactionJournal.StatusInProgress, t.Status));
            Assert.Single(Directory.GetFiles(AcmePaths.JournalRoot(dir.Path), "*.json"));
        }

        /// <summary>
        /// While one same-FQDN writer waits on a held journal lock, a cancelled waiter exits without permanently
        /// blocking a subsequent writer after the holder releases.
        /// </summary>
        /// <returns>A task that completes after cancel and the later successful begin are observed.</returns>
        [Fact]
        public async Task Cancelled_waiter_does_not_block_subsequent_same_path_writer()
        {
            using var dir = new TempStateDir();
            var journal = new AcmeTransactionJournal(dir.Path, Fqdn);
            using var cts = new CancellationTokenSource();

            Task<string>? waiting = null;
            try
            {
                using (AcmeExclusiveFileLock.Acquire(journal.LockPath))
                {
                    waiting = Task.Run(
                        () => journal.BeginTransaction([Fqdn], DirectoryUrl, DateTimeOffset.UtcNow, cts.Token));

                    // Observe genuine contention before cancelling.
                    Assert.False(AcmeExclusiveFileLock.TryAcquireOnce(journal.LockPath, out _));
                    cts.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
                    waiting = null;
                    Assert.False(File.Exists(journal.FilePath));
                }
            }
            finally
            {
                if (waiting is not null)
                {
                    // Holder is released by using; cancel and settle so the background task cannot outlive the test.
                    cts.Cancel();
                    var settled = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(5)));
                    if (settled == waiting && waiting.IsFaulted)
                    {
                        // Observe the fault so it is not an unobserved task exception.
                        _ = waiting.Exception;
                    }
                }
            }

            var id = journal.BeginTransaction([Fqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            Assert.Equal(id, Assert.Single(journal.LoadHistory()).Id);
        }
    }
}

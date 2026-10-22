using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Tests.TestDoubles;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.Common.Tests.Acme
{
    /// <summary>
    /// Proves NNTPD and BackFiller certificate identities can share one physical
    /// <c>certs</c> root through Common ACME paths without colliding.
    /// </summary>
    public sealed class AcmeFilesystemIsolationTests
    {
        /// <summary>NNTPD certificate hostname used to scope live, DNS-01, and journal paths.</summary>
        private const string NntpdFqdn = "nntpd01.usenet.ninja";

        /// <summary>BackFiller certificate hostname kept distinct from <see cref="NntpdFqdn"/> under the same certs root.</summary>
        private const string BackFillerFqdn = "backfiller01.usenet.ninja";

        /// <summary>ACME directory URL recorded by issuance transactions in these tests.</summary>
        private const string DirectoryUrl = AcmeCloudflareOptions.DefaultAcmeDirectoryUrl;

        /// <summary>
        /// Two <see cref="AccountStore"/> instances on the same root share <c>account/private_key.der</c> and the saved account URI.
        /// Per-hostname account key files are not created.
        /// </summary>
        [Fact]
        public void Shared_account_state_is_common_across_identities()
        {
            using var dir = new TempStateDir();
            var first = new AccountStore(dir.Path);
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var key = rsa.ExportPkcs8PrivateKey();
            first.Save(new AcmeAccountState(
                "https://acme.example/acme/acct/1",
                DirectoryUrl,
                key));

            var second = new AccountStore(dir.Path);
            var loaded = second.Load();
            Assert.NotNull(loaded);
            Assert.Equal("https://acme.example/acme/acct/1", loaded.AccountUri);
            Assert.True(key.AsSpan().SequenceEqual(loaded.PrivateKeyDer));
            Assert.Equal(AcmePaths.AccountKeyPath(dir.Path), Path.Join(dir.Path, "account", "private_key.der"));
            Assert.False(File.Exists(Path.Join(dir.Path, "account", "nntpd01_private_key.der")));
            Assert.False(File.Exists(Path.Join(dir.Path, "account", "backfiller01_private_key.der")));
        }

        /// <summary>
        /// DNS-01 recovery, the transaction journal, the current-generation pointer, generations, and the issuance lock
        /// are paths under the FQDN, and the NNTPD and BackFiller current pointers differ.
        /// Legacy <c>dns01/journal</c>, <c>journal/dns01</c>, and <c>journal/acme</c> segments are absent.
        /// </summary>
        [Fact]
        public void Common_paths_are_fqdn_scoped_under_one_certs_root()
        {
            using var dir = new TempStateDir();
            Assert.Equal(
                Path.Join(dir.Path, "live", NntpdFqdn, "dns01"),
                AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn));
            Assert.Equal(
                Path.Join(dir.Path, "journal", BackFillerFqdn + ".json"),
                AcmePaths.TransactionJournalPath(dir.Path, BackFillerFqdn));
            Assert.Equal(
                Path.Join(dir.Path, "live", NntpdFqdn, "current"),
                AcmePaths.CurrentGenerationPointerPath(dir.Path, NntpdFqdn));
            Assert.Equal(
                Path.Join(dir.Path, "live", NntpdFqdn, "gens"),
                AcmePaths.GenerationsDir(dir.Path, NntpdFqdn));
            Assert.Equal(
                Path.Join(dir.Path, "live", BackFillerFqdn, ".issuance.lock"),
                AcmePaths.IssuanceLockPath(dir.Path, BackFillerFqdn));
            Assert.NotEqual(
                AcmePaths.CurrentGenerationPointerPath(dir.Path, NntpdFqdn),
                AcmePaths.CurrentGenerationPointerPath(dir.Path, BackFillerFqdn));
            Assert.DoesNotContain(
                Path.Join("dns01", "journal"),
                AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                Path.Join("journal", "dns01"),
                AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                Path.Join("journal", "acme"),
                AcmePaths.TransactionJournalPath(dir.Path, NntpdFqdn),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Recovering the NNTPD DNS-01 solver deletes only that FQDN's recovery file and TXT record.
        /// The BackFiller recovery file and TXT record remain.
        /// </summary>
        /// <returns>A task that completes after both challenges are placed and only the NNTPD challenge is recovered.</returns>
        [Fact]
        public async Task Dns01_recovery_for_one_fqdn_does_not_touch_another()
        {
            using var dir = new TempStateDir();
            var client = new InMemoryCloudflareDnsClient();
            var nntpd = CreateSolver(client, NntpdFqdn, dir.Path);
            var backfiller = CreateSolver(client, BackFillerFqdn, dir.Path);
            var nntpdSpec = new Dns01ChallengeSpec(NntpdFqdn, "nntpd-token");
            var backfillerSpec = new Dns01ChallengeSpec(BackFillerFqdn, "backfiller-token");
            var resolver = new ImmediateTxtResolver();
            resolver.Set(nntpdSpec.RecordName, nntpdSpec.Validation);
            resolver.Set(backfillerSpec.RecordName, backfillerSpec.Validation);

            await nntpd.PlaceAsync([nntpdSpec], CancellationToken.None);
            await backfiller.PlaceAsync([backfillerSpec], CancellationToken.None);

            var nntpdJournal = Directory.GetFiles(AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn), "*.json");
            var backfillerJournal = Directory.GetFiles(AcmePaths.Dns01RecoveryDir(dir.Path, BackFillerFqdn), "*.json");
            Assert.Single(nntpdJournal);
            Assert.Single(backfillerJournal);
            Assert.Equal(2, client.Snapshot().Count(static r => r.Type == CloudflareDnsRecordTypes.TXT));

            var recovered = CreateSolver(client, NntpdFqdn, dir.Path);
            await recovered.RecoverAsync(CancellationToken.None);

            Assert.Empty(Directory.GetFiles(AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn), "*.json"));
            Assert.Single(Directory.GetFiles(AcmePaths.Dns01RecoveryDir(dir.Path, BackFillerFqdn), "*.json"));
            Assert.DoesNotContain(client.Snapshot(), static r => r.Name == "_acme-challenge." + NntpdFqdn);
            Assert.Contains(client.Snapshot(), static r => r.Name == "_acme-challenge." + BackFillerFqdn);
        }

        /// <summary>
        /// Concurrent DNS-01 placements for two FQDNs create two TXT records and two recovery files, and the NNTPD journal records that FQDN.
        /// </summary>
        /// <returns>A task that completes after both placements finish.</returns>
        [Fact]
        public async Task Simultaneous_dns01_requests_for_different_fqdns_are_independent()
        {
            using var dir = new TempStateDir();
            var client = new InMemoryCloudflareDnsClient();
            var nntpd = CreateSolver(client, NntpdFqdn, dir.Path);
            var backfiller = CreateSolver(client, BackFillerFqdn, dir.Path);
            var nntpdSpec = new Dns01ChallengeSpec(NntpdFqdn, "nntpd-token");
            var backfillerSpec = new Dns01ChallengeSpec(BackFillerFqdn, "backfiller-token");

            await Task.WhenAll(
                nntpd.PlaceAsync([nntpdSpec], CancellationToken.None),
                backfiller.PlaceAsync([backfillerSpec], CancellationToken.None));

            Assert.Equal(2, client.Snapshot().Count(static r => r.Type == CloudflareDnsRecordTypes.TXT));
            Assert.True(File.Exists(
                Directory.GetFiles(AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn), "*.json").Single()));
            Assert.True(File.Exists(
                Directory.GetFiles(AcmePaths.Dns01RecoveryDir(dir.Path, BackFillerFqdn), "*.json").Single()));
            using var nntpdDoc = JsonDocument.Parse(
                File.ReadAllText(Directory.GetFiles(AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn), "*.json").Single()));
            Assert.Equal(NntpdFqdn, nntpdDoc.RootElement.GetProperty("fqdn").GetString());
        }

        /// <summary>
        /// Recovering a seeded NNTPD DNS-01 journal removes that entry and its TXT record and leaves the BackFiller entry and record in place.
        /// </summary>
        /// <returns>A task that completes after NNTPD recovery finishes.</returns>
        [Fact]
        public async Task Dns01_crash_recovery_stays_inside_the_requested_fqdn()
        {
            using var dir = new TempStateDir();
            var client = new InMemoryCloudflareDnsClient();
            client.Seed(
                Txt("nntpd-rec", "_acme-challenge." + NntpdFqdn, "nntpd-token"),
                Txt("bf-rec", "_acme-challenge." + BackFillerFqdn, "backfiller-token"));
            WriteDns01Journal(
                dir.Path,
                NntpdFqdn,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "placed",
                "nntpd-rec",
                "_acme-challenge." + NntpdFqdn,
                "nntpd-token");
            WriteDns01Journal(
                dir.Path,
                BackFillerFqdn,
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "placed",
                "bf-rec",
                "_acme-challenge." + BackFillerFqdn,
                "backfiller-token");

            var solver = CreateSolver(client, NntpdFqdn, dir.Path);
            await solver.RecoverAsync(CancellationToken.None);

            Assert.False(File.Exists(Path.Join(
                AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn),
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.json")));
            Assert.True(File.Exists(Path.Join(
                AcmePaths.Dns01RecoveryDir(dir.Path, BackFillerFqdn),
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.json")));
            Assert.DoesNotContain(client.Snapshot(), static r => r.Id == "nntpd-rec");
            Assert.Contains(client.Snapshot(), static r => r.Id == "bf-rec");
        }

        /// <summary>
        /// <see cref="CertificateManager"/> appends three NNTPD journal transactions: successes with generation, serial, thumbprint, and not-after,
        /// and one <c>finalize_failed</c> failure. The BackFiller journal file is not created.
        /// </summary>
        /// <returns>A task that completes after the initial issuance and the failed then successful renewal are recorded.</returns>
        [Fact]
        public async Task Issuance_ledger_records_request_success_expiry_generation_and_failure()
        {
            using var dir = new TempStateDir();
            var nntpdNames = CertificateIdentities.ForFqdn(NntpdFqdn);
            var store = new CertificateStore(dir.Path, NntpdFqdn, TestCertificateFactory.Password, nntpdNames, TimeSpan.FromDays(30));
            var issuer = new ScriptedIssuer();
            var manager = new CertificateManager(
                NntpdFqdn,
                dir.Path,
                DirectoryUrl,
                store,
                issuer,
                TestCertificateFactory.Password,
                TimeSpan.FromDays(30),
                NullLogger<CertificateManager>.Instance);

            await manager.EnsureCertificateAsync(CancellationToken.None);
            store.Save(TestCertificateFactory.CreateMaterial(nntpdNames, DateTimeOffset.UtcNow.AddDays(5)));
            issuer.ThrowOnIssue = new AcmeOrderException("finalize_failed", "ca rejected");
            Assert.False(await manager.RenewIfDueAsync(CancellationToken.None));
            issuer.ThrowOnIssue = null;
            Assert.True(await manager.RenewIfDueAsync(CancellationToken.None));

            var journal = new AcmeTransactionJournal(dir.Path, NntpdFqdn);
            var history = journal.LoadHistory();
            Assert.Equal(3, history.Count);
            Assert.Contains(history, static r => r.Status == AcmeTransactionJournal.StatusSucceeded && r.GenerationId is not null);
            Assert.Contains(history, static r => r.Status == AcmeTransactionJournal.StatusFailed && r.FailureCategory == "finalize_failed");
            Assert.All(history, static r => Assert.Equal(DirectoryUrl, r.DirectoryUrl));
            Assert.All(
                history,
                static r => Assert.Contains(
                    AcmeTransactionJournal.EventCertificateRequestStarted,
                    r.Events.Select(static e => e.Type)));

            var success = history.Last(static r => r.Status == AcmeTransactionJournal.StatusSucceeded);
            Assert.False(string.IsNullOrWhiteSpace(success.SerialNumber));
            Assert.False(string.IsNullOrWhiteSpace(success.Thumbprint));
            Assert.NotNull(success.NotAfter);
            Assert.NotNull(success.GenerationId);
            Assert.Equal(store.CurrentGenerationId(), success.GenerationId);
            Assert.Contains(success.Events, static e => e.Type == AcmeTransactionJournal.EventCertificateIssued);
            Assert.Contains(success.Events, static e => e.Type == AcmeTransactionJournal.EventCertificatePersisted);
            Assert.Contains(success.Events, static e => e.Type == AcmeTransactionJournal.EventCertificatePromoted);
            var failed = history.Single(static r => r.Status == AcmeTransactionJournal.StatusFailed);
            Assert.Contains(failed.Events, static e => e.Type == AcmeTransactionJournal.EventCertificateIssuanceFailed);
            Assert.Equal(
                Path.Join(dir.Path, "journal", NntpdFqdn + ".json"),
                journal.FilePath);
            Assert.True(File.Exists(journal.FilePath));
            Assert.False(Directory.Exists(Path.Join(dir.Path, "journal", "acme")));
            Assert.Empty(new AcmeTransactionJournal(dir.Path, BackFillerFqdn).LoadHistory());
            Assert.False(File.Exists(AcmePaths.TransactionJournalPath(dir.Path, BackFillerFqdn)));
        }

        /// <summary>
        /// Saving and recovering an NNTPD generation updates only the NNTPD current pointer.
        /// The BackFiller generation directory, pointer, and domain list without <c>news.usenet.ninja</c> stay unchanged.
        /// </summary>
        [Fact]
        public void Live_generation_gc_and_current_pointer_are_fqdn_scoped()
        {
            using var dir = new TempStateDir();
            var nntpdNames = CertificateIdentities.ForFqdn(NntpdFqdn);
            var backfillerNames = CertificateIdentities.ForFqdn(BackFillerFqdn, includeNewsHostname: false);
            var nntpd = new CertificateStore(dir.Path, NntpdFqdn, TestCertificateFactory.Password, nntpdNames, TimeSpan.FromDays(30));
            var backfiller = new CertificateStore(dir.Path, BackFillerFqdn, TestCertificateFactory.Password, backfillerNames, TimeSpan.FromDays(30));

            nntpd.Save(TestCertificateFactory.CreateMaterial(nntpdNames, DateTimeOffset.UtcNow.AddDays(60)));
            backfiller.Save(TestCertificateFactory.CreateMaterial(backfillerNames, DateTimeOffset.UtcNow.AddDays(60)));
            var nntpdCurrent = File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, NntpdFqdn)).Trim();
            var backfillerCurrent = File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, BackFillerFqdn)).Trim();
            var backfillerGenDir = AcmePaths.GenerationDir(dir.Path, BackFillerFqdn, backfillerCurrent);

            nntpd.Save(TestCertificateFactory.CreateMaterial(nntpdNames, DateTimeOffset.UtcNow.AddDays(90)));
            nntpd.Recover();

            Assert.True(Directory.Exists(backfillerGenDir));
            Assert.Equal(backfillerCurrent, File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, BackFillerFqdn)).Trim());
            Assert.NotEqual(nntpdCurrent, File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, NntpdFqdn)).Trim());
            Assert.Contains("news.usenet.ninja", nntpd.Load()!.Domains);
            Assert.DoesNotContain("news.usenet.ninja", backfiller.Load()!.Domains);
            Assert.Single(Directory.GetDirectories(AcmePaths.GenerationsDir(dir.Path, BackFillerFqdn)));
        }

        /// <summary>
        /// NNTPD and BackFiller issuance locks can be held together, their paths differ, and a second acquire of the NNTPD lock cancels.
        /// </summary>
        /// <returns>A task that completes after the second NNTPD acquire is observed to cancel.</returns>
        [Fact]
        public async Task Issuance_lock_is_exclusive_per_fqdn_and_independent_across_fqdns()
        {
            using var dir = new TempStateDir();
            await using var nntpdLock = await AcmeIssuanceLock.AcquireAsync(dir.Path, NntpdFqdn, CancellationToken.None);
            await using var backfillerLock = await AcmeIssuanceLock.AcquireAsync(dir.Path, BackFillerFqdn, CancellationToken.None);
            Assert.Equal(AcmePaths.IssuanceLockPath(dir.Path, NntpdFqdn), Path.Join(dir.Path, "live", NntpdFqdn, ".issuance.lock"));
            Assert.NotEqual(
                AcmePaths.IssuanceLockPath(dir.Path, NntpdFqdn),
                AcmePaths.IssuanceLockPath(dir.Path, BackFillerFqdn));

            using var held = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => AcmeIssuanceLock.AcquireAsync(dir.Path, NntpdFqdn, held.Token));
        }

        /// <summary>
        /// Concurrent issuance into one certs root writes distinct current pointers, generation directories, and journal files,
        /// does not create global <c>live/gens</c>, <c>live/current</c>, <c>journal/acme</c>, <c>journal/dns01</c>, or <c>dns01</c>,
        /// and the TLS certificates carry the NNTPD names plus <see cref="CertificateIdentities.NewsHostname"/> versus only the BackFiller name.
        /// The account key remains the shared <c>account/private_key.der</c>.
        /// </summary>
        /// <returns>A task that completes after both certificate managers finish issuance.</returns>
        [Fact]
        public async Task Same_physical_certs_root_keeps_nntpd_and_backfiller_state_apart()
        {
            using var dir = new TempStateDir();
            var nntpdNames = CertificateIdentities.ForFqdn(NntpdFqdn);
            var backfillerNames = CertificateIdentities.ForFqdn(BackFillerFqdn, includeNewsHostname: false);
            var nntpdStore = new CertificateStore(dir.Path, NntpdFqdn, TestCertificateFactory.Password, nntpdNames, TimeSpan.FromDays(30));
            var backfillerStore = new CertificateStore(dir.Path, BackFillerFqdn, TestCertificateFactory.Password, backfillerNames, TimeSpan.FromDays(30));
            var nntpdManager = new CertificateManager(
                NntpdFqdn,
                dir.Path,
                DirectoryUrl,
                nntpdStore,
                new ScriptedIssuer(),
                TestCertificateFactory.Password,
                TimeSpan.FromDays(30),
                NullLogger<CertificateManager>.Instance,
                includeNewsHostname: true);
            var backfillerManager = new CertificateManager(
                BackFillerFqdn,
                dir.Path,
                DirectoryUrl,
                backfillerStore,
                new ScriptedIssuer(),
                TestCertificateFactory.Password,
                TimeSpan.FromDays(30),
                NullLogger<CertificateManager>.Instance,
                includeNewsHostname: false);

            await Task.WhenAll(
                nntpdManager.EnsureCertificateAsync(CancellationToken.None),
                backfillerManager.EnsureCertificateAsync(CancellationToken.None));

            Assert.Equal(
                Path.Join(dir.Path, "live", NntpdFqdn, "current"),
                AcmePaths.CurrentGenerationPointerPath(dir.Path, NntpdFqdn));
            Assert.Equal(
                Path.Join(dir.Path, "live", BackFillerFqdn, "current"),
                AcmePaths.CurrentGenerationPointerPath(dir.Path, BackFillerFqdn));
            Assert.NotEqual(
                File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, NntpdFqdn)),
                File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, BackFillerFqdn)));
            Assert.True(Directory.Exists(AcmePaths.GenerationsDir(dir.Path, NntpdFqdn)));
            Assert.True(Directory.Exists(AcmePaths.GenerationsDir(dir.Path, BackFillerFqdn)));
            Assert.NotEmpty(new AcmeTransactionJournal(dir.Path, NntpdFqdn).LoadHistory());
            Assert.NotEmpty(new AcmeTransactionJournal(dir.Path, BackFillerFqdn).LoadHistory());
            Assert.True(File.Exists(AcmePaths.TransactionJournalPath(dir.Path, NntpdFqdn)));
            Assert.True(File.Exists(AcmePaths.TransactionJournalPath(dir.Path, BackFillerFqdn)));
            Assert.False(Directory.Exists(Path.Join(dir.Path, "live", "gens")));
            Assert.False(File.Exists(Path.Join(dir.Path, "live", "current")));
            Assert.False(Directory.Exists(Path.Join(dir.Path, "journal", "acme")));
            Assert.False(Directory.Exists(Path.Join(dir.Path, "journal", "dns01")));
            Assert.False(Directory.Exists(Path.Join(dir.Path, "dns01")));
            using var nntpdCert = nntpdManager.CreateTlsCertificate();
            using var backfillerCert = backfillerManager.CreateTlsCertificate();
            var nntpdSans = nntpdCert.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single()
                .EnumerateDnsNames().Select(static n => n.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var backfillerSans = backfillerCert.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single()
                .EnumerateDnsNames().Select(static n => n.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            Assert.Contains(NntpdFqdn, nntpdSans);
            Assert.Contains(CertificateIdentities.NewsHostname, nntpdSans);
            Assert.Equal([BackFillerFqdn], backfillerSans);
            Assert.Equal(AcmePaths.AccountKeyPath(dir.Path), Path.Join(dir.Path, "account", "private_key.der"));
        }

        /// <summary>
        /// <see cref="AcmePaths.EnsureStateLayout"/> does not create <c>live</c> or legacy journal directories.
        /// Saving a certificate creates only the FQDN generation, current pointer, and DNS-01 recovery directories.
        /// </summary>
        [Fact]
        public void Production_layout_never_creates_global_live_gens_or_current()
        {
            using var dir = new TempStateDir();
            AcmePaths.EnsureStateLayout(dir.Path);
            Assert.False(Directory.Exists(Path.Join(dir.Path, "live")));
            Assert.False(Directory.Exists(Path.Join(dir.Path, "journal", "acme")));
            Assert.False(Directory.Exists(Path.Join(dir.Path, "journal", "dns01")));
            Assert.False(Directory.Exists(Path.Join(dir.Path, "dns01")));

            AcmePaths.EnsureCertificateIdentityLayout(dir.Path, NntpdFqdn);
            var names = CertificateIdentities.ForFqdn(NntpdFqdn);
            var store = new CertificateStore(dir.Path, NntpdFqdn, TestCertificateFactory.Password, names, TimeSpan.FromDays(30));
            store.Save(TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(60)));

            Assert.True(Directory.Exists(AcmePaths.GenerationsDir(dir.Path, NntpdFqdn)));
            Assert.True(File.Exists(AcmePaths.CurrentGenerationPointerPath(dir.Path, NntpdFqdn)));
            Assert.True(Directory.Exists(AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn)));
            Assert.False(Directory.Exists(Path.Join(dir.Path, "live", "gens")));
            Assert.False(File.Exists(Path.Join(dir.Path, "live", "current")));
            Assert.False(File.Exists(Path.Join(dir.Path, ".issuance.lock")));
            Assert.False(Directory.Exists(Path.Join(dir.Path, "journal", "acme")));
            Assert.False(Directory.Exists(Path.Join(dir.Path, "journal", "dns01")));
            Assert.False(Directory.Exists(Path.Join(dir.Path, "dns01")));
        }

        /// <summary>
        /// After place, propagation wait, and cleanup, the DNS-01 recovery directory is empty while the transaction journal
        /// keeps created, propagated, and removed events. The other FQDN's journal stays empty, and <c>journal/dns01</c> is not created.
        /// </summary>
        /// <returns>A task that completes after cleanup and the journal assertions.</returns>
        [Fact]
        public async Task Dns01_history_is_permanent_and_recovery_state_is_transient()
        {
            using var dir = new TempStateDir();
            var client = new InMemoryCloudflareDnsClient();
            var journal = new AcmeTransactionJournal(dir.Path, NntpdFqdn);
            journal.BeginTransaction([NntpdFqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            var spec = new Dns01ChallengeSpec(NntpdFqdn, "nntpd-token");
            var resolver = new ImmediateTxtResolver();
            resolver.Set(spec.RecordName, spec.Validation);
            var recorded = new Dns01Solver(
                client,
                "zone-test",
                resolver,
                NntpdFqdn,
                dir.Path,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10),
                journal);

            await recorded.PlaceAsync([spec], CancellationToken.None);
            Assert.Single(Directory.GetFiles(AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn), "*.json"));
            await recorded.WaitPropagatedAsync([spec], CancellationToken.None);
            await recorded.CleanupAsync(CancellationToken.None);

            Assert.Empty(Directory.GetFiles(AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn), "*.json"));
            Assert.True(File.Exists(journal.FilePath));
            Assert.False(Directory.Exists(Path.Join(dir.Path, "journal", "dns01")));
            var transaction = Assert.Single(journal.LoadHistory());
            Assert.Contains(
                transaction.Events,
                static e => e.Type == AcmeTransactionJournal.EventDnsChallengeCreated
                            && e.RecordName == "_acme-challenge." + NntpdFqdn
                            && e.ZoneId == "zone-test"
                            && e.RecordId is not null
                            && e.TxtContent == "nntpd-token");
            Assert.Contains(
                transaction.Events,
                static e => e.Type == AcmeTransactionJournal.EventDnsChallengePropagated
                            && e.RecordName == "_acme-challenge." + NntpdFqdn);
            Assert.Contains(
                transaction.Events,
                static e => e.Type == AcmeTransactionJournal.EventDnsChallengeRemoved
                            && e.RecordName == "_acme-challenge." + NntpdFqdn
                            && e.TxtContent == "nntpd-token");
            Assert.Empty(new AcmeTransactionJournal(dir.Path, BackFillerFqdn).LoadHistory());
        }

        /// <summary>
        /// Recovery of a DNS-01 entry that stored its transaction id appends the recovered-and-removed event to that original transaction,
        /// not to a later begin, and leaves no unattributed events or TXT records.
        /// </summary>
        /// <returns>A task that completes after the orphan is recovered onto the original transaction.</returns>
        [Fact]
        public async Task Orphan_dns_recovery_attaches_to_the_original_transaction_when_id_is_persisted()
        {
            using var dir = new TempStateDir();
            var journal = new AcmeTransactionJournal(dir.Path, NntpdFqdn);
            var originalId = journal.BeginTransaction([NntpdFqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            var client = new InMemoryCloudflareDnsClient();
            var solver = new Dns01Solver(
                client,
                "zone-test",
                new ImmediateTxtResolver(),
                NntpdFqdn,
                dir.Path,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10),
                journal);
            var spec = new Dns01ChallengeSpec(NntpdFqdn, "nntpd-token");
            await solver.PlaceAsync([spec], CancellationToken.None);
            Assert.Contains(client.Snapshot(), static r => r.Type == CloudflareDnsRecordTypes.TXT);

            var recoveredJournal = new AcmeTransactionJournal(dir.Path, NntpdFqdn);
            var unrelated = recoveredJournal.BeginTransaction([NntpdFqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            var recovered = new Dns01Solver(
                client,
                "zone-test",
                new ImmediateTxtResolver(),
                NntpdFqdn,
                dir.Path,
                historyJournal: recoveredJournal);
            await recovered.RecoverAsync(CancellationToken.None);

            Assert.Empty(Directory.GetFiles(AcmePaths.Dns01RecoveryDir(dir.Path, NntpdFqdn), "*.json"));
            Assert.DoesNotContain(client.Snapshot(), static r => r.Type == CloudflareDnsRecordTypes.TXT);
            var history = recoveredJournal.LoadHistory();
            Assert.Equal(2, history.Count);
            var original = history.Single(item => item.Id == originalId);
            var later = history.Single(item => item.Id == unrelated);
            Assert.Contains(
                original.Events,
                static e => e.Type == AcmeTransactionJournal.EventDnsChallengeCreated);
            Assert.Contains(
                original.Events,
                e => e.Type == AcmeTransactionJournal.EventDnsChallengeRecoveredAndRemoved
                     && e.RecordName == spec.RecordName
                     && e.ZoneId == "zone-test"
                     && e.TxtContent == "nntpd-token"
                     && e.RecoveryEntryId is not null);
            Assert.DoesNotContain(
                later.Events,
                static e => e.Type == AcmeTransactionJournal.EventDnsChallengeRecoveredAndRemoved);
            Assert.Empty(recoveredJournal.LoadUnattributedEvents());
        }

        /// <summary>
        /// A recovery file with no transaction id is removed from DNS and recorded as an unattributed recovered-and-removed event.
        /// A transaction begun before recovery does not receive that event.
        /// </summary>
        /// <returns>A task that completes after the unattributed recovery is observed.</returns>
        [Fact]
        public async Task Orphan_dns_recovery_without_transaction_id_is_not_attached_to_a_later_request()
        {
            using var dir = new TempStateDir();
            var client = new InMemoryCloudflareDnsClient();
            client.Seed(Txt("orphan-rec", "_acme-challenge." + NntpdFqdn, "orphan-token"));
            WriteDns01Journal(
                dir.Path,
                NntpdFqdn,
                "cccccccccccccccccccccccccccccccc",
                "placed",
                "orphan-rec",
                "_acme-challenge." + NntpdFqdn,
                "orphan-token");

            var journal = new AcmeTransactionJournal(dir.Path, NntpdFqdn);
            var laterId = journal.BeginTransaction([NntpdFqdn], DirectoryUrl, DateTimeOffset.UtcNow);
            var solver = new Dns01Solver(
                client,
                "zone-test",
                new ImmediateTxtResolver(),
                NntpdFqdn,
                dir.Path,
                historyJournal: journal);
            await solver.RecoverAsync(CancellationToken.None);

            var later = Assert.Single(journal.LoadHistory());
            Assert.Equal(laterId, later.Id);
            Assert.DoesNotContain(
                later.Events,
                static e => e.Type == AcmeTransactionJournal.EventDnsChallengeRecoveredAndRemoved);
            var recovered = Assert.Single(journal.LoadUnattributedEvents());
            Assert.Equal(AcmeTransactionJournal.EventDnsChallengeRecoveredAndRemoved, recovered.Type);
            Assert.Equal("_acme-challenge." + NntpdFqdn, recovered.RecordName);
            Assert.Equal("orphan-rec", recovered.RecordId);
            Assert.Equal("cccccccccccccccccccccccccccccccc", recovered.RecoveryEntryId);
            Assert.DoesNotContain(client.Snapshot(), static r => r.Id == "orphan-rec");
        }

        /// <summary>
        /// Builds a <see cref="Dns01Solver"/> for <paramref name="fqdn"/> with a short propagation timeout and the shared in-memory DNS client.
        /// </summary>
        /// <param name="client">DNS client that records TXT placements.</param>
        /// <param name="fqdn">Certificate hostname whose recovery directory is used.</param>
        /// <param name="stateDir">Certs root passed to the solver.</param>
        /// <returns>A solver that resolves TXT records immediately.</returns>
        private static Dns01Solver CreateSolver(ICloudflareDnsClient client, string fqdn, string stateDir) =>
            new(
                client,
                "zone-test",
                new ImmediateTxtResolver(),
                fqdn,
                stateDir,
                propagationTimeout: TimeSpan.FromSeconds(2),
                propagationInterval: TimeSpan.FromMilliseconds(10));

        /// <summary>
        /// Writes a version-1 DNS-01 recovery JSON file for <paramref name="entryId"/> under the FQDN recovery directory.
        /// </summary>
        /// <param name="stateDir">Certs root.</param>
        /// <param name="fqdn">Hostname stored in the payload and used to choose the directory.</param>
        /// <param name="entryId">Recovery entry id and file name stem.</param>
        /// <param name="phase">Recovery phase written to the payload.</param>
        /// <param name="recordId">Cloudflare record id written to the payload.</param>
        /// <param name="name">TXT record name written to the payload.</param>
        /// <param name="content">TXT content written to the payload.</param>
        private static void WriteDns01Journal(
            string stateDir,
            string fqdn,
            string entryId,
            string phase,
            string recordId,
            string name,
            string content)
        {
            AcmePaths.EnsureCertificateIdentityLayout(stateDir, fqdn);
            var payload = """
                {
                  "version": 1,
                  "entry_id": "%ENTRY%",
                  "phase": "%PHASE%",
                  "zone_id": "zone-test",
                  "name": "%NAME%",
                  "content": "%CONTENT%",
                  "record_id": "%RECORD%",
                  "fqdn": "%FQDN%"
                }
                """
                .Replace("%ENTRY%", entryId, StringComparison.Ordinal)
                .Replace("%PHASE%", phase, StringComparison.Ordinal)
                .Replace("%NAME%", name, StringComparison.Ordinal)
                .Replace("%CONTENT%", content, StringComparison.Ordinal)
                .Replace("%RECORD%", recordId, StringComparison.Ordinal)
                .Replace("%FQDN%", fqdn, StringComparison.Ordinal);
            File.WriteAllText(Path.Join(AcmePaths.Dns01RecoveryDir(stateDir, fqdn), entryId + ".json"), payload);
        }

        /// <summary>
        /// Builds an unproxied TXT <see cref="CloudflareDnsRecord"/> using <see cref="Dns01Solver.ChallengeTtlSeconds"/>.
        /// </summary>
        /// <param name="id">Record id.</param>
        /// <param name="name">Record name.</param>
        /// <param name="content">TXT content.</param>
        /// <returns>The seeded DNS record.</returns>
        private static CloudflareDnsRecord Txt(string id, string name, string content) =>
            new()
            {
                Id = id,
                Type = CloudflareDnsRecordTypes.TXT,
                Name = name,
                Content = content,
                Ttl = Dns01Solver.ChallengeTtlSeconds,
                Proxied = false,
            };

        /// <summary>
        /// Issuer that returns test certificate material, or throws <see cref="ThrowOnIssue"/> when that property is set.
        /// </summary>
        private sealed class ScriptedIssuer : ICertificateIssuer
        {
            /// <summary>
            /// Exception thrown by the next <see cref="IssueAsync"/> call. A null value means issuance succeeds.
            /// </summary>
            public Exception? ThrowOnIssue { get; set; }

            /// <summary>
            /// Returns material for <paramref name="domains"/> valid for 90 days, or throws <see cref="ThrowOnIssue"/> when set.
            /// </summary>
            /// <param name="domains">Names placed on the issued material.</param>
            /// <param name="cancellationToken">Observed before issuance. A cancelled token fails the call.</param>
            /// <returns>A task that yields the scripted certificate material when issuance is allowed.</returns>
            /// <exception cref="Exception">Thrown when <see cref="ThrowOnIssue"/> is non-null. The stored exception is rethrown as-is.</exception>
            /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is already cancelled.</exception>
            public Task<CertificateMaterial> IssueAsync(
                IReadOnlyList<string> domains,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ThrowOnIssue is not null)
                {
                    throw ThrowOnIssue;
                }

                return Task.FromResult(TestCertificateFactory.CreateMaterial(domains, DateTimeOffset.UtcNow.AddDays(90)));
            }
        }
    }
}

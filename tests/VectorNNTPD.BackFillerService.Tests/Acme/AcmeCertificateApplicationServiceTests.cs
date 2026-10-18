using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Acme;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.BackFillerService.Hosting;
using VectorNNTPD.BackFillerService.Hosting.Systemd;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.Common.Networking.Certificates;

namespace VectorNNTPD.BackFillerService.Tests.Acme
{
    /// <summary>
    /// Acme certificate application service lifecycle with test doubles.
    /// </summary>

    public sealed class AcmeCertificateApplicationServiceTests
    {
        /// <summary>
        /// Contract: Adapter implements application service and forwards identity.
        /// </summary>

        [Fact]
        public void Adapter_implements_application_service_and_forwards_identity()
        {
            using var harness = CreateTlsDisabledHarness();
            Assert.IsAssignableFrom<IApplicationService>(harness.Adapter);
            Assert.Equal("AcmeCertificate", harness.Adapter.Name);
            Assert.Null(harness.Adapter.Execution);
        }

        /// <summary>
        /// Contract: StartAsync without readiness fails fast and does not journal.
        /// </summary>

        [Fact]
        public async Task StartAsync_without_readiness_fails_fast_and_does_not_journal()
        {
            using var harness = CreateTlsDisabledHarness();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => harness.Adapter.StartAsync(CancellationToken.None));
            Assert.Contains("usable certificate", ex.Message, StringComparison.Ordinal);
            Assert.Empty(harness.Journal.Stages);
            Assert.False(harness.Readiness.IsReady);
            Assert.Null(harness.Adapter.Execution);
        }

        /// <summary>
        /// Contract: StartAsync with existing certificate journals ready and keeps renewal execution.
        /// </summary>

        [Fact]
        public async Task StartAsync_with_existing_certificate_journals_ready_and_keeps_renewal_execution()
        {
            using var harness = CreateReusableCertificateHarness();

            await harness.Adapter.StartAsync(CancellationToken.None);
            try
            {
                Assert.True(harness.Readiness.IsReady);
                Assert.Equal([BackFillerStartupStages.AcmeCertificateReady], harness.Journal.Stages);
                Assert.Same(harness.Inner.Execution, harness.Adapter.Execution);
                Assert.NotNull(harness.Adapter.Execution);
                Assert.False(harness.Adapter.Execution.IsCompleted);
            }
            finally
            {
                await harness.Adapter.StopAsync(CancellationToken.None);
                await harness.Adapter.DisposeAsync();
            }

            Assert.True(harness.Adapter.Execution?.IsCompleted);
        }

        /// <summary>
        /// Contract: StopAsync and DisposeAsync delegate to the common service.
        /// </summary>

        [Fact]
        public async Task StopAsync_and_DisposeAsync_delegate_to_the_common_service()
        {
            using var harness = CreateReusableCertificateHarness();
            await harness.Adapter.StartAsync(CancellationToken.None);
            var execution = harness.Adapter.Execution;
            Assert.NotNull(execution);

            await harness.Adapter.StopAsync(CancellationToken.None);
            Assert.True(execution.IsCompleted);

            await harness.Adapter.DisposeAsync();
        }

        /// <summary>
        /// A usable certificate outside the renewal window stays healthy after one injected renewal fault.
        /// </summary>
        [Fact]
        public async Task Transient_renewal_fault_outside_the_window_stays_healthy()
        {
            var started = DateTimeOffset.UtcNow;
            await using var observed = CreateObservedRenewal(started, new ThrowingIssuer(), notAfter: started.AddDays(90), seedDisk: true);
            observed.Health.MarkStarted();
            await observed.Service.StartAsync(CancellationToken.None);
            Assert.True(observed.Health.IsHealthyForWatchdog);

            observed.Service.RenewalFaultsToInject = 1;
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => observed.Service.RenewalWaitsStarted == 1, TimeSpan.FromSeconds(2));
            observed.Time.Advance(TimeSpan.FromHours(6));
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => observed.Service.RenewalChecksCompleted == 1, TimeSpan.FromSeconds(2));

            Assert.True(observed.Health.IsHealthyForWatchdog);
            Assert.Equal(0, observed.Service.RenewalFaultsToInject);
            await observed.Service.StopAsync(CancellationToken.None);
            Assert.True(observed.Health.IsHealthyForWatchdog);
        }

        /// <summary>
        /// Repeated renewal failure inside the safety window blocks watchdog keep-alives. A later replacement clears the block.
        /// </summary>
        [Fact]
        public async Task Renewal_failure_inside_the_window_blocks_health_until_a_replacement_is_published()
        {
            var started = DateTimeOffset.UtcNow;
            var reading = 0;
            var issuer = new ScriptedIssuer((call, domains, password) => call switch
            {
                1 => CreateMaterial(domains, password, started.AddDays(20)),
                2 => throw new InvalidOperationException("renewal failed"),
                _ => CreateMaterial(domains, password, started.AddDays(90)),
            });
            await using var observed = CreateObservedRenewal(started, issuer, notAfter: null, seedDisk: false);
            observed.Health.MarkStarted();
            await observed.Service.StartAsync(CancellationToken.None);
            Assert.True(observed.Health.IsHealthyForWatchdog);
            using (observed.Publisher.Acquire())
            {
                var reader = Task.Run(() =>
                {
                    while (Volatile.Read(ref reading) == 0)
                    {
                        _ = observed.Health.IsHealthyForWatchdog;
                        if (observed.Publisher.IsAvailable)
                        {
                            using var current = observed.Publisher.Acquire();
                        }
                    }
                });

                await ArticleWorkTestDeliveries.WaitUntilAsync(() => observed.Service.RenewalWaitsStarted == 1, TimeSpan.FromSeconds(2));
                observed.Time.Advance(TimeSpan.FromHours(6));
                await ArticleWorkTestDeliveries.WaitUntilAsync(() => observed.Service.RenewalChecksCompleted == 1, TimeSpan.FromSeconds(2));
                Assert.False(observed.Health.IsHealthyForWatchdog);

                await ArticleWorkTestDeliveries.WaitUntilAsync(() => observed.Service.RenewalWaitsStarted == 2, TimeSpan.FromSeconds(2));
                observed.Time.Advance(TimeSpan.FromHours(6));
                await ArticleWorkTestDeliveries.WaitUntilAsync(() => observed.Service.RenewalChecksCompleted == 2, TimeSpan.FromSeconds(2));
                Assert.True(observed.Health.IsHealthyForWatchdog);
                Assert.True(observed.Publisher.IsAvailable);

                Volatile.Write(ref reading, 1);
                await reader.WaitAsync(TimeSpan.FromSeconds(2));
            }

            using (observed.Publisher.Acquire())
            {
            }

            Assert.True(observed.Health.IsHealthyForWatchdog);
        }

        /// <summary>
        /// An expired published certificate is not healthy, including when renewal cannot replace it.
        /// </summary>
        [Fact]
        public async Task Expired_certificate_is_not_healthy()
        {
            var started = DateTimeOffset.UtcNow;
            var expires = started.AddHours(5);
            var issuer = new ScriptedIssuer((call, domains, password) => call == 1
                ? CreateMaterial(domains, password, expires)
                : throw new InvalidOperationException("renewal failed"));
            await using var observed = CreateObservedRenewal(started, issuer, notAfter: null, seedDisk: false);
            observed.Health.MarkStarted();
            await observed.Service.StartAsync(CancellationToken.None);
            Assert.True(observed.Health.IsHealthyForWatchdog);

            await ArticleWorkTestDeliveries.WaitUntilAsync(() => observed.Service.RenewalWaitsStarted == 1, TimeSpan.FromSeconds(2));
            observed.Time.Advance(TimeSpan.FromHours(6));
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => observed.Service.RenewalChecksCompleted == 1, TimeSpan.FromSeconds(2));

            Assert.False(observed.Health.IsHealthyForWatchdog);
            Assert.True(AcmeCertificateWatchdog.CertificateRequiresWatchdogBlock(
                new CertificateStatus(Usable: false, DueForRenewal: true, Material: null, Reason: "expired"),
                renewalReplacedCertificate: false));
            Assert.True(AcmeCertificateWatchdog.CertificateRequiresWatchdogBlock(
                new CertificateStatus(Usable: false, DueForRenewal: true, Material: null, Reason: "absent"),
                renewalReplacedCertificate: true));
        }

        /// <summary>
        /// Stopping the renewal loop while it is waiting is not recorded as a renewal failure.
        /// </summary>
        [Fact]
        public async Task Renewal_shutdown_cancellation_is_not_a_failure()
        {
            var started = DateTimeOffset.UtcNow;
            await using var observed = CreateObservedRenewal(
                started,
                new ThrowingIssuer(),
                notAfter: started.AddDays(90),
                seedDisk: true);
            observed.Health.MarkStarted();
            await observed.Service.StartAsync(CancellationToken.None);
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => observed.Service.RenewalWaitsStarted == 1, TimeSpan.FromSeconds(2));

            await observed.Service.StopAsync(CancellationToken.None);

            Assert.Equal(0, observed.Service.RenewalChecksCompleted);
            Assert.True(observed.Health.IsHealthyForWatchdog);
            Assert.True(observed.Service.Execution!.IsCompleted);
            observed.Time.Advance(TimeSpan.FromHours(6));
            Assert.Equal(0, observed.Service.RenewalChecksCompleted);
            Assert.True(observed.Health.IsHealthyForWatchdog);
        }

        /// <summary>
        /// Builds a TLS-enabled certificate service on a manual clock with watchdog health and an in-memory publisher.
        /// </summary>
        /// <summary>
        /// A password changed after startup is the password used to issue, store, and publish the next renewal.
        /// </summary>
        [Fact]
        public async Task Renewal_uses_the_pfx_password_from_the_attempt_snapshot()
        {
            var started = DateTimeOffset.UtcNow;
            await using var scope = CreatePasswordRenewal(started, fixedPassword: null);
            var original = scope.Options.AcmeCertificatePassword;
            await scope.Service.StartAsync(CancellationToken.None);
            Assert.Equal([original], scope.Publisher.Passwords);

            await ArticleWorkTestDeliveries.WaitUntilAsync(() => scope.Service.RenewalWaitsStarted == 1, TimeSpan.FromSeconds(2));
            scope.Options.AcmeCertificatePassword = "rotated-pfx-password";
            scope.Time.Advance(TimeSpan.FromHours(6));
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => scope.Service.RenewalChecksCompleted == 1, TimeSpan.FromSeconds(2));

            Assert.Equal([original, "rotated-pfx-password"], scope.Publisher.Passwords);
            Assert.NotNull(LoadMaterial(scope, "rotated-pfx-password"));
            Assert.Throws<AcmeStorageException>(() => LoadMaterial(scope, original));
        }

        /// <summary>
        /// A snapshot that cannot validate the issued PFX leaves the previous certificate and its password in place.
        /// </summary>
        [Fact]
        public async Task Renewal_with_a_mismatched_pfx_password_keeps_the_existing_certificate()
        {
            var started = DateTimeOffset.UtcNow;
            await using var scope = CreatePasswordRenewal(started, fixedPassword: null);
            var original = scope.Options.AcmeCertificatePassword;
            scope.Issuer.FreezePassword(original);
            await scope.Service.StartAsync(CancellationToken.None);

            await ArticleWorkTestDeliveries.WaitUntilAsync(() => scope.Service.RenewalWaitsStarted == 1, TimeSpan.FromSeconds(2));
            scope.Options.AcmeCertificatePassword = "rotated-pfx-password";
            scope.Time.Advance(TimeSpan.FromHours(6));
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => scope.Service.RenewalChecksCompleted == 1, TimeSpan.FromSeconds(2));

            Assert.Equal([original], scope.Publisher.Passwords);
            Assert.NotNull(LoadMaterial(scope, original));
            Assert.Throws<AcmeStorageException>(() => LoadMaterial(scope, "rotated-pfx-password"));
        }

        /// <summary>
        /// A blank refreshed password does not replace the password that last saved the live PFX.
        /// </summary>
        [Fact]
        public async Task Renewal_with_a_blank_pfx_password_keeps_the_previous_password()
        {
            var started = DateTimeOffset.UtcNow;
            await using var scope = CreatePasswordRenewal(started, fixedPassword: null);
            var original = scope.Options.AcmeCertificatePassword;
            scope.Issuer.FreezePassword(original);
            await scope.Service.StartAsync(CancellationToken.None);

            await ArticleWorkTestDeliveries.WaitUntilAsync(() => scope.Service.RenewalWaitsStarted == 1, TimeSpan.FromSeconds(2));
            scope.Options.AcmeCertificatePassword = " ";
            scope.Time.Advance(TimeSpan.FromHours(6));
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => scope.Service.RenewalChecksCompleted == 1, TimeSpan.FromSeconds(2));

            Assert.Equal([original, original], scope.Publisher.Passwords);
            Assert.NotNull(LoadMaterial(scope, original));
        }

        /// <summary>
        /// A failure after the new generation is current still publishes that generation with the password that saved it.
        /// </summary>
        [Fact]
        public async Task Renewal_publishes_the_saved_certificate_when_the_journal_fails_after_persist()
        {
            var started = DateTimeOffset.UtcNow;
            await using var scope = CreatePasswordRenewal(started, fixedPassword: null);
            var original = scope.Options.AcmeCertificatePassword;
            var persists = 0;
            scope.Manager.AfterPersistForTests = () =>
            {
                if (Interlocked.Increment(ref persists) == 2)
                {
                    throw new IOException("journal failed after persist");
                }
            };

            await scope.Service.StartAsync(CancellationToken.None);
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => scope.Service.RenewalWaitsStarted == 1, TimeSpan.FromSeconds(2));
            scope.Options.AcmeCertificatePassword = "rotated-pfx-password";
            scope.Time.Advance(TimeSpan.FromHours(6));
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => scope.Service.RenewalChecksCompleted == 1, TimeSpan.FromSeconds(2));

            Assert.Equal([original, "rotated-pfx-password"], scope.Publisher.Passwords);
            var saved = LoadMaterial(scope, "rotated-pfx-password");
            Assert.NotNull(saved);
            using var loaded = PfxCrypto.LoadCertificate(saved.PfxBytes, "rotated-pfx-password");
            using var published = scope.Manager.CreateTlsCertificate();
            Assert.Equal(loaded.Thumbprint, published.Thumbprint);
        }

        /// <summary>Builds a renewal service whose issuer and publisher expose the PFX password used for each attempt.</summary>
        private static PasswordRenewal CreatePasswordRenewal(DateTimeOffset clock, string? fixedPassword)
        {
            var time = new ManualTimeProvider(clock);
            var directory = new TempAcmeDir();
            var options = BackFillerTestOptions.CreateValidAcme();
            options.AcmeStateDir = directory.Path;
            options.Fqdn = "backfiller01.usenet.ninja";
            options.IncludeNewsHostnameInCertificate = false;
            var domains = CertificateIdentities.ForFqdn(options.Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(
                directory.Path,
                options.Fqdn,
                options.AcmeCertificatePassword,
                domains,
                TimeSpan.FromDays(options.AcmeRenewalThresholdDays));
            var issuer = new PasswordIssuer(options, clock, fixedPassword);
            var manager = new CertificateManager(
                options.Fqdn,
                directory.Path,
                options.AcmeDirectoryUrl,
                store,
                issuer,
                options.AcmeCertificatePassword,
                TimeSpan.FromDays(options.AcmeRenewalThresholdDays),
                NullLogger<CertificateManager>.Instance,
                includeNewsHostname: false);
            var publisher = new RecordingPublisher();
            var service = new AcmeCertificateService(
                Options.Create<AcmeCloudflareOptions>(options),
                new InjectedManagerFactory(manager, Options.Create<AcmeCloudflareOptions>(options)),
                publisher,
                new AcmeCertificateReadiness(),
                NullLogger<AcmeCertificateService>.Instance,
                time,
                observation: null);
            return new PasswordRenewal(service, time, options, issuer, publisher, directory, domains, manager);
        }

        /// <summary>Loads the live PFX with <paramref name="password"/>.</summary>
        private static CertificateMaterial? LoadMaterial(PasswordRenewal scope, string password)
        {
            var store = new CertificateStore(
                scope.Directory.Path,
                scope.Options.Fqdn,
                password,
                scope.Domains,
                TimeSpan.FromDays(scope.Options.AcmeRenewalThresholdDays));
            return store.Load();
        }

        private static ObservedRenewal CreateObservedRenewal(
            DateTimeOffset clock,
            ICertificateIssuer issuer,
            DateTimeOffset? notAfter,
            bool seedDisk)
        {
            var time = new ManualTimeProvider(clock);
            var dir = new TempAcmeDir();
            var options = BackFillerTestOptions.CreateValidAcme();
            options.AcmeStateDir = dir.Path;
            options.Fqdn = "backfiller01.usenet.ninja";
            options.IncludeNewsHostnameInCertificate = false;
            var domains = CertificateIdentities.ForFqdn(options.Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(
                dir.Path,
                options.Fqdn,
                options.AcmeCertificatePassword,
                domains,
                TimeSpan.FromDays(options.AcmeRenewalThresholdDays));
            if (seedDisk)
            {
                store.Save(CreateMaterial(domains, options.AcmeCertificatePassword, notAfter ?? clock.AddDays(90)));
            }

            var manager = new CertificateManager(
                options.Fqdn,
                dir.Path,
                options.AcmeDirectoryUrl,
                store,
                issuer,
                options.AcmeCertificatePassword,
                TimeSpan.FromDays(options.AcmeRenewalThresholdDays),
                NullLogger<CertificateManager>.Instance,
                includeNewsHostname: false);
            var readiness = new AcmeCertificateReadiness();
            var publisher = new TlsCertificateContextProvider(NullLogger<TlsCertificateContextProvider>.Instance);
            var health = new BackFillerApplicationHealth();
            var service = new AcmeCertificateService(
                Options.Create<AcmeCloudflareOptions>(options),
                new InjectedManagerFactory(manager, Options.Create<AcmeCloudflareOptions>(options)),
                publisher,
                readiness,
                NullLogger<AcmeCertificateService>.Instance,
                time,
                new AcmeCertificateWatchdog(health, NullLogger<AcmeCertificateWatchdog>.Instance));
            return new ObservedRenewal(service, health, time, publisher, dir);
        }

        /// <summary>
        /// Builds an adapter harness with TLS bind port zero, a no-op publisher, and a factory that must not run issuance.
        /// </summary>

        private static AdapterHarness CreateTlsDisabledHarness()
        {
            var options = BackFillerTestOptions.CreateValidAcme();
            options.BindPortTls = 0;
            var readiness = new AcmeCertificateReadiness();
            var journal = new BackFillerStartupJournal();
            var inner = new AcmeCertificateService(
                Options.Create<AcmeCloudflareOptions>(options),
                new UnusedAcmeFactory(options),
                new NoOpPublisher(),
                readiness,
                NullLogger<AcmeCertificateService>.Instance);
            return new AdapterHarness(
                new AcmeCertificateApplicationService(inner, readiness, journal),
                inner,
                readiness,
                journal);
        }

        /// <summary>
        /// Builds an adapter harness with a long-lived self-signed certificate on disk, an injected manager, and a publisher that marks readiness.
        /// </summary>

        private static AdapterHarness CreateReusableCertificateHarness()
        {
            var dir = new TempAcmeDir();
            var options = BackFillerTestOptions.CreateValidAcme();
            options.AcmeStateDir = dir.Path;
            options.Fqdn = "backfiller01.usenet.ninja";
            options.IncludeNewsHostnameInCertificate = false;
            var domains = CertificateIdentities.ForFqdn(options.Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(
                dir.Path,
                options.Fqdn,
                options.AcmeCertificatePassword,
                domains,
                TimeSpan.FromDays(options.AcmeRenewalThresholdDays));
            store.Save(CreateMaterial(domains, options.AcmeCertificatePassword, DateTimeOffset.UtcNow.AddDays(90)));

            var manager = new CertificateManager(
                options.Fqdn,
                dir.Path,
                options.AcmeDirectoryUrl,
                store,
                new ThrowingIssuer(),
                options.AcmeCertificatePassword,
                TimeSpan.FromDays(options.AcmeRenewalThresholdDays),
                NullLogger<CertificateManager>.Instance,
                includeNewsHostname: false);
            var factory = new InjectedManagerFactory(manager, Options.Create<AcmeCloudflareOptions>(options));
            var readiness = new AcmeCertificateReadiness();
            var journal = new BackFillerStartupJournal();
            var inner = new AcmeCertificateService(
                Options.Create<AcmeCloudflareOptions>(options),
                factory,
                new MarkingPublisher(readiness),
                readiness,
                NullLogger<AcmeCertificateService>.Instance);
            var adapter = new AcmeCertificateApplicationService(inner, readiness, journal);
            return new AdapterHarness(adapter, inner, readiness, journal, dir);
        }

        /// <summary>
        /// Creates exportable PFX material with ordered lowercase SAN names and the given validity window.
        /// </summary>
        /// <param name="dnsNames">DNS identifiers; the first name becomes the certificate subject CN.</param>
        /// <param name="password">PFX encryption password.</param>
        /// <param name="notAfter">Certificate not-after instant (UTC).</param>
        private static CertificateMaterial CreateMaterial(
            IReadOnlyList<string> dnsNames,
            string password,
            DateTimeOffset notAfter)
        {
            var before = DateTimeOffset.UtcNow.AddDays(-1);
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=" + dnsNames[0],
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var sanBuilder = new SubjectAlternativeNameBuilder();
            foreach (var name in dnsNames)
            {
                sanBuilder.AddDnsName(name);
            }

            request.CertificateExtensions.Add(sanBuilder.Build());
            using var cert = request.CreateSelfSigned(before.UtcDateTime, notAfter.UtcDateTime);
            var pfx = PfxCrypto.ExportPfx(cert, intermediateCertificates: [], password);
            return new CertificateMaterial(
                pfx,
                dnsNames.Select(static name => name.ToLowerInvariant()).OrderBy(static name => name).ToArray(),
                before,
                notAfter);
        }

        /// <summary>Builds AcmeCertificateApplicationService with injectable issuer/publisher fakes for adapter tests.</summary>
        private sealed class AdapterHarness : IDisposable
        {
            /// <summary>Optional temp ACME directory disposed with the harness.</summary>
            private readonly TempAcmeDir? _dir;

            /// <summary>Retains adapter test services and optional filesystem state.</summary>
            /// <param name="adapter">Application service under test.</param>
            /// <param name="inner">Inner certificate service.</param>
            /// <param name="readiness">Readiness gate observed by tests.</param>
            /// <param name="journal">Startup journal for stage assertions.</param>
            /// <param name="dir">Optional temp directory cleaned up on dispose.</param>
            internal AdapterHarness(
                AcmeCertificateApplicationService adapter,
                AcmeCertificateService inner,
                IAcmeCertificateReadiness readiness,
                IBackFillerStartupJournal journal,
                TempAcmeDir? dir = null)
            {
                Adapter = adapter;
                Inner = inner;
                Readiness = readiness;
                Journal = journal;
                _dir = dir;
            }

            /// <summary>
            /// Application-service adapter under test.
            /// </summary>

            public AcmeCertificateApplicationService Adapter { get; }

            /// <summary>
            /// Inner certificate service wired into the adapter.
            /// </summary>

            public AcmeCertificateService Inner { get; }

            /// <summary>
            /// Shared readiness gate observed by lifecycle assertions.
            /// </summary>

            public IAcmeCertificateReadiness Readiness { get; }

            /// <summary>
            /// Startup journal recording stages emitted during start.
            /// </summary>

            public IBackFillerStartupJournal Journal { get; }

            /// <summary>
            /// Disposes the optional temp ACME directory when present.
            /// </summary>

            public void Dispose() => _dir?.Dispose();
        }

        /// <summary>Creates and deletes a temporary ACME state directory for filesystem-sensitive tests.</summary>
        private sealed class TempAcmeDir : IDisposable
        {
            /// <summary>
            /// Unique directory path under the process temp folder.
            /// </summary>

            public string Path { get; } = System.IO.Path.Join(
                System.IO.Path.GetTempPath(),
                "vectornntp-bf-acme-" + Guid.NewGuid().ToString("N"));

            /// <summary>Creates the directory at <see cref="Path"/>.</summary>
            internal TempAcmeDir() => Directory.CreateDirectory(Path);

            /// <summary>
            /// Recursively deletes the temp directory; IO failures are ignored.
            /// </summary>

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(Path))
                    {
                        Directory.Delete(Path, recursive: true);
                    }
                }
                catch (IOException)
                {
                    // Best-effort test cleanup.
                }
            }
        }

        /// <summary>AcmeComponentFactory placeholder that must not be invoked in the exercised code paths.</summary>
        private sealed class UnusedAcmeFactory : AcmeComponentFactory
        {
            /// <summary>Base factory that must not be invoked on exercised paths.</summary>
            /// <param name="options">Bound ACME options for the unused base.</param>
            internal UnusedAcmeFactory(AcmeCloudflareOptions options)
                : base(
                    Options.Create(options),
                    new UnusedCloudflareClient(),
                    new UnusedHttpClientFactory(),
                    NullLoggerFactory.Instance)
            {
            }
        }

        /// <summary>AcmeComponentFactory that returns test-controlled issuer and publisher instances.</summary>
        private sealed class InjectedManagerFactory : AcmeComponentFactory
        {
            /// <summary>Manager returned from <see cref="GetOrCreateManager"/>.</summary>
            private readonly CertificateManager _manager;

            /// <summary>Factory that always supplies the injected certificate manager.</summary>
            /// <param name="manager">Prebuilt manager for issuer tests.</param>
            /// <param name="options">ACME options passed to the base factory.</param>
            internal InjectedManagerFactory(CertificateManager manager, IOptions<AcmeCloudflareOptions> options)
                : base(
                    options,
                    new UnusedCloudflareClient(),
                    new UnusedHttpClientFactory(),
                    NullLoggerFactory.Instance)
            {
                _manager = manager;
            }

            /// <summary>Returns the injected manager without creating production components.</summary>
            internal override CertificateManager? GetOrCreateManager() => _manager;
        }

        /// <summary>Cloudflare client double that throws if any API method is called unexpectedly.</summary>
        private sealed class UnusedCloudflareClient : ICloudflareDnsClient
        {
            /// <summary>
            /// Always fails because Cloudflare must not be contacted in adapter harness tests.
            /// </summary>
            /// <param name="zoneId">Cloudflare zone identifier (unused).</param>
            /// <param name="fqdn">Record name filter (unused).</param>
            /// <param name="type">DNS record type (unused).</param>
            /// <param name="cancellationToken">Cancellation token (unused).</param>
            public Task<IReadOnlyList<CloudflareDnsRecord>> ListRecordsAsync(
                string zoneId,
                string fqdn,
                string type,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Cloudflare should not be used in this test.");

            /// <summary>
            /// Always fails because Cloudflare must not be contacted in adapter harness tests.
            /// </summary>
            /// <param name="zoneId">Cloudflare zone identifier (unused).</param>
            /// <param name="fqdn">Record name filter (unused).</param>
            /// <param name="cancellationToken">Cancellation token (unused).</param>
            public Task<IReadOnlyList<CloudflareDnsRecord>> ListAllRecordsForNameAsync(
                string zoneId,
                string fqdn,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Cloudflare should not be used in this test.");

            /// <summary>
            /// Always fails because Cloudflare must not be contacted in adapter harness tests.
            /// </summary>
            /// <param name="zoneId">Cloudflare zone identifier (unused).</param>
            /// <param name="request">Record payload (unused).</param>
            /// <param name="cancellationToken">Cancellation token (unused).</param>
            public Task<CloudflareDnsRecord> CreateRecordAsync(
                string zoneId,
                CloudflareDnsRecordWriteRequest request,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Cloudflare should not be used in this test.");

            /// <summary>
            /// Always fails because Cloudflare must not be contacted in adapter harness tests.
            /// </summary>
            /// <param name="zoneId">Cloudflare zone identifier (unused).</param>
            /// <param name="recordId">Existing record identifier (unused).</param>
            /// <param name="request">Updated record payload (unused).</param>
            /// <param name="cancellationToken">Cancellation token (unused).</param>
            public Task<CloudflareDnsRecord> UpdateRecordAsync(
                string zoneId,
                string recordId,
                CloudflareDnsRecordWriteRequest request,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Cloudflare should not be used in this test.");

            /// <summary>
            /// Always fails because Cloudflare must not be contacted in adapter harness tests.
            /// </summary>
            /// <param name="zoneId">Cloudflare zone identifier (unused).</param>
            /// <param name="recordId">Record to delete (unused).</param>
            /// <param name="cancellationToken">Cancellation token (unused).</param>
            public Task DeleteRecordAsync(string zoneId, string recordId, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Cloudflare should not be used in this test.");
        }

        /// <summary>HttpClientFactory that throws if CreateClient is invoked.</summary>
        private sealed class UnusedHttpClientFactory : IHttpClientFactory
        {
            /// <summary>
            /// Satisfies the base factory constructor; returns a disposable client that must not perform ACME HTTP in these tests.
            /// </summary>
            /// <param name="name">Named client key (ignored).</param>
            public HttpClient CreateClient(string name) => new();
        }

        /// <summary>Manual-clock renewal service, watchdog health, and the publisher used to swap certificate leases.</summary>
        private sealed class ObservedRenewal : IAsyncDisposable
        {
            private readonly TempAcmeDir _directory;

            /// <summary>Stores the started collaborators. The caller starts the service.</summary>
            internal ObservedRenewal(
                AcmeCertificateService service,
                BackFillerApplicationHealth health,
                ManualTimeProvider time,
                TlsCertificateContextProvider publisher,
                TempAcmeDir directory)
            {
                Service = service;
                Health = health;
                Time = time;
                Publisher = publisher;
                _directory = directory;
            }

            /// <summary>Certificate service under test.</summary>
            internal AcmeCertificateService Service { get; }

            /// <summary>Watchdog health updated by the service.</summary>
            internal BackFillerApplicationHealth Health { get; }

            /// <summary>Clock advanced by the test.</summary>
            internal ManualTimeProvider Time { get; }

            /// <summary>Publisher that swaps the listener certificate context.</summary>
            internal TlsCertificateContextProvider Publisher { get; }

            /// <summary>Stops the renewal loop, then releases the publisher and temp directory.</summary>
            public async ValueTask DisposeAsync()
            {
                await Service.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await Service.DisposeAsync().ConfigureAwait(false);
                await Publisher.DisposeAsync().ConfigureAwait(false);
                _directory.Dispose();
            }
        }

        /// <summary>Issuer whose result depends on the 1-based call count.</summary>
        private sealed class ScriptedIssuer : ICertificateIssuer
        {
            private readonly Func<int, IReadOnlyList<string>, string, CertificateMaterial> _next;
            private int _calls;

            /// <summary>Stores the script. Call 1 is the first <see cref="IssueAsync"/>.</summary>
            internal ScriptedIssuer(Func<int, IReadOnlyList<string>, string, CertificateMaterial> next)
            {
                _next = next;
            }

            /// <inheritdoc />
            public Task<CertificateMaterial> IssueAsync(IReadOnlyList<string> domains, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var call = Interlocked.Increment(ref _calls);
                var password = BackFillerTestOptions.CreateValidAcme().AcmeCertificatePassword;
                return Task.FromResult(_next(call, domains, password));
            }
        }

        /// <summary>ICertificateIssuer that always throws to simulate issuance failure.</summary>
        private sealed class ThrowingIssuer : ICertificateIssuer
        {
            /// <summary>
            /// Always fails to ensure issuance does not run when a reusable certificate is already present.
            /// </summary>
            /// <param name="domains">Requested DNS names (unused).</param>
            /// <param name="cancellationToken">Cancellation token (unused).</param>
            public Task<CertificateMaterial> IssueAsync(IReadOnlyList<string> domains, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Issuance should not run when a usable certificate exists.");
        }

        /// <summary>Publisher double that completes without writing certificate material.</summary>
        private sealed class NoOpPublisher : IAcmeCertificatePublisher
        {
            /// <summary>
            /// Ignores PFX bytes; does not write certificate files or change readiness.
            /// </summary>
            /// <param name="pfxBytes">Exported certificate bytes (ignored).</param>
            /// <param name="password">PFX password (ignored).</param>
            public void PublishFromPfx(ReadOnlySpan<byte> pfxBytes, string password)
            {
            }
        }

        /// <summary>Publisher double that records whether Publish was invoked.</summary>
        private sealed class MarkingPublisher : IAcmeCertificatePublisher
        {
            /// <summary>Readiness marked when publish is invoked.</summary>
            private readonly IAcmeCertificateReadiness _readiness;

            /// <summary>Records readiness on publish without writing certificate files.</summary>
            /// <param name="readiness">Gate updated in <see cref="PublishFromPfx"/>.</param>
            internal MarkingPublisher(IAcmeCertificateReadiness readiness)
            {
                _readiness = readiness;
            }

            /// <summary>
            /// Marks the injected readiness gate without persisting PFX material to disk.
            /// </summary>
            /// <param name="pfxBytes">Exported certificate bytes (ignored).</param>
            /// <param name="password">PFX password (ignored).</param>
            public void PublishFromPfx(ReadOnlySpan<byte> pfxBytes, string password)
            {
                _readiness.MarkReady();
            }
        }

        /// <summary>Renewal collaborators whose password the test can change between attempts.</summary>
        private sealed class PasswordRenewal : IAsyncDisposable
        {
            /// <summary>Retains the service, clock, options, issuer, publisher, directory, domains, and manager.</summary>
            internal PasswordRenewal(
                AcmeCertificateService service,
                ManualTimeProvider time,
                AcmeCloudflareOptions options,
                PasswordIssuer issuer,
                RecordingPublisher publisher,
                TempAcmeDir directory,
                IReadOnlyList<string> domains,
                CertificateManager manager)
            {
                Service = service;
                Time = time;
                Options = options;
                Issuer = issuer;
                Publisher = publisher;
                Directory = directory;
                Domains = domains;
                Manager = manager;
            }

            /// <summary>Service under test.</summary>
            internal AcmeCertificateService Service { get; }

            /// <summary>Clock advanced by the test.</summary>
            internal ManualTimeProvider Time { get; }

            /// <summary>Options object mutated by the test. The service reads this instance.</summary>
            internal AcmeCloudflareOptions Options { get; }

            /// <summary>Issuer that can freeze the PFX password it embeds.</summary>
            internal PasswordIssuer Issuer { get; }

            /// <summary>Publisher that records each password it receives.</summary>
            internal RecordingPublisher Publisher { get; }

            /// <summary>Certificate state directory.</summary>
            internal TempAcmeDir Directory { get; }

            /// <summary>DNS names required by the store.</summary>
            internal IReadOnlyList<string> Domains { get; }

            /// <summary>Manager whose persist hook the test can arm.</summary>
            internal CertificateManager Manager { get; }

            /// <summary>Disposes the service and deletes the state directory.</summary>
            public async ValueTask DisposeAsync()
            {
                await Service.DisposeAsync().ConfigureAwait(false);
                Directory.Dispose();
            }
        }

        /// <summary>Issuer that embeds either a frozen password or the live options password.</summary>
        private sealed class PasswordIssuer : ICertificateIssuer
        {
            /// <summary>Options read when no password is frozen.</summary>
            private readonly AcmeCloudflareOptions _options;

            /// <summary>Clock origin used to choose the first and later certificate lifetimes.</summary>
            private readonly DateTimeOffset _started;

            /// <summary>Password embedded in later PFX material. <see langword="null"/> reads <see cref="_options"/>.</summary>
            private string? _fixedPassword;

            /// <summary>1-based issue count.</summary>
            private int _calls;

            /// <summary>Stores the options, clock, and optional frozen password.</summary>
            internal PasswordIssuer(AcmeCloudflareOptions options, DateTimeOffset started, string? fixedPassword)
            {
                _options = options;
                _started = started;
                _fixedPassword = fixedPassword;
            }

            /// <summary>Later issues embed <paramref name="password"/> even if options change.</summary>
            internal void FreezePassword(string password) => _fixedPassword = password;

            /// <inheritdoc />
            public Task<CertificateMaterial> IssueAsync(IReadOnlyList<string> domains, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var call = Interlocked.Increment(ref _calls);
                var password = _fixedPassword ?? _options.AcmeCertificatePassword;
                var notAfter = call == 1 ? _started.AddDays(20) : _started.AddDays(90);
                return Task.FromResult(CreateMaterial(domains, password, notAfter));
            }
        }

        /// <summary>Publisher that records the PKCS#12 password from each publish.</summary>
        private sealed class RecordingPublisher : IAcmeCertificatePublisher
        {
            /// <summary>Passwords passed to <see cref="PublishFromPfx"/>, in order.</summary>
            internal List<string> Passwords { get; } = [];

            /// <inheritdoc />
            public void PublishFromPfx(ReadOnlySpan<byte> pfxBytes, string password) => Passwords.Add(password);
        }
    }
}

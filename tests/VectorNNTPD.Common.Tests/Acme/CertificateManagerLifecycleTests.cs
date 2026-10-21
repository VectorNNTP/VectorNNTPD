using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.Tests.TestDoubles;

namespace VectorNNTPD.Common.Tests.Acme
{
    /// <summary>
    /// Startup and renewal behaviour for <see cref="CertificateManager"/> when a usable certificate is due for renewal.
    /// </summary>
    public sealed class CertificateManagerLifecycleTests
    {
        private const string Fqdn = "backfiller01.usenet.ninja";
        private const string DirectoryUrl = AcmeCloudflareOptions.DefaultAcmeDirectoryUrl;

        /// <summary>
        /// A usable certificate outside the renewal window is reused without contacting the issuer.
        /// </summary>
        [Fact]
        public async Task EnsureCertificate_reuses_usable_certificate_outside_renewal_window()
        {
            using var dir = new TempStateDir();
            var names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(
                dir.Path,
                Fqdn,
                TestCertificateFactory.Password,
                names,
                TimeSpan.FromDays(30));
            var seeded = TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(90));
            store.Save(seeded);
            var issuer = new CountingIssuer();
            var manager = CreateManager(dir.Path, store, issuer);

            var material = await manager.EnsureCertificateAsync(CancellationToken.None);

            Assert.Equal(0, issuer.Calls);
            AssertSameCertificateInstant(seeded.NotAfter, material.NotAfter);
            Assert.NotNull(manager.CurrentMaterial);
        }

        /// <summary>
        /// Startup keeps a usable certificate when replacement issuance fails inside the renewal window.
        /// </summary>
        [Fact]
        public async Task EnsureCertificate_preserves_usable_certificate_when_due_renewal_fails()
        {
            using var dir = new TempStateDir();
            var names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(
                dir.Path,
                Fqdn,
                TestCertificateFactory.Password,
                names,
                TimeSpan.FromDays(30));
            var seeded = TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(20));
            store.Save(seeded);
            var issuer = new CountingIssuer
            {
                ThrowOnIssue = new AcmeOrderException("finalize_failed", "ca rejected"),
            };
            var manager = CreateManager(dir.Path, store, issuer);

            var material = await manager.EnsureCertificateAsync(CancellationToken.None);

            Assert.Equal(1, issuer.Calls);
            AssertSameCertificateInstant(seeded.NotAfter, material.NotAfter);
            AssertSameCertificateInstant(seeded.NotAfter, manager.CurrentMaterial!.NotAfter);
            using var tls = manager.CreateTlsCertificate();
            Assert.True(tls.NotAfter.ToUniversalTime() > DateTime.UtcNow);
        }

        /// <summary>
        /// Startup replaces a due certificate when issuance succeeds.
        /// </summary>
        [Fact]
        public async Task EnsureCertificate_replaces_usable_certificate_when_due_renewal_succeeds()
        {
            using var dir = new TempStateDir();
            var names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(
                dir.Path,
                Fqdn,
                TestCertificateFactory.Password,
                names,
                TimeSpan.FromDays(30));
            var seeded = TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(20));
            store.Save(seeded);
            var replacementNotAfter = DateTimeOffset.UtcNow.AddDays(90);
            var issuer = new CountingIssuer { FixedNotAfter = replacementNotAfter };
            var manager = CreateManager(dir.Path, store, issuer);

            var material = await manager.EnsureCertificateAsync(CancellationToken.None);

            Assert.Equal(1, issuer.Calls);
            AssertSameCertificateInstant(replacementNotAfter, material.NotAfter);
            Assert.NotEqual(TruncateToSeconds(seeded.NotAfter), TruncateToSeconds(material.NotAfter));
        }

        /// <summary>
        /// Missing material still fails closed when issuance fails.
        /// </summary>
        [Fact]
        public async Task EnsureCertificate_fails_closed_when_absent_and_issuance_fails()
        {
            using var dir = new TempStateDir();
            var names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(
                dir.Path,
                Fqdn,
                TestCertificateFactory.Password,
                names,
                TimeSpan.FromDays(30));
            var issuer = new CountingIssuer
            {
                ThrowOnIssue = new AcmeOrderException("finalize_failed", "ca rejected"),
            };
            var manager = CreateManager(dir.Path, store, issuer);

            var ex = await Assert.ThrowsAsync<AcmeOrderException>(
                () => manager.EnsureCertificateAsync(CancellationToken.None));
            Assert.Equal("finalize_failed", ex.Category);
            Assert.Equal(1, issuer.Calls);
            Assert.Null(manager.CurrentMaterial);
        }

        /// <summary>
        /// Caller or host cancellation during due renewal propagates and is not treated as a renewal outage.
        /// </summary>
        [Fact]
        public async Task EnsureCertificate_propagates_caller_cancellation_during_due_renewal()
        {
            using var dir = new TempStateDir();
            var names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(
                dir.Path,
                Fqdn,
                TestCertificateFactory.Password,
                names,
                TimeSpan.FromDays(30));
            store.Save(TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(20)));
            using var cts = new CancellationTokenSource();
            var issuer = new CountingIssuer
            {
                CancelOnIssue = cts,
                ThrowOnIssue = new OperationCanceledException(cts.Token),
            };
            var manager = CreateManager(dir.Path, store, issuer);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => manager.EnsureCertificateAsync(cts.Token));
            Assert.Equal(1, issuer.Calls);
        }

        /// <summary>
        /// A transport cancellation that is not tied to the caller token preserves a usable certificate.
        /// </summary>
        [Fact]
        public async Task EnsureCertificate_preserves_usable_certificate_on_transport_cancellation()
        {
            using var dir = new TempStateDir();
            var names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(
                dir.Path,
                Fqdn,
                TestCertificateFactory.Password,
                names,
                TimeSpan.FromDays(30));
            var seeded = TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(20));
            store.Save(seeded);
            var issuer = new CountingIssuer
            {
                ThrowOnIssue = new OperationCanceledException("http timeout"),
            };
            var manager = CreateManager(dir.Path, store, issuer);

            var material = await manager.EnsureCertificateAsync(CancellationToken.None);

            Assert.Equal(1, issuer.Calls);
            AssertSameCertificateInstant(seeded.NotAfter, material.NotAfter);
        }

        /// <summary>
        /// Periodic renewal failure preserves the usable certificate and returns false.
        /// </summary>
        [Fact]
        public async Task RenewIfDue_preserves_usable_certificate_when_issuance_fails()
        {
            using var dir = new TempStateDir();
            var names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(
                dir.Path,
                Fqdn,
                TestCertificateFactory.Password,
                names,
                TimeSpan.FromDays(30));
            var seeded = TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(20));
            store.Save(seeded);
            var issuer = new CountingIssuer
            {
                ThrowOnIssue = new AcmeOrderException("finalize_failed", "ca rejected"),
            };
            var manager = CreateManager(dir.Path, store, issuer);

            Assert.False(await manager.RenewIfDueAsync(CancellationToken.None));
            Assert.Equal(1, issuer.Calls);
            AssertSameCertificateInstant(seeded.NotAfter, manager.CurrentMaterial!.NotAfter);
        }

        /// <summary>
        /// A recoverable HTTP transport failure preserves an existing usable certificate.
        /// </summary>
        [Fact]
        public async Task EnsureCertificate_preserves_usable_certificate_on_http_request_failure()
        {
            using var dir = new TempStateDir();
            var names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(
                dir.Path,
                Fqdn,
                TestCertificateFactory.Password,
                names,
                TimeSpan.FromDays(30));
            var seeded = TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(20));
            store.Save(seeded);
            var issuer = new CountingIssuer
            {
                ThrowOnIssue = new System.Net.Http.HttpRequestException("acme unreachable"),
            };
            var manager = CreateManager(dir.Path, store, issuer);

            var material = await manager.EnsureCertificateAsync(CancellationToken.None);

            Assert.Equal(1, issuer.Calls);
            AssertSameCertificateInstant(seeded.NotAfter, material.NotAfter);
        }

        /// <summary>
        /// An unexpected programming error during due renewal is not treated as a renewal outage.
        /// </summary>
        [Fact]
        public async Task EnsureCertificate_propagates_unexpected_programming_exception()
        {
            using var dir = new TempStateDir();
            var names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(
                dir.Path,
                Fqdn,
                TestCertificateFactory.Password,
                names,
                TimeSpan.FromDays(30));
            store.Save(TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(20)));
            var issuer = new CountingIssuer
            {
                ThrowOnIssue = new InvalidOperationException("bug in issuer"),
            };
            var manager = CreateManager(dir.Path, store, issuer);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.EnsureCertificateAsync(CancellationToken.None));
            Assert.Equal("bug in issuer", ex.Message);
            Assert.Equal(1, issuer.Calls);
        }

        /// <summary>
        /// A certificate-store persistence failure is not classified as an ordinary CA renewal outage.
        /// </summary>
        [Fact]
        public async Task EnsureCertificate_propagates_storage_exception_during_due_renewal()
        {
            using var dir = new TempStateDir();
            var names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(
                dir.Path,
                Fqdn,
                TestCertificateFactory.Password,
                names,
                TimeSpan.FromDays(30));
            store.Save(TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(20)));
            var issuer = new CountingIssuer
            {
                ThrowOnIssue = new AcmeStorageException("certificate_persist_failed", "disk full"),
            };
            var manager = CreateManager(dir.Path, store, issuer);

            var ex = await Assert.ThrowsAsync<AcmeStorageException>(
                () => manager.EnsureCertificateAsync(CancellationToken.None));
            Assert.Equal("certificate_persist_failed", ex.Category);
            Assert.Equal(1, issuer.Calls);
        }

        /// <summary>
        /// Periodic renewal propagates unexpected programming errors instead of preserving.
        /// </summary>
        [Fact]
        public async Task RenewIfDue_propagates_unexpected_programming_exception()
        {
            using var dir = new TempStateDir();
            var names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(
                dir.Path,
                Fqdn,
                TestCertificateFactory.Password,
                names,
                TimeSpan.FromDays(30));
            store.Save(TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(20)));
            var issuer = new CountingIssuer
            {
                ThrowOnIssue = new NullReferenceException("unexpected null"),
            };
            var manager = CreateManager(dir.Path, store, issuer);

            await Assert.ThrowsAsync<NullReferenceException>(
                () => manager.RenewIfDueAsync(CancellationToken.None));
            Assert.Equal(1, issuer.Calls);
        }

        /// <summary>
        /// Periodic renewal propagates storage failures instead of treating them as CA outages.
        /// </summary>
        [Fact]
        public async Task RenewIfDue_propagates_storage_exception()
        {
            using var dir = new TempStateDir();
            var names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(
                dir.Path,
                Fqdn,
                TestCertificateFactory.Password,
                names,
                TimeSpan.FromDays(30));
            store.Save(TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(20)));
            var issuer = new CountingIssuer
            {
                ThrowOnIssue = new AcmeStorageException("certificate_persist_failed", "disk full"),
            };
            var manager = CreateManager(dir.Path, store, issuer);

            var ex = await Assert.ThrowsAsync<AcmeStorageException>(
                () => manager.RenewIfDueAsync(CancellationToken.None));
            Assert.Equal("certificate_persist_failed", ex.Category);
        }

        private static void AssertSameCertificateInstant(DateTimeOffset expected, DateTimeOffset actual) =>
            Assert.Equal(TruncateToSeconds(expected), TruncateToSeconds(actual));

        private static DateTimeOffset TruncateToSeconds(DateTimeOffset value) =>
            new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.Offset);

        private static CertificateManager CreateManager(
            string stateDir,
            CertificateStore store,
            ICertificateIssuer issuer) =>
            new(
                Fqdn,
                stateDir,
                DirectoryUrl,
                store,
                issuer,
                TestCertificateFactory.Password,
                TimeSpan.FromDays(30),
                NullLogger<CertificateManager>.Instance,
                includeNewsHostname: false);

        /// <summary>Issuer that counts calls and optionally throws or returns a fixed not-after.</summary>
        private sealed class CountingIssuer : ICertificateIssuer
        {
            public int Calls { get; private set; }

            public Exception? ThrowOnIssue { get; set; }

            public DateTimeOffset? FixedNotAfter { get; set; }

            /// <summary>When set, cancelled after the call is counted so issuance sees a cancelled caller token.</summary>
            public CancellationTokenSource? CancelOnIssue { get; set; }

            public Task<CertificateMaterial> IssueAsync(
                IReadOnlyList<string> domains,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Calls++;
                CancelOnIssue?.Cancel();
                if (ThrowOnIssue is not null)
                {
                    throw ThrowOnIssue;
                }

                return Task.FromResult(
                    TestCertificateFactory.CreateMaterial(
                        domains,
                        FixedNotAfter ?? DateTimeOffset.UtcNow.AddDays(90)));
            }
        }
    }
}

using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Acme;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.BackFillerService.Tests.Acme
{
    /// <summary>
    /// BackFiller ACME on-disk layout matches shared FQDN path conventions.
    /// </summary>

    public sealed class BackFillerAcmeFilesystemParityTests
    {
        /// <summary>
        /// Contract: BackFiller certificate identity uses common fqdn paths.
        /// </summary>

        [Fact]
        public void BackFiller_certificate_identity_uses_common_fqdn_paths()
        {
            var options = BackFillerTestOptions.CreateValid();
            var fqdn = CertificateIdentities.NormalizeFqdn(options.Fqdn);
            Assert.Equal("backfiller01.usenet.ninja", fqdn);
            Assert.Equal(
                new[] { "backfiller01.usenet.ninja" },
                CertificateIdentities.ForFqdn(fqdn, includeNewsHostname: false));
            Assert.DoesNotContain(CertificateIdentities.NewsHostname, CertificateIdentities.ForFqdn(fqdn, includeNewsHostname: false));

            const string certs = "certs-root";
            Assert.Equal(
                Path.Join(certs, "live", fqdn, "current"),
                AcmePaths.CurrentGenerationPointerPath(certs, fqdn));
            Assert.Equal(
                Path.Join(certs, "live", fqdn, "gens"),
                AcmePaths.GenerationsDir(certs, fqdn));
            Assert.Equal(
                Path.Join(certs, "live", fqdn, "dns01"),
                AcmePaths.Dns01RecoveryDir(certs, fqdn));
            Assert.Equal(
                Path.Join(certs, "journal", fqdn + ".json"),
                AcmePaths.TransactionJournalPath(certs, fqdn));
            Assert.Equal(
                Path.Join(certs, "live", fqdn, ".issuance.lock"),
                AcmePaths.IssuanceLockPath(certs, fqdn));
            Assert.Equal(
                Path.Join(certs, "account", "private_key.der"),
                AcmePaths.AccountKeyPath(certs));
            Assert.DoesNotContain(
                Path.Join("live", "gens"),
                AcmePaths.GenerationsDir(certs, fqdn),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                Path.Join("live", "current"),
                AcmePaths.CurrentGenerationPointerPath(certs, fqdn),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Contract: BackFiller uses shared acme account environment variable.
        /// </summary>

        [Fact]
        public void BackFiller_uses_shared_acme_account_environment_variable()
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            Assert.Equal("security@usenet.ninja", pairs[AcmeCloudflareOptions.AcmeAccountConfigurationKey]);
            Assert.False(pairs.ContainsKey("BackFiller:AcmeEmail"));
            Assert.Equal("VECTOR__ACMEACCOUNT", AcmeCloudflareOptions.AcmeAccountEnvironmentVariable);
            Assert.DoesNotContain("BACKFILLER", AcmeCloudflareOptions.AcmeAccountEnvironmentVariable, StringComparison.OrdinalIgnoreCase);
        }
    }
}

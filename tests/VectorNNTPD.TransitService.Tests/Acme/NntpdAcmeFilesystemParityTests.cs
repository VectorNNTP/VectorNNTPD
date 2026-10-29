using VectorNNTPD.Common.Acme;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.TransitService.Tests.Acme;

public sealed class NntpdAcmeFilesystemParityTests
{
    [Fact]
    public void Nntpd_certificate_identity_uses_common_fqdn_paths()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ServerId = 1;
        options.DnsSuffix = "usenet.ninja";
        var fqdn = CertificateIdentities.NormalizeFqdn(options.Fqdn);
        Assert.Equal("nntpd01.usenet.ninja", fqdn);
        Assert.Equal(
            new[] { "nntpd01.usenet.ninja", CertificateIdentities.NewsHostname },
            CertificateIdentities.ForFqdn(fqdn));

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

    [Fact]
    public void Nntpd_uses_shared_acme_account_environment_variable()
    {
        Assert.Equal("VECTOR__ACMEACCOUNT", AcmeCloudflareOptions.AcmeAccountEnvironmentVariable);
        Assert.Equal(
            AcmeCloudflareOptions.AcmeAccountEnvironmentVariable,
            NntpdOptions.AcmeAccountEnvironmentVariable);
        Assert.DoesNotContain("NNTPD", NntpdOptions.AcmeAccountEnvironmentVariable, StringComparison.OrdinalIgnoreCase);
    }
}

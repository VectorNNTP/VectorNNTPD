using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.TestDoubles;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.TransitService.Tests.Acme;

public sealed class AcmeConfigurationTests
{
    internal const string TestPfxPassword = "unit-test-pfx-password";

    [Fact]
    public void Defaults_MatchSpecification()
    {
        var options = new NntpdOptions();
        Assert.Equal(NntpdOptions.DefaultAcmeDirectoryUrl, options.AcmeDirectoryUrl);
        Assert.Equal(NntpdOptions.DefaultAcmeStateDir, options.AcmeStateDir);
        Assert.Equal(30, options.AcmeRenewalThresholdDays);
        Assert.Equal(string.Empty, options.AcmeEmail);
        Assert.Equal(string.Empty, options.AcmeCertificatePassword);
        Assert.Contains("staging", options.AcmeDirectoryUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingEmailAndPassword_WithTlsDisabled_Succeeds()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindPortTls = 0;
        options.AcmeEmail = string.Empty;
        options.AcmeCertificatePassword = string.Empty;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void MissingEmail_WithTlsEnabled_DoesNotFailOptionsValidation()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindPortTls = 563;
        options.AcmeEmail = string.Empty;
        options.AcmeCertificatePassword = TestPfxPassword;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Succeeded, NntpdOptionsValidator.JoinFailures(result));
    }

    [Fact]
    public void MissingPassword_WithTlsEnabled_DoesNotFailOptionsValidation()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindPortTls = 563;
        options.AcmeEmail = "ops@example.org";
        options.AcmeCertificatePassword = string.Empty;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Succeeded, NntpdOptionsValidator.JoinFailures(result));
    }

    [Fact]
    public void WhitespaceOnlyPassword_WithTlsEnabled_DoesNotFailOptionsValidation()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindPortTls = 563;
        options.AcmeEmail = "ops@example.org";
        options.AcmeCertificatePassword = "   ";
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Succeeded, NntpdOptionsValidator.JoinFailures(result));
    }

    [Fact]
    public void NonPositiveRenewalThreshold_FailsSharedConfiguration()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => NntpSharedConfigurationReader.Validate(
        [
            new NntpSharedConfigurationCandidate(
                1024,
                "news.example",
                null,
                "https://acme.example.test/directory",
                0,
                "0123456789abcdef0123456789abcdef",
                "usenet.ninja"),
        ]));
        Assert.Contains("acmerenewalthresholddays", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RenewalThresholdAboveNinety_IsAcceptedBySharedConfiguration()
    {
        var configuration = NntpSharedConfigurationReader.Validate(
        [
            new NntpSharedConfigurationCandidate(
                1024,
                "news.example",
                null,
                "https://acme.example.test/directory",
                91,
                "0123456789abcdef0123456789abcdef",
                "usenet.ninja"),
        ]);
        Assert.Equal(91, configuration.AcmeRenewalThresholdDays);
    }

    [Fact]
    public void CustomDirectoryAndStateDir_Accepted()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.AcmeDirectoryUrl = "https://acme.example.test/directory";
        options.AcmeStateDir = "var/acme-state";
        options.AcmeRenewalThresholdDays = 15;
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void HttpDirectoryUrl_IsAcceptedBySharedConfiguration()
    {
        var configuration = NntpSharedConfigurationReader.Validate(
        [
            new NntpSharedConfigurationCandidate(
                1024,
                "news.example",
                null,
                "http://acme.example.test/directory",
                14,
                "0123456789abcdef0123456789abcdef",
                "usenet.ninja"),
        ]);
        Assert.Equal("http://acme.example.test/directory", configuration.AcmeDirectoryUrl);
    }

    [Fact]
    public void EnvironmentVariable_PasswordName_IsExact()
    {
        Assert.Equal("VECTOR__ACMECERTIFICATEPASSWORD", NntpdOptions.AcmeCertificatePasswordEnvironmentVariable);
    }
}

public sealed class CertificateIdentitiesTests
{
    [Fact]
    public void ForFqdn_ReturnsExactSanPair()
    {
        var ids = CertificateIdentities.ForFqdn("nntpd01.usenet.ninja");
        Assert.Equal(new[] { "nntpd01.usenet.ninja", "news.usenet.ninja" }, ids);
    }

    [Fact]
    public void NewsTypo_IsNotUsed()
    {
        Assert.DoesNotContain("ninaja", CertificateIdentities.NewsHostname, StringComparison.Ordinal);
        Assert.Equal("news.usenet.ninja", CertificateIdentities.NewsHostname);
    }
}

public sealed class AccountStoreTests
{
    [Fact]
    public void FirstSave_ThenLoad_ReusesAccount_AsPkcs8Der()
    {
        using var dir = new TempAcmeDir();
        var store = new AccountStore(dir.Path);
        Assert.Null(store.Load());

        using var rsa = RSA.Create(2048);
        var key = rsa.ExportPkcs8PrivateKey();
        var state = new AcmeAccountState(
            "https://acme.example/acme/acct/1",
            NntpdOptions.DefaultAcmeDirectoryUrl,
            key);
        store.Save(state);

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(state.AccountUri, loaded.AccountUri);
        Assert.True(key.AsSpan().SequenceEqual(loaded.PrivateKeyDer));
        Assert.False(DerCrypto.LooksLikePem(loaded.PrivateKeyDer));
        Assert.False(File.ReadAllText(AcmePaths.AccountKeyPath(dir.Path)).Contains("BEGIN", StringComparison.Ordinal));
    }

    [Fact]
    public void EnsureRegistered_ReusesExisting_WithoutCallingRegister()
    {
        using var dir = new TempAcmeDir();
        var store = new AccountStore(dir.Path);
        using var rsa = RSA.Create(2048);
        var key = rsa.ExportPkcs8PrivateKey();
        store.Save(new AcmeAccountState("https://acme.example/acme/acct/1", "https://dir.example/directory", key));

        var registerCalls = 0;
        var result = store.EnsureRegistered(
            "https://dir.example/directory",
            () => throw new InvalidOperationException("should not generate"),
            _ =>
            {
                registerCalls++;
                return ("x", "");
            });

        Assert.Equal(0, registerCalls);
        Assert.Equal("https://acme.example/acme/acct/1", result.AccountUri);
    }

    [Fact]
    public void EnsureRegistered_FirstStartup_PersistsAccount()
    {
        using var dir = new TempAcmeDir();
        var store = new AccountStore(dir.Path);
        using var rsa = RSA.Create(2048);
        var key = rsa.ExportPkcs8PrivateKey();
        var registerCalls = 0;

        var result = store.EnsureRegistered(
            "https://dir.example/directory",
            () => key,
            k =>
            {
                registerCalls++;
                Assert.True(k.AsSpan().SequenceEqual(key));
                return ("https://acme.example/acme/acct/9", "body");
            });

        Assert.Equal(1, registerCalls);
        Assert.Equal("https://acme.example/acme/acct/9", result.AccountUri);
        Assert.False(File.Exists(AcmePaths.AccountPendingKeyPath(dir.Path)));
        Assert.NotNull(store.Load());
    }

    [Fact]
    public void IncompleteAccount_ThrowsWithoutDeleting()
    {
        using var dir = new TempAcmeDir();
        AcmePaths.EnsureStateLayout(dir.Path);
        File.WriteAllBytes(AcmePaths.AccountKeyPath(dir.Path), [1, 2, 3]);
        var store = new AccountStore(dir.Path);
        Assert.Throws<AcmeAccountException>(() => store.Load());
        Assert.True(File.Exists(AcmePaths.AccountKeyPath(dir.Path)));
    }
}

public sealed class PfxStoreAndValidationTests
{
    private static readonly string[] RequiredDomains = ["nntpd01.usenet.ninja", "news.usenet.ninja"];

    [Fact]
    public void SaveAndLoad_UsesBinaryPfx_AndSurvivesRestart()
    {
        using var dir = new TempAcmeDir();
        var store = CreateStore(dir.Path);
        var material = TestCertificateFactory.CreateMaterial(
            RequiredDomains,
            AcmeConfigurationTests.TestPfxPassword,
            notAfter: DateTimeOffset.UtcNow.AddDays(60));

        store.Save(material);
        Assert.False(DerCrypto.LooksLikePem(material.PfxBytes));

        var store2 = CreateStore(dir.Path);
        var loaded = store2.Load();
        Assert.NotNull(loaded);
        Assert.False(DerCrypto.LooksLikePem(loaded.PfxBytes));

        var paths = store2.Paths();
        Assert.EndsWith("certificate.pfx", paths.PfxPath, StringComparison.OrdinalIgnoreCase);
        var onDisk = File.ReadAllBytes(paths.PfxPath);
        Assert.False(DerCrypto.LooksLikePem(onDisk));
        Assert.True(onDisk.Length > 0);

        using var cert = PfxCrypto.LoadCertificate(loaded.PfxBytes, AcmeConfigurationTests.TestPfxPassword);
        Assert.True(cert.HasPrivateKey);
        var sans = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single()
            .EnumerateDnsNames().Select(n => n.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("nntpd01.usenet.ninja", sans);
        Assert.Contains("news.usenet.ninja", sans);
    }

    [Fact]
    public void IncorrectPassword_FailsValidation()
    {
        var material = TestCertificateFactory.CreateMaterial(
            RequiredDomains,
            AcmeConfigurationTests.TestPfxPassword,
            notAfter: DateTimeOffset.UtcNow.AddDays(60));
        var status = CertificateValidator.ValidatePfx(
            material.PfxBytes,
            "wrong-password",
            RequiredDomains,
            TimeSpan.FromDays(30));
        Assert.False(status.Usable);
        Assert.StartsWith("parse_failed", status.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-password", status.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void IncompleteGeneration_WithoutComplete_IsIgnored()
    {
        using var dir = new TempAcmeDir();
        var store = CreateStore(dir.Path);
        var genId = Guid.NewGuid().ToString("N");
        var genDir = AcmePaths.GenerationDir(dir.Path, "nntpd01.usenet.ninja", genId);
        Directory.CreateDirectory(genDir);
        var paths = AcmePaths.GenerationCertificatePaths(dir.Path, "nntpd01.usenet.ninja", genId);
        var material = TestCertificateFactory.CreateMaterial(
            RequiredDomains,
            AcmeConfigurationTests.TestPfxPassword,
            notAfter: DateTimeOffset.UtcNow.AddDays(60));
        File.WriteAllBytes(paths.PfxPath, material.PfxBytes);
        Assert.Null(store.Load());
    }

    [Fact]
    public void FailedReplacement_PreservesKnownGood()
    {
        using var dir = new TempAcmeDir();
        var store = CreateStore(dir.Path);
        var first = TestCertificateFactory.CreateMaterial(
            RequiredDomains,
            AcmeConfigurationTests.TestPfxPassword,
            notAfter: DateTimeOffset.UtcNow.AddDays(60));
        store.Save(first);
        var firstPaths = store.Paths();

        var orphan = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(AcmePaths.GenerationDir(dir.Path, "nntpd01.usenet.ninja", orphan));
        store.Recover();
        Assert.Equal(firstPaths.PfxPath, store.Paths().PfxPath);
        Assert.NotNull(store.Load());
    }

    [Fact]
    public void InvalidPfxBytes_DoNotBecomeCurrent()
    {
        using var dir = new TempAcmeDir();
        var store = CreateStore(dir.Path);
        var good = TestCertificateFactory.CreateMaterial(
            RequiredDomains,
            AcmeConfigurationTests.TestPfxPassword,
            notAfter: DateTimeOffset.UtcNow.AddDays(60));
        store.Save(good);
        var currentBefore = store.Paths().PfxPath;

        Assert.ThrowsAny<AcmeException>(() =>
            store.Save(new CertificateMaterial(
                [1, 2, 3, 4],
                RequiredDomains,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(30))));

        Assert.Equal(currentBefore, store.Paths().PfxPath);
        Assert.NotNull(store.Load());
    }

    [Fact]
    public void Validate_WrongSan_Rejects()
    {
        var material = TestCertificateFactory.CreateMaterial(
            ["wrong.example"],
            AcmeConfigurationTests.TestPfxPassword,
            notAfter: DateTimeOffset.UtcNow.AddDays(60));
        var status = CertificateValidator.ValidatePfx(
            material.PfxBytes,
            AcmeConfigurationTests.TestPfxPassword,
            RequiredDomains,
            TimeSpan.FromDays(30));
        Assert.False(status.Usable);
        Assert.Equal("missing_san", status.Reason);
    }

    [Fact]
    public void Validate_RenewalThreshold_Detected()
    {
        var material = TestCertificateFactory.CreateMaterial(
            RequiredDomains,
            AcmeConfigurationTests.TestPfxPassword,
            notAfter: DateTimeOffset.UtcNow.AddDays(10));
        var status = CertificateValidator.ValidatePfx(
            material.PfxBytes,
            AcmeConfigurationTests.TestPfxPassword,
            RequiredDomains,
            TimeSpan.FromDays(30));
        Assert.True(status.Usable);
        Assert.True(status.DueForRenewal);
    }

    [Fact]
    public void Validate_OutsideThreshold_NotDue()
    {
        var material = TestCertificateFactory.CreateMaterial(
            RequiredDomains,
            AcmeConfigurationTests.TestPfxPassword,
            notAfter: DateTimeOffset.UtcNow.AddDays(90));
        var status = CertificateValidator.ValidatePfx(
            material.PfxBytes,
            AcmeConfigurationTests.TestPfxPassword,
            RequiredDomains,
            TimeSpan.FromDays(30));
        Assert.True(status.Usable);
        Assert.False(status.DueForRenewal);
    }

    [Fact]
    public void Validate_Expired_Rejects()
    {
        var material = TestCertificateFactory.CreateMaterial(
            RequiredDomains,
            AcmeConfigurationTests.TestPfxPassword,
            notAfter: DateTimeOffset.UtcNow.AddDays(-1),
            notBefore: DateTimeOffset.UtcNow.AddDays(-40));
        var status = CertificateValidator.ValidatePfx(
            material.PfxBytes,
            AcmeConfigurationTests.TestPfxPassword,
            RequiredDomains,
            TimeSpan.FromDays(30));
        Assert.False(status.Usable);
        Assert.Equal("expired", status.Reason);
    }

    private static CertificateStore CreateStore(string path) =>
        new(path, "nntpd01.usenet.ninja", AcmeConfigurationTests.TestPfxPassword, RequiredDomains, TimeSpan.FromDays(30));
}

public sealed class CertificateManagerTests
{
    private static readonly string[] RequiredDomains = ["nntpd01.usenet.ninja", "news.usenet.ninja"];

    [Fact]
    public async Task Ensure_ReusesValidCertificate_WithoutIssuance()
    {
        using var dir = new TempAcmeDir();
        var store = new CertificateStore(
            dir.Path,
            "nntpd01.usenet.ninja",
            AcmeConfigurationTests.TestPfxPassword,
            RequiredDomains,
            TimeSpan.FromDays(30));
        var material = TestCertificateFactory.CreateMaterial(
            RequiredDomains,
            AcmeConfigurationTests.TestPfxPassword,
            notAfter: DateTimeOffset.UtcNow.AddDays(90));
        store.Save(material);

        var issuer = new FakeCertificateIssuer();
        var manager = CreateManager(dir.Path, store, issuer);

        var ensured = await manager.EnsureCertificateAsync(CancellationToken.None);
        Assert.Equal(0, issuer.IssueCallCount);
        Assert.True(Math.Abs((material.NotAfter - ensured.NotAfter).TotalSeconds) < 2);
        Assert.Contains("news.usenet.ninja", ensured.Domains);
    }

    [Fact]
    public async Task Ensure_IssuesWhenAbsent()
    {
        using var dir = new TempAcmeDir();
        var store = new CertificateStore(
            dir.Path,
            "nntpd01.usenet.ninja",
            AcmeConfigurationTests.TestPfxPassword,
            RequiredDomains,
            TimeSpan.FromDays(30));
        var issuer = new FakeCertificateIssuer();
        var manager = CreateManager(dir.Path, store, issuer);

        _ = await manager.EnsureCertificateAsync(CancellationToken.None);
        Assert.Equal(1, issuer.IssueCallCount);
        using var cert = manager.CreateTlsCertificate();
        Assert.True(cert.HasPrivateKey);
        var sans = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single()
            .EnumerateDnsNames().Select(n => n.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("nntpd01.usenet.ninja", sans);
        Assert.Contains("news.usenet.ninja", sans);
        Assert.Equal(2, sans.Count);
        Assert.True(File.Exists(store.Paths().PfxPath));
    }

    [Fact]
    public async Task RenewIfDue_SkipsWhenNotDue()
    {
        using var dir = new TempAcmeDir();
        var store = new CertificateStore(
            dir.Path,
            "nntpd01.usenet.ninja",
            AcmeConfigurationTests.TestPfxPassword,
            RequiredDomains,
            TimeSpan.FromDays(30));
        store.Save(TestCertificateFactory.CreateMaterial(
            RequiredDomains,
            AcmeConfigurationTests.TestPfxPassword,
            notAfter: DateTimeOffset.UtcNow.AddDays(90)));
        var issuer = new FakeCertificateIssuer();
        var manager = CreateManager(dir.Path, store, issuer);

        Assert.False(await manager.RenewIfDueAsync(CancellationToken.None));
        Assert.Equal(0, issuer.IssueCallCount);
    }

    [Fact]
    public async Task RenewIfDue_RenewsWithinThreshold()
    {
        using var dir = new TempAcmeDir();
        var store = new CertificateStore(
            dir.Path,
            "nntpd01.usenet.ninja",
            AcmeConfigurationTests.TestPfxPassword,
            RequiredDomains,
            TimeSpan.FromDays(30));
        store.Save(TestCertificateFactory.CreateMaterial(
            RequiredDomains,
            AcmeConfigurationTests.TestPfxPassword,
            notAfter: DateTimeOffset.UtcNow.AddDays(10)));
        var issuer = new FakeCertificateIssuer();
        var manager = CreateManager(dir.Path, store, issuer);

        Assert.True(await manager.RenewIfDueAsync(CancellationToken.None));
        Assert.Equal(1, issuer.IssueCallCount);
    }

    [Fact]
    public async Task FailedIssuance_PreservesPriorPfx()
    {
        using var dir = new TempAcmeDir();
        var store = new CertificateStore(
            dir.Path,
            "nntpd01.usenet.ninja",
            AcmeConfigurationTests.TestPfxPassword,
            RequiredDomains,
            TimeSpan.FromDays(30));
        var prior = TestCertificateFactory.CreateMaterial(
            RequiredDomains,
            AcmeConfigurationTests.TestPfxPassword,
            notAfter: DateTimeOffset.UtcNow.AddDays(10));
        store.Save(prior);
        var priorPath = store.Paths().PfxPath;
        var issuer = new FakeCertificateIssuer { ThrowOnIssue = new AcmeOrderException("boom", "fail") };
        var manager = CreateManager(dir.Path, store, issuer);

        Assert.False(await manager.RenewIfDueAsync(CancellationToken.None));
        Assert.Equal(priorPath, store.Paths().PfxPath);
        Assert.NotNull(store.Load());
    }

    private static CertificateManager CreateManager(
        string stateDir,
        CertificateStore store,
        ICertificateIssuer issuer) =>
        new(
            "nntpd01.usenet.ninja",
            stateDir,
            NntpdOptions.DefaultAcmeDirectoryUrl,
            store,
            issuer,
            AcmeConfigurationTests.TestPfxPassword,
            TimeSpan.FromDays(30),
            NullLogger<CertificateManager>.Instance);
}

public sealed class Dns01SolverTests
{
    [Fact]
    public async Task PlaceWaitCleanup_CreatesAndRemovesTxt()
    {
        using var dir = new TempAcmeDir();
        var cf = new FakeCloudflareDnsClient();
        var resolver = new FakeTxtResolver();
        var solver = new Dns01Solver(
            cf,
            "zone-1",
            resolver,
            "nntpd01.usenet.ninja",
            dir.Path,
            propagationTimeout: TimeSpan.FromSeconds(2),
            propagationInterval: TimeSpan.FromMilliseconds(10));

        var spec = new Dns01ChallengeSpec("nntpd01.usenet.ninja", "validation-token");
        resolver.Set(spec.RecordName, spec.Validation);

        await solver.PlaceAsync([spec], CancellationToken.None);
        Assert.Equal(1, cf.CreateCallCount);
        var created = cf.Snapshot().Single(r => r.Type == CloudflareDnsRecordTypes.TXT);
        Assert.Equal(Dns01Solver.ChallengeTtlSeconds, created.Ttl);
        Assert.False(created.Proxied);

        await solver.WaitPropagatedAsync([spec], CancellationToken.None);
        await solver.CleanupAsync(CancellationToken.None);
        Assert.Equal(1, cf.DeleteCallCount);
        Assert.Empty(cf.Snapshot());
    }

    [Fact]
    public async Task CancellationDuringPlace_Propagates()
    {
        using var dir = new TempAcmeDir();
        var cf = new FakeCloudflareDnsClient();
        cf.OnMutate = async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
        };
        var solver = new Dns01Solver(cf, "zone-1", new FakeTxtResolver(), "nntpd01.usenet.ninja", dir.Path);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            solver.PlaceAsync([new Dns01ChallengeSpec("nntpd01.usenet.ninja", "tok")], cts.Token));
    }
}

public sealed class AcmeLifecycleServiceTests
{
    [Fact]
    public async Task TlsDisabled_DoesNotInitializeAcme_OrRequireCertificate()
    {
        using var dir = new TempAcmeDir();
        var options = TestHostFactory.CreateValidOptions();
        options.BindPortTls = 0;
        options.AcmeStateDir = dir.Path;
        options.AcmeEmail = string.Empty;
        options.AcmeCertificatePassword = string.Empty;

        var factory = new AcmeComponentFactory(
            Options.Create<AcmeCloudflareOptions>(options),
            new FakeCloudflareDnsClient(),
            new FakeHttpClientFactory(),
            NullLoggerFactory.Instance);
        var service = new AcmeCertificateService(
            Options.Create<AcmeCloudflareOptions>(options),
            factory,
            new TlsCertificateContextProvider(NullLogger<TlsCertificateContextProvider>.Instance),
            new AcmeCertificateReadiness(),
            NullLogger<AcmeCertificateService>.Instance);

        await service.StartAsync(CancellationToken.None);
        Assert.False(service.AcmeInitialized);
        Assert.False(Directory.Exists(Path.Join(dir.Path, "account")));
        Assert.False(Directory.Exists(Path.Join(dir.Path, "live")));
        await service.StopAsync(CancellationToken.None);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task TlsEnabled_EnsureRuns_AndProviderAvailable()
    {
        using var dir = new TempAcmeDir();
        var options = TestHostFactory.CreateValidOptions();
        options.BindPortTls = 563;
        options.AcmeEmail = "ops@example.org";
        options.AcmeCertificatePassword = AcmeConfigurationTests.TestPfxPassword;
        options.AcmeStateDir = dir.Path;

        var domains = CertificateIdentities.ForFqdn(options.Fqdn);
        var store = new CertificateStore(
            dir.Path,
            options.Fqdn,
            options.AcmeCertificatePassword,
            domains,
            TimeSpan.FromDays(30));
        store.Save(TestCertificateFactory.CreateMaterial(
            domains,
            options.AcmeCertificatePassword,
            notAfter: DateTimeOffset.UtcNow.AddDays(90)));

        var manager = new CertificateManager(
            options.Fqdn,
            dir.Path,
            options.AcmeDirectoryUrl,
            store,
            new FakeCertificateIssuer(),
            options.AcmeCertificatePassword,
            TimeSpan.FromDays(30),
            NullLogger<CertificateManager>.Instance);

        var factory = new InjectedManagerFactory(manager, Options.Create<AcmeCloudflareOptions>(options));
        var service = new AcmeCertificateService(
            Options.Create<AcmeCloudflareOptions>(options),
            factory,
            new TlsCertificateContextProvider(NullLogger<TlsCertificateContextProvider>.Instance),
            new AcmeCertificateReadiness(),
            NullLogger<AcmeCertificateService>.Instance);
        await service.StartAsync(CancellationToken.None);
        Assert.True(service.AcmeInitialized);
        Assert.NotNull(manager.CurrentMaterial);
        await service.StopAsync(CancellationToken.None);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task TlsEnabled_MissingCertificateAndFailingIssuer_FailsStartup()
    {
        using var dir = new TempAcmeDir();
        var options = TestHostFactory.CreateValidOptions();
        options.BindPortTls = 563;
        options.AcmeEmail = "ops@example.org";
        options.AcmeCertificatePassword = AcmeConfigurationTests.TestPfxPassword;
        options.AcmeStateDir = dir.Path;

        var domains = CertificateIdentities.ForFqdn(options.Fqdn);
        var manager = new CertificateManager(
            options.Fqdn,
            dir.Path,
            options.AcmeDirectoryUrl,
            new CertificateStore(
                dir.Path,
                options.Fqdn,
                options.AcmeCertificatePassword,
                domains,
                TimeSpan.FromDays(30)),
            new FakeCertificateIssuer { ThrowOnIssue = new AcmeOrderException("fail", "x") },
            options.AcmeCertificatePassword,
            TimeSpan.FromDays(30),
            NullLogger<CertificateManager>.Instance);

        var factory = new InjectedManagerFactory(manager, Options.Create<AcmeCloudflareOptions>(options));
        var service = new AcmeCertificateService(
            Options.Create<AcmeCloudflareOptions>(options),
            factory,
            new TlsCertificateContextProvider(NullLogger<TlsCertificateContextProvider>.Instance),
            new AcmeCertificateReadiness(),
            NullLogger<AcmeCertificateService>.Instance);
        await Assert.ThrowsAsync<AcmeOrderException>(() => service.StartAsync(CancellationToken.None));
        await service.DisposeAsync();
    }

    private sealed class InjectedManagerFactory : AcmeComponentFactory
    {
        private readonly CertificateManager _manager;

        public InjectedManagerFactory(CertificateManager manager, IOptions<AcmeCloudflareOptions> options)
            : base(
                options,
                new FakeCloudflareDnsClient(),
                new FakeHttpClientFactory(),
                NullLoggerFactory.Instance)
        {
            _manager = manager;
        }

        internal override CertificateManager? GetOrCreateManager() => _manager;
    }
}

internal sealed class FakeCertificateIssuer : ICertificateIssuer
{
    public int IssueCallCount { get; private set; }

    public Exception? ThrowOnIssue { get; set; }

    public Task<CertificateMaterial> IssueAsync(
        IReadOnlyList<string> domains,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IssueCallCount++;
        if (ThrowOnIssue is not null)
        {
            throw ThrowOnIssue;
        }

        return Task.FromResult(
            TestCertificateFactory.CreateMaterial(
                domains,
                AcmeConfigurationTests.TestPfxPassword,
                notAfter: DateTimeOffset.UtcNow.AddDays(90)));
    }
}

internal sealed class FakeTxtResolver : IAuthoritativeTxtResolver
{
    private readonly Dictionary<string, IReadOnlyList<string>> _map = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string name, params string[] values) => _map[name] = values;

    public Task<IReadOnlyList<string>> LookupTxtAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _map.TryGetValue(name, out var values) ? values : Array.Empty<string>());
    }
}

internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}

internal sealed class TempAcmeDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Join(
        System.IO.Path.GetTempPath(),
        "vectornntp-acme-" + Guid.NewGuid().ToString("N"));

    public TempAcmeDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        // codeql[cs/catch-of-all-exceptions] Best-effort test cleanup must tolerate any filesystem or disposal failure.
        catch (Exception ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
            // Best-effort test cleanup.
        }
    }
}

internal static class TestCertificateFactory
{
    public static CertificateMaterial CreateMaterial(
        IReadOnlyList<string> dnsNames,
        string password,
        DateTimeOffset notAfter,
        DateTimeOffset? notBefore = null)
    {
        var before = notBefore ?? DateTimeOffset.UtcNow.AddDays(-1);
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
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                critical: false));

        using var cert = request.CreateSelfSigned(before.UtcDateTime, notAfter.UtcDateTime);
        var pfx = PfxCrypto.ExportPfx(cert, intermediateCertificates: [], password);
        return new CertificateMaterial(
            pfx,
            dnsNames.Select(n => n.ToLowerInvariant()).OrderBy(n => n).ToArray(),
            before,
            notAfter);
    }
}

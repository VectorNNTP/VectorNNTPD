using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Hosting;
using VectorNNTPD.ReaderService.Logging;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.RabbitMq;
using VectorNNTPD.ReaderService.Redis;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.ReaderService.Tests.Configuration;

/// <summary>
/// Proves NNTPD-owned ACME state binds from <c>Nntpd</c>, that the ACME account
/// email and certificate password are not supplied by <c>VECTOR__ACMEACCOUNT</c> or
/// <c>VECTOR__ACMECERTIFICATEPASSWORD</c>, and that relative <c>AcmeStateDir</c> resolves
/// through Common <see cref="ApplicationLocalPath.ResolveApplicationLocalPath"/>
/// against <see cref="AppContext.BaseDirectory"/>.
/// </summary>
[Collection("WorkingDirectory")]
public sealed class NntpdAcmeConfigurationOwnershipTests
{
    private const string NntpdDirectoryUrl = "https://acme-staging-v02.api.letsencrypt.org/directory";
    private const string SharedAcmeAccount = "shared-acme@example.test";
    private const int NntpdRenewalDays = 14;
    private const string NntpdStateDir = "certs/";
    private const string RootDirectoryUrl = "https://root-must-not-bind.example/directory";
    private const string RootEmail = "root-must-not-bind@example.test";
    private const string RootStateDir = "root-must-not-bind/";
    private const string TestCertificatePassword = "unit-test-nntpd-pfx-password";

    [Fact]
    public void Nntpd_json_does_not_own_the_acme_directory_url()
    {
        var json = File.ReadAllText(FindRepoFile(Path.Join("src", "VectorNNTPD.ReaderService", "VectorNNTPD.ReaderService.json")));
        Assert.DoesNotContain("\"AcmeDirectoryUrl\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Shared_acme_account_email_is_not_supplied_by_vector_environment()
    {
        using var host = CreateHost();
        var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;
        Assert.Equal(string.Empty, options.AcmeEmail);
        Assert.NotEqual(SharedAcmeAccount, options.AcmeEmail);
        Assert.Equal(
            AcmeCloudflareOptions.AcmeAccountEnvironmentVariable,
            "VECTOR__ACMEACCOUNT");
    }

    [Fact]
    public void Nntpd_json_does_not_own_the_acme_renewal_threshold()
    {
        var json = File.ReadAllText(FindRepoFile(Path.Join("src", "VectorNNTPD.ReaderService", "VectorNNTPD.ReaderService.json")));
        Assert.DoesNotContain("\"AcmeRenewalThresholdDays\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Nntpd_acme_state_dir_survives_configuration_binding()
    {
        using var host = CreateHost();
        var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;
        Assert.Equal(ExpectedResolvedStateDir(AppContext.BaseDirectory), options.AcmeStateDir);
        Assert.DoesNotContain("root-must-not-bind", options.AcmeStateDir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Root_acme_values_cannot_overwrite_nntpd_owned_settings()
    {
        using var host = CreateHost();
        var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;
        var acme = host.Services.GetRequiredService<IOptions<AcmeCloudflareOptions>>().Value;

        Assert.Equal(string.Empty, options.AcmeEmail);
        Assert.Equal(ExpectedResolvedStateDir(AppContext.BaseDirectory), options.AcmeStateDir);

        Assert.NotEqual(RootEmail, options.AcmeEmail);
        Assert.NotEqual(SharedAcmeAccount, options.AcmeEmail);
        Assert.NotEqual(RootStateDir, options.AcmeStateDir);

        Assert.Same(options, acme);
    }

    [Fact]
    public void Vector_acme_certificate_password_is_not_supplied_by_the_environment_variable()
    {
        var previous = Environment.GetEnvironmentVariable(NntpdOptions.AcmeCertificatePasswordEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                NntpdOptions.AcmeCertificatePasswordEnvironmentVariable,
                TestCertificatePassword);

            using var host = CreateHost();
            var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;
            Assert.Equal(string.Empty, options.AcmeCertificatePassword);
            Assert.NotEqual(TestCertificatePassword, options.AcmeCertificatePassword);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                NntpdOptions.AcmeCertificatePasswordEnvironmentVariable,
                previous);
        }
    }

    [Fact]
    public void Nntpd_does_not_require_an_application_specific_acme_password_variable()
    {
        Assert.Equal(
            "VECTOR__ACMECERTIFICATEPASSWORD",
            NntpdOptions.AcmeCertificatePasswordEnvironmentVariable);
        Assert.Equal(
            AcmeCloudflareOptions.AcmeCertificatePasswordEnvironmentVariable,
            NntpdOptions.AcmeCertificatePasswordEnvironmentVariable);
        Assert.Null(
            typeof(NntpdOptions).GetField(
                "AcmeCertificatePasswordEnvironmentVariable",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));
        Assert.DoesNotContain(
            "NNTPD__",
            NntpdOptions.AcmeCertificatePasswordEnvironmentVariable,
            StringComparison.OrdinalIgnoreCase);

        var previousShared = Environment.GetEnvironmentVariable(NntpdOptions.AcmeCertificatePasswordEnvironmentVariable);
        var previousNntpd = Environment.GetEnvironmentVariable("NNTPD__ACMECERTIFICATEPASSWORD");
        try
        {
            Environment.SetEnvironmentVariable(
                NntpdOptions.AcmeCertificatePasswordEnvironmentVariable,
                TestCertificatePassword);
            Environment.SetEnvironmentVariable("NNTPD__ACMECERTIFICATEPASSWORD", "nntpd-specific-must-not-be-required");

            using var host = CreateHost();
            var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;
            var rabbit = host.Services.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
            Assert.Equal(string.Empty, options.AcmeCertificatePassword);
            Assert.NotEqual(TestCertificatePassword, options.AcmeCertificatePassword);
            Assert.NotEqual("nntpd-specific-must-not-be-required", options.AcmeCertificatePassword);
            Assert.Null(rabbit.Username);
            Assert.Null(rabbit.Password);
        }
        finally
        {
            Environment.SetEnvironmentVariable(NntpdOptions.AcmeCertificatePasswordEnvironmentVariable, previousShared);
            Environment.SetEnvironmentVariable("NNTPD__ACMECERTIFICATEPASSWORD", previousNntpd);
        }
    }

    [Fact]
    public void Relative_nntpd_acme_state_dir_resolves_under_appcontext_base_directory()
    {
        using var host = CreateHost(contentRootPath: AppContext.BaseDirectory);
        var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;
        var expected = ExpectedResolvedStateDir(AppContext.BaseDirectory);
        Assert.Equal(expected, options.AcmeStateDir);
        Assert.StartsWith(
            Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            options.AcmeStateDir,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "ContentRootPath = AppContext.BaseDirectory",
            File.ReadAllText(FindRepoFile(Path.Join("src", "VectorNNTPD.ReaderService", "Program.cs"))),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Changing_process_working_directory_does_not_change_resolved_acme_state_directory()
    {
        var previous = Environment.CurrentDirectory;
        var scratch = Directory.CreateTempSubdirectory("nntpd-acme-cwd-");
        try
        {
            Environment.CurrentDirectory = scratch.FullName;
            using var host = CreateHost(contentRootPath: AppContext.BaseDirectory);
            var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;
            var expected = ExpectedResolvedStateDir(AppContext.BaseDirectory);
            var cwdResolved = Path.GetFullPath(NntpdStateDir.Trim());

            Assert.Equal(expected, options.AcmeStateDir);
            Assert.NotEqual(cwdResolved, options.AcmeStateDir);
            Assert.DoesNotContain(
                Path.GetFullPath(scratch.FullName).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                options.AcmeStateDir,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            scratch.Delete(recursive: true);
        }
    }

    [Fact]
    public void Nntpd_and_backfiller_use_the_same_common_acme_path_resolution_mechanism()
    {
        using var host = CreateHost(contentRootPath: AppContext.BaseDirectory);
        var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;
        Assert.Equal(ExpectedResolvedStateDir(AppContext.BaseDirectory), options.AcmeStateDir);

        var nntpdHosting = File.ReadAllText(FindRepoFile(
            Path.Join("src", "VectorNNTPD.ReaderService", "Hosting", "NntpdServiceCollectionExtensions.cs")));
        var backFillerHosting = File.ReadAllText(FindRepoFile(
            Path.Join("src", "VectorNNTPD.BackFillerService", "Hosting", "BackFillerServiceCollectionExtensions.cs")));

        Assert.Contains("ApplicationLocalPath.ResolveApplicationLocalPath", nntpdHosting, StringComparison.Ordinal);
        Assert.Contains("ApplicationLocalPath.ResolveApplicationLocalPath", backFillerHosting, StringComparison.Ordinal);
        Assert.Contains("AppContext.BaseDirectory", nntpdHosting, StringComparison.Ordinal);
        Assert.Contains("AppContext.BaseDirectory", backFillerHosting, StringComparison.Ordinal);
    }

    private static string ExpectedResolvedStateDir(string contentRootPath) =>
        ApplicationLocalPath.ResolveApplicationLocalPath(NntpdStateDir, contentRootPath);

    private static IHost CreateHost(string? contentRootPath = null)
    {
        // VECTOR__ACMEACCOUNT overlays in-memory ACMEACCOUNT. Isolate from the
        // operator environment so these tests prove the shared-account contract.
        var previousAccount = Environment.GetEnvironmentVariable(
            AcmeCloudflareOptions.AcmeAccountEnvironmentVariable);
        Environment.SetEnvironmentVariable(
            AcmeCloudflareOptions.AcmeAccountEnvironmentVariable,
            SharedAcmeAccount);
        try
        {
            return BuildHost(contentRootPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                AcmeCloudflareOptions.AcmeAccountEnvironmentVariable,
                previousAccount);
        }
    }

    private static IHost BuildHost(string? contentRootPath)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "VectorNNTPD.ReaderService.Tests",
            ContentRootPath = contentRootPath ?? AppContext.BaseDirectory,
            EnvironmentName = Environments.Development,
        });

        builder.Configuration.AddInMemoryCollection(CreateConfigurationPairs());
        builder.Configuration.AddVectorEnvironmentVariables();
        builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        builder.Services.AddSingleton<ICloudflareDnsClient>(new FakeCloudflareDnsClient());
        builder.Services.AddSingleton<IRedisConnectionFactory, FakeRedisConnectionFactory>();
        builder.Services.AddSingleton<IRabbitMqConnectionFactory, FakeRabbitMqConnectionFactory>();
        builder.Services.AddSingleton<INntpDbConnectionFactory, FakeNntpDbConnectionFactory>();
        TestHostFactory.IsolateTransit(builder.Services);
        builder.ConfigureNntpdLogging(static lc => lc.MinimumLevel.Fatal());
        builder.Services.AddNntpReaderHosting(includePlaceholderService: false);
        return builder.Build();
    }

    private static Dictionary<string, string?> CreateConfigurationPairs()
    {
        return new Dictionary<string, string?>
        {
            [$"{NntpdOptions.SectionName}:ServerId"] = "1",
            [$"{NntpdOptions.SectionName}:{NntpdOptions.XTraceKeyConfigurationKey}"] =
                TestHostFactory.TestXTraceKey,
            [$"{NntpdOptions.SectionName}:LogDir"] = TestHostFactory.NewTestLogDir(),
            [$"{NntpdOptions.SectionName}:AcmeDirectoryUrl"] = NntpdDirectoryUrl,
            [$"{NntpdOptions.SectionName}:AcmeEmail"] = "nntpd-section-must-not-bind@example.test",
            [$"{NntpdOptions.SectionName}:AcmeRenewalThresholdDays"] = "14",
            [$"{NntpdOptions.SectionName}:AcmeStateDir"] = NntpdStateDir,
            [AcmeCloudflareOptions.AcmeAccountConfigurationKey] = SharedAcmeAccount,
            ["AcmeDirectoryUrl"] = RootDirectoryUrl,
            ["AcmeEmail"] = RootEmail,
            ["AcmeRenewalThresholdDays"] = "30",
            ["AcmeStateDir"] = RootStateDir,
            [$"{NntpdOptions.SectionName}:BindAddress:0"] = "*",
            [$"{NntpdOptions.SectionName}:BindPortTls"] = "0",
            [NntpdOptions.CloudFlareApiKeyConfigurationKey] = TestHostFactory.TestCloudFlareApiKey,
            [$"{NntpdOptions.SectionName}:{NntpdOptions.CloudFlareZoneIdConfigurationKey}"] =
                "5811a29d39a0732afb5f160c9b137c3d",
            [$"{NntpdOptions.SectionName}:DnsSuffix"] = "usenet.ninja",
            ["Redis:Host:0"] = "127.0.0.1",
            ["RabbitMQ:Hosts:0"] = "127.0.0.1",
            ["RabbitMQ:Username"] = "guest",
            ["RabbitMQ:Password"] = "guest",
            ["RabbitMQ:Management:BaseUrl"] = "http://127.0.0.1:15672",
            ["RabbitMQ:Management:RequestTimeoutSeconds"] = "5",
            [$"ConnectionStrings:{NntpDbOptions.ConnectionStringName}"] =
                TestHostFactory.TestNntpDbConnectionString,
        };
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Join(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {relativePath}.");
    }
}

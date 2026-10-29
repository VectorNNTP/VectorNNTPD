using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Hosting;
using VectorNNTPD.TransitService.Logging;
using VectorNNTPD.TransitService.NntpDb;
using VectorNNTPD.TransitService.RabbitMq;
using VectorNNTPD.TransitService.Redis;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.TestDoubles;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.TransitService.Tests.Configuration;

/// <summary>
/// Proves NNTPD application-local paths resolve against
/// <see cref="AppContext.BaseDirectory"/>, not the process working directory
/// or an IDE/project content root.
/// </summary>
[Collection("WorkingDirectory")]
public sealed class NntpdApplicationLocalPathTests
{
    [Fact]
    public void Relative_certs_resolves_beneath_application_base()
    {
        using var host = CreateHost(decoyContentRoot: CreateTempDir("nntpd-decoy-content-"));
        var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;
        Assert.Equal(Expected("certs/"), options.AcmeStateDir);
        Assert.StartsWith(Normalized(AppContext.BaseDirectory), options.AcmeStateDir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolved_certs_is_not_based_on_process_working_directory()
    {
        var previous = Environment.CurrentDirectory;
        var cwd = CreateTempDir("nntpd-cwd-certs-");
        try
        {
            Environment.CurrentDirectory = cwd;
            using var host = CreateHost();
            var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;
            Assert.Equal(Expected("certs/"), options.AcmeStateDir);
            Assert.NotEqual(Path.GetFullPath("certs/"), options.AcmeStateDir);
            Assert.DoesNotContain(Normalized(cwd), options.AcmeStateDir, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TryDelete(cwd);
        }
    }

    [Fact]
    public void Changing_working_directory_does_not_change_resolved_certs_or_logs()
    {
        var previous = Environment.CurrentDirectory;
        var first = CreateTempDir("nntpd-cwd-a-");
        var second = CreateTempDir("nntpd-cwd-b-");
        try
        {
            Environment.CurrentDirectory = first;
            using var hostA = CreateHost();
            var certsA = hostA.Services.GetRequiredService<IOptions<NntpdOptions>>().Value.AcmeStateDir;
            Environment.CurrentDirectory = second;
            using var hostB = CreateHost();
            var certsB = hostB.Services.GetRequiredService<IOptions<NntpdOptions>>().Value.AcmeStateDir;
            Assert.Equal(certsA, certsB);
            Assert.Equal(Expected("certs/"), certsB);
            Assert.Equal(
                NntpdFileLogging.ResolveDirectory("logs/"),
                ApplicationLocalPath.ResolveApplicationLocalPath("logs/", AppContext.BaseDirectory));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TryDelete(first);
            TryDelete(second);
        }
    }

    [Fact]
    public void Explicit_absolute_paths_remain_absolute()
    {
        var absoluteCerts = CreateTempDir("nntpd-abs-certs-");
        var absoluteLogs = CreateTempDir("nntpd-abs-logs-");
        try
        {
            using var host = CreateHost(
                extra:
                [
                    new KeyValuePair<string, string?>($"{NntpdOptions.SectionName}:AcmeStateDir", absoluteCerts),
                    new KeyValuePair<string, string?>($"{NntpdOptions.SectionName}:LogDir", absoluteLogs),
                ]);
            var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;
            Assert.Equal(Expected(absoluteCerts), options.AcmeStateDir);
            Assert.Equal(
                ApplicationLocalPath.ResolveApplicationLocalPath(absoluteLogs, AppContext.BaseDirectory),
                NntpdFileLogging.ResolveDirectory(absoluteLogs));
        }
        finally
        {
            TryDelete(absoluteCerts);
            TryDelete(absoluteLogs);
        }
    }

    [Fact]
    public void Explicit_relative_paths_resolve_beneath_application_base()
    {
        using var host = CreateHost(
            extra: [new KeyValuePair<string, string?>($"{NntpdOptions.SectionName}:AcmeStateDir", "nested-certs/")]);
        var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;
        Assert.Equal(Expected("nested-certs/"), options.AcmeStateDir);
        Assert.Equal(
            Expected("state-logs/"),
            NntpdFileLogging.ResolveDirectory("state-logs/"));
    }

    [Fact]
    public void Nntpd_uses_the_same_common_path_resolution_as_backfiller()
    {
        using var host = CreateHost();
        var options = host.Services.GetRequiredService<IOptions<NntpdOptions>>().Value;
        Assert.Equal(
            AcmeCloudflareOptionsValidator.ResolveAcmeStateDir("certs/", AppContext.BaseDirectory),
            options.AcmeStateDir);
        Assert.Equal(
            ApplicationLocalPath.ResolveApplicationLocalPath("certs/", AppContext.BaseDirectory),
            options.AcmeStateDir);
        Assert.Contains(
            "ApplicationLocalPath.ResolveApplicationLocalPath",
            File.ReadAllText(FindRepoFile(Path.Join("src", "VectorNNTPD.TransitService", "Hosting", "NntpdServiceCollectionExtensions.cs"))),
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplicationLocalPath.ResolveApplicationLocalPath",
            File.ReadAllText(FindRepoFile(Path.Join("src", "VectorNNTPD.TransitService", "Logging", "NntpdFileLogging.cs"))),
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplicationLocalPath.ResolveApplicationLocalPath",
            File.ReadAllText(FindRepoFile(Path.Join("src", "VectorNNTPD.TransitService", "Email", "FilesystemEmailSpool.cs"))),
            StringComparison.Ordinal);
        Assert.Contains(
            "AppContext.BaseDirectory",
            File.ReadAllText(FindRepoFile(Path.Join("src", "VectorNNTPD.TransitService", "Hosting", "NntpdServiceCollectionExtensions.cs"))),
            StringComparison.Ordinal);
        Assert.Contains(
            "AppContext.BaseDirectory",
            File.ReadAllText(FindRepoFile(Path.Join("src", "VectorNNTPD.BackFillerService", "Hosting", "BackFillerServiceCollectionExtensions.cs"))),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Log_directory_creation_uses_application_base_not_working_directory()
    {
        var previous = Environment.CurrentDirectory;
        var cwd = CreateTempDir("nntpd-log-cwd-");
        var baseDir = CreateTempDir("nntpd-log-base-");
        try
        {
            Environment.CurrentDirectory = cwd;
            using var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{NntpdOptions.SectionName}:LogDir"] = "logs/",
                    [$"{NntpdOptions.SectionName}:ApplicationName"] = "VectorNNTPD.TransitService",
                });
            foreach (var pair in NntpdFileLogging.AsyncFileWriteToKeys())
            {
                configuration[pair.Key] = pair.Value;
            }

            NntpdFileLogging.BindResolvedFilePath(configuration, baseDir);

            var expected = ApplicationLocalPath.ResolveApplicationLocalPath("logs/", baseDir);
            Assert.True(Directory.Exists(expected));
            Assert.False(Directory.Exists(Path.Join(cwd, "logs")));
            Assert.Equal(expected, NntpdFileLogging.ResolveDirectory("logs/", baseDir));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TryDelete(cwd);
            TryDelete(baseDir);
        }
    }

    private static string Expected(string configured) =>
        ApplicationLocalPath.ResolveApplicationLocalPath(configured, AppContext.BaseDirectory);

    private static string Normalized(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static IHost CreateHost(
        string? decoyContentRoot = null,
        IEnumerable<KeyValuePair<string, string?>>? extra = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "VectorNNTPD.TransitService.Tests",
            ContentRootPath = decoyContentRoot ?? AppContext.BaseDirectory,
            EnvironmentName = Environments.Development,
        });

        var pairs = new Dictionary<string, string?>
        {
            [$"{NntpdOptions.SectionName}:ServerId"] = "1",
            [$"{NntpdOptions.SectionName}:{NntpdOptions.XTraceKeyConfigurationKey}"] =
                TestHostFactory.TestXTraceKey,
            [$"{NntpdOptions.SectionName}:LogDir"] = TestHostFactory.NewTestLogDir(),
            [$"{NntpdOptions.SectionName}:AcmeStateDir"] = "certs/",
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
        if (extra is not null)
        {
            foreach (var pair in extra)
            {
                pairs[pair.Key] = pair.Value;
            }
        }

        builder.Configuration.AddInMemoryCollection(pairs);
        builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        builder.Services.AddSingleton<ICloudflareDnsClient>(new FakeCloudflareDnsClient());
        builder.Services.AddSingleton<IRedisConnectionFactory, FakeRedisConnectionFactory>();
        builder.Services.AddSingleton<IRabbitMqConnectionFactory, FakeRabbitMqConnectionFactory>();
        builder.Services.AddSingleton<INntpDbConnectionFactory, FakeNntpDbConnectionFactory>();
        TestHostFactory.IsolateTransit(builder.Services);
        builder.ConfigureNntpdLogging(static lc => lc.MinimumLevel.Fatal());
        builder.Services.AddNntpTransitHosting(includePlaceholderService: false);
        return builder.Build();
    }

    private static string CreateTempDir(string prefix) =>
        Directory.CreateTempSubdirectory(prefix).FullName;

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
        }
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

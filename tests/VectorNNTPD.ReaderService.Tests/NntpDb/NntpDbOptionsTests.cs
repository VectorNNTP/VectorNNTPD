using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Hosting;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.ReaderService.Tests.NntpDb;

public sealed class NntpDbOptionsTests
{
    [Fact]
    public void Defaults_MatchProductionValues()
    {
        var options = new NntpDbOptions();
        Assert.Equal(TimeSpan.FromSeconds(15), options.StartupTimeout);
        Assert.Equal("NntpDb", NntpDbOptions.SectionName);
        Assert.Equal("NntpDB", NntpDbOptions.ConnectionStringName);
        Assert.Equal("ConnectionStrings__NntpDB", NntpDbOptions.ConnectionStringEnvironmentVariable);
    }

    [Fact]
    public void Validate_Succeeds_ForDefaultsWithConnectionString()
    {
        var options = new NntpDbOptions { ConnectionString = TestHostFactory.TestNntpDbConnectionString };
        Assert.True(new NntpDbOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Validate_Fails_WhenConnectionStringMissing()
    {
        var result = new NntpDbOptionsValidator().Validate(null, new NntpDbOptions());
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static failure => failure.Contains("ConnectionStrings:NntpDB", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Failures!, static failure => failure.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_Fails_WhenConnectionStringIsMalformed()
    {
        var options = new NntpDbOptions { ConnectionString = TestHostFactory.MalformedNntpDbConnectionString };
        var result = new NntpDbOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static failure => failure.Contains("NntpDB connection string is invalid", StringComparison.Ordinal));
        Assert.Contains(
            result.Failures!,
            static failure => failure.Contains("initialization string", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            result.Failures!,
            static failure => failure.Contains(TestHostFactory.MalformedNntpDbConnectionString, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_MalformedConnectionString_DoesNotExposePassword()
    {
        var options = new NntpDbOptions
        {
            ConnectionString = TestHostFactory.MalformedNntpDbConnectionStringWithPassword,
        };
        var result = new NntpDbOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.DoesNotContain(
            result.Failures!,
            static failure => failure.Contains(TestHostFactory.FakeNntpDbPassword, StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Failures!,
            static failure => failure.Contains("Password=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_Succeeds_WhenConnectionStringIsAcceptedByMySqlConnector()
    {
        var options = new NntpDbOptions { ConnectionString = TestHostFactory.TestNntpDbConnectionString };
        Assert.True(new NntpDbOptionsValidator().Validate(null, options).Succeeded);
        NntpDbConnectionString.Validate(options.ConnectionString);
    }

    [Fact]
    public void Validate_Fails_WhenStartupTimeoutNotPositive()
    {
        var options = new NntpDbOptions
        {
            ConnectionString = TestHostFactory.TestNntpDbConnectionString,
            StartupTimeout = TimeSpan.Zero,
        };
        Assert.True(new NntpDbOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void Bind_ReadsConnectionStringsNntpDB()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:NntpDB"] = TestHostFactory.TestNntpDbConnectionString,
                    ["NntpDb:StartupTimeout"] = "00:00:20",
                })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddNntpDbOptions();
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<NntpDbOptions>>().Value;
        Assert.Equal(TestHostFactory.TestNntpDbConnectionString, options.ConnectionString);
        Assert.Equal(TimeSpan.FromSeconds(20), options.StartupTimeout);
    }

    [Fact]
    public void ProductionAppsettings_DoesNotContainConnectionStrings()
    {
        var path = FindProductionAppsettings();
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.False(document.RootElement.TryGetProperty("ConnectionStrings", out _));
        Assert.False(File.ReadAllText(path).Contains("GrabberDB", StringComparison.Ordinal));

        var nntpDb = document.RootElement.GetProperty("NntpDb");
        Assert.Equal("00:00:15", nntpDb.GetProperty("StartupTimeout").GetString());
        Assert.False(nntpDb.TryGetProperty("ConnectionString", out _));
        Assert.False(nntpDb.TryGetProperty("MinimumPoolSize", out _));
        Assert.False(nntpDb.TryGetProperty("MaximumPoolSize", out _));
        Assert.False(nntpDb.TryGetProperty("MaximumIdleTime", out _));
        Assert.False(nntpDb.TryGetProperty("MaintenanceInterval", out _));
        Assert.False(nntpDb.TryGetProperty("AcquisitionTimeout", out _));
    }

    [Fact]
    public void Same_connection_strings_nntpdb_value_targets_nntpdb_catalog()
    {
        const string connectionString =
            "Server=198.18.0.70;Port=3306;Database=nntpdb;User ID=nntpd;Pooling=true;MinimumPoolSize=2;MaximumPoolSize=32;ConnectionIdleTimeout=300;";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"ConnectionStrings:{NntpDbOptions.ConnectionStringName}"] = connectionString,
                })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddNntpDbOptions();
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<NntpDbOptions>>().Value;
        Assert.Equal(connectionString, options.ConnectionString);
        Assert.Equal("nntpdb", new MySqlConnector.MySqlConnectionStringBuilder(options.ConnectionString).Database);
        Assert.Equal("ConnectionStrings__NntpDB", NntpDbOptions.ConnectionStringEnvironmentVariable);
    }

    [Fact]
    public void Factory_DoesNotOverrideSuppliedPoolingSetting()
    {
        using var disabled = MySqlNntpDbConnectionFactory.CreateConnection(
            "Server=db;Database=nntpdb;User ID=nntpd;Pooling=false;");
        var disabledPooling = new MySqlConnector.MySqlConnectionStringBuilder(disabled.ConnectionString).Pooling;
        Assert.False(disabledPooling);

        using var enabled = MySqlNntpDbConnectionFactory.CreateConnection(
            "Server=db;Database=nntpdb;User ID=nntpd;Pooling=true;");
        var enabledPooling = new MySqlConnector.MySqlConnectionStringBuilder(enabled.ConnectionString).Pooling;
        Assert.True(enabledPooling);
    }

    private static string FindProductionAppsettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Join(dir.FullName, "src", "VectorNNTPD.ReaderService", "VectorNNTPD.ReaderService.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate src/VectorNNTPD.ReaderService/VectorNNTPD.ReaderService.json.");
    }
}

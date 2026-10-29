using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Hosting;
using VectorNNTPD.ReaderService.Logging;

namespace VectorNNTPD.ReaderService.Tests.Hosting;

[Collection(SerilogCollection.Name)]
public sealed class ObsoleteConsoleFormatterCleanupTests
{
    [Fact]
    public void RemoveObsoleteMicrosoftConsoleFormatterConfiguration_StripsConsoleLoggerOptionsBindings()
    {
        var services = new ServiceCollection();
        services.Configure<ConsoleLoggerOptions>(options =>
        {
            options.FormatterName = ConsoleFormatterNames.Systemd;
        });

        Assert.Contains(services, NntpdServiceCollectionExtensions.IsMicrosoftConsoleLoggerOptionsConfiguration);

        NntpdServiceCollectionExtensions.RemoveObsoleteMicrosoftConsoleFormatterConfiguration(services);

        Assert.DoesNotContain(services, NntpdServiceCollectionExtensions.IsMicrosoftConsoleLoggerOptionsConfiguration);
    }

    [Fact]
    public void PlatformHosting_AfterAddSystemd_DoesNotRetainMicrosoftConsoleFormatterConfiguration()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Nntpd:LogDir"] = TestHostFactory.NewTestLogDir(),
            });
        builder.ConfigureNntpdLogging();
        builder.ConfigureNntpdPlatformHosting();

        Assert.DoesNotContain(
            builder.Services,
            NntpdServiceCollectionExtensions.IsMicrosoftConsoleLoggerOptionsConfiguration);

        using var host = builder.Build();

        Assert.Equal("SerilogLoggerFactory", host.Services.GetRequiredService<ILoggerFactory>().GetType().Name);
        Assert.Empty(host.Services.GetLoggerProviders());

        // ConsoleLoggerOptions may still resolve via Options infrastructure defaults, but must not
        // carry the systemd MEL formatter name that AddSystemd would otherwise leave behind.
        var consoleOptions = host.Services.GetService<IOptions<ConsoleLoggerOptions>>();
        Assert.True(
            consoleOptions is null
            || string.IsNullOrEmpty(consoleOptions.Value.FormatterName)
            || !string.Equals(
                consoleOptions.Value.FormatterName,
                ConsoleFormatterNames.Systemd,
                StringComparison.Ordinal));
    }

    [Fact]
    public void PlatformHosting_PreservesSerilogConsoleSinkConfiguration()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Nntpd:LogDir"] = TestHostFactory.NewTestLogDir(),
            });
        builder.ConfigureNntpdLogging();
        builder.ConfigureNntpdPlatformHosting();

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<ObsoleteConsoleFormatterCleanupTests>>();

        // Behavioral check: logging remains functional through Serilog after cleanup.
        logger.LogInformation("Serilog console pipeline remains active after formatter cleanup");
        Assert.Equal("SerilogLoggerFactory", host.Services.GetRequiredService<ILoggerFactory>().GetType().Name);
    }
}

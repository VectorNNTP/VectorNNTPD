using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Logging.Debug;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Extensions.Logging;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Diagnostics;
using VectorNNTPD.ReaderService.Hosting;
using VectorNNTPD.ReaderService.Logging;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Listeners;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Authentication;
using VectorNNTPD.ReaderService.Session.Commands;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.Common.Logging;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.ReaderService.Tests.Logging;

[Collection(SerilogCollection.Name)]
public sealed class LoggingPipelineTests
{
    [Fact]
    public void ApplicationInformationAndDebug_AreEnabled_ForVectorNntpCategory()
    {
        using var host = CreateProductionConfiguredHost();
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        var root = factory.CreateLogger("VectorNNTPD.ReaderService");
        var listener = factory.CreateLogger("VectorNNTPD.ReaderService.Networking.Listeners.NntpPlainListenerService");

        Assert.True(root.IsEnabled(LogLevel.Information));
        Assert.True(root.IsEnabled(LogLevel.Debug));
        Assert.True(listener.IsEnabled(LogLevel.Information));
        Assert.True(listener.IsEnabled(LogLevel.Debug));
    }

    [Fact]
    public void MicrosoftAndSystem_RemainWarning_WhileHostingLifetimeIsInformation()
    {
        using var host = CreateProductionConfiguredHost();
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        var microsoft = factory.CreateLogger("Microsoft.Extensions.Options");
        var system = factory.CreateLogger("System.Net.Http");
        var lifetime = factory.CreateLogger("Microsoft.Hosting.Lifetime");

        Assert.False(microsoft.IsEnabled(LogLevel.Information));
        Assert.True(microsoft.IsEnabled(LogLevel.Warning));
        Assert.False(system.IsEnabled(LogLevel.Information));
        Assert.True(system.IsEnabled(LogLevel.Warning));
        Assert.True(lifetime.IsEnabled(LogLevel.Information));
    }

    [Fact]
    public void SerilogFactory_IsExclusive_NoMicrosoftProviders()
    {
        using var host = CreateProductionConfiguredHost();
        var factories = host.Services.GetServices<ILoggerFactory>().ToArray();
        Assert.Single(factories);
        Assert.Equal("SerilogLoggerFactory", factories[0].GetType().Name);
        var providers = host.Services.GetLoggerProviders();
        Assert.Empty(providers);
        Assert.DoesNotContain(providers, static p => p is ConsoleLoggerProvider);
        Assert.DoesNotContain(providers, static p => p is DebugLoggerProvider);
    }

    [Fact]
    public void ConnectionAcceptance_ReachesSerilogSink()
    {
        var sink = new CollectingSink();
        using var host = CreateProductionConfiguredHost(sink);
        var logger = host.Services.GetRequiredService<ILogger<NntpPlainListenerService>>();

        Assert.True(logger.IsEnabled(LogLevel.Information));
        NetworkingLogMessages.PlainConnectionAccepted(logger, "192.0.2.10:40000");

        var evt = Assert.Single(
            sink.Events,
            e => e.RenderMessage().Contains("Plain connection accepted", StringComparison.Ordinal));
        Assert.Equal(Serilog.Events.LogEventLevel.Information, evt.Level);
        Assert.Equal(
            "VectorNNTPD.ReaderService.Networking.Listeners.NntpPlainListenerService",
            evt.Properties["SourceContext"].ToString().Trim('"'));
        Assert.DoesNotContain("password", evt.RenderMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FeedDiagnosticsSnapshot_ReachesSerilogSink()
    {
        var sink = new CollectingSink();
        using var host = CreateProductionConfiguredHost(sink);
        var logger = host.Services.GetRequiredService<ILogger<FeedDiagnosticsService>>();
        var text = FeedDiagnosticsFormatter.Format(
            new FeedDiagnosticsSnapshot
            {
                CapturedAt = DateTimeOffset.UtcNow,
                Interval = TimeSpan.FromSeconds(10),
                Peers = [],
                Sessions = [],
                IntervalArticles = 12,
                IntervalBytes = 9_000_000,
            },
            includeSessions: false);

        FeedDiagnosticsLogMessages.Snapshot(logger, text);

        var evt = Assert.Single(
            sink.Events,
            e => e.RenderMessage().Contains("FEED interval=", StringComparison.Ordinal));
        Assert.Contains("rate=", evt.RenderMessage(), StringComparison.Ordinal);
        Assert.Equal(
            "VectorNNTPD.ReaderService.Diagnostics.FeedDiagnosticsService",
            evt.Properties["SourceContext"].ToString().Trim('"'));
        Assert.DoesNotContain("@", evt.RenderMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain("password", evt.RenderMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoggingInitialized_ReachesSerilogSink_ThroughILogger()
    {
        var sink = new CollectingSink();
        using var host = CreateProductionConfiguredHost(sink);

        NntpdLoggingExtensions.WriteLoggingInitialized(
            host.Services,
            "Production",
            AppContext.BaseDirectory);

        var evt = Assert.Single(
            sink.Events,
            e => e.RenderMessage().Contains("Application logging initialized", StringComparison.Ordinal));
        Assert.Equal(Serilog.Events.LogEventLevel.Information, evt.Level);
        Assert.Contains("SerilogLoggerFactory", evt.RenderMessage(), StringComparison.Ordinal);
        Assert.Contains(NntpdLogCategories.Hosting, evt.RenderMessage(), StringComparison.Ordinal);
        Assert.True(evt.Properties.TryGetValue("Application", out var application));
        Assert.Equal(
            "\"" + ApplicationJsonConfiguration.EntryAssemblyName + "\"",
            application.ToString());
        Assert.DoesNotContain("not-the-entry-assembly", application.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("CloudFlare", evt.RenderMessage(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", evt.RenderMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfiguredConsoleSink_ReceivesInformationEvent()
    {
        using var captured = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(captured);
        try
        {
            using var host = CreateProductionConfiguredHost();
            var logger = host.Services.GetRequiredService<ILogger<NntpPlainListenerService>>();
            NetworkingLogMessages.PlainConnectionAccepted(logger, "198.51.100.10:119");
        }
        finally
        {
            Console.SetOut(previous);
        }

        var output = captured.ToString();
        Assert.Contains("Plain connection accepted", output, StringComparison.Ordinal);
        Assert.Contains("198.51.100.10:119", output, StringComparison.Ordinal);
        Assert.DoesNotContain("password", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DateAndTakeThis_AreNotHotPathSuppressed()
    {
        Assert.False(NntpCommandLogFormat.SuppressHotPathCommand(NntpVerb.Date));
        Assert.False(NntpCommandLogFormat.SuppressHotPathCommandLog("DATE"));
        Assert.False(NntpCommandLogFormat.SuppressHotPathCommand(NntpVerb.TakeThis));
        Assert.False(NntpCommandLogFormat.SuppressHotPathCommandLog("TAKETHIS <id@ex.com>"));
    }

    [Fact]
    public async Task LivePlainAccept_AndDate_EmitThroughSerilog()
    {
        var sink = new CollectingSink();
        using var serilog = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();
        using var factory = new SerilogLoggerFactory(serilog);

        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["127.0.0.1"];
        options.BindPort = TestHostFactory.GetFreeTcpPort();
        options.BindPortTls = 0;
        options.ProxyHosts = [];

        await using var service = new NntpPlainListenerService(
            Options.Create(options),
            new TrustedProxyHosts(Options.Create(options)),
            new TlsCertificateContextProvider(NullLogger<TlsCertificateContextProvider>.Instance),
            DenyAllNntpAuthenticationProvider.Instance,
            DisabledArticleIngestionQueue.Instance,
            TransitPeerAuthorization.Disabled,
            factory,
            factory.CreateLogger<NntpPlainListenerService>());

        await service.StartAsync(CancellationToken.None);
        var endpoint = service.LocalEndPoints[0];
        using var client = new TcpClient();
        await client.ConnectAsync(endpoint);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true)
        {
            NewLine = "\r\n",
            AutoFlush = true,
        };

        var greeting = await reader.ReadLineAsync();
        Assert.StartsWith("201 ", greeting, StringComparison.Ordinal);

        await writer.WriteLineAsync("DATE");
        var date = await reader.ReadLineAsync();
        Assert.StartsWith("111 ", date, StringComparison.Ordinal);

        await writer.WriteLineAsync("QUIT");
        _ = await reader.ReadLineAsync();
        await service.StopAsync(CancellationToken.None);

        Assert.Contains(
            sink.Events,
            e => e.MessageTemplate.Text.Contains("Plain connection accepted from", StringComparison.Ordinal)
                 && e.Properties.ContainsKey("TcpPeer"));
        Assert.Contains(
            sink.Events,
            e => e.MessageTemplate.Text.Contains("RX:", StringComparison.Ordinal)
                 && e.Properties.TryGetValue("Command", out var rx)
                 && rx.ToString().Contains("DATE", StringComparison.Ordinal));
        Assert.Contains(
            sink.Events,
            e => e.MessageTemplate.Text.Contains("TX:", StringComparison.Ordinal)
                 && e.Properties.TryGetValue("Command", out var tx)
                 && tx.ToString().Contains("DATE", StringComparison.Ordinal));
        Assert.Contains(
            sink.Events,
            e => e.Properties.TryGetValue("SourceContext", out var sessionCtx)
                 && sessionCtx.ToString().Contains("VectorNNTPD.ReaderService.Session.NntpSession", StringComparison.Ordinal));
        Assert.Contains(
            sink.Events,
            e => e.Properties.TryGetValue("SourceContext", out var dateCtx)
                 && dateCtx.ToString().Contains("VectorNNTPD.ReaderService.Session.Commands.Date", StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionAssembly_ReferencesFileSinksAndCommon()
    {
        var names = typeof(NntpdLoggingExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(static a => a.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Serilog.Sinks.Console", names);
        Assert.Contains("Serilog.Sinks.File", names);
        Assert.Contains("VectorNNTPD.Common", names);
        Assert.DoesNotContain("Serilog.Sinks.File.Archive", names);
    }

    [Fact]
    public void ProductionSerilogSection_ConsoleIsInformationOnly()
    {
        var section = ProductionSerilogSection();
        Assert.Equal("Serilog.Sinks.Console", section["Serilog:Using:0"]);
        Assert.Equal("Console", section["Serilog:WriteTo:0:Name"]);
        Assert.Equal("Information", section["Serilog:WriteTo:0:Args:restrictedToMinimumLevel"]);
        Assert.Equal("Debug", section["Serilog:MinimumLevel:Override:VectorNNTPD.ReaderService"]);
        Assert.Equal("Async", section["Serilog:WriteTo:1:Name"]);
        Assert.Equal("50000", section["Serilog:WriteTo:1:Args:bufferSize"]);
        Assert.Equal("true", section["Serilog:WriteTo:1:Args:blockWhenFull"]);
        Assert.Equal("File", section["Serilog:WriteTo:1:Args:configure:0:Name"]);
        Assert.Equal("Debug", section["Serilog:WriteTo:1:Args:configure:0:Args:restrictedToMinimumLevel"]);
        Assert.Equal("Day", section["Serilog:WriteTo:1:Args:configure:0:Args:rollingInterval"]);
        Assert.Equal("14", section["Serilog:WriteTo:1:Args:configure:0:Args:retainedFileCountLimit"]);
        Assert.Null(section["Serilog:WriteTo:1:Args:configure:0:Args:fileSizeLimitBytes"]);
    }

    [Fact]
    public void CommandLoggers_UseHostFactory_NotNullLogger()
    {
        using var host = CreateProductionConfiguredHost();
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        _ = new NntpCommandDispatcher(factory);
        var dateLogger = NntpCommandLoggers.For(typeof(Date));
        Assert.True(dateLogger.IsEnabled(LogLevel.Information));
        Assert.NotEqual(NullLogger.Instance.GetType(), dateLogger.GetType());
        Assert.Equal("SerilogLoggerFactory", factory.GetType().Name);
    }

    [Fact]
    public async Task LivePlainAccept_AndDate_AppearOnConfiguredConsole()
    {
        using var captured = new StringWriter();
        var previous = Console.Out;
        NntpPlainListenerService? service = null;
        Console.SetOut(captured);
        try
        {
            using var host = CreateProductionConfiguredHost();
            var factory = host.Services.GetRequiredService<ILoggerFactory>();
            var logger = host.Services.GetRequiredService<ILogger<NntpPlainListenerService>>();
            var sessionLogger = factory.CreateLogger<NntpSession>();
            var dateLogger = factory.CreateLogger("VectorNNTPD.ReaderService.Session.Commands.Date");

            Assert.Equal("SerilogLoggerFactory", factory.GetType().Name);
            Assert.True(logger.IsEnabled(LogLevel.Information));
            Assert.True(sessionLogger.IsEnabled(LogLevel.Information));
            Assert.True(dateLogger.IsEnabled(LogLevel.Information));

            var options = TestHostFactory.CreateValidOptions();
            options.BindAddress = ["127.0.0.1"];
            options.BindPort = TestHostFactory.GetFreeTcpPort();
            options.BindPortTls = 0;
            options.ProxyHosts = [];

            service = new NntpPlainListenerService(
                Options.Create(options),
                new TrustedProxyHosts(Options.Create(options)),
                new TlsCertificateContextProvider(NullLogger<TlsCertificateContextProvider>.Instance),
                DenyAllNntpAuthenticationProvider.Instance,
                DisabledArticleIngestionQueue.Instance,
                TransitPeerAuthorization.Disabled,
                factory,
                logger);

            await service.StartAsync(CancellationToken.None);
            var endpoint = service.LocalEndPoints[0];
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = true,
            };

            var greeting = await reader.ReadLineAsync();
            Assert.StartsWith("201 ", greeting, StringComparison.Ordinal);
            await writer.WriteLineAsync("DATE");
            var date = await reader.ReadLineAsync();
            Assert.StartsWith("111 ", date, StringComparison.Ordinal);
            await writer.WriteLineAsync("QUIT");
            _ = await reader.ReadLineAsync();
            await service.StopAsync(CancellationToken.None);
            await service.DisposeAsync();
            service = null;
        }
        finally
        {
            Console.SetOut(previous);
            if (service is not null)
            {
                await service.DisposeAsync();
            }
        }

        var output = captured.ToString();
        Assert.Contains("Plain connection accepted", output, StringComparison.Ordinal);
        Assert.DoesNotContain("RX: DATE", output, StringComparison.Ordinal);
        Assert.DoesNotContain("TX: DATE executed in", output, StringComparison.Ordinal);
        Assert.DoesNotContain("TX: DATE [", output, StringComparison.Ordinal);
        Assert.DoesNotContain("TAKETHIS", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", output, StringComparison.OrdinalIgnoreCase);
    }

    private static IHost CreateProductionConfiguredHost(CollectingSink? sink = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        TestHostFactory.ConfigureNntpdTestHost(builder);
        builder.Configuration.AddInMemoryCollection(ProductionSerilogSection());
        builder.ConfigureNntpdLogging(sink is null
            ? null
            : lc =>
            {
                lc.WriteTo.Sink(sink);
            });
        builder.Services.AddNntpReaderHosting(includePlaceholderService: false);
        return builder.Build();
    }

    private static Dictionary<string, string?> ProductionSerilogSection()
    {
        var section = new Dictionary<string, string?>
        {
            ["Serilog:Using:0"] = "Serilog.Sinks.Console",
            ["Serilog:Using:1"] = "Serilog.Sinks.File",
            ["Serilog:Using:2"] = "Serilog.Sinks.Async",
            ["Serilog:MinimumLevel:Default"] = "Information",
            ["Serilog:MinimumLevel:Override:Microsoft"] = "Warning",
            ["Serilog:MinimumLevel:Override:Microsoft.Hosting.Lifetime"] = "Information",
            ["Serilog:MinimumLevel:Override:System"] = "Warning",
            ["Serilog:MinimumLevel:Override:VectorNNTPD.ReaderService"] = "Debug",
            ["Serilog:WriteTo:0:Name"] = "Console",
            ["Serilog:WriteTo:0:Args:restrictedToMinimumLevel"] = "Information",
            ["Serilog:WriteTo:0:Args:outputTemplate"] = NntpdLoggingExtensions.ConsoleOutputTemplate,
            ["Serilog:Enrich:0"] = "FromLogContext",
            ["Serilog:Properties:Application"] = "not-the-entry-assembly",
        };
        foreach (var pair in NntpdFileLogging.AsyncFileWriteToKeys())
        {
            section[pair.Key] = pair.Value;
        }

        return section;
    }
}

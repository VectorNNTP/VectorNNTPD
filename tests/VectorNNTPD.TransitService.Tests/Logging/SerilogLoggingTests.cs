using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Logging.Debug;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.TransitService.Hosting;
using VectorNNTPD.TransitService.Logging;

namespace VectorNNTPD.TransitService.Tests.Logging;

[Collection(SerilogCollection.Name)]
public sealed class SerilogLoggingTests
{
    [Fact]
    public void ConfigureNntpdLogging_RegistersSerilog_AndRemovesDefaultProviders()
    {
        var builder = Host.CreateApplicationBuilder();
        TestHostFactory.ConfigureNntpdTestHost(builder);
        builder.ConfigureNntpdLogging();
        builder.Services.AddNntpTransitHosting(includePlaceholderService: false);
        builder.Services.AddSingleton<IApplicationService>(new FakeApplicationService("svc"));

        using var host = builder.Build();

        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        Assert.Equal("SerilogLoggerFactory", factory.GetType().Name);

        var providers = host.Services.GetLoggerProviders();
        Assert.DoesNotContain(providers, static p => p is ConsoleLoggerProvider);
        Assert.DoesNotContain(providers, static p => p is DebugLoggerProvider);
        Assert.DoesNotContain(
            providers,
            static p => p.GetType().Name.Contains("EventLog", StringComparison.Ordinal));

        // AddSerilog replaces ILoggerFactory rather than stacking MEL providers.
        Assert.Empty(providers);
    }

    [Fact]
    public void ConfigureNntpdLogging_DoesNotRegisterDuplicateSerilogFactories()
    {
        var builder = Host.CreateApplicationBuilder();
        TestHostFactory.ConfigureNntpdTestHost(builder);
        builder.ConfigureNntpdLogging();
        builder.Services.AddNntpTransitHosting(includePlaceholderService: false);

        using var host = builder.Build();
        var factories = host.Services.GetServices<ILoggerFactory>().ToArray();
        Assert.Single(factories);
        Assert.Equal("SerilogLoggerFactory", factories[0].GetType().Name);
    }

    [Fact]
    public void ApplicationILogger_ResolvesThroughSerilog()
    {
        var sink = new CollectingSink();
        using var host = CreateSerilogHost(sink);

        var logger = host.Services.GetRequiredService<ILogger<ApplicationLifecycle>>();
        logger.LogInformation("Structured probe {ProbeId} for {Component}", 42, "lifecycle");

        var evt = Assert.Single(
            sink.Events,
            e => e.MessageTemplate.Text.Contains("Structured probe", StringComparison.Ordinal));
        Assert.Equal(LogEventLevel.Information, evt.Level);
        Assert.True(evt.Properties.TryGetValue("ProbeId", out var probeId));
        Assert.True(evt.Properties.ContainsKey("Component"));
        Assert.Equal("42", probeId.ToString().Trim('"'));
    }

    [Fact]
    public void StructuredLogging_PreservesExceptions()
    {
        var sink = new CollectingSink();
        using var host = CreateSerilogHost(sink);
        var logger = host.Services.GetRequiredService<ILogger<ApplicationLifecycle>>();

        var ex = new InvalidOperationException("boom");
        logger.LogError(ex, "Failure in {Phase}", "test");

        var evt = Assert.Single(sink.Events, e => e.Exception is not null);
        Assert.Same(ex, evt.Exception);
        Assert.True(evt.Properties.ContainsKey("Phase"));
    }

    [Fact]
    public async Task LifecycleEvents_FlowThroughSerilog()
    {
        var sink = new CollectingSink();
        using var host = CreateSerilogHost(sink, services =>
        {
            services.AddSingleton<IApplicationService>(new FakeApplicationService("svc"));
        });

        await host.StartAsync();
        await host.StopAsync();

        Assert.Contains(
            sink.Events,
            e => e.MessageTemplate.Text.Contains("lifecycle state transition", StringComparison.OrdinalIgnoreCase)
                 || e.RenderMessage().Contains("Running", StringComparison.Ordinal));
        Assert.Contains(
            sink.Events,
            e => e.RenderMessage().Contains("Stopped", StringComparison.Ordinal)
                 || e.MessageTemplate.Text.Contains("shutdown", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Logger_RemainsAvailableDuringShutdown_AndHostDisposesCleanly()
    {
        var sink = new CollectingSink();
        var stopLogged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var service = new FakeApplicationService(
            "svc",
            onStop: ct =>
            {
                // Resolve logger during stop to prove it is still usable.
                return Task.CompletedTask;
            });

        using var host = CreateSerilogHost(sink, services =>
        {
            services.AddSingleton<IApplicationService>(service);
        });

        await host.StartAsync();
        var logger = host.Services.GetRequiredService<ILogger<NntpdHostedService>>();
        logger.LogInformation("Pre-stop marker");

        await host.StopAsync();
        logger.LogInformation("Post-stop marker still reachable from resolved instance");

        Assert.Contains(sink.Events, e => e.RenderMessage().Contains("Pre-stop marker", StringComparison.Ordinal));
        Assert.True(service.StopCount >= 1);
        stopLogged.TrySetResult();
    }

    [Fact]
    public async Task StartupFailure_IsLogged_AndDoesNotLeakHost()
    {
        var sink = new CollectingSink();
        using var host = CreateSerilogHost(sink, services =>
        {
            services.AddSingleton<IApplicationService>(
                new FakeApplicationService("bad", onStart: _ => throw new InvalidOperationException("startup-fail")));
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Equal("startup-fail", ex.Message);

        Assert.Contains(
            sink.Events,
            e => e.Exception is InvalidOperationException
                 || e.RenderMessage().Contains("startup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BootstrapLogger_CapturesFatalBeforeHostBuild()
    {
        var previous = Log.Logger;
        try
        {
            var sink = new CollectingSink();
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(sink)
                .CreateLogger();

            var boom = new InvalidOperationException("bootstrap-fail");
            Log.Fatal(boom, "Host construction failed for {ApplicationName}", "VectorNNTPD.TransitService");

            var evt = Assert.Single(sink.Events);
            Assert.Equal(LogEventLevel.Fatal, evt.Level);
            Assert.Same(boom, evt.Exception);
            Assert.True(evt.Properties.ContainsKey("ApplicationName"));
        }
        finally
        {
            Log.CloseAndFlush();
            Log.Logger = previous;
        }
    }

    [Fact]
    public void CloseAndFlush_CanBeCalledExactlyOnceWithoutThrowing()
    {
        var previous = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration()
                .WriteTo.Sink(new CollectingSink())
                .CreateLogger();

            Log.Information("flush-probe");
            Log.CloseAndFlush();
            Log.CloseAndFlush();
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    [Fact]
    public void PlatformHosting_StillRegistersWithSerilogExclusiveLogging()
    {
        var builder = Host.CreateApplicationBuilder();
        TestHostFactory.ConfigureNntpdTestHost(builder);
        builder.ConfigureNntpdLogging();
        builder.ConfigureNntpdPlatformHosting();
        builder.Services.AddNntpTransitHosting(includePlaceholderService: false);

        using var host = builder.Build();
        Assert.Equal("SerilogLoggerFactory", host.Services.GetRequiredService<ILoggerFactory>().GetType().Name);
        Assert.Empty(host.Services.GetLoggerProviders());
        Assert.NotNull(host.Services.GetService<IHostApplicationLifetime>());
    }

    [Fact]
    public async Task HostedServiceStop_EmitsShutdownLogsThroughSerilog()
    {
        var sink = new CollectingSink();
        using var host = CreateSerilogHost(sink, services =>
        {
            services.AddSingleton<IApplicationService>(new FakeApplicationService("svc"));
        });

        await host.StartAsync();
        await host.StopAsync();

        Assert.Contains(
            sink.Events,
            e => e.RenderMessage().Contains("stopping", StringComparison.OrdinalIgnoreCase)
                 || e.RenderMessage().Contains("shutdown", StringComparison.OrdinalIgnoreCase)
                 || e.MessageTemplate.Text.Contains("Host stopping", StringComparison.OrdinalIgnoreCase));
    }

    private static IHost CreateSerilogHost(
        CollectingSink sink,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = Host.CreateApplicationBuilder();
        TestHostFactory.ConfigureNntpdTestHost(builder);
        builder.Configuration[$"{NntpdOptions.SectionName}:ServerId"] = "1";
        builder.Configuration[$"{NntpdOptions.SectionName}:{NntpdOptions.CloudFlareZoneIdConfigurationKey}"] =
            "5811a29d39a0732afb5f160c9b137c3d";
        builder.Configuration[$"{NntpdOptions.SectionName}:DnsSuffix"] = "usenet.ninja";
        builder.ConfigureNntpdLogging(lc =>
        {
            lc.MinimumLevel.Verbose();
            lc.WriteTo.Sink(sink);
        });
        builder.Services.AddNntpTransitHosting(includePlaceholderService: false);
        configureServices?.Invoke(builder.Services);
        return builder.Build();
    }
}

internal sealed class CollectingSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public IReadOnlyCollection<LogEvent> Events => _events.ToArray();

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        _events.Enqueue(logEvent);
    }
}

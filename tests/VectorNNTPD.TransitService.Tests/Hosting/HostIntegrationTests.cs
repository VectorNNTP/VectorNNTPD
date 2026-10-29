using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.TransitService.Hosting;
using VectorNNTPD.TransitService.Hosting.Systemd;
using VectorNNTPD.TransitService.Logging;
using VectorNNTPD.TransitService.Tests.Hosting.Systemd;

namespace VectorNNTPD.TransitService.Tests.Hosting;

[Collection(SerilogCollection.Name)]
public sealed class HostIntegrationTests
{
    [Fact]
    public async Task Host_StartsAndStopsCleanly()
    {
        using var host = CreateTestHost(services =>
        {
            services.AddSingleton<IApplicationService>(new FakeApplicationService("svc"));
        });

        await host.StartAsync();

        var lifecycle = host.Services.GetRequiredService<ApplicationLifecycle>();
        Assert.Equal(ApplicationState.Running, lifecycle.State);

        await host.StopAsync();
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
    }

    [Fact]
    public async Task Host_RepeatedStop_IsIdempotent()
    {
        using var host = CreateTestHost(services =>
        {
            services.AddSingleton<IApplicationService>(new FakeApplicationService("svc"));
        });

        await host.StartAsync();
        await host.StopAsync();
        await host.StopAsync();

        Assert.Equal(ApplicationState.Stopped, host.Services.GetRequiredService<ApplicationLifecycle>().State);
    }

    [Fact]
    public async Task Host_WithSystemdNotifierFakes_ReportsReadyOnlyAfterRunning()
    {
        var notify = new FakeSystemdNotifyBridge();
        var runtime = RecordingSystemdRuntimeFactory.LinuxUnderSystemd();

        using var host = CreateTestHost(services =>
        {
            services.AddSingleton<IApplicationService>(new FakeApplicationService("svc"));
            services.AddSingleton<ISystemdNotifyBridge>(notify);
            services.AddSingleton<ISystemdRuntime>(runtime);
        });

        Assert.Equal(0, notify.ReadyCount);
        await host.StartAsync();
        Assert.Equal(ApplicationState.Running, host.Services.GetRequiredService<ApplicationLifecycle>().State);
        Assert.Equal(1, notify.ReadyCount);

        await host.StopAsync();
        Assert.Equal(1, notify.StoppingCount);
    }

    [Fact]
    public async Task Host_StartupFailure_Propagates_AndLeavesApplicationStopped()
    {
        using var host = CreateTestHost(services =>
        {
            services.AddSingleton<IApplicationService>(
                new FakeApplicationService("bad", onStart: _ => throw new InvalidOperationException("startup-fail")));
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Equal("startup-fail", ex.Message);

        var lifecycle = host.Services.GetRequiredService<ApplicationLifecycle>();
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
    }

    [Fact]
    public async Task Host_StopApplication_TriggersGracefulShutdown()
    {
        using var host = CreateTestHost(services =>
        {
            services.AddSingleton<IApplicationService>(new FakeApplicationService("svc"));
        });

        await host.StartAsync();

        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var lifecycle = host.Services.GetRequiredService<ApplicationLifecycle>();

        lifetime.StopApplication();
        await host.WaitForShutdownAsync();

        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
    }

    [Fact]
    public async Task Host_RespectsGracefulShutdownTimeoutConfiguration()
    {
        var service = new FakeApplicationService("slow")
        {
            HoldStopUntilReleased = true,
        };

        using var host = CreateTestHost(
            services =>
            {
                services.AddSingleton<IApplicationService>(service);
            },
            configure: options =>
            {
                options.GracefulShutdownTimeout = TimeSpan.FromSeconds(1);
            },
            hostShutdownTimeout: TimeSpan.FromSeconds(5));

        await host.StartAsync();

        // Host StopAsync should surface the application shutdown timeout from the service manager.
        await Assert.ThrowsAsync<TimeoutException>(() => host.StopAsync());
        Assert.Equal(ApplicationState.Stopped, host.Services.GetRequiredService<ApplicationLifecycle>().State);
    }

    [Fact]
    public async Task Host_UnexpectedServiceTermination_StopsHost()
    {
        var service = new FakeApplicationService("fragile", withExecution: true);

        using var host = CreateTestHost(services =>
        {
            services.AddSingleton<IApplicationService>(service);
        });

        await host.StartAsync();

        var lifecycle = host.Services.GetRequiredService<ApplicationLifecycle>();
        var shutdown = host.WaitForShutdownAsync();

        service.CompleteExecution(new InvalidOperationException("boom"));

        await lifecycle.UnexpectedTermination.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdownError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => shutdown.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(
            "An application service terminated unexpectedly while the application was Running.",
            shutdownError.Message);

        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
    }

    private static IHost CreateTestHost(
        Action<IServiceCollection>? configureServices = null,
        Action<NntpdOptions>? configure = null,
        TimeSpan? hostShutdownTimeout = null)
    {
        var builder = Host.CreateApplicationBuilder();
        TestHostFactory.ConfigureNntpdTestHost(builder);
        builder.ConfigureNntpdLogging(static lc =>
        {
            // Tests avoid noisy console output; exclusivity is asserted separately.
            lc.MinimumLevel.Fatal();
        });

        builder.Services.Configure<HostOptions>(options =>
        {
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
            if (hostShutdownTimeout is { } timeout)
            {
                options.ShutdownTimeout = timeout;
            }
        });

        builder.Services.AddNntpTransitHosting(configure, includePlaceholderService: false);
        configureServices?.Invoke(builder.Services);

        return builder.Build();
    }
}

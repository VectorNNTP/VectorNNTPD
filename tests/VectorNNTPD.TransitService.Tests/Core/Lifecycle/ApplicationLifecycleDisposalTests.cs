using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.TransitService.Hosting;
using VectorNNTPD.TransitService.Logging;

namespace VectorNNTPD.TransitService.Tests.Core.Lifecycle;

[Collection(SerilogCollection.Name)]
public sealed class ApplicationLifecycleDisposalTests
{
    [Fact]
    public async Task HostStopThenDispose_DoesNotThrowObjectDisposedException()
    {
        using var host = CreateHost(services =>
        {
            services.AddSingleton<IApplicationService>(new FakeApplicationService("svc"));
        });

        await host.StartAsync();
        var lifecycle = host.Services.GetRequiredService<ApplicationLifecycle>();
        Assert.Equal(ApplicationState.Running, lifecycle.State);

        await host.StopAsync();
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);

        // DI disposal previously reproduced ObjectDisposedException from DisposeAsync → StopAsync.
        var dispose = Record.ExceptionAsync(async () =>
        {
            if (host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                host.Dispose();
            }
        });

        Assert.Null(await dispose);
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
    }

    [Fact]
    public async Task StartStopDispose_CompletesWithoutObjectDisposedException()
    {
        var service = new FakeApplicationService("svc");
        var lifecycle = TestHostFactory.CreateLifecycle([service]);

        await lifecycle.StartAsync(CancellationToken.None);
        Assert.Equal(ApplicationState.Running, lifecycle.State);

        await lifecycle.StopAsync(CancellationToken.None);
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);

        var dispose = await Record.ExceptionAsync(async () => await lifecycle.DisposeAsync());
        Assert.Null(dispose);
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        Assert.Equal(1, service.StopCount);
    }

    [Fact]
    public async Task DisposeAsync_AfterNormalStop_IsIdempotent()
    {
        var service = new FakeApplicationService("svc");
        var lifecycle = TestHostFactory.CreateLifecycle([service]);

        await lifecycle.StartAsync(CancellationToken.None);
        await lifecycle.StopAsync(CancellationToken.None);
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);

        await lifecycle.DisposeAsync();
        await lifecycle.DisposeAsync();

        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        Assert.Equal(1, service.StopCount);
    }

    [Fact]
    public async Task DisposeAsync_WhileRunning_StopsServicesOnce()
    {
        var service = new FakeApplicationService("svc");
        var lifecycle = TestHostFactory.CreateLifecycle([service]);

        await lifecycle.StartAsync(CancellationToken.None);
        Assert.Equal(ApplicationState.Running, lifecycle.State);

        await lifecycle.DisposeAsync();

        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        Assert.Equal(1, service.StopCount);
    }

    [Fact]
    public async Task DisposeAsync_SurfacesShutdownFailure_WhenDisposeTriggersStop()
    {
        var stopAttempts = 0;
        var service = new FakeApplicationService(
            "bad",
            onStop: _ =>
            {
                Interlocked.Increment(ref stopAttempts);
                throw new InvalidOperationException("dispose-stop-fail");
            });
        var lifecycle = TestHostFactory.CreateLifecycle([service]);

        await lifecycle.StartAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AggregateException>(
            async () => await lifecycle.DisposeAsync());
        Assert.Contains(ex.InnerExceptions, e => e.Message == "dispose-stop-fail");
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        Assert.Equal(1, stopAttempts);

        // Subsequent dispose is idempotent and does not rethrow or re-stop.
        await lifecycle.DisposeAsync();
        Assert.Equal(1, stopAttempts);
    }

    [Fact]
    public async Task ConcurrentStopRequests_ShareSingleShutdown()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeApplicationService(
            "svc",
            onStop: async ct =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            });

        var lifecycle = TestHostFactory.CreateLifecycle(
            [service],
            TestHostFactory.CreateOptions(gracefulShutdownTimeout: TimeSpan.FromMinutes(1)));

        await lifecycle.StartAsync(CancellationToken.None);

        var stop1 = lifecycle.StopAsync(CancellationToken.None);
        await entered.Task;
        var stop2 = lifecycle.StopAsync(CancellationToken.None);
        var stop3 = lifecycle.StopAsync(CancellationToken.None);

        release.TrySetResult();
        await Task.WhenAll(stop1, stop2, stop3);

        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        Assert.Equal(1, service.StopCount);

        await lifecycle.DisposeAsync();
        Assert.Equal(1, service.StopCount);
    }

    [Fact]
    public async Task StopAsync_AfterDispose_WhenAlreadyStopped_DoesNotThrow()
    {
        var lifecycle = TestHostFactory.CreateLifecycle([new FakeApplicationService("svc")]);
        await lifecycle.StartAsync(CancellationToken.None);
        await lifecycle.StopAsync(CancellationToken.None);
        await lifecycle.DisposeAsync();

        await lifecycle.StopAsync(CancellationToken.None);
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
    }

    [Fact]
    public async Task ConcurrentStopAndDispose_DoNotThrowOrDoubleStopServices()
    {
        var service = new FakeApplicationService("svc");
        var lifecycle = TestHostFactory.CreateLifecycle([service]);
        await lifecycle.StartAsync(CancellationToken.None);

        var stop = lifecycle.StopAsync(CancellationToken.None);
        var dispose = lifecycle.DisposeAsync().AsTask();

        await Task.WhenAll(stop, dispose);

        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        Assert.Equal(1, service.StopCount);

        await lifecycle.DisposeAsync();
        Assert.Equal(1, service.StopCount);
    }

    [Fact]
    public async Task ConcurrentDisposeRequests_AreSafe()
    {
        var service = new FakeApplicationService("svc");
        var lifecycle = TestHostFactory.CreateLifecycle([service]);
        await lifecycle.StartAsync(CancellationToken.None);

        var d1 = lifecycle.DisposeAsync().AsTask();
        var d2 = lifecycle.DisposeAsync().AsTask();
        var d3 = lifecycle.DisposeAsync().AsTask();

        await Task.WhenAll(d1, d2, d3);

        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        Assert.Equal(1, service.StopCount);
    }

    [Fact]
    public async Task ShutdownFailure_RemainsObservable_OnStopAsync()
    {
        var service = new FakeApplicationService(
            "bad",
            onStop: _ => throw new InvalidOperationException("stop-fail"));
        await using var lifecycle = TestHostFactory.CreateLifecycle([service]);

        await lifecycle.StartAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => lifecycle.StopAsync(CancellationToken.None));
        Assert.Contains(ex.InnerExceptions, e => e.Message == "stop-fail");
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
    }

    [Fact]
    public async Task StartupFailure_FollowedByDispose_Succeeds()
    {
        var service = new FakeApplicationService(
            "bad",
            onStart: _ => throw new InvalidOperationException("start-fail"));
        var lifecycle = TestHostFactory.CreateLifecycle([service]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifecycle.StartAsync(CancellationToken.None));
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);

        await lifecycle.DisposeAsync();
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
    }

    [Fact]
    public async Task ShutdownCancellation_Propagates()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeApplicationService(
            "slow",
            onStop: async ct =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            });

        await using var lifecycle = TestHostFactory.CreateLifecycle(
            [service],
            TestHostFactory.CreateOptions(gracefulShutdownTimeout: TimeSpan.FromMinutes(1)));

        await lifecycle.StartAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var stop = lifecycle.StopAsync(cts.Token);
        await entered.Task;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
    }

    [Fact]
    public void HostWithSerilog_DoesNotRegisterMicrosoftConsoleProvider()
    {
        using var host = CreateHost(services =>
        {
            services.AddSingleton<IApplicationService>(new FakeApplicationService("svc"));
        });

        Assert.Equal("SerilogLoggerFactory", host.Services.GetRequiredService<ILoggerFactory>().GetType().Name);
        Assert.DoesNotContain(host.Services.GetLoggerProviders(), static p => p is ConsoleLoggerProvider);
        Assert.Empty(host.Services.GetLoggerProviders());
    }

    private static IHost CreateHost(Action<IServiceCollection>? configureServices = null)
    {
        var builder = Host.CreateApplicationBuilder();
        TestHostFactory.ConfigureNntpdTestHost(builder);
        builder.ConfigureNntpdLogging(static lc => lc.MinimumLevel.Fatal());
        builder.ConfigureNntpdPlatformHosting();
        builder.Services.AddNntpTransitHosting(includePlaceholderService: false);
        configureServices?.Invoke(builder.Services);
        return builder.Build();
    }
}

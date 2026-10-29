using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.TransitService.RabbitMq;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.TestDoubles;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.TransitService.Tests.RabbitMq;

public sealed class RabbitMqServiceConcurrencyTests
{
    [Fact]
    public async Task SimultaneousLossSignals_StartOneReconnect()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        var first = factory.LastConnection!;
        var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Connected = secondConnected;

        Parallel.For(0, 8, _ => first.SimulateLost());

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await secondConnected.Task.WaitAsync(safety.Token);

        // Eight signals coalesce onto one watch loop. A loss observed during the
        // reconnect delay can schedule one follow-up replace after N+1 is installed.
        Assert.InRange(factory.ConnectCount, 2, 3);
        Assert.True(service.ConnectionGeneration >= 2);
        Assert.True(service.IsReady);
        Assert.False(service.Execution!.IsCompleted);

        await service.DisposeAsync();
    }

    [Fact]
    public async Task StaleConnectionLost_DoesNotAffectCurrentGeneration()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        var first = factory.LastConnection!;
        var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Connected = secondConnected;
        first.SimulateLost();

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await secondConnected.Task.WaitAsync(safety.Token);
        var current = factory.LastConnection!;

        var extra = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Connected = extra;
        first.SimulateLost();

        var extraConnect = extra.Task.WaitAsync(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<TimeoutException>(() => extraConnect);

        Assert.Equal(2, factory.ConnectCount);
        Assert.Equal(2, service.ConnectionGeneration);
        Assert.True(service.TryGetCurrent(out var handle));
        Assert.Same(current, handle.Connection);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, current.DisposeCount);

        await service.DisposeAsync();
        Assert.Equal(1, current.DisposeCount);
    }

    [Fact]
    public async Task StaleConnectionDispose_DoesNotDisposeCurrent()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        var first = factory.LastConnection!;
        var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Connected = secondConnected;
        first.SimulateLost();

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await secondConnected.Task.WaitAsync(safety.Token);
        var current = factory.LastConnection!;

        await first.DisposeAsync();

        Assert.Equal(2, first.DisposeCount);
        Assert.Equal(0, current.DisposeCount);
        Assert.True(service.IsReady);
        Assert.True(service.TryGetCurrent(out var handle));
        Assert.Same(current, handle.Connection);

        await service.DisposeAsync();
        Assert.Equal(1, current.DisposeCount);
    }

    [Fact]
    public async Task ShutdownDuringReconnect_DoesNotInstallAReplacement()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        var first = factory.LastConnection!;
        var connectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.ConnectStarted = connectStarted;
        factory.BlockConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        first.SimulateLost();
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await connectStarted.Task.WaitAsync(safety.Token);

        var stop = service.StopAsync(CancellationToken.None);
        factory.BlockConnect.TrySetCanceled();
        await stop;

        Assert.Equal(1, service.ConnectionCount);
        Assert.Equal(1, service.ConnectionGeneration);
        Assert.False(service.IsReady);
        Assert.False(service.TryGetCurrent(out _));
        foreach (var connection in factory.Connections)
        {
            Assert.Equal(1, connection.DisposeCount);
        }
    }

    [Fact]
    public async Task ConcurrentStopAndLoss_DisposesCurrentOnce()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        var first = factory.LastConnection!;

        var stop = service.StopAsync(CancellationToken.None);
        first.SimulateLost();
        await service.DisposeAsync();
        await stop;

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, factory.ConnectCount);
        Assert.False(service.IsReady);
    }

    [Fact]
    public async Task CapturedHandle_IsNotCurrentAfterLoss_BeforeConnectionIsDisposed()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var options = RabbitMqOptionsTests.CreateValid();
        options.PoolReconnectBaseDelayMs = 30000;
        options.PoolReconnectMaxDelayMs = 30000;
        var service = new RabbitMqService(
            factory,
            Options.Create(options),
            new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTPD.TransitService:nntpd01.usenet.ninja"),
            NullLogger<RabbitMqService>.Instance);

        await service.StartAsync(CancellationToken.None);
        Assert.True(service.TryGetCurrent(out var handle));
        var first = factory.LastConnection!;
        Assert.True(handle.IsCurrent);

        first.SimulateLost();

        Assert.False(handle.IsCurrent);
        Assert.False(handle.IsOpen);
        Assert.Equal(1, handle.Generation);
        Assert.Same(first, handle.Connection);
        Assert.Equal(0, first.DisposeCount);
        Assert.False(service.TryGetCurrent(out _));

        await service.DisposeAsync();
        Assert.Equal(1, first.DisposeCount);
    }

    [Fact]
    public async Task HoldingAHandle_DoesNotPreventDisposeOfThatGeneration()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        Assert.True(service.TryGetCurrent(out var generationOne));
        var first = factory.LastConnection!;
        var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Connected = secondConnected;

        first.SimulateLost();
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await secondConnected.Task.WaitAsync(safety.Token);

        Assert.False(generationOne.IsCurrent);
        Assert.Equal(1, generationOne.Generation);
        Assert.Same(first, generationOne.Connection);
        Assert.Equal(1, first.DisposeCount);
        Assert.True(service.TryGetCurrent(out var generationTwo));
        Assert.Equal(2, generationTwo.Generation);
        Assert.True(generationTwo.IsCurrent);
        Assert.NotSame(generationOne.Connection, generationTwo.Connection);

        await service.DisposeAsync();
    }

    [Fact]
    public async Task DisposeOfCapturedGeneration_CanOverlapCallerStillHoldingTheHandle()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        Assert.True(service.TryGetCurrent(out var handle));
        var first = factory.LastConnection!;
        var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.DisposeStarted = disposeStarted;
        first.BlockDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Connected = secondConnected;

        first.SimulateLost();
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await disposeStarted.Task.WaitAsync(safety.Token);

        Assert.False(handle.IsCurrent);
        Assert.Same(first, handle.Connection);
        Assert.Equal(0, first.DisposeCount);
        Assert.False(service.TryGetCurrent(out _));

        first.BlockDispose.TrySetResult();
        await secondConnected.Task.WaitAsync(safety.Token);

        Assert.Equal(1, first.DisposeCount);
        Assert.False(handle.IsCurrent);
        Assert.True(service.TryGetCurrent(out var next));
        Assert.Equal(2, next.Generation);

        await service.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentTryGetCurrent_DuringReplacement_NeverReturnsADisposedCurrentHandle()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        var first = factory.LastConnection!;
        var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Connected = secondConnected;

        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new List<RabbitMqConnectionHandle>();
        var sampler = Task.Run(async () =>
        {
            while (!stop.Task.IsCompleted)
            {
                if (service.TryGetCurrent(out var handle))
                {
                    lock (observed)
                    {
                        observed.Add(handle);
                    }
                }

                await Task.Yield();
            }
        });

        first.SimulateLost();
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await secondConnected.Task.WaitAsync(safety.Token);
        stop.TrySetResult();
        await sampler.WaitAsync(safety.Token);

        Assert.NotEmpty(observed);
        Assert.True(service.TryGetCurrent(out var current));
        Assert.Equal(2, current.Generation);
        Assert.Equal(1, first.DisposeCount);
        lock (observed)
        {
            foreach (var handle in observed)
            {
                if (!handle.IsCurrent)
                {
                    continue;
                }

                Assert.Equal(service.ConnectionGeneration, handle.Generation);
                Assert.Equal(0, ((FakeRabbitMqConnection)handle.Connection).DisposeCount);
            }
        }

        await service.DisposeAsync();
    }

    [Fact]
    public async Task RepeatedLifecycleCalls_AreIdempotent()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        var connection = factory.LastConnection!;

        await Task.WhenAll(
            service.StopAsync(CancellationToken.None),
            service.StopAsync(CancellationToken.None),
            service.DisposeAsync().AsTask(),
            service.DisposeAsync().AsTask());

        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(1, connection.DisposeCount);
        Assert.False(service.IsReady);
    }

    private static RabbitMqService CreateService(FakeRabbitMqConnectionFactory factory)
    {
        var options = RabbitMqOptionsTests.CreateValid();
        options.PoolReconnectBaseDelayMs = 50;
        options.PoolReconnectMaxDelayMs = 50;
        return new RabbitMqService(
            factory,
            Options.Create(options),
            new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTPD.TransitService:nntpd01.usenet.ninja"),
            NullLogger<RabbitMqService>.Instance);
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.RabbitMq;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.ReaderService.Tests.RabbitMq;

public sealed class RabbitMqServiceTests
{
    [Fact]
    public void ConnectionHandle_DoesNotOwnTheConnection()
    {
        Assert.DoesNotContain(typeof(IDisposable), typeof(RabbitMqConnectionHandle).GetInterfaces());
        Assert.DoesNotContain(typeof(IAsyncDisposable), typeof(RabbitMqConnectionHandle).GetInterfaces());
    }

    [Fact]
    public async Task StartAsync_ConnectsOnce_AndIsReady()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var replaced = new List<RabbitMqConnectionReplacedEventArgs>();
        var service = CreateService(factory);
        service.ConnectionReplaced += (_, args) => replaced.Add(args);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(1, service.ConnectionCount);
        Assert.Equal(1, service.ConnectionGeneration);
        Assert.True(service.IsReady);
        Assert.NotNull(service.Execution);
        Assert.True(service.TryGetCurrent(out var handle));
        Assert.True(handle.IsCurrent);
        Assert.Equal(1, handle.Generation);
        Assert.Same(factory.LastConnection, handle.Connection);
        var generation = Assert.Single(replaced);
        Assert.Equal(1, generation.ConnectionGeneration);
        Assert.False(generation.IsReplacement);
        Assert.Equal("VectorNNTPD.ReaderService:nntpd01.usenet.ninja", factory.LastConnection!.ClientProvidedName);

        await service.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_Fails_WhenConnectThrows()
    {
        var factory = new FakeRabbitMqConnectionFactory
        {
            ConnectException = new InvalidOperationException("broker down"),
        };
        var service = CreateService(factory);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.Equal("broker down", ex.Message);
        Assert.Equal(0, service.ConnectionCount);
        Assert.Equal(0, service.ConnectionGeneration);
        Assert.False(service.IsReady);
        Assert.Null(service.Execution);
        Assert.False(service.TryGetCurrent(out _));
    }

    [Fact]
    public async Task StartAsync_Fails_WhenConnectionIsNotUsable()
    {
        var factory = new FakeRabbitMqConnectionFactory { ReturnUnusableConnection = true };
        var service = CreateService(factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.Equal(0, service.ConnectionCount);
        Assert.False(service.IsReady);
        Assert.Equal(1, factory.LastConnection!.DisposeCount);
    }

    [Fact]
    public async Task StartAsync_Cancelled_DoesNotLeaveConnection()
    {
        var factory = new FakeRabbitMqConnectionFactory
        {
            BlockConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var service = CreateService(factory);
        using var cts = new CancellationTokenSource();

        var start = service.StartAsync(cts.Token);
        cts.Cancel();
        factory.BlockConnect.TrySetCanceled(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(0, service.ConnectionCount);
        Assert.False(service.IsReady);
        Assert.Null(service.Execution);
    }

    [Fact]
    public async Task StartAsync_CanRetry_AfterFailedConnect()
    {
        var factory = new FakeRabbitMqConnectionFactory
        {
            ConnectException = new InvalidOperationException("broker down"),
        };
        var service = CreateService(factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        factory.ConnectException = null;

        await service.StartAsync(CancellationToken.None);
        Assert.True(service.IsReady);
        Assert.Equal(1, service.ConnectionGeneration);

        await service.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_IsIdempotent()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, factory.ConnectCount);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task ConnectionLost_ReplacesConnection_AndIncrementsGeneration()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var replaced = new List<RabbitMqConnectionReplacedEventArgs>();
        var service = CreateService(factory);
        service.ConnectionReplaced += (_, args) => replaced.Add(args);

        await service.StartAsync(CancellationToken.None);
        var first = factory.LastConnection!;
        var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Connected = secondConnected;

        first.SimulateLost();

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var count = await secondConnected.Task.WaitAsync(safety.Token);

        Assert.Equal(2, count);
        Assert.Equal(2, service.ConnectionCount);
        Assert.Equal(2, service.ConnectionGeneration);
        Assert.True(service.IsReady);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(2, replaced.Count);
        Assert.True(replaced[1].IsReplacement);
        Assert.Equal(2, replaced[1].ConnectionGeneration);
        Assert.True(service.TryGetCurrent(out var current));
        Assert.Same(factory.LastConnection, current.Connection);

        await service.DisposeAsync();
    }

    [Fact]
    public async Task ConnectionLost_TryGetCurrent_FailsUntilReplaced()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        Assert.True(service.TryGetCurrent(out var before));
        var first = factory.LastConnection!;
        var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Connected = secondConnected;

        first.SimulateLost();
        Assert.False(service.IsReady);
        Assert.False(service.TryGetCurrent(out _));
        Assert.False(before.IsCurrent);

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await secondConnected.Task.WaitAsync(safety.Token);
        Assert.True(service.IsReady);
        Assert.True(service.TryGetCurrent(out var after));
        Assert.Equal(2, after.Generation);
        Assert.True(after.IsCurrent);
        Assert.False(before.IsCurrent);

        await service.DisposeAsync();
        Assert.False(after.IsCurrent);
    }

    [Fact]
    public async Task Reconnect_FailsThenSucceeds()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        var first = factory.LastConnection!;
        factory.RemainingConnectFailures = 2;
        // Connected fires when the factory returns a connection; generation is published after that.
        // Wait on ConnectionReplaced so the assert observes the installed generation.
        var recovered = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ConnectionReplaced += (_, args) =>
        {
            if (args.IsReplacement)
            {
                recovered.TrySetResult(args.ConnectionGeneration);
            }
        };

        first.SimulateLost();

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var generation = await recovered.Task.WaitAsync(safety.Token);

        Assert.Equal(2, generation);
        Assert.Equal(2, service.ConnectionGeneration);
        Assert.True(service.IsReady);
        Assert.Equal(1 + 2 + 1, factory.AttemptCount);
        Assert.Equal(2, factory.ConnectCount);

        await service.DisposeAsync();
    }

    [Fact]
    public async Task Reconnect_Continues_BeyondFormerAbandonThreshold()
    {
        const int formerAbandonThreshold = 5;
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        var failures = formerAbandonThreshold + 3;
        factory.RemainingConnectFailures = failures;
        // Connected fires when the factory returns a connection; generation is published after that.
        // Wait on ConnectionReplaced so the assert observes the installed generation.
        var recovered = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ConnectionReplaced += (_, args) =>
        {
            if (args.IsReplacement)
            {
                recovered.TrySetResult(args.ConnectionGeneration);
            }
        };
        factory.LastConnection!.SimulateLost();

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await factory.WaitForAttemptsAsync(1 + formerAbandonThreshold + 1, safety.Token);
        Assert.False(service.IsReady);
        Assert.Equal(1, service.ConnectionGeneration);
        Assert.False(service.Execution!.IsCompleted);

        var generation = await recovered.Task.WaitAsync(safety.Token);

        Assert.Equal(2, generation);
        Assert.Equal(2, service.ConnectionGeneration);
        Assert.True(service.IsReady);
        Assert.Equal(1 + failures + 1, factory.AttemptCount);
        Assert.False(service.Execution!.IsCompleted);

        await service.DisposeAsync();
        Assert.True(service.Execution.IsCompleted);
    }

    [Fact]
    public async Task Reconnect_KeepsTrying_UntilShutdown()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        factory.RemainingConnectFailures = 100;
        factory.LastConnection!.SimulateLost();

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await factory.WaitForAttemptsAsync(8, safety.Token);
        Assert.False(service.IsReady);
        Assert.False(service.Execution!.IsCompleted);

        await service.StopAsync(CancellationToken.None);

        var attempts = factory.AttemptCount;
        Assert.True(service.Execution.IsCompleted);
        Assert.False(service.TryGetCurrent(out _));

        using var noMore = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => factory.WaitForAttemptsAsync(attempts + 1, noMore.Token));
        Assert.Equal(attempts, factory.AttemptCount);
    }

    [Fact]
    public async Task SuccessfulReconnect_ResetsBackoffOnNextLoss()
    {
        var clock = new RecordingTimeProvider();
        var factory = new FakeRabbitMqConnectionFactory();
        var options = RabbitMqOptionsTests.CreateValid();
        options.PoolReconnectBaseDelayMs = 50;
        options.PoolReconnectMaxDelayMs = 800;
        var service = new RabbitMqService(
            factory,
            Options.Create(options),
            new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTPD.ReaderService:nntpd01.usenet.ninja"),
            NullLogger<RabbitMqService>.Instance,
            clock);

        await service.StartAsync(CancellationToken.None);
        factory.RemainingConnectFailures = 2;
        var firstRecovery = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Connected = firstRecovery;
        factory.LastConnection!.SimulateLost();

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await firstRecovery.Task.WaitAsync(safety.Token);
        var afterFirstRecovery = clock.Delays.ToArray();
        Assert.Equal(
            [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)],
            afterFirstRecovery);

        var secondRecovery = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Connected = secondRecovery;
        factory.LastConnection!.SimulateLost();
        await secondRecovery.Task.WaitAsync(safety.Token);

        Assert.Equal(3, service.ConnectionGeneration);
        Assert.Equal(TimeSpan.FromMilliseconds(50), clock.Delays[^1]);

        await service.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_InterruptsRecoveryDelay()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var options = RabbitMqOptionsTests.CreateValid();
        options.PoolReconnectBaseDelayMs = 30000;
        options.PoolReconnectMaxDelayMs = 30000;
        var service = new RabbitMqService(
            factory,
            Options.Create(options),
            new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTPD.ReaderService:nntpd01.usenet.ninja"),
            NullLogger<RabbitMqService>.Instance);

        await service.StartAsync(CancellationToken.None);
        factory.LastConnection!.SimulateLost();

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, factory.AttemptCount);
        Assert.True(service.Execution!.IsCompleted);
        Assert.False(service.TryGetCurrent(out _));
    }

    [Fact]
    public async Task StopAsync_DisposesConnectionOnce()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        var connection = factory.LastConnection!;

        await service.StopAsync(CancellationToken.None);
        await service.DisposeAsync();

        Assert.Equal(1, connection.DisposeCount);
        Assert.False(service.IsReady);
        Assert.True(service.Execution is { IsCompleted: true });
    }

    [Fact]
    public async Task DisposeAsync_IsSafe_WhenNeverStarted()
    {
        var service = CreateService(new FakeRabbitMqConnectionFactory());
        await service.DisposeAsync();
        await service.DisposeAsync();
        Assert.False(service.IsReady);
        Assert.Equal(0, service.ConnectionGeneration);
    }

    [Fact]
    public async Task StopAsync_WhileReconnecting_CancelsAndDisposes()
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

        await service.StopAsync(CancellationToken.None);
        factory.BlockConnect.TrySetCanceled();

        Assert.Equal(1, first.DisposeCount);
        Assert.False(service.IsReady);
        Assert.Equal(1, factory.ConnectCount);
        foreach (var connection in factory.Connections)
        {
            Assert.Equal(1, connection.DisposeCount);
        }
    }

    [Fact]
    public async Task StopAsync_DuringStartupConnect_LeavesNoConnection()
    {
        var factory = new FakeRabbitMqConnectionFactory
        {
            ConnectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            BlockConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var service = CreateService(factory);
        using var cts = new CancellationTokenSource();
        var start = service.StartAsync(cts.Token);

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await factory.ConnectStarted.Task.WaitAsync(safety.Token);
        cts.Cancel();
        factory.BlockConnect.TrySetCanceled(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(0, service.ConnectionCount);
        Assert.False(service.IsReady);
    }

    [Fact]
    public async Task StopAsync_DoesNotReconnect()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        var first = factory.LastConnection!;
        await service.StopAsync(CancellationToken.None);

        first.SimulateLost();

        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(1, factory.AttemptCount);
        Assert.False(service.IsReady);
    }

    [Fact]
    public async Task Logs_DoNotContainPassword()
    {
        const string secret = "unit-test-rabbitmq-password-not-real";
        var logger = new CollectingLogger<RabbitMqService>();
        var options = RabbitMqOptionsTests.CreateValid();
        options.Username = "nntparticles";
        options.Password = secret;
        options.PoolReconnectBaseDelayMs = 50;
        options.PoolReconnectMaxDelayMs = 50;

        var service = new RabbitMqService(
            new FakeRabbitMqConnectionFactory(),
            Options.Create(options),
            new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTPD.ReaderService:nntpd01.usenet.ninja"),
            logger);

        await service.StartAsync(CancellationToken.None);
        await service.DisposeAsync();

        Assert.NotEmpty(logger.Messages);
        Assert.Contains(logger.Messages, static message => message.Contains("Connecting to RabbitMQ", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, static message => message.Contains("RabbitMQ connection established", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, static message => message.Contains("RabbitMQ stopped", StringComparison.Ordinal));
        foreach (var message in logger.Messages)
        {
            Assert.DoesNotContain(secret, message, StringComparison.Ordinal);
            Assert.DoesNotContain("Password=", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("nntparticles:", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CreateClientFactory_DisablesClientAutomaticRecovery()
    {
        var runtime = RabbitMqOptionsTests.CreateValid().ToRuntimeOptions();
        var factory = RabbitMqClientConnectionFactory.CreateClientFactory(
            runtime,
            "VectorNNTPD.ReaderService:nntpd01.usenet.ninja");

        Assert.False(factory.AutomaticRecoveryEnabled);
        Assert.False(factory.TopologyRecoveryEnabled);
        Assert.Equal(runtime.Port, factory.Port);
        Assert.Equal(runtime.VirtualHost, factory.VirtualHost);
        Assert.Equal((ushort)runtime.RequestedChannelMax, factory.RequestedChannelMax);
    }

    [Fact]
    public void CreateClientFactory_AppliesCredentialsWithoutExposingThemOnTheName()
    {
        var options = RabbitMqOptionsTests.CreateValid();
        options.Username = "nntparticles";
        options.Password = "unit-test-rabbitmq-password-not-real";
        var runtime = options.ToRuntimeOptions();
        const string name = "VectorNNTPD.ReaderService:nntpd01.usenet.ninja";

        var factory = RabbitMqClientConnectionFactory.CreateClientFactory(runtime, name);

        Assert.Equal("nntparticles", factory.UserName);
        Assert.Equal("unit-test-rabbitmq-password-not-real", factory.Password);
        Assert.Equal(name, factory.ClientProvidedName);
        Assert.DoesNotContain(runtime.Password!, factory.ClientProvidedName, StringComparison.Ordinal);
    }

    private static RabbitMqService CreateService(
        FakeRabbitMqConnectionFactory factory,
        int poolReconnectBaseDelayMs = 50)
    {
        var options = RabbitMqOptionsTests.CreateValid();
        options.PoolReconnectBaseDelayMs = poolReconnectBaseDelayMs;
        options.PoolReconnectMaxDelayMs = poolReconnectBaseDelayMs;
        return new RabbitMqService(
            factory,
            Options.Create(options),
            new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTPD.ReaderService:nntpd01.usenet.ninja"),
            NullLogger<RabbitMqService>.Instance);
    }

    private sealed class RecordingTimeProvider : TimeProvider
    {
        private readonly TimeProvider _inner = TimeProvider.System;

        public List<TimeSpan> Delays { get; } = [];

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
            {
                Delays.Add(dueTime);
            }

            return _inner.CreateTimer(callback, state, dueTime, period);
        }
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}

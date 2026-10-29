using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.TransitService.NntpDb;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.TestDoubles;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.TransitService.Tests.NntpDb;

public sealed class NntpDbServiceTests
{
    [Fact]
    public async Task StartAsync_OpensConnection_SelectsOne_AndDisposesLogicalConnection()
    {
        var factory = new FakeNntpDbConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);

        Assert.True(service.HasStarted);
        Assert.True(service.IsAccepting);
        Assert.Equal(1, service.StartupConnectAttempts);
        Assert.Equal(1, factory.OpenCount);
        var connection = Assert.Single(factory.Connections);
        Assert.Equal(1, connection.SelectOneCount);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task StartAsync_Fails_WhenMySqlIsUnavailable()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            OpenException = new NntpDbUnavailableException("down"),
        };
        var service = CreateService(factory, startupTimeout: TimeSpan.FromMilliseconds(1));

        var ex = await Assert.ThrowsAsync<NntpDbUnavailableException>(() => service.StartAsync(CancellationToken.None));
        Assert.Contains("startup", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(service.HasStarted);
        Assert.False(service.IsAccepting);
    }

    [Fact]
    public async Task StartAsync_MalformedConnectionString_FailsImmediately_WithoutRetry()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            OpenException = new NntpDbUnavailableException("should not be reached"),
        };
        var clock = new ControllableTimeProvider();
        var logger = new CollectingLogger<NntpDbService>();
        var service = CreateService(
            factory,
            startupTimeout: TimeSpan.FromMinutes(1),
            clock: clock,
            connectionString: TestHostFactory.MalformedNntpDbConnectionString,
            logger: logger);

        var starting = service.StartAsync(CancellationToken.None);
        var ex = await Assert.ThrowsAsync<NntpDbConfigurationException>(
            () => starting.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Contains("NntpDB connection string is invalid", ex.Message, StringComparison.Ordinal);
        Assert.Contains("initialization string", ex.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("index", ex.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<ArgumentException>(ex.InnerException);
        Assert.Equal(0, service.StartupConnectAttempts);
        Assert.Equal(0, factory.OpenAttemptCount);
        Assert.False(service.HasStarted);
        Assert.False(service.IsAccepting);
        Assert.Contains(
            logger.Messages,
            static message => message.Contains("NntpDB connection string is invalid", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Messages,
            static message => message.Contains("startup connectivity retry", StringComparison.Ordinal));
        Assert.Equal(1, logger.Messages.Count(static message =>
            message.Contains("NntpDB connection string is invalid", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task StartAsync_MalformedConnectionString_DoesNotExposePassword()
    {
        var factory = new FakeNntpDbConnectionFactory();
        var logger = new CollectingLogger<NntpDbService>();
        var service = CreateService(
            factory,
            connectionString: TestHostFactory.MalformedNntpDbConnectionStringWithPassword,
            logger: logger);

        var ex = await Assert.ThrowsAsync<NntpDbConfigurationException>(
            () => service.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));

        AssertSecretNotExposed(ex.Message);
        AssertSecretNotExposed(ex.Reason);
        AssertSecretNotExposed(ex.ToString());
        foreach (var message in logger.Messages)
        {
            AssertSecretNotExposed(message);
        }

        foreach (var logged in logger.Exceptions)
        {
            AssertSecretNotExposed(logged.ToString());
        }

        Assert.Equal(0, service.StartupConnectAttempts);
        Assert.Equal(0, factory.OpenAttemptCount);
    }

    [Fact]
    public async Task StartAsync_ValidConnectionString_ProceedsPastParsing()
    {
        var factory = new FakeNntpDbConnectionFactory();
        var service = CreateService(factory, connectionString: TestHostFactory.TestNntpDbConnectionString);
        await service.StartAsync(CancellationToken.None);

        Assert.True(service.HasStarted);
        Assert.Equal(1, service.StartupConnectAttempts);
        Assert.Equal(1, factory.OpenAttemptCount);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task StartAsync_CanceledToken_IsNotConvertedToConfigurationFailure()
    {
        var factory = new FakeNntpDbConnectionFactory();
        var service = CreateService(factory, connectionString: TestHostFactory.TestNntpDbConnectionString);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StartAsync(cts.Token));
        Assert.Equal(0, service.StartupConnectAttempts);
        Assert.Equal(0, factory.OpenAttemptCount);
        Assert.False(service.HasStarted);
    }

    [Fact]
    public async Task StartAsync_MalformedConnectionString_WithRealFactory_DoesNotOpen()
    {
        var clock = new ControllableTimeProvider();
        var service = new NntpDbService(
            new MySqlNntpDbConnectionFactory(),
            Options.Create(new NntpDbOptions
            {
                ConnectionString = TestHostFactory.MalformedNntpDbConnectionString,
                StartupTimeout = TimeSpan.FromMinutes(1),
            }),
            NullLogger<NntpDbService>.Instance,
            clock);

        var ex = await Assert.ThrowsAsync<NntpDbConfigurationException>(
            () => service.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Contains("NntpDB connection string is invalid", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, service.StartupConnectAttempts);
        Assert.False(service.HasStarted);
    }

    [Fact]
    public async Task Lifecycle_MalformedConnectionString_FailsStartupWithConfigurationException()
    {
        var factory = new FakeNntpDbConnectionFactory();
        var service = CreateService(
            factory,
            startupTimeout: TimeSpan.FromMinutes(1),
            connectionString: TestHostFactory.MalformedNntpDbConnectionString);
        var lifecycle = TestHostFactory.CreateLifecycle([service]);

        var ex = await Assert.ThrowsAsync<NntpDbConfigurationException>(
            () => lifecycle.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Contains("NntpDB connection string is invalid", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, service.StartupConnectAttempts);
        Assert.Equal(0, factory.OpenAttemptCount);
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
    }

    [Fact]
    public async Task StartAsync_FailsImmediately_OnAuthenticationFailure()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            OpenException = new NntpDbAuthenticationException("denied"),
        };
        var service = CreateService(factory, startupTimeout: TimeSpan.FromMinutes(1));

        await Assert.ThrowsAsync<NntpDbAuthenticationException>(() => service.StartAsync(CancellationToken.None));
        Assert.False(service.HasStarted);
        Assert.False(service.IsAccepting);
    }

    [Fact]
    public async Task StartAsync_FailsImmediately_WhenSelectOneReturnsWrongValue()
    {
        var factory = new FakeNntpDbConnectionFactory { NextHealthCheckResult = 0 };
        var service = CreateService(factory);

        var ex = await Assert.ThrowsAsync<NntpDbUnavailableException>(() => service.StartAsync(CancellationToken.None));
        Assert.Contains("SELECT 1", ex.Message, StringComparison.Ordinal);
        Assert.False(service.HasStarted);
        Assert.Equal(1, Assert.Single(factory.Connections).DisposeCount);
    }

    [Fact]
    public async Task StartAsync_FailsImmediately_WhenSelectOneThrows()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            NextHealthCheckException = new NntpDbUnavailableException("MySQL health query SELECT 1 failed."),
        };
        var service = CreateService(factory);

        await Assert.ThrowsAsync<NntpDbUnavailableException>(() => service.StartAsync(CancellationToken.None));
        Assert.False(service.HasStarted);
        Assert.Equal(1, Assert.Single(factory.Connections).DisposeCount);
    }

    [Fact]
    public async Task StartAsync_RetriesTransientOpenFailure_ThenSucceeds()
    {
        var factory = new FakeNntpDbConnectionFactory { RemainingOpenFailures = 1 };
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);

        Assert.True(service.HasStarted);
        Assert.Equal(0, factory.RemainingOpenFailures);
        Assert.Equal(2, factory.OpenAttemptCount);
        Assert.Equal(2, service.StartupConnectAttempts);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, Assert.Single(factory.Connections).SelectOneCount);
        Assert.Equal(1, factory.Connections[0].DisposeCount);
    }

    [Fact]
    public async Task StartAsync_CancellationDuringOpen_ResetsState_AndIsRetryable()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            BlockOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            OpenStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var service = CreateService(factory);
        using var cts = new CancellationTokenSource();
        var starting = service.StartAsync(cts.Token);
        await factory.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        factory.BlockOpen.TrySetResult();
        factory.BlockOpen = null;
        factory.OpenStarted = null;

        Assert.False(service.HasStarted);
        Assert.False(service.IsAccepting);
        Assert.Empty(factory.Connections);

        await service.StartAsync(CancellationToken.None);
        Assert.True(service.HasStarted);
        Assert.True(service.IsAccepting);
        Assert.Equal(1, Assert.Single(factory.Connections).SelectOneCount);
    }

    [Fact]
    public async Task StartAsync_CancellationDuringSelectOne_DisposesConnection_AndRetriesFullStartup()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            BlockSelectOne = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            SelectOneStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var service = CreateService(factory);
        using var cts = new CancellationTokenSource();
        var starting = service.StartAsync(cts.Token);
        await factory.SelectOneStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        factory.BlockSelectOne.TrySetResult();
        factory.BlockSelectOne = null;
        factory.SelectOneStarted = null;

        var first = Assert.Single(factory.Connections);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, first.SelectOneCount);
        Assert.False(service.HasStarted);
        Assert.False(service.IsAccepting);

        await service.StartAsync(CancellationToken.None);
        Assert.True(service.HasStarted);
        Assert.True(service.IsAccepting);
        Assert.Equal(1, factory.Connections.Sum(static connection => connection.SelectOneCount));
        Assert.All(factory.Connections, static connection => Assert.Equal(1, connection.DisposeCount));
    }

    [Fact]
    public async Task OpenAsync_CreatesLogicalConnection_AndDoesNotRetainIt()
    {
        var factory = new FakeNntpDbConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        Assert.Equal(1, factory.OpenCount);

        await using (var connection = await service.OpenAsync())
        {
            Assert.Equal(2, factory.OpenCount);
            Assert.Equal(0, ((FakeNntpDbConnection)connection).DisposeCount);
        }

        Assert.Equal(2, factory.OpenCount);
        Assert.All(factory.Connections, static connection => Assert.Equal(1, connection.DisposeCount));
    }

    [Fact]
    public async Task StopAsync_RejectsNewOpens()
    {
        var factory = new FakeNntpDbConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.OpenAsync().AsTask());
        Assert.False(service.IsAccepting);
    }

    [Fact]
    public async Task Lifecycle_StartsServiceBeforeRunning_AndStopsIt()
    {
        var factory = new FakeNntpDbConnectionFactory();
        var service = CreateService(factory);
        var lifecycle = TestHostFactory.CreateLifecycle([service]);

        await lifecycle.StartAsync(CancellationToken.None);
        Assert.Equal(ApplicationState.Running, lifecycle.State);
        Assert.True(service.HasStarted);
        Assert.Equal(1, factory.OpenCount);

        await lifecycle.StopAsync(CancellationToken.None);
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.OpenAsync().AsTask());
    }

    [Fact]
    public void Options_NeverExposeSecretPropertyNamesOnValidatorFailures()
    {
        var options = new NntpDbOptions
        {
            ConnectionString = "Server=db;Password=super-secret;User ID=nntpd;",
            StartupTimeout = TimeSpan.Zero,
        };
        var result = new NntpDbOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.DoesNotContain(result.Failures!, static failure => failure.Contains("super-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Failures!, static failure => failure.Contains("Password=", StringComparison.Ordinal));
    }

    private static NntpDbService CreateService(
        FakeNntpDbConnectionFactory factory,
        TimeSpan? startupTimeout = null,
        TimeProvider? clock = null,
        string? connectionString = null,
        ILogger<NntpDbService>? logger = null)
    {
        var options = new NntpDbOptions
        {
            ConnectionString = connectionString ?? TestHostFactory.TestNntpDbConnectionString,
            StartupTimeout = startupTimeout ?? TimeSpan.FromSeconds(15),
        };
        return new NntpDbService(
            factory,
            Options.Create(options),
            logger ?? NullLogger<NntpDbService>.Instance,
            clock ?? TimeProvider.System);
    }

    private static void AssertSecretNotExposed(string text)
    {
        Assert.DoesNotContain(TestHostFactory.FakeNntpDbPassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestHostFactory.MalformedNntpDbConnectionStringWithPassword, text, StringComparison.Ordinal);
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public List<Exception> Exceptions { get; } = [];

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
            if (exception is not null)
            {
                Exceptions.Add(exception);
            }
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

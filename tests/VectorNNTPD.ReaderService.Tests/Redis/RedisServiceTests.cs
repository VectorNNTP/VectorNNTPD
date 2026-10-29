using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Redis;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.Redis;

public sealed class RedisServiceTests
{
    [Fact]
    public async Task StartAsync_ConnectsOnce_AndPings()
    {
        var factory = new FakeRedisConnectionFactory();
        var service = CreateService(factory);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(1, service.ConnectionCount);
        Assert.Equal(1, factory.LastConnection!.Database.PingCount);
        Assert.Same(factory.LastConnection.Database, service.Database);
    }

    [Fact]
    public async Task StartAsync_DoesNotCreatePerRequestConnections()
    {
        var factory = new FakeRedisConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);

        _ = await service.KeyExistsAsync("a"u8.ToArray());
        _ = await service.KeyExistsAsync("b"u8.ToArray());
        _ = await service.GetAsync("c"u8.ToArray());
        await service.SetAsync("c"u8.ToArray(), "d"u8.ToArray(), TimeSpan.FromMinutes(1));

        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(2, factory.LastConnection!.Database.KeyExistsCount);
        Assert.Equal(1, factory.LastConnection.Database.GetCount);
        Assert.Equal(1, factory.LastConnection.Database.SetCount);
    }

    [Fact]
    public async Task StartAsync_Fails_WhenConnectThrows()
    {
        var factory = new FakeRedisConnectionFactory
        {
            ConnectException = new RedisUnavailableException("down"),
        };
        var service = CreateService(factory);

        await Assert.ThrowsAsync<RedisUnavailableException>(() => service.StartAsync(CancellationToken.None));
        Assert.Equal(0, service.ConnectionCount);
    }

    [Fact]
    public async Task StopAsync_DisposesConnectionOnce()
    {
        var factory = new FakeRedisConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        var connection = factory.LastConnection!;

        await service.StopAsync(CancellationToken.None);
        await service.DisposeAsync();

        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task KeyExistsAsync_PropagatesInfrastructureFailure()
    {
        var factory = new FakeRedisConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        factory.LastConnection!.Database.ExistsException = new RedisUnavailableException("timeout");

        await Assert.ThrowsAsync<RedisUnavailableException>(
            () => service.KeyExistsAsync("k"u8.ToArray()).AsTask());
    }

    [Fact]
    public async Task Circuit_BlocksUntilCooldown_ThenRecovers()
    {
        var clock = new ManualTimeProvider();
        var factory = new FakeRedisConnectionFactory();
        var service = new RedisService(
            factory,
            Options.Create(new RedisOptions { Host = ["127.0.0.1"], Port = 6379 }),
            NullLogger<RedisService>.Instance,
            clock,
            TimeSpan.FromSeconds(1));
        await service.StartAsync(CancellationToken.None);
        factory.LastConnection!.Database.ExistsException = new RedisUnavailableException("timeout");

        var history = new VectorNNTPD.ReaderService.History.HistoryDb(
            service,
            TimeSpan.FromHours(2),
            NullLogger<VectorNNTPD.ReaderService.History.HistoryDb>.Instance,
            TimeProvider.System,
            new VectorNNTPD.ReaderService.History.HistoryWriteQueue());

        Assert.Equal(
            VectorNNTPD.ReaderService.History.HistoryLookupResult.Unavailable,
            await history.LookupAsync("<a@example.com>"u8.ToArray()));
        Assert.True(service.IsUnavailable);
        var exists = factory.LastConnection.Database.KeyExistsCount;

        Assert.Equal(
            VectorNNTPD.ReaderService.History.HistoryLookupResult.Unavailable,
            await history.LookupAsync("<b@example.com>"u8.ToArray()));
        Assert.Equal(exists, factory.LastConnection.Database.KeyExistsCount);

        clock.Advance(TimeSpan.FromSeconds(1));
        factory.LastConnection.Database.ExistsException = null;
        Assert.Equal(
            VectorNNTPD.ReaderService.History.HistoryLookupResult.Unseen,
            await history.LookupAsync("<c@example.com>"u8.ToArray()));
        Assert.False(service.IsUnavailable);
        Assert.Equal(exists + 1, factory.LastConnection.Database.KeyExistsCount);

        await service.DisposeAsync();
    }

    [Fact]
    public async Task CancelledLookup_DoesNotOpenCooldown_AndNextLookupHitsRedis()
    {
        var factory = new FakeRedisConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.LastConnection!.Database.ExistsStarted = started;
        factory.LastConnection.Database.BlockExists = block;

        var history = new VectorNNTPD.ReaderService.History.HistoryDb(
            service,
            TimeSpan.FromHours(2),
            NullLogger<VectorNNTPD.ReaderService.History.HistoryDb>.Instance,
            TimeProvider.System,
            new VectorNNTPD.ReaderService.History.HistoryWriteQueue());

        using var cts = new CancellationTokenSource();
        var lookup = history.LookupAsync("<cancel@example.com>"u8.ToArray(), cts.Token);
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await started.Task.WaitAsync(safety.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup.AsTask());
        Assert.False(service.IsUnavailable);

        factory.LastConnection.Database.BlockExists = null;
        block.TrySetCanceled();
        Assert.Equal(
            VectorNNTPD.ReaderService.History.HistoryLookupResult.Unseen,
            await history.LookupAsync("<after-cancel@example.com>"u8.ToArray()));
        Assert.False(service.IsUnavailable);
        Assert.Equal(1, factory.LastConnection.Database.KeyExistsCount);

        await service.DisposeAsync();
    }

    [Fact]
    public void RecoveryProbe_Alone_CanClearCooldown()
    {
        var clock = new ManualTimeProvider();
        var circuit = new RedisAvailability(clock, TimeSpan.FromSeconds(1));
        Assert.True(circuit.TryBegin(out var healthyGrant));
        Assert.False(healthyGrant);
        circuit.Complete(isRecoveryProbe: false, succeeded: false, out var becameUnavailable, out _);
        Assert.True(becameUnavailable);
        Assert.True(circuit.IsUnavailable);
        Assert.False(circuit.TryBegin(out _));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(circuit.TryBegin(out var probe));
        Assert.True(probe);
        Assert.False(circuit.TryBegin(out var other));
        Assert.False(other);

        circuit.Complete(isRecoveryProbe: false, succeeded: true, out _, out var recoveredByOther);
        Assert.False(recoveredByOther);
        Assert.False(circuit.TryBegin(out _));

        circuit.Complete(isRecoveryProbe: true, succeeded: true, out _, out var recoveredByProbe);
        Assert.True(recoveredByProbe);
        Assert.False(circuit.IsUnavailable);
        Assert.True(circuit.TryBegin(out var after));
        Assert.False(after);
    }

    [Fact]
    public void RecoveryProbe_Failure_RestartsCooldown()
    {
        var clock = new ManualTimeProvider();
        var circuit = new RedisAvailability(clock, TimeSpan.FromSeconds(1));
        Assert.True(circuit.TryBegin(out _));
        circuit.Complete(isRecoveryProbe: false, succeeded: false, out _, out _);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(circuit.TryBegin(out var probe));
        Assert.True(probe);
        circuit.Complete(isRecoveryProbe: true, succeeded: false, out var becameUnavailable, out var recovered);
        Assert.False(recovered);
        Assert.False(becameUnavailable);
        Assert.True(circuit.IsUnavailable);
        Assert.False(circuit.TryBegin(out _));
    }

    [Fact]
    public void AbandonProbe_DoesNotOpenCooldown()
    {
        var clock = new ManualTimeProvider();
        var circuit = new RedisAvailability(clock, TimeSpan.FromSeconds(1));
        Assert.True(circuit.TryBegin(out _));
        circuit.Complete(isRecoveryProbe: false, succeeded: false, out _, out _);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(circuit.TryBegin(out var probe));
        Assert.True(probe);
        circuit.Abandon(isRecoveryProbe: true);
        Assert.False(circuit.IsUnavailable);
        Assert.True(circuit.TryBegin(out var next));
        Assert.True(next);
    }

    private static RedisService CreateService(FakeRedisConnectionFactory factory) =>
        new(
            factory,
            Options.Create(new RedisOptions { Host = ["127.0.0.1"], Port = 6379 }),
            NullLogger<RedisService>.Instance);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utc;

        public void Advance(TimeSpan delta) => _utc += delta;
    }
}

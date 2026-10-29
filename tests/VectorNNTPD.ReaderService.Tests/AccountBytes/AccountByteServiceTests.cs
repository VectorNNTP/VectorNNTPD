using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.SessionState.BytesAccounting;
using VectorNNTPD.ReaderService.Tests.Fixtures;

namespace VectorNNTPD.ReaderService.Tests.AccountBytes;

public sealed class AccountByteServiceTests
{
    [Fact]
    public async Task SessionStateService_ReconcilesBytesOnInterval_ThenStopDoesFinalReconcile()
    {
        var durable = new InMemoryAccountByteDurableStore();
        var cluster = new InMemoryAccountByteStore();
        durable.SeedByteAccount("alice", 1000);
        var tracker = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        tracker.CreateSink("alice").ObserveCopied(25);
        var clock = new ControllableTimeProvider();
        var service = new SessionStateService(
            new NoOpLeaseManager(),
            tracker,
            NullLogger<SessionStateService>.Instance,
            clock,
            TimeSpan.FromSeconds(10));

        await service.StartAsync(CancellationToken.None);
        Assert.Equal("SessionState", service.Name);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (durable.ConsumeCalls < 1)
        {
            wait.Token.ThrowIfCancellationRequested();
            if (!clock.HasScheduledTimers)
            {
                await Task.Yield();
                continue;
            }

            clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Yield();
        }

        Assert.Equal(975, durable.Remaining("alice"));
        tracker.CreateSink("alice").ObserveCopied(25);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(950, durable.Remaining("alice"));
        Assert.True(durable.ConsumeCalls >= 2);
    }

    [Fact]
    public async Task SessionStateService_StartIsIdempotent()
    {
        var durable = new InMemoryAccountByteDurableStore();
        var cluster = new InMemoryAccountByteStore();
        var tracker = new AccountByteTracker(durable, cluster, NullLogger<AccountByteTracker>.Instance);
        var service = new SessionStateService(
            new NoOpLeaseManager(),
            tracker,
            NullLogger<SessionStateService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(0, durable.ConsumeCalls);
    }

    [Fact]
    public void Defaults_ShareSessionStateTenSecondCycle()
    {
        Assert.Equal(SessionStateDefaults.RenewalPeriod, AccountByteDefaults.ReconciliationPeriod);
        Assert.Equal(TimeSpan.FromSeconds(10), AccountByteDefaults.ReconciliationPeriod);
    }

    [Fact]
    public void AccountByteService_DoesNotExist()
    {
        var assembly = typeof(SessionStateService).Assembly;
        Assert.Null(assembly.GetType("VectorNNTPD.ReaderService.AccountBytes.AccountByteService"));
        Assert.Null(assembly.GetType("VectorNNTPD.ReaderService.SessionState.BytesAccounting.AccountByteService"));
        Assert.Null(assembly.GetType("VectorNNTPD.ReaderService.SessionState.AccountByteService"));
    }

    private sealed class NoOpLeaseManager : ISessionStateLeaseManager
    {
        public ValueTask RenewLeasesAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ReleaseAllOwnershipAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}

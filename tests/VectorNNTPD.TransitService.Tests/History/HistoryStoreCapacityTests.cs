using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.TransitService.History;
using VectorNNTPD.TransitService.Tests.TestDoubles;

namespace VectorNNTPD.TransitService.Tests.History;

public sealed class HistoryStoreCapacityTests
{
    [Fact]
    public void Add_DoesNotScan_AndHonorsHardCap()
    {
        const int cap = 2048;
        var clock = new ManualTimeProvider();
        var store = new LocalHistoryStore(TimeSpan.FromHours(2), clock, cap);

        for (var i = 0; i < cap + 256; i++)
        {
            store.Add(HistoryDigest.FromMessageId(Id(i)));
        }

        Assert.Equal(cap, store.ApproximateCount);
        Assert.Equal(0, store.MaintenanceScanCount);
        Assert.True(store.Contains(HistoryDigest.FromMessageId(Id(0))));
        Assert.False(store.Contains(HistoryDigest.FromMessageId(Id(cap + 10))));
    }

    [Fact]
    public void Maintain_RemovesExpired_AndFreesSlots_OffCheckPath()
    {
        const int cap = 64;
        var clock = new ManualTimeProvider();
        var store = new LocalHistoryStore(TimeSpan.FromMinutes(5), clock, cap);
        for (var i = 0; i < cap; i++)
        {
            store.Add(HistoryDigest.FromMessageId(Id(i)));
        }

        clock.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, store.MaintenanceScanCount);
        Assert.False(store.Contains(HistoryDigest.FromMessageId(Id(0))));
        Assert.Equal(cap - 1, store.ApproximateCount);

        var removed = store.Maintain();
        Assert.Equal(1, store.MaintenanceScanCount);
        Assert.Equal(cap - 1, removed);
        Assert.Equal(0, store.ApproximateCount);

        store.Add(HistoryDigest.FromMessageId(Id(10_000)));
        Assert.True(store.Contains(HistoryDigest.FromMessageId(Id(10_000))));
        Assert.Equal(1, store.MaintenanceScanCount);
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public void UniqueInserts_StayBounded_WithoutMaintenanceScan(int count)
    {
        var store = new LocalHistoryStore(TimeSpan.FromHours(2), TimeProvider.System);
        for (var i = 0; i < count; i++)
        {
            store.Add(HistoryDigest.FromMessageId(Id(i)));
        }

        Assert.Equal(count, store.ApproximateCount);
        Assert.Equal(0, store.MaintenanceScanCount);
        Assert.True(store.Contains(HistoryDigest.FromMessageId(Id(0))));
        Assert.True(store.Contains(HistoryDigest.FromMessageId(Id(count - 1))));
    }

    [Fact]
    public void Maintain_DoesNotRemoveRefreshedLiveEntry()
    {
        var clock = new ManualTimeProvider();
        var store = new LocalHistoryStore(TimeSpan.FromMinutes(5), clock);
        var digest = HistoryDigest.FromMessageId(Id(1));
        var observedExpiry = clock.GetUtcNow().Add(TimeSpan.FromMinutes(5)).UtcTicks;
        store.Add(digest);

        clock.Advance(TimeSpan.FromMinutes(6));
        store.Add(digest);

        Assert.False(store.TryRemoveObservedExpiry(digest, observedExpiry));
        Assert.Equal(0, store.Maintain());
        Assert.True(store.Contains(digest));
        Assert.Equal(1, store.EntryCountForTests);
        Assert.Equal(1, store.ApproximateCount);
    }

    [Fact]
    public void ConcurrentInserts_NeverExceedHardCap()
    {
        const int cap = 128;
        var store = new LocalHistoryStore(TimeSpan.FromHours(2), TimeProvider.System, cap);
        Parallel.For(0, 2_000, i => store.Add(HistoryDigest.FromMessageId(Id(i))));

        Assert.True(store.EntryCountForTests <= cap);
        Assert.True(store.ApproximateCount <= cap);
        Assert.Equal(store.EntryCountForTests, store.ApproximateCount);
        Assert.Equal(0, store.MaintenanceScanCount);
    }

    [Fact]
    public async Task MaintenanceService_Stop_CompletesWithoutThrowing()
    {
        var redis = new FakeRedisService();
        var history = new HistoryDb(
            redis,
            TimeSpan.FromHours(2),
            NullLogger<HistoryDb>.Instance,
            TimeProvider.System,
            new HistoryWriteQueue());
        var service = new HistoryMaintenanceService(
            history,
            NullLogger<HistoryMaintenanceService>.Instance,
            TimeProvider.System,
            TimeSpan.FromHours(1));

        await service.StartAsync(CancellationToken.None);
        Assert.NotNull(service.Execution);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(0, history.Local.MaintenanceScanCount);
    }

    private static byte[] Id(int i) => Encoding.ASCII.GetBytes($"<cap-{i}@example.com>");

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utc;

        public void Advance(TimeSpan delta) => _utc += delta;
    }
}

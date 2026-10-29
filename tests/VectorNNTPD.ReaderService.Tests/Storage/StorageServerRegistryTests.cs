using System.Reflection;
using VectorNNTPD.Common.Messaging.Cache;
using VectorNNTPD.ReaderService.Storage;

namespace VectorNNTPD.ReaderService.Tests.Storage;

public sealed class StorageServerRegistryTests
{
    [Fact]
    public void ApplyAdvertisement_CreatesAndUpdatesEntry()
    {
        var registry = new StorageServerRegistry();
        var t0 = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
        var first = new StorageServerAdvertisement(1, 1, "cache01.usenet.ninja", 1000, 100, 900, t0);
        registry.ApplyAdvertisement(first, t0);

        Assert.True(registry.TryGet("cache01.usenet.ninja", out var entry));
        Assert.Equal(1, entry.ServerId);
        Assert.Equal(1000, entry.TotalBytes);
        Assert.Equal(100, entry.UsedBytes);
        Assert.Equal(900, entry.AvailableBytes);
        Assert.Equal(t0, entry.LastSeen);
        Assert.True(entry.IsActive(t0));

        var t1 = t0.AddSeconds(1);
        var second = first with { UsedBytes = 200, AvailableBytes = 800, Timestamp = t1 };
        registry.ApplyAdvertisement(second, t1);

        Assert.True(registry.TryGet("cache01.usenet.ninja", out entry));
        Assert.Equal(200, entry.UsedBytes);
        Assert.Equal(800, entry.AvailableBytes);
        Assert.Equal(t1, entry.LastSeen);
        Assert.Equal(StorageServerFleetState.Active, entry.GetState(t1));
    }

    [Fact]
    public void GetActive_ExcludesStaleServers_AndKeepsMultipleActive()
    {
        var registry = new StorageServerRegistry();
        var t0 = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
        registry.ApplyAdvertisement(
            new StorageServerAdvertisement(1, 1, "cache01.usenet.ninja", 100, 10, 90, t0),
            t0);
        registry.ApplyAdvertisement(
            new StorageServerAdvertisement(1, 2, "cache02.usenet.ninja", 200, 20, 180, t0),
            t0);

        var activeAtStart = registry.GetActive(t0);
        Assert.Equal(2, activeAtStart.Count);

        var staleNow = t0 + CacheFleetTopology.LivenessWindow + TimeSpan.FromMilliseconds(1);
        Assert.Empty(registry.GetActive(staleNow));
        Assert.Equal(2, registry.Snapshot().Count);
        Assert.Equal(StorageServerFleetState.Stale, registry.Snapshot()[0].GetState(staleNow));

        registry.ApplyAdvertisement(
            new StorageServerAdvertisement(1, 2, "cache02.usenet.ninja", 200, 50, 150, staleNow),
            staleNow);
        var active = Assert.Single(registry.GetActive(staleNow));
        Assert.Equal("cache02.usenet.ninja", active.Fqdn);
        Assert.Equal(50, active.UsedBytes);
    }

    [Fact]
    public void FirstAdvertisement_MakesTheServerSelectable_WithoutASecondRegistryEntry()
    {
        var registry = new StorageServerRegistry();
        var t0 = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
        registry.ApplyAdvertisement(Advertisement("cache01.usenet.ninja", 1, t0), t0);

        var selected = Assert.Single(registry.GetActive(t0));
        Assert.Equal(1, selected.ServerId);
        Assert.Equal(1191, selected.VatpPort);
        Assert.False(selected.IsDraining);
        Assert.True(StorageServerPlacementSelector.TrySelect(registry.GetActive(t0), out var target));
        Assert.Equal("cache01.usenet.ninja", target.Fqdn);

        registry.ApplyAdvertisement(
            Advertisement("cache01.usenet.ninja", 1, t0.AddSeconds(1)) with { AvailableBytes = 900 },
            t0.AddSeconds(1));
        var updated = Assert.Single(registry.Snapshot());
        Assert.Equal(900, updated.AvailableBytes);
        Assert.False(updated.IsDraining);
        Assert.Equal("cache01.usenet.ninja", Assert.Single(registry.GetActive(t0.AddSeconds(1))).Fqdn);
    }

    [Fact]
    public void Draining_RemovesTheServerFromNewSelection_Immediately()
    {
        var registry = new StorageServerRegistry();
        var t0 = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
        registry.ApplyAdvertisement(Advertisement("cache01.usenet.ninja", 1, t0), t0);
        registry.ApplyAdvertisement(Advertisement("cache02.usenet.ninja", 2, t0), t0);
        registry.ApplyLifecycle(Draining("cache01.usenet.ninja", 1, t0.AddTicks(1)), t0);

        var selected = Assert.Single(registry.GetActive(t0));
        Assert.Equal("cache02.usenet.ninja", selected.Fqdn);
        Assert.True(registry.TryGet("cache01.usenet.ninja", out var drained));
        Assert.True(drained.IsDraining);
        Assert.Equal(500, drained.AvailableBytes);
        Assert.True(drained.IsActive(t0));
    }

    [Fact]
    public void DelayedAdvertisement_DoesNotClearDraining()
    {
        var registry = new StorageServerRegistry();
        var t0 = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
        var drainAt = t0.AddSeconds(2);
        registry.ApplyAdvertisement(Advertisement("cache01.usenet.ninja", 1, t0), t0);
        registry.ApplyLifecycle(Draining("cache01.usenet.ninja", 1, drainAt), drainAt);

        registry.ApplyAdvertisement(
            Advertisement("cache01.usenet.ninja", 1, t0.AddSeconds(1)) with { AvailableBytes = 1 },
            drainAt);

        Assert.Empty(registry.GetActive(drainAt));
        Assert.True(registry.TryGet("cache01.usenet.ninja", out var entry));
        Assert.True(entry.IsDraining);
        Assert.Equal(500, entry.AvailableBytes);
        Assert.Equal(drainAt, entry.LastSeen);
    }

    [Fact]
    public void CrashWithoutDraining_ExpiresThroughTheLivenessWindow()
    {
        var registry = new StorageServerRegistry();
        var seen = DateTimeOffset.Parse("2026-09-29T12:00:01Z");
        registry.ApplyAdvertisement(Advertisement("cache01.usenet.ninja", 1, seen), seen);

        Assert.Single(registry.GetActive(seen));
        var expired = seen + CacheFleetTopology.LivenessWindow + TimeSpan.FromMilliseconds(1);
        Assert.Empty(registry.GetActive(expired));
        Assert.Single(registry.Snapshot());
        Assert.False(registry.Snapshot()[0].IsDraining);
    }

    [Fact]
    public void Restart_BecomesSelectableFromANewerAdvertisement()
    {
        var registry = new StorageServerRegistry();
        var t0 = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
        registry.ApplyAdvertisement(Advertisement("cache01.usenet.ninja", 1, t0), t0);
        registry.ApplyLifecycle(Draining("cache01.usenet.ninja", 1, t0.AddSeconds(1)), t0.AddSeconds(1));
        Assert.Empty(registry.GetActive(t0.AddSeconds(1)));

        var restarted = t0.AddSeconds(3);
        registry.ApplyAdvertisement(
            Advertisement("cache01.usenet.ninja", 1, restarted) with { AvailableBytes = 42 },
            restarted);
        var selected = Assert.Single(registry.GetActive(restarted));
        Assert.False(selected.IsDraining);
        Assert.Equal(42, selected.AvailableBytes);

        registry.ApplyLifecycle(Draining("cache01.usenet.ninja", 1, t0.AddSeconds(1)), restarted);
        Assert.Single(registry.GetActive(restarted));
        Assert.Equal(42, Assert.Single(registry.Snapshot()).AvailableBytes);

        var drainedAgain = restarted.AddSeconds(1);
        registry.ApplyLifecycle(Draining("cache01.usenet.ninja", 1, drainedAgain), drainedAgain);
        Assert.Empty(registry.GetActive(drainedAgain));
        Assert.Equal(42, Assert.Single(registry.Snapshot()).AvailableBytes);
    }

    [Fact]
    public void DelayedAdvertisement_AfterRestart_DoesNotReplaceANewerAdvertisement()
    {
        var registry = new StorageServerRegistry();
        var t0 = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
        var drainAt = t0.AddSeconds(10);
        var restartedAt = drainAt.AddSeconds(1);
        var received = restartedAt.AddSeconds(1);
        registry.ApplyAdvertisement(Advertisement("cache01.usenet.ninja", 1, t0), t0);
        registry.ApplyLifecycle(Draining("cache01.usenet.ninja", 1, drainAt), drainAt);
        registry.ApplyAdvertisement(
            Advertisement("cache01.usenet.ninja", 1, restartedAt) with { AvailableBytes = 42 },
            received);

        registry.ApplyAdvertisement(
            Advertisement("cache01.usenet.ninja", 1, t0.AddSeconds(1)) with { AvailableBytes = 1 },
            received.AddSeconds(1));
        registry.ApplyLifecycle(Draining("cache01.usenet.ninja", 1, drainAt), received.AddSeconds(1));

        var entry = Assert.Single(registry.GetActive(received.AddSeconds(1)));
        Assert.False(entry.IsDraining);
        Assert.Equal(received, entry.LastSeen);
        Assert.Equal(42, entry.AvailableBytes);
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void AdvertisementNotNewerThanDraining_StaysUnselectable()
    {
        var registry = new StorageServerRegistry();
        var t0 = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
        var drainAt = t0.AddSeconds(10);
        registry.ApplyAdvertisement(Advertisement("cache01.usenet.ninja", 1, t0), t0);
        registry.ApplyLifecycle(Draining("cache01.usenet.ninja", 1, drainAt), drainAt);

        registry.ApplyAdvertisement(
            Advertisement("cache01.usenet.ninja", 1, drainAt) with { AvailableBytes = 7 },
            drainAt.AddSeconds(1));
        registry.ApplyAdvertisement(
            Advertisement("cache01.usenet.ninja", 1, t0.AddSeconds(9)) with { AvailableBytes = 8 },
            drainAt.AddSeconds(2));

        Assert.Empty(registry.GetActive(drainAt.AddSeconds(2)));
        Assert.True(registry.TryGet("cache01.usenet.ninja", out var entry));
        Assert.True(entry.IsDraining);
        Assert.Equal(500, entry.AvailableBytes);
        Assert.Equal(drainAt, entry.LastSeen);
    }

    [Fact]
    public void OlderAdvertisement_DoesNotReplaceANewerAdvertisement()
    {
        var registry = new StorageServerRegistry();
        var t0 = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
        var newer = t0.AddSeconds(2);
        registry.ApplyAdvertisement(Advertisement("cache01.usenet.ninja", 1, newer) with { AvailableBytes = 80 }, newer);
        registry.ApplyAdvertisement(
            Advertisement("cache01.usenet.ninja", 1, t0) with { AvailableBytes = 1 },
            newer.AddSeconds(1));

        var entry = Assert.Single(registry.GetActive(newer.AddSeconds(1)));
        Assert.Equal(80, entry.AvailableBytes);
        Assert.Equal(newer, entry.LastSeen);
        Assert.False(entry.IsDraining);
    }

    [Fact]
    public void RepeatedDraining_StaysOneEntry()
    {
        var registry = new StorageServerRegistry();
        var t0 = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
        registry.ApplyAdvertisement(Advertisement("cache01.usenet.ninja", 1, t0), t0);
        registry.ApplyLifecycle(Draining("cache01.usenet.ninja", 1, t0.AddSeconds(2)), t0.AddSeconds(2));
        registry.ApplyLifecycle(Draining("cache01.usenet.ninja", 1, t0.AddSeconds(3)), t0.AddSeconds(3));
        registry.ApplyLifecycle(Draining("cache01.usenet.ninja", 1, t0.AddSeconds(2)), t0.AddSeconds(4));

        Assert.Empty(registry.GetActive(t0.AddSeconds(4)));
        var entry = Assert.Single(registry.Snapshot());
        Assert.True(entry.IsDraining);
        Assert.Equal(500, entry.AvailableBytes);
        Assert.Equal(t0.AddSeconds(3), entry.NewestMessageTimestamp);
    }

    [Fact]
    public void LivenessExpiry_DoesNotConvertDrainingToSelectable()
    {
        var registry = new StorageServerRegistry();
        var t0 = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
        registry.ApplyAdvertisement(Advertisement("cache01.usenet.ninja", 1, t0), t0);
        registry.ApplyLifecycle(Draining("cache01.usenet.ninja", 1, t0.AddSeconds(1)), t0.AddSeconds(1));

        Assert.Empty(registry.GetActive(t0.AddSeconds(1)));
        var expired = t0.AddSeconds(1) + CacheFleetTopology.LivenessWindow + TimeSpan.FromMilliseconds(1);
        Assert.Empty(registry.GetActive(expired));
        Assert.True(registry.TryGet("cache01.usenet.ninja", out var entry));
        Assert.True(entry.IsDraining);
    }

    [Fact]
    public void Registry_HasNoReadyEpochState()
    {
        const BindingFlags publicStatic = BindingFlags.Public | BindingFlags.Static;
        Assert.Null(typeof(StorageServerFleetEntry).GetProperty("LifecycleOriginTimestamp"));
        Assert.Null(typeof(StorageServerFleetEntry).GetProperty("SupersededDrainingTimestamp"));
        Assert.Null(typeof(StorageServerLifecycleState).GetField("Ready", publicStatic));
        Assert.NotNull(typeof(StorageServerLifecycleState).GetField(nameof(StorageServerLifecycleState.Draining), publicStatic));
        Assert.Equal(1, StorageServerLifecycleWireProtocol.CurrentVersion);
    }

    private static StorageServerAdvertisement Advertisement(string fqdn, int serverId, DateTimeOffset timestamp) =>
        new(1, serverId, fqdn, 1000, 500, 500, timestamp, 1191);

    private static StorageServerLifecycleAnnouncement Draining(string fqdn, int serverId, DateTimeOffset timestamp) =>
        new(1, serverId, fqdn, StorageServerLifecycleState.Draining, timestamp, 1191);
}

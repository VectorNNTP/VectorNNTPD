using System.Text;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Session.SpeedTest;
using VectorNNTPD.TransitService.Tests.Transit;
using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.Tests.Session.SpeedTest;

public sealed class SpeedTestCoordinatorTests
{
    [Fact]
    public void TryResolvePeer_ExactOrdinalMatch_Only()
    {
        var store = StoreWithPeer("GIGANEWS");
        var coordinator = SpeedTestCoordinator.Create(store, Limits());

        Assert.True(coordinator.TryResolvePeer("GIGANEWS"u8, out var peer));
        Assert.Equal("GIGANEWS", peer!.Identifier);
        Assert.Equal(TransitTestPeers.DefaultPeerDisplayName, peer.PeerName);
        Assert.False(coordinator.TryResolvePeer("giganews"u8, out _));
        Assert.False(coordinator.TryResolvePeer("news.example.com"u8, out _));
        Assert.False(coordinator.TryResolvePeer("192.0.2.1"u8, out _));
        Assert.False(coordinator.TryResolvePeer("GIGANEWS:119"u8, out _));
    }

    [Fact]
    public void TryResolvePeer_UsesCurrentSnapshot_NotAStaleReference()
    {
        var store = StoreWithPeer("GIGANEWS");
        var coordinator = SpeedTestCoordinator.Create(store, Limits());
        Assert.True(coordinator.TryResolvePeer("GIGANEWS"u8, out _));

        store.Replace(TransitTestPeers.Snapshot("INVISION", TransitTestPeers.Peer(allowFrom: ["127.0.0.1"])));
        Assert.False(coordinator.TryResolvePeer("GIGANEWS"u8, out _));
        Assert.True(coordinator.TryResolvePeer("INVISION"u8, out var peer));
        Assert.Equal("INVISION", peer!.Identifier);
    }

    [Fact]
    public void TryAcquire_EnforcesGlobalAndPerPeerLimits_AndReleases()
    {
        var store = StoreWithPeer("GIGANEWS");
        store.Replace(
            TransitConfigurationSnapshot.Create(
                new TransitPeersOptions
                {
                    ["GIGANEWS"] = TransitTestPeers.Peer(allowFrom: ["127.0.0.1"]),
                    ["INVISION"] = TransitTestPeers.Peer(allowFrom: ["127.0.0.2"]),
                }));
        var coordinator = SpeedTestCoordinator.Create(
            store,
            Limits(maxConcurrent: 2, maxPerPeer: 1));

        Assert.True(coordinator.TryAcquire("GIGANEWS", out var first));
        Assert.False(coordinator.TryAcquire("GIGANEWS", out _));
        Assert.True(coordinator.TryAcquire("INVISION", out var second));
        Assert.False(coordinator.TryAcquire("INVISION", out _));
        Assert.Equal(2, coordinator.GlobalInFlight);

        first!.Dispose();
        first.Dispose();
        Assert.Equal(1, coordinator.GlobalInFlight);
        Assert.Equal(0, coordinator.InFlightFor("GIGANEWS"));

        Assert.True(coordinator.TryAcquire("GIGANEWS", out var again));
        second!.Dispose();
        again!.Dispose();
        Assert.Equal(0, coordinator.GlobalInFlight);
    }

    [Fact]
    public void NameEquals_IsExactOrdinal()
    {
        Assert.True(SpeedTestCoordinator.NameEquals("GIGANEWS", "GIGANEWS"u8));
        Assert.False(SpeedTestCoordinator.NameEquals("GIGANEWS", "giganews"u8));
        Assert.False(SpeedTestCoordinator.NameEquals("GIGANEWS", "GIGANEW"u8));
    }

    private static TransitConfigurationStore StoreWithPeer(string name)
    {
        var store = new TransitConfigurationStore();
        store.Replace(TransitTestPeers.Snapshot(name, TransitTestPeers.Peer(allowFrom: ["127.0.0.1"])));
        return store;
    }

    private static NntpdOptions Limits(int maxConcurrent = 2, int maxPerPeer = 1) =>
        new()
        {
            SpeedTest = new SpeedTestOptions
            {
                MaxBytes = 4096,
                MaxDurationSeconds = 10,
                MaxConcurrent = maxConcurrent,
                MaxConcurrentPerPeer = maxPerPeer,
            },
        };
}

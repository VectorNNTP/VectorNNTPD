using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.Transit;

namespace VectorNNTPD.ReaderService.Tests.Transit;

internal static class TransitPeerStateTestFactory
{
    public static (TransitInboundConnectionLimiter Limiter, DistributedTransitPeerStateTracker Tracker, InMemoryTransitPeerStateStore Membership)
        CreateLimiter(TransitConfigurationStore store, string nodeId = "nntpd01", string incarnation = "test")
    {
        var membership = new InMemoryTransitPeerStateStore();
        var tracker = CreateTracker(membership, nodeId, incarnation);
        return (new TransitInboundConnectionLimiter(store, tracker), tracker, membership);
    }

    public static DistributedTransitPeerStateTracker CreateTracker(
        ITransitPeerStateStore membership,
        string nodeId = "nntpd01",
        string incarnation = "a",
        TimeProvider? time = null,
        TimeSpan? leaseTtl = null) =>
        new(
            membership,
            NullLogger<DistributedTransitPeerStateTracker>.Instance,
            nodeId,
            time ?? TimeProvider.System,
            leaseTtl,
            incarnation);

    public static long NowMs(TimeProvider? time = null) =>
        (time ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
}

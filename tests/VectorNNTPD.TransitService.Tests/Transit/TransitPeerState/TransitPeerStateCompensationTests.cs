using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.Tests.Transit;

public sealed class TransitPeerStateCompensationTests
{
    private const string Peer = "peer-a";

    [Fact]
    public async Task SameIdentifierAdmission_WaitsUntilCompensationReleasesGate()
    {
        var inner = new InMemoryTransitPeerStateStore();
        var store = new CompensationStore(inner);
        var node = TransitPeerStateTestFactory.CreateTracker(store);
        using var cts = new CancellationTokenSource();
        var admitA = node.TryAdmitAsync(Peer, 5, cts.Token).AsTask();
        await store.Admitted.Task;
        Assert.Equal(1, inner.ActiveCount(Peer, TransitPeerStateTestFactory.NowMs()));

        var admitB = node.TryAdmitAsync(Peer, 5).AsTask();
        Assert.False(admitB.IsCompleted);

        Assert.True(inner.Engine.TryGetOwnership(
            InMemoryTransitPeerStateStore.ConnectionKey(Peer),
            node.OwnerId,
            out _,
            out var canceledGeneration,
            out var canceledCount));
        Assert.Equal(1, canceledCount);

        cts.Cancel();
        store.Continue.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admitA);
        var admitted = await admitB;
        Assert.True(admitted.Accepted);
        Assert.Equal(1, node.GetLocalCount(Peer));
        Assert.Equal(1, inner.ActiveCount(Peer, TransitPeerStateTestFactory.NowMs()));
        Assert.Equal(node.OwnerId, store.LastReleaseOwnerId);
        Assert.Equal(canceledGeneration, store.LastReleaseGeneration);
        Assert.Equal(CancellationToken.None, store.LastReleaseToken);
        Assert.True(inner.Engine.TryGetOwnership(
            InMemoryTransitPeerStateStore.ConnectionKey(Peer),
            node.OwnerId,
            out _,
            out var liveGeneration,
            out _));
        Assert.NotEqual(canceledGeneration, liveGeneration);
    }

    [Fact]
    public async Task CanceledCallerToken_IsNotUsedForCompensatingRelease()
    {
        var inner = new InMemoryTransitPeerStateStore();
        var store = new CompensationStore(inner);
        var node = TransitPeerStateTestFactory.CreateTracker(store);
        using var cts = new CancellationTokenSource();
        var admit = node.TryAdmitAsync(Peer, 2, cts.Token).AsTask();
        await store.Admitted.Task;
        cts.Cancel();
        store.Continue.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admit);
        Assert.Equal(CancellationToken.None, store.LastReleaseToken);
        Assert.False(store.LastReleaseToken.CanBeCanceled);
        Assert.Equal(1, store.ReleaseCalls);
        Assert.Equal(0, inner.ActiveCount(Peer, TransitPeerStateTestFactory.NowMs()));
        Assert.Equal(0, node.GetLocalCount(Peer));
        Assert.Equal(0, node.DistributedAdmits);
    }

    [Fact]
    public async Task CompensationWhenRedisUnavailable_LeavesTtlOwnershipAndNoLocalCommit()
    {
        var inner = new InMemoryTransitPeerStateStore();
        var store = new CompensationStore(inner) { UnavailableOnRelease = true };
        var node = TransitPeerStateTestFactory.CreateTracker(store);
        using var cts = new CancellationTokenSource();
        var admit = node.TryAdmitAsync(Peer, 2, cts.Token).AsTask();
        await store.Admitted.Task;
        cts.Cancel();
        store.Continue.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admit);
        Assert.Equal(0, node.GetLocalCount(Peer));
        Assert.Equal(0, node.DistributedAdmits);
        Assert.Equal(1, inner.ActiveCount(Peer, TransitPeerStateTestFactory.NowMs()));
        Assert.Equal(1, store.ReleaseCalls);
    }

    [Fact]
    public async Task NonCanceledStoreException_DoesNotCompensate()
    {
        var inner = new InMemoryTransitPeerStateStore();
        var store = new CompensationStore(inner) { ThrowAfterAdmit = new InvalidOperationException("store fault") };
        var node = TransitPeerStateTestFactory.CreateTracker(store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => node.TryAdmitAsync(Peer, 2).AsTask());
        Assert.Equal(0, store.ReleaseCalls);
        Assert.Equal(0, node.GetLocalCount(Peer));
    }

    [Fact]
    public async Task CompensationRelease_DoesNotAffectNewerGeneration()
    {
        var inner = new InMemoryTransitPeerStateStore();
        var store = new CompensationStore(inner);
        var node = TransitPeerStateTestFactory.CreateTracker(store);
        using var cts = new CancellationTokenSource();
        var admitA = node.TryAdmitAsync(Peer, 5, cts.Token).AsTask();
        await store.Admitted.Task;
        Assert.True(inner.Engine.TryGetOwnership(
            InMemoryTransitPeerStateStore.ConnectionKey(Peer),
            node.OwnerId,
            out _,
            out var canceledGeneration,
            out _));

        cts.Cancel();
        store.Continue.TrySetResult();
        var admitB = node.TryAdmitAsync(Peer, 5).AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admitA);
        Assert.True((await admitB).Accepted);
        Assert.Equal(canceledGeneration, store.LastReleaseGeneration);
        Assert.True(inner.Engine.TryGetOwnership(
            InMemoryTransitPeerStateStore.ConnectionKey(Peer),
            node.OwnerId,
            out _,
            out var liveGeneration,
            out var liveCount));
        Assert.NotEqual(canceledGeneration, liveGeneration);
        Assert.Equal(1, liveCount);
    }

    private sealed class CompensationStore : ITransitPeerStateStore
    {
        private readonly InMemoryTransitPeerStateStore _inner;

        public CompensationStore(InMemoryTransitPeerStateStore inner)
        {
            _inner = inner;
        }

        public TaskCompletionSource Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool UnavailableOnRelease { get; set; }

        public Exception? ThrowAfterAdmit { get; set; }

        public int ReleaseCalls { get; private set; }

        public string? LastReleaseOwnerId { get; private set; }

        public long LastReleaseGeneration { get; private set; }

        public CancellationToken LastReleaseToken { get; private set; } = new(true);

        public async ValueTask<TransitPeerStateAdmitResult> TryAdmitAsync(
            string identifier,
            string ownerId,
            int maxIncoming,
            long generation,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            CancellationToken cancellationToken = default)
        {
            var result = await _inner.TryAdmitAsync(
                identifier,
                ownerId,
                maxIncoming,
                generation,
                now,
                leaseTtl,
                CancellationToken.None).ConfigureAwait(false);
            if (ThrowAfterAdmit is { } fault)
            {
                throw fault;
            }

            Admitted.TrySetResult();
            await Continue.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        public async ValueTask ReleaseAsync(
            string identifier,
            string ownerId,
            long generation,
            CancellationToken cancellationToken = default)
        {
            ReleaseCalls++;
            LastReleaseOwnerId = ownerId;
            LastReleaseGeneration = generation;
            LastReleaseToken = cancellationToken;
            if (UnavailableOnRelease)
            {
                return;
            }

            await _inner.ReleaseAsync(identifier, ownerId, generation, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<TransitPeerStateRenewStatus> RenewAsync(
            string identifier,
            string ownerId,
            long generation,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            CancellationToken cancellationToken = default) =>
            _inner.RenewAsync(identifier, ownerId, generation, now, leaseTtl, cancellationToken);

        public ValueTask ReleaseOwnerAsync(
            string identifier,
            string ownerId,
            CancellationToken cancellationToken = default) =>
            _inner.ReleaseOwnerAsync(identifier, ownerId, cancellationToken);
    }
}

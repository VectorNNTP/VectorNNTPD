using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.Tests.Fixtures;

namespace VectorNNTPD.ReaderService.Tests.SessionState;

/// <summary>
/// Deterministic races at the local-tracker / Redis-membership boundary.
/// </summary>
public sealed class SessionStateConcurrencyBoundaryTests
{
    private static readonly IPAddress V4A = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress V4B = IPAddress.Parse("198.51.100.20");
    private static readonly IPAddress V4C = IPAddress.Parse("203.0.113.30");
    private static readonly IPAddress V6A = IPAddress.Parse("2001:db8::10");

    [Fact]
    public async Task RedisAdmitSuccess_ThenCancelBeforeLocalCommit_ReleasesDistributedOwnership()
    {
        var inner = new InMemorySessionStateStore();
        var store = new HoldAfterAdmitStore(inner);
        var node = CreateNode(store);
        using var cts = new CancellationTokenSource();
        var admit = node.TryAdmitAsync("alice", "s1", V4A, sessionLimit: 2, srcIpLimit: 2, cts.Token).AsTask();
        await store.Admitted.Task;
        Assert.Equal(1, inner.ActiveSessionCount("alice", NowMs()));
        Assert.Equal(0, node.GetLocalSessionCount("alice"));

        cts.Cancel();
        store.Continue.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admit);
        Assert.Equal(0, node.GetLocalSessionCount("alice"));
        Assert.Equal(0, inner.ActiveSessionCount("alice", NowMs()));
        Assert.False(inner.HasSourceOwner("alice", "192.0.2.10", node.OwnerId, NowMs()));
        Assert.Equal(1, store.ReleaseCalls);
    }

    [Fact]
    public async Task RedisReject_DoesNotCreateLocalSession()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode(membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "held", V4A, sessionLimit: 1, srcIpLimit: 1));
        Assert.Equal(
            SessionAdmissionResult.SessionLimitExceeded,
            await Admit(node, "extra", V4B, sessionLimit: 1, srcIpLimit: 1));
        Assert.Equal(1, node.GetLocalSessionCount("alice"));
        Assert.Equal(1, membership.ActiveSessionCount("alice", NowMs()));
        Assert.False(membership.HasSourceOwner("alice", "198.51.100.20", node.OwnerId, NowMs()));
    }

    [Fact]
    public async Task ReleaseOneOfTwoSameSource_LeavesOwnership_FiniteAndUnlimited()
    {
        await AssertReleaseOneOfTwoSameSource(sessionLimit: 5, srcIpLimit: 1);
        await AssertReleaseOneOfTwoSameSource(sessionLimit: 0, srcIpLimit: 1);
    }

    [Fact]
    public async Task ReleaseOneOfTwoDifferentSources_LeavesTheOther_FiniteAndUnlimited()
    {
        await AssertReleaseOneOfTwoDifferentSources(sessionLimit: 5, srcIpLimit: 2);
        await AssertReleaseOneOfTwoDifferentSources(sessionLimit: 0, srcIpLimit: 2);
    }

    [Fact]
    public async Task ReleaseBlockedAtRedis_ConcurrentSameAccountAdmit_WaitsThenSeesReleasedSlot()
    {
        var membership = new InMemorySessionStateStore
        {
            BlockRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var node = CreateNode(membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 1, srcIpLimit: 2));
        var release = node.ReleaseAsync("alice", "s1").AsTask();
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (node.GetLocalSessionCount("alice") != 0)
        {
            wait.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        Assert.Equal(1, membership.ActiveSessionCount("alice", NowMs()));
        var admit = Admit(node, "s2", V4B, sessionLimit: 1, srcIpLimit: 2).AsTask();
        Assert.False(admit.IsCompleted);
        membership.BlockRelease.TrySetResult();
        await release;
        Assert.Equal(SessionAdmissionResult.Success, await admit);
        Assert.Equal(1, node.GetLocalSessionCount("alice"));
        Assert.Equal(1, membership.ActiveSessionCount("alice", NowMs()));
        Assert.False(membership.HasSourceOwner("alice", "192.0.2.10", node.OwnerId, NowMs()));
        Assert.True(membership.HasSourceOwner("alice", "198.51.100.20", node.OwnerId, NowMs()));
    }

    [Fact]
    public async Task ReleaseBlockedAtRedis_ConcurrentSameSourceAdmit_KeepsSourceMembership()
    {
        var membership = new InMemorySessionStateStore
        {
            BlockRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var node = CreateNode(membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 5, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V4A, sessionLimit: 5, srcIpLimit: 1));
        var release = node.ReleaseAsync("alice", "s1").AsTask();
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (node.GetLocalSessionCount("alice") != 1)
        {
            wait.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        var admit = Admit(node, "s3", V4A, sessionLimit: 5, srcIpLimit: 1).AsTask();
        membership.BlockRelease.TrySetResult();
        await release;
        Assert.Equal(SessionAdmissionResult.Success, await admit);
        Assert.Equal(2, node.GetLocalSessionCount("alice"));
        Assert.Equal(2, membership.ActiveSessionCount("alice", NowMs()));
        Assert.Equal(["192.0.2.10"], membership.ActiveSourceIps("alice", NowMs()));
    }

    [Fact]
    public async Task RenewSnapshotThenRelease_DoesNotResurrect()
    {
        var membership = new InMemorySessionStateStore
        {
            BlockRenew = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            NotifyRenewStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var node = CreateNode(membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 2, srcIpLimit: 1));
        var renew = node.RenewLeasesAsync().AsTask();
        await membership.NotifyRenewStarted.Task;

        await node.ReleaseAsync("alice", "s1");
        Assert.Equal(0, node.GetLocalSessionCount("alice"));
        Assert.Equal(0, membership.ActiveSessionCount("alice", NowMs()));
        membership.BlockRenew.TrySetResult();
        await renew;
        Assert.Equal(0, membership.ActiveSessionCount("alice", NowMs()));
        Assert.Null(membership.SessionExpiry("alice", node.OwnerId));
        Assert.False(membership.HasSourceOwner("alice", "192.0.2.10", node.OwnerId, NowMs()));
    }

    [Fact]
    public async Task RenewSnapshotThenReleaseOneOfTwo_KeepsRemainingOwnership()
    {
        var membership = new InMemorySessionStateStore
        {
            BlockRenew = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            NotifyRenewStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var clock = new ControllableTimeProvider();
        var node = CreateNode(membership, clock);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V4B, sessionLimit: 5, srcIpLimit: 2));
        var renew = node.RenewLeasesAsync().AsTask();
        await membership.NotifyRenewStarted.Task;

        await node.ReleaseAsync("alice", "s1");
        membership.BlockRenew.TrySetResult();
        await renew;
        Assert.Equal(1, membership.ActiveSessionCount("alice", clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.False(membership.HasSourceOwner("alice", "192.0.2.10", node.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.True(membership.HasSourceOwner("alice", "198.51.100.20", node.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task LaterFiniteSessionLimit_DoesNotUseExistingSourceHotPath()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode(membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 0, srcIpLimit: 1));
        Assert.Equal(1, node.DistributedAdmits);
        membership.Unavailable = true;
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V4A, sessionLimit: 0, srcIpLimit: 1));
        Assert.Equal(1, node.LocalHotPathAdmits);
        Assert.Equal(SessionAdmissionResult.Unavailable, await Admit(node, "s3", V4A, sessionLimit: 5, srcIpLimit: 1));
        Assert.Equal(1, node.LocalHotPathAdmits);
        Assert.Equal(2, node.GetLocalSessionCount("alice"));
    }

    [Fact]
    public async Task SameNode_SessionLimit_ExactlyNOfNPlusOne()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode(membership);
        var results = new SessionAdmissionResult[4];
        var ips = new[] { V4A, V4B, V4C, V6A };
        Parallel.Invoke(
            () => results[0] = Admit(node, "s0", ips[0], sessionLimit: 3, srcIpLimit: 8).AsTask().GetAwaiter().GetResult(),
            () => results[1] = Admit(node, "s1", ips[1], sessionLimit: 3, srcIpLimit: 8).AsTask().GetAwaiter().GetResult(),
            () => results[2] = Admit(node, "s2", ips[2], sessionLimit: 3, srcIpLimit: 8).AsTask().GetAwaiter().GetResult(),
            () => results[3] = Admit(node, "s3", ips[3], sessionLimit: 3, srcIpLimit: 8).AsTask().GetAwaiter().GetResult());
        Assert.Equal(3, results.Count(static r => r == SessionAdmissionResult.Success));
        Assert.Equal(1, results.Count(static r => r == SessionAdmissionResult.SessionLimitExceeded));
        Assert.Equal(3, membership.ActiveSessionCount("alice", NowMs()));
        Assert.Equal(3, node.GetLocalSessionCount("alice"));
    }

    [Fact]
    public async Task SameNode_SourceLimit_ExactlyNDistinctOfNPlusOne()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode(membership);
        var results = new SessionAdmissionResult[4];
        var ips = new[] { V4A, V4B, V4C, V6A };
        Parallel.Invoke(
            () => results[0] = Admit(node, "s0", ips[0], sessionLimit: 10, srcIpLimit: 3).AsTask().GetAwaiter().GetResult(),
            () => results[1] = Admit(node, "s1", ips[1], sessionLimit: 10, srcIpLimit: 3).AsTask().GetAwaiter().GetResult(),
            () => results[2] = Admit(node, "s2", ips[2], sessionLimit: 10, srcIpLimit: 3).AsTask().GetAwaiter().GetResult(),
            () => results[3] = Admit(node, "s3", ips[3], sessionLimit: 10, srcIpLimit: 3).AsTask().GetAwaiter().GetResult());
        Assert.Equal(3, results.Count(static r => r == SessionAdmissionResult.Success));
        Assert.Equal(1, results.Count(static r => r == SessionAdmissionResult.SourceAddressLimitExceeded));
        Assert.Equal(3, membership.ActiveSourceIps("alice", NowMs()).Count);
        Assert.Equal(3, membership.ActiveSessionCount("alice", NowMs()));
    }

    [Fact]
    public async Task SameSourceThreeSessions_ReleaseStepsKeepSourceUntilLast()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode(membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 3, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V4A, sessionLimit: 3, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s3", V4A, sessionLimit: 3, srcIpLimit: 1));
        Assert.Equal(SessionAdmissionResult.SessionLimitExceeded, await Admit(node, "s4", V4A, sessionLimit: 3, srcIpLimit: 1));
        Assert.Equal(3, membership.ActiveSessionCount("alice", NowMs()));
        Assert.Equal(["192.0.2.10"], membership.ActiveSourceIps("alice", NowMs()));

        await node.ReleaseAsync("alice", "s1");
        Assert.Equal(2, membership.ActiveSessionCount("alice", NowMs()));
        Assert.Equal(["192.0.2.10"], membership.ActiveSourceIps("alice", NowMs()));
        await node.ReleaseAsync("alice", "s2");
        Assert.Equal(1, membership.ActiveSessionCount("alice", NowMs()));
        Assert.Equal(["192.0.2.10"], membership.ActiveSourceIps("alice", NowMs()));
        await node.ReleaseAsync("alice", "s3");
        Assert.Equal(0, membership.ActiveSessionCount("alice", NowMs()));
        Assert.Empty(membership.ActiveSourceIps("alice", NowMs()));
    }

    [Fact]
    public async Task TwoSources_SourceLimitIndependentOfSessionRelease()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode(membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "a1", V4A, sessionLimit: 10, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "a2", V4A, sessionLimit: 10, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "b1", V6A, sessionLimit: 10, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.SourceAddressLimitExceeded, await Admit(node, "c1", V4B, sessionLimit: 10, srcIpLimit: 2));
        await node.ReleaseAsync("alice", "a1");
        Assert.Equal(2, membership.ActiveSourceIps("alice", NowMs()).Count);
        await node.ReleaseAsync("alice", "a2");
        Assert.Equal(["2001:db8::10"], membership.ActiveSourceIps("alice", NowMs()));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "c1", V4B, sessionLimit: 10, srcIpLimit: 2));
        Assert.Equal(2, membership.ActiveSourceIps("alice", NowMs()).Count);
    }

    [Fact]
    public async Task CrossNode_CannotReleaseOrRenewPeerOwnership()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var nodeA = CreateNode(membership, clock, "nntpd01", "incA");
        var nodeB = CreateNode(membership, clock, "nntpd02", "incB");
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeA, "a1", V4A, sessionLimit: 5, srcIpLimit: 2));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(nodeB, "b1", V6A, sessionLimit: 5, srcIpLimit: 2));
        var bExpiry = membership.SessionExpiry("alice", nodeB.OwnerId);
        await nodeA.ReleaseAsync("alice", "missing-on-a");
        await nodeA.ReleaseAsync("alice", "b1");
        Assert.Equal(1, membership.OwnerSessionCount("alice", nodeB.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.True(membership.HasSourceOwner("alice", "2001:db8::10", nodeB.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));

        clock.Advance(TimeSpan.FromSeconds(10));
        await nodeA.RenewLeasesAsync();
        Assert.Equal(bExpiry, membership.SessionExpiry("alice", nodeB.OwnerId));
        Assert.True(membership.SessionExpiry("alice", nodeA.OwnerId) > bExpiry);
    }

    [Fact]
    public async Task FailureMatrix_MatchesImplementedPolicy()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode(membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit: 2, srcIpLimit: 1));
        var expiry = membership.SessionExpiry("alice", node.OwnerId);
        await node.RenewLeasesAsync();
        Assert.True(membership.SessionExpiry("alice", node.OwnerId) >= expiry);

        membership.Unavailable = true;
        Assert.Equal(SessionAdmissionResult.Unavailable, await Admit(node, "s2", V4B, sessionLimit: 2, srcIpLimit: 2));
        var frozen = membership.SessionExpiry("alice", node.OwnerId);
        await node.RenewLeasesAsync();
        Assert.Equal(frozen, membership.SessionExpiry("alice", node.OwnerId));
        await node.ReleaseAsync("alice", "s1");
        Assert.Equal(0, node.GetLocalSessionCount("alice"));
        Assert.Equal(frozen, membership.SessionExpiry("alice", node.OwnerId));
        await node.ReleaseAllOwnershipAsync();
        Assert.Equal(frozen, membership.SessionExpiry("alice", node.OwnerId));
    }

    private static async Task AssertReleaseOneOfTwoSameSource(int sessionLimit, int srcIpLimit)
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode(membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit, srcIpLimit));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V4A, sessionLimit, srcIpLimit));
        await node.ReleaseAsync("alice", "s1");
        Assert.Equal(1, node.GetLocalSessionCount("alice"));
        Assert.True(membership.HasSourceOwner("alice", "192.0.2.10", node.OwnerId, NowMs()));
        if (sessionLimit > 0)
        {
            Assert.Equal(1, membership.ActiveSessionCount("alice", NowMs()));
        }

        Assert.Equal(["192.0.2.10"], membership.ActiveSourceIps("alice", NowMs()));
        await node.ReleaseAsync("alice", "s2");
        Assert.Equal(0, node.GetLocalSessionCount("alice"));
        Assert.Empty(membership.ActiveSourceIps("alice", NowMs()));
    }

    private static async Task AssertReleaseOneOfTwoDifferentSources(int sessionLimit, int srcIpLimit)
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode(membership);
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s1", V4A, sessionLimit, srcIpLimit));
        Assert.Equal(SessionAdmissionResult.Success, await Admit(node, "s2", V6A, sessionLimit, srcIpLimit));
        await node.ReleaseAsync("alice", "s1");
        Assert.False(membership.HasSourceOwner("alice", "192.0.2.10", node.OwnerId, NowMs()));
        Assert.True(membership.HasSourceOwner("alice", "2001:db8::10", node.OwnerId, NowMs()));
        Assert.Equal(1, node.GetLocalSessionCount("alice"));
        await node.ReleaseAsync("alice", "s2");
        Assert.Empty(membership.ActiveSourceIps("alice", NowMs()));
    }

    private static ValueTask<SessionAdmissionResult> Admit(
        DistributedSessionStateTracker node,
        string sessionId,
        IPAddress ip,
        int sessionLimit = 0,
        int srcIpLimit = 0) =>
        node.TryAdmitAsync("alice", sessionId, ip, sessionLimit, srcIpLimit);

    private static DistributedSessionStateTracker CreateNode(ISessionStateStore membership) =>
        CreateNode(membership, TimeProvider.System);

    private static DistributedSessionStateTracker CreateNode(
        ISessionStateStore membership,
        TimeProvider time,
        string nodeId = "nntpd01",
        string? incarnation = null) =>
        new(
            membership,
            NullLogger<DistributedSessionStateTracker>.Instance,
            nodeId,
            time,
            incarnation: incarnation);

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private sealed class HoldAfterAdmitStore : ISessionStateStore
    {
        private readonly InMemorySessionStateStore _inner;

        public HoldAfterAdmitStore(InMemorySessionStateStore inner)
        {
            _inner = inner;
        }

        public TaskCompletionSource Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReleaseCalls { get; private set; }

        public async ValueTask<SessionStateAdmitResult> TryAdmitAsync(
            string accountName,
            string normalizedSourceIp,
            string ownerId,
            int sessionLimit,
            int srcIpLimit,
            long sessionGeneration,
            long sourceGeneration,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            CancellationToken cancellationToken = default,
            bool trackSessions = false)
        {
            var result = await _inner.TryAdmitAsync(
                accountName,
                normalizedSourceIp,
                ownerId,
                sessionLimit,
                srcIpLimit,
                sessionGeneration,
                sourceGeneration,
                now,
                leaseTtl,
                cancellationToken,
                trackSessions).ConfigureAwait(false);
            Admitted.TrySetResult();
            await Continue.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }

        public ValueTask<int> ReleaseAsync(
            string accountName,
            string normalizedSourceIp,
            string ownerId,
            long sessionGeneration,
            long sourceGeneration,
            CancellationToken cancellationToken = default)
        {
            ReleaseCalls++;
            return _inner.ReleaseAsync(
                accountName,
                normalizedSourceIp,
                ownerId,
                sessionGeneration,
                sourceGeneration,
                cancellationToken);
        }

        public ValueTask<SessionStateRenewResult> RenewAsync(
            string accountName,
            string ownerId,
            long sessionGeneration,
            IReadOnlyList<(string Ip, long Generation)> sources,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            CancellationToken cancellationToken = default) =>
            _inner.RenewAsync(accountName, ownerId, sessionGeneration, sources, now, leaseTtl, cancellationToken);

        public ValueTask<SessionStateRenewAndApplyResult> RenewAndApplyAsync(
            string accountName,
            string ownerId,
            long sessionGeneration,
            IReadOnlyList<(string Ip, long Generation)> sources,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            string batchId,
            long consumed,
            long mysqlRemainingAfter,
            CancellationToken cancellationToken = default) =>
            _inner.RenewAndApplyAsync(
                accountName,
                ownerId,
                sessionGeneration,
                sources,
                now,
                leaseTtl,
                batchId,
                consumed,
                mysqlRemainingAfter,
                cancellationToken);

        public ValueTask ReleaseOwnerAsync(
            string accountName,
            string ownerId,
            CancellationToken cancellationToken = default) =>
            _inner.ReleaseOwnerAsync(accountName, ownerId, cancellationToken);
    }
}

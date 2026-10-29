using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.ReaderService.Authentication;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.Networking.Transport;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.TestDoubles;

namespace VectorNNTPD.ReaderService.Tests.Authentication;

public sealed class AuthenticatedSessionTeardownTests
{
    private static readonly IPAddress V4A = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress V4B = IPAddress.Parse("198.51.100.20");
    private static readonly IPAddress V6A = IPAddress.Parse("2001:db8::10");

    [Fact]
    public async Task Quit_ReleasesAdmittedSessionOnce()
    {
        var (harness, session, run, membership, node, _) = await StartAuthenticatedAsync();
        await using (harness)
        {
            await harness.WriteClientLineAsync("QUIT");
            Assert.StartsWith("205 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
            await run;
            Assert.Equal(NntpSessionLifetime.Finalized, session.Lifetime);
            Assert.Equal(1, node.ReleaseCalls);
            Assert.Equal(0, membership.ActiveSessionCount("alice", NowMs()));
            await session.FinalizeAdmissionAsync();
            Assert.Equal(1, node.ReleaseCalls);
        }
    }

    [Fact]
    public async Task RemoteEof_ReleasesAdmittedSessionOnce()
    {
        var (harness, session, run, membership, node, _) = await StartAuthenticatedAsync();
        await using (harness)
        {
            await harness.CompleteClientInputAsync();
            await run;
            Assert.Equal(NntpSessionLifetime.Finalized, session.Lifetime);
            Assert.Equal(1, node.ReleaseCalls);
            Assert.Equal(0, membership.OwnerSessionCount("alice", node.OwnerId, NowMs()));
        }
    }

    [Fact]
    public async Task ConnectionReset_ReleasesAdmittedSessionOnce()
    {
        var (harness, session, run, _, node, _) = await StartAuthenticatedAsync();
        await using (harness)
        {
            await harness.CompleteClientInputAsync(new SocketException((int)SocketError.ConnectionReset));
            await run;
            Assert.Equal(NntpSessionLifetime.Finalized, session.Lifetime);
            Assert.Equal(1, node.ReleaseCalls);
        }
    }

    [Fact]
    public async Task SocketException_ReleasesAdmittedSessionOnce()
    {
        var (harness, session, run, _, node, _) = await StartAuthenticatedAsync();
        await using (harness)
        {
            await harness.CompleteClientInputAsync(new SocketException((int)SocketError.ConnectionAborted));
            await run;
            Assert.Equal(NntpSessionLifetime.Finalized, session.Lifetime);
            Assert.Equal(1, node.ReleaseCalls);
        }
    }

    [Fact]
    public async Task IoException_ReleasesAdmittedSessionOnce()
    {
        var (harness, session, run, _, node, _) = await StartAuthenticatedAsync();
        await using (harness)
        {
            await harness.CompleteClientInputAsync(new IOException("peer disappeared"));
            await run;
            Assert.Equal(NntpSessionLifetime.Finalized, session.Lifetime);
            Assert.Equal(1, node.ReleaseCalls);
        }
    }

    [Fact]
    public async Task IdleTimeout_ReleasesAdmittedSessionOnce()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership, clock);
        var record = MemoryNntpUserRecordStore.Create("alice", "secret", sessionLimit: 1);
        await using var harness = await AuthHarness.CreateAsync(record, admission: node, clientIp: V4A);
        var session = harness.CreateSession(
            commandIdleTimeout: TimeSpan.FromSeconds(1),
            timeProvider: clock);
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();
        await harness.AuthenticateAsync("alice", "secret");
        await session.IdleWatchArmed;
        clock.Advance(TimeSpan.FromSeconds(2));
        await run;
        Assert.Equal(TcpDisconnectReason.IdleTimeout, session.CloseReasonForTests);
        Assert.Equal(NntpSessionLifetime.Finalized, session.Lifetime);
        Assert.Equal(1, node.ReleaseCalls);
    }

    [Fact]
    public async Task Cancellation_ReleasesAdmittedSessionOnce()
    {
        var (harness, session, run, _, node, cts) = await StartAuthenticatedAsync();
        await using (harness)
        {
            await cts.CancelAsync();
            await run;
            Assert.Equal(NntpSessionLifetime.Finalized, session.Lifetime);
            Assert.Equal(1, node.ReleaseCalls);
        }
    }

    [Fact]
    public async Task MultipleTeardownSignals_ReleaseOnce()
    {
        var (harness, session, run, _, node, cts) = await StartAuthenticatedAsync();
        await using (harness)
        {
            var quit = harness.WriteClientLineAsync("QUIT");
            await cts.CancelAsync();
            session.RequestClose();
            await session.FinalizeAdmissionAsync();
            await harness.CompleteClientInputAsync(new SocketException((int)SocketError.ConnectionReset));
            await quit;
            await run;
            await session.FinalizeAdmissionAsync();
            Assert.Equal(NntpSessionLifetime.Finalized, session.Lifetime);
            Assert.Equal(1, node.ReleaseCalls);
        }
    }

    [Fact]
    public async Task UnauthenticatedDisconnect_DoesNotRelease()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership);
        var record = MemoryNntpUserRecordStore.Create("alice", "secret", sessionLimit: 1);
        await using var harness = await AuthHarness.CreateAsync(record, admission: node, clientIp: V4A);
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();
        await harness.CompleteClientInputAsync();
        await run;
        Assert.Equal(NntpSessionLifetime.Finalized, session.Lifetime);
        Assert.Equal(0, node.ReleaseCalls);
        Assert.Equal(0, membership.ActiveSessionCount("alice", NowMs()));
    }

    [Fact]
    public async Task AdmissionRejection_LeavesNoOwnership()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership);
        var record = MemoryNntpUserRecordStore.Create("alice", "secret", sessionLimit: 1);
        await using var first = await AuthHarness.CreateAsync(record, admission: node, clientIp: V4A);
        await using var second = await AuthHarness.CreateAsync(record, admission: node, clientIp: V6A);
        var session1 = first.CreateSession();
        var run1 = session1.RunAsync();
        await first.ReadGreetingAsync();
        await first.AuthenticateAsync("alice", "secret");

        var session2 = second.CreateSession();
        var run2 = session2.RunAsync();
        await second.ReadGreetingAsync();
        await second.WriteClientLineAsync("AUTHINFO USER alice");
        await second.ReadClientLineAsync();
        await second.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.Equal("481 Too many sessions", await second.ReadClientLineAsync());
        await second.CompleteClientInputAsync();
        await run2;
        Assert.Equal(1, membership.ActiveSessionCount("alice", NowMs()));
        Assert.False(membership.HasSourceOwner("alice", "2001:db8::10", node.OwnerId, NowMs()));
        Assert.Equal(0, node.ReleaseCalls);

        await first.WriteClientLineAsync("QUIT");
        await first.ReadClientLineAsync();
        await run1;
        Assert.Equal(1, node.ReleaseCalls);
        Assert.Equal(0, membership.ActiveSessionCount("alice", NowMs()));
    }

    [Fact]
    public async Task RedisUnavailableDuringRelease_FinalizesLocallyAndDropsRenewal()
    {
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership, clock);
        var record = MemoryNntpUserRecordStore.Create("alice", "secret", sessionLimit: 1, srcIpLimit: 1);
        await using var harness = await AuthHarness.CreateAsync(record, admission: node, clientIp: V4A);
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();
        await harness.AuthenticateAsync("alice", "secret");
        Assert.Equal(1, membership.OwnerSessionCount("alice", node.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));

        membership.Unavailable = true;
        await harness.CompleteClientInputAsync();
        await run;
        Assert.Equal(NntpSessionLifetime.Finalized, session.Lifetime);
        Assert.Equal(0, node.GetLocalSessionCount("alice"));
        Assert.Equal(1, node.ReleaseCalls);
        Assert.Equal(1, membership.OwnerSessionCount("alice", node.OwnerId, clock.GetUtcNow().ToUnixTimeMilliseconds()));

        await node.RenewLeasesAsync();
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(0, membership.ActiveSessionCount("alice", clock.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task RenewalConcurrentWithFinalization_DoesNotResurrectOwnership()
    {
        var membership = new InMemorySessionStateStore
        {
            BlockRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var node = CreateNode("nntpd01", membership);
        var record = MemoryNntpUserRecordStore.Create("alice", "secret", sessionLimit: 1, srcIpLimit: 1);
        await using var harness = await AuthHarness.CreateAsync(record, admission: node, clientIp: V4A);
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();
        await harness.AuthenticateAsync("alice", "secret");

        var eof = harness.CompleteClientInputAsync();
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (node.GetLocalSessionCount("alice") != 0)
        {
            wait.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        var renew = node.RenewLeasesAsync().AsTask();
        membership.BlockRelease.TrySetResult();
        await eof;
        await run;
        await renew;
        Assert.Equal(0, membership.ActiveSessionCount("alice", NowMs()));
        Assert.Null(membership.SessionExpiry("alice", node.OwnerId));
        Assert.Equal(1, node.ReleaseCalls);
    }

    [Fact]
    public async Task SameIpMultipleSessions_ReleaseDecrementsUntilSourceGone()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership);
        var record = MemoryNntpUserRecordStore.Create("alice", "secret", sessionLimit: 5, srcIpLimit: 2);
        await using var a = await AuthHarness.CreateAsync(record, admission: node, clientIp: V4A);
        await using var b = await AuthHarness.CreateAsync(record, admission: node, clientIp: V4A);
        await using var c = await AuthHarness.CreateAsync(record, admission: node, clientIp: V4A);
        var (s1, r1) = await AuthAsync(a);
        var (s2, r2) = await AuthAsync(b);
        var (s3, r3) = await AuthAsync(c);
        Assert.Equal(3, membership.OwnerSessionCount("alice", node.OwnerId, NowMs()));

        await a.CompleteClientInputAsync();
        await r1;
        Assert.Equal(2, membership.OwnerSessionCount("alice", node.OwnerId, NowMs()));
        Assert.True(membership.HasSourceOwner("alice", "192.0.2.10", node.OwnerId, NowMs()));

        await b.CompleteClientInputAsync();
        await r2;
        Assert.Equal(1, membership.OwnerSessionCount("alice", node.OwnerId, NowMs()));
        Assert.True(membership.HasSourceOwner("alice", "192.0.2.10", node.OwnerId, NowMs()));

        await c.CompleteClientInputAsync();
        await r3;
        Assert.Equal(0, membership.OwnerSessionCount("alice", node.OwnerId, NowMs()));
        Assert.False(membership.HasSourceOwner("alice", "192.0.2.10", node.OwnerId, NowMs()));
        Assert.Equal(3, node.ReleaseCalls);
        Assert.Equal(NntpSessionLifetime.Finalized, s1.Lifetime);
        Assert.Equal(NntpSessionLifetime.Finalized, s2.Lifetime);
        Assert.Equal(NntpSessionLifetime.Finalized, s3.Lifetime);
    }

    [Fact]
    public async Task ReleasingOneSourceIp_LeavesTheOther()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership);
        var record = MemoryNntpUserRecordStore.Create("alice", "secret", sessionLimit: 5, srcIpLimit: 2);
        await using var a = await AuthHarness.CreateAsync(record, admission: node, clientIp: V4A);
        await using var b = await AuthHarness.CreateAsync(record, admission: node, clientIp: V6A);
        var (_, r1) = await AuthAsync(a);
        var (_, r2) = await AuthAsync(b);

        await a.CompleteClientInputAsync();
        await r1;
        Assert.False(membership.HasSourceOwner("alice", "192.0.2.10", node.OwnerId, NowMs()));
        Assert.True(membership.HasSourceOwner("alice", "2001:db8::10", node.OwnerId, NowMs()));
        Assert.Equal(1, membership.OwnerSessionCount("alice", node.OwnerId, NowMs()));

        await b.CompleteClientInputAsync();
        await r2;
        Assert.Equal(0, membership.ActiveSessionCount("alice", NowMs()));
    }

    [Fact]
    public async Task ReleasingOneNode_DoesNotAffectTheOther()
    {
        var membership = new InMemorySessionStateStore();
        var node1 = CreateNode("nntpd01", membership);
        var node2 = CreateNode("nntpd02", membership);
        var record = MemoryNntpUserRecordStore.Create("alice", "secret", sessionLimit: 5, srcIpLimit: 2);
        await using var a = await AuthHarness.CreateAsync(record, admission: node1, clientIp: V4A);
        await using var b = await AuthHarness.CreateAsync(record, admission: node2, clientIp: V6A);
        var (_, r1) = await AuthAsync(a);
        var (_, r2) = await AuthAsync(b);

        await a.CompleteClientInputAsync();
        await r1;
        Assert.Equal(0, membership.OwnerSessionCount("alice", node1.OwnerId, NowMs()));
        Assert.Equal(1, membership.OwnerSessionCount("alice", node2.OwnerId, NowMs()));
        Assert.True(membership.HasSourceOwner("alice", "2001:db8::10", node2.OwnerId, NowMs()));

        await b.CompleteClientInputAsync();
        await r2;
    }

    [Fact]
    public async Task ReleaseAndAdmitRace_PreservesExactCapacity()
    {
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership);
        var record = MemoryNntpUserRecordStore.Create("alice", "secret", sessionLimit: 1);
        await using var first = await AuthHarness.CreateAsync(record, admission: node, clientIp: V4A);
        await using var second = await AuthHarness.CreateAsync(record, admission: node, clientIp: V4B);
        var (_, run1) = await AuthAsync(first);
        var session2 = second.CreateSession();
        var run2 = session2.RunAsync();
        await second.ReadGreetingAsync();

        var eof = first.CompleteClientInputAsync();
        await second.WriteClientLineAsync("AUTHINFO USER alice");
        await second.ReadClientLineAsync();
        await second.WriteClientLineAsync("AUTHINFO PASS secret");
        var secondLine = await second.ReadClientLineAsync();
        await eof;
        await run1;
        if (secondLine.StartsWith("481 ", StringComparison.Ordinal))
        {
            await second.WriteClientLineAsync("AUTHINFO USER alice");
            await second.ReadClientLineAsync();
            await second.WriteClientLineAsync("AUTHINFO PASS secret");
            Assert.StartsWith("281 ", await second.ReadClientLineAsync(), StringComparison.Ordinal);
        }
        else
        {
            Assert.StartsWith("281 ", secondLine, StringComparison.Ordinal);
        }

        Assert.Equal(1, membership.ActiveSessionCount("alice", NowMs()));
        await second.CompleteClientInputAsync();
        await run2;
        Assert.Equal(0, membership.ActiveSessionCount("alice", NowMs()));
    }

    private static async Task<(AuthHarness Harness, NntpSession Session, Task Run, InMemorySessionStateStore Membership, DistributedSessionStateTracker Node, CancellationTokenSource Cts)>
        StartAuthenticatedAsync()
    {
        // codeql[cs/local-not-disposed] CancellationTokenSource ownership transfers to the caller via the returned tuple.
        var cts = new CancellationTokenSource();
        var membership = new InMemorySessionStateStore();
        var node = CreateNode("nntpd01", membership);
        var record = MemoryNntpUserRecordStore.Create("alice", "secret", sessionLimit: 2, srcIpLimit: 2);
        var harness = await AuthHarness.CreateAsync(record, admission: node, clientIp: V4A);
        var session = harness.CreateSession();
        var run = session.RunAsync(cts.Token);
        await harness.ReadGreetingAsync();
        await harness.AuthenticateAsync("alice", "secret");
        return (harness, session, run, membership, node, cts);
    }

    private static async Task<(NntpSession Session, Task Run)> AuthAsync(AuthHarness harness)
    {
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();
        await harness.AuthenticateAsync("alice", "secret");
        return (session, run);
    }

    private static DistributedSessionStateTracker CreateNode(
        string nodeId,
        InMemorySessionStateStore membership,
        TimeProvider? time = null) =>
        new(
            membership,
            NullLogger<DistributedSessionStateTracker>.Instance,
            nodeId,
            time ?? TimeProvider.System);

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

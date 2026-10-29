using System.Net;
using VectorNNTPD.ReaderService.SessionState;

namespace VectorNNTPD.ReaderService.Tests.SessionState;

public sealed class SessionStateTrackerTests
{
    [Fact]
    public void ZeroLimits_AreUnlimited()
    {
        var tracker = new InMemorySessionStateTracker();
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(
                SessionAdmissionResult.Success,
                tracker.TryAdmit("alice", $"s{i}", IPAddress.Parse("203.0.113.10"), sessionLimit: 0, srcIpLimit: 0));
        }
    }

    [Fact]
    public void SessionLimit_IsAtomicAndExcludesReleasedSlots()
    {
        var tracker = new InMemorySessionStateTracker();
        Assert.Equal(
            SessionAdmissionResult.Success,
            tracker.TryAdmit("alice", "a", IPAddress.Loopback, sessionLimit: 1, srcIpLimit: 0));
        Assert.Equal(
            SessionAdmissionResult.SessionLimitExceeded,
            tracker.TryAdmit("alice", "b", IPAddress.Loopback, sessionLimit: 1, srcIpLimit: 0));
        tracker.Release("alice", "a");
        Assert.Equal(
            SessionAdmissionResult.Success,
            tracker.TryAdmit("alice", "b", IPAddress.Loopback, sessionLimit: 1, srcIpLimit: 0));
    }

    [Fact]
    public void SourceIpLimit_CountsDistinctAddresses_NotSessionsOnSameIp()
    {
        var tracker = new InMemorySessionStateTracker();
        var ip = IPAddress.Parse("2001:db8::1");
        Assert.Equal(
            SessionAdmissionResult.Success,
            tracker.TryAdmit("alice", "a", ip, sessionLimit: 0, srcIpLimit: 1));
        Assert.Equal(
            SessionAdmissionResult.Success,
            tracker.TryAdmit("alice", "b", ip, sessionLimit: 0, srcIpLimit: 1));
        Assert.Equal(
            SessionAdmissionResult.SourceAddressLimitExceeded,
            tracker.TryAdmit("alice", "c", IPAddress.Parse("2001:db8::2"), sessionLimit: 0, srcIpLimit: 1));
    }

    [Fact]
    public void Ipv4MappedAddress_CountsAsIpv4()
    {
        var tracker = new InMemorySessionStateTracker();
        var v4 = IPAddress.Parse("192.0.2.10");
        var mapped = IPAddress.Parse("::ffff:192.0.2.10");
        Assert.Equal(
            SessionAdmissionResult.Success,
            tracker.TryAdmit("alice", "a", v4, sessionLimit: 0, srcIpLimit: 1));
        Assert.Equal(
            SessionAdmissionResult.Success,
            tracker.TryAdmit("alice", "b", mapped, sessionLimit: 0, srcIpLimit: 1));
        Assert.Equal(
            SessionAdmissionResult.SourceAddressLimitExceeded,
            tracker.TryAdmit("alice", "c", IPAddress.Parse("192.0.2.11"), sessionLimit: 0, srcIpLimit: 1));
    }

    [Fact]
    public void ConcurrentAdmit_RespectsSessionLimit()
    {
        var tracker = new InMemorySessionStateTracker();
        var success = 0;
        Parallel.For(0, 32, i =>
        {
            if (tracker.TryAdmit("alice", $"s{i}", IPAddress.Loopback, sessionLimit: 2, srcIpLimit: 0)
                == SessionAdmissionResult.Success)
            {
                Interlocked.Increment(ref success);
            }
        });

        Assert.Equal(2, success);
    }

    [Fact]
    public void CompressedAndExpandedIpv6_AreOneSource()
    {
        var tracker = new InMemorySessionStateTracker();
        var compressed = IPAddress.Parse("2001:db8::10");
        var expanded = IPAddress.Parse("2001:0db8:0000:0000:0000:0000:0000:0010");
        Assert.Equal(
            SessionAdmissionResult.Success,
            tracker.TryAdmit("alice", "a", compressed, sessionLimit: 0, srcIpLimit: 1));
        Assert.Equal(
            SessionAdmissionResult.Success,
            tracker.TryAdmit("alice", "b", expanded, sessionLimit: 0, srcIpLimit: 1));
        Assert.Equal(
            SessionAdmissionResult.SourceAddressLimitExceeded,
            tracker.TryAdmit("alice", "c", IPAddress.Parse("2001:db8::11"), sessionLimit: 0, srcIpLimit: 1));
    }

    [Fact]
    public void Ipv4AndIpv6_AreDistinctSources()
    {
        var tracker = new InMemorySessionStateTracker();
        Assert.Equal(
            SessionAdmissionResult.Success,
            tracker.TryAdmit("alice", "a", IPAddress.Parse("192.0.2.10"), sessionLimit: 0, srcIpLimit: 1));
        Assert.Equal(
            SessionAdmissionResult.SourceAddressLimitExceeded,
            tracker.TryAdmit("alice", "b", IPAddress.Parse("2001:db8::10"), sessionLimit: 0, srcIpLimit: 1));
    }

    [Fact]
    public void AccountsAreIsolated()
    {
        var tracker = new InMemorySessionStateTracker();
        Assert.Equal(
            SessionAdmissionResult.Success,
            tracker.TryAdmit("alice", "a", IPAddress.Loopback, sessionLimit: 1, srcIpLimit: 1));
        Assert.Equal(
            SessionAdmissionResult.Success,
            tracker.TryAdmit("bob", "b", IPAddress.Loopback, sessionLimit: 1, srcIpLimit: 1));
    }
}

using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.Tests.Transit;

public sealed class TransitAllowFromAndDnsTests
{
    [Fact]
    public void LiteralIpv4Address_MatchesExactly()
    {
        var authz = Create("192.0.2.10");
        Assert.Equal("peer", authz.Resolve(IPAddress.Parse("192.0.2.10")).TransitPeerName);
        Assert.Null(authz.Resolve(IPAddress.Parse("192.0.2.11")).TransitPeerName);
    }

    [Fact]
    public void LiteralIpv4Cidr_MatchesPrefix()
    {
        var authz = Create("192.0.2.0/24");
        Assert.Equal("peer", authz.Resolve(IPAddress.Parse("192.0.2.200")).TransitPeerName);
        Assert.Null(authz.Resolve(IPAddress.Parse("198.51.100.1")).TransitPeerName);
    }

    [Fact]
    public void LiteralIpv6AddressAndCidr_Match()
    {
        var authz = Create("2001:db8::10", "2001:db8:1234::/48");
        Assert.Equal("peer", authz.Resolve(IPAddress.Parse("2001:db8::10")).TransitPeerName);
        Assert.Equal("peer", authz.Resolve(IPAddress.Parse("2001:db8:1234::abcd")).TransitPeerName);
        Assert.Null(authz.Resolve(IPAddress.Parse("2001:db8:9999::1")).TransitPeerName);
    }

    [Fact]
    public void PrivateAndDocumentationAddresses_AreNotRejected()
    {
        var authz = Create("10.0.0.0/8", "192.168.1.5", "fd00::/8", "198.18.0.70");
        Assert.Equal("peer", authz.Resolve(IPAddress.Parse("10.1.2.3")).TransitPeerName);
        Assert.Equal("peer", authz.Resolve(IPAddress.Parse("192.168.1.5")).TransitPeerName);
        Assert.Equal("peer", authz.Resolve(IPAddress.Parse("fd00::1")).TransitPeerName);
        Assert.Equal("peer", authz.Resolve(IPAddress.Parse("198.18.0.70")).TransitPeerName);
    }

    [Fact]
    public async Task DnsHostname_AuthorizesAllResolvedAddresses()
    {
        var resolver = new FakeTransitDnsResolver
        {
            Handler = _ => TransitDnsResolveResult.Success(
                [IPAddress.Parse("192.0.2.10"), IPAddress.Parse("2001:db8::10")],
                TimeSpan.FromMinutes(5)),
        };
        var cache = new TransitDnsAddressCache(resolver, NullLogger<TransitDnsAddressCache>.Instance);
        var snapshot = TransitTestPeers.Snapshot("peer", TransitTestPeers.Peer(allowFrom: ["news.example.net"]));
        await cache.RefreshAllAsync(snapshot, CancellationToken.None);
        var authz = TransitPeerAuthorization.CreateStatic(snapshot, cache);
        Assert.Equal("peer", authz.Resolve(IPAddress.Parse("192.0.2.10")).TransitPeerName);
        Assert.Equal("peer", authz.Resolve(IPAddress.Parse("2001:db8::10")).TransitPeerName);
        Assert.Null(authz.Resolve(IPAddress.Parse("198.51.100.1")).TransitPeerName);
    }

    [Fact]
    public async Task MixedDnsAndLiterals_AuthorizeTogether()
    {
        var resolver = new FakeTransitDnsResolver
        {
            Handler = _ => TransitDnsResolveResult.Success([IPAddress.Parse("203.0.113.5")], TimeSpan.FromMinutes(5)),
        };
        var cache = new TransitDnsAddressCache(resolver, NullLogger<TransitDnsAddressCache>.Instance);
        var snapshot = TransitTestPeers.Snapshot(
            "peer",
            TransitTestPeers.Peer(allowFrom: ["news.example.net", "192.0.2.10", "2001:db8::10"]));
        await cache.RefreshAllAsync(snapshot, CancellationToken.None);
        var authz = TransitPeerAuthorization.CreateStatic(snapshot, cache);
        Assert.Equal("peer", authz.Resolve(IPAddress.Parse("203.0.113.5")).TransitPeerName);
        Assert.Equal("peer", authz.Resolve(IPAddress.Parse("192.0.2.10")).TransitPeerName);
        Assert.Equal("peer", authz.Resolve(IPAddress.Parse("2001:db8::10")).TransitPeerName);
    }

    [Fact]
    public async Task DnsRefresh_ReplacesAddressesAtomically()
    {
        var addresses = new List<IPAddress> { IPAddress.Parse("192.0.2.10") };
        var resolver = new FakeTransitDnsResolver
        {
            Handler = _ => TransitDnsResolveResult.Success([.. addresses], TimeSpan.FromMinutes(5)),
        };
        var cache = new TransitDnsAddressCache(resolver, NullLogger<TransitDnsAddressCache>.Instance);
        var snapshot = TransitTestPeers.Snapshot("peer", TransitTestPeers.Peer(allowFrom: ["news.example.net"]));
        await cache.RefreshAllAsync(snapshot, CancellationToken.None);
        Assert.Equal(["192.0.2.10"], cache.GetResolved("news.example.net").Select(static a => a.ToString()));

        addresses.Clear();
        addresses.Add(IPAddress.Parse("198.51.100.20"));
        await cache.RefreshAllAsync(snapshot, CancellationToken.None);
        var resolved = cache.GetResolved("news.example.net");
        Assert.DoesNotContain(IPAddress.Parse("192.0.2.10"), resolved);
        Assert.Contains(IPAddress.Parse("198.51.100.20"), resolved);
    }

    [Fact]
    public async Task DnsTransientFailure_PreservesLastAddresses()
    {
        var resolver = new FakeTransitDnsResolver
        {
            Handler = _ => TransitDnsResolveResult.Success([IPAddress.Parse("192.0.2.10")], TimeSpan.FromMinutes(5)),
        };
        var cache = new TransitDnsAddressCache(resolver, NullLogger<TransitDnsAddressCache>.Instance);
        var snapshot = TransitTestPeers.Snapshot("peer", TransitTestPeers.Peer(allowFrom: ["news.example.net"]));
        await cache.RefreshAllAsync(snapshot, CancellationToken.None);
        resolver.Handler = _ => TransitDnsResolveResult.TransientFailure();
        await cache.RefreshAllAsync(snapshot, CancellationToken.None);
        Assert.Contains(IPAddress.Parse("192.0.2.10"), cache.GetResolved("news.example.net"));
    }

    [Fact]
    public async Task DnsEmptyResult_ClearsAddresses()
    {
        var resolver = new FakeTransitDnsResolver
        {
            Handler = _ => TransitDnsResolveResult.Success([IPAddress.Parse("192.0.2.10")], TimeSpan.FromMinutes(5)),
        };
        var cache = new TransitDnsAddressCache(resolver, NullLogger<TransitDnsAddressCache>.Instance);
        var snapshot = TransitTestPeers.Snapshot("peer", TransitTestPeers.Peer(allowFrom: ["news.example.net"]));
        await cache.RefreshAllAsync(snapshot, CancellationToken.None);
        resolver.Handler = _ => TransitDnsResolveResult.Empty();
        await cache.RefreshAllAsync(snapshot, CancellationToken.None);
        Assert.Empty(cache.GetResolved("news.example.net"));
    }

    [Fact]
    public async Task DnsRefresh_HonorsMinimumSixtySecondsEvenWhenTtlIsShorter()
    {
        var clock = new MutableTimeProvider { UtcNow = DateTimeOffset.UnixEpoch };
        var resolver = new FakeTransitDnsResolver
        {
            Handler = _ => TransitDnsResolveResult.Success([IPAddress.Parse("192.0.2.10")], TimeSpan.FromSeconds(5)),
        };
        var cache = new TransitDnsAddressCache(
            resolver,
            NullLogger<TransitDnsAddressCache>.Instance,
            TimeSpan.FromSeconds(60),
            clock);
        var snapshot = TransitTestPeers.Snapshot("peer", TransitTestPeers.Peer(allowFrom: ["news.example.net"]));
        await cache.RefreshAllAsync(snapshot, CancellationToken.None);
        Assert.Equal(1, resolver.Calls);

        clock.UtcNow = clock.UtcNow.AddSeconds(30);
        await cache.RefreshDueAsync(snapshot, CancellationToken.None);
        Assert.Equal(1, resolver.Calls);

        clock.UtcNow = clock.UtcNow.AddSeconds(31);
        await cache.RefreshDueAsync(snapshot, CancellationToken.None);
        Assert.Equal(2, resolver.Calls);
        Assert.Equal(TimeSpan.FromSeconds(60), cache.GetDelayUntilNextRefresh(snapshot));
        clock.UtcNow = clock.UtcNow.AddSeconds(60);
        Assert.Equal(TimeSpan.Zero, cache.GetDelayUntilNextRefresh(snapshot));
    }

    [Fact]
    public async Task DnsRefresh_HonorsTtlWhenGreaterThanSixtySeconds()
    {
        var clock = new MutableTimeProvider { UtcNow = DateTimeOffset.UnixEpoch };
        var resolver = new FakeTransitDnsResolver
        {
            Handler = _ => TransitDnsResolveResult.Success([IPAddress.Parse("192.0.2.10")], TimeSpan.FromMinutes(10)),
        };
        var cache = new TransitDnsAddressCache(
            resolver,
            NullLogger<TransitDnsAddressCache>.Instance,
            TimeSpan.FromSeconds(60),
            clock);
        var snapshot = TransitTestPeers.Snapshot("peer", TransitTestPeers.Peer(allowFrom: ["news.example.net"]));
        await cache.RefreshAllAsync(snapshot, CancellationToken.None);
        clock.UtcNow = clock.UtcNow.AddMinutes(9);
        await cache.RefreshDueAsync(snapshot, CancellationToken.None);
        Assert.Equal(1, resolver.Calls);
        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        await cache.RefreshDueAsync(snapshot, CancellationToken.None);
        Assert.Equal(2, resolver.Calls);
    }

    [Fact]
    public async Task ConnectionAuthorization_DoesNotInvokeDnsResolver()
    {
        var resolver = new FakeTransitDnsResolver
        {
            Handler = _ => TransitDnsResolveResult.Success([IPAddress.Parse("192.0.2.10")], TimeSpan.FromMinutes(5)),
        };
        var cache = new TransitDnsAddressCache(resolver, NullLogger<TransitDnsAddressCache>.Instance);
        var snapshot = TransitTestPeers.Snapshot("peer", TransitTestPeers.Peer(allowFrom: ["news.example.net"]));
        await cache.RefreshAllAsync(snapshot, CancellationToken.None);
        var callsAfterRefresh = resolver.Calls;
        resolver.Handler = _ => throw new InvalidOperationException("connection-time DNS is forbidden");

        var authz = TransitPeerAuthorization.CreateStatic(snapshot, cache);
        Assert.Equal("peer", authz.Resolve(IPAddress.Parse("192.0.2.10")).TransitPeerName);
        Assert.Null(authz.Resolve(IPAddress.Parse("198.51.100.1")).TransitPeerName);
        Assert.Equal(callsAfterRefresh, resolver.Calls);
    }

    [Fact]
    public void ConnectionAuthorization_UsesMaterializedIpAcl_ThrowingResolverNeverCalled()
    {
        var resolver = new FakeTransitDnsResolver
        {
            Handler = _ => throw new InvalidOperationException("connection-time DNS is forbidden"),
        };
        var cache = new SeededDnsCache();
        cache.Seed("news.example.net", IPAddress.Parse("192.0.2.10"));
        var snapshot = TransitTestPeers.Snapshot("peer", TransitTestPeers.Peer(allowFrom: ["news.example.net"]));
        var authz = TransitPeerAuthorization.CreateStatic(snapshot, cache);

        Assert.Equal("peer", authz.Resolve(IPAddress.Parse("192.0.2.10")).TransitPeerName);
        Assert.Null(authz.Resolve(IPAddress.Parse("198.51.100.1")).TransitPeerName);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, cache.RefreshCalls);
    }

    [Fact]
    public void ConnectionAuthorization_UnresolvedHostname_DoesNotInvokeResolver()
    {
        var resolver = new FakeTransitDnsResolver
        {
            Handler = _ => throw new InvalidOperationException("connection-time DNS is forbidden"),
        };
        var cache = new TransitDnsAddressCache(resolver, NullLogger<TransitDnsAddressCache>.Instance);
        var snapshot = TransitTestPeers.Snapshot("peer", TransitTestPeers.Peer(allowFrom: ["news.example.net"]));
        var authz = TransitPeerAuthorization.CreateStatic(snapshot, cache);

        Assert.Null(authz.Resolve(IPAddress.Parse("192.0.2.10")).TransitPeerName);
        Assert.False(authz.Resolve(IPAddress.Parse("192.0.2.10")).AuthorizedTransit);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task DnsFailure_LogsWarningForPeerOnEveryAttempt()
    {
        var logger = new CollectingLogger<TransitDnsAddressCache>();
        var resolver = new FakeTransitDnsResolver
        {
            Handler = _ => TransitDnsResolveResult.TransientFailure(),
        };
        var cache = new TransitDnsAddressCache(resolver, logger);
        var snapshot = TransitTestPeers.Snapshot(
            "giganews",
            TransitTestPeers.Peer(allowFrom: ["news.example.net"], peerName: "Giganews, Inc."));
        await cache.RefreshAllAsync(snapshot, CancellationToken.None);
        await cache.RefreshAllAsync(snapshot, CancellationToken.None);

        var warnings = logger.Messages.Where(static m => m.Contains("AllowFrom DNS resolution failed", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(
            warnings,
            static w =>
            {
                Assert.Contains("Giganews, Inc.", w, StringComparison.Ordinal);
                Assert.Contains("news.example.net", w, StringComparison.Ordinal);
                Assert.DoesNotContain("Password", w, StringComparison.Ordinal);
                Assert.DoesNotContain("s3cret", w, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void AmbiguousIpMatch_DeniesTransit_AndWarnsEveryResolve()
    {
        var logger = new CollectingLogger<TransitPeerAuthorization>();
        var snapshot = new TransitConfigurationSnapshot(
            new Dictionary<string, TransitPeerPolicy>(StringComparer.Ordinal)
            {
                ["one"] = TransitConfigurationSnapshot.CreatePeer("one", TransitTestPeers.Peer(allowFrom: ["192.0.2.0/24"])),
                ["two"] = TransitConfigurationSnapshot.CreatePeer("two", TransitTestPeers.Peer(allowFrom: ["192.0.2.0/24"])),
            });
        var authz = TransitPeerAuthorization.CreateStatic(snapshot, logger: logger);
        var source = IPAddress.Parse("192.0.2.10");

        Assert.Null(authz.Resolve(source).TransitPeerName);
        Assert.False(authz.Resolve(source).AuthorizedTransit);
        Assert.Equal(2, logger.Messages.Count);
        Assert.All(
            logger.Messages,
            static m =>
            {
                Assert.Contains("ambiguous", m, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("192.0.2.10", m, StringComparison.Ordinal);
                Assert.Contains("one", m, StringComparison.Ordinal);
                Assert.Contains("two", m, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void EmptyAllowFrom_DoesNotMatchInbound()
    {
        var authz = TransitPeerAuthorization.CreateStatic(
            TransitTestPeers.Snapshot("outbound-only", TransitTestPeers.Peer(allowFrom: [])));
        Assert.True(authz.IsEnabled);
        Assert.Null(authz.Resolve(IPAddress.Parse("192.0.2.10")).TransitPeerName);
    }

    private static TransitPeerAuthorization Create(params string[] allowFrom) =>
        TransitPeerAuthorization.CreateStatic(
            TransitTestPeers.Snapshot("peer", TransitTestPeers.Peer(allowFrom: allowFrom)));

    private sealed class SeededDnsCache : ITransitDnsAddressCache
    {
        private readonly Dictionary<string, IReadOnlySet<IPAddress>> _addresses =
            new(StringComparer.OrdinalIgnoreCase);

        public int RefreshCalls { get; private set; }

        public void Seed(string hostname, params IPAddress[] addresses) =>
            _addresses[hostname] = addresses.ToHashSet();

        public IReadOnlySet<IPAddress> GetResolved(string hostname) =>
            _addresses.TryGetValue(hostname, out var set) ? set : new HashSet<IPAddress>();

        public Task RefreshAllAsync(TransitConfigurationSnapshot snapshot, CancellationToken cancellationToken)
        {
            RefreshCalls++;
            throw new InvalidOperationException("connection-time DNS refresh is forbidden");
        }

        public Task RefreshDueAsync(TransitConfigurationSnapshot snapshot, CancellationToken cancellationToken)
        {
            RefreshCalls++;
            throw new InvalidOperationException("connection-time DNS refresh is forbidden");
        }

        public TimeSpan GetDelayUntilNextRefresh(TransitConfigurationSnapshot snapshot) => Timeout.InfiniteTimeSpan;
    }

    private sealed class FakeTransitDnsResolver : ITransitDnsResolver
    {
        public Func<string, TransitDnsResolveResult> Handler { get; set; } =
            static _ => TransitDnsResolveResult.TransientFailure();

        public int Calls { get; private set; }

        public Task<TransitDnsResolveResult> ResolveAsync(string hostname, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Handler(hostname));
        }
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add($"{logLevel}: {formatter(state, exception)}");
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}

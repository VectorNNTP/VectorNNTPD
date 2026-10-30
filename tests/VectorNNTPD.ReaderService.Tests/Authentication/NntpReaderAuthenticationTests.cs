using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.ReaderService.Authentication.Sasl;
using VectorNNTPD.ReaderService.Authentication;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.ReaderService.NntpDb;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Authentication;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Session.Framing;
using VectorNNTPD.ReaderService.Tests.Session;
using VectorNNTPD.ReaderService.Tests.TestDoubles;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.ReaderService.Tests.Authentication;

[Collection(nameof(NntpCommandLoggerCollection))]
public sealed class NntpReaderAuthenticationTests
{
    [Fact]
    public async Task UserPass_Succeeds_AndCachesPolicy()
    {
        await using var harness = await AuthHarness.CreateAsync(MemoryNntpUserRecordStore.Create("alice", "secret"));
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("AUTHINFO USER alice");
        Assert.StartsWith("381 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.False(session.Authentication.IsAuthenticated);

        await harness.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.StartsWith("281 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.True(session.Authentication.IsAuthenticated);
        Assert.Equal("alice", session.Authentication.Username);
        Assert.True(session.Authorization.AuthorizedReader);
        Assert.True(session.Authorization.PostingPermitted);
        Assert.False(session.Authorization.AuthorizedTransit);
        Assert.False(session.Authorization.ControlCancelPermitted);
        Assert.NotNull(session.AccountPolicy);
        Assert.Equal(0, session.AccountPolicy!.RateLimitBps);
        Assert.True(session.AccountPolicy.ByteLimit > 0);
        Assert.Equal(ArticleTypeCapabilities.All, session.AccountPolicy.AllowedArtTypes);

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task UserPass_CachesTextOnlyArtTypeCapability()
    {
        await using var harness = await AuthHarness.CreateAsync(
            MemoryNntpUserRecordStore.Create(
                "alice",
                "secret",
                allowedArtTypes: ArticleTypeCapabilities.TextOnly));
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();
        await harness.WriteClientLineAsync("AUTHINFO USER alice");
        Assert.StartsWith("381 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
        await harness.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.StartsWith("281 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.Equal(ArticleTypeCapabilities.TextOnly, session.AccountPolicy!.AllowedArtTypes);
        Assert.True(ArticleTypeAccessPolicy.CanPostArticleType(session, ArticleType.Default));
        Assert.False(ArticleTypeAccessPolicy.CanPostArticleType(session, ArticleType.Binary));
        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task UnknownUser_AndDisabled_AndWrongPassword_AllReturn481()
    {
        await using var harness = await AuthHarness.CreateAsync(
            MemoryNntpUserRecordStore.Create("alice", "secret"),
            MemoryNntpUserRecordStore.Create("disabled", "secret", enabled: false));
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("AUTHINFO USER missing");
        Assert.StartsWith("381 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
        await harness.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.Equal("481 Authentication failed", await harness.ReadClientLineAsync());
        Assert.False(session.Authentication.IsAuthenticated);
        Assert.Equal("missing", session.PendingAuthUsername);

        await harness.WriteClientLineAsync("AUTHINFO USER disabled");
        Assert.StartsWith("381 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
        await harness.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.Equal("481 Authentication failed", await harness.ReadClientLineAsync());

        await harness.WriteClientLineAsync("AUTHINFO USER alice");
        Assert.StartsWith("381 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
        await harness.WriteClientLineAsync("AUTHINFO PASS wrong");
        Assert.Equal("481 Authentication failed", await harness.ReadClientLineAsync());
        Assert.False(session.Authentication.IsAuthenticated);

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task AllowAuthPlainDisabled_RejectsPasswordMechanisms()
    {
        await using var harness = await AuthHarness.CreateAsync(
            MemoryNntpUserRecordStore.Create("alice", "secret", allowPlain: false));
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("AUTHINFO USER alice");
        await harness.ReadClientLineAsync();
        await harness.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.Equal("481 Authentication failed", await harness.ReadClientLineAsync());
        Assert.False(session.Authentication.IsAuthenticated);

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task MissingAccountArtTypeColumn_Returns503_Not481()
    {
        var store = new MemoryNntpUserRecordStore();
        store.Add(MemoryNntpUserRecordStore.Create("alice", "secret"));
        store.Exception = new NntpDbUnavailableException(
            "MySQL nntpusers lookup failed.",
            new InvalidOperationException("Unknown column 'account_art_type'"));
        await using var harness = await AuthHarness.CreateAsync(store);
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("AUTHINFO USER alice");
        await harness.ReadClientLineAsync();
        await harness.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.Equal("503 Temporary authentication failure", await harness.ReadClientLineAsync());
        Assert.False(session.Authentication.IsAuthenticated);

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task BackendFailure_Returns503_Not481()
    {
        var store = new MemoryNntpUserRecordStore();
        store.Add(MemoryNntpUserRecordStore.Create("alice", "secret"));
        store.Exception = new NntpDbUnavailableException("down");
        await using var harness = await AuthHarness.CreateAsync(store);
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("AUTHINFO USER alice");
        await harness.ReadClientLineAsync();
        await harness.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.Equal("503 Temporary authentication failure", await harness.ReadClientLineAsync());
        Assert.False(session.Authentication.IsAuthenticated);

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task SessionLimit_RejectsSecondSession_ThenReleasesOnClose()
    {
        var record = MemoryNntpUserRecordStore.Create("alice", "secret", sessionLimit: 1);
        var admission = new InMemorySessionStateTracker();
        await using var first = await AuthHarness.CreateAsync(record, admission: admission);
        await using var second = await AuthHarness.CreateAsync(record, admission: admission);

        var session1 = first.CreateSession();
        var run1 = session1.RunAsync();
        await first.ReadGreetingAsync();
        await first.WriteClientLineAsync("AUTHINFO USER alice");
        await first.ReadClientLineAsync();
        await first.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.StartsWith("281 ", await first.ReadClientLineAsync(), StringComparison.Ordinal);

        var session2 = second.CreateSession();
        var run2 = session2.RunAsync();
        await second.ReadGreetingAsync();
        await second.WriteClientLineAsync("AUTHINFO USER alice");
        await second.ReadClientLineAsync();
        await second.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.Equal("481 Too many sessions", await second.ReadClientLineAsync());
        Assert.False(session2.Authentication.IsAuthenticated);

        await first.WriteClientLineAsync("QUIT");
        await first.ReadClientLineAsync();
        await run1;

        await second.WriteClientLineAsync("AUTHINFO USER alice");
        await second.ReadClientLineAsync();
        await second.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.StartsWith("281 ", await second.ReadClientLineAsync(), StringComparison.Ordinal);

        await second.WriteClientLineAsync("QUIT");
        await second.ReadClientLineAsync();
        await run2;
    }

    [Fact]
    public async Task SourceIpLimit_RejectsDistinctAddress()
    {
        var record = MemoryNntpUserRecordStore.Create("alice", "secret", srcIpLimit: 1);
        var admission = new InMemorySessionStateTracker();
        await using var first = await AuthHarness.CreateAsync(
            record,
            admission: admission,
            clientIp: IPAddress.Parse("203.0.113.10"));
        await using var second = await AuthHarness.CreateAsync(
            record,
            admission: admission,
            clientIp: IPAddress.Parse("203.0.113.11"));

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
        Assert.Equal("481 Too many source addresses", await second.ReadClientLineAsync());
        Assert.False(session2.Authentication.IsAuthenticated);

        await first.WriteClientLineAsync("QUIT");
        await first.ReadClientLineAsync();
        await second.WriteClientLineAsync("QUIT");
        await second.ReadClientLineAsync();
        await run1;
        await run2;
    }

    [Fact]
    public async Task SourceIpLimit_SameAddressAdditionalSessions_AreAccepted()
    {
        var record = MemoryNntpUserRecordStore.Create("alice", "secret", srcIpLimit: 1);
        var admission = new InMemorySessionStateTracker();
        var ip = IPAddress.Parse("203.0.113.10");
        await using var first = await AuthHarness.CreateAsync(record, admission: admission, clientIp: ip);
        await using var second = await AuthHarness.CreateAsync(record, admission: admission, clientIp: ip);

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
        Assert.Equal("281 Authentication accepted", await second.ReadClientLineAsync());
        Assert.True(session2.Authentication.IsAuthenticated);

        await first.WriteClientLineAsync("QUIT");
        await first.ReadClientLineAsync();
        await second.WriteClientLineAsync("QUIT");
        await second.ReadClientLineAsync();
        await run1;
        await run2;
    }

    [Fact]
    public async Task DistributedAdmissionUnavailable_Returns503()
    {
        var record = MemoryNntpUserRecordStore.Create("alice", "secret", srcIpLimit: 1);
        var membership = new InMemorySessionStateStore { Unavailable = true };
        var admission = new DistributedSessionStateTracker(
            membership,
            NullLogger<DistributedSessionStateTracker>.Instance,
            "nntpd01");
        await using var harness = await AuthHarness.CreateAsync(
            record,
            admission: admission,
            clientIp: IPAddress.Parse("192.0.2.10"));
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();
        await harness.WriteClientLineAsync("AUTHINFO USER alice");
        await harness.ReadClientLineAsync();
        await harness.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.Equal("503 Temporary authentication failure", await harness.ReadClientLineAsync());
        Assert.False(session.Authentication.IsAuthenticated);
        Assert.Null(session.AccountPolicy);

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task DistributedSourceIpLimit_SameAddressAdditionalSessions_AreAccepted()
    {
        var record = MemoryNntpUserRecordStore.Create("alice", "secret", srcIpLimit: 1);
        var membership = new InMemorySessionStateStore();
        var admission = new DistributedSessionStateTracker(
            membership,
            NullLogger<DistributedSessionStateTracker>.Instance,
            "nntpd01");
        var ip = IPAddress.Parse("192.0.2.10");
        await using var first = await AuthHarness.CreateAsync(record, admission: admission, clientIp: ip);
        await using var second = await AuthHarness.CreateAsync(record, admission: admission, clientIp: ip);

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
        Assert.Equal("281 Authentication accepted", await second.ReadClientLineAsync());
        Assert.True(session2.Authentication.IsAuthenticated);

        await first.WriteClientLineAsync("QUIT");
        await first.ReadClientLineAsync();
        await second.WriteClientLineAsync("QUIT");
        await second.ReadClientLineAsync();
        await run1;
        await run2;
    }

    [Fact]
    public async Task DistributedSourceIpLimit_RejectsIpv6WhenIpv4IsActive()
    {
        var record = MemoryNntpUserRecordStore.Create("a", "secret", srcIpLimit: 1);
        var membership = new InMemorySessionStateStore();
        var admission = new DistributedSessionStateTracker(
            membership,
            NullLogger<DistributedSessionStateTracker>.Instance,
            "nntpd01");
        await using var first = await AuthHarness.CreateAsync(
            record,
            admission: admission,
            clientIp: IPAddress.Parse("192.0.2.10"));
        await using var second = await AuthHarness.CreateAsync(
            record,
            admission: admission,
            clientIp: IPAddress.Parse("2001:db8::10"));

        var session1 = first.CreateSession();
        var run1 = session1.RunAsync();
        await first.ReadGreetingAsync();
        await first.AuthenticateAsync("a", "secret");
        Assert.True(session1.Authentication.IsAuthenticated);

        var session2 = second.CreateSession();
        var run2 = session2.RunAsync();
        await second.ReadGreetingAsync();
        await second.WriteClientLineAsync("AUTHINFO USER a");
        await second.ReadClientLineAsync();
        await second.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.Equal("481 Too many source addresses", await second.ReadClientLineAsync());
        Assert.False(session2.Authentication.IsAuthenticated);
        Assert.Null(session2.AccountPolicy);
        Assert.False(session2.Authorization.IsAuthenticated);

        await first.WriteClientLineAsync("QUIT");
        await first.ReadClientLineAsync();
        await second.WriteClientLineAsync("QUIT");
        await second.ReadClientLineAsync();
        await run1;
        await run2;
    }

    [Fact]
    public async Task DistributedSourceIpLimit_RejectsIpv4WhenIpv6IsActive()
    {
        var record = MemoryNntpUserRecordStore.Create("a", "secret", srcIpLimit: 1);
        var membership = new InMemorySessionStateStore();
        var admission = new DistributedSessionStateTracker(
            membership,
            NullLogger<DistributedSessionStateTracker>.Instance,
            "nntpd01");
        await using var first = await AuthHarness.CreateAsync(
            record,
            admission: admission,
            clientIp: IPAddress.Parse("2001:db8::10"));
        await using var second = await AuthHarness.CreateAsync(
            record,
            admission: admission,
            clientIp: IPAddress.Parse("192.0.2.10"));

        var session1 = first.CreateSession();
        var run1 = session1.RunAsync();
        await first.ReadGreetingAsync();
        await first.AuthenticateAsync("a", "secret");

        var session2 = second.CreateSession();
        var run2 = session2.RunAsync();
        await second.ReadGreetingAsync();
        await second.WriteClientLineAsync("AUTHINFO USER a");
        await second.ReadClientLineAsync();
        await second.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.Equal("481 Too many source addresses", await second.ReadClientLineAsync());
        Assert.False(session2.Authentication.IsAuthenticated);

        await first.WriteClientLineAsync("QUIT");
        await first.ReadClientLineAsync();
        await second.WriteClientLineAsync("QUIT");
        await second.ReadClientLineAsync();
        await run1;
        await run2;
    }

    [Fact]
    public async Task IdleAuthenticatedSession_KeepsClusterSlotWithoutFurtherCommands()
    {
        var record = MemoryNntpUserRecordStore.Create("alice", "secret", sessionLimit: 1);
        var clock = new ControllableTimeProvider();
        var membership = new InMemorySessionStateStore();
        var admission = new DistributedSessionStateTracker(
            membership,
            NullLogger<DistributedSessionStateTracker>.Instance,
            "nntpd01",
            clock);
        await using var first = await AuthHarness.CreateAsync(
            record,
            admission: admission,
            clientIp: IPAddress.Parse("192.0.2.10"));
        await using var second = await AuthHarness.CreateAsync(
            record,
            admission: admission,
            clientIp: IPAddress.Parse("198.51.100.20"));

        var session1 = first.CreateSession();
        var run1 = session1.RunAsync();
        await first.ReadGreetingAsync();
        await first.AuthenticateAsync("alice", "secret");
        Assert.True(session1.Authentication.IsAuthenticated);

        for (var i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await admission.RenewLeasesAsync();
        }

        Assert.Equal(1, membership.ActiveSessionCount("alice", clock.GetUtcNow().ToUnixTimeMilliseconds()));

        var session2 = second.CreateSession();
        var run2 = session2.RunAsync();
        await second.ReadGreetingAsync();
        await second.WriteClientLineAsync("AUTHINFO USER alice");
        await second.ReadClientLineAsync();
        await second.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.Equal("481 Too many sessions", await second.ReadClientLineAsync());
        Assert.False(session2.Authentication.IsAuthenticated);

        await first.WriteClientLineAsync("QUIT");
        await first.ReadClientLineAsync();
        await run1;
        await second.WriteClientLineAsync("QUIT");
        await second.ReadClientLineAsync();
        await run2;
    }

    [Fact]
    public async Task DistributedSessionLimit_IsClusterWide()
    {
        var record = MemoryNntpUserRecordStore.Create("alice", "secret", sessionLimit: 1);
        var membership = new InMemorySessionStateStore();
        var nodeA = new DistributedSessionStateTracker(
            membership,
            NullLogger<DistributedSessionStateTracker>.Instance,
            "nntpd01");
        var nodeB = new DistributedSessionStateTracker(
            membership,
            NullLogger<DistributedSessionStateTracker>.Instance,
            "nntpd02");
        await using var first = await AuthHarness.CreateAsync(
            record,
            admission: nodeA,
            clientIp: IPAddress.Parse("192.0.2.10"));
        await using var second = await AuthHarness.CreateAsync(
            record,
            admission: nodeB,
            clientIp: IPAddress.Parse("2001:db8::10"));

        var session1 = first.CreateSession();
        var run1 = session1.RunAsync();
        await first.ReadGreetingAsync();
        await first.AuthenticateAsync("alice", "secret");
        Assert.True(session1.Authentication.IsAuthenticated);

        var session2 = second.CreateSession();
        var run2 = session2.RunAsync();
        await second.ReadGreetingAsync();
        await second.WriteClientLineAsync("AUTHINFO USER alice");
        await second.ReadClientLineAsync();
        await second.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.Equal("481 Too many sessions", await second.ReadClientLineAsync());
        Assert.False(session2.Authentication.IsAuthenticated);
        Assert.Null(session2.AccountPolicy);

        await first.WriteClientLineAsync("QUIT");
        await first.ReadClientLineAsync();
        await run1;

        await second.WriteClientLineAsync("AUTHINFO USER alice");
        await second.ReadClientLineAsync();
        await second.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.Equal("281 Authentication accepted", await second.ReadClientLineAsync());
        Assert.True(session2.Authentication.IsAuthenticated);

        await second.WriteClientLineAsync("QUIT");
        await second.ReadClientLineAsync();
        await run2;
    }

    [Fact]
    public async Task DistributedReauthentication_DoesNotLeakSourceSlot()
    {
        var record = MemoryNntpUserRecordStore.Create("alice", "secret", srcIpLimit: 1);
        var membership = new InMemorySessionStateStore();
        var admission = new DistributedSessionStateTracker(
            membership,
            NullLogger<DistributedSessionStateTracker>.Instance,
            "nntpd01");
        await using var first = await AuthHarness.CreateAsync(
            record,
            admission: admission,
            clientIp: IPAddress.Parse("192.0.2.10"));
        await using var second = await AuthHarness.CreateAsync(
            record,
            admission: admission,
            clientIp: IPAddress.Parse("2001:db8::10"));

        var session1 = first.CreateSession();
        var run1 = session1.RunAsync();
        await first.ReadGreetingAsync();
        await first.AuthenticateAsync("alice", "secret");
        await first.WriteClientLineAsync("AUTHINFO USER alice");
        Assert.StartsWith("502 ", await first.ReadClientLineAsync(), StringComparison.Ordinal);

        var session2 = second.CreateSession();
        var run2 = session2.RunAsync();
        await second.ReadGreetingAsync();
        await second.WriteClientLineAsync("AUTHINFO USER alice");
        await second.ReadClientLineAsync();
        await second.WriteClientLineAsync("AUTHINFO PASS secret");
        Assert.Equal("481 Too many source addresses", await second.ReadClientLineAsync());
        Assert.False(session2.Authentication.IsAuthenticated);

        await first.WriteClientLineAsync("QUIT");
        await first.ReadClientLineAsync();
        await second.WriteClientLineAsync("QUIT");
        await second.ReadClientLineAsync();
        await run1;
        await run2;
    }

    [Fact]
    public async Task NewsmasterUsername_DoesNotFallThroughToMysql()
    {
        var options = new NntpdOptions
        {
            NewsmasterUser = "alice",
            NewsmasterPassword = "newsmaster-secret",
        };
        var store = new MemoryNntpUserRecordStore();
        store.Add(MemoryNntpUserRecordStore.Create("alice", "mysql-secret"));
        await using var harness = await AuthHarness.CreateAsync(store, newsmaster: options);
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("AUTHINFO USER alice");
        await harness.ReadClientLineAsync();
        await harness.WriteClientLineAsync("AUTHINFO PASS mysql-secret");
        Assert.Equal("481 Authentication failed", await harness.ReadClientLineAsync());
        Assert.Equal(0, store.LookupCount);

        await harness.WriteClientLineAsync("AUTHINFO USER alice");
        await harness.ReadClientLineAsync();
        await harness.WriteClientLineAsync("AUTHINFO PASS newsmaster-secret");
        Assert.StartsWith("281 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.True(session.Authorization.ControlCancelPermitted);
        Assert.Equal(0, store.LookupCount);

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task FailedAuth_ClearsIdentity_AndDoesNotLogPassword()
    {
        var recording = new RecordingLoggerFactory();
        await using var harness = await AuthHarness.CreateAsync(MemoryNntpUserRecordStore.Create("alice", "secret"));
        var session = harness.CreateSession(loggerFactory: recording);
        session.ApplySuccessfulAuthentication(
            "stale",
            new NntpAuthorization(true, true, false, true, false),
            NntpAccountPolicy.FromRecord(MemoryNntpUserRecordStore.Create("stale", "x")));
        session.ApplyFailedAuthentication();
        Assert.False(session.Authentication.IsAuthenticated);
        Assert.Null(session.AccountPolicy);

        var run = session.RunAsync();
        await harness.ReadGreetingAsync();
        await harness.WriteClientLineAsync("AUTHINFO USER alice");
        await harness.ReadClientLineAsync();
        await harness.WriteClientLineAsync("AUTHINFO PASS not-the-password");
        Assert.StartsWith("481 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;

        var joined = string.Join('\n', recording.Messages);
        Assert.DoesNotContain("not-the-password", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", joined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FakeNntpDb_LookupAndBackendFailure()
    {
        var factory = new FakeNntpDbConnectionFactory();
        factory.Users["alice"] = MemoryNntpUserRecordStore.Create("alice", "secret");
        await using var connection = (FakeNntpDbConnection)await factory.OpenAsync("Server=test;", CancellationToken.None);
        var found = await connection.QueryUserAccountAsync("alice", CancellationToken.None);
        Assert.NotNull(found);
        Assert.Null(await connection.QueryUserAccountAsync("missing", CancellationToken.None));

        connection.QueryUserException = new NntpDbUnavailableException("down");
        await Assert.ThrowsAsync<NntpDbUnavailableException>(
            async () => await connection.QueryUserAccountAsync("alice", CancellationToken.None));
    }

    [Fact]
    public async Task AuthenticatedAccount_ReceivesBothByteAndRatePolicies()
    {
        await using var harness = await AuthHarness.CreateAsync(
            MemoryNntpUserRecordStore.Create("alice", "secret", rateLimitBps: 240, byteLimit: 1000));
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();
        await harness.AuthenticateAsync("alice", "secret");
        Assert.Equal(1000, session.AccountPolicy!.ByteLimit);
        Assert.Equal(240, session.AccountPolicy.RateLimitBps);
        Assert.True(session.AccountPolicy.RequiresRateTracking);
        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }
}

[Collection(nameof(NntpCommandLoggerCollection))]
public sealed class NntpSaslAuthenticationTests
{
    [Fact]
    public async Task Capabilities_AdvertiseImplementedMechanismsOnly()
    {
        await using var harness = await AuthHarness.CreateAsync(MemoryNntpUserRecordStore.Create("alice", "secret"));
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("CAPABILITIES");
        Assert.StartsWith("101 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
        var caps = await harness.ReadMultilineBodyAsync();
        Assert.Contains("AUTHINFO USER SASL", caps);
        Assert.Contains("SASL PLAIN LOGIN SCRAM-SHA-256 CRAM-MD5", caps);
        Assert.DoesNotContain(caps, static c => c.Contains("DIGEST-MD5", StringComparison.Ordinal));
        Assert.DoesNotContain(caps, static c => c.Contains("GSSAPI", StringComparison.Ordinal));
        Assert.DoesNotContain(caps, static c => c.Contains("EXTERNAL", StringComparison.Ordinal));
        Assert.DoesNotContain(caps, static c => c.Contains("SCRAM-SHA-1", StringComparison.Ordinal));
        Assert.DoesNotContain(caps, static c => c.Contains("PLUS", StringComparison.Ordinal));

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task Plain_WithInitialResponse_Succeeds()
    {
        await using var harness = await AuthHarness.CreateAsync(MemoryNntpUserRecordStore.Create("alice", "secret"));
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        var token = Convert.ToBase64String("\0alice\0secret"u8.ToArray());
        await harness.WriteClientLineAsync($"AUTHINFO SASL PLAIN {token}");
        Assert.StartsWith("281 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.Equal("alice", session.Authentication.Username);

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task Plain_ContinuationAndCancel()
    {
        await using var harness = await AuthHarness.CreateAsync(MemoryNntpUserRecordStore.Create("alice", "secret"));
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("AUTHINFO SASL PLAIN");
        Assert.Equal("383 =", await harness.ReadClientLineAsync());
        Assert.True(session.HasSaslExchange);

        await harness.WriteClientLineAsync("*");
        Assert.Equal("481 Authentication cancelled", await harness.ReadClientLineAsync());
        Assert.False(session.HasSaslExchange);
        Assert.False(session.Authentication.IsAuthenticated);

        await harness.WriteClientLineAsync("AUTHINFO SASL PLAIN");
        Assert.Equal("383 =", await harness.ReadClientLineAsync());
        await harness.WriteClientLineAsync(Convert.ToBase64String("\0alice\0secret"u8.ToArray()));
        Assert.StartsWith("281 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task Login_Succeeds_AndInvalidBase64Is504()
    {
        await using var harness = await AuthHarness.CreateAsync(MemoryNntpUserRecordStore.Create("alice", "secret"));
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("AUTHINFO SASL PLAIN !!!not-base64!!!");
        Assert.Equal("504 Base64 encoding error", await harness.ReadClientLineAsync());

        await harness.WriteClientLineAsync("AUTHINFO SASL LOGIN");
        var loginUserChallenge = await harness.ReadClientLineAsync();
        Assert.Equal("383 VXNlcm5hbWU6", loginUserChallenge);
        Assert.False(loginUserChallenge.StartsWith("334 ", StringComparison.Ordinal));
        await harness.WriteClientLineAsync(Convert.ToBase64String("alice"u8.ToArray()));
        var loginPasswordChallenge = await harness.ReadClientLineAsync();
        Assert.Equal("383 UGFzc3dvcmQ6", loginPasswordChallenge);
        await harness.WriteClientLineAsync(Convert.ToBase64String("secret"u8.ToArray()));
        var loginSuccess = await harness.ReadClientLineAsync();
        Assert.StartsWith("281 ", loginSuccess, StringComparison.Ordinal);
        Assert.False(loginSuccess.StartsWith("235 ", StringComparison.Ordinal));

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task UnsupportedMechanisms_Return503()
    {
        await using var harness = await AuthHarness.CreateAsync(MemoryNntpUserRecordStore.Create("alice", "secret"));
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        foreach (var mechanism in new[] { "DIGEST-MD5", "SCRAM-SHA-1", "SCRAM-SHA-256-PLUS", "EXTERNAL", "GSSAPI" })
        {
            await harness.WriteClientLineAsync($"AUTHINFO SASL {mechanism}");
            Assert.Equal("503 Mechanism not supported", await harness.ReadClientLineAsync());
        }

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task CramMd5_ValidAndInvalid()
    {
        await using var harness = await AuthHarness.CreateAsync(MemoryNntpUserRecordStore.Create("alice", "secret"));
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("AUTHINFO SASL CRAM-MD5");
        var challengeLine = await harness.ReadClientLineAsync();
        Assert.StartsWith("383 ", challengeLine, StringComparison.Ordinal);
        Assert.False(challengeLine.StartsWith("334 ", StringComparison.Ordinal));
        var challenge = challengeLine[4..];
        var native = Encoding.ASCII.GetString(Convert.FromBase64String(challenge));
        Assert.True(CramMd5Mechanism.IsRfc2195Challenge(native));
        Assert.EndsWith("@nntpd01.usenet.ninja>", native, StringComparison.Ordinal);
        await harness.WriteClientLineAsync(CramResponse("alice", "secret", challenge));
        var cramSuccess = await harness.ReadClientLineAsync();
        Assert.Equal("281 Authentication accepted", cramSuccess);
        Assert.False(cramSuccess.StartsWith("235 ", StringComparison.Ordinal));
        Assert.Equal("alice", session.Authentication.Username);

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task CramMd5_WrongResponse_Returns481()
    {
        await using var harness = await AuthHarness.CreateAsync(MemoryNntpUserRecordStore.Create("alice", "secret"));
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("AUTHINFO SASL CRAM-MD5");
        var challengeLine = await harness.ReadClientLineAsync();
        await harness.WriteClientLineAsync(CramResponse("alice", "wrong", challengeLine[4..]));
        Assert.Equal("481 Authentication failed", await harness.ReadClientLineAsync());
        Assert.False(session.Authentication.IsAuthenticated);

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task CramMd5_ChallengeIsUniqueAndUsesNntpdFqdn()
    {
        await using var harness = await AuthHarness.CreateAsync(MemoryNntpUserRecordStore.Create("alice", "secret"));
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("AUTHINFO SASL CRAM-MD5");
        var firstLine = await harness.ReadClientLineAsync();
        Assert.StartsWith("383 ", firstLine, StringComparison.Ordinal);
        var firstNative = Encoding.ASCII.GetString(Convert.FromBase64String(firstLine[4..]));
        Assert.True(CramMd5Mechanism.IsRfc2195Challenge(firstNative));
        Assert.StartsWith("<", firstNative, StringComparison.Ordinal);
        Assert.EndsWith("@nntpd01.usenet.ninja>", firstNative, StringComparison.Ordinal);
        await harness.WriteClientLineAsync("*");
        Assert.Equal("481 Authentication cancelled", await harness.ReadClientLineAsync());

        await harness.WriteClientLineAsync("AUTHINFO SASL CRAM-MD5");
        var secondLine = await harness.ReadClientLineAsync();
        Assert.StartsWith("383 ", secondLine, StringComparison.Ordinal);
        var secondNative = Encoding.ASCII.GetString(Convert.FromBase64String(secondLine[4..]));
        Assert.True(CramMd5Mechanism.IsRfc2195Challenge(secondNative));
        Assert.EndsWith("@nntpd01.usenet.ninja>", secondNative, StringComparison.Ordinal);
        Assert.NotEqual(firstNative, secondNative);
        Assert.NotEqual(firstLine, secondLine);

        await harness.WriteClientLineAsync("*");
        Assert.StartsWith("481 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task CramMd5_InvalidBase64Continuation_Returns504()
    {
        await using var harness = await AuthHarness.CreateAsync(MemoryNntpUserRecordStore.Create("alice", "secret"));
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("AUTHINFO SASL CRAM-MD5");
        Assert.StartsWith("383 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
        await harness.WriteClientLineAsync("abcd=efg");
        Assert.Equal("504 Base64 encoding error", await harness.ReadClientLineAsync());
        Assert.False(session.Authentication.IsAuthenticated);

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task ScramSha256_CompletesRfcExchange()
    {
        const string password = "pencil";
        var salt = Convert.FromHexString("4142434445464748494A4B4C4D4E4F50");
        var keys = ScramClient.Derive(password, salt, 4096);
        var record = MemoryNntpUserRecordStore.Create(
            "user",
            password,
            scramSalt: salt,
            scramIterations: 4096,
            scramStoredKey: keys.StoredKey,
            scramServerKey: keys.ServerKey);
        await using var harness = await AuthHarness.CreateAsync(record);
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        const string clientFirst = "n,,n=user,r=fyko+d2lbbFgONRv9qkxdawL";
        await harness.WriteClientLineAsync($"AUTHINFO SASL SCRAM-SHA-256 {Convert.ToBase64String(Encoding.UTF8.GetBytes(clientFirst))}");
        var serverFirstLine = await harness.ReadClientLineAsync();
        Assert.StartsWith("383 ", serverFirstLine, StringComparison.Ordinal);
        Assert.DoesNotContain("s=", serverFirstLine, StringComparison.Ordinal);
        Assert.DoesNotContain("i=", serverFirstLine, StringComparison.Ordinal);
        var serverFirst = Encoding.UTF8.GetString(Convert.FromBase64String(serverFirstLine[4..]));
        Assert.Contains("s=", serverFirst, StringComparison.Ordinal);
        var clientFinal = ScramClient.CreateClientFinal(clientFirst, serverFirst, keys.ClientKey, keys.StoredKey);
        await harness.WriteClientLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(clientFinal)));
        var success = await harness.ReadClientLineAsync();
        Assert.StartsWith("283 ", success, StringComparison.Ordinal);
        Assert.False(success.StartsWith("235 ", StringComparison.Ordinal));
        Assert.False(success.StartsWith("281 ", StringComparison.Ordinal));
        var serverFinal = Encoding.UTF8.GetString(Convert.FromBase64String(success[4..]));
        Assert.StartsWith("v=", serverFinal, StringComparison.Ordinal);
        Assert.Equal("user", session.Authentication.Username);
        Assert.DoesNotContain(Convert.ToHexString(keys.StoredKey), success, StringComparison.Ordinal);

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task Scram_DisabledFlag_FailsWithoutAdvertisingKeys()
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var keys = ScramClient.Derive("pencil", salt, 4096);
        var record = MemoryNntpUserRecordStore.Create(
            "user",
            "pencil",
            allowScram: false,
            scramSalt: salt,
            scramIterations: 4096,
            scramStoredKey: keys.StoredKey,
            scramServerKey: keys.ServerKey);
        await using var harness = await AuthHarness.CreateAsync(record);
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        const string clientFirst = "n,,n=user,r=clientnonce";
        await harness.WriteClientLineAsync($"AUTHINFO SASL SCRAM-SHA-256 {Convert.ToBase64String(Encoding.UTF8.GetBytes(clientFirst))}");
        var serverFirstLine = await harness.ReadClientLineAsync();
        Assert.StartsWith("383 ", serverFirstLine, StringComparison.Ordinal);
        var serverFirst = Encoding.UTF8.GetString(Convert.FromBase64String(serverFirstLine[4..]));
        Assert.DoesNotContain(Convert.ToBase64String(salt), serverFirst, StringComparison.Ordinal);
        var clientFinal = ScramClient.CreateClientFinal(clientFirst, serverFirst, keys.ClientKey, keys.StoredKey);
        await harness.WriteClientLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(clientFinal)));
        Assert.Equal("481 Authentication failed", await harness.ReadClientLineAsync());
        Assert.False(session.Authentication.IsAuthenticated);
        Assert.Null(session.Authentication.Username);

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task Login_Cancellation_Returns481_AndNextCommandIsParsed()
    {
        await using var harness = await AuthHarness.CreateAsync(MemoryNntpUserRecordStore.Create("alice", "secret"));
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("AUTHINFO SASL LOGIN");
        Assert.Equal("383 VXNlcm5hbWU6", await harness.ReadClientLineAsync());
        await harness.WriteClientLineAsync("*");
        Assert.Equal("481 Authentication cancelled", await harness.ReadClientLineAsync());
        Assert.False(session.HasSaslExchange);
        Assert.False(session.Authentication.IsAuthenticated);

        await harness.WriteClientLineAsync("CAPABILITIES");
        Assert.StartsWith("101 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
        _ = await harness.ReadMultilineBodyAsync();

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task AllowAuthPlainDisabled_BlocksPlainLoginAndCram()
    {
        await using var harness = await AuthHarness.CreateAsync(
            MemoryNntpUserRecordStore.Create("alice", "secret", allowPlain: false));
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync($"AUTHINFO SASL PLAIN {Convert.ToBase64String("\0alice\0secret"u8.ToArray())}");
        Assert.Equal("481 Authentication failed", await harness.ReadClientLineAsync());

        await harness.WriteClientLineAsync("AUTHINFO SASL LOGIN");
        Assert.Equal("383 VXNlcm5hbWU6", await harness.ReadClientLineAsync());
        await harness.WriteClientLineAsync(Convert.ToBase64String("alice"u8.ToArray()));
        Assert.Equal("383 UGFzc3dvcmQ6", await harness.ReadClientLineAsync());
        await harness.WriteClientLineAsync(Convert.ToBase64String("secret"u8.ToArray()));
        Assert.Equal("481 Authentication failed", await harness.ReadClientLineAsync());

        await harness.WriteClientLineAsync("AUTHINFO SASL CRAM-MD5");
        var challengeLine = await harness.ReadClientLineAsync();
        Assert.StartsWith("383 ", challengeLine, StringComparison.Ordinal);
        await harness.WriteClientLineAsync(CramResponse("alice", "secret", challengeLine[4..]));
        Assert.Equal("481 Authentication failed", await harness.ReadClientLineAsync());
        Assert.False(session.Authentication.IsAuthenticated);

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task Plain_EmptyFields_Returns481()
    {
        await using var harness = await AuthHarness.CreateAsync(MemoryNntpUserRecordStore.Create("alice", "secret"));
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync($"AUTHINFO SASL PLAIN {Convert.ToBase64String("\0\0"u8.ToArray())}");
        Assert.Equal("481 Authentication failed", await harness.ReadClientLineAsync());
        Assert.False(session.Authentication.IsAuthenticated);

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task SaslChallenges_AreNotCopiedIntoDebugLogs()
    {
        const string password = "pencil";
        var salt = Convert.FromHexString("4142434445464748494A4B4C4D4E4F50");
        var keys = ScramClient.Derive(password, salt, 4096);
        var record = MemoryNntpUserRecordStore.Create(
            "user",
            password,
            scramSalt: salt,
            scramIterations: 4096,
            scramStoredKey: keys.StoredKey,
            scramServerKey: keys.ServerKey);
        var recording = new RecordingLoggerFactory();
        await using var harness = await AuthHarness.CreateAsync(record);
        var session = harness.CreateSession(loggerFactory: recording);
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        const string clientFirst = "n,,n=user,r=fyko+d2lbbFgONRv9qkxdawL";
        await harness.WriteClientLineAsync($"AUTHINFO SASL SCRAM-SHA-256 {Convert.ToBase64String(Encoding.UTF8.GetBytes(clientFirst))}");
        var serverFirstLine = await harness.ReadClientLineAsync();
        Assert.StartsWith("383 ", serverFirstLine, StringComparison.Ordinal);
        Assert.DoesNotContain("s=", serverFirstLine, StringComparison.Ordinal);
        await harness.WriteClientLineAsync("*");
        Assert.StartsWith("481 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);

        await harness.WriteClientLineAsync("AUTHINFO SASL CRAM-MD5");
        var cramLine = await harness.ReadClientLineAsync();
        Assert.StartsWith("383 ", cramLine, StringComparison.Ordinal);
        await harness.WriteClientLineAsync("*");
        Assert.StartsWith("481 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;

        var joined = string.Join('\n', recording.Messages);
        Assert.DoesNotContain(Convert.ToBase64String(salt), joined, StringComparison.Ordinal);
        Assert.DoesNotContain(serverFirstLine[4..], joined, StringComparison.Ordinal);
        Assert.DoesNotContain(cramLine[4..], joined, StringComparison.Ordinal);
        Assert.DoesNotContain(password, joined, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(keys.StoredKey), joined, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(keys.ServerKey), joined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sasl_CleartextDisabled_Returns483()
    {
        await using var harness = await AuthHarness.CreateAsync(MemoryNntpUserRecordStore.Create("alice", "secret"));
        var session = harness.CreateSession(allowCleartextAuth: false);
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("AUTHINFO SASL PLAIN");
        Assert.StartsWith("483 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
        await harness.WriteClientLineAsync("CAPABILITIES");
        await harness.ReadClientLineAsync();
        var caps = await harness.ReadMultilineBodyAsync();
        Assert.DoesNotContain(caps, static c => c.Contains("SASL", StringComparison.Ordinal));

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task LoginAndCram_InitialResponse_Returns482()
    {
        await using var harness = await AuthHarness.CreateAsync(MemoryNntpUserRecordStore.Create("alice", "secret"));
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync($"AUTHINFO SASL LOGIN {Convert.ToBase64String("alice"u8.ToArray())}");
        Assert.Equal("482 SASL protocol error", await harness.ReadClientLineAsync());
        await harness.WriteClientLineAsync($"AUTHINFO SASL CRAM-MD5 {Convert.ToBase64String("alice"u8.ToArray())}");
        Assert.Equal("482 SASL protocol error", await harness.ReadClientLineAsync());
        Assert.False(session.HasSaslExchange);

        await harness.WriteClientLineAsync("QUIT");
        await harness.ReadClientLineAsync();
        await run;
    }

    private static string CramResponse(string username, string password, string challenge)
    {
        var challengeBytes = Convert.FromBase64String(challenge);
#pragma warning disable CA5351
        using var hmac = new System.Security.Cryptography.HMACMD5(Encoding.ASCII.GetBytes(password));
#pragma warning restore CA5351
        var hex = Convert.ToHexString(hmac.ComputeHash(challengeBytes)).ToLowerInvariant();
        return Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username} {hex}"));
    }
}

internal sealed class AuthHarness : IAsyncDisposable
{
    private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
    private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());
    private readonly INntpAuthenticationProvider _provider;
    private readonly NntpSaslService _sasl;
    private readonly ISessionStateTracker _admission;
    private readonly IPAddress _clientIp;
    private readonly bool _isTls;

    private AuthHarness(
        INntpAuthenticationProvider provider,
        NntpSaslService sasl,
        ISessionStateTracker admission,
        IPAddress clientIp,
        bool isTls)
    {
        _provider = provider;
        _sasl = sasl;
        _admission = admission;
        _clientIp = clientIp;
        _isTls = isTls;
    }

    public static Task<AuthHarness> CreateAsync(
        params NntpUserRecord[] users) =>
        CreateAsync(Users(users));

    public static Task<AuthHarness> CreateAsync(
        NntpUserRecord record,
        ISessionStateTracker? admission = null,
        IPAddress? clientIp = null) =>
        CreateAsync(Users(record), admission: admission, clientIp: clientIp);

    public static Task<AuthHarness> CreateAsync(
        MemoryNntpUserRecordStore store,
        ISessionStateTracker? admission = null,
        IPAddress? clientIp = null,
        NntpdOptions? newsmaster = null,
        bool isTls = false,
        ScramDummyVerifier? dummyScram = null)
    {
        var validator = new MySqlNntpCredentialValidator(store, NullLogger<MySqlNntpCredentialValidator>.Instance);
        var newsmasterProvider = newsmaster is null
            ? DenyAllNntpAuthenticationProvider.Instance
            : NewsmasterNntpAuthenticationProvider.Create(newsmaster);
        var provider = new CompositeNntpAuthenticationProvider(
            newsmasterProvider,
            newsmaster?.NewsmasterUser,
            validator);
        var nntpd = newsmaster is not null && !string.IsNullOrWhiteSpace(newsmaster.Fqdn)
            ? newsmaster
            : new NntpdOptions { ServerId = 1, DnsSuffix = "usenet.ninja" };
        var sasl = new NntpSaslService(provider, validator, Options.Create(nntpd), dummyScram);
        return Task.FromResult(new AuthHarness(
            provider,
            sasl,
            admission ?? new InMemorySessionStateTracker(),
            clientIp ?? IPAddress.Loopback,
            isTls));
    }

    public NntpSession CreateSession(
        bool allowCleartextAuth = true,
        ILoggerFactory? loggerFactory = null,
        TimeSpan? commandIdleTimeout = null,
        TimeProvider? timeProvider = null)
    {
        var connection = new PipeNntpConnection(
            _clientToServer.Reader,
            _serverToClient.Writer,
            ConnectionClientIdentity.Direct(new IPEndPoint(_clientIp, 119)),
            isTls: _isTls);
        return new NntpSession(
            connection,
            loggerFactory?.CreateLogger<NntpSession>() ?? NullLogger<NntpSession>.Instance,
            authenticationProvider: _provider,
            allowCleartextAuth: allowCleartextAuth,
            loggerFactory: loggerFactory,
            sessionAdmission: _admission,
            saslService: _sasl,
            commandIdleTimeout: commandIdleTimeout,
            timeProvider: timeProvider);
    }

    public async Task AuthenticateAsync(string username, string password)
    {
        await WriteClientLineAsync($"AUTHINFO USER {username}");
        Assert.StartsWith("381 ", await ReadClientLineAsync(), StringComparison.Ordinal);
        await WriteClientLineAsync($"AUTHINFO PASS {password}");
        Assert.StartsWith("281 ", await ReadClientLineAsync(), StringComparison.Ordinal);
    }

    public async Task ReadGreetingAsync()
    {
        var line = await ReadClientLineAsync();
        Assert.StartsWith("20", line, StringComparison.Ordinal);
    }

    public Task CompleteClientInputAsync(Exception? error = null) =>
        _clientToServer.Writer.CompleteAsync(error).AsTask();

    public async Task WriteClientLineAsync(string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
        await _clientToServer.Writer.WriteAsync(bytes);
        await _clientToServer.Writer.FlushAsync();
    }

    public async Task<string> ReadClientLineAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var line = await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, cts.Token);
        Assert.NotNull(line);
        return line!;
    }

    public async Task<List<string>> ReadMultilineBodyAsync()
    {
        var lines = new List<string>();
        while (true)
        {
            var line = await ReadClientLineAsync();
            if (line == ".")
            {
                break;
            }

            lines.Add(line);
        }

        return lines;
    }

    public async ValueTask DisposeAsync()
    {
        await _clientToServer.Writer.CompleteAsync();
        await _clientToServer.Reader.CompleteAsync();
        await _serverToClient.Writer.CompleteAsync();
        await _serverToClient.Reader.CompleteAsync();
    }

    private static MemoryNntpUserRecordStore Users(params NntpUserRecord[] records)
    {
        var store = new MemoryNntpUserRecordStore();
        foreach (var record in records)
        {
            store.Add(record);
        }

        return store;
    }

    private sealed class PipeNntpConnection : INntpConnection
    {
        private readonly CancellationTokenSource _cts = new();
        private int _compressed;

        public PipeNntpConnection(
            PipeReader input,
            PipeWriter output,
            ConnectionClientIdentity identity,
            bool isTls)
        {
            Input = input;
            Output = output;
            ClientIdentity = identity;
            IsTls = isTls;
        }

        public PipeReader Input { get; }
        public PipeWriter Output { get; }
        public EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;
        public EndPoint? LocalEndPoint => null;
        public ConnectionClientIdentity ClientIdentity { get; }
        public bool IsTls { get; }

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
        {
            tlsVersion = string.Empty;
            cipher = string.Empty;
            return false;
        }

        public bool IsCompressed => Volatile.Read(ref _compressed) == 1;
        public CancellationToken ConnectionClosed => _cts.Token;
        public bool IsCompleted => _cts.IsCancellationRequested;
        public long OutboundIdleVersion => 0;

        public Task PauseReadsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WaitForOutboundDeliveryAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WaitForOutboundDeliveryAndPauseReadsAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CompleteAsync(Exception? exception = null)
        {
            _cts.Cancel();
            return Task.CompletedTask;
        }

        public Task UpgradeToTlsAsync(
            ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _compressed, 1);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    public ConcurrentBag<string> Messages { get; } = [];

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(Messages);

    public void Dispose()
    {
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly ConcurrentBag<string> _messages;

        public RecordingLogger(ConcurrentBag<string> messages) => _messages = messages;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _messages.Add(formatter(state, exception));
        }
    }
}

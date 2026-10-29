using System.Data;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.NntpDb;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.TestDoubles;
using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.Tests.Transit;

public sealed class TransitCatalogueTests
{
    private const long SharedSendMaxArticleBytes = 5_242_880;

    [Fact]
    public void MigratedPublication_ReconstructsTheThreePeers()
    {
        var snapshot = TransitCatalogueCompiler.Compile(MigratedPublication());

        Assert.Equal(1, snapshot.PublicationId);
        Assert.True(snapshot.WantTrash);
        Assert.True(snapshot.LogTrash);
        Assert.Equal(3, snapshot.Peers.Count);
        Assert.Equal(
            ["blueworld-hosting", "giganews", "usenet-ninja"],
            snapshot.Peers.Keys.OrderBy(static key => key, StringComparer.Ordinal));

        var blueworld = snapshot.Peers["blueworld-hosting"];
        Assert.Equal("blueworld-hosting", blueworld.Identifier);
        Assert.Equal("Blueworld Hosting", blueworld.PeerName);
        Assert.Equal(["usenet.blueworldhosting.com"], blueworld.DnsHostnames);
        Assert.Empty(blueworld.LiteralPrefixes);
        Assert.Equal(10, blueworld.MaxIncomingConnections);
        Assert.False(blueworld.HasPeerCredentials);
        Assert.True(blueworld.DeferOnDuplicate);
        Assert.Equal(1_048_576, blueworld.MaxSize);
        Assert.Equal("usenet.blueworldhosting.com", blueworld.ConnectTo[0].Host);
        Assert.Equal(119, blueworld.ConnectTo[0].Port);
        Assert.Equal(10, blueworld.MaxOutgoingConnections);
        Assert.Equal(TransitSslMode.None, blueworld.Ssl);
        Assert.False(blueworld.HasSendCredentials);
        Assert.Equal("*,!unidata.*,!control.*,!junk", blueworld.Patterns.Expression);
        Assert.Equal(TransitMessageTypes.All, blueworld.MessageTypes);
        Assert.Equal("usenet.blueworldhosting.com", blueworld.PathToken);
        Assert.Empty(blueworld.PathExclusions);

        var giganews = snapshot.Peers["giganews"];
        Assert.Equal("giganews", giganews.Identifier);
        Assert.Equal("Giganews, Inc.", giganews.PeerName);
        Assert.Equal(["news-out.nntp.giganews.com"], giganews.DnsHostnames);
        Assert.Equal(80, giganews.MaxIncomingConnections);
        Assert.Equal(5_242_880, giganews.MaxSize);
        Assert.Equal("opticnetworks-in.nntp.ord.giganews.com", giganews.ConnectTo[0].Host);
        Assert.Equal(119, giganews.ConnectTo[0].Port);
        Assert.Equal(80, giganews.MaxOutgoingConnections);
        Assert.Equal("*,!unidata.*", giganews.Patterns.Expression);
        Assert.Equal("nntp.giganews.com", giganews.PathToken);

        var local = snapshot.Peers["usenet-ninja"];
        Assert.Equal("usenet-ninja", local.Identifier);
        Assert.Equal("Usenet Ninja", local.PeerName);
        Assert.Empty(local.DnsHostnames);
        Assert.True(local.LiteralPrefixes[0].Contains(IPAddress.Parse("198.18.1.1")));
        Assert.False(local.LiteralPrefixes[0].Contains(IPAddress.Parse("203.0.113.1")));
        Assert.Equal(10, local.MaxIncomingConnections);
        Assert.Equal(5_242_880, local.MaxSize);
        Assert.Empty(local.ConnectTo);
        Assert.Equal(10, local.MaxOutgoingConnections);
        Assert.Equal("*", local.Patterns.Expression);
        Assert.Equal("nntp.usenet.ninja", local.PathToken);
    }

    [Fact]
    public void MigratedPublication_KeepsIntentionalDefaultsDistinctFromJson()
    {
        var snapshot = TransitCatalogueCompiler.Compile(MigratedPublication());
        foreach (var peer in snapshot.Peers.Values)
        {
            Assert.Equal(TransitMessageTypes.All, peer.ReceiveArticleTypes);
            Assert.Equal("*", peer.ReceivePatterns.Expression);
            Assert.Equal(SharedSendMaxArticleBytes, peer.SendMaxArticleBytes);
        }

        Assert.NotEqual(
            snapshot.Peers["blueworld-hosting"].MaxSize,
            snapshot.Peers["blueworld-hosting"].SendMaxArticleBytes);
    }

    [Fact]
    public void Planes_KeepArticleMasksSizesAndPatternsIndependent()
    {
        var peer = Peer(
            identifier: "planes",
            receiveArticleTypes: (int)TransitMessageTypes.Default,
            sendArticleTypes: (int)TransitMessageTypes.All,
            receiveMaxArticleBytes: 1_048_576,
            sendMaxArticleBytes: SharedSendMaxArticleBytes,
            receivePatterns: "*",
            sendPatterns: "*,!unidata.*");
        var compiled = TransitCatalogueCompiler.Compile(Publication(peer)).Peers["planes"];

        Assert.Equal(TransitMessageTypes.Default, compiled.ReceiveArticleTypes);
        Assert.Equal(TransitMessageTypes.All, compiled.MessageTypes);
        Assert.Equal(1_048_576, compiled.MaxSize);
        Assert.Equal(SharedSendMaxArticleBytes, compiled.SendMaxArticleBytes);
        Assert.True(compiled.ReceivePatterns.MatchesNewsgroup("unidata.maps"));
        Assert.False(compiled.Patterns.MatchesNewsgroup("unidata.maps"));
        Assert.True(compiled.Patterns.MatchesNewsgroup("comp.lang.c"));
    }

    [Fact]
    public void ArticleTypeMasks_MatchTheCapabilityContract()
    {
        Assert.Equal(65535, (int)TransitMessageTypes.All);
        Assert.Equal(1, (int)TransitMessageTypes.Default);
        Assert.Equal(0, (int)TransitMessageTypes.None);
        Assert.Equal(65535, TransitCatalogueCompiler.UnrestrictedArticleTypes);

        Assert.True(ArticleTypeCapabilities.Allows((ArticleType)65535, ArticleType.Binary | ArticleType.YEncoded));
        Assert.True(ArticleTypeCapabilities.Allows((ArticleType)1, ArticleType.Default));
        Assert.False(ArticleTypeCapabilities.Allows((ArticleType)1, ArticleType.Control));
        Assert.False(ArticleTypeCapabilities.Allows((ArticleType)0, ArticleType.Default));
        Assert.False(ArticleTypeCapabilities.Allows((ArticleType)0, ArticleType.Control));
        Assert.False(ArticleTypeCapabilities.Allows((ArticleType)0, ArticleTypeCapabilities.All));

        var star = TransitCatalogueCompiler.Compile(Publication(Peer(receivePatterns: "*", sendPatterns: "*")));
        Assert.True(star.Peers["peer"].ReceivePatterns.MatchesNewsgroup("comp.lang.c"));
        Assert.True(star.Peers["peer"].Patterns.MatchesNewsgroup("alt.test"));
    }

    [Fact]
    public void ZeroConnectionLimits_CloseThatPlane()
    {
        var compiled = TransitCatalogueCompiler.Compile(Publication(Peer(maxInbound: 0, maxOutbound: 0)))
            .Peers["peer"];
        Assert.Equal(0, compiled.MaxIncomingConnections);
        Assert.Equal(0, compiled.MaxOutgoingConnections);

        var engine = new TransitPeerStateEngine();
        Assert.Equal(
            TransitPeerStateEngine.Rejected,
            engine.TryAdmit("peer", "owner", compiled.MaxIncomingConnections, 1, 30_000, 1));
        Assert.Equal(
            TransitPeerStateEngine.Accepted,
            engine.TryAdmit("peer", "owner", 1, 1, 30_000, 1));
    }

    [Fact]
    public void InvalidArticleTypesPatternsAndDuplicateEndpoints_AreRejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TransitCatalogueCompiler.Compile(Publication(Peer(receiveArticleTypes: 65536))));
        Assert.Throws<InvalidOperationException>(() =>
            TransitCatalogueCompiler.Compile(Publication(Peer(sendArticleTypes: 65536))));
        Assert.Throws<InvalidOperationException>(() =>
            TransitCatalogueCompiler.Compile(Publication(Peer(receivePatterns: ""))));
        Assert.Throws<InvalidOperationException>(() =>
            TransitCatalogueCompiler.Compile(Publication(Peer(receivePatterns: "   "))));
        Assert.Throws<InvalidOperationException>(() =>
            TransitCatalogueCompiler.Compile(Publication(Peer(sendPatterns: "*,"))));
        Assert.Throws<InvalidOperationException>(() =>
            TransitCatalogueCompiler.Compile(Publication(Peer(
                endpoints:
                [
                    new TransitCatalogueEndpointRecord("News.Example", 119),
                    new TransitCatalogueEndpointRecord("news.example", 119),
                ]))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1_000_000L)]
    [InlineData(5_242_880L)]
    public void SendMaxArticleBytes_PreservesUnlimitedAndExplicitCeilings(long? sendMaxArticleBytes)
    {
        var compiled = TransitCatalogueCompiler.Compile(
            Publication(Peer(sendMaxArticleBytes: sendMaxArticleBytes))).Peers["peer"];
        Assert.Equal(sendMaxArticleBytes, compiled.SendMaxArticleBytes);
        Assert.Equal(SharedSendMaxArticleBytes, compiled.MaxSize);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(2_147_483_648L)]
    public void ExplicitSendMaxArticleBytes_RejectsNonPositiveAndAboveIntMax(long sendMaxArticleBytes)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TransitCatalogueCompiler.Compile(Publication(Peer(sendMaxArticleBytes: sendMaxArticleBytes))));
        Assert.Contains("send max_article_bytes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReceiveMaxArticleBytes_StaysAnExplicitCeiling()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TransitCatalogueCompiler.Compile(Publication(Peer(receiveMaxArticleBytes: 0))));
        Assert.Contains("receive max_article_bytes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Service_PublishesUnlimitedSendSizeWithoutANumericDefault()
    {
        var factory = new FakeNntpDbConnectionFactory { TransitCatalogue = PeerSendPublication() };
        var database = await StartDatabaseAsync(factory);
        var store = new TransitConfigurationStore();
        var services = new ServiceCollection();
        services.AddOptions<NntpdOptions>();
        using var provider = services.BuildServiceProvider();
        var service = new TransitCatalogueService(
            database,
            store,
            provider.GetRequiredService<IOptionsMonitor<NntpdOptions>>(),
            NullLogger<TransitCatalogueService>.Instance);
        await service.StartAsync(CancellationToken.None);

        Assert.Equal(2, store.Current.PublicationId);
        Assert.Null(store.Current.Peers["giganews"].SendMaxArticleBytes);
        Assert.Equal(50, store.Current.Peers["giganews"].MaxOutgoingConnections);
        Assert.Equal(1_000_000, store.Current.Peers["blueworld-hosting"].SendMaxArticleBytes);
        Assert.Equal(10, store.Current.Peers["blueworld-hosting"].MaxOutgoingConnections);
        Assert.Equal(SharedSendMaxArticleBytes, store.Current.Peers["usenet-ninja"].SendMaxArticleBytes);
        Assert.Equal(5_242_880, store.Current.Peers["giganews"].MaxSize);
        Assert.Equal(1_048_576, store.Current.Peers["blueworld-hosting"].MaxSize);
        await service.DisposeAsync();
        await database.DisposeAsync();
    }

    [Fact]
    public void SqlSendSize_RoundTripsToSendPolicy()
    {
        using var table = new DataTable();
        table.Columns.Add("max_article_bytes", typeof(int));
        table.Rows.Add(DBNull.Value);
        table.Rows.Add(1_000_000);
        table.Rows.Add(5_242_880);
        using var reader = table.CreateDataReader();
        var sizes = new List<int?>();
        while (reader.Read())
        {
            sizes.Add(NntpTransitCatalogueLoader.ReadSendMaxArticleBytes(reader, 0));
        }

        var snapshot = TransitCatalogueCompiler.Compile(new TransitCatalogueRecord(
            2,
            1,
            2,
            1,
            true,
            true,
            [
                Peer(
                    "giganews",
                    "Giganews, Inc.",
                    80,
                    5_242_880,
                    ["news-out.nntp.giganews.com"],
                    50,
                    [new TransitCatalogueEndpointRecord("opticnetworks-in.nntp.ord.giganews.com", 119)],
                    "*,!unidata.*",
                    "nntp.giganews.com",
                    sendMaxArticleBytes: (long?)sizes[0]),
                Peer(
                    "blueworld-hosting",
                    "Blueworld Hosting",
                    10,
                    1_048_576,
                    ["usenet.blueworldhosting.com"],
                    10,
                    [new TransitCatalogueEndpointRecord("usenet.blueworldhosting.com", 119)],
                    "*,!unidata.*,!control.*,!junk",
                    "usenet.blueworldhosting.com",
                    sendMaxArticleBytes: (long?)sizes[1]),
                Peer(
                    "usenet-ninja",
                    "Usenet Ninja",
                    10,
                    5_242_880,
                    ["198.18.0.0/15"],
                    10,
                    [],
                    "*",
                    "nntp.usenet.ninja",
                    sendMaxArticleBytes: (long?)sizes[2]),
            ]));

        Assert.Null(snapshot.Peers["giganews"].SendMaxArticleBytes);
        Assert.Equal("*,!unidata.*", snapshot.Peers["giganews"].Patterns.Expression);
        Assert.Equal(TransitMessageTypes.All, snapshot.Peers["giganews"].MessageTypes);
        Assert.Equal(1_000_000, snapshot.Peers["blueworld-hosting"].SendMaxArticleBytes);
        Assert.Equal("*,!unidata.*,!control.*,!junk", snapshot.Peers["blueworld-hosting"].Patterns.Expression);
        Assert.False(snapshot.Peers["blueworld-hosting"].Patterns.Expression.Contains("!local", StringComparison.Ordinal));
        Assert.Equal(SharedSendMaxArticleBytes, snapshot.Peers["usenet-ninja"].SendMaxArticleBytes);
        Assert.Equal(80, snapshot.Peers["giganews"].MaxIncomingConnections);
        Assert.Equal(10, snapshot.Peers["blueworld-hosting"].MaxIncomingConnections);
    }

    [Fact]
    public void LeftoverJson_IsRejected_ProcessLocalDepthRemains()
    {
        var peers = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Transit:alpha:PeerName"] = "Alpha",
        }).Build();
        Assert.True(new TransitLeftoverConfigurationValidator(peers).Validate(null, new NntpdOptions()).Failed);

        var flags = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Nntpd:Transit:LogTrash"] = "false",
        }).Build();
        var flagsResult = new TransitLeftoverConfigurationValidator(flags).Validate(null, new NntpdOptions());
        Assert.True(flagsResult.Failed);
        Assert.Contains("nntptransitglobalrevision", flagsResult.FailureMessage, StringComparison.Ordinal);

        var local = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Nntpd:Transit:StreamOutstandingArticleDepth"] = "8",
            ["Nntpd:TransitQueueMemoryLimit"] = "4294967296",
        }).Build();
        Assert.True(new TransitLeftoverConfigurationValidator(local).Validate(null, new NntpdOptions()).Succeeded);
    }

    [Fact]
    public async Task Service_PublishesOneCatalogue_AndMissingCurrentFailsStartup()
    {
        var factory = new FakeNntpDbConnectionFactory { TransitCatalogue = MigratedPublication() };
        var database = await StartDatabaseAsync(factory);
        var store = new TransitConfigurationStore();
        var services = new ServiceCollection();
        services.AddOptions<NntpdOptions>();
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<NntpdOptions>>();
        var service = new TransitCatalogueService(
            database,
            store,
            monitor,
            NullLogger<TransitCatalogueService>.Instance);
        await service.StartAsync(CancellationToken.None);

        Assert.Equal(3, store.Current.Peers.Count);
        Assert.Equal(1, store.Current.PublicationId);
        Assert.True(monitor.CurrentValue.Transit.WantTrash);
        Assert.True(monitor.CurrentValue.Transit.LogTrash);
        await service.DisposeAsync();
        await database.DisposeAsync();

        var missingFactory = new FakeNntpDbConnectionFactory { TransitCatalogue = null };
        var missingDatabase = await StartDatabaseAsync(missingFactory);
        var missing = new TransitCatalogueService(
            missingDatabase,
            new TransitConfigurationStore(),
            monitor,
            NullLogger<TransitCatalogueService>.Instance);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => missing.StartAsync(CancellationToken.None));
        Assert.Contains("nntptransitcurrent", ex.Message, StringComparison.Ordinal);
        await missing.DisposeAsync();
        await missingDatabase.DisposeAsync();
    }

    [Fact]
    public void EmptyJsonReload_DoesNotWipeThePublishedSnapshot()
    {
        var store = new TransitConfigurationStore();
        store.Replace(TransitCatalogueCompiler.Compile(MigratedPublication()));
        using var manager = new ConfigurationManager();
        var services = new ServiceCollection();
        services.AddLogging(static builder => builder.ClearProviders());
        services.AddOptions<TransitPeersOptions>().Bind(manager.GetSection("Transit"));
        services.AddSingleton<IOptionsChangeTokenSource<TransitPeersOptions>>(
            new ConfigurationChangeTokenSource<TransitPeersOptions>(manager));
        services.AddSingleton(store);
        services.AddSingleton<TransitConfigurationHotReload>();
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<TransitConfigurationHotReload>();

        manager["Nntpd:Transit:StreamOutstandingArticleDepth"] = "8";
        ((IConfigurationRoot)manager).Reload();

        Assert.Equal(3, store.Current.Peers.Count);
        Assert.Equal("nntp.giganews.com", store.Current.Peers["giganews"].PathToken);
    }

    private static async Task<NntpDbService> StartDatabaseAsync(FakeNntpDbConnectionFactory factory)
    {
        var database = new NntpDbService(
            factory,
            Options.Create(new NntpDbOptions { ConnectionString = TestHostFactory.TestNntpDbConnectionString }),
            NullLogger<NntpDbService>.Instance);
        await database.StartAsync(CancellationToken.None);
        return database;
    }

    private static TransitCatalogueRecord PeerSendPublication() =>
        new(
            2,
            1,
            2,
            1,
            true,
            true,
            [
                Peer(
                    "blueworld-hosting",
                    "Blueworld Hosting",
                    10,
                    1_048_576,
                    ["usenet.blueworldhosting.com"],
                    10,
                    [new TransitCatalogueEndpointRecord("usenet.blueworldhosting.com", 119)],
                    "*,!unidata.*,!control.*,!junk",
                    "usenet.blueworldhosting.com",
                    sendMaxArticleBytes: 1_000_000),
                Peer(
                    "giganews",
                    "Giganews, Inc.",
                    80,
                    5_242_880,
                    ["news-out.nntp.giganews.com"],
                    50,
                    [new TransitCatalogueEndpointRecord("opticnetworks-in.nntp.ord.giganews.com", 119)],
                    "*,!unidata.*",
                    "nntp.giganews.com",
                    sendMaxArticleBytes: null),
                Peer(
                    "usenet-ninja",
                    "Usenet Ninja",
                    10,
                    5_242_880,
                    ["198.18.0.0/15"],
                    10,
                    [],
                    "*",
                    "nntp.usenet.ninja"),
            ]);

    private static TransitCatalogueRecord MigratedPublication() =>
        new(
            1,
            1,
            1,
            1,
            true,
            true,
            [
                Peer(
                    "blueworld-hosting",
                    "Blueworld Hosting",
                    10,
                    1_048_576,
                    ["usenet.blueworldhosting.com"],
                    10,
                    [new TransitCatalogueEndpointRecord("usenet.blueworldhosting.com", 119)],
                    "*,!unidata.*,!control.*,!junk",
                    "usenet.blueworldhosting.com"),
                Peer(
                    "giganews",
                    "Giganews, Inc.",
                    80,
                    5_242_880,
                    ["news-out.nntp.giganews.com"],
                    80,
                    [new TransitCatalogueEndpointRecord("opticnetworks-in.nntp.ord.giganews.com", 119)],
                    "*,!unidata.*",
                    "nntp.giganews.com"),
                Peer(
                    "usenet-ninja",
                    "Usenet Ninja",
                    10,
                    5_242_880,
                    ["198.18.0.0/15"],
                    10,
                    [],
                    "*",
                    "nntp.usenet.ninja"),
            ]);

    private static TransitCatalogueRecord Publication(TransitCataloguePeerRecord peer) =>
        new(1, 1, 1, 1, true, true, [peer]);

    private static TransitCataloguePeerRecord Peer(
        string identifier = "peer",
        string peerName = "Peer",
        int maxInbound = 1,
        long receiveMaxArticleBytes = SharedSendMaxArticleBytes,
        IReadOnlyList<string>? allowFrom = null,
        int maxOutbound = 1,
        IReadOnlyList<TransitCatalogueEndpointRecord>? endpoints = null,
        string sendPatterns = "*",
        string pathToken = "peer.example",
        int receiveArticleTypes = 65535,
        int sendArticleTypes = 65535,
        long? sendMaxArticleBytes = SharedSendMaxArticleBytes,
        string receivePatterns = "*") =>
        new(
            identifier,
            peerName,
            maxInbound,
            string.Empty,
            string.Empty,
            true,
            receiveMaxArticleBytes,
            receiveArticleTypes,
            receivePatterns,
            allowFrom ?? [],
            maxOutbound,
            "None",
            string.Empty,
            string.Empty,
            sendMaxArticleBytes,
            sendArticleTypes,
            sendPatterns,
            pathToken,
            endpoints ?? [],
            []);
}

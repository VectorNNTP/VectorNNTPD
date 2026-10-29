using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Messaging.Cache;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Newsgroups;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.ReaderService.RabbitMq.ArticleWork;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Storage;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.RabbitMq;
using VectorNNTPD.ReaderService.Tests.TestDoubles;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.ReaderService.Tests.Session;

/// <summary>
/// ARTICLE/HEAD/BODY/STAT argument-form failure codes and message-id RPC/VATP wiring prerequisites.
/// Successful message-id retrieval is covered by <see cref="ArticleRetrievalVatpCommandTests"/>.
/// </summary>
public sealed class ArticleRetrievalCommandTests
{
    private static readonly NntpAuthorization Reader = new(
        isAuthenticated: true,
        authorizedReader: true,
        authorizedTransit: false,
        postingPermitted: false,
        streamingPermitted: false);

    public static TheoryData<string> RetrievalVerbs() =>
        new()
        {
            "ARTICLE",
            "HEAD",
            "BODY",
            "STAT",
        };

    [Fact]
    public async Task ArticleMessageId_InvokesRpc_WithoutVatp_Returns400()
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new RecordingArticleWorkRpcClient();
        var session = duplex.CreateSession(articleWorkRpc: rpc, storageLookup: SilenceLookup());
        await DispatchLineAsync(duplex, session, "ARTICLE <12345@example.invalid>");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        var messageId = Assert.Single(rpc.Lookups);
        Assert.Equal("<12345@example.invalid>"u8.ToArray(), messageId.ToArray());
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task MessageId_WithRpcOnly_InvokesRpc_Returns400(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new RecordingArticleWorkRpcClient();
        var session = duplex.CreateSession(articleWorkRpc: rpc, storageLookup: SilenceLookup());
        await DispatchLineAsync(duplex, session, $"{verb} <12345@example.invalid>");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        Assert.Single(rpc.Lookups);
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task MessageId_Returns430_WithoutSelectingGroup(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession();
        await DispatchLineAsync(duplex, session, $"{verb} <does-not-exist@example.com>");
        Assert.Equal("430 No article with that message-id", await duplex.ReadClientLineAsync());
        Assert.False(session.HasSelectedGroup);
        await AssertNextCommandStillWorksAsync(duplex, session);
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task Number_WithoutGroup_Returns412(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession();
        await DispatchLineAsync(duplex, session, $"{verb} 123456");
        Assert.Equal("412 No newsgroup selected", await duplex.ReadClientLineAsync());
        Assert.False(session.HasSelectedGroup);
        await AssertNextCommandStillWorksAsync(duplex, session);
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task Number_WithGroup_Returns423_AndDoesNotChangeSelection(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession(SampleSnapshot());
        await DispatchLineAsync(duplex, session, "GROUP misc.test");
        Assert.StartsWith("211 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.True(session.HasSelectedGroup);
        var selected = session.SelectedGroupName.ToArray();

        await DispatchLineAsync(duplex, session, $"{verb} 123456");
        Assert.Equal("423 No article with that number", await duplex.ReadClientLineAsync());
        Assert.True(session.HasSelectedGroup);
        Assert.True(session.SelectedGroupName.SequenceEqual(selected));
        await AssertNextCommandStillWorksAsync(duplex, session);
        Assert.True(session.SelectedGroupName.SequenceEqual(selected));
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task Current_WithoutGroup_Returns412(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession();
        await DispatchLineAsync(duplex, session, verb);
        Assert.Equal("412 No newsgroup selected", await duplex.ReadClientLineAsync());
        Assert.False(session.HasSelectedGroup);
        await AssertNextCommandStillWorksAsync(duplex, session);
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task Current_WithGroup_Returns420_BecauseNoCurrentArticleExists(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession(SampleSnapshot());
        await DispatchLineAsync(duplex, session, "GROUP misc.test");
        Assert.StartsWith("211 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        var selected = session.SelectedGroupName.ToArray();

        await DispatchLineAsync(duplex, session, verb);
        Assert.Equal("420 Current article number is invalid", await duplex.ReadClientLineAsync());
        Assert.True(session.SelectedGroupName.SequenceEqual(selected));
        await AssertNextCommandStillWorksAsync(duplex, session);
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task FailedNumberLookup_DoesNotEstablishCurrentArticle(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession(SampleSnapshot());
        await DispatchLineAsync(duplex, session, "GROUP misc.test");
        Assert.StartsWith("211 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        var selected = session.SelectedGroupName.ToArray();

        await DispatchLineAsync(duplex, session, $"{verb} 123456");
        Assert.Equal("423 No article with that number", await duplex.ReadClientLineAsync());

        await DispatchLineAsync(duplex, session, verb);
        Assert.Equal("420 Current article number is invalid", await duplex.ReadClientLineAsync());
        Assert.True(session.SelectedGroupName.SequenceEqual(selected));
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task MessageIdVsNumber_AreDistinctNotFoundCodes(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession(SampleSnapshot());

        await DispatchLineAsync(duplex, session, $"{verb} <123@example.com>");
        Assert.Equal("430 No article with that message-id", await duplex.ReadClientLineAsync());

        await DispatchLineAsync(duplex, session, "GROUP misc.test");
        Assert.StartsWith("211 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await DispatchLineAsync(duplex, session, $"{verb} 123");
        Assert.Equal("423 No article with that number", await duplex.ReadClientLineAsync());
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task MalformedAngleBracket_Is501_NotAnArticleNumber(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession(SampleSnapshot());
        await DispatchLineAsync(duplex, session, "GROUP misc.test");
        Assert.StartsWith("211 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await DispatchLineAsync(duplex, session, $"{verb} <123@example.com");
        Assert.Equal("501 Syntax error", await duplex.ReadClientLineAsync());
        Assert.True(session.SelectedGroupName.SequenceEqual("misc.test"u8));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("0001")]
    [InlineData("2147483647")]
    [InlineData("2147483648")]
    [InlineData("9999999999999999")]
    public async Task DigitTokenThrough16Digits_IsNumberForm_423WhenGroupSelected(string number)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession(SampleSnapshot());
        await DispatchLineAsync(duplex, session, "GROUP misc.test");
        Assert.StartsWith("211 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        var selected = session.SelectedGroupName.ToArray();

        await DispatchLineAsync(duplex, session, "ARTICLE " + number);
        Assert.Equal("423 No article with that number", await duplex.ReadClientLineAsync());
        Assert.True(session.SelectedGroupName.SequenceEqual(selected));
        await DispatchLineAsync(duplex, session, "ARTICLE");
        Assert.Equal("420 Current article number is invalid", await duplex.ReadClientLineAsync());
    }

    [Theory]
    [InlineData("10000000000000000")]
    [InlineData("12345678901234567")]
    [InlineData("12a")]
    [InlineData("-1")]
    public async Task OutsideRfcDigitSyntax_Returns501(string token)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession(SampleSnapshot());
        await DispatchLineAsync(duplex, session, "GROUP misc.test");
        Assert.StartsWith("211 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await DispatchLineAsync(duplex, session, "ARTICLE " + token);
        Assert.Equal("501 Syntax error", await duplex.ReadClientLineAsync());
        Assert.True(session.SelectedGroupName.SequenceEqual("misc.test"u8));
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task Error_IsSingleLine_WithoutMultilineTerminator(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession(SampleSnapshot());

        await DispatchLineAsync(duplex, session, $"{verb} <missing@example.com>");
        Assert.Equal("430 No article with that message-id", await duplex.ReadClientLineAsync());
        await DispatchLineAsync(duplex, session, "DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await DispatchLineAsync(duplex, session, $"{verb} 123");
        Assert.Equal("412 No newsgroup selected", await duplex.ReadClientLineAsync());
        await DispatchLineAsync(duplex, session, "DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await DispatchLineAsync(duplex, session, "GROUP misc.test");
        Assert.StartsWith("211 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await DispatchLineAsync(duplex, session, $"{verb} 123");
        Assert.Equal("423 No article with that number", await duplex.ReadClientLineAsync());
        await DispatchLineAsync(duplex, session, "DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await DispatchLineAsync(duplex, session, verb);
        Assert.Equal("420 Current article number is invalid", await duplex.ReadClientLineAsync());
        await DispatchLineAsync(duplex, session, "DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SequentialFailedNumberLookups_DoNotChangeGroupOrCurrentArticle()
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession(SampleSnapshot());
        await DispatchLineAsync(duplex, session, "GROUP misc.test");
        Assert.StartsWith("211 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        var selected = session.SelectedGroupName.ToArray();

        foreach (var verb in new[] { "ARTICLE", "HEAD", "BODY", "STAT" })
        {
            await DispatchLineAsync(duplex, session, $"{verb} 123456");
            Assert.Equal("423 No article with that number", await duplex.ReadClientLineAsync());
            Assert.True(session.SelectedGroupName.SequenceEqual(selected));
        }

        await DispatchLineAsync(duplex, session, "STAT");
        Assert.Equal("420 Current article number is invalid", await duplex.ReadClientLineAsync());
        Assert.True(session.SelectedGroupName.SequenceEqual(selected));
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task InvalidArgument_Returns501_NotALookupFailure(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession();
        await DispatchLineAsync(duplex, session, $"{verb} not-an-id");
        Assert.Equal("501 Syntax error", await duplex.ReadClientLineAsync());
        await AssertNextCommandStillWorksAsync(duplex, session);
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task Unauthenticated_StillReturns480(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession(authorize: false);
        await DispatchLineAsync(duplex, session, $"{verb} <a@b.c>");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public void Catalog_RequiresAuthenticationAndReader(string verb)
    {
        var parsed = NntpCommandTestParse.ParseCommand(verb);
        Assert.Equal(
            NntpCommandAccess.RequiresAuthentication | NntpCommandAccess.RequiresReader,
            DefaultNntpCommandCatalog.GetAccess(parsed.Verb, parsed.Qualifier));
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task EmptyRegistry_SkipsStorageRpc_AndStillCallsBackFiller(string verb)
    {
        var registry = new StorageServerRegistry();
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var lookup = CreateLookup(rabbit, registry);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new RecordingArticleWorkRpcClient();
        var session = duplex.CreateSession(articleWorkRpc: rpc, storageLookup: lookup);

        await DispatchLineAsync(duplex, session, $"{verb} <12345@example.invalid>");

        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        Assert.Single(rpc.Lookups);
        Assert.Null(factory.LastConnection);
        Assert.Null(lookup.CurrentReplyTo);
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task EmptyRegistry_BackFillerMiss_Returns430(string verb)
    {
        var registry = new StorageServerRegistry();
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var lookup = CreateLookup(rabbit, registry);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new RecordingArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound);
        var session = duplex.CreateSession(articleWorkRpc: rpc, storageLookup: lookup);

        await DispatchLineAsync(duplex, session, $"{verb} <missing@example.com>");

        Assert.Equal("430 No article with that message-id", await duplex.ReadClientLineAsync());
        Assert.False(session.HasSelectedGroup);
        Assert.Single(rpc.Lookups);
        Assert.Null(factory.LastConnection);
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task RegisteredStorageServer_PublishesFleetLookup_AndDoesNotCallBackFiller(string verb)
    {
        var registry = new StorageServerRegistry();
        var now = DateTimeOffset.Parse("2026-10-04T12:00:00Z");
        registry.ApplyAdvertisement(
            new StorageServerAdvertisement(1, 1, "cache01.usenet.ninja", 1000, 100, 900, now, 1191),
            now);
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var time = new FakeTimeProvider();
        var lookup = CreateLookup(rabbit, registry, time);
        await rabbit.StartAsync(CancellationToken.None);
        await lookup.StartAsync(CancellationToken.None);
        try
        {
            await using var duplex = await ArticleDuplex.CreateAsync();
            var rpc = new RecordingArticleWorkRpcClient();
            var session = duplex.CreateSession(articleWorkRpc: rpc, storageLookup: lookup);
            var dispatch = DispatchLineAsync(duplex, session, $"{verb} <12345@example.invalid>");
            var publication = await WaitForFleetPublicationAsync(factory);
            Assert.Equal(CacheFleetTopology.RequestsExchangeName, publication.Exchange);
            Assert.True(StorageArticleLookupWireProtocol.TryParseRequestV1(publication.Body, out var request, out _));
            Assert.NotNull(request);
            var consume = factory.LastConnection!.RpcChannels.First(static channel => channel.ConsumedQueue is not null);
            await consume.DeliverAsync(
                publication.CorrelationId,
                StorageArticleLookupWireProtocol.SerializeResponseV1(
                    new StorageArticleLookupResponse(
                        1,
                        request!.RequestId,
                        1,
                        "cache01.usenet.ninja",
                        request.ArticleId,
                        1191)));

            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
            Assert.Empty(rpc.Lookups);
        }
        finally
        {
            await lookup.StopAsync(CancellationToken.None);
        }
    }

    private static NewsgroupSnapshot SampleSnapshot() =>
        NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("misc.test", "General testing", 3002322, 3000234, NewsgroupPostingStatus.Allowed),
        ]);

    private static async Task DispatchLineAsync(ArticleDuplex duplex, NntpSession session, string command)
    {
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, command);
    }

    private static async Task AssertNextCommandStillWorksAsync(ArticleDuplex duplex, NntpSession session)
    {
        await DispatchLineAsync(duplex, session, "DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    private static SilenceStorageLookup SilenceLookup() => new();

    private sealed class SilenceStorageLookup : IStorageArticleLookupClient
    {
        public Task<StorageArticleLookupResult> LookupAsync(ArticleId articleId, CancellationToken cancellationToken) =>
            Task.FromResult(StorageArticleLookupResult.NotFound(
                Guid.NewGuid(),
                articleId,
                "Storage article lookup timed out with no positive response."));
    }

    private sealed class RecordingArticleWorkRpcClient : IArticleWorkRpcClient
    {
        private readonly ArticleWorkOutcome _outcome;

        public RecordingArticleWorkRpcClient(ArticleWorkOutcome outcome = ArticleWorkOutcome.Success)
        {
            _outcome = outcome;
        }

        public List<ReadOnlyMemory<byte>> Lookups { get; } = [];

        public Task<ArticleWorkRpcResult> LookupByMessageIdAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken)
        {
            Lookups.Add(messageId.ToArray());
            return Task.FromResult(new ArticleWorkRpcResult(
                _outcome,
                Guid.NewGuid(),
                "<12345@example.invalid>",
                "Storage",
                "backfiller01.usenet.ninja",
                119,
                VectorNNTPD.Common.Articles.ArticleId.ParseLowerHex(
                    "dcab316ba0e91c6abbad8d5759bff207932dbe9168c88954c6dd9240b4a6da14"),
                Error: _outcome == ArticleWorkOutcome.Success ? null : "not found",
                "cache.requests"));
        }
    }

    private sealed class ArticleDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public PipeWriter ServerOutput => _serverToClient.Writer;

        public static Task<ArticleDuplex> CreateAsync() => Task.FromResult(new ArticleDuplex());

        public NntpSession CreateSession(
            NewsgroupSnapshot? snapshot = null,
            bool authorize = true,
            IArticleWorkRpcClient? articleWorkRpc = null,
            IStorageArticleLookupClient? storageLookup = null)
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)));
            var session = new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                newsgroupCatalogue: snapshot is null ? null : new StaticNewsgroupCatalogue(snapshot),
                articleWorkRpc: articleWorkRpc,
                storageArticleLookup: storageLookup);
            if (authorize)
            {
                session.SetAuthorization(Reader);
            }

            return session;
        }

        public async Task<string> ReadClientLineAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var line = await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, cts.Token);
            Assert.NotNull(line);
            return line!;
        }

        public async ValueTask DisposeAsync()
        {
            await _clientToServer.Writer.CompleteAsync();
            await _clientToServer.Reader.CompleteAsync();
            await _serverToClient.Writer.CompleteAsync();
            await _serverToClient.Reader.CompleteAsync();
        }
    }

    private sealed class PipeNntpConnection : INntpConnection
    {
        private readonly CancellationTokenSource _cts = new();

        public PipeNntpConnection(PipeReader input, PipeWriter output, ConnectionClientIdentity identity)
        {
            Input = input;
            Output = output;
            ClientIdentity = identity;
        }

        public PipeReader Input { get; }

        public PipeWriter Output { get; }

        public System.Net.EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;

        public System.Net.EndPoint? LocalEndPoint => null;

        public ConnectionClientIdentity ClientIdentity { get; }

        public bool IsTls => false;

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
        {
            tlsVersion = string.Empty;
            cipher = string.Empty;
            return false;
        }

        public bool IsCompressed => false;

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
            VectorNNTPD.Common.Networking.Certificates.ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static StorageArticleLookupService CreateLookup(
        RabbitMqService rabbit,
        IStorageServerRegistry registry,
        TimeProvider? time = null) =>
        new(
            rabbit,
            Options.Create(CreateLookupOptions()),
            NullLogger<StorageArticleLookupService>.Instance,
            time,
            registry);

    private static NntpdOptions CreateLookupOptions()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ServerId = 1;
        return options;
    }

    private static RabbitMqService CreateRabbitMq(FakeRabbitMqConnectionFactory factory)
    {
        var options = RabbitMqOptionsTests.CreateValid();
        options.PoolReconnectBaseDelayMs = 50;
        options.PoolReconnectMaxDelayMs = 50;
        return new RabbitMqService(
            factory,
            Options.Create(options),
            new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTPD.ReaderService:nntpd01.usenet.ninja"),
            NullLogger<RabbitMqService>.Instance);
    }

    private static async Task<FakeRabbitMqRpcPublication> WaitForFleetPublicationAsync(FakeRabbitMqConnectionFactory factory)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var connection = factory.LastConnection;
            if (connection is not null)
            {
                var publication = connection.RpcChannels
                    .SelectMany(static channel => channel.Publications)
                    .FirstOrDefault(static item => item.Exchange == CacheFleetTopology.RequestsExchangeName);
                if (publication is not null)
                {
                    return publication;
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Expected one cache.requests publication.");
    }
}

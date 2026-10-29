using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.History;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.ReaderService.Networking.Proxy;
using VectorNNTPD.ReaderService.Networking.Transport;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.PostFilter.Quota;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Authentication;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Session.Commands.Posting;
using VectorNNTPD.ReaderService.Tests.Fixtures;

namespace VectorNNTPD.ReaderService.Tests.PostFilter;

/// <summary>Shared POST-boundary harness for PostFilter policy-matrix tests.</summary>
internal static class PostFilterPostHarness
{
    public static readonly NntpAuthorization Poster = new(
        isAuthenticated: true,
        authorizedReader: true,
        authorizedTransit: false,
        postingPermitted: true,
        streamingPermitted: false);

    public static readonly TimeSpan Safety = TimeSpan.FromSeconds(20);

    public static IPostFilter CreateFilter(
        PostFilterPolicySnapshot snapshot,
        IPostFilterQuotaStore quota,
        IPostFilterSpamAssassin? sa = null,
        PostFilterReservationIdentity? identity = null) =>
        CreateFilter(
            new StaticPostFilterPolicySource(snapshot),
            quota,
            sa,
            identity);

    public static IPostFilter CreateFilter(
        IPostFilterPolicySource source,
        IPostFilterQuotaStore quota,
        IPostFilterSpamAssassin? sa = null,
        PostFilterReservationIdentity? identity = null) =>
        new PostFilterEvaluator(
            source,
            quota,
            identity ?? new PostFilterReservationIdentity("n1", "inc"),
            sa ?? new PolicyRecordingSpamAssassin(),
            NullLogger<PostFilterEvaluator>.Instance);

    public static PostFilterPolicySnapshot Compile(PostFilterOptions options, long revision = 1)
    {
        var repository = new InMemoryPostFilterPolicyRepository
        {
            Record = InMemoryPostFilterPolicyRepository.Create(options, revision),
        };
        var record = repository.LoadAsync().AsTask().GetAwaiter().GetResult();
        return PostFilterPolicyCompiler.Compile(record.Options, record.Revision);
    }

    public static PostFilterSpamAssassinOptions EnabledSpamAssassin(
        PostFilterSpamOnFailure onFailure = PostFilterSpamOnFailure.Reject,
        int? maxArticleSize = null,
        string[]? excludeArtTypes = null) =>
        new()
        {
            Enabled = true,
            OnFailure = onFailure,
            Hosts = ["127.0.0.1"],
            MaxArticleSize = maxArticleSize ?? 0,
            ExcludeArtTypes = excludeArtTypes ?? [],
        };

    public static ArticleIngestionQueue NewQueue() =>
        new(new ArticleIngestionOptions { QueueCapacity = 8, MaxArticleBytes = NntpdOptions.DefaultMaxArticleSize });

    public static string TextArticle(string body = "body\r\n", string? messageId = null) =>
        "Date: " + PostRfcDate.Format(DateTimeOffset.UtcNow) + "\r\n"
        + "From: poster@example.com\r\n"
        + "Newsgroups: misc.test\r\n"
        + "Subject: test\r\n"
        + "Message-ID: <" + (messageId ?? Guid.NewGuid().ToString("N")) + "@example.com>\r\n"
        + "\r\n"
        + body;

    public static async Task PostAsync(
        PostFilterPostDuplex duplex,
        NntpSession session,
        string expect,
        string username = "poster",
        string? article = null)
    {
        session.ApplySuccessfulAuthentication(username, Poster);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();
        await duplex.WriteClientLineAsync("POST");
        Assert.Equal("340 Input article; end with <CR-LF>.<CR-LF>", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync((article ?? TextArticle()) + ".\r\n");
        Assert.Equal(expect, await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }
}

/// <summary>In-memory NNTP duplex used by PostFilter POST-boundary tests.</summary>
internal sealed class PostFilterPostDuplex : IAsyncDisposable
{
    private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
    private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

    public NntpSession CreateSession(
        IArticleIngestionQueue queue,
        IPostFilter filter,
        PostFilterMetrics? metrics = null,
        IHistoryDb? historyDb = null,
        bool cancelConnectionAfterAccept = false,
        IPAddress? clientAddress = null,
        IPostFilterRejectionEvidenceQueue? postFilterEvidence = null)
    {
        var address = clientAddress ?? IPAddress.Loopback;
        var connection = new PolicyPipeConnection(
            _clientToServer.Reader,
            _serverToClient.Writer,
            ConnectionClientIdentity.Direct(new IPEndPoint(address, 119)));
        IPostFilter wired = filter;
        if (cancelConnectionAfterAccept)
        {
            wired = new PolicyCancelAfterAcceptFilter(filter, connection);
        }

        return new NntpSession(
            connection,
            NullLogger<NntpSession>.Instance,
            articleIngestion: queue,
            historyDb: historyDb,
            postingTraceProtector: AesGcmPostingTraceProtector.Create(
                new NntpdOptions { XTraceKey = TestHostFactory.TestXTraceKey }),
            postFilter: wired,
            postFilterMetrics: metrics,
            postFilterEvidence: postFilterEvidence);
    }

    public async Task WriteClientLineAsync(string line) =>
        await WriteClientAsync(line + "\r\n");

    public async Task WriteClientAsync(string payload)
    {
        await _clientToServer.Writer.WriteAsync(Encoding.ASCII.GetBytes(payload));
        await _clientToServer.Writer.FlushAsync();
    }

    public async Task<string> ReadClientLineAsync()
    {
        using var cts = new CancellationTokenSource(PostFilterPostHarness.Safety);
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

/// <summary>Records quota operations and optionally delegates to an inner store.</summary>
internal sealed class PolicyRecordingQuotaStore : IPostFilterQuotaStore
{
    private readonly IPostFilterQuotaStore _inner;

    public PolicyRecordingQuotaStore(IPostFilterQuotaStore? inner = null)
    {
        _inner = inner ?? new InMemoryPostFilterQuotaStore();
    }

    public bool Unavailable { get; set; }

    public bool CommitNoop { get; set; }

    public bool CommitUnavailable { get; set; }

    public CancellationToken LastReleaseToken { get; private set; }

    public List<string> Operations { get; } = [];

    public string? LastAccountName { get; private set; }

    public long LastReservedMessages { get; private set; }

    public long LastReservedBytes { get; private set; }

    public string? LastBodyHex { get; private set; }

    public ValueTask<PostFilterQuotaReserveStatus> ReserveAsync(
        string accountName,
        PostFilterReservationId reservation,
        DateTimeOffset now,
        PostFilterQuotaWindows windows,
        PostFilterQuotaCeilings ceilings,
        long messages,
        long bytes,
        int mpUnits,
        string? bodyHex,
        CancellationToken cancellationToken = default,
        long reservationTtlMs = 0)
    {
        Operations.Add("reserve");
        LastAccountName = accountName;
        LastReservedMessages = messages;
        LastReservedBytes = bytes;
        LastBodyHex = bodyHex;
        _ = reservationTtlMs;
        if (Unavailable)
        {
            return ValueTask.FromResult(PostFilterQuotaReserveStatus.Unavailable);
        }

        return _inner.ReserveAsync(
            accountName,
            reservation,
            now,
            windows,
            ceilings,
            messages,
            bytes,
            mpUnits,
            bodyHex,
            cancellationToken,
            reservationTtlMs);
    }

    public ValueTask<PostFilterQuotaCommitStatus> CommitAsync(
        string accountName,
        PostFilterReservationId reservation,
        DateTimeOffset now,
        PostFilterQuotaWindows windows,
        PostFilterQuotaCeilings ceilings,
        CancellationToken cancellationToken = default)
    {
        Operations.Add("commit");
        if (CommitNoop)
        {
            return ValueTask.FromResult(PostFilterQuotaCommitStatus.Noop);
        }

        if (CommitUnavailable)
        {
            return ValueTask.FromResult(PostFilterQuotaCommitStatus.Unavailable);
        }

        return _inner.CommitAsync(accountName, reservation, now, windows, ceilings, cancellationToken);
    }

    public ValueTask<PostFilterQuotaReleaseStatus> ReleaseAsync(
        string accountName,
        PostFilterReservationId reservation,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        Operations.Add("release");
        LastReleaseToken = cancellationToken;
        return _inner.ReleaseAsync(accountName, reservation, now, cancellationToken);
    }
}

/// <summary>Records SPAMD CHECK calls and builds the disposable scan representation.</summary>
internal sealed class PolicyRecordingSpamAssassin : IPostFilterSpamAssassin
{
    public int Calls { get; private set; }

    public PostFilterSpamAssassinResult Result { get; set; } = PostFilterSpamAssassinResult.Ham();

    public byte[]? LastArtData { get; private set; }

    public byte[]? LastScan { get; private set; }

    public ArticleId LastArtId { get; private set; }

    public ulong LastArtHash { get; private set; }

    public ArticleType LastArtType { get; private set; }

    public int LastArtSize { get; private set; }

    public ArticleFieldTable LastFields { get; private set; }

    public ValueTask<PostFilterSpamAssassinResult> CheckAsync(
        ArticleRecord article,
        string? accountName,
        PostFilterSpamAssassinTarget target,
        SpamdScanContext scanContext,
        CancellationToken cancellationToken = default)
    {
        _ = accountName;
        _ = target;
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        LastArtData = article.ArtData.ToArray();
        LastArtId = article.ArtId;
        LastArtHash = article.ArtHash;
        LastArtType = article.ArtType;
        LastArtSize = article.ArtSize;
        LastFields = article.Fields;
        LastScan = SpamdScanArticleBuilder.Build(article, scanContext);
        Assert.True(LastArtData.AsSpan().SequenceEqual(article.ArtData.Span));
        return ValueTask.FromResult(Result);
    }
}

/// <summary>Records History Peek / Remember for POST-boundary assertions.</summary>
internal sealed class PolicyRecordingHistoryDb : IHistoryDb
{
    public int RememberCalls { get; private set; }

    public HistoryLookupResult PeekResult { get; set; } = HistoryLookupResult.Unseen;

    public ValueTask<HistoryLookupResult> LookupAsync(
        ReadOnlyMemory<byte> messageId,
        CancellationToken cancellationToken = default) =>
        new(HistoryLookupResult.Unseen);

    public ValueTask<HistoryLookupResult> PeekAsync(
        ReadOnlyMemory<byte> messageId,
        CancellationToken cancellationToken = default) =>
        new(PeekResult);

    public void Remember(ReadOnlyMemory<byte> messageId)
    {
        _ = messageId;
        RememberCalls++;
    }

    public bool ContainsLocal(in HistoryDigest digest) => false;
}

internal sealed class PolicyCancelAfterAcceptFilter : IPostFilter
{
    private readonly IPostFilter _inner;
    private readonly INntpConnection _connection;

    public PolicyCancelAfterAcceptFilter(IPostFilter inner, INntpConnection connection)
    {
        _inner = inner;
        _connection = connection;
    }

    public async ValueTask<PostFilterResult> EvaluateAsync(
        PostFilterRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Decision == PostFilterDecision.Accept && result.Lease is not null)
        {
            await _connection.CompleteAsync().ConfigureAwait(false);
        }

        return result;
    }
}

internal sealed class PolicyPipeConnection : INntpConnection
{
    private readonly CancellationTokenSource _cts = new();

    public PolicyPipeConnection(PipeReader input, PipeWriter output, ConnectionClientIdentity identity)
    {
        Input = input;
        Output = output;
        ClientIdentity = identity;
    }

    public PipeReader Input { get; }

    public PipeWriter Output { get; }

    public EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;

    public EndPoint? LocalEndPoint => null;

    public ConnectionClientIdentity ClientIdentity { get; }

    public bool IsTls => false;

    public bool IsCompressed => false;

    public CancellationToken ConnectionClosed => _cts.Token;

    public bool IsCompleted => _cts.IsCancellationRequested;

    public long OutboundIdleVersion => 0;

    public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
    {
        tlsVersion = string.Empty;
        cipher = string.Empty;
        return false;
    }

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

    public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask DisposeAsync()
    {
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}

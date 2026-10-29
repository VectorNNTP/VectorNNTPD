using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Transport.ArticleTransfer;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.ReaderService.Storage;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.ReaderService.Tests.Transport.Vatp;
using VectorNNTPD.ReaderService.Transport.Vatp;

namespace VectorNNTPD.ReaderService.Tests.Storage;

public sealed class ArticlePlacementConnectionTests
{
    [Fact]
    public async Task Several_articles_reuse_one_connection()
    {
        await using var server = StoreServer.Start();
        await using var client = Client(maxOutstanding: 2);
        var target = Entry(server.Port);
        for (var i = 0; i < 3; i++)
        {
            var article = CanonicalArticleText.CreateQueued($"<reuse-{i}@example.test>", InboundArticleProducer.TakeThis);
            var result = await client.PlaceAsync(article.Record, target, CancellationToken.None);
            Assert.Equal(ArticlePlacementKind.Accepted, result.Kind);
        }

        Assert.Equal(1, client.ConnectCount);
        Assert.Equal(1, server.AcceptedConnections);
        Assert.Equal(3, server.Results);
    }

    [Fact]
    public async Task Two_targets_do_not_share_a_connection()
    {
        await using var left = StoreServer.Start();
        await using var right = StoreServer.Start();
        await using var client = Client(maxOutstanding: 1);
        var a = CanonicalArticleText.CreateQueued("<left@example.test>", InboundArticleProducer.TakeThis);
        var b = CanonicalArticleText.CreateQueued("<right@example.test>", InboundArticleProducer.TakeThis);
        Assert.Equal(ArticlePlacementKind.Accepted, (await client.PlaceAsync(a.Record, Entry(left.Port, "cache01.usenet.ninja"), CancellationToken.None)).Kind);
        Assert.Equal(ArticlePlacementKind.Accepted, (await client.PlaceAsync(b.Record, Entry(right.Port, "cache02.usenet.ninja"), CancellationToken.None)).Kind);
        Assert.Equal(2, client.ConnectCount);
        Assert.Equal(1, left.AcceptedConnections);
        Assert.Equal(1, right.AcceptedConnections);
    }

    [Fact]
    public async Task Fqdn_case_uses_one_connection()
    {
        await using var server = StoreServer.Start();
        await using var client = Client(maxOutstanding: 1);
        var lower = CanonicalArticleText.CreateQueued("<case-a@example.test>", InboundArticleProducer.TakeThis);
        var upper = CanonicalArticleText.CreateQueued("<case-b@example.test>", InboundArticleProducer.TakeThis);
        Assert.Equal(ArticlePlacementKind.Accepted, (await client.PlaceAsync(lower.Record, Entry(server.Port, "cache01.usenet.ninja"), CancellationToken.None)).Kind);
        Assert.Equal(ArticlePlacementKind.Accepted, (await client.PlaceAsync(upper.Record, Entry(server.Port, "Cache01.usenet.ninja"), CancellationToken.None)).Kind);
        Assert.Equal(1, client.ConnectCount);
        Assert.Equal(1, server.AcceptedConnections);
    }

    [Fact]
    public async Task Limit_one_does_not_overlap_stores()
    {
        await using var server = StoreServer.Start(holdResult: true);
        await using var client = Client(maxOutstanding: 1);
        var target = Entry(server.Port);
        var first = CanonicalArticleText.CreateQueued("<seq-a@example.test>", InboundArticleProducer.TakeThis);
        var second = CanonicalArticleText.CreateQueued("<seq-b@example.test>", InboundArticleProducer.TakeThis);
        var firstTask = client.PlaceAsync(first.Record, target, CancellationToken.None).AsTask();
        await server.WaitForStoresAsync(1);
        var secondTask = client.PlaceAsync(second.Record, target, CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.Equal(1, server.PeakOutstanding);
        server.ReleaseResults();
        Assert.Equal(ArticlePlacementKind.Accepted, (await firstTask).Kind);
        Assert.Equal(ArticlePlacementKind.Accepted, (await secondTask).Kind);
        Assert.Equal(1, server.PeakOutstanding);
        Assert.Equal(1, client.ConnectCount);
    }

    [Fact]
    public async Task Two_stores_complete_with_the_result_for_their_stream()
    {
        await using var server = StoreServer.Start(holdResult: true, duplicateSecond: true);
        await using var client = Client(maxOutstanding: 2);
        var target = Entry(server.Port);
        var first = CanonicalArticleText.CreateQueued("<mux-a@example.test>", InboundArticleProducer.TakeThis);
        var second = CanonicalArticleText.CreateQueued("<mux-b@example.test>", InboundArticleProducer.TakeThis);
        var firstTask = client.PlaceAsync(first.Record, target, CancellationToken.None).AsTask();
        var secondTask = client.PlaceAsync(second.Record, target, CancellationToken.None).AsTask();
        await server.WaitForStoresAsync(2);
        Assert.Equal(2, server.PeakOutstanding);
        server.ReleaseResults();
        var results = await Task.WhenAll(firstTask, secondTask);
        Assert.Contains(results, static result => result.Kind == ArticlePlacementKind.Accepted);
        Assert.Contains(results, static result => result.Kind == ArticlePlacementKind.Duplicate);
        Assert.Equal(1, client.ConnectCount);
        Assert.Equal(0, server.ParseFailures);
    }

    [Fact]
    public async Task Dead_connection_is_replaced_once()
    {
        await using var server = StoreServer.Start(closeAfterAccepted: 1);
        await using var client = Client(maxOutstanding: 1);
        var target = Entry(server.Port);
        var first = CanonicalArticleText.CreateQueued("<replace-a@example.test>", InboundArticleProducer.TakeThis);
        Assert.Equal(ArticlePlacementKind.Accepted, (await client.PlaceAsync(first.Record, target, CancellationToken.None)).Kind);
        var replaced = ArticlePlacementKind.TransportFailure;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && replaced != ArticlePlacementKind.Accepted)
        {
            var article = CanonicalArticleText.CreateQueued($"<replace-{Guid.NewGuid():N}@example.test>", InboundArticleProducer.TakeThis);
            replaced = (await client.PlaceAsync(article.Record, target, CancellationToken.None)).Kind;
        }

        Assert.Equal(ArticlePlacementKind.Accepted, replaced);
        Assert.Equal(2, client.ConnectCount);
        Assert.Equal(2, server.AcceptedConnections);
    }

    [Fact]
    public async Task Closed_port_does_not_dial_again_during_backoff()
    {
        await using var client = new ArticlePlacementClient(
            NullLogger<ArticlePlacementClient>.Instance,
            new VatpClientOptions(),
            ArticleTransferLimits.Default,
            maxOutstandingStores: 1)
        {
            TestTcpConnectHost = "127.0.0.1",
        };
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var article = CanonicalArticleText.CreateQueued("<closed@example.test>", InboundArticleProducer.TakeThis);
        var target = Entry(port);
        var first = await client.PlaceAsync(article.Record, target, CancellationToken.None);
        Assert.Equal(ArticlePlacementKind.TransportFailure, first.Kind);
        Assert.StartsWith("SocketException:", first.Failure, StringComparison.Ordinal);
        var second = await client.PlaceAsync(article.Record, target, CancellationToken.None);
        Assert.Equal("backoff", second.Failure);
        Assert.Equal(1, client.DialAttempts);
    }

    [Fact]
    public async Task Drop_after_end_is_not_success()
    {
        await using var server = StoreServer.Start(dropAfterEnd: true);
        await using var client = Client(maxOutstanding: 1);
        var article = CanonicalArticleText.CreateQueued("<ambiguous@example.test>", InboundArticleProducer.TakeThis);
        var result = await client.PlaceAsync(article.Record, Entry(server.Port), CancellationToken.None);
        Assert.Equal(ArticlePlacementKind.AcknowledgementNotObserved, result.Kind);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Remote_rejection_is_not_success()
    {
        await using var server = StoreServer.Start(reject: true);
        await using var client = Client(maxOutstanding: 1);
        var article = CanonicalArticleText.CreateQueued("<reject@example.test>", InboundArticleProducer.TakeThis);
        var result = await client.PlaceAsync(article.Record, Entry(server.Port), CancellationToken.None);
        Assert.Equal(ArticlePlacementKind.RejectedInvalid, result.Kind);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Cancel_while_waiting_for_a_slot_sends_nothing()
    {
        await using var server = StoreServer.Start(holdResult: true);
        await using var client = Client(maxOutstanding: 1);
        var target = Entry(server.Port);
        var held = CanonicalArticleText.CreateQueued("<held@example.test>", InboundArticleProducer.TakeThis);
        var heldTask = client.PlaceAsync(held.Record, target, CancellationToken.None).AsTask();
        await server.WaitForStoresAsync(1);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var queued = CanonicalArticleText.CreateQueued("<queued@example.test>", InboundArticleProducer.TakeThis);
        var result = await client.PlaceAsync(queued.Record, target, cts.Token);
        Assert.Equal(ArticlePlacementKind.Cancelled, result.Kind);
        Assert.Equal(1, server.Stores);
        server.ReleaseResults();
        Assert.Equal(ArticlePlacementKind.Accepted, (await heldTask).Kind);
    }

    [Fact]
    public async Task Shutdown_cancels_an_outstanding_store_and_rejects_new_calls()
    {
        await using var server = StoreServer.Start(holdResult: true);
        await using var client = Client(maxOutstanding: 1);
        var article = CanonicalArticleText.CreateQueued("<stop@example.test>", InboundArticleProducer.TakeThis);
        var pending = client.PlaceAsync(article.Record, Entry(server.Port), CancellationToken.None).AsTask();
        await server.WaitForStoresAsync(1);
        await client.DisposeAsync();
        var result = await pending;
        Assert.NotEqual(ArticlePlacementKind.Accepted, result.Kind);
        var later = await client.PlaceAsync(article.Record, Entry(server.Port), CancellationToken.None);
        Assert.Equal(ArticlePlacementKind.TransportFailure, later.Kind);
        Assert.Equal("shutdown", later.Failure);
    }

    [Fact]
    public async Task Stream_ids_are_not_reused_on_a_live_connection()
    {
        await using var server = StoreServer.Start();
        await using var client = Client(maxOutstanding: 2);
        var target = Entry(server.Port);
        for (var i = 0; i < 3; i++)
        {
            var article = CanonicalArticleText.CreateQueued($"<id-{i}@example.test>", InboundArticleProducer.TakeThis);
            Assert.Equal(ArticlePlacementKind.Accepted, (await client.PlaceAsync(article.Record, target, CancellationToken.None)).Kind);
        }

        var ids = server.StreamIds;
        Assert.Equal(new uint[] { 1, 2, 3 }, ids);
        Assert.Equal(1, client.ConnectCount);
    }

    [Fact]
    public async Task Cancel_during_dial_does_not_open_a_second_connection()
    {
        await using var server = StoreServer.Start(handshakeDelay: TimeSpan.FromMilliseconds(400));
        await using var client = Client(maxOutstanding: 2);
        var target = Entry(server.Port);
        using var cts = new CancellationTokenSource();
        var firstArticle = CanonicalArticleText.CreateQueued("<dial-a@example.test>", InboundArticleProducer.TakeThis);
        var secondArticle = CanonicalArticleText.CreateQueued("<dial-b@example.test>", InboundArticleProducer.TakeThis);
        var first = client.PlaceAsync(firstArticle.Record, target, cts.Token).AsTask();
        var started = DateTime.UtcNow;
        while (client.DialAttempts < 1)
        {
            if (DateTime.UtcNow - started > TimeSpan.FromSeconds(2))
            {
                throw new TimeoutException("Dial did not start.");
            }

            await Task.Delay(5);
        }

        var second = client.PlaceAsync(secondArticle.Record, target, CancellationToken.None).AsTask();
        await cts.CancelAsync();
        var firstResult = await first;
        var secondResult = await second;
        Assert.Equal(ArticlePlacementKind.Cancelled, firstResult.Kind);
        Assert.Equal(ArticlePlacementKind.Accepted, secondResult.Kind);
        Assert.Equal(1, client.ConnectCount);
        Assert.Equal(1, server.AcceptedConnections);
    }

    [Fact]
    public async Task Two_streams_keep_independent_window_credit()
    {
        var limits = new ArticleTransferLimits { InitialStreamWindowBytes = 32 };
        await using var server = StoreServer.Start(initialCredit: 32);
        await using var client = Client(maxOutstanding: 2, limits: limits);
        var target = Entry(server.Port);
        var first = CanonicalArticleText.CreateQueued("<win-a@example.test>", InboundArticleProducer.TakeThis, body: new string('a', 200) + "\r\n");
        var second = CanonicalArticleText.CreateQueued("<win-b@example.test>", InboundArticleProducer.TakeThis, body: new string('b', 200) + "\r\n");
        var results = await Task.WhenAll(
            client.PlaceAsync(first.Record, target, CancellationToken.None).AsTask(),
            client.PlaceAsync(second.Record, target, CancellationToken.None).AsTask());
        Assert.All(results, static result => Assert.Equal(ArticlePlacementKind.Accepted, result.Kind));
        Assert.Equal(0, server.ParseFailures);
        Assert.Equal(2, server.PeakOutstanding);
        Assert.Equal(1, client.ConnectCount);
    }

    [Fact]
    public async Task Connection_loss_after_end_is_ambiguous_for_every_outstanding_store()
    {
        await using var server = StoreServer.Start(holdResult: true);
        await using var client = Client(maxOutstanding: 2);
        var target = Entry(server.Port);
        var first = CanonicalArticleText.CreateQueued("<loss-a@example.test>", InboundArticleProducer.TakeThis);
        var second = CanonicalArticleText.CreateQueued("<loss-b@example.test>", InboundArticleProducer.TakeThis);
        var firstTask = client.PlaceAsync(first.Record, target, CancellationToken.None).AsTask();
        var secondTask = client.PlaceAsync(second.Record, target, CancellationToken.None).AsTask();
        await server.WaitForEndsAsync(2);
        server.DropConnections();
        var results = await Task.WhenAll(firstTask, secondTask);
        Assert.All(results, static result => Assert.Equal(ArticlePlacementKind.AcknowledgementNotObserved, result.Kind));
        Assert.All(results, static result => Assert.False(result.Succeeded));
    }

    [Fact]
    public async Task Cancel_while_blocked_on_credit_does_not_report_success()
    {
        var limits = new ArticleTransferLimits { InitialStreamWindowBytes = 16 };
        await using var server = StoreServer.Start(grantWindows: false, initialCredit: 16);
        await using var client = Client(maxOutstanding: 1, limits: limits, ioTimeout: TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();
        var article = CanonicalArticleText.CreateQueued("<credit@example.test>", InboundArticleProducer.TakeThis, body: new string('c', 400) + "\r\n");
        var pending = client.PlaceAsync(article.Record, Entry(server.Port), cts.Token).AsTask();
        await server.WaitForDataAsync(1);
        await cts.CancelAsync();
        var result = await pending;
        Assert.Equal(ArticlePlacementKind.Cancelled, result.Kind);
        Assert.Equal(0, server.Ends);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Client_does_not_send_the_body_before_window_credit()
    {
        await using var server = StoreServer.Start(initialCredit: 0);
        await using var client = Client(maxOutstanding: 1, ioTimeout: TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();
        var article = CanonicalArticleText.CreateQueued(
            "<no-body-yet@example.test>",
            InboundArticleProducer.TakeThis,
            body: new string('d', 400) + "\r\n");
        var pending = client.PlaceAsync(article.Record, Entry(server.Port), cts.Token).AsTask();
        await server.WaitForStoresAsync(1);
        Assert.Equal(0, server.DataFrames);
        await cts.CancelAsync();
        var result = await pending;
        Assert.Equal(ArticlePlacementKind.Cancelled, result.Kind);
        Assert.Equal(0, server.DataFrames);
    }

    [Fact]
    public async Task Timeout_after_end_is_ambiguous()
    {
        await using var server = StoreServer.Start(holdResult: true);
        await using var client = Client(maxOutstanding: 1, ioTimeout: TimeSpan.FromMilliseconds(300));
        var article = CanonicalArticleText.CreateQueued("<late@example.test>", InboundArticleProducer.TakeThis);
        var result = await client.PlaceAsync(article.Record, Entry(server.Port), CancellationToken.None);
        Assert.Equal(ArticlePlacementKind.AcknowledgementNotObserved, result.Kind);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Timeout_before_handshake_is_not_success()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        await using var client = new ArticlePlacementClient(
            NullLogger<ArticlePlacementClient>.Instance,
            new VatpClientOptions
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(200),
                TlsHandshakeTimeout = TimeSpan.FromMilliseconds(200),
                IoTimeout = TimeSpan.FromMilliseconds(200),
            },
            ArticleTransferLimits.Default,
            maxOutstandingStores: 1)
        {
            TestTcpConnectHost = "127.0.0.1",
            ServerCertificateValidationCallback = static (_, _, _, _) => true,
        };
        var article = CanonicalArticleText.CreateQueued("<hang@example.test>", InboundArticleProducer.TakeThis);
        var place = client.PlaceAsync(article.Record, Entry(port), CancellationToken.None).AsTask();
        using var accepted = await accept;
        var result = await place;
        listener.Stop();
        Assert.Equal(ArticlePlacementKind.TransportFailure, result.Kind);
        Assert.Equal("timeout", result.Failure);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Production_placement_stays_disabled()
    {
        var property = typeof(VectorNNTPD.ReaderService.ArticleIngestion.IncomingSpoolWriterService)
            .GetProperty("ArticlePlacementEnabled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(property);
        Assert.False((bool)property.GetValue(null)!);
    }

    [Fact]
    public void Outstanding_limit_rejects_values_other_than_one_or_two()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ArticlePlacementClient(NullLogger<ArticlePlacementClient>.Instance, 64));
    }

    private static ArticlePlacementClient Client(
        int maxOutstanding,
        ArticleTransferLimits? limits = null,
        TimeSpan? ioTimeout = null) =>
        new(
            NullLogger<ArticlePlacementClient>.Instance,
            new VatpClientOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                TlsHandshakeTimeout = TimeSpan.FromSeconds(2),
                IoTimeout = ioTimeout ?? TimeSpan.FromSeconds(2),
            },
            limits ?? ArticleTransferLimits.Default,
            maxOutstanding)
        {
            TestTcpConnectHost = "127.0.0.1",
            ServerCertificateValidationCallback = static (_, _, _, _) => true,
        };

    private static StorageServerFleetEntry Entry(int port, string fqdn = "cache01.usenet.ninja") =>
        new(1, fqdn, 10_000, 0, 10_000, DateTimeOffset.UtcNow, port);

    private sealed class StoreServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accept;
        private readonly ConcurrentDictionary<SslStream, byte> _streams = new();
        private readonly ConcurrentQueue<uint> _streamIds = new();
        private int _outstanding;
        private int _peak;
        private int _stores;
        private int _ends;
        private int _dataFrames;
        private int _results;
        private int _acceptedConnections;
        private int _parseFailures;

        private StoreServer(
            TcpListener listener,
            bool holdResult,
            bool duplicateSecond,
            bool dropAfterEnd,
            bool reject,
            int closeAfterAccepted,
            bool grantWindows,
            int initialCredit,
            TimeSpan handshakeDelay)
        {
            _listener = listener;
            HoldResult = holdResult;
            DuplicateSecond = duplicateSecond;
            DropAfterEnd = dropAfterEnd;
            Reject = reject;
            CloseAfterAccepted = closeAfterAccepted;
            GrantWindows = grantWindows;
            InitialCredit = initialCredit;
            HandshakeDelay = handshakeDelay;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _accept = Task.Run(AcceptLoop);
        }

        public int Port { get; }

        public bool HoldResult { get; }

        public bool DuplicateSecond { get; }

        public bool DropAfterEnd { get; }

        public bool Reject { get; }

        public int CloseAfterAccepted { get; }

        public bool GrantWindows { get; }

        public int InitialCredit { get; }

        public TimeSpan HandshakeDelay { get; }

        public int PeakOutstanding => Volatile.Read(ref _peak);

        public int Stores => Volatile.Read(ref _stores);

        public int Ends => Volatile.Read(ref _ends);

        public int DataFrames => Volatile.Read(ref _dataFrames);

        public uint[] StreamIds => _streamIds.ToArray();

        public int Results => Volatile.Read(ref _results);

        public int AcceptedConnections => Volatile.Read(ref _acceptedConnections);

        public int ParseFailures => Volatile.Read(ref _parseFailures);

        private TaskCompletionSource ResultsReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static StoreServer Start(
            bool holdResult = false,
            bool duplicateSecond = false,
            bool dropAfterEnd = false,
            bool reject = false,
            int closeAfterAccepted = 0,
            bool grantWindows = true,
            int initialCredit = 1024 * 1024,
            TimeSpan handshakeDelay = default)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new StoreServer(
                listener,
                holdResult,
                duplicateSecond,
                dropAfterEnd,
                reject,
                closeAfterAccepted,
                grantWindows,
                initialCredit,
                handshakeDelay);
        }

        public void DropConnections()
        {
            foreach (var stream in _streams.Keys)
            {
                stream.Close();
            }
        }

        public Task WaitForStoresAsync(int count) => WaitForAsync(() => Stores, count, "STORE frames did not arrive.");

        public Task WaitForEndsAsync(int count) => WaitForAsync(() => Ends, count, "END frames did not arrive.");

        public Task WaitForDataAsync(int count) => WaitForAsync(() => DataFrames, count, "DATA frames did not arrive.");

        private static async Task WaitForAsync(Func<int> read, int count, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (read() < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException(message);
                }

                await Task.Delay(10);
            }
        }

        public void ReleaseResults() => ResultsReleased.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _accept.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or SocketException or ObjectDisposedException)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
            }
        }

        private async Task AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                Interlocked.Increment(ref _acceptedConnections);
                _ = HandleAsync(tcp);
            }
        }

        private async Task HandleAsync(TcpClient tcp)
        {
            var certificate = VatpTestCertificates.CreateServerCertificate();
            using var tcpDisposable = tcp;
            // codeql[cs/local-not-disposed] SslStream ownership transfers to the VATP/TLS session fixture for connection lifetime.
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            _streams.TryAdd(ssl, 0);
            try
            {
                if (HandshakeDelay > TimeSpan.Zero)
                {
                    await Task.Delay(HandshakeDelay, _stop.Token);
                }

                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                });
                var buffer = new byte[64 * 1024];
                var held = new List<byte>();
                var credit = new Dictionary<uint, long>();
                var order = new List<uint>();
                var pendingResults = new List<uint>();
                Task<int>? readTask = null;
                while (!_stop.IsCancellationRequested)
                {
                    if (HoldResult && ResultsReleased.Task.IsCompleted)
                    {
                        await WritePendingResultsAsync(ssl, pendingResults, order);
                    }

                    var parsed = VatpFrameParser.ParseOneFrame(held.ToArray(), VatpProtocol.DefaultMaxFramePayload);
                    if (parsed.Status == VatpFrameParseStatus.Invalid)
                    {
                        Interlocked.Increment(ref _parseFailures);
                        return;
                    }

                    if (parsed.Status != VatpFrameParseStatus.Success || parsed.Frame is not { } frame)
                    {
                        readTask ??= ssl.ReadAsync(buffer, _stop.Token).AsTask();
                        if (HoldResult && !ResultsReleased.Task.IsCompleted)
                        {
                            await Task.WhenAny(readTask, ResultsReleased.Task);
                            if (!readTask.IsCompleted)
                            {
                                continue;
                            }
                        }

                        var read = await readTask;
                        readTask = null;
                        if (read == 0)
                        {
                            return;
                        }

                        for (var i = 0; i < read; i++)
                        {
                            held.Add(buffer[i]);
                        }

                        continue;
                    }

                    held.RemoveRange(0, checked((int)parsed.ConsumedBytes));
                    if (frame.Header.Type == VatpFrameType.Hello)
                    {
                        await WriteAsync(ssl, VatpFrameEncoder.EncodeHello(VatpProtocol.DefaultMaxFramePayload));
                        continue;
                    }

                    if (frame.Header.Type == VatpFrameType.Store)
                    {
                        var now = Interlocked.Increment(ref _outstanding);
                        int previous;
                        do
                        {
                            previous = Volatile.Read(ref _peak);
                            if (now <= previous)
                            {
                                break;
                            }
                        }
                        while (Interlocked.CompareExchange(ref _peak, now, previous) != previous);

                        Interlocked.Increment(ref _stores);
                        credit[frame.Header.StreamId] = InitialCredit;
                        _streamIds.Enqueue(frame.Header.StreamId);
                        order.Add(frame.Header.StreamId);
                        if (InitialCredit > 0)
                        {
                            await WriteAsync(ssl, VatpFrameEncoder.EncodeWindow(frame.Header.StreamId, (uint)InitialCredit));
                        }

                        continue;
                    }

                    if (frame.Header.Type == VatpFrameType.Data)
                    {
                        var length = frame.Payload.Length;
                        if (!credit.TryGetValue(frame.Header.StreamId, out var available) || length > available)
                        {
                            Interlocked.Increment(ref _parseFailures);
                            return;
                        }

                        credit[frame.Header.StreamId] = available - length;
                        Interlocked.Increment(ref _dataFrames);
                        if (length > 0 && GrantWindows)
                        {
                            await WriteAsync(ssl, VatpFrameEncoder.EncodeWindow(frame.Header.StreamId, (uint)length));
                            credit[frame.Header.StreamId] += length;
                        }

                        continue;
                    }

                    if (frame.Header.Type != VatpFrameType.End)
                    {
                        continue;
                    }

                    Interlocked.Increment(ref _ends);
                    if (DropAfterEnd)
                    {
                        return;
                    }

                    if (HoldResult && !ResultsReleased.Task.IsCompleted)
                    {
                        pendingResults.Add(frame.Header.StreamId);
                        continue;
                    }

                    if (await WriteResultAsync(ssl, frame.Header.StreamId, order))
                    {
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                _ = ex.GetType(); // observed: expected during test teardown/cleanup
            }
            finally
            {
                _streams.TryRemove(ssl, out _);
                await ssl.DisposeAsync();
            }
        }

        private async Task WritePendingResultsAsync(SslStream ssl, List<uint> pendingResults, List<uint> order)
        {
            while (pendingResults.Count > 0)
            {
                var streamId = pendingResults[0];
                pendingResults.RemoveAt(0);
                if (await WriteResultAsync(ssl, streamId, order))
                {
                    return;
                }
            }
        }

        private async Task<bool> WriteResultAsync(SslStream ssl, uint streamId, List<uint> order)
        {
            var index = order.IndexOf(streamId);
            var outcome = Reject
                ? (byte)ArticlePlacementKind.RejectedInvalid
                : DuplicateSecond && index == 1
                    ? (byte)ArticlePlacementKind.Duplicate
                    : (byte)ArticlePlacementKind.Accepted;
            await WriteAsync(ssl, VatpFrameEncoder.EncodeResult(streamId, outcome));
            Interlocked.Decrement(ref _outstanding);
            var sent = Interlocked.Increment(ref _results);
            return CloseAfterAccepted > 0 && sent >= CloseAfterAccepted;
        }

        private static async Task WriteAsync(Stream stream, VatpFrameEncoder.EncodedFrame frame)
        {
            if (!frame.Header.IsEmpty)
            {
                await stream.WriteAsync(frame.Header);
            }

            if (!frame.Payload.IsEmpty)
            {
                await stream.WriteAsync(frame.Payload);
            }
        }
    }
}

using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.History;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.TransitService.Redis;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.Commands;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Session.Framing;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.TestDoubles;
using VectorNNTPD.TransitService.Tests.Transit;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.TransitService.Tests.Session;

public sealed class CheckPipelineTests
{
    private static readonly IPAddress TransitPeer = IPAddress.Parse("198.18.0.70");
    private static readonly byte[] IdA = "<a@example.com>"u8.ToArray();
    private static readonly byte[] IdB = "<b@example.com>"u8.ToArray();
    private static readonly byte[] IdC = "<c@example.com>"u8.ToArray();

    [Fact]
    public async Task PipelinedChecks_AreInFlight_BeforeRedisCompletes()
    {
        var redis = new FakeRedisService();
        redis.Database.BlockExists = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        redis.Database.NotifyExistsStartedAt = 3;
        redis.Database.ExistsReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = CreateHistory(redis);
        await using var duplex = new CheckPipelineDuplex();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync(
            "CHECK <a@example.com>\r\nCHECK <b@example.com>\r\nCHECK <c@example.com>\r\n");
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await redis.Database.ExistsReached.Task.WaitAsync(safety.Token);
        Assert.Equal(3, redis.Database.ExistsStartedCount);
        Assert.Equal(0, redis.Database.KeyExistsCount);
        Assert.True(session.Pipeline!.Occupied >= 1);

        redis.Database.BlockExists.TrySetResult();
        Assert.Equal("238 <a@example.com> send article to be transferred", await duplex.ReadClientLineAsync());
        Assert.Equal("238 <b@example.com> send article to be transferred", await duplex.ReadClientLineAsync());
        Assert.Equal("238 <c@example.com> send article to be transferred", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task CompletionOrderBac_EmitsAbc()
    {
        var redis = new FakeRedisService();
        var blockA = BlockKey(redis, IdA);
        var blockC = BlockKey(redis, IdC);
        var history = CreateHistory(redis);
        _ = await history.LookupAsync(IdB);
        redis.Database.KeyExistsCount = 0;
        // LookupAsync(<b>) already counted one Redis start. Threshold 3 is that warmup plus CHECK <a> and CHECK <c>.
        redis.Database.NotifyExistsStartedAt = 3;
        redis.Database.ExistsReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var duplex = new CheckPipelineDuplex();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync(
            "CHECK <a@example.com>\r\nCHECK <b@example.com>\r\nCHECK <c@example.com>\r\n");
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await redis.Database.ExistsReached.Task.WaitAsync(safety.Token);
        Assert.True(session.Pipeline!.Occupied >= 2);

        blockA.TrySetResult();
        Assert.Equal("238 <a@example.com> send article to be transferred", await duplex.ReadClientLineAsync());
        Assert.Equal("431 <b@example.com>", await duplex.ReadClientLineAsync());
        blockC.TrySetResult();
        Assert.Equal("238 <c@example.com> send article to be transferred", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task LocalHitBehindDelayedRedis_DoesNotEmitEarly()
    {
        var redis = new FakeRedisService();
        var blockA = BlockKey(redis, IdA);
        var history = CreateHistory(redis);
        _ = await history.LookupAsync(IdB);
        redis.Database.KeyExistsCount = 0;

        await using var duplex = new CheckPipelineDuplex();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync("CHECK <a@example.com>\r\nCHECK <b@example.com>\r\n");
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (session.Pipeline is null || session.Pipeline.Occupied < 2)
        {
            safety.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        var early = duplex.TryReadClientLine();
        Assert.Null(early);

        blockA.TrySetResult();
        Assert.Equal("238 <a@example.com> send article to be transferred", await duplex.ReadClientLineAsync());
        Assert.Equal("431 <b@example.com>", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task PartialCommandSegmentation_CompletesAfterSecondWrite()
    {
        var redis = new FakeRedisService();
        var history = CreateHistory(redis);
        await using var duplex = new CheckPipelineDuplex();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync("CHECK <seg@");
        await Task.Yield();
        Assert.Null(duplex.TryReadClientLine());
        Assert.True(session.Pipeline is null || session.Pipeline.Occupied == 0);

        await duplex.WriteClientAsync("example.com>\r\n");
        Assert.Equal("238 <seg@example.com> send article to be transferred", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task StreamScratchReuse_DoesNotCorruptEarlierMessageId()
    {
        var redis = new FakeRedisService();
        var blockA = BlockKey(redis, IdA);
        var history = CreateHistory(redis);
        await using var duplex = new CheckPipelineDuplex();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <a@example.com>");
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (session.Pipeline is null || session.Pipeline.Occupied < 1)
        {
            safety.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        await duplex.WriteClientLineAsync("CHECK <b@example.com>");
        while (session.Pipeline.Occupied < 2)
        {
            safety.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        blockA.TrySetResult();
        Assert.Equal("238 <a@example.com> send article to be transferred", await duplex.ReadClientLineAsync());
        Assert.Equal("238 <b@example.com> send article to be transferred", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task ReaderScratchReuse_DoesNotCorruptEarlierMessageId()
    {
        var redis = new FakeRedisService();
        var blockA = BlockKey(redis, IdA);
        var history = CreateHistory(redis);
        await using var duplex = new CheckPipelineDuplex();
        var session = duplex.CreateSession(history);
        session.SetMode(NntpSessionMode.Reader);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <a@example.com>");
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (session.Pipeline is null || session.Pipeline.Occupied < 1)
        {
            safety.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        await duplex.WriteClientLineAsync("CHECK <b@example.com>");
        while (session.Pipeline.Occupied < 2)
        {
            safety.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        blockA.TrySetResult();
        Assert.Equal("238 <a@example.com> send article to be transferred", await duplex.ReadClientLineAsync());
        Assert.Equal("238 <b@example.com> send article to be transferred", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task CheckThenTakeThis_DrainsChecksFirst()
    {
        var redis = new FakeRedisService();
        var blockA = BlockKey(redis, IdA);
        var history = CreateHistory(redis);
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = new CheckPipelineDuplex();
        var session = duplex.CreateSession(history, queue);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync("CHECK <a@example.com>\r\nCHECK <b@example.com>\r\n");
        await duplex.WriteClientAsync(
            "TAKETHIS <c@example.com>\r\n" + CanonicalArticleText.Destuffed("<c@example.com>") + ".\r\n");
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (redis.Database.ExistsStartedCount < 2)
        {
            safety.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        Assert.Null(duplex.TryReadClientLine());
        blockA.TrySetResult();
        Assert.Equal("238 <a@example.com> send article to be transferred", await duplex.ReadClientLineAsync());
        Assert.Equal("238 <b@example.com> send article to be transferred", await duplex.ReadClientLineAsync());
        Assert.Equal("239 <c@example.com>", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task WindowFull_StopsRxUntilCapacityFrees()
    {
        var redis = new FakeRedisService();
        redis.Database.BlockExists = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        redis.Database.NotifyExistsStartedAt = 16;
        redis.Database.ExistsReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = CreateHistory(redis);
        await using var duplex = new CheckPipelineDuplex();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var payload = new StringBuilder();
        for (var i = 1; i <= 16; i++)
        {
            payload.Append(System.Globalization.CultureInfo.InvariantCulture, $"CHECK <n{i}@example.com>\r\n");
        }

        await duplex.WriteClientAsync(payload.ToString());
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await redis.Database.ExistsReached.Task.WaitAsync(safety.Token);
        Assert.Equal(16, redis.Database.ExistsStartedCount);
        Assert.Equal(16, session.Pipeline!.Occupied);

        redis.Database.NotifyExistsStartedAt = 17;
        redis.Database.ExistsReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await duplex.WriteClientLineAsync("CHECK <n17@example.com>");
        await Task.Yield();
        Assert.Equal(16, redis.Database.ExistsStartedCount);
        Assert.False(redis.Database.ExistsReached.Task.IsCompleted);

        redis.Database.BlockExists.TrySetResult();
        await redis.Database.ExistsReached.Task.WaitAsync(safety.Token);
        Assert.Equal(17, redis.Database.ExistsStartedCount);

        for (var i = 1; i <= 17; i++)
        {
            var line = await duplex.ReadClientLineAsync();
            Assert.StartsWith("238 <n", line, StringComparison.Ordinal);
        }

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task FastCheck_CannotOvertakeBlockedPrefixEmit()
    {
        var redis = new FakeRedisService();
        var blockA = BlockKey(redis, IdA);
        var history = CreateHistory(redis);
        _ = await history.LookupAsync(IdB);
        redis.Database.KeyExistsCount = 0;
        await using var duplex = new CheckPipelineDuplex();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var enqueue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueueReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Pipeline!.BeforeEnqueueProbe = async () =>
        {
            enqueueReached.TrySetResult();
            await enqueue.Task;
        };

        await duplex.WriteClientLineAsync("CHECK <a@example.com>");
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (session.Pipeline.Occupied < 1)
        {
            safety.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        Assert.Null(duplex.TryReadClientLine());
        blockA.TrySetResult();
        await enqueueReached.Task.WaitAsync(safety.Token);
        Assert.Equal(1, session.Pipeline.Occupied);
        Assert.Null(duplex.TryReadClientLine());

        await duplex.WriteClientLineAsync("CHECK <b@example.com>");
        while (session.Pipeline.Occupied < 2)
        {
            safety.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        Assert.Null(duplex.TryReadClientLine());
        enqueue.TrySetResult();
        Assert.Equal("238 <a@example.com> send article to be transferred", await duplex.ReadClientLineAsync());
        Assert.Equal("431 <b@example.com>", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task SlowTx_FastRedis_SeventeenthWaitsOnOccupancy()
    {
        var redis = new FakeRedisService();
        redis.Database.BlockExists = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        redis.Database.NotifyExistsStartedAt = 16;
        redis.Database.ExistsReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = CreateHistory(redis);
        await using var duplex = new CheckPipelineDuplex();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var payload = new StringBuilder();
        for (var i = 1; i <= 16; i++)
        {
            payload.Append(System.Globalization.CultureInfo.InvariantCulture, $"CHECK <n{i}@example.com>\r\n");
        }

        await duplex.WriteClientAsync(payload.ToString());
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await redis.Database.ExistsReached.Task.WaitAsync(safety.Token);
        Assert.Equal(16, session.Pipeline!.Occupied);
        Assert.Equal(16, redis.Database.ExistsStartedCount);

        var enqueue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueueReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Pipeline.BeforeEnqueueProbe = async () =>
        {
            enqueueReached.TrySetResult();
            await enqueue.Task;
        };

        redis.Database.BlockExists.TrySetResult();
        await enqueueReached.Task.WaitAsync(safety.Token);
        Assert.Equal(16, session.Pipeline.Occupied);
        Assert.True(session.Pipeline.InFlightCompletions <= CheckPipeline.Depth);
        Assert.Null(duplex.TryReadClientLine());

        redis.Database.NotifyExistsStartedAt = 17;
        redis.Database.ExistsReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await duplex.WriteClientLineAsync("CHECK <n17@example.com>");
        await Task.Yield();
        Assert.Equal(16, redis.Database.ExistsStartedCount);
        Assert.Equal(16, session.Pipeline.Occupied);
        Assert.False(redis.Database.ExistsReached.Task.IsCompleted);

        enqueue.TrySetResult();
        await redis.Database.ExistsReached.Task.WaitAsync(safety.Token);
        Assert.Equal(17, redis.Database.ExistsStartedCount);

        for (var i = 1; i <= 17; i++)
        {
            var line = await duplex.ReadClientLineAsync();
            Assert.StartsWith("238 <n", line, StringComparison.Ordinal);
        }

        while (session.Pipeline.InFlightCompletions != 0)
        {
            safety.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        Assert.Equal(0, session.Pipeline.Occupied);
        Assert.Equal(0, session.Pipeline.InFlightCompletions);
        Assert.True(session.Pipeline.PeakOccupied <= CheckPipeline.Depth);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task Disconnect_SixteenInFlight_ReleasesAllSlots()
    {
        var redis = new FakeRedisService();
        redis.Database.BlockExists = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        redis.Database.NotifyExistsStartedAt = 16;
        redis.Database.ExistsReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = CreateHistory(redis);
        await using var duplex = new CheckPipelineDuplex();
        using var sessionCts = new CancellationTokenSource();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync(sessionCts.Token);
        _ = await duplex.ReadClientLineAsync();

        var payload = new StringBuilder();
        for (var i = 1; i <= 16; i++)
        {
            payload.Append(System.Globalization.CultureInfo.InvariantCulture, $"CHECK <n{i}@example.com>\r\n");
        }

        await duplex.WriteClientAsync(payload.ToString());
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await redis.Database.ExistsReached.Task.WaitAsync(safety.Token);
        Assert.Equal(16, session.Pipeline!.Occupied);

        sessionCts.Cancel();
        redis.Database.BlockExists.TrySetCanceled();
        await run.WaitAsync(safety.Token);
        Assert.Equal(0, session.Pipeline.Occupied);
        Assert.Equal(0, session.Pipeline.InFlightCompletions);
        Assert.False(redis.IsUnavailable);
        Assert.Null(duplex.TryReadClientLine());
    }

    [Fact]
    public async Task FortyPipelinedChecks_DoNotRetainCompletions()
    {
        var redis = new FakeRedisService();
        var history = CreateHistory(redis);
        await using var duplex = new CheckPipelineDuplex();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var payload = new StringBuilder();
        for (var i = 1; i <= 40; i++)
        {
            payload.Append(System.Globalization.CultureInfo.InvariantCulture, $"CHECK <m{i}@example.com>\r\n");
        }

        await duplex.WriteClientAsync(payload.ToString());
        for (var i = 1; i <= 40; i++)
        {
            var line = await duplex.ReadClientLineAsync();
            Assert.StartsWith("238 <m", line, StringComparison.Ordinal);
        }

        Assert.Equal(0, session.Pipeline!.Occupied);
        Assert.Equal(0, session.Pipeline.InFlightCompletions);
        Assert.True(session.Pipeline.PeakOccupied <= CheckPipeline.Depth);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task Disconnect_CancelsInFlight_DoesNotOpenCooldown_OrEmit400()
    {
        var redis = new FakeRedisService();
        redis.Database.BlockExists = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        redis.Database.NotifyExistsStartedAt = 3;
        redis.Database.ExistsReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = CreateHistory(redis);
        await using var duplex = new CheckPipelineDuplex();
        using var sessionCts = new CancellationTokenSource();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync(sessionCts.Token);
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync(
            "CHECK <a@example.com>\r\nCHECK <b@example.com>\r\nCHECK <c@example.com>\r\n");
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await redis.Database.ExistsReached.Task.WaitAsync(safety.Token);

        sessionCts.Cancel();
        redis.Database.BlockExists.TrySetCanceled();
        await run.WaitAsync(safety.Token);
        Assert.False(redis.IsUnavailable);
        Assert.Null(duplex.TryReadClientLine());

        redis.Database.BlockExists = null;
        await using var duplex2 = new CheckPipelineDuplex();
        var session2 = duplex2.CreateSession(history);
        var run2 = session2.RunAsync();
        _ = await duplex2.ReadClientLineAsync();
        var existsBefore = redis.Database.KeyExistsCount;
        await duplex2.WriteClientLineAsync("CHECK <after@example.com>");
        Assert.Equal(
            "238 <after@example.com> send article to be transferred",
            await duplex2.ReadClientLineAsync());
        Assert.Equal(existsBefore + 1, redis.Database.KeyExistsCount);
        await duplex2.WriteClientLineAsync("QUIT");
        _ = await duplex2.ReadClientLineAsync();
        await run2;
    }

    [Fact]
    public async Task RedisFailure_UnderPipeline_OpensCooldown_NoFalse438()
    {
        var redis = new FakeRedisService();
        redis.Database.BlockExists = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        redis.Database.NotifyExistsStartedAt = 3;
        redis.Database.ExistsReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = CreateHistory(redis);
        await using var duplex = new CheckPipelineDuplex();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync(
            "CHECK <a@example.com>\r\nCHECK <b@example.com>\r\nCHECK <c@example.com>\r\n");
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await redis.Database.ExistsReached.Task.WaitAsync(safety.Token);
        while (session.Pipeline is null || session.Pipeline.Occupied < 3)
        {
            safety.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        redis.Database.ExistsException = new RedisUnavailableException("down");
        redis.Database.BlockExists.TrySetResult();

        Assert.Equal("431 <a@example.com>", await duplex.ReadClientLineAsync());
        Assert.Equal("431 <b@example.com>", await duplex.ReadClientLineAsync());
        Assert.Equal("431 <c@example.com>", await duplex.ReadClientLineAsync());
        Assert.True(redis.IsUnavailable);
        Assert.False(history.ContainsLocal(HistoryDigest.FromMessageId(IdA)));
        var exists = redis.Database.KeyExistsCount;

        await duplex.WriteClientLineAsync("CHECK <later@example.com>");
        Assert.Equal("431 <later@example.com>", await duplex.ReadClientLineAsync());
        Assert.Equal(exists, redis.Database.KeyExistsCount);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task OneLookupCanceled_DoesNotOpenCooldown_OthersContinue()
    {
        var redis = new FakeRedisService();
        var blockA = BlockKey(redis, IdA);
        var blockB = BlockKey(redis, IdB);
        var history = CreateHistory(redis);
        await using var duplex = new CheckPipelineDuplex();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync("CHECK <a@example.com>\r\nCHECK <b@example.com>\r\n");
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (redis.Database.ExistsStartedCount < 2)
        {
            safety.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        blockA.TrySetCanceled();
        blockB.TrySetResult();
        Assert.Equal("431 <a@example.com>", await duplex.ReadClientLineAsync());
        Assert.Equal("238 <b@example.com> send article to be transferred", await duplex.ReadClientLineAsync());
        Assert.False(redis.IsUnavailable);

        await duplex.WriteClientLineAsync("CHECK <c@example.com>");
        Assert.Equal("238 <c@example.com> send article to be transferred", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task SameMessageId_ConcurrentChecks_RemainAdvisory()
    {
        var redis = new FakeRedisService();
        redis.Database.BlockExists = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        redis.Database.NotifyExistsStartedAt = 2;
        redis.Database.ExistsReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = CreateHistory(redis);
        await using var duplex = new CheckPipelineDuplex();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync("CHECK <a@example.com>\r\nCHECK <a@example.com>\r\n");
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await redis.Database.ExistsReached.Task.WaitAsync(safety.Token);
        redis.Database.BlockExists.TrySetResult();
        Assert.Equal("238 <a@example.com> send article to be transferred", await duplex.ReadClientLineAsync());
        Assert.Equal("238 <a@example.com> send article to be transferred", await duplex.ReadClientLineAsync());
        Assert.False(history.ContainsLocal(HistoryDigest.FromMessageId(IdA)));
        Assert.Equal(0, history.Writes.Count);
        Assert.Equal(0, redis.Database.SetCount);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public void Depth_IsArchitecturalSixteen_AndNotConfigurable()
    {
        Assert.Equal(16, CheckPipeline.Depth);
        Assert.Null(typeof(NntpdOptions).GetProperty("CheckPipelineDepth"));
        Assert.Null(typeof(NntpSession).GetProperty("CheckPipelineDepth"));
    }

    [Theory]
    [InlineData("DATE", "111 ")]
    [InlineData("AUTHINFO USER peer", "381 ")]
    [InlineData("MODE READER", "502 Command not available on this server")]
    [InlineData("MODE STREAM", "203 ")]
    [InlineData("STARTTLS", "502 TLS provider unavailable")]
    [InlineData("QUIT", "205 Connection closing")]
    public async Task NonCheckBarrier_EmitsCheckBeforeFollowingCommand(string command, string followingPrefix)
    {
        var redis = new FakeRedisService();
        var blockA = BlockKey(redis, IdA);
        var history = CreateHistory(redis);
        await using var duplex = new CheckPipelineDuplex();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync("CHECK <a@example.com>\r\n" + command + "\r\n");
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (redis.Database.ExistsStartedCount < 1)
        {
            safety.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        Assert.Null(duplex.TryReadClientLine());
        blockA.TrySetResult();
        Assert.Equal("238 <a@example.com> send article to be transferred", await duplex.ReadClientLineAsync());
        var following = await duplex.ReadClientLineAsync();
        Assert.StartsWith(followingPrefix, following, StringComparison.Ordinal);
        if (!command.StartsWith("QUIT", StringComparison.Ordinal))
        {
            await duplex.WriteClientLineAsync("QUIT");
            _ = await duplex.ReadClientLineAsync();
        }

        await run;
    }

    [Fact]
    public async Task CompressBarrier_EmitsCheckBefore206()
    {
        var redis = new FakeRedisService();
        var blockA = BlockKey(redis, IdA);
        var history = CreateHistory(redis);
        await using var duplex = new CheckPipelineDuplex();
        var session = duplex.CreateSession(history);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync("CHECK <a@example.com>\r\nCOMPRESS DEFLATE\r\n");
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (redis.Database.ExistsStartedCount < 1)
        {
            safety.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        Assert.Null(duplex.TryReadClientLine());
        blockA.TrySetResult();
        Assert.Equal("238 <a@example.com> send article to be transferred", await duplex.ReadClientLineAsync());
        Assert.Equal("206 Compression active", await duplex.ReadClientLineAsync());
        await run.WaitAsync(safety.Token);
    }

    private static HistoryDb CreateHistory(FakeRedisService redis) =>
        new(redis, TimeSpan.FromHours(2), NullLogger<HistoryDb>.Instance, TimeProvider.System, new HistoryWriteQueue());

    private static TaskCompletionSource BlockKey(FakeRedisService redis, ReadOnlySpan<byte> messageId)
    {
        var key = HistoryRedisKeys.Create(HistoryDigest.FromMessageId(messageId));
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        redis.Database.BlockExistsByKey[Convert.ToHexString(key)] = block;
        return block;
    }

    private sealed class CheckPipelineDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public NntpSession CreateSession(
            IHistoryDb history,
            IArticleIngestionQueue? ingestion = null)
        {
            var peers = TransitTestPeers.ForAllowFrom(TransitPeer);
            var connection = new PipelineTestConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new IPEndPoint(TransitPeer, 40000)));
            return new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                transitPeerAuthorization: peers,
                historyDb: history,
                articleIngestion: ingestion);
        }

        public async Task WriteClientLineAsync(string line)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
            await _clientToServer.Writer.WriteAsync(bytes);
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task WriteClientAsync(string payload)
        {
            var bytes = Encoding.ASCII.GetBytes(payload);
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

        public string? TryReadClientLine()
        {
            if (!_serverToClient.Reader.TryRead(out var result))
            {
                return null;
            }

            var buffer = result.Buffer;
            if (!NntpDelimiterSearch.TryReadLine(ref buffer, out var lineBytes))
            {
                _serverToClient.Reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
                return null;
            }

            var text = Encoding.ASCII.GetString(System.Buffers.BuffersExtensions.ToArray(in lineBytes));
            _serverToClient.Reader.AdvanceTo(buffer.Start, buffer.Start);
            return text;
        }

        public async ValueTask DisposeAsync()
        {
            await _clientToServer.Writer.CompleteAsync();
            await _clientToServer.Reader.CompleteAsync();
            await _serverToClient.Writer.CompleteAsync();
            await _serverToClient.Reader.CompleteAsync();
        }
    }

    private sealed class PipelineTestConnection(PipeReader input, PipeWriter output, ConnectionClientIdentity identity)
        : INntpConnection
    {
        private readonly CancellationTokenSource _cts = new();

        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
        public EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;
        public EndPoint? LocalEndPoint => null;
        public ConnectionClientIdentity ClientIdentity { get; } = identity;
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
}

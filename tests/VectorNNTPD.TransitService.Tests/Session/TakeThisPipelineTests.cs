using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.History;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.Networking.Transport;
using VectorNNTPD.TransitService.Redis;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.Commands;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Session.Framing;
using VectorNNTPD.TransitService.Tests.Fixtures;
using VectorNNTPD.TransitService.Tests.TestDoubles;
using VectorNNTPD.TransitService.Tests.Transit;

namespace VectorNNTPD.TransitService.Tests.Session;

/// <summary>
/// TAKETHIS bounded pipeline: RX frames and detaches owned articles; Peek/enqueue/239
/// complete off the RX stack; one PipeReader consumer; ordered immediate TX flush.
/// </summary>
public sealed class TakeThisPipelineTests
{
    private static NntpAuthorization TransitAuth { get; } = new(
        isAuthenticated: true,
        authorizedReader: false,
        authorizedTransit: true,
        postingPermitted: false,
        streamingPermitted: true);

    [Fact]
    public void PipelineDepth_MatchesCheckWindow()
    {
        Assert.Equal(CheckPipeline.Depth, TakeThisPipeline.Depth);
        Assert.Equal(16, TakeThisPipeline.Depth);
    }

    [Fact]
    public async Task MessageIdAvailable_StartsHistoryPeek_BeforeArticleCompletes()
    {
        var history = new GatedHistoryDb();
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var peekHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TakeThisWindow!.AfterPeekStarted = () => peekHook.TrySetResult();

        const string id = "<early-peek@ex.com>";
        await duplex.WriteClientAsync($"TAKETHIS {id}\r\n{CanonicalArticleText.Destuffed(id, "body-without-terminator\r\n")}");

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await peekHook.Task.WaitAsync(safety.Token);
        await history.FirstPeek.Task.WaitAsync(safety.Token);

        Assert.Equal(1, history.PeekStarted);
        Assert.Equal(1, session.TakeThisWindow.Occupied);
        Assert.Null(duplex.TryReadClientLine());
        Assert.Equal(0, queue.Count);

        history.Release.TrySetResult(HistoryLookupResult.Unseen);
        await Task.Yield();
        Assert.Null(duplex.TryReadClientLine());

        await duplex.WriteClientAsync(".\r\n");
        Assert.Equal($"239 {id}", await duplex.ReadClientLineAsync());

        var article = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal(id, article!.MessageId);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task TwoThirtyNine_WaitsForCompleteArticle_EvenWhenHistoryAlreadyReady()
    {
        var history = new GatedHistoryDb();
        history.Force("<ready-hist@ex.com>", HistoryLookupResult.Unseen);
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var peekHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TakeThisWindow!.AfterPeekStarted = () => peekHook.TrySetResult();

        const string id = "<ready-hist@ex.com>";
        await duplex.WriteClientAsync($"TAKETHIS {id}\r\n{CanonicalArticleText.Destuffed(id, string.Empty)}");
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await peekHook.Task.WaitAsync(safety.Token);
        Assert.Equal(1, history.PeekStarted);
        Assert.Null(duplex.TryReadClientLine());
        Assert.Equal(0, queue.Count);

        await duplex.WriteClientAsync("body\r\n.\r\n");
        Assert.Equal($"239 {id}", await duplex.ReadClientLineAsync());
        Assert.Equal(1, queue.Count);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task TwoThirtyNine_WaitsForHistory_AfterArticleReceived()
    {
        var history = new GatedHistoryDb();
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string id = "<wait-hist@ex.com>";
        await duplex.WriteClientAsync(BuildTakeThis(id, CanonicalArticleText.Destuffed(id, "x\r\n")));

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => session.TakeThisWindow is { Occupied: 1 } && history.PeekStarted == 1, safety.Token);
        Assert.Null(duplex.TryReadClientLine());
        Assert.Equal(0, queue.Count);

        history.Release.TrySetResult(HistoryLookupResult.Unseen);
        Assert.Equal($"239 {id}", await duplex.ReadClientLineAsync());
        Assert.Equal(1, queue.Count);
        Assert.Contains(id, history.Remembered);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task ArticleReceiveAndHistory_Overlap_SecondArticleReceivedWhileFirstPeekPending()
    {
        var history = new GatedHistoryDb();
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync(BuildTakeThis("<a@ex.com>", CanonicalArticleText.Destuffed("<a@ex.com>", "1\r\n")));
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => history.PeekStarted == 1 && session.TakeThisWindow!.Occupied == 1, safety.Token);

        await duplex.WriteClientAsync(BuildTakeThis("<b@ex.com>", CanonicalArticleText.Destuffed("<b@ex.com>", "2\r\n")));
        await WaitUntilAsync(() => history.PeekStarted == 2 && session.TakeThisWindow!.Occupied == 2, safety.Token);

        Assert.True(session.TakeThisWindow!.Occupied <= TakeThisPipeline.Depth);
        Assert.True(session.TakeThisWindow.InFlightCompletions <= TakeThisPipeline.Depth);
        Assert.Null(duplex.TryReadClientLine());
        Assert.Equal(0, queue.Count);

        history.Release.TrySetResult(HistoryLookupResult.Unseen);
        Assert.Equal("239 <a@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal("239 <b@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal(2, queue.Count);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task HoldReceive_PreventsNextTakeThisFromBeingParsed()
    {
        var history = new GatedHistoryDb();
        history.Force("<hold-a@ex.com>", HistoryLookupResult.Unseen);
        history.Force("<hold-b@ex.com>", HistoryLookupResult.Unseen);
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiveA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peekB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivesInFlight = 0;
        var maxReceivesInFlight = 0;
        session.TakeThisWindow!.HoldReceive = hold.Task;
        session.TakeThisWindow.AfterPeekStarted = () =>
        {
            if (history.PeekStarted >= 2)
            {
                peekB.TrySetResult();
            }
        };
        session.TakeThisWindow.AfterReceiveStarted = () =>
        {
            var n = Interlocked.Increment(ref receivesInFlight);
            if (n > maxReceivesInFlight)
            {
                maxReceivesInFlight = n;
            }

            receiveA.TrySetResult();
        };
        session.TakeThisWindow.AfterReceiveCompleted = () => Interlocked.Decrement(ref receivesInFlight);

        await duplex.WriteClientAsync(
            BuildTakeThis("<hold-a@ex.com>", CanonicalArticleText.Destuffed("<hold-a@ex.com>", "1\r\n")) +
            BuildTakeThis("<hold-b@ex.com>", CanonicalArticleText.Destuffed("<hold-b@ex.com>", "2\r\n")));

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await receiveA.Task.WaitAsync(safety.Token);
        Assert.Equal(1, history.PeekStarted);
        Assert.Equal(1, session.TakeThisWindow.Occupied);
        Assert.Equal(1, Volatile.Read(ref receivesInFlight));
        Assert.False(peekB.Task.IsCompleted);

        hold.TrySetResult();
        await peekB.Task.WaitAsync(safety.Token);
        Assert.Equal(2, history.PeekStarted);
        Assert.True(Volatile.Read(ref maxReceivesInFlight) == 1);

        Assert.Equal("239 <hold-a@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal("239 <hold-b@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal(1, maxReceivesInFlight);
        Assert.Equal(0, Volatile.Read(ref receivesInFlight));

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task OpenArticle_ConsumesFollowingTakeThisCommandAsPayload()
    {
        var history = new GatedHistoryDb();
        history.Force("<open-a@ex.com>", HistoryLookupResult.Unseen);
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var receiveA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peekB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TakeThisWindow!.AfterReceiveStarted = () => receiveA.TrySetResult();
        session.TakeThisWindow.AfterPeekStarted = () =>
        {
            if (history.PeekStarted >= 2)
            {
                peekB.TrySetResult();
            }
        };

        await duplex.WriteClientAsync("TAKETHIS <open-a@ex.com>\r\nSubject: a\r\n\r\npartial\r\n");
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await receiveA.Task.WaitAsync(safety.Token);
        await history.FirstPeek.Task.WaitAsync(safety.Token);
        Assert.Equal(1, history.PeekStarted);
        Assert.Equal(1, session.TakeThisWindow.Occupied);

        await duplex.WriteClientAsync(BuildTakeThis("<open-b@ex.com>", CanonicalArticleText.Destuffed("<open-b@ex.com>", "2\r\n")));
        Assert.False(peekB.Task.IsCompleted);
        Assert.Equal(1, history.PeekStarted);

        Assert.Equal("439 <open-a@ex.com>", await duplex.ReadClientLineAsync());
        Assert.False(peekB.Task.IsCompleted);
        Assert.Equal(1, history.PeekStarted);
        Assert.Equal(0, queue.Count);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task TwoOccupiedSlots_NeverHaveTwoReceivesInFlight()
    {
        var history = new GatedHistoryDb();
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var receiveStarted = 0;
        var receiveCompleted = 0;
        var inFlight = 0;
        var maxInFlight = 0;
        var firstReceiveDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReceiveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TakeThisWindow!.AfterReceiveStarted = () =>
        {
            var n = Interlocked.Increment(ref inFlight);
            if (n > maxInFlight)
            {
                maxInFlight = n;
            }

            var started = Interlocked.Increment(ref receiveStarted);
            if (started == 2)
            {
                secondReceiveStarted.TrySetResult();
            }
        };
        session.TakeThisWindow.AfterReceiveCompleted = () =>
        {
            Interlocked.Decrement(ref inFlight);
            if (Interlocked.Increment(ref receiveCompleted) == 1)
            {
                firstReceiveDone.TrySetResult();
            }
        };

        await duplex.WriteClientAsync(BuildTakeThis("<seq-a@ex.com>", CanonicalArticleText.Destuffed("<seq-a@ex.com>", "1\r\n")));
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await firstReceiveDone.Task.WaitAsync(safety.Token);
        Assert.Equal(1, receiveStarted);
        Assert.Equal(0, Volatile.Read(ref inFlight));
        Assert.Equal(1, session.TakeThisWindow.Occupied);
        Assert.Equal(1, history.PeekStarted);

        await duplex.WriteClientAsync(BuildTakeThis("<seq-b@ex.com>", CanonicalArticleText.Destuffed("<seq-b@ex.com>", "2\r\n")));
        await secondReceiveStarted.Task.WaitAsync(safety.Token);
        await WaitUntilAsync(
            () => receiveCompleted >= 2 && session.TakeThisWindow.Occupied == 2,
            safety.Token);

        Assert.Equal(1, maxInFlight);
        Assert.Equal(2, history.PeekStarted);
        Assert.Equal(2, session.TakeThisWindow.Occupied);
        Assert.Equal(0, Volatile.Read(ref inFlight));

        history.Release.TrySetResult(HistoryLookupResult.Unseen);
        Assert.Equal("239 <seq-a@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal("239 <seq-b@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal(1, session.TakeThisWindow.MaxActiveArticleReads);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task DetachedArticle_AllowsNextReceive_WhilePeekRemainsGated()
    {
        var history = new GatedHistoryDb();
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var detached = new List<byte[]>();
        var receiveADone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiveBStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TakeThisWindow!.AfterArticleDetached = payload =>
        {
            detached.Add(payload.ToArray());
            if (detached.Count >= 1)
            {
                receiveADone.TrySetResult();
            }
        };
        session.TakeThisWindow.AfterReceiveStarted = () =>
        {
            if (history.PeekStarted >= 2)
            {
                receiveBStarted.TrySetResult();
            }
        };

        await duplex.WriteClientAsync(BuildTakeThis("<det-a@ex.com>", CanonicalArticleText.Destuffed("<det-a@ex.com>", "1\r\n")));
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await receiveADone.Task.WaitAsync(safety.Token);
        Assert.Single(detached);
        Assert.Equal(1, session.TakeThisWindow.Occupied);
        Assert.Equal(0, session.TakeThisWindow.ActiveArticleReads);
        Assert.Null(duplex.TryReadClientLine());

        await duplex.WriteClientAsync(BuildTakeThis("<det-b@ex.com>", CanonicalArticleText.Destuffed("<det-b@ex.com>", "2\r\n")));
        await receiveBStarted.Task.WaitAsync(safety.Token);
        await WaitUntilAsync(
            () => detached.Count == 2 && session.TakeThisWindow.Occupied == 2,
            safety.Token);

        Assert.Equal(1, session.TakeThisWindow.MaxActiveArticleReads);
        Assert.Equal(2, history.PeekStarted);
        Assert.Equal(2, session.TakeThisWindow.InFlightCompletions);
        Assert.Null(duplex.TryReadClientLine());
        Assert.Equal(0, queue.Count);

        history.Release.TrySetResult(HistoryLookupResult.Unseen);
        Assert.Equal("239 <det-a@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal("239 <det-b@ex.com>", await duplex.ReadClientLineAsync());

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task FastPeek_DoesNotSerializeRx_BehindHeldEmit()
    {
        var history = new GatedHistoryDb();
        history.Force("<fast-a@ex.com>", HistoryLookupResult.Unseen);
        history.Force("<fast-b@ex.com>", HistoryLookupResult.Unseen);
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var holdEmit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var emitStarted = 0;
        var receiveBStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peekB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var noTwoThirtyNineOnDetachA = false;
        session.TakeThisWindow!.BeforeEnqueueProbe = () =>
        {
            if (Interlocked.Increment(ref emitStarted) == 1)
            {
                return new ValueTask(holdEmit.Task);
            }

            return ValueTask.CompletedTask;
        };
        session.TakeThisWindow.AfterArticleDetached = _ =>
        {
            if (history.PeekStarted == 1)
            {
                noTwoThirtyNineOnDetachA = duplex.TryReadClientLine() is null;
            }
        };
        session.TakeThisWindow.AfterPeekStarted = () =>
        {
            if (history.PeekStarted >= 2)
            {
                peekB.TrySetResult();
            }
        };
        session.TakeThisWindow.AfterReceiveStarted = () =>
        {
            if (history.PeekStarted >= 2)
            {
                receiveBStarted.TrySetResult();
            }
        };

        await duplex.WriteClientAsync(
            BuildTakeThis("<fast-a@ex.com>", CanonicalArticleText.Destuffed("<fast-a@ex.com>", "1\r\n")) +
            BuildTakeThis("<fast-b@ex.com>", CanonicalArticleText.Destuffed("<fast-b@ex.com>", "2\r\n")));

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await peekB.Task.WaitAsync(safety.Token);
        await receiveBStarted.Task.WaitAsync(safety.Token);
        Assert.True(noTwoThirtyNineOnDetachA);
        Assert.True(holdEmit.Task.IsCompleted is false);
        Assert.Null(duplex.TryReadClientLine());
        Assert.Equal(1, session.TakeThisWindow.MaxActiveArticleReads);

        holdEmit.TrySetResult();
        Assert.Equal("239 <fast-a@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal("239 <fast-b@ex.com>", await duplex.ReadClientLineAsync());

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task ThreeDetachedArticles_HeldCompletion_RetainOwnedBuffers()
    {
        var history = new GatedHistoryDb();
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var detached = new List<ReadOnlyMemory<byte>>();
        session.TakeThisWindow!.AfterArticleDetached = payload => detached.Add(payload);

        await duplex.WriteClientAsync(
            BuildTakeThis("<tri-a@ex.com>", CanonicalArticleText.Destuffed("<tri-a@ex.com>", "A\r\n")) +
            BuildTakeThis("<tri-b@ex.com>", CanonicalArticleText.Destuffed("<tri-b@ex.com>", "B\r\n")) +
            BuildTakeThis("<tri-c@ex.com>", CanonicalArticleText.Destuffed("<tri-c@ex.com>", "C\r\n")));

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => detached.Count == 3 && session.TakeThisWindow.Occupied == 3,
            safety.Token);

        Assert.Equal(3, history.PeekStarted);
        Assert.Equal(0, session.TakeThisWindow.ActiveArticleReads);
        Assert.Equal(1, session.TakeThisWindow.MaxActiveArticleReads);
        Assert.Null(duplex.TryReadClientLine());
        Assert.Equal(CanonicalArticleText.Destuffed("<tri-a@ex.com>", "A\r\n"), Encoding.ASCII.GetString(detached[0].Span));
        Assert.Equal(CanonicalArticleText.Destuffed("<tri-b@ex.com>", "B\r\n"), Encoding.ASCII.GetString(detached[1].Span));
        Assert.Equal(CanonicalArticleText.Destuffed("<tri-c@ex.com>", "C\r\n"), Encoding.ASCII.GetString(detached[2].Span));
        Assert.True(MemoryMarshal.TryGetArray(detached[0], out var aSeg));
        Assert.True(MemoryMarshal.TryGetArray(detached[1], out var bSeg));
        Assert.True(MemoryMarshal.TryGetArray(detached[2], out var cSeg));
        Assert.Equal(IHaveArticleReader.InitialCapacity, aSeg.Array!.Length);
        Assert.NotSame(aSeg.Array, bSeg.Array);
        Assert.NotSame(bSeg.Array, cSeg.Array);

        history.Release.TrySetResult(HistoryLookupResult.Unseen);
        Assert.Equal("239 <tri-a@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal("239 <tri-b@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal("239 <tri-c@ex.com>", await duplex.ReadClientLineAsync());

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task DepthFull_DoesNotAdmitSeventeenthUntilSlotReleases()
    {
        var history = new GatedHistoryDb();
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var detached = 0;
        session.TakeThisWindow!.AfterArticleDetached = _ => Interlocked.Increment(ref detached);

        var extra = TakeThisPipeline.Depth + 1;
        var batch = new StringBuilder();
        for (var i = 0; i < extra; i++)
        {
            batch.Append(BuildTakeThis($"<d{i}@ex.com>", CanonicalArticleText.Destuffed($"<d{i}@ex.com>", "x\r\n")));
        }

        await duplex.WriteClientAsync(batch.ToString());
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => Volatile.Read(ref detached) == TakeThisPipeline.Depth
                  && session.TakeThisWindow.Occupied == TakeThisPipeline.Depth,
            safety.Token);

        Assert.Equal(TakeThisPipeline.Depth, history.PeekStarted);
        Assert.Equal(0, session.TakeThisWindow.ActiveArticleReads);
        Assert.Equal(1, session.TakeThisWindow.MaxActiveArticleReads);
        Assert.Null(duplex.TryReadClientLine());

        history.Release.TrySetResult(HistoryLookupResult.Unseen);
        await WaitUntilAsync(() => history.PeekStarted == extra, safety.Token);
        for (var i = 0; i < extra; i++)
        {
            Assert.Equal($"239 <d{i}@ex.com>", await duplex.ReadClientLineAsync());
        }

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task TakeThisInFlight_CheckDoesNotLookupUntilDrain()
    {
        var history = new GatedHistoryDb();
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync(
            BuildTakeThis("<bar-a@ex.com>", CanonicalArticleText.Destuffed("<bar-a@ex.com>", "1\r\n")) +
            BuildTakeThis("<bar-b@ex.com>", CanonicalArticleText.Destuffed("<bar-b@ex.com>", "2\r\n")) +
            "CHECK <bar-chk@ex.com>\r\n");

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => history.PeekStarted == 2 && session.TakeThisWindow is { Occupied: 2 },
            safety.Token);
        Assert.True(session.Pipeline is null || session.Pipeline.Occupied == 0);
        Assert.Null(duplex.TryReadClientLine());

        history.Release.TrySetResult(HistoryLookupResult.Unseen);
        Assert.Equal("239 <bar-a@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal("239 <bar-b@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal("238 <bar-chk@ex.com> send article to be transferred", await duplex.ReadClientLineAsync());
        Assert.Equal(3, history.PeekStarted);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task TakeThisInFlight_QuitWaitsForOrderedTwoThirtyNine()
    {
        var history = new GatedHistoryDb();
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TakeThisWindow!.AfterArticleDetached = _ => detached.TrySetResult();

        await duplex.WriteClientAsync(
            BuildTakeThis("<quit-a@ex.com>", CanonicalArticleText.Destuffed("<quit-a@ex.com>", "1\r\n")) +
            "QUIT\r\n");

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await detached.Task.WaitAsync(safety.Token);
        Assert.Equal(1, session.TakeThisWindow.Occupied);
        Assert.Null(duplex.TryReadClientLine());

        history.Release.TrySetResult(HistoryLookupResult.Unseen);
        Assert.Equal("239 <quit-a@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal("205 Connection closing", await duplex.ReadClientLineAsync());
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DetachedPayload_SurvivesSubsequentArticleReceive()
    {
        var history = new GatedHistoryDb();
        history.Force("<own-a@ex.com>", HistoryLookupResult.Unseen);
        history.Force("<own-b@ex.com>", HistoryLookupResult.Unseen);
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        ReadOnlyMemory<byte> first = default;
        var firstDetached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDetached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TakeThisWindow!.AfterArticleDetached = payload =>
        {
            if (first.IsEmpty)
            {
                first = payload;
                firstDetached.TrySetResult();
                return;
            }

            secondDetached.TrySetResult();
        };

        var wireA = CanonicalArticleText.Destuffed("<own-a@ex.com>", ".dot\r\n")
            .Replace(".dot\r\n", "..dot\r\n", StringComparison.Ordinal);
        await duplex.WriteClientAsync(BuildTakeThis("<own-a@ex.com>", wireA));
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await firstDetached.Task.WaitAsync(safety.Token);
        var snapshot = first.ToArray();

        await duplex.WriteClientAsync(BuildTakeThis("<own-b@ex.com>", CanonicalArticleText.Destuffed("<own-b@ex.com>", "2\r\n")));
        await secondDetached.Task.WaitAsync(safety.Token);
        Assert.Equal(wireA, Encoding.ASCII.GetString(first.Span));
        Assert.Equal(snapshot, first.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(first, out var segment));
        Assert.Equal(IHaveArticleReader.InitialCapacity, segment.Array!.Length);

        Assert.Equal("239 <own-a@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal("239 <own-b@ex.com>", await duplex.ReadClientLineAsync());

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task TooLarge_ThenNextTakeThis_IsNotConsumedAsPayload()
    {
        var history = new GatedHistoryDb();
        history.Force("<big-pipe@ex.com>", HistoryLookupResult.Unseen);
        history.Force("<after-big@ex.com>", HistoryLookupResult.Unseen);
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions
        {
            QueueCapacity = 8,
            MaxArticleBytes = 256,
        });
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync(
            BuildTakeThis("<big-pipe@ex.com>", "Subject: oversized\r\n\r\n" + new string('Z', 300) + "\r\n") +
            BuildTakeThis("<after-big@ex.com>", CanonicalArticleText.Destuffed("<after-big@ex.com>", "ok\r\n")));

        Assert.Equal("439 <big-pipe@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal("239 <after-big@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal(1, queue.Count);
        var article = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal("<after-big@ex.com>", article!.MessageId);
        Assert.Equal(ArticleParseStatus.CanonicalV1, article.Record.ParseStatus);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task SinglePipeReader_MaxActiveArticleReadsIsOne()
    {
        var history = new GatedHistoryDb();
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync(
            BuildTakeThis("<one-a@ex.com>", CanonicalArticleText.Destuffed("<one-a@ex.com>", "1\r\n")) +
            BuildTakeThis("<one-b@ex.com>", CanonicalArticleText.Destuffed("<one-b@ex.com>", "2\r\n")) +
            BuildTakeThis("<one-c@ex.com>", CanonicalArticleText.Destuffed("<one-c@ex.com>", "3\r\n")));

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => session.TakeThisWindow is { Occupied: 3, ActiveArticleReads: 0 },
            safety.Token);
        Assert.Equal(1, session.TakeThisWindow!.MaxActiveArticleReads);

        history.Release.TrySetResult(HistoryLookupResult.Unseen);
        Assert.Equal("239 <one-a@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal("239 <one-b@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal("239 <one-c@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal(1, session.TakeThisWindow.MaxActiveArticleReads);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task SlowHeadProcess_DoesNotBlockLaterDestuff_ResponsesStayOrdered()
    {
        var history = new GatedHistoryDb();
        history.Force("<slow-n@ex.com>", HistoryLookupResult.Unseen);
        history.Force("<fast-n1@ex.com>", HistoryLookupResult.Unseen);
        history.Force("<fast-n2@ex.com>", HistoryLookupResult.Unseen);
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var holdN = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new ConcurrentBag<string>();
        var completed = new ConcurrentBag<string>();
        session.TakeThisWindow!.HoldProcess = id =>
        {
            var text = Encoding.ASCII.GetString(id.Span);
            return text == "<slow-n@ex.com>" ? holdN.Task : slowEntered.Task;
        };
        session.TakeThisWindow.AfterProcessStarted = id =>
        {
            var text = Encoding.ASCII.GetString(id.Span);
            started.Add(text);
            if (text == "<slow-n@ex.com>")
            {
                slowEntered.TrySetResult();
            }
        };
        session.TakeThisWindow.AfterProcessCompleted = id => completed.Add(Encoding.ASCII.GetString(id.Span));

        await duplex.WriteClientAsync(
            BuildTakeThis("<slow-n@ex.com>", CanonicalArticleText.Destuffed("<slow-n@ex.com>", "N\r\n")) +
            BuildTakeThis("<fast-n1@ex.com>", CanonicalArticleText.Destuffed("<fast-n1@ex.com>", "N1\r\n")) +
            BuildTakeThis("<fast-n2@ex.com>", CanonicalArticleText.Destuffed("<fast-n2@ex.com>", "N2\r\n")));

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(
            () => completed.Contains("<fast-n1@ex.com>") && completed.Contains("<fast-n2@ex.com>"),
            safety.Token);

        Assert.Contains("<slow-n@ex.com>", started);
        Assert.DoesNotContain("<slow-n@ex.com>", completed);
        Assert.True(session.TakeThisWindow.MaxActiveArticleProcessing >= 2);
        Assert.Equal(3, session.TakeThisWindow.Occupied);
        Assert.Null(duplex.TryReadClientLine());
        Assert.Equal(0, queue.Count);
        Assert.Empty(history.Remembered);

        holdN.TrySetResult();
        Assert.Equal("239 <slow-n@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal("239 <fast-n1@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal("239 <fast-n2@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Contains("<slow-n@ex.com>", completed);
        Assert.Equal(3, queue.Count);
        Assert.Equal(3, history.Remembered.Count);
        Assert.Contains("<slow-n@ex.com>", history.Remembered);
        Assert.Contains("<fast-n1@ex.com>", history.Remembered);
        Assert.Contains("<fast-n2@ex.com>", history.Remembered);
        Assert.Equal(1, session.TakeThisWindow.MaxActiveArticleReads);
        Assert.Equal(0, session.TakeThisWindow.Occupied);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task HistorySeen_SkipsDestuff()
    {
        var history = new GatedHistoryDb();
        history.Force("<skip-crc@ex.com>", HistoryLookupResult.Seen);
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var processStarted = 0;
        session.TakeThisWindow!.AfterProcessStarted = _ => Interlocked.Increment(ref processStarted);

        await duplex.WriteClientAsync(BuildTakeThis("<skip-crc@ex.com>", CanonicalArticleText.Destuffed("<skip-crc@ex.com>", "x\r\n")));
        Assert.Equal("239 <skip-crc@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal(0, Volatile.Read(ref processStarted));
        Assert.Equal(0, session.TakeThisWindow.MaxActiveArticleProcessing);
        Assert.Equal(0, queue.Count);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task Shutdown_CancelsHeldProcess_ReleasesSlotWithoutResponse()
    {
        var history = new GatedHistoryDb();
        history.Force("<shut-n@ex.com>", HistoryLookupResult.Unseen);
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TakeThisWindow!.HoldProcess = _ => hold.Task;
        session.TakeThisWindow.AfterProcessStarted = _ => started.TrySetResult();

        await duplex.WriteClientAsync(
            BuildTakeThis("<shut-n@ex.com>", CanonicalArticleText.Destuffed("<shut-n@ex.com>", "x\r\n")));
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await started.Task.WaitAsync(safety.Token);
        Assert.Equal(1, session.TakeThisWindow.Occupied);
        Assert.Null(duplex.TryReadClientLine());
        Assert.Equal(0, queue.Count);

        await session.TakeThisWindow.ShutdownAsync().AsTask().WaitAsync(safety.Token);
        Assert.Equal(0, session.TakeThisWindow.Occupied);
        Assert.Null(duplex.TryReadClientLine());
        Assert.Equal(0, queue.Count);
        Assert.Empty(history.Remembered);

        await session.Connection.CompleteAsync();
        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        // codeql[cs/catch-of-all-exceptions] Best-effort test cleanup must tolerate any filesystem or disposal failure.
        catch (Exception ex)
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
            // RX may observe connection close after the pipeline already shut down.
        }
    }

    [Fact]
    public async Task HistorySeen_Returns239_DoesNotEnqueue()
    {
        var history = new GatedHistoryDb();
        history.Force("<dup@ex.com>", HistoryLookupResult.Seen);
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync(BuildTakeThis("<dup@ex.com>", "Subject: d\r\n\r\nz\r\n"));
        Assert.Equal("239 <dup@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);
        Assert.Empty(history.Remembered);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task RememberedId_TakeThis_RemainsAcceptedDuplicate()
    {
        var redis = new FakeRedisService();
        var history = new HistoryDb(
            redis,
            TimeSpan.FromHours(2),
            NullLogger<HistoryDb>.Instance,
            TimeProvider.System,
            new HistoryWriteQueue());
        history.Remember("<dup@ex.com>"u8.ToArray());
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync(BuildTakeThis("<dup@ex.com>", "Subject: d\r\n\r\nz\r\n"));
        Assert.Equal("239 <dup@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);

        await duplex.WriteClientLineAsync("CHECK <dup@ex.com>");
        Assert.Equal("438 <dup@ex.com>", await duplex.ReadClientLineAsync());

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task HistoryMiss_Returns239_EnqueuesAndRemembers()
    {
        var history = new GatedHistoryDb();
        history.Force("<new@ex.com>", HistoryLookupResult.Unseen);
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string id = "<new@ex.com>";
        await duplex.WriteClientAsync(BuildTakeThis(id, CanonicalArticleText.Destuffed(id, ".stuffed\r\nplain\r\n").Replace(".stuffed\r\n", "..stuffed\r\n", StringComparison.Ordinal)));
        Assert.Equal("239 <new@ex.com>", await duplex.ReadClientLineAsync());

        var article = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal(id, article!.MessageId);
        Assert.Equal(InboundArticleProducer.TakeThis, article.Producer);
        Assert.Equal(ArticleParseStatus.CanonicalV1, article.Record.ParseStatus);
        Assert.True(article.Payload.Equals(article.Record.ArtData));
        Assert.Contains("\r\n\r\n..stuffed\r\nplain\r\n", Encoding.ASCII.GetString(article.Record.ArtData.Span), StringComparison.Ordinal);
        Assert.Contains("<new@ex.com>", history.Remembered);
        Assert.True(history.ContainsLocal(HistoryDigest.FromMessageId("<new@ex.com>"u8)));

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task HistoryUnavailable_Returns400AndCloses()
    {
        var history = new GatedHistoryDb();
        history.Force("<down@ex.com>", HistoryLookupResult.Unavailable);
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync(BuildTakeThis("<down@ex.com>", "Subject: x\r\n\r\ny\r\n"));
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task HistoryThrows_Returns400AndCloses()
    {
        var history = new GatedHistoryDb { ThrowOnPeek = true };
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync(BuildTakeThis("<boom@ex.com>", "Subject: x\r\n\r\ny\r\n"));
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task QueuedPayload_IsSingleOwnedWireBuffer_NotToArrayCopy()
    {
        var history = new GatedHistoryDb();
        history.Force("<own@ex.com>", HistoryLookupResult.Unseen);
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string id = "<own@ex.com>";
        await duplex.WriteClientAsync(BuildTakeThis(id, CanonicalArticleText.Destuffed(id, ".dot\r\n").Replace(".dot\r\n", "..dot\r\n", StringComparison.Ordinal)));
        Assert.Equal("239 <own@ex.com>", await duplex.ReadClientLineAsync());

        var article = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal(ArticleParseStatus.CanonicalV1, article!.Record.ParseStatus);
        Assert.True(article.Payload.Equals(article.Record.ArtData));
        Assert.True(MemoryMarshal.TryGetArray(article.Payload, out var segment));
        Assert.NotNull(segment.Array);
        Assert.Equal(article.Record.ArtSize, segment.Array.Length);
        Assert.Equal(article.Record.ArtSize, article.Payload.Length);
        Assert.Contains("\r\n\r\n..dot\r\n", Encoding.ASCII.GetString(article.Record.ArtData.Span), StringComparison.Ordinal);

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task Window_IsBounded_AtCheckDepth()
    {
        var history = new GatedHistoryDb();
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var extra = TakeThisPipeline.Depth + 1;
        var batch = new StringBuilder();
        for (var i = 0; i < extra; i++)
        {
            batch.Append(BuildTakeThis($"<w{i}@ex.com>", CanonicalArticleText.Destuffed($"<w{i}@ex.com>", "x\r\n")));
        }

        await duplex.WriteClientAsync(batch.ToString());
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => history.PeekStarted == TakeThisPipeline.Depth && session.TakeThisWindow!.Occupied == TakeThisPipeline.Depth,
            safety.Token);

        Assert.Equal(TakeThisPipeline.Depth, history.PeekStarted);
        Assert.Equal(TakeThisPipeline.Depth, session.TakeThisWindow!.Occupied);
        Assert.True(session.TakeThisWindow.PeakOccupied <= TakeThisPipeline.Depth);
        Assert.True(session.TakeThisWindow.InFlightCompletions <= TakeThisPipeline.Depth);
        Assert.Null(duplex.TryReadClientLine());

        history.Release.TrySetResult(HistoryLookupResult.Unseen);
        for (var i = 0; i < extra; i++)
        {
            Assert.Equal($"239 <w{i}@ex.com>", await duplex.ReadClientLineAsync());
        }

        Assert.Equal(extra, queue.Count);
        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task FasterSecondHistory_DoesNotReorderResponses()
    {
        var history = new GatedHistoryDb();
        history.Force("<b@ex.com>", HistoryLookupResult.Unseen);
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync(
            BuildTakeThis("<a@ex.com>", CanonicalArticleText.Destuffed("<a@ex.com>", "1\r\n")) +
            BuildTakeThis("<b@ex.com>", CanonicalArticleText.Destuffed("<b@ex.com>", "2\r\n")));

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => session.TakeThisWindow is { Occupied: 2 }, safety.Token);
        Assert.Null(duplex.TryReadClientLine());

        history.Release.TrySetResult(HistoryLookupResult.Unseen);
        Assert.Equal("239 <a@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal("239 <b@ex.com>", await duplex.ReadClientLineAsync());

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task ImmediateFlush_EmitsFirst239_WhileNextArticleStillOpen()
    {
        var history = new GatedHistoryDb();
        history.Force("<imm-a@ex.com>", HistoryLookupResult.Unseen);
        history.Force("<imm-b@ex.com>", HistoryLookupResult.Unseen);
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync(
            BuildTakeThis("<imm-a@ex.com>", CanonicalArticleText.Destuffed("<imm-a@ex.com>", "1\r\n")) +
            $"TAKETHIS <imm-b@ex.com>\r\n{CanonicalArticleText.Destuffed("<imm-b@ex.com>", "no-term\r\n")}");

        Assert.Equal("239 <imm-a@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Null(duplex.TryReadClientLine());
        Assert.Equal(1, queue.Count);

        await duplex.WriteClientAsync(".\r\n");
        Assert.Equal("239 <imm-b@ex.com>", await duplex.ReadClientLineAsync());

        await QuitAsync(duplex, run);
    }

    [Fact]
    public async Task CheckThenTakeThis_DrainsCheckBeforeTakeThisPeek()
    {
        var redis = new FakeRedisService();
        var checkId = "<chk@ex.com>"u8.ToArray();
        var block = BlockKey(redis, checkId);
        var history = new HistoryDb(
            redis,
            TimeSpan.FromHours(2),
            NullLogger<HistoryDb>.Instance,
            TimeProvider.System,
            new HistoryWriteQueue());
        var queue = NewQueue();
        await using var duplex = new TakeThisPipelineDuplex();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync(
            "CHECK <chk@ex.com>\r\n" + BuildTakeThis("<after-check@ex.com>", CanonicalArticleText.Destuffed("<after-check@ex.com>", "z\r\n")));

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => session.Pipeline is { Occupied: >= 1 }, safety.Token);
        Assert.True(session.TakeThisWindow is null || session.TakeThisWindow.Occupied == 0);
        Assert.Equal(1, redis.Database.ExistsStartedCount);
        Assert.Null(duplex.TryReadClientLine());
        Assert.False(history.ContainsLocal(HistoryDigest.FromMessageId(checkId)));

        block.TrySetResult();
        Assert.Equal("238 <chk@ex.com> send article to be transferred", await duplex.ReadClientLineAsync());
        Assert.False(history.ContainsLocal(HistoryDigest.FromMessageId(checkId)));
        Assert.Equal("239 <after-check@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal(1, queue.Count);
        Assert.True(history.ContainsLocal(HistoryDigest.FromMessageId("<after-check@ex.com>"u8)));

        await QuitAsync(duplex, run);
    }

    private static ArticleIngestionQueue NewQueue() =>
        new(new ArticleIngestionOptions { QueueCapacity = 32 });

    private static string BuildTakeThis(string messageId, string? articleWithoutTerminator = null) =>
        $"TAKETHIS {messageId}\r\n{(articleWithoutTerminator ?? CanonicalArticleText.Destuffed(messageId))}.\r\n";

    private static async Task QuitAsync(TakeThisPipelineDuplex duplex, Task run)
    {
        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private static TaskCompletionSource BlockKey(FakeRedisService redis, ReadOnlySpan<byte> messageId)
    {
        var key = HistoryRedisKeys.Create(HistoryDigest.FromMessageId(messageId));
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        redis.Database.BlockExistsByKey[Convert.ToHexString(key)] = block;
        return block;
    }

    private sealed class GatedHistoryDb : IHistoryDb
    {
        private readonly ConcurrentDictionary<string, HistoryLookupResult> _forced = new(StringComparer.Ordinal);
        private readonly HashSet<HistoryDigest> _local = [];
        private readonly object _gate = new();

        public TaskCompletionSource<HistoryLookupResult> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstPeek { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentBag<string> Remembered { get; } = [];

        public int PeekStarted;

        public bool ThrowOnPeek { get; set; }

        public void Force(string messageId, HistoryLookupResult result) =>
            _forced[messageId] = result;

        public ValueTask<HistoryLookupResult> LookupAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default) =>
            PeekAsync(messageId, cancellationToken);

        public async ValueTask<HistoryLookupResult> PeekAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref PeekStarted);
            FirstPeek.TrySetResult();
            if (ThrowOnPeek)
            {
                throw new InvalidOperationException("HistoryDB test failure.");
            }

            var id = Encoding.ASCII.GetString(messageId.Span);
            if (_forced.TryGetValue(id, out var forced))
            {
                return forced;
            }

            return await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Remember(ReadOnlyMemory<byte> messageId)
        {
            var text = Encoding.ASCII.GetString(messageId.Span);
            Remembered.Add(text);
            lock (_gate)
            {
                _local.Add(HistoryDigest.FromMessageId(messageId.Span));
            }
        }

        public bool ContainsLocal(in HistoryDigest digest)
        {
            lock (_gate)
            {
                return _local.Contains(digest);
            }
        }
    }

    private sealed class TakeThisPipelineDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public NntpSession CreateSession(IArticleIngestionQueue queue, IHistoryDb history)
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119)));
            return new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                articleIngestion: queue,
                historyDb: history);
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

    private sealed class PipeNntpConnection(PipeReader input, PipeWriter output, ConnectionClientIdentity identity)
        : INntpConnection
    {
        private readonly CancellationTokenSource _closed = new();

        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;

        public ConnectionClientIdentity ClientIdentity { get; } = identity;

        public EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;

        public EndPoint? LocalEndPoint => null;

        public bool IsTls => false;

        public bool IsCompressed => false;

        public bool IsCompleted => _closed.IsCancellationRequested;

        public long OutboundIdleVersion => 0;

        public CancellationToken ConnectionClosed => _closed.Token;

        public Task CompleteAsync(Exception? exception = null)
        {
            _closed.Cancel();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _closed.Dispose();
            return ValueTask.CompletedTask;
        }

        public Task WaitForOutboundDeliveryAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WaitForOutboundDeliveryAndPauseReadsAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task PauseReadsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpgradeToTlsAsync(
            ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipherSuite)
        {
            tlsVersion = string.Empty;
            cipherSuite = string.Empty;
            return false;
        }
    }
}

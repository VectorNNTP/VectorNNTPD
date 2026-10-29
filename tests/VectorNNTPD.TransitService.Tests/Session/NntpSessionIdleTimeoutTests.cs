using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.TransitService.ArticleIngestion;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.History;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.TransitService.Networking.Transport;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.Authentication;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.TransitService.Session.Framing;
using VectorNNTPD.TransitService.Tests.Fixtures;

namespace VectorNNTPD.TransitService.Tests.Session;

/// <summary>
/// NNTP command idle timeout: session-owned waiter, ControllableTimeProvider, no 300s sleeps.
/// </summary>
public sealed class NntpSessionIdleTimeoutTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(5);

    private static readonly NntpAuthorization TransitAuth = new(
        isAuthenticated: true,
        authorizedReader: false,
        authorizedTransit: true,
        postingPermitted: false,
        streamingPermitted: true);

    [Fact]
    public void DisconnectReason_IdleTimeout_FormatsForTcpLog()
    {
        Assert.Equal("IdleTimeout", TcpDisconnectReason.IdleTimeout.ToString());
    }

    [Fact]
    public void SessionConstructor_RejectsIdleTimeOutsideValidatedRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NntpSession(
                new StubConnection(),
                NullLogger<NntpSession>.Instance,
                commandIdleTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NntpSession(
                new StubConnection(),
                NullLogger<NntpSession>.Instance,
                commandIdleTimeout: TimeSpan.FromSeconds(NntpdOptions.MaxIdleTime + 1)));
    }

    [Fact]
    public async Task NewSession_StaysConnectedBeforeIdleTime_ThenDisconnects()
    {
        var clock = new ControllableTimeProvider();
        await using var duplex = new IdleDuplex();
        var session = duplex.CreateSession(clock);
        var run = session.RunAsync();

        Assert.StartsWith("201 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await session.IdleWatchArmed.WaitAsync(Safety);
        await Task.Yield();

        clock.Advance(Idle - TimeSpan.FromSeconds(1));
        await Task.Yield();
        Assert.False(run.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));
        await run.WaitAsync(Safety);
        Assert.Equal(TcpDisconnectReason.IdleTimeout, session.CloseReasonForTests);
        Assert.True(session.IdleWatchTaskForTests!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CommandActivity_ResetsIdleTimeout()
    {
        var clock = new ControllableTimeProvider();
        await using var duplex = new IdleDuplex();
        var session = duplex.CreateSession(clock);
        var run = session.RunAsync();

        _ = await duplex.ReadClientLineAsync();
        await session.IdleWatchArmed.WaitAsync(Safety);
        await Task.Yield();

        clock.Advance(Idle - TimeSpan.FromSeconds(1));
        await duplex.WriteClientLineAsync("DATE");
        var date = await duplex.ReadClientLineAsync();
        Assert.StartsWith("111 ", date, StringComparison.Ordinal);

        // Drain the greeting Delay so the waiter re-samples. T+Idle would have
        // disconnected without the DATE reset.
        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Yield();
        Assert.False(run.IsCompleted);
        Assert.Equal(TcpDisconnectReason.Unspecified, session.CloseReasonForTests);

        clock.Advance(Idle - TimeSpan.FromSeconds(2));
        await Task.Yield();
        Assert.False(run.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(2));
        await run.WaitAsync(Safety);
        Assert.Equal(TcpDisconnectReason.IdleTimeout, session.CloseReasonForTests);
    }

    [Fact]
    public async Task MultipleCommands_TimeoutMeasuredFromMostRecent()
    {
        var clock = new ControllableTimeProvider();
        await using var duplex = new IdleDuplex();
        var session = duplex.CreateSession(clock);
        var run = session.RunAsync();

        _ = await duplex.ReadClientLineAsync();
        await session.IdleWatchArmed.WaitAsync(Safety);
        await Task.Yield();

        clock.Advance(Idle - TimeSpan.FromSeconds(1));
        await duplex.WriteClientLineAsync("DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Yield();
        Assert.False(run.IsCompleted);

        clock.Advance(Idle - TimeSpan.FromSeconds(2));
        await duplex.WriteClientLineAsync("DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Yield();
        Assert.False(run.IsCompleted);

        clock.Advance(Idle - TimeSpan.FromSeconds(2));
        await duplex.WriteClientLineAsync("DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Yield();
        Assert.False(run.IsCompleted);

        clock.Advance(Idle);
        await run.WaitAsync(Safety);
        Assert.Equal(TcpDisconnectReason.IdleTimeout, session.CloseReasonForTests);
    }

    [Fact]
    public async Task PartialCommandLine_DoesNotResetIdleTimeout()
    {
        var clock = new ControllableTimeProvider();
        await using var duplex = new IdleDuplex();
        var session = duplex.CreateSession(clock);
        var run = session.RunAsync();

        _ = await duplex.ReadClientLineAsync();
        await session.IdleWatchArmed.WaitAsync(Safety);
        await Task.Yield();

        await duplex.WriteClientAsync("DAT");
        clock.Advance(Idle);
        await run.WaitAsync(Safety);
        Assert.Equal(TcpDisconnectReason.IdleTimeout, session.CloseReasonForTests);
    }

    [Fact]
    public async Task ActiveAuthinfoCommand_LongerThanIdleTime_IsNotKilled()
    {
        var clock = new ControllableTimeProvider();
        var hold = new TaskCompletionSource<NntpAuthenticationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new GatedAuthProvider(hold, entered);

        await using var duplex = new IdleDuplex();
        var session = duplex.CreateSession(clock, authenticationProvider: provider);
        var run = session.RunAsync();

        _ = await duplex.ReadClientLineAsync();
        await session.IdleWatchArmed.WaitAsync(Safety);
        await duplex.WriteClientLineAsync("AUTHINFO USER fred");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("AUTHINFO PASS secret");
        await entered.Task.WaitAsync(Safety);
        Assert.True(session.CommandWorkForTests > 0);

        clock.Advance(Idle);
        await Task.Delay(20);
        Assert.False(run.IsCompleted);
        Assert.Equal(TcpDisconnectReason.Unspecified, session.CloseReasonForTests);

        hold.TrySetResult(NntpAuthenticationResult.Failed);
        await WaitUntilAsync(() => session.CommandWorkForTests == 0, Safety);
        var afterPass = await duplex.ReadClientLineAsync();
        Assert.StartsWith("481 ", afterPass, StringComparison.Ordinal);
        Assert.False(run.IsCompleted);

        clock.Advance(Idle);
        await run.WaitAsync(Safety);
        Assert.Equal(TcpDisconnectReason.IdleTimeout, session.CloseReasonForTests);
    }

    [Fact]
    public async Task ActiveCheckLookup_LongerThanIdleTime_IsNotKilled()
    {
        var clock = new ControllableTimeProvider();
        var history = new GatedHistoryDb();
        await using var duplex = new IdleDuplex();
        var session = duplex.CreateSession(clock, historyDb: history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();

        _ = await duplex.ReadClientLineAsync();
        await session.IdleWatchArmed.WaitAsync(Safety);
        await duplex.WriteClientLineAsync("CHECK <idle-check@example.com>");
        await history.Started.Task.WaitAsync(Safety);
        await WaitUntilAsync(() => session.Pipeline is { Occupied: 1 }, Safety);
        Assert.True(session.CommandWorkForTests > 0);

        clock.Advance(Idle);
        await Task.Yield();
        Assert.False(run.IsCompleted);
        Assert.Equal(TcpDisconnectReason.Unspecified, session.CloseReasonForTests);
        Assert.Equal(1, session.Pipeline!.Occupied);

        history.Release.TrySetResult(HistoryLookupResult.Unseen);
        Assert.StartsWith("238 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        clock.Advance(Idle);
        await run.WaitAsync(Safety);
        Assert.Equal(TcpDisconnectReason.IdleTimeout, session.CloseReasonForTests);
    }

    [Fact]
    public async Task ActiveTakeThisReceive_LongerThanIdleTime_IsNotKilled()
    {
        var clock = new ControllableTimeProvider();
        var history = new GatedHistoryDb();
        history.Release.TrySetResult(HistoryLookupResult.Unseen);
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 8 });
        await using var duplex = new IdleDuplex();
        var session = duplex.CreateSession(clock, historyDb: history, articleIngestion: queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();

        _ = await duplex.ReadClientLineAsync();
        await session.IdleWatchArmed.WaitAsync(Safety);

        var receiveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TakeThisWindow!.HoldReceive = hold.Task;
        session.TakeThisWindow.AfterReceiveStarted = () => receiveStarted.TrySetResult();

        await duplex.WriteClientAsync(
            "TAKETHIS <idle-take@example.com>\r\n" +
            CanonicalArticleText.Destuffed("<idle-take@example.com>") +
            ".\r\n");
        await receiveStarted.Task.WaitAsync(Safety);
        Assert.True(session.CommandWorkForTests > 0);

        clock.Advance(Idle);
        await Task.Delay(20);
        Assert.False(run.IsCompleted);
        Assert.Equal(TcpDisconnectReason.Unspecified, session.CloseReasonForTests);
        Assert.Equal(1, session.TakeThisWindow.Occupied);

        hold.TrySetResult();
        await WaitUntilAsync(() => session.TakeThisWindow.Occupied == 0, Safety);
        Assert.StartsWith("239 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        clock.Advance(Idle);
        await run.WaitAsync(Safety);
        Assert.Equal(TcpDisconnectReason.IdleTimeout, session.CloseReasonForTests);
    }

    [Fact]
    public async Task Quit_CancelsIdleWatchWithoutLeak()
    {
        var clock = new ControllableTimeProvider();
        await using var duplex = new IdleDuplex();
        var session = duplex.CreateSession(clock);
        var run = session.RunAsync();

        _ = await duplex.ReadClientLineAsync();
        await session.IdleWatchArmed.WaitAsync(Safety);
        await duplex.WriteClientLineAsync("QUIT");
        Assert.StartsWith("205 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await run.WaitAsync(Safety);

        Assert.Equal(TcpDisconnectReason.ProtocolClose, session.CloseReasonForTests);
        Assert.True(session.IdleWatchTaskForTests!.IsCompleted);
        Assert.False(session.IdleWatchTaskForTests.IsFaulted);
        Assert.False(session.IdleWatchTaskForTests.IsCanceled);
    }

    [Fact]
    public async Task CommandWorkConcurrentWithExpiry_DoesNotDisconnectAfterReset()
    {
        var clock = new ControllableTimeProvider();
        await using var duplex = new IdleDuplex();
        var session = duplex.CreateSession(clock);
        var aboutTo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.IdleAboutToCommit = aboutTo;
        session.IdleCommitHold = hold.Task;
        var run = session.RunAsync();

        _ = await duplex.ReadClientLineAsync();
        await session.IdleWatchArmed.WaitAsync(Safety);
        await Task.Yield();

        clock.Advance(Idle);
        await aboutTo.Task.WaitAsync(Safety);
        session.BeginCommandWork();
        Assert.False(session.IdleCloseCommittedForTests);
        hold.TrySetResult();
        await Task.Yield();
        Assert.False(run.IsCompleted);
        Assert.Equal(TcpDisconnectReason.Unspecified, session.CloseReasonForTests);
        Assert.False(session.IdleCloseCommittedForTests);

        session.EndCommandWork();
        await WaitUntilAsync(() => clock.HasScheduledTimers, Safety);
        clock.Advance(Idle);
        await run.WaitAsync(Safety);
        Assert.Equal(TcpDisconnectReason.IdleTimeout, session.CloseReasonForTests);
    }

    [Fact]
    public async Task BeginAndEndDuringIdleCommit_StaleIdleDecisionDoesNotClose()
    {
        var clock = new ControllableTimeProvider();
        await using var duplex = new IdleDuplex();
        var session = duplex.CreateSession(clock);
        var aboutTo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.IdleAboutToCommit = aboutTo;
        session.IdleCommitHold = hold.Task;
        var run = session.RunAsync();

        _ = await duplex.ReadClientLineAsync();
        await session.IdleWatchArmed.WaitAsync(Safety);
        await Task.Yield();

        clock.Advance(Idle);
        await aboutTo.Task.WaitAsync(Safety);
        session.BeginCommandWork();
        session.EndCommandWork();
        Assert.Equal(0, session.CommandWorkForTests);
        Assert.False(session.IdleCloseCommittedForTests);
        hold.TrySetResult();
        await Task.Yield();
        Assert.False(run.IsCompleted);
        Assert.Equal(TcpDisconnectReason.Unspecified, session.CloseReasonForTests);
        Assert.False(session.IdleCloseCommittedForTests);

        await WaitUntilAsync(() => clock.HasScheduledTimers, Safety);
        clock.Advance(Idle);
        await run.WaitAsync(Safety);
        Assert.Equal(TcpDisconnectReason.IdleTimeout, session.CloseReasonForTests);
    }

    [Fact]
    public async Task IdleCommitThenBegin_CommandObservesClosingSession()
    {
        var clock = new ControllableTimeProvider();
        await using var duplex = new IdleDuplex();
        var session = duplex.CreateSession(clock);
        var aboutTo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.IdleAboutToCommit = aboutTo;
        session.IdleCommitHold = hold.Task;
        var run = session.RunAsync();

        _ = await duplex.ReadClientLineAsync();
        await session.IdleWatchArmed.WaitAsync(Safety);
        await Task.Yield();

        clock.Advance(Idle);
        await aboutTo.Task.WaitAsync(Safety);
        hold.TrySetResult();
        await WaitUntilAsync(() => session.IdleCloseCommittedForTests, Safety);

        session.BeginCommandWork();
        Assert.True(session.IdleCloseCommittedForTests);
        session.EndCommandWork();
        await run.WaitAsync(Safety);
        Assert.Equal(TcpDisconnectReason.IdleTimeout, session.CloseReasonForTests);
        Assert.Equal(0, session.CommandWorkForTests);
    }

    [Fact]
    public async Task SuccessfulCommand_ReleasesCommandWork()
    {
        var clock = new ControllableTimeProvider();
        await using var duplex = new IdleDuplex();
        var session = duplex.CreateSession(clock);
        var run = session.RunAsync();

        _ = await duplex.ReadClientLineAsync();
        await session.IdleWatchArmed.WaitAsync(Safety);
        await duplex.WriteClientLineAsync("DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await WaitUntilAsync(() => session.CommandWorkForTests == 0, Safety);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run.WaitAsync(Safety);
    }

    [Fact]
    public async Task HandlerException_ReleasesCommandWork()
    {
        var clock = new ControllableTimeProvider();
        await using var duplex = new IdleDuplex();
        var session = duplex.CreateSession(clock, authenticationProvider: new ThrowingAuthProvider());
        var run = session.RunAsync();

        _ = await duplex.ReadClientLineAsync();
        await session.IdleWatchArmed.WaitAsync(Safety);
        await duplex.WriteClientLineAsync("AUTHINFO USER fred");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS secret");
        _ = await duplex.ReadClientLineAsync();
        await WaitUntilAsync(() => session.CommandWorkForTests == 0, Safety);
        Assert.False(run.IsCompleted);

        await duplex.WriteClientLineAsync("DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run.WaitAsync(Safety);
    }

    [Fact]
    public async Task CommandCancellation_ReleasesCommandWork()
    {
        var clock = new ControllableTimeProvider();
        var hold = new TaskCompletionSource<NntpAuthenticationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var duplex = new IdleDuplex();
        var session = duplex.CreateSession(clock, authenticationProvider: new GatedAuthProvider(hold, entered));
        var run = session.RunAsync();

        _ = await duplex.ReadClientLineAsync();
        await session.IdleWatchArmed.WaitAsync(Safety);
        await duplex.WriteClientLineAsync("AUTHINFO USER fred");
        Assert.StartsWith("381 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("AUTHINFO PASS secret");
        await entered.Task.WaitAsync(Safety);
        Assert.True(session.CommandWorkForTests > 0);

        session.RequestClose();
        await run.WaitAsync(Safety);
        Assert.Equal(0, session.CommandWorkForTests);
        Assert.Equal(TcpDisconnectReason.ProtocolClose, session.CloseReasonForTests);
    }

    [Fact]
    public async Task CheckEmitException_ReleasesCommandWork()
    {
        var clock = new ControllableTimeProvider();
        var history = new GatedHistoryDb();
        await using var duplex = new IdleDuplex();
        var session = duplex.CreateSession(clock, historyDb: history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();

        _ = await duplex.ReadClientLineAsync();
        await session.IdleWatchArmed.WaitAsync(Safety);
        await duplex.WriteClientLineAsync("CHECK <emit-boom@example.com>");
        await history.Started.Task.WaitAsync(Safety);
        await WaitUntilAsync(() => session.Pipeline is { Occupied: 1 }, Safety);

        session.Pipeline!.BeforeEnqueueProbe = static () =>
            throw new InvalidOperationException("emit-boom");
        history.Release.TrySetResult(HistoryLookupResult.Unseen);
        await WaitUntilAsync(
            () => session.CommandWorkForTests == 0 && session.Pipeline!.Occupied == 0,
            Safety);
        Assert.False(run.IsCompleted);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run.WaitAsync(Safety);
    }

    [Fact]
    public async Task CheckImmediateEmitException_ReleasesCommandWork()
    {
        var clock = new ControllableTimeProvider();
        var history = new GatedHistoryDb();
        history.Release.TrySetResult(HistoryLookupResult.Unseen);
        await using var duplex = new IdleDuplex();
        var session = duplex.CreateSession(clock, historyDb: history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();

        _ = await duplex.ReadClientLineAsync();
        await session.IdleWatchArmed.WaitAsync(Safety);
        session.Pipeline!.BeforeEnqueueProbe = static () =>
            throw new InvalidOperationException("emit-boom");
        await duplex.WriteClientLineAsync("CHECK <immediate-emit-boom@example.com>");
        await run.WaitAsync(Safety);
        Assert.Equal(0, session.CommandWorkForTests);
        Assert.Equal(0, session.Pipeline.Occupied);
    }

    [Fact]
    public async Task PipelineShutdown_ReleasesCommandWork()
    {
        var clock = new ControllableTimeProvider();
        var history = new GatedHistoryDb();
        await using var duplex = new IdleDuplex();
        var session = duplex.CreateSession(clock, historyDb: history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();

        _ = await duplex.ReadClientLineAsync();
        await session.IdleWatchArmed.WaitAsync(Safety);
        await duplex.WriteClientLineAsync("CHECK <shutdown-work@example.com>");
        await history.Started.Task.WaitAsync(Safety);
        await WaitUntilAsync(() => session.Pipeline is { Occupied: 1 }, Safety);
        Assert.True(session.CommandWorkForTests > 0);

        session.RequestClose();
        await run.WaitAsync(Safety);
        Assert.Equal(0, session.CommandWorkForTests);
        history.Release.TrySetResult(HistoryLookupResult.Unseen);
    }

    [Fact]
    public async Task TakeThisEmitException_ReleasesCommandWork()
    {
        var clock = new ControllableTimeProvider();
        var history = new GatedHistoryDb();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 8 });
        var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var duplex = new IdleDuplex();
        var session = duplex.CreateSession(clock, historyDb: history, articleIngestion: queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();

        _ = await duplex.ReadClientLineAsync();
        await session.IdleWatchArmed.WaitAsync(Safety);
        session.TakeThisWindow!.AfterArticleDetached = _ => detached.TrySetResult();
        await duplex.WriteClientAsync("TAKETHIS <emit-boom@example.com>\r\nSubject: x\r\n\r\nbody\r\n.\r\n");
        await detached.Task.WaitAsync(Safety);

        session.TakeThisWindow.BeforeEnqueueProbe = static () =>
            throw new InvalidOperationException("emit-boom");
        history.Release.TrySetResult(HistoryLookupResult.Unseen);
        await WaitUntilAsync(
            () => session.CommandWorkForTests == 0 && session.TakeThisWindow.Occupied == 0,
            Safety);
        Assert.False(run.IsCompleted);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run.WaitAsync(Safety);
    }

    [Fact]
    public async Task TakeThisPipelineShutdown_ReleasesCommandWork()
    {
        var clock = new ControllableTimeProvider();
        var history = new GatedHistoryDb();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 8 });
        var receiveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var duplex = new IdleDuplex();
        var session = duplex.CreateSession(clock, historyDb: history, articleIngestion: queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();

        _ = await duplex.ReadClientLineAsync();
        await session.IdleWatchArmed.WaitAsync(Safety);
        session.TakeThisWindow!.HoldReceive = hold.Task;
        session.TakeThisWindow.AfterReceiveStarted = () => receiveStarted.TrySetResult();
        await duplex.WriteClientAsync("TAKETHIS <shutdown-work@example.com>\r\nSubject: x\r\n\r\nbody\r\n.\r\n");
        await receiveStarted.Task.WaitAsync(Safety);
        Assert.True(session.CommandWorkForTests > 0);

        session.RequestClose();
        await run.WaitAsync(Safety);
        Assert.Equal(0, session.CommandWorkForTests);
        hold.TrySetResult();
        history.Release.TrySetResult(HistoryLookupResult.Unseen);
    }

    [Fact]
    public async Task ConfiguredIdleTime_IsHonouredBySession()
    {
        var clock = new ControllableTimeProvider();
        var idle = TimeSpan.FromSeconds(2);
        await using var duplex = new IdleDuplex();
        var session = duplex.CreateSession(clock, idle);
        var run = session.RunAsync();

        _ = await duplex.ReadClientLineAsync();
        await session.IdleWatchArmed.WaitAsync(Safety);
        await Task.Yield();

        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Yield();
        Assert.False(run.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));
        await run.WaitAsync(Safety);
        Assert.Equal(TcpDisconnectReason.IdleTimeout, session.CloseReasonForTests);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private sealed class IdleDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public NntpSession CreateSession(
            ControllableTimeProvider clock,
            TimeSpan? idle = null,
            IHistoryDb? historyDb = null,
            IArticleIngestionQueue? articleIngestion = null,
            INntpAuthenticationProvider? authenticationProvider = null)
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119)));
            return new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                authenticationProvider: authenticationProvider,
                articleIngestion: articleIngestion,
                historyDb: historyDb,
                commandIdleTimeout: idle ?? Idle,
                timeProvider: clock);
        }

        public async Task WriteClientLineAsync(string line) =>
            await WriteClientAsync(line + "\r\n");

        public async Task WriteClientAsync(string payload)
        {
            var bytes = Encoding.ASCII.GetBytes(payload);
            await _clientToServer.Writer.WriteAsync(bytes);
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task<string> ReadClientLineAsync()
        {
            using var cts = new CancellationTokenSource(Safety);
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
            Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubConnection : INntpConnection
    {
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public EndPoint? RemoteEndPoint => null;
        public EndPoint? LocalEndPoint => null;
        public ConnectionClientIdentity ClientIdentity { get; } =
            ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119));
        public bool IsTls => false;
        public bool IsCompressed => false;
        public CancellationToken ConnectionClosed => CancellationToken.None;
        public bool IsCompleted => false;
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

        public Task CompleteAsync(Exception? exception = null) => Task.CompletedTask;

        public Task UpgradeToTlsAsync(
            ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GatedAuthProvider(
        TaskCompletionSource<NntpAuthenticationResult> hold,
        TaskCompletionSource entered) : INntpAuthenticationProvider
    {
        public async ValueTask<NntpAuthenticationResult> AuthenticateAsync(
            string username,
            string password,
            CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            return await hold.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ThrowingAuthProvider : INntpAuthenticationProvider
    {
        public ValueTask<NntpAuthenticationResult> AuthenticateAsync(
            string username,
            string password,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("handler-boom");
    }

    private sealed class GatedHistoryDb : IHistoryDb
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<HistoryLookupResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<HistoryLookupResult> LookupAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default) =>
            WaitAsync(cancellationToken);

        public ValueTask<HistoryLookupResult> PeekAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default) =>
            WaitAsync(cancellationToken);

        public void Remember(ReadOnlyMemory<byte> messageId)
        {
        }

        public bool ContainsLocal(in HistoryDigest digest) => false;

        private async ValueTask<HistoryLookupResult> WaitAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return await Release.Task.WaitAsync(cancellationToken);
        }
    }
}

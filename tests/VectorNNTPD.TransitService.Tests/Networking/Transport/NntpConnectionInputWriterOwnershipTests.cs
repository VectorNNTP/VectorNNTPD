using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.TransitService.Networking.Transport;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Tests.Fixtures;

namespace VectorNNTPD.TransitService.Tests.Networking.Transport;

/// <summary>
/// Deterministic <c>Input.Writer</c> ownership: <c>ReceiveAsync</c> GetMemory/Advance/completes;
/// <c>CompleteAsync</c> must not complete the writer while a receive reservation is outstanding.
/// </summary>
[Collection(nameof(TransportTestHostCollection))]
public sealed class NntpConnectionInputWriterOwnershipTests
{
    [Fact]
    public async Task CompleteAsync_AfterReadReturns_DoesNotInvalidateAdvance()
    {
        var logger = new CapturingConnectionLogger();
        await using var host = await TransportTestHost.StartPlainAsync(logger);
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = server.ByteTransportForTests;
        Assert.NotNull(transport);
        transport.AfterStreamReadProbe = async () =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };

        await client.SendAsync(new byte[] { 0x41 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await entered.Task.WaitAsync(timeout.Token);

        await server.CompleteInputReaderAsync();
        var complete = server.CompleteAsync();
        await WaitUntilAsync(static s => s.ConnectionClosed.IsCancellationRequested, server, timeout.Token);

        Assert.True(server.ConnectionClosed.IsCancellationRequested);
        Assert.False(server.InputWriterCompletedForTests);
        Assert.False(complete.IsCompleted);
        Assert.False(server.ReceivePumpTaskForTests!.IsCompleted);

        release.TrySetResult();
        await complete.WaitAsync(timeout.Token);

        Assert.True(server.InputWriterCompletedForTests);
        AssertNoAdvanceRangeFault(server.ReceivePumpTaskForTests);
        Assert.Empty(logger.PumpErrors);
        Assert.Equal(TcpDisconnectReason.LocalClose, server.DisconnectReasonForTests);
    }

    [Fact]
    public async Task IdleTimeout_WhileReadOutstanding_DoesNotLogReceivePumpError()
    {
        var logger = new CapturingConnectionLogger();
        var clock = new ControllableTimeProvider();
        await using var host = await TransportTestHost.StartPlainAsync(logger);
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = server.ByteTransportForTests;
        Assert.NotNull(transport);

        var session = new NntpSession(
            server,
            NullLogger<NntpSession>.Instance,
            commandIdleTimeout: TimeSpan.FromSeconds(2),
            timeProvider: clock);
        var run = session.RunAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.IdleWatchArmed.WaitAsync(timeout.Token);
        transport.AfterStreamReadProbe = async () =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };

        await client.SendAsync(new byte[] { (byte)'D' });
        await entered.Task.WaitAsync(timeout.Token);

        clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(static s => s.ConnectionClosed.IsCancellationRequested, server, timeout.Token);
        Assert.False(server.InputWriterCompletedForTests);

        release.TrySetResult();
        await run.WaitAsync(timeout.Token);

        Assert.Equal(TcpDisconnectReason.IdleTimeout, session.CloseReasonForTests);
        Assert.Equal(TcpDisconnectReason.IdleTimeout, server.DisconnectReasonForTests);
        Assert.True(server.InputWriterCompletedForTests);
        AssertNoAdvanceRangeFault(server.ReceivePumpTaskForTests);
        Assert.Empty(logger.PumpErrors);
    }

    [Fact]
    public async Task Quit_WhileNextReadOutstanding_DoesNotLogReceivePumpError()
    {
        var logger = new CapturingConnectionLogger();
        await using var host = await TransportTestHost.StartPlainAsync(logger);
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());

        var session = new NntpSession(server, NullLogger<NntpSession>.Instance);
        var run = session.RunAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.IdleWatchArmed.WaitAsync(timeout.Token);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = server.ByteTransportForTests;
        Assert.NotNull(transport);
        transport.BeforeStreamReadProbe = async () =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };

        await client.SendAsync("QUIT\r\n"u8.ToArray());
        await entered.Task.WaitAsync(timeout.Token);
        await WaitUntilAsync(static s => s.ConnectionClosed.IsCancellationRequested, server, timeout.Token);
        Assert.False(server.InputWriterCompletedForTests);

        release.TrySetResult();
        await run.WaitAsync(timeout.Token);

        Assert.Equal(TcpDisconnectReason.ProtocolClose, session.CloseReasonForTests);
        Assert.True(server.InputWriterCompletedForTests);
        AssertNoAdvanceRangeFault(server.ReceivePumpTaskForTests);
        Assert.Empty(logger.PumpErrors);
    }

    [Fact]
    public async Task RemoteEof_CompletesWriterOnce_AndKeepsRemoteClosed()
    {
        var logger = new CapturingConnectionLogger();
        await using var host = await TransportTestHost.StartPlainAsync(logger);
        var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());

        var inbound = server.Input.ReadAsync().AsTask();
        client.Shutdown(SocketShutdown.Send);
        client.Dispose();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await inbound.WaitAsync(timeout.Token);
        Assert.True(result.IsCompleted);
        server.Input.AdvanceTo(result.Buffer.End);

        await WaitUntilAsync(static s => s.InputWriterCompletedForTests, server, timeout.Token);
        Assert.Equal(TcpDisconnectReason.RemoteClosed, server.DisconnectReasonForTests);
        Assert.True(server.InputWriterCompletedForTests);
        Assert.False(server.InputReaderCompletedForTests);
        Assert.Empty(logger.PumpErrors);
    }

    [Fact]
    public async Task ReceiveException_StillCompletesWriter()
    {
        var logger = new CapturingConnectionLogger();
        await using var host = await TransportTestHost.StartPlainAsync(logger);
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());

        var transport = server.ByteTransportForTests;
        Assert.NotNull(transport);
        transport.BeforeStreamReadProbe = static () =>
            throw new IOException("forced-receive-failure");

        await client.SendAsync(new byte[] { 0x01 });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            static s => s.InputWriterCompletedForTests && s.IsCompleted,
            server,
            timeout.Token);
        await server.CompleteAsync().WaitAsync(timeout.Token);

        Assert.True(server.InputWriterCompletedForTests);
        Assert.Equal(TcpDisconnectReason.ReceiveError, server.DisconnectReasonForTests);
        Assert.Contains(
            logger.PumpErrors,
            static e => e.Contains("pump ended with an error", StringComparison.Ordinal));
        var receive = server.ReceivePumpTaskForTests;
        Assert.NotNull(receive);
        Assert.True(receive.IsFaulted);
        Assert.Contains(
            receive.Exception!.Flatten().InnerExceptions,
            static ex => ex is IOException);
    }

    private static void AssertNoAdvanceRangeFault(Task? receive)
    {
        Assert.NotNull(receive);
        if (!receive.IsFaulted)
        {
            return;
        }

        foreach (var ex in receive.Exception!.Flatten().InnerExceptions)
        {
            Assert.False(
                ex is ArgumentOutOfRangeException aor
                && string.Equals(aor.ParamName, "bytes", StringComparison.Ordinal),
                "Receive pump must not Advance after Input.Writer was completed.");
        }
    }

    private static async Task WaitUntilAsync(
        Func<NntpConnection, bool> predicate,
        NntpConnection server,
        CancellationToken cancellationToken)
    {
        while (!predicate(server))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private sealed class CapturingConnectionLogger : ILogger<NntpConnection>
    {
        public List<string> PumpErrors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var formatted = formatter(state, exception);
            if (formatted.Contains("pump ended with an error", StringComparison.Ordinal))
            {
                PumpErrors.Add(formatted);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}

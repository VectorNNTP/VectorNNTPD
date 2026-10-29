using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.ReaderService.Networking.Listeners;
using VectorNNTPD.ReaderService.Networking.Transport;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Tests.Fixtures;

namespace VectorNNTPD.ReaderService.Tests.Networking.Transport;

/// <summary>
/// Exactly one Information TCP disconnect summary per established connection, with a
/// known-cause reason and no exception text on that line.
/// </summary>
[Collection(nameof(TransportTestHostCollection))]
public sealed class TcpConnectionDisconnectLoggingTests
{
    [Fact]
    public async Task RemoteClose_EmitsExactlyOneDisconnect_WithRemoteClosed()
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

        // IsCompleted flips before the summary is logged and a second CompleteAsync returns immediately,
        // so wait on the observable (the single disconnect line) rather than on the completion flag.
        await WaitUntilAsync(
            () => server.IsCompleted && logger.Disconnects.Count > 0,
            timeout.Token);
        await server.CompleteAsync().WaitAsync(timeout.Token);

        var line = Assert.Single(logger.Disconnects);
        AssertSingleLineDisconnect(line, server, "RemoteClosed");
        Assert.Equal(TcpDisconnectReason.RemoteClosed, server.DisconnectReasonForTests);
        Assert.Empty(logger.DisconnectExceptions);
    }

    [Fact]
    public async Task LocalCompleteAsync_EmitsExactlyOneDisconnect_WithLocalClose()
    {
        var logger = new CapturingConnectionLogger();
        await using var host = await TransportTestHost.StartPlainAsync(logger);
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await server.CompleteAsync().WaitAsync(timeout.Token);
        await server.CompleteAsync().WaitAsync(timeout.Token);

        var line = Assert.Single(logger.Disconnects);
        AssertSingleLineDisconnect(line, server, "LocalClose");
        Assert.Equal(TcpDisconnectReason.LocalClose, server.DisconnectReasonForTests);
        Assert.Empty(logger.DisconnectExceptions);
    }

    [Fact]
    public async Task ReceivePumpError_EmitsExactlyOneDisconnect_WithReceiveError_AndKeepsDebugException()
    {
        var logger = new CapturingConnectionLogger();
        await using var host = await TransportTestHost.StartPlainAsync(logger);
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());

        var transport = server.ByteTransportForTests;
        Assert.NotNull(transport);
        transport.BeforeStreamReadProbe = static () =>
            throw new IOException("forced-receive-failure");

        // The in-flight receive already passed the probe; one octet starts the next read.
        await client.SendAsync(new byte[] { 0x01 });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => server.IsCompleted && logger.Disconnects.Count == 1 && logger.PumpErrors.Count > 0,
            timeout.Token);
        await server.CompleteAsync().WaitAsync(timeout.Token);

        var line = Assert.Single(logger.Disconnects);
        AssertSingleLineDisconnect(line, server, "ReceiveError");
        Assert.Equal(TcpDisconnectReason.ReceiveError, server.DisconnectReasonForTests);
        Assert.DoesNotContain("forced-receive-failure", line, StringComparison.Ordinal);
        Assert.Contains(
            logger.PumpErrors,
            static e => e.Contains("pump ended with an error", StringComparison.Ordinal));
        Assert.Contains(
            logger.PumpExceptions,
            static e => e.Contains("forced-receive-failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendPumpError_EmitsExactlyOneDisconnect_WithSendError_AndKeepsDebugException()
    {
        var logger = new CapturingConnectionLogger();
        await using var host = await TransportTestHost.StartPlainAsync(logger);
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());

        var transport = server.ByteTransportForTests;
        Assert.NotNull(transport);
        transport.BeforeStreamWriteProbe = static () =>
            throw new IOException("forced-send-failure");

        // The send pump can fail and complete the output pipe before WriteAsync/FlushAsync observe it
        // (timing depends on JIT warm-up); the pipe then surfaces the pump's exception to the producer.
        try
        {
            await server.Output.WriteAsync(new byte[] { 0x01 });
            await server.Output.FlushAsync();
        }
        catch (IOException ex) when (ex.Message == "forced-send-failure")
        {
            _ = ex.GetType(); // observed: expected during test teardown/cleanup
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => server.IsCompleted, timeout.Token);
        await server.CompleteAsync().WaitAsync(timeout.Token);

        var line = Assert.Single(logger.Disconnects);
        AssertSingleLineDisconnect(line, server, "SendError");
        Assert.Equal(TcpDisconnectReason.SendError, server.DisconnectReasonForTests);
        Assert.DoesNotContain("forced-send-failure", line, StringComparison.Ordinal);
        Assert.Contains(
            logger.PumpErrors,
            static e => e.Contains("pump ended with an error", StringComparison.Ordinal));
        Assert.Contains(
            logger.PumpExceptions,
            static e => e.Contains("forced-send-failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RequestCloseThenComplete_EmitsExactlyOneDisconnect_WithProtocolClose()
    {
        var logger = new CapturingConnectionLogger();
        await using var host = await TransportTestHost.StartPlainAsync(logger);
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());
        var session = new NntpSession(server, NullLogger<NntpSession>.Instance);

        session.RequestClose();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await server.CompleteAsync().WaitAsync(timeout.Token);
        await server.CompleteAsync().WaitAsync(timeout.Token);

        var line = Assert.Single(logger.Disconnects);
        AssertSingleLineDisconnect(line, server, "ProtocolClose");
        Assert.Equal(TcpDisconnectReason.ProtocolClose, server.DisconnectReasonForTests);
    }

    [Fact]
    public async Task ConcurrentCompleteAndDispose_EmitsExactlyOneDisconnect()
    {
        var logger = new CapturingConnectionLogger();
        await using var host = await TransportTestHost.StartPlainAsync(logger);
        using var client = await host.ConnectPlainClientAsync();
        var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await server.CompleteAsync().WaitAsync(timeout.Token);
        await server.CompleteAsync().WaitAsync(timeout.Token);
        await server.DisposeAsync().AsTask().WaitAsync(timeout.Token);

        Assert.Single(logger.Disconnects);
        Assert.Equal(TcpDisconnectReason.LocalClose, server.DisconnectReasonForTests);
    }

    [Fact]
    public async Task IdleTimeout_EmitsDisconnect_WithIdleTimeoutReason()
    {
        var logger = new CapturingConnectionLogger();
        var clock = new ControllableTimeProvider();
        await using var host = await TransportTestHost.StartPlainAsync(logger);
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());
        var session = new NntpSession(
            server,
            NullLogger<NntpSession>.Instance,
            commandIdleTimeout: TimeSpan.FromSeconds(2),
            timeProvider: clock);
        var run = session.RunAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.IdleWatchArmed.WaitAsync(timeout.Token);
        clock.Advance(TimeSpan.FromSeconds(2));
        await run.WaitAsync(timeout.Token);

        Assert.Equal(TcpDisconnectReason.IdleTimeout, session.CloseReasonForTests);
        Assert.Equal(TcpDisconnectReason.IdleTimeout, server.DisconnectReasonForTests);
        var line = Assert.Single(logger.Disconnects);
        AssertSingleLineDisconnect(line, server, "IdleTimeout");
        Assert.Empty(logger.DisconnectExceptions);
        Assert.Null(logger.LastException);
        Assert.Equal(LogLevel.Information, logger.LastLevel);
    }

    [Fact]
    public void GeneratedDisconnectMessage_IsSingleLine_WithStructuredReason()
    {
        var logger = new CapturingConnectionLogger();
        NetworkingLogMessages.TcpConnectionDisconnected(
            logger,
            "69.80.99.16:49136",
            "0.0.0.0:119",
            "RemoteClosed");

        var line = Assert.Single(logger.Disconnects);
        Assert.Equal(LogLevel.Information, logger.LastLevel);
        Assert.Equal(1425, logger.LastEventId);
        Assert.Equal(
            "TCP connection disconnected: remote=69.80.99.16:49136 local=0.0.0.0:119 reason=RemoteClosed",
            line);
        Assert.DoesNotContain('\n', line);
        Assert.Null(logger.LastException);
        Assert.Equal("69.80.99.16:49136", logger.LastRemote);
        Assert.Equal("RemoteClosed", logger.LastReason);
    }

    private static void AssertSingleLineDisconnect(string line, NntpConnection server, string reason)
    {
        Assert.DoesNotContain('\n', line);
        Assert.StartsWith("TCP connection disconnected:", line, StringComparison.Ordinal);
        Assert.Contains("remote=", line, StringComparison.Ordinal);
        Assert.Contains("reason=" + reason, line, StringComparison.Ordinal);
        var remote = ConnectionAcceptanceLogging.FormatEndpoint(server.RemoteEndPoint);
        Assert.Contains(remote, line, StringComparison.Ordinal);
        Assert.DoesNotContain("at VectorNNTP", line, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", line, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private sealed class CapturingConnectionLogger : ILogger<NntpConnection>
    {
        public List<string> Disconnects { get; } = [];

        public List<Exception> DisconnectExceptions { get; } = [];

        public List<string> PumpErrors { get; } = [];

        public List<string> PumpExceptions { get; } = [];

        public LogLevel LastLevel { get; private set; }

        public int LastEventId { get; private set; }

        public Exception? LastException { get; private set; }

        public string? LastRemote { get; private set; }

        public string? LastReason { get; private set; }

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
            LastLevel = logLevel;
            LastEventId = eventId.Id;
            LastException = exception;
            if (state is IReadOnlyList<KeyValuePair<string, object?>> properties)
            {
                LastRemote = properties.FirstOrDefault(static p => p.Key == "Remote").Value as string;
                LastReason = properties.FirstOrDefault(static p => p.Key == "Reason").Value as string;
            }

            if (formatted.Contains("TCP connection disconnected:", StringComparison.Ordinal))
            {
                Disconnects.Add(formatted);
                if (exception is not null)
                {
                    DisconnectExceptions.Add(exception);
                }
            }

            if (formatted.Contains("pump ended with an error", StringComparison.Ordinal))
            {
                PumpErrors.Add(formatted);
                if (exception is not null)
                {
                    PumpExceptions.Add(exception.ToString());
                }
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

using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Tests.Networking.Transport;

namespace VectorNNTPD.TransitService.Tests.Session;

/// <summary>
/// Session RX owns <c>Connection.Input</c>. Teardown must not complete that reader while
/// <c>NntpContinuousRxReader.ReadUnitAsync</c> can still call <c>ReadAsync</c>.
/// </summary>
[Collection(nameof(TransportTestHostCollection))]
public sealed class NntpSessionInputReaderLifecycleTests
{
    [Fact]
    public async Task RemoteFin_WhileSessionWaitingForCommand_EndsWithoutReaderCompletedException()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());
        var logger = new CapturingSessionLogger();
        var session = new NntpSession(server, logger);
        var run = session.RunAsync();

        Assert.StartsWith("201 ", await ReadLineAsync(client), StringComparison.Ordinal);

        client.Shutdown(SocketShutdown.Send);
        client.Dispose();

        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(
            logger.Errors,
            static e => e.Contains("Reading is not allowed after reader was completed", StringComparison.Ordinal));
        Assert.True(server.InputReaderCompletedForTests);
        Assert.True(server.IsCompleted);
    }

    [Fact]
    public async Task RemoteFin_AfterDateCommand_EndsWithoutReaderCompletedException()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());
        var logger = new CapturingSessionLogger();
        var session = new NntpSession(server, logger);
        var run = session.RunAsync();

        Assert.StartsWith("201 ", await ReadLineAsync(client), StringComparison.Ordinal);
        await client.SendAsync("DATE\r\n"u8.ToArray());
        Assert.StartsWith("111 ", await ReadLineAsync(client), StringComparison.Ordinal);

        client.Shutdown(SocketShutdown.Send);
        client.Dispose();

        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(
            logger.Errors,
            static e => e.Contains("Reading is not allowed after reader was completed", StringComparison.Ordinal));
        Assert.True(server.InputReaderCompletedForTests);
    }

    [Fact]
    public async Task RequestClose_WhileSessionWaitingForCommand_CompletesReaderAfterRxStops()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());
        var logger = new CapturingSessionLogger();
        var session = new NntpSession(server, logger);
        var run = session.RunAsync();

        Assert.StartsWith("201 ", await ReadLineAsync(client), StringComparison.Ordinal);
        Assert.False(server.InputReaderCompletedForTests);

        session.RequestClose();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(
            logger.Errors,
            static e => e.Contains("Reading is not allowed after reader was completed", StringComparison.Ordinal));
        Assert.True(server.InputReaderCompletedForTests);
        Assert.True(server.IsCompleted);
    }

    [Fact]
    public async Task ConnectionCompleteAsync_WhileSessionWaitingForCommand_DoesNotRaceInputReader()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());
        var logger = new CapturingSessionLogger();
        var session = new NntpSession(server, logger);
        var run = session.RunAsync();

        Assert.StartsWith("201 ", await ReadLineAsync(client), StringComparison.Ordinal);

        await server.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(
            logger.Errors,
            static e => e.Contains("Reading is not allowed after reader was completed", StringComparison.Ordinal));
        Assert.True(server.InputReaderCompletedForTests);
    }

    [Fact]
    public async Task Quit_CompletesInputReaderOnce_AfterCommandLoopStops()
    {
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = Assert.IsType<NntpConnection>(await host.AcceptAsync());
        var session = new NntpSession(server, NullLogger<NntpSession>.Instance);
        var run = session.RunAsync();

        Assert.StartsWith("201 ", await ReadLineAsync(client), StringComparison.Ordinal);
        await client.SendAsync("QUIT\r\n"u8.ToArray());
        Assert.Equal("205 Connection closing", await ReadLineAsync(client));
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(server.InputReaderCompletedForTests);
        Assert.True(server.IsCompleted);
    }

    private static async Task<string> ReadLineAsync(Socket socket)
    {
        var buffer = new byte[256];
        var stored = new List<byte>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var n = await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token);
            Assert.True(n > 0);
            for (var i = 0; i < n; i++)
            {
                stored.Add(buffer[i]);
            }

            var span = stored.ToArray().AsSpan();
            var crlf = span.IndexOf("\r\n"u8);
            if (crlf >= 0)
            {
                return Encoding.ASCII.GetString(span[..crlf]);
            }
        }
    }

    private sealed class CapturingSessionLogger : ILogger<NntpSession>
    {
        public List<string> Errors { get; } = [];

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
            if (logLevel < LogLevel.Error)
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                Errors.Add(exception.ToString());
            }

            if (!string.IsNullOrEmpty(message))
            {
                Errors.Add(message);
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

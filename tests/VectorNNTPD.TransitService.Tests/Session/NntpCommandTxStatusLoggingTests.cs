using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.TransitService.History;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.TransitService.Networking.Proxy;
using VectorNNTPD.Common.Networking.Transport;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.Commands;
using VectorNNTPD.TransitService.Session.CommandProcessor;
using VectorNNTPD.Common.Networking.Proxy;
namespace VectorNNTPD.TransitService.Tests.Session;

/// <summary>
/// Semantic TX status logging: Debug-gated construction, writer isolation, no wire decode.
/// </summary>
[Collection(nameof(NntpCommandLoggerCollection))]
public sealed class NntpCommandTxStatusLoggingTests
{
    [Fact]
    public void Writer_HasNoLoggingInspectionSurface()
    {
        var type = typeof(NntpResponseWriter);
        Assert.Null(type.GetMethod("TrackCommandResponse", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Null(type.GetField("CommandResponseScope", BindingFlags.Static | BindingFlags.NonPublic));
        Assert.Null(type.GetNestedType("CommandResponseTrackingScope", BindingFlags.NonPublic));
        Assert.DoesNotContain(type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic),
            f => f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(AsyncLocal<>));
    }

    [Fact]
    public void FormatCheck_UsesSemanticResultNotWireDecode()
    {
        var id = "<want@example.com>"u8;
        Assert.Equal("238 <want@example.com> send article to be transferred",
            NntpCommandStatusText.FormatCheck(HistoryLookupResult.Unseen, id));
        Assert.Equal("438 <want@example.com>",
            NntpCommandStatusText.FormatCheck(HistoryLookupResult.Seen, id));
        Assert.Equal("431 <want@example.com>",
            NntpCommandStatusText.FormatCheck(HistoryLookupResult.Unavailable, id));
    }

    [Fact]
    public void FormatTakeThis_UsesSemanticOutcome()
    {
        var id = "<mid@example.com>"u8;
        Assert.Equal("239 <mid@example.com>", NntpCommandStatusText.FormatTakeThisAccepted(id));
        Assert.Equal("439 <mid@example.com>", NntpCommandStatusText.FormatTakeThisRejected(id));
    }

    [Fact]
    public void StaticStatus_IsNotDecodedFromWireBytes()
    {
        Assert.Equal("500 Command not implemented", NntpResponseStatus.CommandNotImplemented);
        Assert.Equal("412 No newsgroup selected", NntpResponseStatus.NoNewsgroupSelected);
        Assert.Equal("420 Current article number is invalid", NntpResponseStatus.CurrentArticleNumberInvalid);
        Assert.Equal("423 No article with that number", NntpResponseStatus.NoArticleWithNumber);
        Assert.Equal("430 No article with that message-id", NntpResponseStatus.NoArticleWithMessageId);
        Assert.Equal("503 Data item not stored", NntpResponseStatus.ListDataItemNotStored);
        Assert.Equal("215 Order of fields in overview database.", NntpResponseStatus.ListOverviewFmtFollows);
        Assert.Equal("215 headers and metadata items supported:", NntpResponseStatus.ListHeadersFollows);
        Assert.Equal("100 Help text follows", NntpResponseStatus.HelpTextFollows);
        Assert.Equal("400 Service temporarily unavailable", NntpResponseStatus.ServiceTemporarilyUnavailable);
    }

    [Fact]
    public async Task WriteCompletion_DisabledLogger_DoesNotFormatClientOrStatus()
    {
        var logger = new GateLogger(enabled: false);
        await using var harness = new StatusHarness();
        var session = harness.CreateSession();

        NntpCommandExecution.WriteCompletion(
            logger,
            session,
            "CHECK",
            TimeSpan.FromSeconds(0.012),
            statusLine: "must-not-be-logged");

        Assert.Empty(logger.Messages);
        Assert.Equal(0, logger.FormatCount);
    }

    [Fact]
    public async Task RunAsync_DisabledLogger_DoesNotSetStatusLine()
    {
        var logger = new GateLogger(enabled: false);
        await using var harness = new StatusHarness();
        var session = harness.CreateSession();
        var (command, line) = NntpCommandTestParse.Parse("HELP");
        await using var writer = new NntpResponseWriter(harness.ServerOutput);
        var context = new NntpCommandContext(session, command, line, writer);

        await NntpCommandExecution.RunAsync(
            logger,
            context,
            "HELP",
            static (ctx, ct) => NntpCommandReply.WriteAsync(
                ctx,
                new GateLogger(enabled: false),
                NntpResponses.HelpComplete,
                NntpResponseStatus.HelpTextFollows,
                ct),
            CancellationToken.None);

        Assert.Null(context.StatusLine);
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public async Task RunAsync_EnabledLogger_NotesSemanticHelpStatus()
    {
        var logger = new GateLogger(enabled: true);
        await using var harness = new StatusHarness();
        var session = harness.CreateSession();
        var (command, line) = NntpCommandTestParse.Parse("HELP");
        await using var writer = new NntpResponseWriter(harness.ServerOutput);
        var context = new NntpCommandContext(session, command, line, writer);

        await NntpCommandExecution.RunAsync(
            logger,
            context,
            "HELP",
            static (ctx, ct) => NntpCommandReply.WriteAsync(
                ctx,
                new GateLogger(enabled: true),
                NntpResponses.HelpComplete,
                NntpResponseStatus.HelpTextFollows,
                ct),
            CancellationToken.None);

        Assert.Equal(NntpResponseStatus.HelpTextFollows, context.StatusLine);
        var tx = Assert.Single(logger.Messages, m => m.Contains("TX: HELP", StringComparison.Ordinal));
        Assert.Contains("TX: HELP [100 Help text follows] executed in", tx, StringComparison.Ordinal);
        Assert.DoesNotContain("ARTICLE [message-id / article-number]", tx, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_HandlerThrowsBeforeResponse_KeepsNoStatusFormat()
    {
        var logger = new GateLogger(enabled: true);
        await using var harness = new StatusHarness();
        var session = harness.CreateSession();
        var (command, line) = NntpCommandTestParse.Parse("DATE");
        await using var writer = new NntpResponseWriter(harness.ServerOutput);
        var context = new NntpCommandContext(session, command, line, writer);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await NntpCommandExecution.RunAsync(
                logger,
                context,
                "DATE",
                static (_, _) => throw new InvalidOperationException("boom"),
                CancellationToken.None));
        Assert.Equal("boom", thrown.Message);

        var tx = Assert.Single(logger.Messages, m => m.Contains("TX: DATE", StringComparison.Ordinal));
        Assert.Contains("TX: DATE executed in", tx, StringComparison.Ordinal);
        Assert.Contains("[failed]", tx, StringComparison.Ordinal);
        Assert.DoesNotContain("[403", tx, StringComparison.Ordinal);
        Assert.DoesNotContain("[no response]", tx, StringComparison.Ordinal);
        Assert.DoesNotContain("111 ", tx, StringComparison.Ordinal);
        Assert.Null(context.StatusLine);
    }

    [Fact]
    public async Task ConcurrentContexts_KeepIsolatedStatusLines()
    {
        var firstLogger = new GateLogger(enabled: true);
        var secondLogger = new GateLogger(enabled: true);
        await using var harness = new StatusHarness();
        var session = harness.CreateSession();
        await using var writer = new NntpResponseWriter(harness.ServerOutput);

        var (checkA, lineA) = NntpCommandTestParse.Parse("CHECK <a@example.com>");
        var (checkB, lineB) = NntpCommandTestParse.Parse("CHECK <b@example.com>");
        var contextA = new NntpCommandContext(session, checkA, lineA, writer);
        var contextB = new NntpCommandContext(session, checkB, lineB, writer);

        await Task.WhenAll(
            NntpCommandExecution.RunAsync(
                firstLogger,
                contextA,
                "CHECK",
                (ctx, _) =>
                {
                    ctx.StatusLine = NntpCommandStatusText.FormatCheck(
                        HistoryLookupResult.Unseen,
                        "<a@example.com>"u8);
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None).AsTask(),
            NntpCommandExecution.RunAsync(
                secondLogger,
                contextB,
                "CHECK",
                (ctx, _) =>
                {
                    ctx.StatusLine = NntpCommandStatusText.FormatCheck(
                        HistoryLookupResult.Seen,
                        "<b@example.com>"u8);
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None).AsTask());

        Assert.Equal("238 <a@example.com> send article to be transferred", contextA.StatusLine);
        Assert.Equal("438 <b@example.com>", contextB.StatusLine);
        Assert.Contains(firstLogger.Messages, m => m.Contains("[238 <a@example.com> send article to be transferred]", StringComparison.Ordinal));
        Assert.Contains(secondLogger.Messages, m => m.Contains("[438 <b@example.com>]", StringComparison.Ordinal));
        Assert.DoesNotContain(firstLogger.Messages, m => m.Contains("438", StringComparison.Ordinal));
        Assert.DoesNotContain(secondLogger.Messages, m => m.Contains("238", StringComparison.Ordinal));
    }

    private sealed class GateLogger : ILogger
    {
        private readonly bool _enabled;

        public GateLogger(bool enabled) => _enabled = enabled;

        public ConcurrentBag<string> Messages { get; } = [];

        public int FormatCount => Messages.Count;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _enabled && logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class StatusHarness : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public PipeWriter ServerOutput => _serverToClient.Writer;

        public NntpSession CreateSession()
        {
            var connection = new PipeConnection(_clientToServer.Reader, _serverToClient.Writer);
            return new NntpSession(connection, NullLogger<NntpSession>.Instance);
        }

        public async ValueTask DisposeAsync()
        {
            await _clientToServer.Writer.CompleteAsync();
            await _clientToServer.Reader.CompleteAsync();
            await _serverToClient.Writer.CompleteAsync();
            await _serverToClient.Reader.CompleteAsync();
        }

        private sealed class PipeConnection : INntpConnection
        {
            private readonly CancellationTokenSource _cts = new();

            public PipeConnection(PipeReader input, PipeWriter output)
            {
                Input = input;
                Output = output;
                ClientIdentity = ConnectionClientIdentity.Direct(
                    new IPEndPoint(IPAddress.Parse("198.18.0.70"), 49860));
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
    }
}

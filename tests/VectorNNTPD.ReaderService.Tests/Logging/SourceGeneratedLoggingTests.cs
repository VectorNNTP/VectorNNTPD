using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using VectorNNTPD.ReaderService.Hosting;
using VectorNNTPD.ReaderService.Logging;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Session.Commands;
using VectorNNTPD.ReaderService.Session.CommandProcessor;
using VectorNNTPD.ReaderService.Tests.Fixtures;
using VectorNNTPD.Common.Logging;

namespace VectorNNTPD.ReaderService.Tests.Logging;

/// <summary>
/// Observable contracts for source-generated logging: templates, structured properties,
/// EventIds, levels, and exception preservation.
/// </summary>
[Collection(SerilogCollection.Name)]
public sealed class SourceGeneratedLoggingTests
{
    [Fact]
    public void CommandRx_PreservesStructuredTemplateAndEventId()
    {
        var logger = new CapturingLogger();

        CommandLogMessages.CommandRx(logger, "198.18.0.70:49860", "HELP");

        Assert.Equal(LogLevel.Debug, logger.Level);
        Assert.Equal(1600, logger.EventId.Id);
        Assert.Equal("[198.18.0.70:49860] RX: HELP", logger.Formatted);
        Assert.Equal("198.18.0.70:49860", logger.GetString("Client"));
        Assert.Equal("HELP", logger.GetString("Command"));
        Assert.DoesNotContain("{Command}", logger.Formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("$\"", logger.Formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandRejected_PreservesStructuredProtocolFields()
    {
        var logger = new CapturingLogger();

        CommandLogMessages.CommandRejected(
            logger,
            "198.18.0.70:49860",
            NntpVerb.Unknown,
            NntpVerb.None,
            NntpParseStatus.UnknownVerb,
            "empty command");

        Assert.Equal(LogLevel.Debug, logger.Level);
        Assert.Equal(1601, logger.EventId.Id);
        Assert.Contains("RX rejected:", logger.Formatted, StringComparison.Ordinal);
        Assert.Equal("198.18.0.70:49860", logger.GetString("Client"));
        Assert.Equal(NntpVerb.Unknown, logger.GetValue<NntpVerb>("Verb"));
        Assert.Equal(NntpVerb.None, logger.GetValue<NntpVerb>("Qualifier"));
        Assert.Equal(NntpParseStatus.UnknownVerb, logger.GetValue<NntpParseStatus>("Status"));
        Assert.Equal("empty command", logger.GetString("Detail"));
    }

    [Fact]
    public void CommandFailed_PreservesException()
    {
        var logger = new CapturingLogger();
        var boom = new InvalidOperationException("handler-fail");

        CommandLogMessages.CommandFailed(logger, boom, "198.18.0.70:49860", "DATE");

        Assert.Equal(LogLevel.Error, logger.Level);
        Assert.Equal(1604, logger.EventId.Id);
        Assert.Same(boom, logger.Exception);
        Assert.Equal("[198.18.0.70:49860] DATE failed", logger.Formatted);
        Assert.Equal("DATE", logger.GetString("Command"));
    }

    [Fact]
    public void CommandTx_PreservesTimingShape()
    {
        var logger = new CapturingLogger();

        CommandLogMessages.CommandTx(logger, "198.18.0.70:49860", "HELP", 0.012);

        Assert.Equal(LogLevel.Debug, logger.Level);
        Assert.Equal(1602, logger.EventId.Id);
        Assert.Equal("[198.18.0.70:49860] TX: HELP executed in 0.012s", logger.Formatted);
        Assert.Equal(0.012, logger.GetValue<double>("ElapsedSeconds"));
    }

    [Fact]
    public void CommandTxWithStatus_PreservesStatusLineAndTimingShape()
    {
        var logger = new CapturingLogger();

        CommandLogMessages.CommandTxWithStatus(
            logger,
            "198.18.0.70:49860",
            "CHECK",
            "238 <want@example.com> send article to be transferred",
            0.012);

        Assert.Equal(LogLevel.Debug, logger.Level);
        Assert.Equal(1615, logger.EventId.Id);
        Assert.Equal(
            "[198.18.0.70:49860] TX: CHECK [238 <want@example.com> send article to be transferred] executed in 0.012s",
            logger.Formatted);
        Assert.Equal("CHECK", logger.GetString("Command"));
        Assert.Equal(
            "238 <want@example.com> send article to be transferred",
            logger.GetString("StatusLine"));
        Assert.Equal(0.012, logger.GetValue<double>("ElapsedSeconds"));
    }

    [Fact]
    public void CommandTxWithStatusAndDetail_PreservesBothStatusAndDetail()
    {
        var logger = new CapturingLogger();

        CommandLogMessages.CommandTxWithStatusAndDetail(
            logger,
            "198.18.0.70:49860",
            "STARTTLS",
            "382 Continue with TLS negotiation",
            0.001,
            "TlsVersion=TLSv1.3, Cipher=TLS_AES_256_GCM_SHA384");

        Assert.Equal(LogLevel.Debug, logger.Level);
        Assert.Equal(1616, logger.EventId.Id);
        Assert.Equal(
            "[198.18.0.70:49860] TX: STARTTLS [382 Continue with TLS negotiation] executed in 0.001s [TlsVersion=TLSv1.3, Cipher=TLS_AES_256_GCM_SHA384]",
            logger.Formatted);
        Assert.Equal("382 Continue with TLS negotiation", logger.GetString("StatusLine"));
        Assert.Equal("TlsVersion=TLSv1.3, Cipher=TLS_AES_256_GCM_SHA384", logger.GetString("Detail"));
    }

    [Fact]
    public void RedactRxCommand_IsExplicitLoggingStringBoundary()
    {
        var command = NntpCommandParser.Parse("HELP"u8);
        var text = NntpCommandLogFormat.RedactRxCommand(command, "HELP"u8);

        Assert.Equal("HELP", text);

        var pass = NntpCommandParser.Parse("AUTHINFO PASS secret"u8);
        Assert.Equal("AUTHINFO PASS <redacted>", NntpCommandLogFormat.RedactRxCommand(pass, "AUTHINFO PASS secret"u8));
    }

    [Fact]
    public void DisabledInformation_DoesNotInvokeFormatter()
    {
        var logger = new CapturingLogger(enabled: false);

        CommandLogMessages.CommandRx(logger, "198.18.0.70:49860", "HELP");

        Assert.Null(logger.Formatted);
        Assert.Empty(logger.Properties);
    }

    [Fact]
    public void LifecycleTransition_UsesReservedEventId1000()
    {
        var logger = new CapturingLogger();

        LifecycleLogMessages.LifecycleTransition(
            logger,
            ApplicationStateLog.Starting,
            ApplicationStateLog.Running);

        Assert.Equal(LogLevel.Information, logger.Level);
        Assert.Equal(1000, logger.EventId.Id);
        Assert.Equal(
            "Application lifecycle state transition: Starting -> Running",
            logger.Formatted);
    }

    [Fact]
    public void SessionEndedWithError_PreservesClientAndException()
    {
        var logger = new CapturingLogger();
        var boom = new IOException("peer reset");
        var client = IPAddress.Parse("198.18.0.70");

        SessionLogMessages.SessionEndedWithError(logger, boom, client);

        Assert.Equal(LogLevel.Debug, logger.Level);
        Assert.Equal(1611, logger.EventId.Id);
        Assert.Same(boom, logger.Exception);
        Assert.Equal(client, logger.GetValue<IPAddress>("Client"));
    }

    [Fact]
    public void PlainConnectionAccepted_PreservesEndpointProperty()
    {
        var logger = new CapturingLogger();

        NetworkingLogMessages.PlainConnectionAccepted(logger, "198.18.0.70:42122");

        Assert.Equal(LogLevel.Information, logger.Level);
        Assert.Equal(1405, logger.EventId.Id);
        Assert.Equal("Plain connection accepted from 198.18.0.70:42122", logger.Formatted);
        Assert.Equal("198.18.0.70:42122", logger.GetString("TcpPeer"));
    }

    [Fact]
    public void GeneratedMessages_FlowThroughSerilogStructuredProperties()
    {
        var sink = new CollectingSink();
        var builder = Host.CreateApplicationBuilder();
        TestHostFactory.ConfigureNntpdTestHost(builder);
        builder.ConfigureNntpdLogging(lc =>
        {
            lc.MinimumLevel.Verbose();
            lc.WriteTo.Sink(sink);
        });

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("VectorNNTPD.ReaderService.Tests.SourceGenerated");

        var boom = new SocketException((int)SocketError.ConnectionReset);
        CommandLogMessages.QuitUncaught(logger, boom, "198.18.0.70:49860", boom.GetType().Name, boom.SocketErrorCode);

        var evt = Assert.Single(sink.Events, e => e.Exception is SocketException);
        Assert.Equal(LogEventLevel.Error, evt.Level);
        Assert.Same(boom, evt.Exception);
        Assert.Equal(
            "[{Client}] QUIT uncaught during termination: {Type} socket={Socket}",
            evt.MessageTemplate.Text);
        Assert.True(evt.Properties.ContainsKey("Client"));
        Assert.True(evt.Properties.ContainsKey("Type"));
        Assert.True(evt.Properties.ContainsKey("Socket"));
        Assert.Equal("198.18.0.70:49860", Scalar(evt, "Client"));
    }

    [Fact]
    public void EventIds_AreUniqueAcrossGeneratedMessageFiles()
    {
        var ids = new[]
        {
            1000, 1001, 1002, 1003, 1004, 1005,
            1010, 1011, 1012, 1013, 1014, 1015, 1016, 1017, 1018, 1019, 1020, 1021, 1022, 1023,
            1100, 1101, 1102, 1103, 1104, 1105, 1106, 1107, 1108, 1109, 1110, 1111, 1112, 1113,
            1114, 1115, 1116, 1117, 1118, 1119, 1120, 1121, 1122, 1123,
            1200, 1201, 1202, 1203, 1204, 1205, 1206, 1207, 1208,
            1300, 1301, 1302, 1303, 1304, 1305, 1306, 1307, 1308, 1309, 1310, 1311, 1312, 1313,
            1314, 1315, 1316, 1317, 1318,
            1400, 1401, 1402, 1403, 1404, 1405, 1406, 1407, 1408, 1409, 1410, 1411, 1412, 1413,
            1414, 1415, 1416, 1417, 1418, 1419, 1420, 1421, 1422, 1423,
            1500, 1501, 1502, 1503,
            1600, 1601, 1602, 1603, 1604, 1605, 1606, 1607, 1608, 1609, 1610, 1611,
            1615, 1616,
            1700, 1701, 1702, 1703, 1704, 1705, 1706,
            1800, 1801, 1802, 1803, 1804, 1805, 1806, 1807, 1808, 1809, 1810, 1811, 1812, 1813,
            1814, 1815, 1816, 1817, 1818, 1819, 1820, 1821, 1822, 1823, 1824, 1825, 1826, 1827,
            1828, 1829,
            1900, 1901, 1902, 1903, 1904, 1905, 1906, 1907, 1908, 1909, 1910, 1911, 1912, 1913,
            1914, 1915, 1916, 1917, 1918,
            2000, 2001, 2002, 2003, 2004,
        };

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    private static string Scalar(LogEvent evt, string name) =>
        evt.Properties[name].ToString().Trim('"');

    private sealed class CapturingLogger : ILogger
    {
        private readonly bool _enabled;

        public CapturingLogger(bool enabled = true) => _enabled = enabled;

        public LogLevel? Level { get; private set; }

        public EventId EventId { get; private set; }

        public Exception? Exception { get; private set; }

        public string? Formatted { get; private set; }

        public Dictionary<string, object?> Properties { get; } = new(StringComparer.Ordinal);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _enabled;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Level = logLevel;
            EventId = eventId;
            Exception = exception;
            Formatted = formatter(state, exception);
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    Properties[pair.Key] = pair.Value;
                }
            }
        }

        public string GetString(string name) =>
            Convert.ToString(Properties[name], CultureInfo.InvariantCulture) ?? string.Empty;

        public T GetValue<T>(string name) =>
            Assert.IsType<T>(Properties[name]);
    }
}

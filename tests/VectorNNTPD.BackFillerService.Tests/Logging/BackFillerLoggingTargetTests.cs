using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Logging;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.BackFillerService.Tests.RabbitMq;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using System.Text;
using System.Net.Sockets;
using System.Net;

namespace VectorNNTPD.BackFillerService.Tests.Logging
{
    /// <summary>
    /// Optional log targets (file, syslog, rabbit), formatters, console switch, and log-context enrichment.
    /// </summary>
    [Collection(SerilogCollection.Name)]
    public sealed class BackFillerLoggingTargetTests
    {
        /// <summary>Default bound logging options enable file only with documented defaults.</summary>
        [Fact]
        public void Defaults_EnableFile_AndDisableRabbitMq_Syslog_AndJson()
        {
            using var configuration = new ConfigurationManager();
            var logging = new BackFillerLoggingOptions();
            configuration.GetSection($"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}").Bind(logging);
            Assert.True(logging.File.Enabled);
            Assert.Equal(BackFillerFileLoggingTargetOptions.DefaultLogDir, logging.File.LogDir);
            Assert.False(logging.RabbitMq.Enabled);
            Assert.Equal("logs", logging.RabbitMq.Exchange);
            Assert.Equal("backfiller", logging.RabbitMq.RoutingKey);
            Assert.False(logging.Syslog.Enabled);
            Assert.Equal(514, logging.Syslog.Port);
            Assert.Equal("Udp", logging.Syslog.Protocol);
            Assert.False(logging.Json);
            Assert.Equal(BackFillerLoggingOptions.DefaultLogLevel, logging.LogLevel);
            Assert.Equal(BackFillerLoggingOptions.DefaultLogRetentionDays, logging.LogRetentionDays);
        }
        /// <summary>File disabled skips file sink and does not create log directory.</summary>

        [Fact]
        public void File_CanBeDisabled_WithoutCreatingTheLogDirectory()
        {
            var logDir = Path.Join(Path.GetTempPath(), "vectornntp-bf-nolog-" + Guid.NewGuid().ToString("N"));
            try
            {
                var configuration = Configuration(logDir);
                configuration[$"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:File:Enabled"] = "false";
                using var logger = Build(configuration);
                Assert.DoesNotContain(Sinks(logger), static sink => sink.GetType().Name.Contains("File", StringComparison.Ordinal));
                Assert.False(Directory.Exists(logDir));
            }
            finally
            {
                Log.CloseAndFlush();
                TryDelete(logDir);
            }
        }
        /// <summary>Multiple sinks register without Serilog.Sinks.RabbitMQ package reference.</summary>

        [Fact]
        public void File_Syslog_AndRabbitMq_CanBeEnabledTogether()
        {
            var logDir = NewLogDir();
            try
            {
                var configuration = Configuration(logDir);
                var logging = $"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}";
                configuration[$"{logging}:RabbitMQ:Enabled"] = "true";
                configuration[$"{logging}:Syslog:Enabled"] = "true";
                configuration[$"{logging}:Syslog:Host"] = "127.0.0.1";
                configuration[$"{logging}:Syslog:Protocol"] = "Udp";
                using var services = RabbitServices();
                using var logger = Build(configuration, services: services);
                var names = Sinks(logger).Select(static sink => sink.GetType().Name).ToArray();
                Assert.Contains(names, static name => name.Contains("File", StringComparison.Ordinal));
                Assert.Contains(names, static name => name.Contains("Syslog", StringComparison.Ordinal));
                Assert.Contains(names, static name => name.Equals("RabbitMqLogEventSink", StringComparison.Ordinal));
                var namespaced = typeof(BackFillerLoggingExtensions).Assembly.GetReferencedAssemblies().Select(static name => name.Name);
                Assert.DoesNotContain("Serilog.Sinks.RabbitMQ", namespaced);
            }
            finally
            {
                Log.CloseAndFlush();
                TryDelete(logDir);
            }
        }
        /// <summary>Rabbit log sink uses existing IRabbitMqService connection without blocking Log.Information caller.</summary>

        [Fact]
        public async Task RabbitMqTarget_PublishesOnTheExistingConnection_WithoutBlockingTheCaller()
        {
            var logDir = NewLogDir();
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = RabbitMqServiceTests.CreateService(factory);
            await service.StartAsync(CancellationToken.None);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var connection = factory.LastConnection!;
                var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.DefaultPublishConfirmBehavior = FakePublishConfirmBehavior.Confirm;
                connection.BlockCreatePublishChannel = gate;
                connection.CreatePublishChannelStarted = started;
                connection.OnPublishChannelCreated = channel => channel.Enqueued = published;
                var configuration = Configuration(logDir);
                configuration[$"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:RabbitMQ:Enabled"] = "true";
                var services = new ServiceCollection();
                services.AddSingleton<IRabbitMqService>(service);
                using var provider = services.BuildServiceProvider();
                using var logger = Build(configuration, services: provider);

                var logged = Task.Run(() => logger.Information("rabbit-existing-connection"));
                using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await started.Task.WaitAsync(safety.Token);
                await logged.WaitAsync(safety.Token);
                Assert.Equal(1, factory.ConnectCount);
                Assert.Empty(connection.PublishChannels);

                gate.TrySetResult();
                await published.Task.WaitAsync(safety.Token);
                Assert.Equal(1, factory.ConnectCount);
                var publication = Assert.Single(Assert.Single(connection.PublishChannels).Publications);
                Assert.Equal("logs", publication.Exchange);
                Assert.Equal("backfiller", publication.RoutingKey);
                Assert.Contains("rabbit-existing-connection", Encoding.UTF8.GetString(publication.Body.Span), StringComparison.Ordinal);
                Assert.DoesNotContain(BackFillerTestOptions.SecretPassword, Encoding.UTF8.GetString(publication.Body.Span), StringComparison.Ordinal);
            }
            finally
            {
                gate.TrySetResult();
                Log.CloseAndFlush();
                await service.DisposeAsync();
                TryDelete(logDir);
            }
        }
        /// <summary>Syslog sink sends to loopback UDP or TCP per protocol setting.</summary>

        [Theory]
        [InlineData("Udp")]
        [InlineData("TCP")]
        public async Task Syslog_ConfiguresUdpAndTcpTargets(string protocol)
        {
            var logDir = NewLogDir();
            try
            {
                using UdpClient? udp = protocol.Equals("Udp", StringComparison.OrdinalIgnoreCase)
                    ? new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))
                    : null;
                using TcpListener? tcp = udp is null ? new TcpListener(IPAddress.Loopback, 0) : null;
                if (tcp is not null)
                {
                    tcp.Start();
                }

                var port = udp is not null
                    ? ((IPEndPoint)udp.Client.LocalEndPoint!).Port
                    : ((IPEndPoint)tcp!.LocalEndpoint).Port;
                var configuration = Configuration(logDir);
                var section = $"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:Syslog";
                configuration[$"{section}:Enabled"] = "true";
                configuration[$"{section}:Host"] = "127.0.0.1";
                configuration[$"{section}:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                configuration[$"{section}:Protocol"] = protocol;
                using (var logger = Build(configuration))
                {
                    logger.Information("syslog-target-marker");
                    Assert.Contains(Sinks(logger), static sink => sink.GetType().Name.Contains("Syslog", StringComparison.Ordinal));
                }

                if (udp is not null)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var packet = await udp.ReceiveAsync(timeout.Token);
                    var text = Encoding.UTF8.GetString(packet.Buffer);
                    Assert.Contains("syslog-target-marker", text, StringComparison.Ordinal);
                }
                else
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var client = await tcp!.AcceptTcpClientAsync(timeout.Token);
                    var buffer = new byte[256];
                    var read = await client.GetStream().ReadAsync(buffer, timeout.Token);
                    Assert.True(read > 0);
                }
            }
            finally
            {
                Log.CloseAndFlush();
                TryDelete(logDir);
            }
        }
        /// <summary>Global Json flag applies MessageTemplate JSON to file, console, syslog, and rabbit sinks.</summary>

        [Fact]
        public async Task JsonFormatter_AppliesToFile_Console_AndSyslog_WithoutPerTargetSettings()
        {
            var logDir = NewLogDir();
            using var captured = new StringWriter();
            var previous = Console.Out;
            try
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
                Console.SetOut(captured);
                var configuration = Configuration(logDir);
                var logging = $"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}";
                configuration[$"{logging}:Json"] = "true";
                configuration[$"{logging}:File:Formatter"] = "not-a-formatter";
                configuration[$"{logging}:Syslog:Json"] = "true";
                configuration[$"{logging}:Syslog:Enabled"] = "true";
                configuration[$"{logging}:Syslog:Host"] = "127.0.0.1";
                configuration[$"{logging}:Syslog:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                configuration[$"{logging}:Syslog:Protocol"] = "Udp";
                configuration[$"{logging}:RabbitMQ:Enabled"] = "true";
                configuration[$"{logging}:RabbitMQ:Json"] = "true";
                using var services = RabbitServices();
                using (var logger = Build(
                    configuration,
                    new BackFillerLoggingCommandLine(Console: true, EnrichFromLogContext: false),
                    services))
                {
                    logger.Information("json-formatter-marker");
                    var rabbitSink = Assert.Single(Sinks(logger).OfType<RabbitMqLogEventSink>());
                    Assert.Equal(RabbitMqLogEventSink.JsonContentType, rabbitSink.ContentType);
                    using var rabbitJson = new StringWriter();
                    rabbitSink.Formatter.Format(
                        SourceContextEvent("VectorNNTPD.BackFillerService.SourceContextProbe", "json-formatter-marker"),
                        rabbitJson);
                    Assert.Contains("\"MessageTemplate\"", rabbitJson.ToString(), StringComparison.Ordinal);
                    Assert.DoesNotContain("SourceContext", rabbitJson.ToString(), StringComparison.Ordinal);
                }

                var fileText = File.ReadAllText(Directory.GetFiles(logDir, "*.log").Single());
                Assert.Contains("\"MessageTemplate\"", fileText, StringComparison.Ordinal);
                Assert.Contains("json-formatter-marker", captured.ToString(), StringComparison.Ordinal);
                Assert.Contains("{", captured.ToString(), StringComparison.Ordinal);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var packet = await udp.ReceiveAsync(timeout.Token);
                var syslog = Encoding.UTF8.GetString(packet.Buffer);
                Assert.Contains("\"MessageTemplate\"", syslog, StringComparison.Ordinal);
            }
            finally
            {
                Console.SetOut(previous);
                Log.CloseAndFlush();
                TryDelete(logDir);
            }
        }
        /// <summary>Per-target Formatter keys do not override global text formatting.</summary>

        [Fact]
        public void TextFormatter_StaysInEffect_WhenOnlyADecoyPerTargetFormatterIsSet()
        {
            var logDir = NewLogDir();
            try
            {
                var configuration = Configuration(logDir);
                var logging = $"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}";
                configuration[$"{logging}:File:Formatter"] = "Json";
                configuration[$"{logging}:RabbitMQ:Enabled"] = "true";
                configuration[$"{logging}:RabbitMQ:Formatter"] = "Json";
                using var services = RabbitServices();
                using (var logger = Build(configuration, services: services))
                {
                    var rabbitSink = Assert.Single(Sinks(logger).OfType<RabbitMqLogEventSink>());
                    Assert.IsType<MessageTemplateTextFormatter>(rabbitSink.Formatter);
                    Assert.Equal(RabbitMqLogEventSink.TextContentType, rabbitSink.ContentType);
                    logger.Information("plain-template-marker");
                }

                var fileText = File.ReadAllText(Directory.GetFiles(logDir, "*.log").Single());
                Assert.Contains("plain-template-marker", fileText, StringComparison.Ordinal);
                Assert.DoesNotContain("\"MessageTemplate\"", fileText, StringComparison.Ordinal);
            }
            finally
            {
                Log.CloseAndFlush();
                TryDelete(logDir);
            }
        }
        /// <summary>File and syslog sinks coexist in one logger configuration.</summary>

        [Fact]
        public void File_And_Syslog_CanBeEnabledTogether()
        {
            var logDir = NewLogDir();
            try
            {
                var configuration = Configuration(logDir);
                var syslog = $"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:Syslog";
                configuration[$"{syslog}:Enabled"] = "true";
                configuration[$"{syslog}:Host"] = "127.0.0.1";
                configuration[$"{syslog}:Protocol"] = "Tcp";
                configuration[$"{syslog}:Port"] = "9";
                using var logger = Build(configuration);
                var names = Sinks(logger).Select(static sink => sink.GetType().Name).ToArray();
                Assert.Contains(names, static name => name.Contains("File", StringComparison.Ordinal));
                Assert.Contains(names, static name => name.Contains("Syslog", StringComparison.Ordinal));
            }
            finally
            {
                Log.CloseAndFlush();
                TryDelete(logDir);
            }
        }
        /// <summary>Console sink appears only when --console command-line flag is set.</summary>

        [Fact]
        public void Console_IsAbsentUnlessTheConsoleSwitchIsPresent()
        {
            var absent = BackFillerLoggingCommandLine.FromArguments(["--other"]);
            Assert.False(absent.Console);
            var present = BackFillerLoggingCommandLine.FromArguments(["--console"]);
            Assert.True(present.Console);
            Assert.False(present.EnrichFromLogContext);

            var logDir = NewLogDir();
            using var captured = new StringWriter();
            var previous = Console.Out;
            try
            {
                Console.SetOut(captured);
                using var logger = Build(Configuration(logDir));
                logger.Information("console-should-stay-off");
                Log.CloseAndFlush();
                Assert.DoesNotContain("console-should-stay-off", captured.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain(
                    Sinks(logger),
                    static sink => sink is ILogEventSink
                        && sink.GetType().Name.Contains("Console", StringComparison.Ordinal));
            }
            finally
            {
                Console.SetOut(previous);
                Log.CloseAndFlush();
                TryDelete(logDir);
            }
        }
        /// <summary>LogContext enrichment requires --log-context switch.</summary>

        [Fact]
        public void LogContextEnrichment_IsAppliedOnlyWhenRequested()
        {
            var off = BackFillerLoggingCommandLine.FromArguments(null);
            Assert.False(off.EnrichFromLogContext);
            var on = BackFillerLoggingCommandLine.FromArguments(["--log-context"]);
            Assert.True(on.EnrichFromLogContext);
            Assert.False(on.Console);

            Assert.False(EventHasProbe(off));
            Assert.True(EventHasProbe(on));
        }
        /// <summary>Text outputs omit SourceContext unless log-context switch is set.</summary>

        [Fact]
        public void SourceContext_IsAbsentFromTextOutput_UnlessTheLogContextSwitchIsSet()
        {
            const string category = "VectorNNTPD.BackFillerService.SourceContextProbe";
            const string marker = "source-context-text-marker";

            var without = RenderTextTargets(
                BackFillerLoggingCommandLine.FromArguments(["--console"]),
                category,
                marker);
            Assert.Contains(marker, without.File, StringComparison.Ordinal);
            Assert.Contains(marker, without.Console, StringComparison.Ordinal);
            Assert.Contains(marker, without.Bootstrap, StringComparison.Ordinal);
            Assert.Contains(marker, without.Rabbit, StringComparison.Ordinal);
            Assert.DoesNotContain(category, without.File, StringComparison.Ordinal);
            Assert.DoesNotContain(category, without.Console, StringComparison.Ordinal);
            Assert.DoesNotContain(category, without.Bootstrap, StringComparison.Ordinal);
            Assert.DoesNotContain(category, without.Rabbit, StringComparison.Ordinal);

            var withContext = RenderTextTargets(
                BackFillerLoggingCommandLine.FromArguments(["--console", "--log-context"]),
                category,
                marker);
            Assert.Contains(category, withContext.File, StringComparison.Ordinal);
            Assert.Contains(category, withContext.Console, StringComparison.Ordinal);
            Assert.Contains(category, withContext.Bootstrap, StringComparison.Ordinal);
            Assert.Contains(category, withContext.Rabbit, StringComparison.Ordinal);
        }
        /// <summary>Syslog text includes category only with log-context.</summary>

        [Fact]
        public async Task Syslog_RendersSourceContext_OnlyWhenTheLogContextSwitchIsSet()
        {
            const string category = "VectorNNTPD.BackFillerService.SourceContextProbe";
            var absent = await ReadSyslogPacketAsync(
                BackFillerLoggingCommandLine.FromArguments(null),
                category);
            Assert.Contains("syslog-source-context-marker", absent.Packet, StringComparison.Ordinal);
            Assert.DoesNotContain(category, absent.Packet, StringComparison.Ordinal);
            Assert.True(absent.RootEventHasSourceContext);

            var present = await ReadSyslogPacketAsync(
                BackFillerLoggingCommandLine.FromArguments(["--log-context"]),
                category);
            Assert.Contains(category, present.Packet, StringComparison.Ordinal);
        }
        /// <summary>JSON file and rabbit omit SourceContext property unless log-context.</summary>

        [Fact]
        public void JsonOutput_IncludesSourceContext_OnlyWhenTheLogContextSwitchIsSet()
        {
            const string category = "VectorNNTPD.BackFillerService.SourceContextProbe";
            const string marker = "json-source-context-marker";

            var without = RenderJson(BackFillerLoggingCommandLine.FromArguments(null), category, marker);
            Assert.Contains(marker, without.File, StringComparison.Ordinal);
            Assert.Contains("\"Application\":", without.File, StringComparison.Ordinal);
            Assert.Contains("\"MessageTemplate\"", without.File, StringComparison.Ordinal);
            Assert.DoesNotContain("SourceContext", without.File, StringComparison.Ordinal);
            Assert.Contains("\"Application\":", without.Rabbit, StringComparison.Ordinal);
            Assert.DoesNotContain("SourceContext", without.Rabbit, StringComparison.Ordinal);
            Assert.True(without.RootEventHasSourceContext);

            var withContext = RenderJson(BackFillerLoggingCommandLine.FromArguments(["--log-context"]), category, marker);
            Assert.Contains("\"SourceContext\":\"" + category + "\"", withContext.File, StringComparison.Ordinal);
            Assert.Contains("\"Application\":", withContext.File, StringComparison.Ordinal);
            Assert.Contains("\"SourceContext\":\"" + category + "\"", withContext.Rabbit, StringComparison.Ordinal);
            Assert.Contains("\"Application\":", withContext.Rabbit, StringComparison.Ordinal);
        }
        /// <summary>JSON syslog includes SourceContext only with log-context.</summary>

        [Fact]
        public async Task JsonSyslog_IncludesSourceContext_OnlyWhenTheLogContextSwitchIsSet()
        {
            const string category = "VectorNNTPD.BackFillerService.SourceContextProbe";
            var absent = await ReadSyslogPacketAsync(BackFillerLoggingCommandLine.FromArguments(null), category, json: true);
            Assert.Contains("\"MessageTemplate\"", absent.Packet, StringComparison.Ordinal);
            Assert.DoesNotContain(category, absent.Packet, StringComparison.Ordinal);
            Assert.DoesNotContain("SourceContext", absent.Packet, StringComparison.Ordinal);
            Assert.True(absent.RootEventHasSourceContext);

            var present = await ReadSyslogPacketAsync(
                BackFillerLoggingCommandLine.FromArguments(["--log-context"]),
                category,
                json: true);
            Assert.Contains("\"SourceContext\":\"" + category + "\"", present.Packet, StringComparison.Ordinal);
        }

        /// <summary>Renders file, console, bootstrap, and Rabbit text output for one probe event.</summary>
        /// <param name="commandLine">Logging command-line switches.</param>
        /// <param name="category">Source context category.</param>
        /// <param name="marker">Message template marker written to sinks.</param>
        /// <returns>Captured text from each target.</returns>
        private static (string File, string Console, string Bootstrap, string Rabbit) RenderTextTargets(
            BackFillerLoggingCommandLine commandLine,
            string category,
            string marker)
        {
            var logDir = NewLogDir();
            using var captured = new StringWriter();
            var previous = Console.Out;
            try
            {
                Console.SetOut(captured);
                var configuration = Configuration(logDir);
                var logging = $"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}";
                configuration[$"{logging}:LogLevel"] = "Debug";
                configuration[$"{logging}:RabbitMQ:Enabled"] = "true";
                using var services = RabbitServices();
                string rabbit;
                using (var logger = Build(configuration, commandLine, services))
                {
                    logger.ForContext(Constants.SourceContextPropertyName, category).Debug(marker);
                    var rabbitSink = Assert.Single(Sinks(logger).OfType<RabbitMqLogEventSink>());
                    using var writer = new StringWriter();
                    rabbitSink.Formatter.Format(SourceContextEvent(category, marker), writer);
                    rabbit = writer.ToString();
                }

                return (
                    File.ReadAllText(Directory.GetFiles(logDir, "*.log").Single()),
                    captured.ToString(),
                    RenderBootstrap(commandLine, category, marker),
                    rabbit);
            }
            finally
            {
                Console.SetOut(previous);
                Log.CloseAndFlush();
                TryDelete(logDir);
            }
        }

        /// <summary>Builds JSON file logging plus Rabbit formatter output and reports SourceContext on the root event.</summary>
        /// <param name="commandLine">Logging command-line switches.</param>
        /// <param name="category">Source context category.</param>
        /// <param name="marker">Debug marker message.</param>
        /// <returns>File contents, Rabbit line, and whether SourceContext was attached.</returns>
        private static (string File, string Rabbit, bool RootEventHasSourceContext) RenderJson(
            BackFillerLoggingCommandLine commandLine,
            string category,
            string marker)
        {
            var logDir = NewLogDir();
            try
            {
                var configuration = Configuration(logDir);
                var logging = $"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}";
                configuration[$"{logging}:Json"] = "true";
                configuration[$"{logging}:LogLevel"] = "Debug";
                configuration[$"{logging}:RabbitMQ:Enabled"] = "true";
                using var services = RabbitServices();
                var sink = new CollectingSink();
                var loggerConfiguration = new LoggerConfiguration();
                BackFillerFileLogging.ConfigureLogger(
                    loggerConfiguration,
                    configuration,
                    commandLine: commandLine,
                    services: services);
                loggerConfiguration.WriteTo.Sink(sink);
                string rabbit;
                using (var logger = loggerConfiguration.CreateLogger())
                {
                    logger.ForContext(Constants.SourceContextPropertyName, category).Debug(marker);
                    var rabbitSink = Assert.Single(Sinks(logger).OfType<RabbitMqLogEventSink>());
                    using var writer = new StringWriter();
                    var probe = SourceContextEvent(category, marker);
                    probe.AddOrUpdateProperty(new LogEventProperty("Application", new ScalarValue("json-probe-application")));
                    rabbitSink.Formatter.Format(probe, writer);
                    rabbit = writer.ToString();
                    Assert.True(probe.Properties.ContainsKey(Constants.SourceContextPropertyName));
                }

                var evt = Assert.Single(sink.Events);
                return (
                    File.ReadAllText(Directory.GetFiles(logDir, "*.log").Single()),
                    rabbit,
                    evt.Properties.ContainsKey(Constants.SourceContextPropertyName));
            }
            finally
            {
                Log.CloseAndFlush();
                TryDelete(logDir);
            }
        }

        /// <summary>Captures bootstrap logger console output before full host configuration.</summary>
        /// <param name="commandLine">Logging command-line switches.</param>
        /// <param name="category">Source context for the probe event.</param>
        /// <param name="marker">Information marker text.</param>
        /// <returns>Console text from the bootstrap logger.</returns>
        private static string RenderBootstrap(BackFillerLoggingCommandLine commandLine, string category, string marker)
        {
            using var captured = new StringWriter();
            var previous = Console.Out;
            try
            {
                Console.SetOut(captured);
                var logger = BackFillerLoggingExtensions.CreateBootstrapLogger(
                    new BackFillerLoggingCommandLine(Console: true, commandLine.EnrichFromLogContext));
                string text;
                using ((IDisposable)logger)
                {
                    logger.ForContext(Constants.SourceContextPropertyName, category).Information(marker);
                    text = captured.ToString();
                }

                return text;
            }
            finally
            {
                Console.SetOut(previous);
                Log.CloseAndFlush();
            }
        }

        /// <summary>Emits one UDP syslog packet and records whether SourceContext was on the emitted log event.</summary>
        /// <param name="commandLine">Logging command-line switches.</param>
        /// <param name="category">Source context category.</param>
        /// <param name="json">When true, enables JSON formatting for the syslog target.</param>
        /// <returns>UTF-8 packet text and SourceContext presence on the root event.</returns>
        private static async Task<(string Packet, bool RootEventHasSourceContext)> ReadSyslogPacketAsync(
            BackFillerLoggingCommandLine commandLine,
            string category,
            bool json = false)
        {
            var logDir = NewLogDir();
            try
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
                var configuration = Configuration(logDir);
                var logging = $"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}";
                configuration[$"{logging}:LogLevel"] = "Debug";
                if (json)
                {
                    configuration[$"{logging}:Json"] = "true";
                }

                var section = $"{logging}:Syslog";
                configuration[$"{section}:Enabled"] = "true";
                configuration[$"{section}:Host"] = "127.0.0.1";
                configuration[$"{section}:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                configuration[$"{section}:Protocol"] = "Udp";
                var sink = new CollectingSink();
                var loggerConfiguration = new LoggerConfiguration();
                BackFillerFileLogging.ConfigureLogger(
                    loggerConfiguration,
                    configuration,
                    commandLine: commandLine);
                loggerConfiguration.WriteTo.Sink(sink);
                using var logger = loggerConfiguration.CreateLogger();
                logger.ForContext(Constants.SourceContextPropertyName, category)
                    .Debug("syslog-source-context-marker");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var packet = await udp.ReceiveAsync(timeout.Token);
                var evt = Assert.Single(sink.Events);
                return (
                    Encoding.UTF8.GetString(packet.Buffer),
                    evt.Properties.ContainsKey(Constants.SourceContextPropertyName));
            }
            finally
            {
                Log.CloseAndFlush();
                TryDelete(logDir);
            }
        }

        /// <summary>Builds a debug log event with SourceContext for Rabbit formatter probes.</summary>
        /// <param name="category">Source context value.</param>
        /// <param name="marker">Message template text.</param>
        /// <returns>Log event passed to formatters.</returns>
        private static LogEvent SourceContextEvent(string category, string marker) =>
            new(
                DateTimeOffset.UtcNow,
                LogEventLevel.Debug,
                exception: null,
                new Serilog.Parsing.MessageTemplateParser().Parse(marker),
                [new LogEventProperty(Constants.SourceContextPropertyName, new ScalarValue(category))]);

        /// <summary>Returns whether LogContext properties reach the configured logger when enrich switches demand it.</summary>
        /// <param name="commandLine">Logging command-line switches.</param>
        /// <returns>True when the captured event includes the pushed ProbeToken property.</returns>
        private static bool EventHasProbe(BackFillerLoggingCommandLine commandLine)
        {
            var logDir = NewLogDir();
            try
            {
                var sink = new CollectingSink();
                var configuration = new LoggerConfiguration();
                BackFillerFileLogging.ConfigureLogger(configuration, Configuration(logDir), commandLine: commandLine);
                configuration.WriteTo.Sink(sink);
                using var logger = configuration.CreateLogger();
                using (LogContext.PushProperty("ProbeToken", "probe-value"))
                {
                    logger.Information("context-marker");
                }

                var evt = Assert.Single(sink.Events);
                return evt.Properties.ContainsKey("ProbeToken");
            }
            finally
            {
                Log.CloseAndFlush();
                TryDelete(logDir);
            }
        }

        /// <summary>In-memory configuration pointing file logging at a temp directory.</summary>
        /// <param name="logDir">Directory for rolling file output.</param>
        /// <returns>Configuration manager for ConfigureLogger calls.</returns>
        private static ConfigurationManager Configuration(string logDir)
        {
            using var configuration = new ConfigurationManager();
            configuration[$"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:File:{nameof(BackFillerFileLoggingTargetOptions.LogDir)}"] = logDir;
            return configuration;
        }

        /// <summary>Creates a production-configured Serilog logger from in-memory settings.</summary>
        /// <param name="configuration">BackFiller configuration.</param>
        /// <param name="commandLine">Logging command-line switches.</param>
        /// <param name="services">Optional DI services (for Rabbit sink).</param>
        /// <returns>Root logger for target tests.</returns>
        private static Logger Build(
            IConfiguration configuration,
            BackFillerLoggingCommandLine commandLine = default,
            IServiceProvider? services = null)
        {
            var loggerConfiguration = new LoggerConfiguration();
            BackFillerFileLogging.ConfigureLogger(
                loggerConfiguration,
                configuration,
                commandLine: commandLine,
                services: services);
            return loggerConfiguration.CreateLogger();
        }

        /// <summary>Service provider with RabbitMQ marked unavailable so sink wiring can be exercised safely.</summary>
        /// <returns>Built service provider owning the fake Rabbit service.</returns>
        private static ServiceProvider RabbitServices()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IRabbitMqService>(new UnavailableRabbitMqService());
            return services.BuildServiceProvider();
        }

        /// <summary>Depth-first walk of Serilog sink object graphs for introspection tests.</summary>
        /// <param name="root">Logger or sink root object.</param>
        /// <returns>Unique sink objects reachable from fields.</returns>
        private static IEnumerable<object> Sinks(object root)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<object>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!seen.Add(current))
                {
                    continue;
                }

                yield return current;
                foreach (var field in current.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    object? value;
                    try
                    {
                        value = field.GetValue(current);
                    }
                    catch (TargetInvocationException)
                    {
                        continue;
                    }

                    if (value is null or string or ValueType)
                    {
                        continue;
                    }

                    pending.Push(value);
                    if (value is IEnumerable enumerable)
                    {
                        // A sink may drain its event queue on another thread while this walk reads it.
                        var snapshot = new List<object>();
                        try
                        {
                            snapshot.AddRange(enumerable.OfType<object>().Where(
                                static inner => inner is not string and not ValueType));
                        }
                        catch (InvalidOperationException)
                        {
                            snapshot.Clear();
                        }

                        foreach (var inner in snapshot)
                        {
                            pending.Push(inner);
                        }
                    }
                }
            }
        }

        /// <summary>Allocates a unique temp directory name for isolated file logging runs.</summary>
        /// <returns>Path under the system temp folder (not created).</returns>
        private static string NewLogDir()
        {
            var dir = Path.Join(Path.GetTempPath(), "vectornntp-bf-target-" + Guid.NewGuid().ToString("N"));
            return dir;
        }

        /// <summary>Best-effort recursive delete of a temp log directory; ignores files still in use.</summary>
        /// <param name="path">Directory to remove.</param>
        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
                /* A directory that is still in use must not fail the test. */
            }
        }

        /// <summary>RabbitMQ service double that never offers a connection handle.</summary>
        private sealed class UnavailableRabbitMqService : IRabbitMqService
        {
            /// <inheritdoc />
            public bool IsReady => false;

            /// <inheritdoc />
            public long ConnectionGeneration => 0;

            /// <inheritdoc />
            public event EventHandler<RabbitMqConnectionReplacedEventArgs>? ConnectionReplaced
            {
                add { }
                remove { }
            }

            /// <inheritdoc />
            public bool TryGetCurrent(out RabbitMqConnectionHandle handle)
            {
                handle = default;
                return false;
            }
        }

        /// <summary>In-memory Serilog sink that records emitted events for assertions.</summary>
        private sealed class CollectingSink : ILogEventSink
        {
            /// <summary>Events in emission order. The tests emit on one thread, so this list is not synchronized.</summary>
            private readonly List<LogEvent> _events = [];

            /// <summary>Events captured in emission order.</summary>
            internal IReadOnlyList<LogEvent> Events => _events;

            /// <inheritdoc />
            public void Emit(LogEvent logEvent)
            {
                ArgumentNullException.ThrowIfNull(logEvent);
                _events.Add(logEvent);
            }
        }
    }
}

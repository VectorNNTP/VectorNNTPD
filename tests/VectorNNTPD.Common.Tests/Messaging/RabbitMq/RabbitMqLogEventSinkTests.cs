using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Parsing;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Tests.Logging;

namespace VectorNNTPD.Common.Tests.Messaging.RabbitMq
{
    /// <summary>
    /// Verifies <see cref="RabbitMqLogEventSink"/> publication metadata, channel replacement after a simulated connection loss, publish-failure self-log behavior, re-entrant drops, and skipping when the service has not been started.
    /// </summary>
    public sealed class RabbitMqLogEventSinkTests
    {
        /// <summary>
        /// Password assigned to the harness options and checked for absence from publication descriptions and Serilog self-log text.
        /// </summary>
        private const string SecretPassword = "rabbit-secret-value-not-in-logs";

        /// <summary>
        /// Asserts that two emits on a started service publish both formatter payloads through one connection and one channel, with distinct message ids and the sink's JSON metadata, and that the first publication's description omits the harness password and username.
        /// </summary>
        /// <remarks>
        /// Both publications use exchange <c>logs</c>, routing key <c>backfiller</c>, and the UTF-8 body <c>formatted-payload</c>.
        /// The first publication is persistent, not mandatory, has a blank expiration, app id <c>VectorNNTPD.Common.Tests</c>, content type <see cref="RabbitMqLogEventSink.JsonContentType"/>, content encoding <see cref="RabbitMqLogEventSink.Utf8ContentEncoding"/>, and the first event timestamp.
        /// <see cref="RabbitMqClientPublishChannel.CreateProperties(RabbitMqConfirmedPublication)"/> copies that message id, content type, content encoding, persistent delivery mode, app id, Unix-second timestamp, and blank expiration.
        /// </remarks>
        /// <returns>A task that completes when the assertions finish.</returns>
        [Fact]
        public async Task Emit_PublishesThroughTheCurrentConnection_WithFormatterAndMetadata()
        {
            await using var harness = await Harness.StartAsync();
            var formatter = new FixedFormatter("formatted-payload");
            var timestamp = DateTimeOffset.Parse("2026-10-02T12:00:00Z");
            using var sink = CreateSink(harness.Service, formatter);

            sink.Emit(Event(timestamp));
            sink.Emit(Event(timestamp.AddSeconds(1)));

            Assert.Equal(1, harness.Factory.ConnectCount);
            var channel = Assert.Single(harness.Factory.LastConnection!.PublishChannels);
            Assert.Equal(2, channel.Publications.Count);
            Assert.Equal(["logs", "logs"], channel.Publications.Select(static publication => publication.Exchange));
            Assert.Equal(["backfiller", "backfiller"], channel.Publications.Select(static publication => publication.RoutingKey));
            Assert.Equal(
                ["formatted-payload", "formatted-payload"],
                channel.Publications.Select(static publication => Encoding.UTF8.GetString(publication.Body.Span)));
            Assert.NotEqual(channel.Publications[0].MessageId, channel.Publications[1].MessageId);

            var first = channel.Publications[0];
            Assert.Equal(RabbitMqLogEventSink.JsonContentType, first.ContentType);
            Assert.Equal(RabbitMqLogEventSink.Utf8ContentEncoding, first.ContentEncoding);
            Assert.True(first.Persistent);
            Assert.False(first.Mandatory);
            Assert.True(string.IsNullOrWhiteSpace(first.ExpirationMilliseconds));
            Assert.Equal("VectorNNTPD.Common.Tests", first.AppId);
            Assert.Equal(timestamp, first.Timestamp);
            Assert.DoesNotContain(SecretPassword, Describe(first), StringComparison.Ordinal);
            Assert.DoesNotContain("nntparticles", Describe(first), StringComparison.Ordinal);

            var properties = RabbitMqClientPublishChannel.CreateProperties(first);
            Assert.Equal(first.MessageId, properties.MessageId);
            Assert.Equal(RabbitMqLogEventSink.JsonContentType, properties.ContentType);
            Assert.Equal(RabbitMqLogEventSink.Utf8ContentEncoding, properties.ContentEncoding);
            Assert.Equal(DeliveryModes.Persistent, properties.DeliveryMode);
            Assert.Equal("VectorNNTPD.Common.Tests", properties.AppId);
            Assert.Equal(timestamp.ToUnixTimeSeconds(), properties.Timestamp.UnixTime);
            Assert.True(string.IsNullOrWhiteSpace(properties.Expiration));
        }

        /// <summary>
        /// Asserts that after the first connection reports loss and the factory connects again, the next emit is published on the replacement connection, the first channel has been disposed once, and the first channel still holds only its original publication.
        /// </summary>
        /// <remarks>
        /// The test waits for <see cref="CountingConnectionFactory.Connected"/> before the second emit.
        /// It then expects connect count 2, a different connection at <c>Connections[1]</c>, and a single replacement publication whose body is <c>one</c>.
        /// </remarks>
        /// <returns>A task that completes when the assertions finish.</returns>
        [Fact]
        public async Task Emit_ReplacesTheChannelWhenTheConnectionGenerationChanges()
        {
            await using var harness = await Harness.StartAsync();
            using var sink = CreateSink(harness.Service, new FixedFormatter("one"));
            sink.Emit(Event(DateTimeOffset.UtcNow));
            var first = harness.Factory.LastConnection!;
            var firstChannel = Assert.Single(first.PublishChannels);

            var replaced = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Factory.Connected = replaced;
            first.SimulateLost();
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await replaced.Task.WaitAsync(safety.Token);

            sink.Emit(Event(DateTimeOffset.UtcNow));

            Assert.Equal(2, harness.Factory.ConnectCount);
            Assert.Equal(1, firstChannel.DisposeCount);
            var second = harness.Factory.Connections[1];
            Assert.NotSame(first, second);
            var secondChannel = Assert.Single(second.PublishChannels);
            Assert.Equal("one", Encoding.UTF8.GetString(Assert.Single(secondChannel.Publications).Body.Span));
            Assert.Single(firstChannel.Publications);
        }

        /// <summary>
        /// Asserts that <see cref="RabbitMqLogEventSink.Emit(LogEvent)"/> does not throw when the connection's publish path throws, that a replacement Serilog logger receives no events, and that Serilog self-log records publication failure without the harness password.
        /// </summary>
        /// <remarks>
        /// The connection's <see cref="LoggingConnection.PublishException"/> is an <see cref="InvalidOperationException"/> whose message is <c>publish failed</c>.
        /// Self-log text is required to contain <c>publication failed</c>.
        /// The previous <c>Serilog.Log.Logger</c> is restored and self-log is disabled after the assertions.
        /// </remarks>
        /// <returns>A task that completes when the assertions finish.</returns>
        [Fact]
        public async Task Emit_DoesNotThrow_WhenPublishFails_AndDoesNotLogThroughSerilog()
        {
            await using var harness = await Harness.StartAsync();
            harness.Factory.LastConnection!.PublishException = new InvalidOperationException("publish failed");
            using var sink = CreateSink(harness.Service, new FixedFormatter("payload"));
            var selfLog = new StringWriter();
            await SelfLogCapture.RunAsync(() =>
            {
                SelfLog.Enable(selfLog);
                var probe = new List<LogEvent>();
                var previous = Serilog.Log.Logger;
                Serilog.Log.Logger = new Serilog.LoggerConfiguration().WriteTo.Sink(new ProbeSink(probe)).CreateLogger();
                try
                {
                    var error = Record.Exception(() => sink.Emit(Event(DateTimeOffset.UtcNow)));
                    Assert.Null(error);
                    Assert.Empty(probe);
                    Assert.Contains("publication failed", selfLog.ToString(), StringComparison.Ordinal);
                    Assert.DoesNotContain(SecretPassword, selfLog.ToString(), StringComparison.Ordinal);
                }
                finally
                {
                    Serilog.Log.CloseAndFlush();
                    Serilog.Log.Logger = previous;
                    SelfLog.Disable();
                }

                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Asserts that a publication callback which calls <see cref="RabbitMqLogEventSink.Emit(LogEvent)"/> again records only the outer publication, and that Serilog self-log contains <c>re-entrant</c>.
        /// </summary>
        /// <returns>A task that completes when the assertions finish.</returns>
        [Fact]
        public async Task Emit_DropsReentrantEvents()
        {
            await using var harness = await Harness.StartAsync();
            RabbitMqLogEventSink? sink = null;
            harness.Factory.LastConnection!.OnPublish = () => sink!.Emit(Event(DateTimeOffset.UtcNow));
            sink = CreateSink(harness.Service, new FixedFormatter("outer"));
            using var _ = sink;
            var selfLog = new StringWriter();
            await SelfLogCapture.RunAsync(() =>
            {
                SelfLog.Enable(selfLog);
                try
                {
                    sink.Emit(Event(DateTimeOffset.UtcNow));
                    Assert.Single(Assert.Single(harness.Factory.LastConnection.PublishChannels).Publications);
                    Assert.Contains("re-entrant", selfLog.ToString(), StringComparison.Ordinal);
                }
                finally
                {
                    SelfLog.Disable();
                }

                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Asserts that <see cref="RabbitMqLogEventSink.Emit(LogEvent)"/> does not throw when the service was not started, that the factory connect count stays 0, and that Serilog self-log contains <c>not ready</c>.
        /// </summary>
        /// <returns>A task that completes when the assertions finish.</returns>
        [Fact]
        public async Task Emit_SkipsWhenTheConnectionIsNotReady()
        {
            await using var harness = await Harness.StartAsync(start: false);
            using var sink = CreateSink(harness.Service, new FixedFormatter("skipped"));
            var selfLog = new StringWriter();
            await SelfLogCapture.RunAsync(() =>
            {
                SelfLog.Enable(selfLog);
                try
                {
                    var error = Record.Exception(() => sink.Emit(Event(DateTimeOffset.UtcNow)));
                    Assert.Null(error);
                    Assert.Equal(0, harness.Factory.ConnectCount);
                    Assert.Contains("not ready", selfLog.ToString(), StringComparison.Ordinal);
                }
                finally
                {
                    SelfLog.Disable();
                }

                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Constructs a sink for <paramref name="service"/> using exchange <c>logs</c>, routing key <c>backfiller</c>, app id <c>VectorNNTPD.Common.Tests</c>, <paramref name="formatter"/>, and <see cref="RabbitMqLogEventSink.JsonContentType"/>.
        /// </summary>
        /// <param name="service">Process connection owner passed to the sink.</param>
        /// <param name="formatter">Formatter the sink uses for each event payload.</param>
        /// <returns>The constructed sink.</returns>
        private static RabbitMqLogEventSink CreateSink(IRabbitMqService service, ITextFormatter formatter) =>
            new(service, "logs", "backfiller", "VectorNNTPD.Common.Tests", formatter, RabbitMqLogEventSink.JsonContentType);

        /// <summary>
        /// Builds an information-level event at <paramref name="timestamp"/> with a null exception, message template <c>marker</c>, and no properties.
        /// </summary>
        /// <param name="timestamp">Timestamp stored on the event.</param>
        /// <returns>The constructed event.</returns>
        private static LogEvent Event(DateTimeOffset timestamp) =>
            new(
                timestamp,
                LogEventLevel.Information,
                exception: null,
                messageTemplate: new MessageTemplate("marker", []),
                properties: []);

        /// <summary>
        /// Joins the publication's exchange, routing key, message id, app id, content type, content encoding, correlation id, request-id header, expiration, and UTF-8 body with newline characters.
        /// </summary>
        /// <param name="publication">Publication whose fields are concatenated.</param>
        /// <returns>The concatenated field text.</returns>
        private static string Describe(RabbitMqConfirmedPublication publication) =>
            string.Join(
                '\n',
                publication.Exchange,
                publication.RoutingKey,
                publication.MessageId,
                publication.AppId,
                publication.ContentType,
                publication.ContentEncoding,
                publication.CorrelationId,
                publication.RequestIdHeader,
                publication.ExpirationMilliseconds,
                Encoding.UTF8.GetString(publication.Body.Span));

        /// <summary>
        /// Formatter that writes <paramref name="text"/> and ignores the log event after the null checks.
        /// </summary>
        /// <param name="text">Characters written to the output writer.</param>
        private sealed class FixedFormatter(string text) : ITextFormatter
        {
            /// <summary>
            /// Writes the constructor text to <paramref name="output"/>.
            /// </summary>
            /// <param name="logEvent">Event required to be non-null. Its contents are not written.</param>
            /// <param name="output">Writer that receives the constructor text.</param>
            /// <exception cref="ArgumentNullException">Thrown when <paramref name="logEvent"/> or <paramref name="output"/> is <see langword="null"/>.</exception>
            public void Format(LogEvent logEvent, TextWriter output)
            {
                ArgumentNullException.ThrowIfNull(logEvent);
                ArgumentNullException.ThrowIfNull(output);
                output.Write(text);
            }
        }

        /// <summary>
        /// Serilog sink that appends each emitted event to <paramref name="events"/>.
        /// </summary>
        /// <param name="events">List that receives events from <see cref="Emit(LogEvent)"/>.</param>
        private sealed class ProbeSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
        {
            /// <summary>
            /// Appends <paramref name="logEvent"/> to the constructor list.
            /// </summary>
            /// <param name="logEvent">Event to record.</param>
            public void Emit(LogEvent logEvent) => events.Add(logEvent);
        }

        /// <summary>
        /// Owns the <see cref="RabbitMqService"/> and <see cref="CountingConnectionFactory"/> used by these tests, and disposes the service.
        /// </summary>
        private sealed class Harness : IAsyncDisposable
        {
            /// <summary>
            /// Stores the service and factory this harness exposes.
            /// </summary>
            /// <param name="service">Service assigned to <see cref="Service"/>.</param>
            /// <param name="factory">Factory assigned to <see cref="Factory"/>.</param>
            private Harness(RabbitMqService service, CountingConnectionFactory factory)
            {
                Service = service;
                Factory = factory;
            }

            /// <summary>
            /// Service constructed for the test.
            /// </summary>
            /// <value>The <see cref="RabbitMqService"/> passed to the constructor.</value>
            public RabbitMqService Service { get; }

            /// <summary>
            /// Factory passed to <see cref="Service"/>.
            /// </summary>
            /// <value>The <see cref="CountingConnectionFactory"/> passed to the constructor.</value>
            public CountingConnectionFactory Factory { get; }

            /// <summary>
            /// Builds connectivity-only options, a counting factory, and a <see cref="RabbitMqService"/>, and starts that service when <paramref name="start"/> is <see langword="true"/>.
            /// </summary>
            /// <param name="start">
            /// <see langword="true"/> to call <see cref="RabbitMqService.StartAsync(CancellationToken)"/> before returning.
            /// The default is <see langword="true"/>. <see langword="false"/> leaves the service unstarted.
            /// </param>
            /// <returns>A harness holding the constructed service and factory.</returns>
            /// <remarks>
            /// The options use username <c>nntparticles</c>, password <see cref="SecretPassword"/>, and pool reconnect delays of 1 millisecond.
            /// The service uses a connection-name provider that returns <c>VectorNNTPD.Common.Tests:unit</c> and <see cref="NullLogger{T}.Instance"/>.
            /// </remarks>
            public static async Task<Harness> StartAsync(bool start = true)
            {
                var options = RabbitMqOptionsValidatorTests.CreateValidConnectivityOnly();
                options.Username = "nntparticles";
                options.Password = SecretPassword;
                options.PoolReconnectBaseDelayMs = 1;
                options.PoolReconnectMaxDelayMs = 1;
                var factory = new CountingConnectionFactory();
                var service = new RabbitMqService(
                    factory,
                    Options.Create(options),
                    new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTPD.Common.Tests:unit"),
                    NullLogger<RabbitMqService>.Instance);
                if (start)
                {
                    await service.StartAsync(CancellationToken.None);
                }

                return new Harness(service, factory);
            }

            /// <summary>
            /// Disposes <see cref="Service"/>.
            /// </summary>
            /// <returns>A task that completes when <see cref="Service"/> disposal completes.</returns>
            public async ValueTask DisposeAsync() => await Service.DisposeAsync();
        }

        /// <summary>
        /// Connection factory that counts connect calls, retains every connection, and can signal each connect through <see cref="Connected"/>.
        /// </summary>
        private sealed class CountingConnectionFactory : IRabbitMqConnectionFactory
        {
            /// <summary>
            /// Connect-call count incremented by <see cref="ConnectAsync"/> and read by <see cref="ConnectCount"/>.
            /// </summary>
            private int _connectCount;

            /// <summary>
            /// Number of <see cref="ConnectAsync"/> calls.
            /// </summary>
            /// <value>The connect count read with a volatile read of the backing field.</value>
            public int ConnectCount => Volatile.Read(ref _connectCount);

            /// <summary>
            /// Connection created by the most recent <see cref="ConnectAsync"/> call.
            /// </summary>
            /// <value>The latest connection, or <see langword="null"/> before the first connect.</value>
            public LoggingConnection? LastConnection { get; private set; }

            /// <summary>
            /// Connections created by <see cref="ConnectAsync"/>, in call order.
            /// </summary>
            /// <value>The list of created connections. It starts empty.</value>
            public List<LoggingConnection> Connections { get; } = [];

            /// <summary>
            /// Optional signal that <see cref="ConnectAsync"/> tries to complete with the new connect count.
            /// </summary>
            /// <value>The completion source assigned by a test, or <see langword="null"/> when no waiter is registered.</value>
            public TaskCompletionSource<int>? Connected { get; set; }

            /// <summary>
            /// Creates a <see cref="LoggingConnection"/>, copies publish failure and callback state from the previous connection, and records the new connection.
            /// </summary>
            /// <param name="options">Broker options supplied by the service. Not read.</param>
            /// <param name="connectionName">Connection name supplied by the service. Not read.</param>
            /// <param name="cancellationToken">Cancellation token supplied by the service. Not observed.</param>
            /// <returns>A completed task whose result is the new connection.</returns>
            /// <remarks>
            /// When <see cref="LastConnection"/> is already set, the new connection receives that connection's <see cref="LoggingConnection.PublishException"/> and <see cref="LoggingConnection.OnPublish"/>.
            /// <see cref="Connected"/> is completed with the incremented count when the property is not <see langword="null"/> and the source accepts the result.
            /// </remarks>
            public Task<IRabbitMqConnection> ConnectAsync(
                RabbitMqOptions options,
                string connectionName,
                CancellationToken cancellationToken)
            {
                var count = Interlocked.Increment(ref _connectCount);
                var connection = new LoggingConnection();
                if (LastConnection is not null)
                {
                    connection.PublishException = LastConnection.PublishException;
                    connection.OnPublish = LastConnection.OnPublish;
                }

                LastConnection = connection;
                Connections.Add(connection);
                Connected?.TrySetResult(count);
                return Task.FromResult<IRabbitMqConnection>(connection);
            }
        }

        /// <summary>
        /// Open connection double that records publish channels and can raise <see cref="ConnectionLost"/>.
        /// </summary>
        private sealed class LoggingConnection : IRabbitMqConnection
        {
            /// <summary>
            /// Whether this double currently reports the connection as open.
            /// </summary>
            /// <value>
            /// <see langword="true"/> until <see cref="SimulateLost"/> or <see cref="DisposeAsync"/> sets it to <see langword="false"/>.
            /// </value>
            public bool IsOpen { get; set; } = true;

            /// <summary>
            /// Host name reported by this double.
            /// </summary>
            /// <value>The literal <c>127.0.0.1</c>.</value>
            public string Host => "127.0.0.1";

            /// <summary>
            /// Port reported by this double.
            /// </summary>
            /// <value>The literal <c>5672</c>.</value>
            public int Port => 5672;

            /// <summary>
            /// Virtual host reported by this double.
            /// </summary>
            /// <value>The literal <c>/</c>.</value>
            public string VirtualHost => "/";

            /// <summary>
            /// Client-provided name reported by this double.
            /// </summary>
            /// <value>The literal <c>unit</c>.</value>
            public string ClientProvidedName => "unit";

            /// <summary>
            /// Exception thrown by <see cref="LoggingPublishChannel.PublishConfirmedAsync(RabbitMqConfirmedPublication, CancellationToken)"/> when set.
            /// </summary>
            /// <value>The exception to throw, or <see langword="null"/> when publication should be recorded.</value>
            public Exception? PublishException { get; set; }

            /// <summary>
            /// Callback invoked after a publication is recorded.
            /// </summary>
            /// <value>The callback, or <see langword="null"/> when no callback is registered.</value>
            public Action? OnPublish { get; set; }

            /// <summary>
            /// Publish channels created by <see cref="CreatePublishChannelAsync"/>.
            /// </summary>
            /// <value>The list of created channels. It starts empty.</value>
            public List<LoggingPublishChannel> PublishChannels { get; } = [];

            /// <summary>
            /// Raised by <see cref="SimulateLost"/> with reply code 320, reply text <c>lost</c>, and initiator <c>Peer</c>.
            /// </summary>
            public event EventHandler<RabbitMqConnectionLostEventArgs>? ConnectionLost;

            /// <summary>
            /// Sets <see cref="IsOpen"/> to <see langword="false"/> and invokes <see cref="ConnectionLost"/> when a handler is attached.
            /// </summary>
            public void SimulateLost()
            {
                IsOpen = false;
                ConnectionLost?.Invoke(this, new RabbitMqConnectionLostEventArgs(320, "lost", "Peer"));
            }

            /// <summary>
            /// Creates a <see cref="LoggingPublishChannel"/> for <paramref name="generation"/> and appends it to <see cref="PublishChannels"/>.
            /// </summary>
            /// <param name="generation">Generation stored on the new channel.</param>
            /// <param name="cancellationToken">Cancellation token supplied by the caller. Not observed.</param>
            /// <returns>A completed task whose result is the new channel.</returns>
            public Task<IRabbitMqPublishChannel> CreatePublishChannelAsync(long generation, CancellationToken cancellationToken)
            {
                var channel = new LoggingPublishChannel(generation, this);
                PublishChannels.Add(channel);
                return Task.FromResult<IRabbitMqPublishChannel>(channel);
            }

            /// <summary>
            /// Rejects topology-channel creation.
            /// </summary>
            /// <param name="cancellationToken">Cancellation token supplied by the caller. Not observed.</param>
            /// <returns>No task is returned.</returns>
            /// <exception cref="NotSupportedException">Thrown on every call.</exception>
            public Task<IRabbitMqTopologyChannel> CreateTopologyChannelAsync(CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            /// <summary>
            /// Rejects RPC-channel creation.
            /// </summary>
            /// <param name="generation">Generation supplied by the caller. Not used.</param>
            /// <param name="cancellationToken">Cancellation token supplied by the caller. Not observed.</param>
            /// <returns>No task is returned.</returns>
            /// <exception cref="NotSupportedException">Thrown on every call.</exception>
            public Task<IRabbitMqRpcChannel> CreateRpcChannelAsync(long generation, CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            /// <summary>
            /// Rejects manual-ack channel creation.
            /// </summary>
            /// <param name="generation">Generation supplied by the caller. Not used.</param>
            /// <param name="cancellationToken">Cancellation token supplied by the caller. Not observed.</param>
            /// <returns>No task is returned.</returns>
            /// <exception cref="NotSupportedException">Thrown on every call.</exception>
            public Task<IRabbitMqManualAckChannel> CreateManualAckChannelAsync(long generation, CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            /// <summary>
            /// Rejects async-confirm publish-channel creation.
            /// </summary>
            /// <param name="generation">Generation supplied by the caller. Not used.</param>
            /// <param name="cancellationToken">Cancellation token supplied by the caller. Not observed.</param>
            /// <returns>No task is returned.</returns>
            /// <exception cref="NotSupportedException">Thrown on every call.</exception>
            public Task<IRabbitMqAsyncConfirmPublishChannel> CreateAsyncConfirmPublishChannelAsync(
                long generation,
                CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            /// <summary>
            /// Sets <see cref="IsOpen"/> to <see langword="false"/>.
            /// </summary>
            /// <returns>A completed value task.</returns>
            public ValueTask DisposeAsync()
            {
                IsOpen = false;
                return ValueTask.CompletedTask;
            }
        }

        /// <summary>
        /// Publish channel that records confirmed publications for <paramref name="generation"/> on <paramref name="connection"/>.
        /// </summary>
        /// <param name="generation">Generation exposed by <see cref="Generation"/>.</param>
        /// <param name="connection">Connection whose <see cref="LoggingConnection.PublishException"/> and <see cref="LoggingConnection.OnPublish"/> this channel reads.</param>
        private sealed class LoggingPublishChannel(long generation, LoggingConnection connection) : IRabbitMqPublishChannel
        {
            /// <summary>
            /// Generation supplied when the channel was created.
            /// </summary>
            /// <value>The generation constructor argument.</value>
            public long Generation { get; } = generation;

            /// <summary>
            /// Whether this channel currently reports itself as open.
            /// </summary>
            /// <value>
            /// <see langword="true"/> until <see cref="DisposeAsync"/> sets it to <see langword="false"/>.
            /// </value>
            public bool IsOpen { get; private set; } = true;

            /// <summary>
            /// Number of <see cref="DisposeAsync"/> calls.
            /// </summary>
            /// <value>The dispose count. It starts at 0 and increases by one on each dispose.</value>
            public int DisposeCount { get; private set; }

            /// <summary>
            /// Publications recorded by <see cref="PublishConfirmedAsync(RabbitMqConfirmedPublication, CancellationToken)"/>.
            /// </summary>
            /// <value>The recorded publications. The list starts empty.</value>
            public List<RabbitMqConfirmedPublication> Publications { get; } = [];

            /// <summary>
            /// Rejects the scalar confirmed-publish overload.
            /// </summary>
            /// <param name="exchange">Exchange supplied by the caller. Not used.</param>
            /// <param name="routingKey">Routing key supplied by the caller. Not used.</param>
            /// <param name="messageId">Message id supplied by the caller. Not used.</param>
            /// <param name="appId">App id supplied by the caller. Not used.</param>
            /// <param name="expiration">Expiration supplied by the caller. Not used.</param>
            /// <param name="body">Body supplied by the caller. Not used.</param>
            /// <param name="cancellationToken">Cancellation token supplied by the caller. Not observed.</param>
            /// <returns>No task is returned.</returns>
            /// <exception cref="NotSupportedException">Thrown on every call.</exception>
            public Task PublishConfirmedAsync(
                string exchange,
                string routingKey,
                string messageId,
                string appId,
                string expiration,
                ReadOnlyMemory<byte> body,
                CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            /// <summary>
            /// Throws <see cref="LoggingConnection.PublishException"/> when it is set; otherwise records <paramref name="publication"/> and invokes <see cref="LoggingConnection.OnPublish"/> when that callback is set.
            /// </summary>
            /// <param name="publication">Publication appended to <see cref="Publications"/> when no publish exception is set.</param>
            /// <param name="cancellationToken">Cancellation token supplied by the caller. Not observed.</param>
            /// <returns>A completed task when the publication is recorded.</returns>
            /// <exception cref="Exception">
            /// Thrown when <see cref="LoggingConnection.PublishException"/> is not <see langword="null"/>.
            /// The thrown instance is that stored exception.
            /// </exception>
            public Task PublishConfirmedAsync(RabbitMqConfirmedPublication publication, CancellationToken cancellationToken)
            {
                if (connection.PublishException is not null)
                {
                    throw connection.PublishException;
                }

                Publications.Add(publication);
                connection.OnPublish?.Invoke();
                return Task.CompletedTask;
            }

            /// <summary>
            /// Increments <see cref="DisposeCount"/> and sets <see cref="IsOpen"/> to <see langword="false"/>.
            /// </summary>
            /// <returns>A completed value task.</returns>
            public ValueTask DisposeAsync()
            {
                DisposeCount++;
                IsOpen = false;
                return ValueTask.CompletedTask;
            }
        }
    }
}

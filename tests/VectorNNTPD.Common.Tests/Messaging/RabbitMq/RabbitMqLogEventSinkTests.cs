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
            await sink.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

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
            await sink.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var first = harness.Factory.LastConnection!;
            var firstChannel = Assert.Single(first.PublishChannels);

            var replaced = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Factory.Connected = replaced;
            first.SimulateLost();
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await replaced.Task.WaitAsync(safety.Token);

            sink.Emit(Event(DateTimeOffset.UtcNow));
            await sink.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

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
            using var selfLog = new StringWriter();
            await SelfLogCapture.RunAsync(async () =>
            {
                SelfLog.Enable(selfLog);
                var probe = new List<LogEvent>();
                var previous = Serilog.Log.Logger;
                Serilog.Log.Logger = new Serilog.LoggerConfiguration().WriteTo.Sink(new ProbeSink(probe)).CreateLogger();
                try
                {
                    var error = Record.Exception(() => sink.Emit(Event(DateTimeOffset.UtcNow)));
                    Assert.Null(error);
                    await sink.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
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
            });
        }

        /// <summary>
        /// Asserts that a formatter which calls <see cref="RabbitMqLogEventSink.Emit(LogEvent)"/> again is dropped as re-entrant,
        /// while the outer formatted payload is still published once.
        /// </summary>
        /// <returns>A task that completes when the assertions finish.</returns>
        [Fact]
        public async Task Emit_DropsReentrantFormatterCalls()
        {
            await using var harness = await Harness.StartAsync();
            RabbitMqLogEventSink? sink = null;
            var formatter = new ReentrantFormatter(() => sink!.Emit(Event(DateTimeOffset.UtcNow)), "outer");
            sink = CreateSink(harness.Service, formatter);
            using var _ = sink;
            using var selfLog = new StringWriter();
            await SelfLogCapture.RunAsync(async () =>
            {
                SelfLog.Enable(selfLog);
                try
                {
                    sink.Emit(Event(DateTimeOffset.UtcNow));
                    await sink.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal("outer", Encoding.UTF8.GetString(Assert.Single(harness.Factory.LastConnection!.PublishChannels).Publications.Single().Body.Span));
                    Assert.Contains("re-entrant", selfLog.ToString(), StringComparison.Ordinal);
                }
                finally
                {
                    SelfLog.Disable();
                }
            });
        }

        /// <summary>
        /// A synchronous publish callback that calls <see cref="RabbitMqLogEventSink.Emit(LogEvent)"/> is accepted
        /// rather than misclassified as re-entrant, because the worker does not elevate the emit-depth guard.
        /// </summary>
        [Fact]
        public async Task Emit_FromPublishCallback_IsAccepted()
        {
            await using var harness = await Harness.StartAsync();
            RabbitMqLogEventSink? sink = null;
            var nestedEmits = 0;
            harness.Factory.LastConnection!.OnPublish = () =>
            {
                if (Interlocked.Increment(ref nestedEmits) == 1)
                {
                    sink!.Emit(Event(DateTimeOffset.UtcNow));
                }
            };
            sink = CreateSink(harness.Service, new FixedFormatter("callback"));
            using var _ = sink;

            sink.Emit(Event(DateTimeOffset.UtcNow));
            await sink.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, harness.Factory.LastConnection.TotalPublications);
            Assert.Equal(2, nestedEmits);
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
            using var selfLog = new StringWriter();
            await SelfLogCapture.RunAsync(async () =>
            {
                SelfLog.Enable(selfLog);
                try
                {
                    var error = Record.Exception(() => sink.Emit(Event(DateTimeOffset.UtcNow)));
                    Assert.Null(error);
                    await sink.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal(0, harness.Factory.ConnectCount);
                    Assert.Contains("not ready", selfLog.ToString(), StringComparison.Ordinal);
                }
                finally
                {
                    SelfLog.Disable();
                }
            });
        }

        /// <summary>Concurrent Emit calls are accepted and all publish when capacity remains.</summary>
        [Fact]
        public async Task Emit_AcceptsConcurrentCallers_WithoutDroppingWhenCapacityRemains()
        {
            await using var harness = await Harness.StartAsync();
            using var sink = CreateSink(harness.Service, new FixedFormatter("concurrent"), queueCapacity: 64);
            const int count = 32;
            var published = harness.Factory.LastConnection!.WaitForPublicationTotalAsync(count);
            await Task.WhenAll(Enumerable.Range(0, count).Select(_ => Task.Run(() => sink.Emit(Event(DateTimeOffset.UtcNow)))));
            await published.WaitAsync(TimeSpan.FromSeconds(5));
            await sink.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, sink.DroppedCount);
            Assert.Equal(count, harness.Factory.LastConnection.TotalPublications);
        }

        /// <summary>Emit returns while a publisher confirm remains incomplete.</summary>
        [Fact]
        public async Task Emit_DoesNotBlockCaller_WhileConfirmIsPending()
        {
            await using var harness = await Harness.StartAsync();
            var confirm = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Factory.LastConnection!.ConfirmGate = confirm;
            harness.Factory.LastConnection.OnPublish = () => entered.TrySetResult();
            using var sink = CreateSink(harness.Service, new FixedFormatter("pending"));

            sink.Emit(Event(DateTimeOffset.UtcNow));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(Assert.Single(harness.Factory.LastConnection.PublishChannels).Publications);

            confirm.TrySetResult();
            await sink.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(Assert.Single(harness.Factory.LastConnection.PublishChannels).Publications);
        }

        /// <summary>A full bounded queue rejects the newest event, keeps queued items, and rate-limits SelfLog.</summary>
        [Fact]
        public async Task Emit_DropsOverflow_WhenQueueIsFull_AndRateLimitsDiagnostics()
        {
            await using var harness = await Harness.StartAsync();
            var confirm = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Factory.LastConnection!.ConfirmGate = confirm;
            harness.Factory.LastConnection.OnPublish = () => firstEntered.TrySetResult();
            const int capacity = 4;
            using var sink = CreateSink(harness.Service, new FixedFormatter("overflow"), queueCapacity: capacity);
            Assert.Equal(capacity, sink.QueueCapacity);
            using var selfLog = new StringWriter();
            await SelfLogCapture.RunAsync(async () =>
            {
                SelfLog.Enable(selfLog);
                try
                {
                    sink.Emit(Event(DateTimeOffset.UtcNow));
                    await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal(0, sink.DroppedCount);

                    for (var i = 0; i < capacity; i++)
                    {
                        sink.Emit(Event(DateTimeOffset.UtcNow));
                    }

                    Assert.Equal(0, sink.DroppedCount);

                    for (var i = 0; i < RabbitMqLogEventSink.DropDiagnosticInterval + 1; i++)
                    {
                        sink.Emit(Event(DateTimeOffset.UtcNow));
                    }

                    Assert.Equal(RabbitMqLogEventSink.DropDiagnosticInterval + 1, sink.DroppedCount);
                    var diagnostics = selfLog.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Count(static line => line.Contains("queue is full", StringComparison.Ordinal));
                    Assert.Equal(2, diagnostics);

                    confirm.TrySetResult();
                    await sink.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal(capacity + 1, harness.Factory.LastConnection.TotalPublications);
                }
                finally
                {
                    confirm.TrySetResult();
                    SelfLog.Disable();
                }
            });
        }

        /// <summary>Two sink instances do not share re-entrancy depth; concurrent emits on each remain independent.</summary>
        [Fact]
        public async Task Emit_ReentrancyGuard_IsIsolatedPerSinkInstance()
        {
            await using var harness = await Harness.StartAsync();
            using var first = CreateSink(harness.Service, new FixedFormatter("first"));
            using var second = CreateSink(harness.Service, new FixedFormatter("second"));
            RabbitMqLogEventSink? active = null;
            var formatter = new ReentrantFormatter(() => active!.Emit(Event(DateTimeOffset.UtcNow)), "outer");
            using var recursive = CreateSink(harness.Service, formatter);
            active = recursive;

            first.Emit(Event(DateTimeOffset.UtcNow));
            second.Emit(Event(DateTimeOffset.UtcNow));
            recursive.Emit(Event(DateTimeOffset.UtcNow));
            await first.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await second.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await recursive.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(harness.Factory.LastConnection!.TotalPublications >= 3);
        }

        /// <summary>A failed publication does not stop the worker from publishing a later event.</summary>
        [Fact]
        public async Task Emit_ContinuesAfterTransientPublishFailure()
        {
            await using var harness = await Harness.StartAsync();
            harness.Factory.LastConnection!.PublishException = new InvalidOperationException("publish failed");
            using var sink = CreateSink(harness.Service, new FixedFormatter("retry"));
            sink.Emit(Event(DateTimeOffset.UtcNow));
            await sink.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            harness.Factory.LastConnection.PublishException = null;
            sink.Emit(Event(DateTimeOffset.UtcNow));
            await sink.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("retry", Encoding.UTF8.GetString(Assert.Single(harness.Factory.LastConnection.PublishChannels).Publications.Single().Body.Span));
        }

        /// <summary>Dispose rejects new events and disposes the cached channel once.</summary>
        [Fact]
        public async Task Dispose_RejectsNewEvents_AndDisposesChannelOnce()
        {
            await using var harness = await Harness.StartAsync();
            using var sink = CreateSink(harness.Service, new FixedFormatter("shutdown"));
            sink.Emit(Event(DateTimeOffset.UtcNow));
            await sink.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var channel = Assert.Single(harness.Factory.LastConnection!.PublishChannels);

            sink.Dispose();
            var ignored = Record.Exception(() => sink.Emit(Event(DateTimeOffset.UtcNow)));
            Assert.Null(ignored);
            Assert.Equal(1, channel.DisposeCount);
            Assert.Single(channel.Publications);
            sink.Dispose();
            Assert.Equal(1, channel.DisposeCount);
        }

        /// <summary>Dispose concurrent with Emit does not throw to the caller or leave the worker unobserved.</summary>
        [Fact]
        public async Task Dispose_RacesWithEmit_WithoutThrowingToCaller()
        {
            await using var harness = await Harness.StartAsync();
            using var sink = CreateSink(harness.Service, new FixedFormatter("race"));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workers = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            {
                started.Task.GetAwaiter().GetResult();
                var error = Record.Exception(() => sink.Emit(Event(DateTimeOffset.UtcNow)));
                Assert.Null(error);
            })).ToArray();

            started.SetResult();
            var disposeError = Record.Exception(sink.Dispose);
            Assert.Null(disposeError);
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// An Emit that begins before Dispose must not throw when Dispose returns while formatting is still running.
        /// The re-entrancy guard must remain usable through Emit's finally (it is not disposed under active callers).
        /// </summary>
        [Fact]
        public async Task Dispose_DuringActiveEmit_DoesNotThrowFromReentrancyGuard()
        {
            await using var harness = await Harness.StartAsync();
            var enteredFormat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFormat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var formatter = new BlockingFormatter(enteredFormat, releaseFormat, "held");
            using var sink = CreateSink(harness.Service, formatter);

            var emitTask = Task.Run(() => Record.Exception(() => sink.Emit(Event(DateTimeOffset.UtcNow))));
            await enteredFormat.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var disposeError = Record.Exception(sink.Dispose);
            Assert.Null(disposeError);

            releaseFormat.TrySetResult();
            var emitError = await emitTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(emitError);
        }

        /// <summary>
        /// When a confirm ignores cancellation, Dispose returns after the shutdown bound without disposing the
        /// channel under the worker; releasing the confirm later lets the worker dispose the channel once.
        /// </summary>
        [Fact]
        public async Task Dispose_UncooperativeConfirm_DoesNotDisposeChannelWhileInFlight()
        {
            await using var harness = await Harness.StartAsync();
            var confirm = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Factory.LastConnection!.ConfirmGate = confirm;
            harness.Factory.LastConnection.IgnoreConfirmCancellation = true;
            harness.Factory.LastConnection.OnPublish = () => entered.TrySetResult();
            using var sink = CreateSink(harness.Service, new FixedFormatter("stuck"));
            try
            {
                sink.Emit(Event(DateTimeOffset.UtcNow));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var channel = Assert.Single(harness.Factory.LastConnection.PublishChannels);
                Assert.Equal(0, channel.DisposeCount);

                var disposeTask = Task.Run(sink.Dispose);
                await disposeTask.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, channel.DisposeCount);

                confirm.TrySetResult();
                using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (channel.DisposeCount == 0)
                {
                    await Task.Delay(10, safety.Token);
                }

                Assert.Equal(1, channel.DisposeCount);
            }
            finally
            {
                confirm.TrySetResult();
            }
        }

        /// <summary>
        /// Constructs a sink for <paramref name="service"/> using exchange <c>logs</c>, routing key <c>backfiller</c>, app id <c>VectorNNTPD.Common.Tests</c>, <paramref name="formatter"/>, and <see cref="RabbitMqLogEventSink.JsonContentType"/>.
        /// </summary>
        /// <param name="service">Process connection owner passed to the sink.</param>
        /// <param name="formatter">Formatter the sink uses for each event payload.</param>
        /// <param name="queueCapacity">Bounded queue capacity forwarded to the sink.</param>
        /// <returns>The constructed sink.</returns>
        private static RabbitMqLogEventSink CreateSink(
            IRabbitMqService service,
            ITextFormatter formatter,
            int queueCapacity = RabbitMqLogEventSink.DefaultQueueCapacity) =>
            new(service, "logs", "backfiller", "VectorNNTPD.Common.Tests", formatter, RabbitMqLogEventSink.JsonContentType, queueCapacity);

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
        /// Formatter that invokes <paramref name="reenter"/> once before writing <paramref name="text"/>.
        /// </summary>
        /// <param name="reenter">Callback invoked from <see cref="Format"/> to exercise same-thread re-entrancy.</param>
        /// <param name="text">Characters written after the re-entrant callback.</param>
        private sealed class ReentrantFormatter(Action reenter, string text) : ITextFormatter
        {
            /// <summary>True after the first re-entrant callback so formatting cannot recurse unboundedly.</summary>
            private int _reentered;

            /// <inheritdoc />
            public void Format(LogEvent logEvent, TextWriter output)
            {
                ArgumentNullException.ThrowIfNull(logEvent);
                ArgumentNullException.ThrowIfNull(output);
                if (Interlocked.Exchange(ref _reentered, 1) == 0)
                {
                    reenter();
                }

                output.Write(text);
            }
        }

        /// <summary>
        /// Formatter that signals <paramref name="entered"/> then blocks until <paramref name="release"/> completes before writing.
        /// </summary>
        private sealed class BlockingFormatter(
            TaskCompletionSource entered,
            TaskCompletionSource release,
            string text) : ITextFormatter
        {
            /// <inheritdoc />
            public void Format(LogEvent logEvent, TextWriter output)
            {
                ArgumentNullException.ThrowIfNull(logEvent);
                ArgumentNullException.ThrowIfNull(output);
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
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
                    connection.ConfirmGate = LastConnection.ConfirmGate;
                    connection.IgnoreConfirmCancellation = LastConnection.IgnoreConfirmCancellation;
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
            /// Callback invoked after a publication is recorded and before the confirm gate is awaited.
            /// </summary>
            /// <value>The callback, or <see langword="null"/> when no callback is registered.</value>
            public Action? OnPublish { get; set; }

            /// <summary>
            /// When set, <see cref="LoggingPublishChannel.PublishConfirmedAsync(RabbitMqConfirmedPublication, CancellationToken)"/>
            /// records the publication then waits for this gate.
            /// </summary>
            public TaskCompletionSource? ConfirmGate { get; set; }

            /// <summary>
            /// When <see langword="true"/>, the confirm gate wait ignores the publish cancellation token so disposal
            /// timeout behavior can be exercised.
            /// </summary>
            public bool IgnoreConfirmCancellation { get; set; }

            /// <summary>
            /// Publish channels created by <see cref="CreatePublishChannelAsync"/>.
            /// </summary>
            /// <value>The list of created channels. It starts empty.</value>
            public List<LoggingPublishChannel> PublishChannels { get; } = [];

            /// <summary>Total publications recorded across every channel on this connection.</summary>
            public int TotalPublications
            {
                get
                {
                    lock (_publicationGate)
                    {
                        return _totalPublications;
                    }
                }
            }

            /// <summary>Protects <see cref="_totalPublications"/> and publication waiters.</summary>
            private readonly object _publicationGate = new();

            /// <summary>Running total of recorded publications.</summary>
            private int _totalPublications;

            /// <summary>Waiters armed by <see cref="WaitForPublicationTotalAsync"/>.</summary>
            private readonly List<(int Count, TaskCompletionSource Source)> _publicationWaiters = [];

            /// <summary>Completes when <see cref="TotalPublications"/> reaches <paramref name="count"/>.</summary>
            public Task WaitForPublicationTotalAsync(int count)
            {
                lock (_publicationGate)
                {
                    if (_totalPublications >= count)
                    {
                        return Task.CompletedTask;
                    }

                    var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _publicationWaiters.Add((count, source));
                    return source.Task;
                }
            }

            /// <summary>Records one publication toward <see cref="TotalPublications"/>.</summary>
            internal void NotePublication()
            {
                List<TaskCompletionSource>? completed = null;
                lock (_publicationGate)
                {
                    _totalPublications++;
                    for (var i = _publicationWaiters.Count - 1; i >= 0; i--)
                    {
                        if (_totalPublications < _publicationWaiters[i].Count)
                        {
                            continue;
                        }

                        completed ??= [];
                        completed.Add(_publicationWaiters[i].Source);
                        _publicationWaiters.RemoveAt(i);
                    }
                }

                if (completed is null)
                {
                    return;
                }

                foreach (var source in completed)
                {
                    source.TrySetResult();
                }
            }

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
            public async Task PublishConfirmedAsync(RabbitMqConfirmedPublication publication, CancellationToken cancellationToken)
            {
                if (connection.PublishException is not null)
                {
                    throw connection.PublishException;
                }

                Publications.Add(publication);
                connection.NotePublication();
                connection.OnPublish?.Invoke();
                if (connection.ConfirmGate is not null)
                {
                    if (connection.IgnoreConfirmCancellation)
                    {
                        await connection.ConfirmGate.Task.ConfigureAwait(false);
                    }
                    else
                    {
                        await connection.ConfirmGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
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

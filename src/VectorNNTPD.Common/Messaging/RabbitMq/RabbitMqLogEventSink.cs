using System.Globalization;
using System.Text;
using System.Threading.Channels;
using RabbitMQ.Client.Exceptions;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting;

namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>
    /// Serilog sink that publishes formatted log events on the process RabbitMQ connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RabbitMqService"/> remains the sole TCP connection owner. Each queued event calls
    /// <see cref="IRabbitMqService.TryGetCurrent"/> and publishes through
    /// <see cref="IRabbitMqConnection.CreatePublishChannelAsync"/>. A cached channel is replaced
    /// when the connection generation changes. This sink does not open a broker connection and
    /// does not accept host, credential, virtual-host, heartbeat, or TLS settings.
    /// </para>
    /// <para>
    /// <see cref="Emit"/> formats the payload on the caller thread and enqueues it onto a bounded
    /// channel drained by a single background worker. The caller does not wait for channel creation,
    /// network I/O, or publisher confirms. When the queue is full, <see cref="ChannelWriter{T}.TryWrite"/>
    /// rejects the incoming event without blocking (the channel uses <see cref="BoundedChannelFullMode.Wait"/>
    /// so <c>TryWrite</c> returns <see langword="false"/> rather than dropping an already-queued item) and a
    /// rate-limited <see cref="SelfLog"/> diagnostic is written. Publish and format failures are reported
    /// through <see cref="SelfLog"/> and are not written back through Serilog.
    /// </para>
    /// <para>
    /// Disposal marks the sink disposed, completes the writer, and waits up to
    /// <see cref="ShutdownTimeout"/> for the worker to drain already-accepted events. If that bound is
    /// exceeded, cancellation is requested and disposal waits again. The cached publish channel is owned
    /// exclusively by the worker: disposal never disposes the channel while a publish may still be in
    /// flight. An uncooperative confirm that ignores cancellation can therefore outlive
    /// <see cref="Dispose"/>; the worker disposes the channel when it later exits. The injected
    /// <see cref="IRabbitMqService"/> is never disposed by this sink.
    /// </para>
    /// <para>
    /// The configured exchange is assumed to exist. This sink does not declare topology.
    /// </para>
    /// </remarks>
    internal sealed class RabbitMqLogEventSink : ILogEventSink, IDisposable
    {
        /// <summary>AMQP content type used for plain-text log payloads.</summary>
        public const string TextContentType = "text/plain";

        /// <summary>AMQP content type used for JSON log payloads.</summary>
        public const string JsonContentType = "application/json";

        /// <summary>AMQP content encoding written on every publication.</summary>
        public const string Utf8ContentEncoding = "utf-8";

        /// <summary>Default bounded queue capacity when the caller does not supply one.</summary>
        internal const int DefaultQueueCapacity = 1024;

        /// <summary>Maximum time <see cref="Dispose"/> waits for the worker to drain accepted events.</summary>
        internal static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

        /// <summary>Delay after a skipped publication when RabbitMQ is not ready.</summary>
        private static readonly TimeSpan UnavailableBackoff = TimeSpan.FromMilliseconds(250);

        /// <summary>SelfLog every Nth dropped overflow event after the first.</summary>
        internal const int DropDiagnosticInterval = 100;

        /// <summary>
        /// Per-instance, per-thread re-entrancy depth for the synchronous <see cref="Emit"/> path.
        /// Concurrent callers on other threads see an independent depth of zero. Distinct sink instances
        /// do not share state. The publication worker does not use this guard; worker diagnostics use
        /// <see cref="SelfLog"/> and must not write through Serilog into this sink.
        /// </summary>
        /// <remarks>
        /// Not disposed explicitly. <see cref="Dispose"/> can return while another thread is still inside
        /// <see cref="Emit"/> after observing a pre-dispose snapshot of <see cref="_disposed"/>. Disposing
        /// this guard underneath those callers would throw from <see cref="ThreadLocal{T}.Value"/> and escape
        /// the logging boundary. The instance is reclaimed with the sink.
        /// </remarks>
        private readonly ThreadLocal<int> _emitDepth = new(static () => 0);

        /// <summary>Process connection owner. This sink does not dispose it and does not open its own connection.</summary>
        private readonly IRabbitMqService _rabbitMq;

        /// <summary>Bounded queue of fully prepared publications.</summary>
        private readonly Channel<PreparedPublication> _queue;

        /// <summary>Writer half of <see cref="_queue"/>. Completed by <see cref="Dispose"/>.</summary>
        private readonly ChannelWriter<PreparedPublication> _writer;

        /// <summary>Reader half of <see cref="_queue"/>. Owned by <see cref="_worker"/>.</summary>
        private readonly ChannelReader<PreparedPublication> _reader;

        /// <summary>Cancels an in-flight confirm when disposal exceeds <see cref="ShutdownTimeout"/>.</summary>
        private readonly CancellationTokenSource _workerCts = new();

        /// <summary>Single publication worker. Observed by <see cref="Dispose"/>.</summary>
        private readonly Task _worker;

        /// <summary>Serializes cached-channel replacement and disposal on the worker path.</summary>
        private readonly Lock _channelGate = new();

        /// <summary>
        /// Cached confirm channel for the current connection generation. Owned by the worker only.
        /// </summary>
        private IRabbitMqPublishChannel? _channel;

        /// <summary><c>1</c> after <see cref="Dispose"/>. Later events are ignored.</summary>
        private int _disposed;

        /// <summary>Accepted events not yet finished by the worker (queued or publishing).</summary>
        private int _outstanding;

        /// <summary>Completes when <see cref="_outstanding"/> reaches zero. Replaced under <see cref="_idleGate"/>.</summary>
        private TaskCompletionSource _idle =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Protects replacement of <see cref="_idle"/>.</summary>
        private readonly Lock _idleGate = new();

        /// <summary>Total events dropped because the bounded queue was full.</summary>
        private long _droppedCount;

        /// <summary>
        /// Initializes a sink that publishes to <paramref name="exchange"/> with <paramref name="routingKey"/>.
        /// </summary>
        /// <param name="rabbitMq">Process RabbitMQ connection owner. Not disposed by this sink.</param>
        /// <param name="exchange">Destination exchange. The sink does not declare it.</param>
        /// <param name="routingKey">Routing key for each publication.</param>
        /// <param name="appId">AMQP AppId. Callers pass the entry-assembly application identity.</param>
        /// <param name="formatter">Payload formatter selected by the application.</param>
        /// <param name="contentType">AMQP content type for the formatted payload.</param>
        /// <param name="queueCapacity">
        /// Maximum number of prepared events retained for the background worker.
        /// Defaults to <see cref="DefaultQueueCapacity"/>. Must be at least 1.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="queueCapacity"/> is less than 1.</exception>
        internal RabbitMqLogEventSink(
            IRabbitMqService rabbitMq,
            string exchange,
            string routingKey,
            string appId,
            ITextFormatter formatter,
            string contentType,
            int queueCapacity = DefaultQueueCapacity)
        {
            ArgumentNullException.ThrowIfNull(rabbitMq);
            ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
            ArgumentException.ThrowIfNullOrWhiteSpace(routingKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(appId);
            ArgumentNullException.ThrowIfNull(formatter);
            ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
            ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);

            _rabbitMq = rabbitMq;
            Exchange = exchange.Trim();
            RoutingKey = routingKey.Trim();
            AppId = appId.Trim();
            Formatter = formatter;
            ContentType = contentType.Trim();
            QueueCapacity = queueCapacity;

            // Wait mode: TryWrite returns false immediately when full without blocking Emit and without
            // discarding an item already in the queue.
            _queue = Channel.CreateBounded<PreparedPublication>(new BoundedChannelOptions(queueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
            _writer = _queue.Writer;
            _reader = _queue.Reader;
            _idle.TrySetResult();
            _worker = Task.Run(() => RunWorkerAsync(_workerCts.Token));
        }

        /// <summary>Gets the destination exchange.</summary>
        private string Exchange { get; }

        /// <summary>Gets the routing key.</summary>
        private string RoutingKey { get; }

        /// <summary>Gets the AMQP AppId.</summary>
        private string AppId { get; }

        /// <summary>Gets the payload formatter.</summary>
        internal ITextFormatter Formatter { get; }

        /// <summary>Gets the AMQP content type.</summary>
        internal string ContentType { get; }

        /// <summary>Gets the bounded queue capacity used by this sink.</summary>
        internal int QueueCapacity { get; }

        /// <summary>Gets the number of events dropped because the queue was full.</summary>
        internal long DroppedCount => Interlocked.Read(ref _droppedCount);

        /// <summary>
        /// Formats <paramref name="logEvent"/> and enqueues it for background publication.
        /// </summary>
        /// <param name="logEvent">The event to publish.</param>
        /// <remarks>
        /// Failures are written to <see cref="SelfLog"/> and are not thrown to the logging caller.
        /// Same-thread re-entrant <see cref="Emit"/> calls (for example from a formatter) are dropped.
        /// Concurrent callers on other threads are accepted when capacity remains.
        /// </remarks>
        public void Emit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);
            if (Volatile.Read(ref _disposed) == 1)
            {
                SelfLog.WriteLine("RabbitMqLogEventSink ignored an event because the sink is disposed.");
                return;
            }

            if (_emitDepth.Value > 0)
            {
                SelfLog.WriteLine("RabbitMqLogEventSink dropped a re-entrant log event.");
                return;
            }

            _emitDepth.Value++;
            try
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    SelfLog.WriteLine("RabbitMqLogEventSink ignored an event because the sink is disposed.");
                    return;
                }

                var prepared = Prepare(logEvent);
                NoteQueued();
                if (_writer.TryWrite(prepared))
                {
                    return;
                }

                NoteCompleted();
                if (Volatile.Read(ref _disposed) == 1)
                {
                    SelfLog.WriteLine("RabbitMqLogEventSink ignored an event because the sink is disposed.");
                    return;
                }

                var dropped = Interlocked.Increment(ref _droppedCount);
                if (dropped == 1 || dropped % DropDiagnosticInterval == 0)
                {
                    SelfLog.WriteLine(
                        "RabbitMqLogEventSink dropped a log event because the queue is full (dropped={0}, capacity={1}).",
                        dropped,
                        QueueCapacity);
                }
            }
            // codeql[cs/catch-of-all-exceptions] Application formatters may throw arbitrary managed exceptions; contain them at the Serilog sink boundary via SelfLog. Process-integrity failures are excluded by the filter.
            catch (Exception ex) when (IsContainedCallerFailure(ex))
            {
                SelfLog.WriteLine("RabbitMqLogEventSink publication failed: {0}", ex.Message);
            }
            finally
            {
                _emitDepth.Value--;
            }
        }

        /// <summary>
        /// Completes when every accepted event has left the worker (published, skipped, or failed).
        /// </summary>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <returns>A task that completes when <see cref="_outstanding"/> is zero.</returns>
        /// <remarks>Intended for tests that must observe publication after a non-blocking <see cref="Emit"/>.</remarks>
        internal Task WhenIdleAsync(CancellationToken cancellationToken = default)
        {
            TaskCompletionSource idle;
            lock (_idleGate)
            {
                if (_outstanding == 0)
                {
                    return Task.CompletedTask;
                }

                idle = _idle;
            }

            return idle.Task.WaitAsync(cancellationToken);
        }

        /// <summary>
        /// Rejects new events and waits for the worker to finish. Does not dispose the RabbitMQ connection.
        /// </summary>
        /// <remarks>
        /// Idempotent. Channel disposal is performed only by the worker after it stops using the channel.
        /// If the worker does not exit within two <see cref="ShutdownTimeout"/> windows, disposal returns
        /// without disposing a channel that may still be in use; the worker disposes it on a later exit.
        /// </remarks>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _writer.TryComplete();

            using (_workerCts)
            {
                try
                {
                    if (!_worker.Wait(ShutdownTimeout))
                    {
                        _workerCts.Cancel();
                        if (!_worker.Wait(ShutdownTimeout))
                        {
                            SelfLog.WriteLine(
                                "RabbitMqLogEventSink disposal timed out waiting for the publication worker; " +
                                "the publish channel remains owned by the worker until it exits.");
                        }
                    }
                }
                catch (Exception ex) when (ex is AggregateException or ObjectDisposedException or OperationCanceledException)
                {
                    SelfLog.WriteLine("RabbitMqLogEventSink disposal observed a worker failure: {0}", ex.Message);
                }
            }
        }

        /// <summary>Background loop that confirm-publishes prepared events sequentially.</summary>
        private async Task RunWorkerAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (await _reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (_reader.TryRead(out var prepared))
                    {
                        try
                        {
                            await PublishAsync(prepared, cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            // Always settle outstanding for the dequeued item, including cancel and unexpected faults.
                            NoteCompleted();
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DrainDiscardingRemaining();
            }
            catch (Exception ex) when (!IsProcessIntegrityFailure(ex)) // codeql[cs/catch-of-all-exceptions] Unexpected worker faults must SelfLog and drain; they must not become unobserved task exceptions.
            {
                SelfLog.WriteLine("RabbitMqLogEventSink publication worker failed: {0}", ex.Message);
                DrainDiscardingRemaining();
            }
            finally
            {
                // Stop accepting further events if this worker exits without Dispose having completed the writer.
                _writer.TryComplete();
                await DisposeChannelAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Discards any items still in the reader after cancellation or a fatal worker fault.</summary>
        private void DrainDiscardingRemaining()
        {
            while (_reader.TryRead(out _))
            {
                NoteCompleted();
            }
        }

        /// <summary>Confirm-publishes one prepared event, replacing the channel when the generation changes.</summary>
        private async Task PublishAsync(PreparedPublication prepared, CancellationToken cancellationToken)
        {
            if (!_rabbitMq.TryGetCurrent(out var handle) || !handle.IsOpen)
            {
                SelfLog.WriteLine("RabbitMqLogEventSink skipped an event because RabbitMQ is not ready.");
                try
                {
                    await Task.Delay(UnavailableBackoff, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Expected when disposal cancels the worker during the unavailable-broker backoff.
                    SelfLog.WriteLine(
                        "RabbitMqLogEventSink unavailable-broker backoff ended because disposal cancelled the worker.");
                }

                return;
            }

            IRabbitMqPublishChannel channel;
            try
            {
                channel = await EnsureChannelAsync(handle, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AlreadyClosedException
                or OperationInterruptedException
                or BrokerUnreachableException
                or PublishException
                or IOException
                or ObjectDisposedException
                or TimeoutException
                or InvalidOperationException
                or NotSupportedException)
            {
                SelfLog.WriteLine("RabbitMqLogEventSink publication failed: {0}", ex.Message);
                return;
            }

            var publication = new RabbitMqConfirmedPublication(
                Exchange: Exchange,
                RoutingKey: RoutingKey,
                MessageId: prepared.MessageId,
                AppId: AppId,
                CorrelationId: null,
                ContentType: ContentType,
                RequestIdHeader: null,
                ExpirationMilliseconds: string.Empty,
                Persistent: true,
                Mandatory: false,
                Body: prepared.Body,
                ContentEncoding: Utf8ContentEncoding,
                Timestamp: prepared.Timestamp);

            try
            {
                await channel.PublishConfirmedAsync(publication, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is AlreadyClosedException
                or OperationInterruptedException
                or BrokerUnreachableException
                or PublishException
                or IOException
                or ObjectDisposedException
                or TimeoutException
                or InvalidOperationException
                or NotSupportedException)
            {
                SelfLog.WriteLine("RabbitMqLogEventSink publication failed: {0}", ex.Message);
                if (!channel.IsOpen)
                {
                    await DisposeChannelAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Returns the cached open channel when it matches <paramref name="handle"/>'s generation; otherwise replaces it.
        /// </summary>
        private async Task<IRabbitMqPublishChannel> EnsureChannelAsync(
            RabbitMqConnectionHandle handle,
            CancellationToken cancellationToken)
        {
            lock (_channelGate)
            {
                if (_channel is { IsOpen: true } current && current.Generation == handle.Generation)
                {
                    return current;
                }
            }

            await DisposeChannelAsync().ConfigureAwait(false);
            var created = await handle.Connection
                .CreatePublishChannelAsync(handle.Generation, cancellationToken)
                .ConfigureAwait(false);

            lock (_channelGate)
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _channel = created;
                    return created;
                }
            }

            try
            {
                await created.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ObjectDisposedException
                or InvalidOperationException
                or IOException
                or NotSupportedException
                or OperationCanceledException
                or AlreadyClosedException
                or OperationInterruptedException
                or TimeoutException)
            {
                SelfLog.WriteLine("RabbitMqLogEventSink failed to dispose a publish channel: {0}", ex.Message);
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new ObjectDisposedException(nameof(RabbitMqLogEventSink));
        }

        /// <summary>Formats <paramref name="logEvent"/> into an immutable queued publication.</summary>
        private PreparedPublication Prepare(LogEvent logEvent)
        {
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            Formatter.Format(logEvent, writer);
            var body = Encoding.UTF8.GetBytes(writer.ToString());
            return new PreparedPublication(
                body,
                logEvent.Timestamp,
                Guid.NewGuid().ToString("D"));
        }

        /// <summary>Increments the outstanding counter and clears the idle signal when work begins.</summary>
        private void NoteQueued()
        {
            lock (_idleGate)
            {
                _outstanding++;
                if (_outstanding == 1)
                {
                    _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }

        /// <summary>Decrements the outstanding counter and signals idle when the last item completes.</summary>
        private void NoteCompleted()
        {
            TaskCompletionSource? idle = null;
            lock (_idleGate)
            {
                _outstanding--;
                if (_outstanding == 0)
                {
                    idle = _idle;
                }
            }

            idle?.TrySetResult();
        }

        /// <summary>Disposes the cached publish channel asynchronously. Called only from the worker.</summary>
        private async Task DisposeChannelAsync()
        {
            IRabbitMqPublishChannel? channel;
            lock (_channelGate)
            {
                channel = _channel;
                _channel = null;
            }

            if (channel is null)
            {
                return;
            }

            try
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ObjectDisposedException
                or InvalidOperationException
                or IOException
                or NotSupportedException
                or OperationCanceledException
                or AlreadyClosedException
                or OperationInterruptedException
                or TimeoutException)
            {
                SelfLog.WriteLine("RabbitMqLogEventSink failed to dispose a publish channel: {0}", ex.Message);
            }
        }

        /// <summary>
        /// True for failures that may be reported from the caller's <see cref="Emit"/> path without killing the process.
        /// </summary>
        /// <remarks>
        /// Formatters are application-supplied and may throw arbitrary managed exceptions. Process-integrity
        /// failures are excluded so they still tear down the process.
        /// </remarks>
        private static bool IsContainedCallerFailure(Exception exception) =>
            !IsProcessIntegrityFailure(exception);

        /// <summary>True for failures that must tear down the process rather than be contained by the sink.</summary>
        private static bool IsProcessIntegrityFailure(Exception exception) =>
            exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException;

        /// <summary>Fully prepared log publication captured before enqueue.</summary>
        /// <param name="Body">UTF-8 formatted payload.</param>
        /// <param name="Timestamp">Log event timestamp copied into AMQP properties.</param>
        /// <param name="MessageId">Unique AMQP message id assigned at enqueue time.</param>
        private readonly record struct PreparedPublication(
            byte[] Body,
            DateTimeOffset Timestamp,
            string MessageId);
    }
}

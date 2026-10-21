using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Logging;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.ArticleWork
{
    /// <summary>
    /// Hosts the confirm-enabled Article Work response publisher. Does not own the RabbitMQ connection.
    /// </summary>
    /// <remarks>
    /// Startup fails if a publishing channel cannot be opened on the current generation.
    /// After start, connection replacement and independent publish-channel faults rebuild the publisher channel
    /// on the current connection generation. This type does not implement a second connection-recovery loop
    /// and never ACK/NACKs deliveries.
    /// Completing <see cref="PublishAsync"/> means the broker confirmed and the publication generation was still current afterward.
    /// Confirmation is not permission to ACK the original delivery.
    /// One publication holds <see cref="_publishGate"/>. The confirmation wait is cancelled by the caller token, disposal, or the configured confirmation timeout.
    /// </remarks>
    internal sealed class ArticleWorkResponsePublisher : IArticleWorkResponsePublisher, IHostedService, IAsyncDisposable
    {
        /// <summary>Process RabbitMQ connection owner. This publisher opens channels on it and does not open another connection.</summary>
        private readonly IRabbitMqService _connections;

        /// <summary>Validated runtime snapshot. Supplies the publisher-confirm timeout.</summary>
        private readonly BackFillerRuntimeOptions _runtime;

        /// <summary>Publisher logger. This type does not write response bodies or credentials.</summary>
        private readonly ILogger<ArticleWorkResponsePublisher> _logger;

        /// <summary>Guards the publication channel, lifecycle state, the stopping flag, and the replacement task.</summary>
        private readonly Lock _gate = new();

        /// <summary>Serializes confirmed publications. Disposal waits until the current holder releases it.</summary>
        private readonly SemaphoreSlim _publishGate = new(1, 1);

        /// <summary>Serializes startup, channel replacement, and disposal of the publication channel.</summary>
        private readonly SemaphoreSlim _replaceGate = new(1, 1);

        /// <summary>Cancelled when disposal begins. Linked into each confirmation wait. Disposed with the publisher.</summary>
        private readonly CancellationTokenSource _runCts = new();

        /// <summary>Installed a confirm-enabled publication channel. Null before a successful installation and after disposal clears it.</summary>
        private IRabbitMqPublishChannel? _channel;

        /// <summary>Latest connection-replacement rebuild. Callers do not await a task that a later replacement overwrote.</summary>
        private Task _replaceTask = Task.CompletedTask;

        /// <summary>Local lifecycle. Mutations are made under <see cref="_gate"/>.</summary>
        private ArticleWorkResponsePublisherState _state = ArticleWorkResponsePublisherState.Created;

        /// <summary>One after <see cref="StartAsync"/> has entered startup. Reset to zero when that attempt fails.</summary>
        private int _started;

        /// <summary>One after <see cref="DisposeAsync"/> has entered disposal.</summary>
        private int _disposed;

        /// <summary>Set under <see cref="_gate"/> when disposal has begun. Blocks new publications and replacement scheduling.</summary>
        private bool _stopping;

        /// <summary>
        /// Initializes a new response publisher.
        /// </summary>
        /// <param name="connections">Sole connection owner.</param>
        /// <param name="runtime">Validated runtime snapshot.</param>
        /// <param name="logger">Publisher logger.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="connections"/>, <paramref name="runtime"/>, or <paramref name="logger"/> is null.
        /// </exception>
        /// <remarks>Does not open a channel. <see cref="State"/> remains <see cref="ArticleWorkResponsePublisherState.Created"/>.</remarks>
        internal ArticleWorkResponsePublisher(
            IRabbitMqService connections,
            BackFillerRuntimeOptions runtime,
            ILogger<ArticleWorkResponsePublisher> logger)
        {
            ArgumentNullException.ThrowIfNull(connections);
            ArgumentNullException.ThrowIfNull(runtime);
            ArgumentNullException.ThrowIfNull(logger);
            _connections = connections;
            _runtime = runtime;
            _logger = logger;
        }

        /// <inheritdoc />
        public bool CompletesSuccessPublication => true;

        /// <summary>Gets the current local lifecycle state.</summary>
        internal ArticleWorkResponsePublisherState State
        {
            get
            {
                lock (_gate)
                {
                    return _state;
                }
            }
        }

        /// <summary>Gets the publish-channel generation or zero before start.</summary>
        internal long Generation
        {
            get
            {
                lock (_gate)
                {
                    return _channel?.Generation ?? 0;
                }
            }
        }

        /// <summary>Gets the caller-owned publishing channel while the publisher is live (tests).</summary>
        internal IRabbitMqPublishChannel? Channel
        {
            get
            {
                lock (_gate)
                {
                    return _channel;
                }
            }
        }

        /// <summary>
        /// Opens a confirm-enabled publication channel on the current connection and subscribes to connection replacement.
        /// </summary>
        /// <param name="cancellationToken">
        /// Cancels the replacement-gate wait and the initial channel open.
        /// A second call returns without waiting for an in-progress start.
        /// </param>
        /// <returns>
        /// A task that completes when the publisher is <see cref="ArticleWorkResponsePublisherState.Running"/>,
        /// or immediately when start has already been entered.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the connection is not ready or the opened channel's generation is no longer current.
        /// </exception>
        /// <remarks>
        /// On failure the replacement subscription is removed, any installed channel is disposed, state becomes
        /// <see cref="ArticleWorkResponsePublisherState.Stopped"/>, the start flag is cleared so a later call can retry, and the exception propagates.
        /// Other exceptions from channel creation propagate after that clean-up.
        /// </remarks>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            lock (_gate)
            {
                _state = ArticleWorkResponsePublisherState.Starting;
            }

            await _replaceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _connections.ConnectionReplaced += OnConnectionReplaced;
                await ReplaceChannelAsync(startup: true, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    _state = ArticleWorkResponsePublisherState.Running;
                }

                ArticleWorkLogMessages.PublisherRunning(_logger, Generation);
            }
            catch (Exception ex)
            {
                ArticleWorkLogMessages.PublisherStartFailed(_logger, ex.Message);
                _connections.ConnectionReplaced -= OnConnectionReplaced;
                await DisposeChannelAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    _state = ArticleWorkResponsePublisherState.Stopped;
                }

                Interlocked.Exchange(ref _started, 0);
                throw;
            }
            finally
            {
                _replaceGate.Release();
            }
        }

        /// <summary>Disposes the publisher.</summary>
        /// <param name="cancellationToken">Not observed. Shutdown uses the publisher's own disposal sequence.</param>
        /// <returns>A task that completes when <see cref="DisposeAsync"/> completes.</returns>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Stops accepting publications, cancels in-flight confirm waits, disposes the publication channel, and releases the gates.
        /// </summary>
        /// <returns>A task that completes when the publisher is <see cref="ArticleWorkResponsePublisherState.Stopped"/>. A second call returns a completed task.</returns>
        /// <remarks>
        /// Waits for the tracked replacement task and logs its failure without rethrowing.
        /// The replacement-gate wait is not cancellable. After the channel is disposed, disposal waits up to the confirmation timeout
        /// for <see cref="_publishGate"/> and then releases it. A timeout is ignored, so disposal still finishes.
        /// The stopped log reads <see cref="Generation"/> after the channel is cleared, so that value is zero.
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            lock (_gate)
            {
                _stopping = true;
                _state = ArticleWorkResponsePublisherState.Retiring;
            }

            ArticleWorkLogMessages.PublisherRetiring(_logger, Generation);
            _connections.ConnectionReplaced -= OnConnectionReplaced;
            DetachPublishChannelFaultHandler();
            await _runCts.CancelAsync().ConfigureAwait(false);

            Task replace;
            lock (_gate)
            {
                replace = _replaceTask;
            }

            try
            {
                await replace.ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] A publish or replacement failure must not escape this publisher boundary.
            catch (Exception ex)
            {
                ArticleWorkLogMessages.PublisherReplaceFailed(_logger, ex.Message);
            }

            await _replaceGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await DisposeChannelAsync().ConfigureAwait(false);
                await WaitForPublishGateReleaseAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    _state = ArticleWorkResponsePublisherState.Stopped;
                }

                ArticleWorkLogMessages.PublisherStopped(_logger, Generation);
            }
            finally
            {
                _replaceGate.Release();
                DisposeShutdownHandles();
            }
        }

        /// <summary>Releases the replacement gate, the publish gate, and the run cancellation source.</summary>
        private void DisposeShutdownHandles()
        {
            _replaceGate.Dispose();
            _publishGate.Dispose();
            _runCts.Dispose();
        }

        /// <summary>
        /// Publishes one terminal response to the default exchange and waits until the broker confirms.
        /// </summary>
        /// <param name="intent">Response identities and outcome. Correlation id and reply-to must both be non-whitespace.</param>
        /// <param name="cancellationToken">
        /// Cancels the gate wait and the confirmation wait. Linked with disposal and cancelled again after
        /// <see cref="BackFillerRabbitMqRuntimeOptions.PublishConfirmTimeoutSeconds"/>.
        /// </param>
        /// <returns>
        /// A task that completes when the broker has confirmed and the same open channel is still the current generation.
        /// Completion is not permission to ACK the original delivery.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="intent"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the publisher is not <see cref="ArticleWorkResponsePublisherState.Running"/>, correlation id or reply-to is missing,
        /// no open publication channel is installed, the connection generation is no longer the channel generation, or that check fails again after confirmation.
        /// <see cref="ArticleWorkResponseWireProtocol.SerializeV1"/> validation failures also propagate before the publication gate is taken.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// Thrown when <paramref name="cancellationToken"/>, disposal, or the confirmation timeout cancels the attempt.
        /// That exception is not written as a publication-failed log.
        /// </exception>
        /// <remarks>
        /// Publications on this instance are serialized by <see cref="_publishGate"/>.
        /// The AMQP message id is a new GUID, not the request id. App id is null. Delivery is non-persistent and mandatory.
        /// The routing key is <see cref="ArticleWorkResponseIntent.ReplyTo"/> and the exchange is empty.
        /// Content type, expiration, and body come from <see cref="ArticleWorkResponseWireProtocol"/>.
        /// The request-id header is the intent request id in <c>D</c> format, or null when the intent has no request id.
        /// Any other publication exception is logged with <see cref="Exception.Message"/> and rethrown. This method does not ACK or NACK.
        /// </remarks>
        public async Task PublishAsync(ArticleWorkResponseIntent intent, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(intent);
            ThrowIfNotRunning();
            if (string.IsNullOrWhiteSpace(intent.CorrelationId) || string.IsNullOrWhiteSpace(intent.ReplyTo))
            {
                throw new InvalidOperationException("Response publication requires CorrelationId and ReplyTo.");
            }

            var body = ArticleWorkResponseWireProtocol.SerializeV1(intent);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _runCts.Token);
            linked.CancelAfter(TimeSpan.FromSeconds(_runtime.RabbitMq.PublishConfirmTimeoutSeconds));

            await _publishGate.WaitAsync(linked.Token).ConfigureAwait(false);
            var generation = 0L;
            try
            {
                ThrowIfNotRunning();
                await EnsureOpenPublishChannelAsync(linked.Token).ConfigureAwait(false);
                var channel = CurrentOpenChannelOrThrow();
                generation = channel.Generation;
                if (!IsPublisherGenerationCurrent(generation))
                {
                    throw new InvalidOperationException("RabbitMQ publish generation is no longer current.");
                }

                var publication = new BackFillerRabbitMqPublication(
                    intent.ReplyTo,
                    intent.CorrelationId,
                    ArticleWorkResponseWireProtocol.JsonContentType,
                    Guid.NewGuid().ToString("D"),
                    intent.RequestId?.ToString("D"),
                    ArticleWorkResponseWireProtocol.ExpirationMilliseconds,
                    body);

                await channel.PublishConfirmedAsync(
                        new RabbitMqConfirmedPublication(
                            Exchange: string.Empty,
                            RoutingKey: publication.ReplyTo,
                            MessageId: publication.MessageId,
                            AppId: null,
                            CorrelationId: publication.CorrelationId,
                            ContentType: publication.ContentType,
                            RequestIdHeader: publication.RequestIdHeader,
                            ExpirationMilliseconds: publication.ExpirationMilliseconds,
                            Persistent: false,
                            Mandatory: true,
                            Body: publication.Body),
                        linked.Token)
                    .ConfigureAwait(false);

                if (!IsPublisherGenerationCurrent(generation) || !channel.IsOpen || !ReferenceEquals(Channel, channel))
                {
                    throw new InvalidOperationException(
                        "RabbitMQ publish generation became invalid during confirmation.");
                }

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    var requestId = intent.RequestId?.ToString("D") ?? "(none)";
                    var outcome = ArticleWorkResponseWireProtocol.OutcomeName(intent.Outcome);
                    ArticleWorkLogMessages.PublicationConfirmed(
                        _logger,
                        generation,
                        requestId,
                        intent.CorrelationId,
                        outcome);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ArticleWorkLogMessages.PublicationFailed(
                    _logger,
                    generation,
                    intent.RequestId?.ToString("D") ?? "(none)",
                    intent.Outcome.ToString(),
                    ex.Message);
                throw;
            }
            finally
            {
                _publishGate.Release();
            }
        }

        /// <summary>
        /// Installs <paramref name="candidate"/> only when it is not older than the current channel.
        /// A stale candidate is disposed and cannot replace or dispose of a newer channel.
        /// </summary>
        /// <param name="candidate">Channel opened against a connection generation. The caller transfers ownership of it.</param>
        /// <returns>
        /// A task that completes after a rejected candidate or a replaced channel has been disposed of.
        /// Completes immediately when no channel is disposed.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="candidate"/> is null.</exception>
        /// <remarks>
        /// A candidate is stale only when its generation is strictly less than the installed channel's generation.
        /// An equal or newer generation replaces the installed channel.
        /// </remarks>
        internal async Task InstallPublishChannelAsync(IRabbitMqPublishChannel candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            IRabbitMqPublishChannel? replaced = null;
            IRabbitMqPublishChannel? stale = null;
            lock (_gate)
            {
                if (_channel is { } current && current.Generation > candidate.Generation)
                {
                    stale = candidate;
                }
                else
                {
                    replaced = _channel;
                    if (replaced is not null)
                    {
                        replaced.ChannelFaulted -= OnPublishChannelFaulted;
                    }

                    candidate.ChannelFaulted += OnPublishChannelFaulted;
                    _channel = candidate;
                }
            }

            if (stale is not null)
            {
                await stale.DisposeAsync().ConfigureAwait(false);
                return;
            }

            if (replaced is not null && !ReferenceEquals(replaced, candidate))
            {
                await replaced.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Schedules a publish-channel rebuild when the event is a connection replacement and disposal has not started.</summary>
        /// <param name="sender">Event sender. Not used.</param>
        /// <param name="eventArgs">Replacement notice. Non-replacement events are ignored.</param>
        /// <remarks>
        /// The scheduled task replaces <see cref="_replaceTask"/>. An earlier rebuild that is still running is no longer tracked.
        /// </remarks>
        private void OnConnectionReplaced(object? sender, RabbitMqConnectionReplacedEventArgs eventArgs)
        {
            if (!eventArgs.IsReplacement)
            {
                return;
            }

            ScheduleChannelRebuild();
        }

        /// <summary>
        /// Schedules a publish-channel rebuild when the installed channel faults independently of connection replacement.
        /// </summary>
        /// <param name="sender">Faulted channel. Ignored when it is not the installed channel.</param>
        /// <param name="eventArgs">Fault classification and reason.</param>
        private void OnPublishChannelFaulted(object? sender, RabbitMqChannelFaultedEventArgs eventArgs)
        {
            long generation;
            lock (_gate)
            {
                if (_stopping || _channel is null || !ReferenceEquals(_channel, sender))
                {
                    return;
                }

                generation = _channel.Generation;
            }

            ArticleWorkLogMessages.PublisherChannelFaulted(
                _logger,
                generation,
                eventArgs.Kind,
                eventArgs.Reason);
            ScheduleChannelRebuild();
        }

        /// <summary>
        /// Chains a non-cancelling rebuild after any in-flight rebuild when disposal has not started.
        /// </summary>
        /// <remarks>
        /// Overwriting <see cref="_replaceTask"/> with a sibling <see cref="RebuildPublishChannelAsync"/> left prior
        /// waiters on <see cref="_replaceGate"/> unobserved; disposal could then dispose the semaphore while those
        /// waiters were still queued. Chaining keeps every rebuild on the task Dispose awaits.
        /// </remarks>
        private void ScheduleChannelRebuild()
        {
            lock (_gate)
            {
                if (_stopping)
                {
                    return;
                }

                _replaceTask = ContinueRebuildAsync(_replaceTask);
            }
        }

        /// <summary>
        /// Awaits <paramref name="previous"/> then runs one rebuild so later connection or channel-fault requests are not dropped.
        /// </summary>
        /// <param name="previous">Earlier rebuild or completed task. Failures are ignored here because that boundary already logged them.</param>
        /// <returns>A task that completes when the follow-on rebuild finishes.</returns>
        private async Task ContinueRebuildAsync(Task previous)
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] A prior rebuild already logged; this link must still run the follow-on replace.
            catch (Exception)
            {
                // Prior rebuild logged at its boundary; a later request must still run.
            }

            await RebuildPublishChannelAsync().ConfigureAwait(false);
        }

        /// <summary>Rebuilds the publication channel on the current connection unless startup never succeeded or disposal has started.</summary>
        /// <returns>A task that completes when the rebuild attempt finishes. Failures are logged and not rethrown.</returns>
        /// <remarks>
        /// Waits on <see cref="_replaceGate"/> without a cancellation token, then opens the channel with <see cref="_runCts"/>.
        /// Used for both connection replacement and independent channel faults.
        /// </remarks>
        private async Task RebuildPublishChannelAsync()
        {
            await _replaceGate.WaitAsync().ConfigureAwait(false);
            try
            {
                bool stopping;
                lock (_gate)
                {
                    stopping = _stopping;
                }

                if (stopping || Volatile.Read(ref _started) == 0)
                {
                    return;
                }

                await ReplaceChannelAsync(startup: false, _runCts.Token).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] A publish or replacement failure must not escape this publisher boundary.
            catch (Exception ex)
            {
                ArticleWorkLogMessages.PublisherReplaceFailed(_logger, ex.Message);
            }
            finally
            {
                _replaceGate.Release();
            }
        }

        /// <summary>
        /// Waits for any in-flight rebuild and, when the installed channel is closed, schedules another rebuild on the current connection.
        /// </summary>
        /// <param name="cancellationToken">Cancels the wait for an in-flight rebuild.</param>
        /// <remarks>
        /// Does not take <see cref="_replaceGate"/> while holding <see cref="_publishGate"/>; it only awaits the tracked rebuild task.
        /// </remarks>
        private async Task EnsureOpenPublishChannelAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task replace;
            var open = false;
            lock (_gate)
            {
                if (_channel is { IsOpen: true })
                {
                    open = true;
                    replace = Task.CompletedTask;
                }
                else if (_stopping)
                {
                    replace = Task.CompletedTask;
                }
                else if (!_replaceTask.IsCompleted)
                {
                    replace = _replaceTask;
                }
                else
                {
                    _replaceTask = ContinueRebuildAsync(_replaceTask);
                    replace = _replaceTask;
                }
            }

            if (open)
            {
                return;
            }

            await replace.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Opens a publication channel on the current connection and installs it when the generation is still current.</summary>
        /// <param name="startup">
        /// <see langword="true"/> when the caller is <see cref="StartAsync"/>. A missing connection then throws.
        /// A missing connection during replacement returns and leaves the installed channel unchanged.
        /// </param>
        /// <param name="cancellationToken">Cancels channel creation.</param>
        /// <returns>A task that completes when the candidate is installed or, on a non-startup miss, when the method returns without a channel.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown on startup when no current connection exists, or when the opened channel's generation does not match the current connection.
        /// </exception>
        /// <remarks>A channel that fails the generation check or installation is disposed before the exception propagates.</remarks>
        private async Task ReplaceChannelAsync(bool startup, CancellationToken cancellationToken)
        {
            if (!_connections.TryGetCurrent(out var handle))
            {
                if (startup)
                {
                    throw new InvalidOperationException("RabbitMQ connection is not ready for Article Work response publication.");
                }

                return;
            }

            ArticleWorkLogMessages.PublisherStarting(_logger, handle.Generation);
            var channel = await handle.Connection
                .CreatePublishChannelAsync(handle.Generation, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (!handle.IsCurrent || channel.Generation != handle.Generation)
                {
                    throw new InvalidOperationException("RabbitMQ publish channel generation is no longer current.");
                }

                await InstallPublishChannelAsync(channel).ConfigureAwait(false);
            }
            catch
            {
                await channel.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>Clears and disposes the installed publication channel.</summary>
        /// <returns>A task that completes when the channel is disposed, or immediately when none is installed.</returns>
        private async Task DisposeChannelAsync()
        {
            IRabbitMqPublishChannel? channel;
            lock (_gate)
            {
                channel = _channel;
                if (channel is not null)
                {
                    channel.ChannelFaulted -= OnPublishChannelFaulted;
                }

                _channel = null;
            }

            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Unsubscribes the fault handler from the installed channel without disposing it.</summary>
        private void DetachPublishChannelFaultHandler()
        {
            lock (_gate)
            {
                if (_channel is not null)
                {
                    _channel.ChannelFaulted -= OnPublishChannelFaulted;
                }
            }
        }

        /// <summary>
        /// Waits until <see cref="_publishGate"/> can be acquired, then releases it so disposal can dispose of the semaphore.
        /// </summary>
        /// <returns>
        /// A task that completes when the gate has been observed free. The wait is bounded by the confirmation timeout, with a minimum of one second.
        /// <see cref="OperationCanceledException"/> from that timeout is swallowed.
        /// </returns>
        private async Task WaitForPublishGateReleaseAsync()
        {
            using var waitCts = new CancellationTokenSource(
                TimeSpan.FromSeconds(Math.Max(1, _runtime.RabbitMq.PublishConfirmTimeoutSeconds)));
            try
            {
                await _publishGate.WaitAsync(waitCts.Token).ConfigureAwait(false);
                _publishGate.Release();
            }
            catch (OperationCanceledException)
            {
                ShutdownObservations.Canceled(_logger, "response publisher gate release");
            }
        }

        /// <summary>Returns the installed channel when it reports itself open.</summary>
        /// <returns>The current open publication channel.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no channel is installed or the installed channel is not open.</exception>
        private IRabbitMqPublishChannel CurrentOpenChannelOrThrow()
        {
            lock (_gate)
            {
                if (_channel is not { IsOpen: true } channel)
                {
                    throw new InvalidOperationException("Article Work response publisher has no open publish channel.");
                }

                return channel;
            }
        }

        /// <summary>Returns whether <paramref name="generation"/> is still the process's current connection generation.</summary>
        /// <param name="generation">Publish-channel generation captured for the attempt.</param>
        /// <returns>
        /// <see langword="true"/> when a current connection handle exists, its generation equals <paramref name="generation"/>, and that handle is current.
        /// </returns>
        private bool IsPublisherGenerationCurrent(long generation) =>
            _connections.TryGetCurrent(out var handle)
            && handle.Generation == generation
            && handle.IsCurrent;

        /// <summary>Rejects publication when disposal has started or the state is not running.</summary>
        /// <exception cref="InvalidOperationException">Thrown when the publisher is not accepting publications.</exception>
        private void ThrowIfNotRunning()
        {
            lock (_gate)
            {
                if (_stopping || _state != ArticleWorkResponsePublisherState.Running)
                {
                    throw new InvalidOperationException("Article Work response publisher is not accepting publications.");
                }
            }
        }

        /// <summary>
        /// One confirm-enabled Article Work response publication. Distinct from the consumer settlement channel.
        /// </summary>
        /// <param name="ReplyTo">Request ReplyTo used as the default-exchange routing key.</param>
        /// <param name="CorrelationId">Request CorrelationId echoed on the response.</param>
        /// <param name="ContentType">
        /// AMQP content type supplied by the caller. Article Work responses use <c>application/json</c>.
        /// This record does not validate the value.
        /// </param>
        /// <param name="MessageId">
        /// Caller-supplied AMQP message id. The Article Work publisher passes a new UUID string on each attempt.
        /// </param>
        /// <param name="RequestIdHeader">Logical request UUID when known. Not the CorrelationId.</param>
        /// <param name="ExpirationMilliseconds">
        /// Caller-supplied AMQP expiration, in milliseconds, as text. Article Work responses use <c>1000</c>.
        /// This record does not validate the value.
        /// </param>
        /// <param name="Body">UTF-8 JSON response. Never article bytes.</param>
        private sealed record BackFillerRabbitMqPublication(
            string ReplyTo,
            string CorrelationId,
            string ContentType,
            string MessageId,
            string? RequestIdHeader,
            string ExpirationMilliseconds,
            ReadOnlyMemory<byte> Body);
    }
}

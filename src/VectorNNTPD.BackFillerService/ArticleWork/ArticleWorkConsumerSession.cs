using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Logging;
using VectorNNTPD.BackFillerService.RabbitMq;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.ArticleWork
{
    /// <summary>
    /// One backbone-scoped consumer session that owns a single consumer channel.
    /// </summary>
    /// <remarks>
    /// Delivery states:
    /// <list type="bullet">
    /// <item><description>Broker-delivered: the fake/broker invoked the consumer callback. Not yet admitted.</description></item>
    /// <item><description>Admitted/queued: admission succeeded and the delivery is waiting for the dispatch lock. This is queued work.</description></item>
    /// <item><description>Active: the delivery holds the dispatch lock and has entered <c>ProcessAsync</c>.</description></item>
    /// <item><description>Completed/settled: the pipeline has attempted settlement on the original channel or skipped it as stale.</description></item>
    /// </list>
    /// RabbitMQ prefetch is not admission. Prefetched deliveries that arrive after retirement are not admitted and are not settled here; channel dispose lets the broker redeliver.
    /// </remarks>
    internal sealed class ArticleWorkConsumerSession : IAsyncDisposable
    {
        /// <summary>
        /// Shutdown snapshot for the shorter constructor: a 30-second grace value, drain queued work, and finish active articles.
        /// The grace value is logged. This session does not start a timer from it.
        /// </summary>
        private static readonly BackFillerShutdownRuntimeOptions DefaultShutdown = new(
            TimeSpan.FromSeconds(30),
            DrainQueuedWork: true,
            FinishActiveArticles: true);

        /// <summary>Provider backbone label supplied at construction. Not case-folded.</summary>
        private readonly string _backbone;

        /// <summary>One-based consume slot. Inclusive range is 1 through <see cref="_connectionLimit"/>.</summary>
        private readonly int _connectionNumber;

        /// <summary>Desired slot count for this backbone. Inclusive upper bound for <see cref="_connectionNumber"/>.</summary>
        private readonly int _connectionLimit;

        /// <summary>Provider queue name from <see cref="BackFillerRabbitMqTopology.ComposeProviderEntity"/>.</summary>
        private readonly string _queue;

        /// <summary>Per-channel prefetch passed to consume. Construction rejects zero.</summary>
        private readonly ushort _prefetch;

        /// <summary>Parse, publish, and settlement pipeline for deliveries admitted by this session.</summary>
        private readonly ArticleWorkDeliveryPipeline _pipeline;

        /// <summary>Process RabbitMQ connection owner. This session opens channels on it and does not open another connection.</summary>
        private readonly IRabbitMqService _connections;

        /// <summary>Session logger. This type does not write payloads or credentials.</summary>
        private readonly ILogger _logger;

        /// <summary>Shutdown policy captured at construction. Not re-read from options later.</summary>
        private readonly BackFillerShutdownRuntimeOptions _shutdown;

        /// <summary>
        /// Guards lifecycle state, the channel, the consumer tag, and the admitted-delivery count.
        /// Non-recursive. No caller enters it again on the same call stack.
        /// </summary>
        private readonly Lock _gate = new();

        /// <summary>Serializes admitted deliveries, so this session has at most one active pipeline call.</summary>
        private readonly SemaphoreSlim _dispatch = new(1, 1);

        /// <summary>Cancelled when queued work must not acquire dispatch. Disposed by <see cref="DisposeAsync"/>.</summary>
        private readonly CancellationTokenSource _queueCts = new();

        /// <summary>Cancelled when active pipeline work must stop. Disposed by <see cref="DisposeAsync"/>.</summary>
        private readonly CancellationTokenSource _workCts = new();

        /// <summary>Session-owned consume channel. Null before start and after retirement clears it.</summary>
        private IRabbitMqManualAckChannel? _channel;

        /// <summary>Consumer tag from registration. Null before start and after retirement clears it.</summary>
        private string? _consumerTag;

        /// <summary>Admitted deliveries not yet released, including those still waiting for <see cref="_dispatch"/>.</summary>
        private int _inFlight;

        /// <summary>Deliveries that have entered the pipeline processing and have not yet left it.</summary>
        private int _active;

        /// <summary>
        /// Completed when retirement observes no remaining admitted deliveries.
        /// Continuations run asynchronously. Assigned once and not reset.
        /// </summary>
        private readonly TaskCompletionSource _drained = NewDrainSource();

        /// <summary>Local lifecycle. Mutations are made under <see cref="_gate"/>.</summary>
        private ArticleWorkConsumerState _state = ArticleWorkConsumerState.Created;

        /// <summary>Single retirement task. Null until the first <see cref="RetireAsync(CancellationToken)"/> call.</summary>
        private Task? _retireTask;

        /// <summary>One after <see cref="DisposeAsync"/> has entered disposal.</summary>
        private int _disposed;

        /// <summary>
        /// Initializes a new session with the default drain-and-finish shutdown policy for connection slot 1 of 1.
        /// </summary>
        /// <param name="backbone">Provider backbone label.</param>
        /// <param name="prefetch">Per-channel prefetch. Must not be zero.</param>
        /// <param name="pipeline">Parse/classify/settle pipeline.</param>
        /// <param name="connections">Process connection owner. Must not be used to open a second connection.</param>
        /// <param name="logger">Session logger.</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="backbone"/> is null or whitespace.</exception>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="pipeline"/>, <paramref name="connections"/>, or <paramref name="logger"/> is null.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="prefetch"/> is zero.</exception>
        internal ArticleWorkConsumerSession(
            string backbone,
            ushort prefetch,
            ArticleWorkDeliveryPipeline pipeline,
            IRabbitMqService connections,
            ILogger logger)
            : this(backbone, prefetch, pipeline, connections, logger, DefaultShutdown, connectionNumber: 1, connectionLimit: 1)
        {
        }

        /// <summary>
        /// Initializes a new session with a validated shutdown snapshot.
        /// </summary>
        /// <param name="backbone">Provider backbone label.</param>
        /// <param name="prefetch">Per-channel prefetch. Must not be zero.</param>
        /// <param name="pipeline">Parse/classify/settle pipeline.</param>
        /// <param name="connections">Process connection owner. Must not be used to open a second connection.</param>
        /// <param name="logger">Session logger.</param>
        /// <param name="shutdown">Immutable runtime shutdown policy. Must not be re-read from options.</param>
        /// <param name="connectionNumber">One-based consume slot (old NNTP connection number).</param>
        /// <param name="connectionLimit">Desired NNTP slot count for this backbone.</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="backbone"/> is null or whitespace.</exception>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="pipeline"/>, <paramref name="connections"/>, <paramref name="logger"/>, or <paramref name="shutdown"/> is null.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="prefetch"/> is zero, either connection argument is less than 1, or
        /// <paramref name="connectionNumber"/> is greater than <paramref name="connectionLimit"/>.
        /// </exception>
        internal ArticleWorkConsumerSession(
            string backbone,
            ushort prefetch,
            ArticleWorkDeliveryPipeline pipeline,
            IRabbitMqService connections,
            ILogger logger,
            BackFillerShutdownRuntimeOptions shutdown,
            int connectionNumber = 1,
            int connectionLimit = 1)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(backbone);
            ArgumentNullException.ThrowIfNull(pipeline);
            ArgumentNullException.ThrowIfNull(connections);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(shutdown);
            if (prefetch == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(prefetch));
            }

            ArgumentOutOfRangeException.ThrowIfLessThan(connectionNumber, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(connectionLimit, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(connectionNumber, connectionLimit);

            _backbone = backbone;
            _connectionNumber = connectionNumber;
            _connectionLimit = connectionLimit;
            _queue = BackFillerRabbitMqTopology.ComposeProviderEntity(backbone);
            _prefetch = prefetch;
            _pipeline = pipeline;
            _connections = connections;
            _logger = logger;
            _shutdown = shutdown;
        }

        /// <summary>Provider backbone label for this consume session. Not case-folded.</summary>
        internal string Backbone => _backbone;

        /// <summary>One-based slot reconciled against desired NNTP pool capacity for this backbone.</summary>
        internal int ConnectionNumber => _connectionNumber;

        /// <summary>Desired NNTP connection count that bounds how many consume sessions may run for this backbone.</summary>
        private int ConnectionLimit => _connectionLimit;

        /// <summary>Stable reconcile identity <c>backbone/connectionNumber</c> used by consumer reconciliation.</summary>
        internal string SessionKey => ComposeSessionKey(_backbone, _connectionNumber);

        /// <summary>Composes the reconciled key for one consume slot.</summary>
        /// <param name="backbone">Provider backbone label. Not validated or case-folded.</param>
        /// <param name="connectionNumber">One-based slot written with invariant decimal digits.</param>
        /// <returns>The key <c>backbone/connectionNumber</c>.</returns>
        internal static string ComposeSessionKey(string backbone, int connectionNumber) =>
            $"{backbone}/{connectionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        /// <summary>Durable quorum queue name from <see cref="BackFillerRabbitMqTopology.ComposeProviderEntity"/>.</summary>
        internal string Queue => _queue;

        /// <summary>Shutdown policy captured at construction; retirement does not re-read host options.</summary>
        internal BackFillerShutdownRuntimeOptions Shutdown => _shutdown;

        /// <summary>Admitted deliveries waiting for <see cref="_dispatch"/> that have not yet entered pipeline processing.</summary>
        internal int QueuedCount
        {
            get
            {
                lock (_gate)
                {
                    return Math.Max(0, _inFlight - Volatile.Read(ref _active));
                }
            }
        }

        /// <summary>Deliveries that hold <see cref="_dispatch"/> and are inside the pipeline handler.</summary>
        internal int ActiveCount => Volatile.Read(ref _active);

        /// <summary>Cancelled when retirement or dispose must not admit new queued pipeline work.</summary>
        private CancellationToken QueueCancellationToken => _queueCts.Token;

        /// <summary>Cancelled when in-flight handler work must observe shutdown during retirement.</summary>
        internal CancellationToken WorkCancellationToken => _workCts.Token;

        /// <summary>Local session lifecycle under <see cref="_gate"/>.</summary>
        internal ArticleWorkConsumerState State
        {
            get
            {
                lock (_gate)
                {
                    return _state;
                }
            }
        }

        /// <summary>Connection generation of the open consumer channel, or zero before start and after retirement clears it.</summary>
        internal long Generation { get; private set; }

        /// <summary>Manual-ack consume channel used for settlement; null before start and after retirement.</summary>
        internal IRabbitMqManualAckChannel? Channel
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
        /// Optional test seam signalled after the dispatch lock is acquired and before the
        /// delivery is classified as active.
        /// </summary>
        internal TaskCompletionSource? DispatchAcquired { get; set; }

        /// <summary>
        /// Optional test seam that holds a delivery in the queued-to-active transition
        /// until the caller releases it. Wait is still subject to the queued-work policy.
        /// </summary>
        internal TaskCompletionSource? DispatchAcquireHold { get; set; }

        /// <summary>
        /// Opens a caller-owned consumer channel on the current connection generation and registers the consumer.
        /// </summary>
        /// <param name="cancellationToken">
        /// Cancels channel creation and consumer registration.
        /// A session that has already reached <see cref="ArticleWorkConsumerState.Running"/> is not retired by this token.
        /// </param>
        /// <returns>
        /// A task that completes when the session is <see cref="ArticleWorkConsumerState.Running"/>,
        /// or immediately when <see cref="State"/> was not <see cref="ArticleWorkConsumerState.Created"/>.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no current RabbitMQ connection is available. The session is left <see cref="ArticleWorkConsumerState.Stopped"/>.
        /// </exception>
        /// <remarks>
        /// A failed start disposes a channel opened during the attempt, logs the exception message, and leaves the session stopped.
        /// A later call does not retry. Other exceptions from channel or consume setup propagate after that clean-up.
        /// </remarks>
        internal async Task StartAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_state != ArticleWorkConsumerState.Created)
                {
                    return;
                }

                _state = ArticleWorkConsumerState.Starting;
            }

            if (!_connections.TryGetCurrent(out var handle))
            {
                lock (_gate)
                {
                    _state = ArticleWorkConsumerState.Stopped;
                }

                throw new InvalidOperationException("RabbitMQ connection is not ready for Article Work consume.");
            }

            ArticleWorkLogMessages.ConsumerStarting(_logger, _backbone, _queue, handle.Generation);
            IRabbitMqManualAckChannel? channel = null;
            try
            {
                channel = await handle.Connection
                    .CreateManualAckChannelAsync(handle.Generation, cancellationToken)
                    .ConfigureAwait(false);
                if (channel is RabbitMqClientManualAckChannel manualAck)
                {
                    manualAck.UseExclusiveDispose(body => ArticleWorkSettlementLease.RunExclusiveAsync(manualAck, body));
                }

                var tag = await channel
                    .BasicConsumeAsync(_queue, _prefetch, OnDeliveryAsync, cancellationToken)
                    .ConfigureAwait(false);

                lock (_gate)
                {
                    _channel = channel;
                    _consumerTag = tag;
                    Generation = handle.Generation;
                    _state = ArticleWorkConsumerState.Running;
                    channel = null;
                }

                ArticleWorkLogMessages.ConsumerRunning(_logger, _backbone, _queue, Generation, tag);
            }
            catch (Exception ex)
            {
                ArticleWorkLogMessages.ConsumerStartFailed(_logger, _backbone, _queue, ex.Message);
                if (channel is not null)
                {
                    await channel.DisposeAsync().ConfigureAwait(false);
                }

                lock (_gate)
                {
                    _state = ArticleWorkConsumerState.Stopped;
                }

                throw;
            }
        }

        /// <summary>
        /// Retires this session with <see cref="CancellationToken.None"/>.
        /// </summary>
        /// <returns>
        /// The session's single retirement task. This overload supplies no shutdown budget, so it does not
        /// arm the grace callback unless an earlier <see cref="RetireAsync(CancellationToken)"/> already captured a token.
        /// </returns>
        /// <remarks>
        /// Admitted work is waited without a timeout. <see cref="BackFillerShutdownRuntimeOptions.GracePeriod"/> is logged and is not used as a timer here.
        /// </remarks>
        internal Task RetireAsync() => RetireAsync(CancellationToken.None);

        /// <summary>
        /// Cancels the consumer, applies the captured shutdown policy, and waits until admitted work drains or <paramref name="shutdownToken"/> is cancelled.
        /// </summary>
        /// <param name="shutdownToken">
        /// Host shutdown budget. When cancelled, remaining owned work is forced toward
        /// cancellation and the channel is released. This is not a second Article Work timeout.
        /// The first call captures the token. Later calls return the same task and ignore their token.
        /// </param>
        /// <returns>The single retirement task for this session.</returns>
        /// <remarks>
        /// The first call publishes <see cref="_retireTask"/> under <see cref="_gate"/>.
        /// Unless the session is already <see cref="ArticleWorkConsumerState.Stopped"/>, that same critical section
        /// moves the state to <see cref="ArticleWorkConsumerState.Retiring"/> and captures the channel and consumer tag.
        /// When <see cref="BackFillerShutdownRuntimeOptions.DrainQueuedWork"/> is false, this method cancels
        /// <see cref="_queueCts"/> on the caller before it returns, so a delivery still waiting to become active
        /// observes cancellation before the caller continues. Basic.Cancel, the drain wait, and channel disposal
        /// still run after the lock is released.
        /// </remarks>
        internal Task RetireAsync(CancellationToken shutdownToken)
        {
            TaskCompletionSource started;
            Task retireTask;
            lock (_gate)
            {
                if (_retireTask is not null)
                {
                    return _retireTask;
                }

                if (_state == ArticleWorkConsumerState.Stopped)
                {
                    _retireTask = Task.CompletedTask;
                    return _retireTask;
                }

                _state = ArticleWorkConsumerState.Retiring;
                var channel = _channel;
                var tag = _consumerTag;
                started = new TaskCompletionSource();
                retireTask = RetireCoreAsync(channel, tag, started.Task, shutdownToken);
                _retireTask = retireTask;
            }

            if (!_shutdown.DrainQueuedWork)
            {
                _queueCts.Cancel();
            }

            started.TrySetResult();
            return retireTask;
        }

        /// <summary>
        /// Retires the session if retirement has not started, then disposes the queue token, work token, and dispatch semaphore.
        /// </summary>
        /// <returns>A task that completes when retirement and local disposal finish. A second call returns a completed task.</returns>
        /// <remarks>
        /// Calls <see cref="RetireAsync(CancellationToken)"/> with <see cref="CancellationToken.None"/>,
        /// so a first retirement from disposal supplies no shutdown budget.
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await RetireAsync(CancellationToken.None).ConfigureAwait(false);
            _queueCts.Dispose();
            _workCts.Dispose();
            _dispatch.Dispose();
        }

        /// <summary>
        /// Cancels the consumer, applies the captured drain and finish flags, waits for admitted work, and disposes the channel.
        /// </summary>
        /// <param name="channel">Consume channel captured with the transition to <see cref="ArticleWorkConsumerState.Retiring"/>.</param>
        /// <param name="consumerTag">Consumer tag captured with <paramref name="channel"/>.</param>
        /// <param name="started">Completed after <see cref="RetireAsync(CancellationToken)"/> releases <see cref="_gate"/>.</param>
        /// <param name="shutdownToken">
        /// Budget captured by the first <see cref="RetireAsync(CancellationToken)"/> call.
        /// Cancellation forces both work tokens to cancel. <see cref="CancellationToken.None"/> waits until drain without that callback.
        /// </param>
        /// <returns>A task that completes when the session is <see cref="ArticleWorkConsumerState.Stopped"/>.</returns>
        /// <remarks>
        /// The caller has already published <see cref="ArticleWorkConsumerState.Retiring"/> and this task.
        /// This method does not take <see cref="_gate"/> until it clears the channel.
        /// The shutdown callback is registered before Basic.Cancel waits for the settlement gate.
        /// A blocked ACK or NACK therefore cannot postpone grace cancellation.
        /// Basic.Cancel still waits for that operation to release the gate, and the channel is disposed only after this task holds the gate.
        /// Basic.Cancel failures are swallowed.
        /// When queued work is not drained, <see cref="RetireAsync(CancellationToken)"/> has already cancelled
        /// <see cref="_queueCts"/>; the cancel here is idempotent and still runs after Basic.Cancel returns.
        /// When active articles are not finished, <see cref="_workCts"/> is cancelled after Basic.Cancel returns.
        /// The channel is disposed of even when cancel or the wait fails. Generation is left at the value captured at the start.
        /// </remarks>
        private async Task RetireCoreAsync(
            IRabbitMqManualAckChannel? channel,
            string? consumerTag,
            Task started,
            CancellationToken shutdownToken)
        {
            await started.ConfigureAwait(false);

            ArticleWorkLogMessages.ConsumerRetiring(_logger, _backbone, Generation);
            ArticleWorkLogMessages.ConsumerShutdownPolicy(
                _logger,
                _backbone,
                Generation,
                _shutdown.DrainQueuedWork,
                _shutdown.FinishActiveArticles,
                (int)_shutdown.GracePeriod.TotalSeconds);

            // Arm the budget before Basic.Cancel waits on the settlement gate. The wait itself stays
            // uncancellable so the in-flight ACK or NACK still finishes on this channel.
            await using var grace = shutdownToken.Register(OnShutdownGraceExpired);

            if (channel is not null && consumerTag is not null && channel.IsOpen)
            {
                _ = await TryBasicCancelAsync(channel, consumerTag).ConfigureAwait(false);
            }

            if (!_shutdown.DrainQueuedWork)
            {
                await _queueCts.CancelAsync().ConfigureAwait(false);
            }

            if (!_shutdown.FinishActiveArticles)
            {
                await _workCts.CancelAsync().ConfigureAwait(false);
            }

            try
            {
                await WaitForDrainAsync().WaitAsync(shutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                await _queueCts.CancelAsync().ConfigureAwait(false);
                await _workCts.CancelAsync().ConfigureAwait(false);
            }

            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }

            lock (_gate)
            {
                _channel = null;
                _consumerTag = null;
                _state = ArticleWorkConsumerState.Stopped;
            }

            ArticleWorkLogMessages.ConsumerStopped(_logger, _backbone, Generation);
        }

        /// <summary>
        /// Issues Basic.Cancel and contains any failure, so retirement still drains and disposes the channel.
        /// </summary>
        /// <param name="channel">Open consume channel captured for retirement.</param>
        /// <param name="consumerTag">Consumer tag captured with <paramref name="channel"/>.</param>
        /// <returns>
        /// <see langword="true"/> when cancel completed.
        /// <see langword="false"/> when the exception was contained. The caller still continues retirement.
        /// </returns>
        private static async Task<bool> TryBasicCancelAsync(IRabbitMqManualAckChannel channel, string consumerTag)
        {
            try
            {
                await ArticleWorkSettlementLease.RunExclusiveAsync(
                    channel,
                    () => channel.BasicCancelAsync(consumerTag, CancellationToken.None)).ConfigureAwait(false);
                return true;
            }
            // codeql[cs/catch-of-all-exceptions] A session failure must be observed here so the consumer loop stays owned.
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Logs grace expiry and cancels queued and active work.</summary>
        /// <remarks>Runs on the shutdown-token registration callback and does not take <see cref="_gate"/>.</remarks>
        private void OnShutdownGraceExpired()
        {
            ArticleWorkLogMessages.ConsumerShutdownGraceExpired(_logger, _backbone, Generation);
            _queueCts.Cancel();
            _workCts.Cancel();
        }

        /// <summary>
        /// Admits one broker callback, waits for the dispatch lock, and processes it as the single active delivery.
        /// </summary>
        /// <param name="delivery">Manual-ack delivery from the consumption callback. Not settled when admission is refused.</param>
        /// <returns>A task that completes when the delivery has been processed or its cancelled-admission settlement attempt has finished.</returns>
        /// <remarks>
        /// A callback that arrives when the session is not <see cref="ArticleWorkConsumerState.Running"/> returns without ACK or NACK.
        /// Queue-token cancellation or a disposed dispatch semaphore settles the delivery with an already-cancelled processing token.
        /// <see cref="DispatchAcquired"/> is signalled after the lock is taken and before the optional hold.
        /// <see cref="DispatchAcquireHold"/> is waited with the queue token. The active count covers only the pipeline call.
        /// Admission is released after the lock is released. Releasing a disposed semaphore is ignored.
        /// </remarks>
        private async Task OnDeliveryAsync(RabbitMqManualAckDelivery delivery)
        {
            if (!TryAdmit())
            {
                return;
            }

            try
            {
                try
                {
                    await _dispatch.WaitAsync(_queueCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await SettleCancelledAsync(delivery).ConfigureAwait(false);
                    return;
                }
                catch (ObjectDisposedException)
                {
                    await SettleCancelledAsync(delivery).ConfigureAwait(false);
                    return;
                }

                try
                {
                    DispatchAcquired?.TrySetResult();
                    if (DispatchAcquireHold is not null)
                    {
                        try
                        {
                            await DispatchAcquireHold.Task.WaitAsync(_queueCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            await SettleCancelledAsync(delivery).ConfigureAwait(false);
                            return;
                        }
                    }

                    Interlocked.Increment(ref _active);
                    try
                    {
                        await ProcessCurrentAsync(delivery, _workCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _active);
                    }
                }
                finally
                {
                    try
                    {
                        _ = _dispatch.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                        ShutdownObservations.AlreadyDisposed(_logger, "article-work dispatch release");
                    }
                }
            }
            finally
            {
                ReleaseAdmission();
            }
        }

        /// <summary>
        /// Runs the pipeline when the session channel is still the delivery's open generation; otherwise skips settlement.
        /// </summary>
        /// <param name="delivery">Admitted delivery to parse and settle.</param>
        /// <param name="cancellationToken">
        /// Active-work cancellation passed to the pipeline. An already-cancelled token force cancelled settlement for a valid request.
        /// </param>
        /// <returns>A task that completes when the pipeline returns. An invalid-request outcome is logged after that return.</returns>
        /// <remarks>
        /// <c>ChannelStillCurrent</c> is true only while the process connection generation equals the delivery generation,
        /// that connection is still current, the session still references this channel, and the channel is open.
        /// It is a local function because the pipeline must evaluate it again at settlement time.
        /// The invalid-request log uses the diagnostic reason from the pipeline and does not reparse the body with an unbounded limit.
        /// </remarks>
        private async Task ProcessCurrentAsync(RabbitMqManualAckDelivery delivery, CancellationToken cancellationToken)
        {
            IRabbitMqManualAckChannel? channel;
            lock (_gate)
            {
                channel = _channel;
            }

            if (channel is null || !channel.IsOpen || channel.Generation != delivery.Generation)
            {
                ArticleWorkLogMessages.StaleSettlementSkipped(_logger, _backbone, delivery.Generation, delivery.DeliveryTag);
                return;
            }

            bool ChannelStillCurrent() =>
                _connections.TryGetCurrent(out var handle)
                && handle.Generation == delivery.Generation
                && handle.IsCurrent
                && ReferenceEquals(_channel, channel)
                && channel.IsOpen;

            // Perform the initial bounded parse here so we capture the failure reason and
            // prevent any downstream code from reparsing the body with a different limit.
            var parsed = ArticleWorkRequestParser.Parse(delivery, _backbone, _pipeline.MaxPayloadBytes);

            var outcome = await _pipeline
                .ProcessAsync(delivery, _backbone, channel, ChannelStillCurrent, cancellationToken, parsed)
                .ConfigureAwait(false);

            if (outcome == ArticleWorkOutcome.InvalidRequest)
            {
                var reason = parsed.IsValid ? "InvalidRequest" : parsed.Failure?.Reason ?? "InvalidRequest";
                ArticleWorkLogMessages.RequestRejected(
                    _logger,
                    _backbone,
                    delivery.Generation,
                    delivery.DeliveryTag,
                    reason);
            }
        }

        /// <summary>
        /// Settles a delivery that was admitted but must not start by processing it with an already-cancelled token.
        /// </summary>
        /// <param name="delivery">Admitted delivery whose dispatch wait was cancelled or whose semaphore was disposed.</param>
        /// <returns>The <see cref="ProcessCurrentAsync"/> task for that cancelled processing token.</returns>
        /// <remarks>
        /// A valid request becomes <see cref="ArticleWorkOutcome.Cancelled"/>. An invalid payload still follows the pipeline's invalid-request path.
        /// </remarks>
        private Task SettleCancelledAsync(RabbitMqManualAckDelivery delivery) =>
            ProcessCurrentAsync(delivery, new CancellationToken(canceled: true));

        /// <summary>Admits a callback only while the session is running.</summary>
        /// <returns>
        /// <see langword="true"/> when the in-flight count was incremented.
        /// <see langword="false"/> when the session is not <see cref="ArticleWorkConsumerState.Running"/>; this method does not settle the delivery.
        /// </returns>
        private bool TryAdmit()
        {
            lock (_gate)
            {
                if (_state != ArticleWorkConsumerState.Running)
                {
                    return false;
                }

                _inFlight++;
                return true;
            }
        }

        /// <summary>
        /// Decrements the admitted count and completes drain when retirement is waiting and nothing admitted remains.
        /// </summary>
        private void ReleaseAdmission()
        {
            lock (_gate)
            {
                _inFlight--;
                if (_inFlight <= 0 && _state == ArticleWorkConsumerState.Retiring)
                {
                    _drained.TrySetResult();
                }
            }
        }

        /// <summary>Returns a task that completes when no admitted delivery remains.</summary>
        /// <returns>
        /// A completed task when the in-flight count is already zero; otherwise <see cref="_drained"/>.
        /// The drain source is signalled only from <see cref="ReleaseAdmission"/> while the state is <see cref="ArticleWorkConsumerState.Retiring"/>.
        /// </returns>
        private Task WaitForDrainAsync()
        {
            lock (_gate)
            {
                if (_inFlight <= 0)
                {
                    return Task.CompletedTask;
                }

                return _drained.Task;
            }
        }

        /// <summary>Creates the drain signal used for one retirement wait.</summary>
        /// <returns>A completion source whose continuations are scheduled asynchronously.</returns>
        private static TaskCompletionSource NewDrainSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

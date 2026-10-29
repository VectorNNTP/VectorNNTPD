using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using VectorNNTPD.TransitService.Configuration;
using VectorNNTPD.TransitService.Diagnostics;
using VectorNNTPD.TransitService.RabbitMq;
using VectorNNTPD.Common.Core;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.TransitService.ArticleIngestion.OverviewDb
{
    /// <summary>
    /// Publishes OverviewDB protobuf payloads to <c>overviewdb.queue</c> with an asynchronous
    /// outstanding-confirmation window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses one confirm-enabled RabbitMQ channel (library tracking disabled) so
    /// <c>BasicPublishAsync</c> returns after the write. Confirms arrive via
    /// <c>BasicAcksAsync</c> / <c>BasicNacksAsync</c> / <c>BasicReturnAsync</c> and are
    /// correlated by publish sequence number. Channel publishes are serialized; callbacks
    /// only update outstanding state and must not publish.
    /// </para>
    /// <para>
    /// Crash or shutdown loss of outstanding unconfirmed OverviewDB work is acceptable.
    /// </para>
    /// </remarks>
    internal sealed class OverviewDbHandoffPublisher : IOverviewDbHandoffPublisher, IAsyncDisposable
    {
        private readonly IRabbitMqService _rabbitMq;
        private readonly IOptions<NntpdOptions> _nntpdOptions;
        private readonly IngestionPipelineMetrics? _pipeline;
        private readonly SemaphoreSlim _publishGate = new(1, 1);
        private readonly ConcurrentDictionary<ulong, OverviewDbWorkItem> _outstanding = new();
        private readonly ConcurrentDictionary<ulong, byte> _returned = new();
        private readonly Channel<OverviewDbWorkItem> _failures = Channel.CreateUnbounded<OverviewDbWorkItem>(
            new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        private SemaphoreSlim? _outstandingWindow;
        private int _windowSize;
        private IRabbitMqAsyncConfirmPublishChannel? _channel;
        private int _disposed;

        /// <summary>Initializes a new OverviewDB handoff publisher.</summary>
        public OverviewDbHandoffPublisher(
            IRabbitMqService rabbitMq,
            IOptions<NntpdOptions> nntpdOptions,
            IOptions<RabbitMqOptions> rabbitMqOptions,
            IngestionPipelineMetrics? pipelineMetrics = null)
        {
            ArgumentNullException.ThrowIfNull(rabbitMq);
            ArgumentNullException.ThrowIfNull(nntpdOptions);
            ArgumentNullException.ThrowIfNull(rabbitMqOptions);
            _rabbitMq = rabbitMq;
            _nntpdOptions = nntpdOptions;
            _pipeline = pipelineMetrics;
            _ = rabbitMqOptions;
        }

        /// <inheritdoc />
        public int OutstandingCount => _outstanding.Count;

        /// <summary>Gets the configured outstanding window size (tests).</summary>
        internal int WindowSize => Volatile.Read(ref _windowSize);

        /// <inheritdoc />
        public async Task PublishAsync(OverviewDbWorkItem item, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

            var fqdn = _nntpdOptions.Value.Fqdn;
            if (string.IsNullOrWhiteSpace(fqdn))
            {
                throw new InvalidOperationException("OverviewDB handoff requires the generated application FQDN.");
            }

            var window = EnsureOutstandingWindow();
            var publishStart = System.Diagnostics.Stopwatch.GetTimestamp();
            await window.WaitAsync(cancellationToken).ConfigureAwait(false);
            _pipeline?.RecordGateWait(publishStart);
            _pipeline?.ObserveInFlightPublishes(OutstandingCount + 1);

            try
            {
                await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var channel = await EnsureChannelAlreadyLockedAsync(cancellationToken).ConfigureAwait(false);
                    var sequence = await channel
                        .GetNextPublishSequenceNumberAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (!_outstanding.TryAdd(sequence, item))
                    {
                        throw new InvalidOperationException(
                            $"Duplicate OverviewDB publish sequence number {sequence}.");
                    }

                    try
                    {
                        var writeStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        await channel.PublishAsync(
                                OverviewDbTopology.DefaultExchange,
                                OverviewDbTopology.RoutingKey,
                                Guid.NewGuid().ToString(),
                                fqdn,
                                OverviewDbTopology.ExpirationMilliseconds,
                                sequence,
                                item.Payload,
                                cancellationToken)
                            .ConfigureAwait(false);
                        // Write latency only; broker confirms are asynchronous.
                        _pipeline?.RecordPublishAndConfirm(writeStart);
                        _pipeline?.RecordHandoff(publishStart);
                    }
                    // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                    catch
                    {
                        _outstanding.TryRemove(sequence, out _);
                        _returned.TryRemove(sequence, out _);
                        throw;
                    }
                }
                finally
                {
                    _publishGate.Release();
                }
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch
            {
                ReleaseWindowSlot();
                _pipeline?.RecordConfirmFailure();
                _pipeline?.ObserveInFlightPublishes(OutstandingCount);
                throw;
            }

            _pipeline?.ObserveInFlightPublishes(OutstandingCount);
        }

        /// <inheritdoc />
        public bool TryDequeuePublishFailure(out OverviewDbWorkItem item) =>
            _failures.Reader.TryRead(out item!);

        /// <inheritdoc />
        public void AbandonOutstanding()
        {
            // codeql[cs/linq/missed-where] ConcurrentDictionary iteration with TryRemove side effects.
            foreach (var pair in _outstanding)
            {
                if (_outstanding.TryRemove(pair.Key, out _))
                {
                    _returned.TryRemove(pair.Key, out _);
                    ReleaseWindowSlot();
                }
            }

            while (_failures.Reader.TryRead(out _))
            {
                // Drain residual failure notifications before observe.
            }

            _pipeline?.ObserveInFlightPublishes(OutstandingCount);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            AbandonOutstanding();
            await DisposeChannelAsync().ConfigureAwait(false);
            _publishGate.Dispose();
            Interlocked.Exchange(ref _outstandingWindow, null)?.Dispose();
        }

        private SemaphoreSlim EnsureOutstandingWindow()
        {
            var existing = Volatile.Read(ref _outstandingWindow);
            if (existing is not null)
            {
                return existing;
            }

            var size = _nntpdOptions.Value.ArticleIngestion?.OverviewDbPublisherBatchSize
                ?? ArticleIngestionOptions.DefaultOverviewDbPublisherBatchSize;
            if (size < 1)
            {
                size = 1;
            }

            var created = new SemaphoreSlim(size, size);
            // codeql[cs/missed-using-statement] Ownership transfers into _outstandingWindow on successful CompareExchange; using would dispose the live window. toDispose is cleared only after publish succeeds.
            SemaphoreSlim? toDispose = created;
            try
            {
                var prior = Interlocked.CompareExchange(ref _outstandingWindow, created, null);
                if (prior is not null)
                {
                    return prior;
                }

                toDispose = null;
                Volatile.Write(ref _windowSize, size);
                return created;
            }
            finally
            {
                toDispose?.Dispose();
            }
        }

        /// <summary>
        /// Ensures a usable async-confirm channel. Caller must hold <see cref="_publishGate"/>.
        /// </summary>
        private async Task<IRabbitMqAsyncConfirmPublishChannel> EnsureChannelAlreadyLockedAsync(
            CancellationToken cancellationToken)
        {
            if (!_rabbitMq.TryGetCurrent(out var handle) || !handle.IsCurrent)
            {
                throw new InvalidOperationException("RabbitMQ connection is not ready for OverviewDB handoff.");
            }

            var existing = _channel;
            if (existing is not null
                && existing.Generation == handle.Generation
                && existing.IsOpen)
            {
                return existing;
            }

            if (existing is not null)
            {
                DetachHandlers(existing);
                try
                {
                    await existing.DisposeAsync().ConfigureAwait(false);
                }
                // codeql[cs/catch-of-all-exceptions] Best-effort channel dispose must not fail channel recovery.
                catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
                {
                    _ = ignored; // observed; intentionally ignored during channel recovery dispose
                }

                FailOutstandingForChannelLoss();
                _channel = null;
            }

            var created = await handle.Connection
                .CreateAsyncConfirmPublishChannelAsync(handle.Generation, cancellationToken)
                .ConfigureAwait(false);
            AttachHandlers(created);
            _channel = created;
            return created;
        }

        private void AttachHandlers(IRabbitMqAsyncConfirmPublishChannel channel)
        {
            channel.BasicAcksAsync += OnBasicAckAsync;
            channel.BasicNacksAsync += OnBasicNackAsync;
            channel.BasicReturnAsync += OnBasicReturnAsync;
        }

        private void DetachHandlers(IRabbitMqAsyncConfirmPublishChannel channel)
        {
            channel.BasicAcksAsync -= OnBasicAckAsync;
            channel.BasicNacksAsync -= OnBasicNackAsync;
            channel.BasicReturnAsync -= OnBasicReturnAsync;
        }

        private Task OnBasicAckAsync(object sender, BasicAckEventArgs args)
        {
            // Lightweight: settle outstanding only — never publish from this callback.
            CompleteOutstanding(args.DeliveryTag, args.Multiple, nack: false);
            return Task.CompletedTask;
        }

        private Task OnBasicNackAsync(object sender, BasicNackEventArgs args)
        {
            CompleteOutstanding(args.DeliveryTag, args.Multiple, nack: true);
            return Task.CompletedTask;
        }

        private Task OnBasicReturnAsync(object sender, BasicReturnEventArgs args)
        {
            // Mark returned; settlement (window release + failure dequeue) waits for ack/nack.
            // RabbitMQ typically acks after returning a mandatory unroutable message.
            if (TryReadPublishSequence(args.BasicProperties, out var sequence))
            {
                _returned[sequence] = 0;
            }

            return Task.CompletedTask;
        }

        private void CompleteOutstanding(ulong deliveryTag, bool multiple, bool nack)
        {
            if (multiple)
            {
                // codeql[cs/linq/missed-where] ConcurrentDictionary keys with SettleOne side effects.
                foreach (var key in _outstanding.Keys)
                {
                    if (key <= deliveryTag)
                    {
                        SettleOne(key, nack);
                    }
                }
            }
            else
            {
                SettleOne(deliveryTag, nack);
            }

            _pipeline?.ObserveInFlightPublishes(OutstandingCount);
        }

        private void SettleOne(ulong sequence, bool nack)
        {
            if (!_outstanding.TryRemove(sequence, out var item))
            {
                return;
            }

            var wasReturned = _returned.TryRemove(sequence, out _);
            ReleaseWindowSlot();

            if (nack || wasReturned)
            {
                _ = _failures.Writer.TryWrite(item);
                _pipeline?.RecordConfirmFailure();
            }
        }

        private void FailOutstandingForChannelLoss()
        {
            // codeql[cs/linq/missed-where] ConcurrentDictionary iteration with TryRemove side effects.
            foreach (var pair in _outstanding)
            {
                if (_outstanding.TryRemove(pair.Key, out var item))
                {
                    _returned.TryRemove(pair.Key, out _);
                    _ = _failures.Writer.TryWrite(item);
                    ReleaseWindowSlot();
                    _pipeline?.RecordConfirmFailure();
                }
            }
        }

        private void ReleaseWindowSlot()
        {
            try
            {
                EnsureOutstandingWindow().Release();
            }
            catch (ObjectDisposedException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
            catch (SemaphoreFullException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
        }

        private async Task DisposeChannelAsync()
        {
            var channel = Interlocked.Exchange(ref _channel, null);
            if (channel is null)
            {
                return;
            }

            DetachHandlers(channel);
            try
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Best-effort channel dispose must not fail publisher teardown.
            catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
            {
                _ = ignored; // observed; intentionally ignored during publisher teardown dispose
            }
        }

        /// <summary>
        /// Recovers the publish sequence from Basic.Return headers.
        /// </summary>
        /// <remarks>
        /// Matches RabbitMQ.Client 7.2.2 <c>HandleReturn</c> representations:
        /// <c>long</c>, decimal <c>string</c>, and longstr <c>byte[]</c> (ASCII digits).
        /// </remarks>
        internal static bool TryReadPublishSequence(IReadOnlyBasicProperties? properties, out ulong sequence)
        {
            sequence = 0;
            if (properties?.Headers is null
                || !properties.Headers.TryGetValue(Constants.PublishSequenceNumberHeader, out var raw)
                || raw is null)
            {
                return false;
            }

            return TryParsePublishSequenceNumberHeader(raw, out sequence);
        }

        /// <summary>
        /// Parses an AMQP table value written for <see cref="Constants.PublishSequenceNumberHeader"/>.
        /// </summary>
        internal static bool TryParsePublishSequenceNumberHeader(object raw, out ulong sequence)
        {
            switch (raw)
            {
                case long l when l >= 0:
                    sequence = (ulong)l;
                    return true;
                case int i when i >= 0:
                    sequence = (ulong)i;
                    return true;
                case string s:
                    return ulong.TryParse(
                        s,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out sequence);
                case byte[] bytes:
                    // Wire longstr ('S') for a decimal string arrives as UTF-8/ASCII bytes.
                    return ulong.TryParse(
                        Encoding.ASCII.GetString(bytes),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out sequence);
                default:
                    sequence = 0;
                    return false;
            }
        }
    }
}

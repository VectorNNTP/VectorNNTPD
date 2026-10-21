using System.Runtime.CompilerServices;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.ArticleWork
{
    /// <summary>
    /// Exactly-once settlement token bound to one channel generation and delivery tag.
    /// </summary>
    /// <remarks>
    /// Broker ACK, NACK, Basic.Cancel, and the consume-channel dispose mark share one gate per wrapper.
    /// A cancelled queued delivery may settle while an active article is still inside the handler, and those
    /// channel calls must not overlap. Client close runs after the gate is released.
    /// </remarks>
    internal sealed class ArticleWorkSettlementLease
    {
        /// <summary>
        /// One gate per consume-channel wrapper. A replacement wrapper owns a new RabbitMQ channel and therefore a new gate.
        /// The table drops the gate when the wrapper is collected. <see cref="SemaphoreSlim"/> here is unnamed, so it has no wait handle to dispose.
        /// </summary>
        private static readonly ConditionalWeakTable<IRabbitMqManualAckChannel, SettlementGate> SettlementGates = [];

        /// <summary>Original consumer channel. Settlement never moves to a replacement channel.</summary>
        private readonly IRabbitMqManualAckChannel _channel;

        /// <summary>Channel-scoped delivery tag passed to Basic.Ack or Basic.Reject.</summary>
        private readonly ulong _deliveryTag;

        /// <summary>Connection generation the channel must still report for the settlement to proceed.</summary>
        private readonly long _generation;

        /// <summary>
        /// One after this lease has claimed its only settlement attempt.
        /// Stays set when the broker RPC then fails.
        /// </summary>
        private int _settled;

        /// <summary>
        /// Initializes a new settlement lease.
        /// </summary>
        /// <param name="channel">Original consumer channel. Must not be a replacement channel.</param>
        /// <param name="deliveryTag">Channel-scoped delivery tag.</param>
        /// <param name="generation">Connection generation captured at admission.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="channel"/> is null.</exception>
        internal ArticleWorkSettlementLease(IRabbitMqManualAckChannel channel, ulong deliveryTag, long generation)
        {
            ArgumentNullException.ThrowIfNull(channel);
            _channel = channel;
            _deliveryTag = deliveryTag;
            _generation = generation;
        }

        /// <summary>
        /// Whether this lease has already consumed its one settlement attempt, including when the broker RPC then failed.
        /// </summary>
        /// <remarks>
        /// Set before the broker RPC. Remains <see langword="true"/> when that RPC throws, so the failed attempt is not retried on this lease.
        /// </remarks>
        internal bool IsSettled => Volatile.Read(ref _settled) == 1;

        /// <summary>
        /// Attempts ACK or NACK on the original channel only.
        /// </summary>
        /// <param name="disposition">Settlement plan.</param>
        /// <param name="channelStillCurrent">
        /// <see langword="true"/> when the lease generation is still the process current generation
        /// and the channel is still the session channel.
        /// </param>
        /// <param name="cancellationToken">
        /// Observed after this call acquires the channel gate and before the lease is claimed.
        /// A cancelled token does not call the broker and does not claim the lease, so a later call can still settle once.
        /// Article Work passes <see cref="CancellationToken.None"/> because broker settlement is not cancelled with the handler.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when this call performed settlement on the original channel.
        /// <see langword="false"/> when the lease was already settled, the token was cancelled before the claim,
        /// the channel is stale, or the broker RPC failed. A failed RPC leaves the lease claimed.
        /// </returns>
        internal Task<bool> TrySettleAsync(
            ArticleWorkDisposition disposition,
            bool channelStillCurrent,
            CancellationToken cancellationToken) =>
            ExclusiveAsync(_channel, async () =>
            {
                if (cancellationToken.IsCancellationRequested
                    || !channelStillCurrent
                    || _channel.Generation != _generation
                    || !_channel.IsOpen)
                {
                    return false;
                }

                if (Interlocked.Exchange(ref _settled, 1) == 1)
                {
                    return false;
                }

                try
                {
                    if (disposition.Acknowledge)
                    {
                        await _channel.BasicAckAsync(_deliveryTag, CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        // basic.reject (not basic.nack): quorum x-delivery-count must increment on RMQ 4.3+.
                        await _channel.BasicRejectAsync(_deliveryTag, disposition.Requeue, CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    return true;
                }
                // codeql[cs/catch-of-all-exceptions] Settlement cleanup must finish even when an unexpected exception is thrown.
                catch (Exception)
                {
                    return false;
                }
            });

        /// <summary>
        /// Runs <paramref name="action"/> with exclusive use of <paramref name="channel"/>'s settlement gate.
        /// </summary>
        /// <param name="channel">Consume-channel wrapper whose broker operations must not overlap settlement.</param>
        /// <param name="action">Channel operation, such as Basic.Cancel during retirement.</param>
        /// <returns>A task that completes when <paramref name="action"/> finishes and the gate is released.</returns>
        internal static Task RunExclusiveAsync(IRabbitMqManualAckChannel channel, Func<Task> action)
        {
            ArgumentNullException.ThrowIfNull(channel);
            ArgumentNullException.ThrowIfNull(action);
            return ExclusiveAsync(channel, async () =>
            {
                await action().ConfigureAwait(false);
                return true;
            });
        }

        /// <summary>
        /// Serializes <paramref name="action"/> with ACK, NACK, and other exclusive operations on <paramref name="channel"/>.
        /// </summary>
        /// <typeparam name="T">Result of <paramref name="action"/>.</typeparam>
        /// <param name="channel">Consume-channel wrapper.</param>
        /// <param name="action">Operation that uses the underlying channel.</param>
        /// <returns>The result of <paramref name="action"/>.</returns>
        /// <remarks>
        /// The wait itself is not cancelled. Cancelling it would drop the waiter without deciding the delivery.
        /// The gate is released when <paramref name="action"/> completes or throws.
        /// </remarks>
        private static async Task<T> ExclusiveAsync<T>(IRabbitMqManualAckChannel channel, Func<Task<T>> action)
        {
            var gate = SettlementGates.GetValue(channel, static _ => new SettlementGate());
            Interlocked.Increment(ref gate.Waiters);
            try
            {
                await gate.Semaphore.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref gate.Waiters);
            }

            try
            {
                return await action().ConfigureAwait(false);
            }
            finally
            {
                gate.Semaphore.Release();
            }
        }

        /// <summary>
        /// Count of callers blocked on the per-channel settlement gate before they may use <paramref name="channel"/>.
        /// </summary>
        /// <param name="channel">Consume channel whose gate should be observed.</param>
        /// <returns>The waiter count, or zero when that channel has not settled yet.</returns>
        internal static int SettlementWaiters(IRabbitMqManualAckChannel channel)
        {
            ArgumentNullException.ThrowIfNull(channel);
            return SettlementGates.TryGetValue(channel, out var gate)
                ? Volatile.Read(ref gate.Waiters)
                : 0;
        }

        /// <summary>
        /// Returns whether <paramref name="channel"/> is the original lease channel at the captured generation.
        /// </summary>
        /// <param name="channel">Candidate channel.</param>
        /// <returns><see langword="true"/> when settlement may use this channel.</returns>
        internal bool IsOriginalChannel(IRabbitMqManualAckChannel? channel) =>
            channel is not null
            && ReferenceEquals(channel, _channel)
            && channel.Generation == _generation;

        /// <summary>Async mutex and waiter count for one consume channel.</summary>
        private sealed class SettlementGate
        {
            /// <summary>Settlements currently blocked before they may call the channel.</summary>
            internal int Waiters;

            /// <summary>Allows one ACK or NACK on the channel.</summary>
            internal SemaphoreSlim Semaphore { get; } = new(1, 1);
        }
    }
}

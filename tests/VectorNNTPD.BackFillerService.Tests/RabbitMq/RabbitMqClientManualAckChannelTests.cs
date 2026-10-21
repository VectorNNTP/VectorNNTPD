using System.Reflection;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.Tests.RabbitMq
{
    /// <summary>
    /// Manual-ack channel consume, cancellation, and disposal against Common's channel wrapper.
    /// </summary>

    public sealed class RabbitMqClientManualAckChannelTests
    {
        /// <summary>Maximum wait for lifecycle gates and async completions in these tests.</summary>

        private static readonly TimeSpan SafetyBound = TimeSpan.FromSeconds(2);

        /// <summary>
        /// A completed consume receives one delivery, then disposal closes the channel and ignores a later delivery.
        /// </summary>
        [Fact]
        public async Task Completed_consume_receives_one_delivery_and_disposal_closes_once()
        {
            var (channel, broker) = Create();
            var deliveries = 0;
            var consume = channel.BasicConsumeAsync("work", 10, CountDelivery, CancellationToken.None);

            await broker.Entered.Task.WaitAsync(SafetyBound);
            Assert.Equal(["registered"], broker.Snapshot());
            Assert.Equal(0, broker.CloseCalls);
            await DeliverAsync(broker.Captured!);
            Assert.Equal(1, Volatile.Read(ref deliveries));

            broker.Release.TrySetResult();
            Assert.Equal("ctag-1", await consume.WaitAsync(SafetyBound));
            await channel.DisposeAsync();

            Assert.Equal(["registered", "consume-returning", "close", "disposed"], broker.Snapshot());
            Assert.Empty(broker.Registered);
            Assert.False(channel.IsOpen);
            await DeliverAsync(broker.Captured!);
            Assert.Equal(1, Volatile.Read(ref deliveries));

            Task CountDelivery(RabbitMqManualAckDelivery _)
            {
                Interlocked.Increment(ref deliveries);
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// After disposal has begun, consume throws <see cref="ObjectDisposedException"/> before broker registration.
        /// </summary>
        [Fact]
        public async Task Disposal_rejects_consume_before_registration()
        {
            var (channel, broker) = Create();
            broker.HoldClose = true;
            var dispose = channel.DisposeAsync();
            await broker.CloseEntered.Task.WaitAsync(SafetyBound);

            var rejected = await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                channel.BasicConsumeAsync("work", 10, static _ => Task.CompletedTask, CancellationToken.None));

            Assert.Equal(typeof(RabbitMqClientManualAckChannel).FullName, rejected.ObjectName);
            Assert.Equal(0, broker.ConsumeCalls);
            Assert.False(channel.IsOpen);

            broker.CloseRelease.TrySetResult();
            await dispose;
            Assert.Equal(["close", "disposed"], broker.Snapshot());
        }

        /// <summary>
        /// A failed broker consume detaches the handler, leaves no registered consumer, and allows a later consume.
        /// </summary>
        [Fact]
        public async Task A_failed_registration_detaches_the_handler_and_allows_retry()
        {
            var (channel, broker) = Create();
            broker.ConsumeFailure = new InvalidOperationException("consume rejected");
            var deliveries = 0;

            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                channel.BasicConsumeAsync("work", 10, CountDelivery, CancellationToken.None));

            Assert.Equal("consume rejected", rejected.Message);
            Assert.Equal(["consume-failed"], broker.Snapshot());
            Assert.Empty(broker.Registered);
            Assert.Equal(0, broker.CloseCalls);
            await DeliverAsync(broker.Captured!);
            Assert.Equal(0, Volatile.Read(ref deliveries));

            broker.ConsumeFailure = null;
            broker.Release.TrySetResult();
            Assert.Equal("ctag-1", await channel.BasicConsumeAsync("work", 10, static _ => Task.CompletedTask, CancellationToken.None));
            Assert.Single(broker.Registered);
            Assert.Equal(0, broker.CloseCalls);

            Task CountDelivery(RabbitMqManualAckDelivery _)
            {
                Interlocked.Increment(ref deliveries);
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Canceling consume before the broker registers a consumer detaches the handler and leaves the broker list empty.
        /// </summary>
        [Fact]
        public async Task Cancellation_during_registration_detaches_the_handler()
        {
            var (channel, broker) = Create();
            broker.CancelDuringConsume = true;
            using var cancellation = new CancellationTokenSource();
            var deliveries = 0;
            var consume = channel.BasicConsumeAsync("work", 10, CountDelivery, cancellation.Token);

            await broker.Entered.Task.WaitAsync(SafetyBound);
            Assert.Empty(broker.Registered);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consume);
            await DeliverAsync(broker.Captured!);
            Assert.Equal(0, Volatile.Read(ref deliveries));
            Assert.Empty(broker.Registered);
            Assert.Equal(0, broker.CloseCalls);

            broker.CancelDuringConsume = false;
            broker.Release.TrySetResult();
            Assert.Equal("ctag-1", await channel.BasicConsumeAsync("work", 10, static _ => Task.CompletedTask, CancellationToken.None));
            Assert.Single(broker.Registered);

            Task CountDelivery(RabbitMqManualAckDelivery _)
            {
                Interlocked.Increment(ref deliveries);
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// A second consume waits for the first registration, then throws without registering another consumer.
        /// </summary>
        [Fact]
        public async Task A_second_consume_is_rejected_after_the_first_registration()
        {
            var (channel, broker) = Create();
            var first = channel.BasicConsumeAsync("work", 10, static _ => Task.CompletedTask, CancellationToken.None);
            await broker.Entered.Task.WaitAsync(SafetyBound);

            var second = channel.BasicConsumeAsync("work", 10, static _ => Task.CompletedTask, CancellationToken.None);
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => channel.LifecycleWaiters >= 1, SafetyBound);
            Assert.Equal(1, broker.ConsumeCalls);
            Assert.Single(broker.Registered);

            broker.Release.TrySetResult();
            Assert.Equal("ctag-1", await first.WaitAsync(SafetyBound));
            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => second);
            Assert.Equal("RabbitMQ manual-ack channel already has a consumer.", rejected.Message);
            Assert.Equal(1, broker.ConsumeCalls);
            Assert.Single(broker.Registered);
        }

        /// <summary>
        /// Concurrent DisposeAsync closes underlying channel exactly once.
        /// </summary>

        [Fact]

        public async Task Concurrent_disposal_closes_the_client_channel_once()
        {
            var (channel, broker) = Create();
            broker.HoldClose = true;
            var first = channel.DisposeAsync();
            await broker.CloseEntered.Task.WaitAsync(SafetyBound);

            var second = channel.DisposeAsync();
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => channel.LifecycleWaiters >= 1, SafetyBound);

            Assert.Equal(1, broker.CloseCalls);
            Assert.Equal(0, broker.DisposeCalls);
            Assert.False(second.IsCompleted);

            broker.CloseRelease.TrySetResult();
            await first;
            await second;

            Assert.Equal(1, broker.CloseCalls);
            Assert.Equal(1, broker.DisposeCalls);
            Assert.False(channel.IsOpen);
            Assert.Equal(["close", "disposed"], broker.Snapshot());
        }

        /// <summary>
        /// Disposal waits until an in-flight registration finishes, then closes the client channel once.
        /// The handler from that registration is detached and does not receive a later delivery.
        /// </summary>
        [Fact]
        public async Task Disposal_waits_for_in_flight_registration_and_detaches_the_handler()
        {
            var (channel, broker) = Create();
            var deliveries = 0;
            var consume = channel.BasicConsumeAsync("work", 10, CountDelivery, CancellationToken.None);
            await broker.Entered.Task.WaitAsync(SafetyBound);

            var dispose = channel.DisposeAsync();
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => channel.LifecycleWaiters >= 1, SafetyBound);
            Assert.Equal(0, broker.CloseCalls);

            broker.Release.TrySetResult();
            Assert.Equal("ctag-1", await consume.WaitAsync(SafetyBound));
            await dispose;

            Assert.Equal(1, broker.CloseCalls);
            Assert.Equal(1, broker.DisposeCalls);
            Assert.False(channel.IsOpen);
            await DeliverAsync(broker.Captured!);
            Assert.Equal(0, Volatile.Read(ref deliveries));

            Task CountDelivery(RabbitMqManualAckDelivery _)
            {
                Interlocked.Increment(ref deliveries);
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// The exclusive dispose hook marks the channel before the client channel is closed.
        /// </summary>
        [Fact]
        public async Task Dispose_invokes_the_exclusive_hook_before_close()
        {
            var (channel, broker) = Create();
            var order = new List<string>();
            channel.UseExclusiveDispose(async body =>
            {
                order.Add("hook-enter");
                await body().ConfigureAwait(false);
                order.Add("hook-exit");
            });

            await channel.DisposeAsync();

            Assert.Equal(["hook-enter", "hook-exit"], order);
            Assert.Equal(["close", "disposed"], broker.Snapshot());
            Assert.False(channel.IsOpen);
        }

        /// <summary>
        /// A hook that throws after marking the channel still closes the client channel once, then propagates.
        /// </summary>
        [Fact]
        public async Task Dispose_closes_the_client_channel_when_the_exclusive_hook_throws_after_the_mark()
        {
            var (channel, broker) = Create();
            channel.UseExclusiveDispose(async body =>
            {
                await body().ConfigureAwait(false);
                throw new InvalidOperationException("hook failed");
            });

            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => channel.DisposeAsync().AsTask());

            Assert.Equal("hook failed", rejected.Message);
            Assert.Equal(["close", "disposed"], broker.Snapshot());
            Assert.False(channel.IsOpen);

            await Assert.ThrowsAsync<InvalidOperationException>(() => channel.DisposeAsync().AsTask());
            Assert.Equal(1, broker.CloseCalls);
            Assert.Equal(1, broker.DisposeCalls);
        }

        /// <summary>
        /// A hook that throws before the mark still closes the client channel once, then propagates.
        /// </summary>
        [Fact]
        public async Task Dispose_closes_the_client_channel_when_the_exclusive_hook_throws_before_the_mark()
        {
            var (channel, broker) = Create();
            channel.UseExclusiveDispose(static _ => throw new InvalidOperationException("hook failed"));

            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => channel.DisposeAsync().AsTask());

            Assert.Equal("hook failed", rejected.Message);
            Assert.Equal(["close", "disposed"], broker.Snapshot());
            Assert.False(channel.IsOpen);
        }

        /// <summary>
        /// Consume on closed channel fails before broker registration.
        /// </summary>

        [Fact]

        public async Task A_closed_channel_is_rejected_before_consumer_registration()
        {
            var (channel, broker) = Create();
            broker.IsOpen = false;

            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                channel.BasicConsumeAsync("work", 10, static _ => Task.CompletedTask, CancellationToken.None));

            Assert.Equal("RabbitMQ channel is not open for consume.", rejected.Message);
            Assert.Equal(0, broker.ConsumeCalls);
            Assert.Empty(broker.Snapshot());
        }

        /// <summary>Creates a manual-ack channel over a dispatch-proxy broker double.</summary>
        private static (RabbitMqClientManualAckChannel Channel, ConsumeChannelDouble Broker) Create()
        {
            var broker = ConsumeChannelDouble.Create();
            return (new RabbitMqClientManualAckChannel(broker.Channel, generation: 3), broker);
        }

        /// <summary>
        /// Invokes <see cref="AsyncEventingBasicConsumer.HandleBasicDeliverAsync"/> with empty body for lifecycle tests.
        /// Does not construct a <see cref="RabbitMqManualAckDelivery"/>.
        /// </summary>
        /// <param name="consumer">Consumer registered by the channel under test.</param>

        private static Task DeliverAsync(IAsyncBasicConsumer consumer)
        {
            var eventing = Assert.IsType<AsyncEventingBasicConsumer>(consumer);
            return eventing.HandleBasicDeliverAsync(
                "ctag-1",
                deliveryTag: 11,
                redelivered: false,
                exchange: "ex",
                routingKey: "rk",
                new BasicProperties(),
                ReadOnlyMemory<byte>.Empty);
        }

        /// <summary>
        /// DispatchProxy IChannel double sequencing consume, close, and dispose for lifecycle tests.
        /// </summary>

        private class ConsumeChannelDouble : DispatchProxy
        {
            /// <summary>Serializes event log and registered-consumer snapshots.</summary>

            private readonly object _gate = new();
            /// <summary>Ordered broker-side lifecycle labels recorded for assertions.</summary>

            private readonly List<string> _events = [];
            /// <summary>Consumers successfully registered through BasicConsumeAsync.</summary>

            private readonly List<IAsyncBasicConsumer> _registered = [];

            /// <summary>Dispatch-proxy channel handle passed to the production manual-ack wrapper.</summary>

            public IChannel Channel { get; private set; } = null!;

            /// <summary>Signaled when consume registration reaches the post-failure checkpoint.</summary>

            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Released to unblock a held BasicConsumeAsync.</summary>

            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Signaled when CloseAsync begins while close is held.</summary>

            public TaskCompletionSource CloseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Released to finish a held CloseAsync.</summary>

            public TaskCompletionSource CloseRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>When true, CloseAsync waits on <see cref="CloseRelease"/> after signaling <see cref="CloseEntered"/>.</summary>

            public bool HoldClose { get; set; }

            /// <summary>When true, consume signals <see cref="Entered"/> then waits on <see cref="Release"/> with the caller token.</summary>

            public bool CancelDuringConsume { get; set; }

            /// <summary>When set, BasicConsumeAsync records consume-failed and rethrows without registering.</summary>

            public Exception? ConsumeFailure { get; set; }

            /// <summary>Last consumer instance passed to BasicConsumeAsync.</summary>

            public IAsyncBasicConsumer? Captured { get; private set; }

            /// <summary>Count of BasicConsumeAsync invocations observed by the proxy.</summary>

            public int ConsumeCalls { get; private set; }

            /// <summary>Count of CloseAsync invocations observed by the proxy.</summary>

            public int CloseCalls { get; private set; }

            /// <summary>Count of DisposeAsync invocations observed by the proxy.</summary>

            public int DisposeCalls { get; private set; }

            /// <summary>Open flag returned by get_IsOpen; cleared when close runs.</summary>

            public bool IsOpen { get; set; } = true;

            /// <summary>Consumers registered through BasicConsumeAsync.</summary>
            internal IReadOnlyList<IAsyncBasicConsumer> Registered
            {
                get
                {
                    lock (_gate)
                    {
                        return _registered.ToArray();
                    }
                }
            }

            /// <summary>Creates a dispatch-proxy <see cref="IChannel"/> double wired back to this instance.</summary>

            internal static ConsumeChannelDouble Create()
            {
                var channel = DispatchProxy.Create<IChannel, ConsumeChannelDouble>();
                var proxy = (ConsumeChannelDouble)channel;
                proxy.Channel = channel;
                return proxy;
            }

            /// <summary>Returns a copy of recorded lifecycle event labels under the gate lock.</summary>

            internal string[] Snapshot()
            {
                lock (_gate)
                {
                    return _events.ToArray();
                }
            }

            /// <inheritdoc />
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                ArgumentNullException.ThrowIfNull(targetMethod);
                if (targetMethod.Name is "add_ChannelShutdownAsync" or "remove_ChannelShutdownAsync")
                {
                    return null;
                }

                if (targetMethod.Name == "get_IsOpen")
                {
                    return IsOpen;
                }

                if (targetMethod.Name == "BasicQosAsync")
                {
                    return Task.CompletedTask;
                }

                if (targetMethod.Name == "BasicConsumeAsync")
                {
                    return ConsumeAsync(args ?? []);
                }

                if (targetMethod.Name == "CloseAsync")
                {
                    return CloseAsync();
                }

                if (targetMethod.Name == "DisposeAsync")
                {
                    return DisposeCore();
                }

                throw new NotSupportedException(targetMethod.Name);
            }

            /// <summary>
            /// Simulates BasicConsumeAsync: optional failure or cancel path, then registers the consumer until released.
            /// Unsupported broker methods are not handled here.
            /// </summary>
            /// <param name="args">BasicConsumeAsync argument array; consumer at index 6 and token at index 7.</param>

            private async Task<string> ConsumeAsync(object?[] args)
            {
                var consumer = args[6] as IAsyncBasicConsumer;
                Assert.NotNull(consumer);
                var cancellationToken = Assert.IsType<CancellationToken>(args[7]);
                lock (_gate)
                {
                    ConsumeCalls++;
                    Captured = consumer;
                }

                if (ConsumeFailure is not null)
                {
                    Add("consume-failed");
                    throw ConsumeFailure;
                }

                if (CancelDuringConsume)
                {
                    Entered.TrySetResult();
                    await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                lock (_gate)
                {
                    _registered.Add(consumer);
                    _events.Add("registered");
                }

                Entered.TrySetResult();
                await Release.Task.ConfigureAwait(false);
                Add("consume-returning");
                return "ctag-1";
            }

            /// <summary>
            /// Marks the channel closed, clears registrations, and optionally waits on <see cref="CloseRelease"/>.
            /// </summary>

            private async Task CloseAsync()
            {
                lock (_gate)
                {
                    CloseCalls++;
                    IsOpen = false;
                    _registered.Clear();
                    _events.Add("close");
                }

                if (HoldClose)
                {
                    CloseEntered.TrySetResult();
                    await CloseRelease.Task.ConfigureAwait(false);
                }
            }

            /// <summary>Increments dispose count and appends disposed to the event log.</summary>

            private ValueTask DisposeCore()
            {
                lock (_gate)
                {
                    DisposeCalls++;
                    _events.Add("disposed");
                }

                return ValueTask.CompletedTask;
            }

            /// <summary>Appends a lifecycle label to the event log under the gate lock.</summary>
            /// <param name="name">Label recorded for Snapshot assertions.</param>

            private void Add(string name)
            {
                lock (_gate)
                {
                    _events.Add(name);
                }
            }
        }
    }
}

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using VectorNNTPD.BackFillerService.Listener;
using Xunit.Abstractions;

namespace VectorNNTPD.BackFillerService.Tests.Listener
{
    /// <summary>
    /// No-progress deadline, cancellation, and write-call behavior of <see cref="StreamCacheListenerTransport"/>.
    /// </summary>
    public sealed class StreamCacheListenerTransportTests
    {
        /// <summary>Writes measurement lines for the allocation comparison.</summary>
        private readonly ITestOutputHelper _output;

        /// <summary>Captures the xUnit output helper.</summary>
        /// <param name="output">Test output used for the benchmark log.</param>
        public StreamCacheListenerTransportTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>A stream write of an article-sized buffer is one <see cref="Stream.WriteAsync"/> call.</summary>
        [Fact]
        public async Task Article_sized_buffer_is_one_stream_write()
        {
            await using var stream = new CountingMemoryStream();
            await using var transport = new StreamCacheListenerTransport(stream, TimeSpan.FromSeconds(5), leaveInnerStreamOpen: true);
            var payload = new byte[768 * 1024];
            var accepted = await transport.WriteAsync(payload, CancellationToken.None);
            Assert.Equal(payload.Length, accepted);
            Assert.Equal(1, stream.WriteCalls);
            Assert.Equal(payload.Length, stream.LargestWrite);
            Assert.Equal(payload.Length, stream.Length);
        }

        /// <summary>An empty write does not arm a deadline or touch the stream.</summary>
        [Fact]
        public async Task Empty_write_returns_zero_without_a_stream_call()
        {
            await using var stream = new CountingMemoryStream();
            await using var transport = new StreamCacheListenerTransport(stream, TimeSpan.FromMilliseconds(50), leaveInnerStreamOpen: true);
            Assert.Equal(0, await transport.WriteAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None));
            Assert.Equal(0, stream.WriteCalls);
        }

        /// <summary>A write that makes no progress times out, and the next write on that connection still runs.</summary>
        [Fact]
        public async Task Stalled_write_times_out_and_a_later_write_succeeds()
        {
            await using var stream = new BlockFirstCallStream();
            await using var transport = new StreamCacheListenerTransport(stream, TimeSpan.FromMilliseconds(150), leaveInnerStreamOpen: true);
            var started = Stopwatch.StartNew();
            var timeout = await Assert.ThrowsAsync<TimeoutException>(
                () => transport.WriteAsync(new byte[8], CancellationToken.None).AsTask());
            started.Stop();
            Assert.Contains("write", timeout.Message, StringComparison.Ordinal);
            Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));

            stream.Release();
            Assert.Equal(4, await transport.WriteAsync(new byte[4], CancellationToken.None));
            Assert.Equal(2, stream.WriteCalls);
        }

        /// <summary>A read that makes no progress times out, and the next read on that connection still runs.</summary>
        [Fact]
        public async Task Stalled_read_times_out_and_a_later_read_succeeds()
        {
            await using var stream = new BlockFirstCallStream();
            await using var transport = new StreamCacheListenerTransport(stream, TimeSpan.FromMilliseconds(150), leaveInnerStreamOpen: true);
            var timeout = await Assert.ThrowsAsync<TimeoutException>(
                () => transport.ReadAsync(new byte[8], CancellationToken.None).AsTask());
            Assert.Contains("read", timeout.Message, StringComparison.Ordinal);

            stream.Release();
            stream.NextRead = [1, 2, 3, 4];
            var buffer = new byte[4];
            Assert.Equal(4, await transport.ReadAsync(buffer, CancellationToken.None));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer);
        }

        /// <summary>
        /// Two writes that each finish inside the deadline succeed when their combined time exceeds one deadline.
        /// </summary>
        [Fact]
        public async Task Completed_writes_restart_the_deadline()
        {
            var deadline = TimeSpan.FromMilliseconds(900);
            await using var stream = new DelayStream(TimeSpan.FromMilliseconds(550));
            await using var transport = new StreamCacheListenerTransport(stream, deadline, leaveInnerStreamOpen: true);
            var started = Stopwatch.StartNew();
            Assert.Equal(16, await transport.WriteAsync(new byte[16], CancellationToken.None));
            Assert.Equal(16, await transport.WriteAsync(new byte[16], CancellationToken.None));
            started.Stop();
            Assert.True(started.Elapsed > deadline, $"elapsed {started.Elapsed} did not exceed {deadline}");
            Assert.Equal(2, stream.WriteCalls);
        }

        /// <summary>Caller cancellation is not reported as a no-progress timeout, including a following operation.</summary>
        [Fact]
        public async Task Caller_cancellation_is_not_a_timeout()
        {
            await using var stream = new BlockFirstCallStream();
            await using var transport = new StreamCacheListenerTransport(stream, TimeSpan.FromSeconds(30), leaveInnerStreamOpen: true);
            using var caller = new CancellationTokenSource();
            var write = transport.WriteAsync(new byte[8], caller.Token);
            await stream.Entered.WaitAsync(TimeSpan.FromSeconds(2));
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.AsTask());

            stream.Release();
            Assert.Equal(4, await transport.WriteAsync(new byte[4], CancellationToken.None));
        }

        /// <summary>An already canceled caller fails before a deadline is reported.</summary>
        [Fact]
        public async Task Already_canceled_caller_fails_before_the_deadline()
        {
            await using var stream = new CountingMemoryStream();
            await using var transport = new StreamCacheListenerTransport(stream, TimeSpan.FromSeconds(30), leaveInnerStreamOpen: true);
            using var caller = new CancellationTokenSource();
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => transport.WriteAsync(new byte[4], caller.Token).AsTask());
            Assert.Equal(0, stream.WriteCalls);
            Assert.Equal(4, await transport.WriteAsync(new byte[4], CancellationToken.None));
        }

        /// <summary>Disposing the transport unblocks an in-flight read as disposal, not as a timeout.</summary>
        [Fact]
        public async Task Dispose_cancels_an_in_flight_read()
        {
            await using var stream = new BlockFirstCallStream();
            var transport = new StreamCacheListenerTransport(stream, TimeSpan.FromSeconds(30), leaveInnerStreamOpen: true);
            var read = transport.ReadAsync(new byte[4], CancellationToken.None);
            await stream.Entered.WaitAsync(TimeSpan.FromSeconds(2));
            var dispose = transport.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => read.AsTask());
            await dispose;
        }

        /// <summary>One connection rejects an overlapping operation. A second connection is independent.</summary>
        [Fact]
        public async Task Overlapping_calls_on_one_connection_are_rejected_and_another_connection_is_independent()
        {
            await using var blocked = new BlockFirstCallStream();
            await using var ready = new CountingMemoryStream();
            await using var first = new StreamCacheListenerTransport(blocked, TimeSpan.FromSeconds(5), leaveInnerStreamOpen: true);
            await using var second = new StreamCacheListenerTransport(ready, TimeSpan.FromSeconds(5), leaveInnerStreamOpen: true);
            var pending = first.ReadAsync(new byte[1], CancellationToken.None);
            await blocked.Entered.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => first.WriteAsync(new byte[1], CancellationToken.None).AsTask());
            Assert.Equal(8, await second.WriteAsync(new byte[8], CancellationToken.None));
            blocked.Release();
            Assert.Equal(0, await pending);
        }

        /// <summary>
        /// A stream that throws <see cref="OperationCanceledException"/> before returning is a timeout when the caller token is still active.
        /// The next call on that connection still runs.
        /// </summary>
        [Fact]
        public async Task Synchronous_cancellation_is_a_timeout_and_the_next_call_still_runs()
        {
            await using var stream = new SynchronousCancelStream();
            await using var transport = new StreamCacheListenerTransport(stream, TimeSpan.FromMinutes(5), leaveInnerStreamOpen: true);

            var write = await Assert.ThrowsAsync<TimeoutException>(
                () => transport.WriteAsync(new byte[4], CancellationToken.None).AsTask());
            Assert.Contains("write", write.Message, StringComparison.Ordinal);
            var read = await Assert.ThrowsAsync<TimeoutException>(
                () => transport.ReadAsync(new byte[4], CancellationToken.None).AsTask());
            Assert.Contains("read", read.Message, StringComparison.Ordinal);
            Assert.Equal(2, stream.Calls);

            var again = await Assert.ThrowsAsync<TimeoutException>(
                () => transport.WriteAsync(new byte[4], CancellationToken.None).AsTask());
            Assert.Contains("write", again.Message, StringComparison.Ordinal);
            Assert.Equal(3, stream.Calls);
        }

        /// <summary>A completed canceled read or write is translated on the asynchronous path.</summary>
        [Fact]
        public async Task Completed_canceled_operation_is_a_timeout_and_the_next_call_still_runs()
        {
            await using var stream = new CompletedCancelStream();
            await using var transport = new StreamCacheListenerTransport(stream, TimeSpan.FromMinutes(5), leaveInnerStreamOpen: true);

            await Assert.ThrowsAsync<TimeoutException>(
                () => transport.WriteAsync(new byte[4], CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<TimeoutException>(
                () => transport.ReadAsync(new byte[4], CancellationToken.None).AsTask());
            Assert.Equal(2, stream.Calls);
        }

        /// <summary>
        /// A callback that runs after an operation has finished does not cancel the next blocked operation.
        /// Canceling the source detached by the earlier timeout does not cancel that later operation either.
        /// </summary>
        [Fact]
        public async Task Queued_timeout_callback_does_not_cancel_a_later_write()
        {
            await using var stream = new GateStream();
            await using var transport = new StreamCacheListenerTransport(stream, TimeSpan.FromMinutes(5), leaveInnerStreamOpen: true);

            var progressed = transport.WriteAsync(new byte[4], CancellationToken.None);
            (await stream.NextReleaseAsync().WaitAsync(TimeSpan.FromSeconds(2))).TrySetResult();
            Assert.Equal(4, await progressed);

            SetDeadlineTicks(transport, Environment.TickCount64 - 1);
            InvokeTimeout(transport);

            var stalled = transport.WriteAsync(new byte[8], CancellationToken.None);
            _ = await stream.NextReleaseAsync().WaitAsync(TimeSpan.FromSeconds(2));
            var stalledSource = GetTimeoutSource(transport);
            InvokeTimeout(transport);
            Assert.False(stalled.IsCompleted);

            SetDeadlineTicks(transport, Environment.TickCount64 - 1);
            InvokeTimeout(transport);
            var timeout = await Assert.ThrowsAsync<TimeoutException>(() => stalled.AsTask());
            Assert.Contains("write", timeout.Message, StringComparison.Ordinal);

            var recovered = transport.WriteAsync(new byte[2], CancellationToken.None);
            var recoveredGate = await stream.NextReleaseAsync().WaitAsync(TimeSpan.FromSeconds(2));
            stalledSource.Cancel();
            InvokeTimeout(transport);
            Assert.False(recovered.IsCompleted);
            recoveredGate.TrySetResult();
            Assert.Equal(2, await recovered);
        }

        /// <summary>A read timeout detaches its source. That source cannot cancel the next read, and a callback while the next deadline remains does not either.</summary>
        [Fact]
        public async Task Queued_timeout_callback_does_not_cancel_a_later_read()
        {
            await using var stream = new GateStream();
            await using var transport = new StreamCacheListenerTransport(stream, TimeSpan.FromMinutes(5), leaveInnerStreamOpen: true);
            var buffer = new byte[4];

            var stalled = transport.ReadAsync(buffer, CancellationToken.None);
            _ = await stream.NextReleaseAsync().WaitAsync(TimeSpan.FromSeconds(2));
            var stalledSource = GetTimeoutSource(transport);
            SetDeadlineTicks(transport, Environment.TickCount64 - 1);
            InvokeTimeout(transport);
            var timeout = await Assert.ThrowsAsync<TimeoutException>(() => stalled.AsTask());
            Assert.Contains("read", timeout.Message, StringComparison.Ordinal);

            stream.NextRead = [9, 8, 7, 6];
            var recovered = transport.ReadAsync(buffer, CancellationToken.None);
            var recoveredGate = await stream.NextReleaseAsync().WaitAsync(TimeSpan.FromSeconds(2));
            stalledSource.Cancel();
            InvokeTimeout(transport);
            Assert.False(recovered.IsCompleted);
            recoveredGate.TrySetResult();
            Assert.Equal(stream.NextRead.Length, await recovered);
            Assert.Equal(stream.NextRead, buffer);
        }

        /// <summary>Caller cancellation stays <see cref="OperationCanceledException"/> when a timeout callback also runs.</summary>
        [Fact]
        public async Task Caller_cancellation_wins_over_a_timeout_callback()
        {
            await using var stream = new GateStream();
            await using var transport = new StreamCacheListenerTransport(stream, TimeSpan.FromMinutes(5), leaveInnerStreamOpen: true);
            using var caller = new CancellationTokenSource();
            var write = transport.WriteAsync(new byte[4], caller.Token);
            var gate = await stream.NextReleaseAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await caller.CancelAsync();
            SetDeadlineTicks(transport, Environment.TickCount64 - 1);
            InvokeTimeout(transport);

            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.AsTask());
            Assert.IsNotType<TimeoutException>(error);
            gate.TrySetResult();

            var next = transport.WriteAsync(new byte[2], CancellationToken.None);
            (await stream.NextReleaseAsync().WaitAsync(TimeSpan.FromSeconds(2))).TrySetResult();
            Assert.Equal(2, await next);
        }

        /// <summary>A timeout callback that runs after disposal does not throw and does not replace the disposal result.</summary>
        [Fact]
        public async Task Timeout_callback_after_dispose_does_not_throw()
        {
            await using var stream = new GateStream();
            var transport = new StreamCacheListenerTransport(stream, TimeSpan.FromMinutes(5), leaveInnerStreamOpen: true);
            try
            {
                var read = transport.ReadAsync(new byte[4], CancellationToken.None);
                var gate = await stream.NextReleaseAsync().WaitAsync(TimeSpan.FromSeconds(2));
                await transport.DisposeAsync();
                await Assert.ThrowsAsync<ObjectDisposedException>(() => read.AsTask());
                InvokeTimeout(transport);
                gate.TrySetResult();
            }
            finally
            {
                await transport.DisposeAsync();
            }
        }

        /// <summary>
        /// Replacing the caller token while the previous callback is still running does not deadlock,
        /// and that callback does not cancel the replacement operation.
        /// </summary>
        [Fact]
        public async Task Replacing_a_caller_token_does_not_deadlock_when_the_previous_callback_is_running()
        {
            await using var stream = new GateStream();
            await using var transport = new StreamCacheListenerTransport(stream, TimeSpan.FromMinutes(5), leaveInnerStreamOpen: true);
            using var first = new CancellationTokenSource();
            var initial = transport.WriteAsync(new byte[4], first.Token);
            (await stream.NextReleaseAsync().WaitAsync(SafetyBound)).TrySetResult();
            Assert.Equal(4, await initial.AsTask().WaitAsync(SafetyBound));

            var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.BeforeCallerCancelLock = () =>
            {
                callbackEntered.TrySetResult();
                WaitForSignal(releaseCallback.Task);
            };
            transport.BeforeDisposeDetachedRegistration = () => detached.TrySetResult();

            using var second = new CancellationTokenSource();
            var cancel = Task.Run(first.Cancel);
            await callbackEntered.Task.WaitAsync(SafetyBound);
            var replacement = Task.Run(() => transport.WriteAsync(new byte[8], second.Token).AsTask());
            await detached.Task.WaitAsync(SafetyBound);
            releaseCallback.TrySetResult();

            var release = await stream.NextReleaseAsync().WaitAsync(SafetyBound);
            Assert.False(replacement.IsCompleted);
            release.TrySetResult();
            Assert.Equal(8, await replacement.WaitAsync(SafetyBound));
            await cancel.WaitAsync(SafetyBound);
        }

        /// <summary>The caller-cancel callback waits until it can acquire the transport lock, then cancels the active operation.</summary>
        [Fact]
        public async Task Caller_callback_waits_for_the_transport_lock_then_cancels_the_active_operation()
        {
            await using var stream = new GateStream();
            await using var transport = new StreamCacheListenerTransport(stream, TimeSpan.FromMinutes(5), leaveInnerStreamOpen: true);
            using var caller = new CancellationTokenSource();
            var aboutToLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.BeforeCallerCancelLock = () => aboutToLock.TrySetResult();

            var write = transport.WriteAsync(new byte[4], caller.Token);
            var gate = await stream.NextReleaseAsync().WaitAsync(SafetyBound);
            var lockHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = Task.Run(() => transport.InvokeUnderGate(() =>
            {
                lockHeld.TrySetResult();
                WaitForSignal(releaseLock.Task);
            }));
            await lockHeld.Task.WaitAsync(SafetyBound);

            var cancel = Task.Run(caller.Cancel);
            await aboutToLock.Task.WaitAsync(SafetyBound);
            Assert.False(write.IsCompleted);
            Assert.False(cancel.IsCompleted);

            releaseLock.TrySetResult();
            await holder.WaitAsync(SafetyBound);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.AsTask().WaitAsync(SafetyBound));
            Assert.IsNotType<TimeoutException>(error);
            gate.TrySetResult();
            await cancel.WaitAsync(SafetyBound);

            using var later = new CancellationTokenSource();
            var recovered = transport.WriteAsync(new byte[2], later.Token);
            (await stream.NextReleaseAsync().WaitAsync(SafetyBound)).TrySetResult();
            Assert.Equal(2, await recovered.AsTask().WaitAsync(SafetyBound));
        }

        /// <summary>An already canceled replacement token fails before the stream call, and a later token still writes.</summary>
        [Fact]
        public async Task Already_canceled_replacement_token_fails_and_the_transport_stays_usable()
        {
            await using var stream = new CountingMemoryStream();
            await using var transport = new StreamCacheListenerTransport(stream, TimeSpan.FromMinutes(5), leaveInnerStreamOpen: true);
            using var first = new CancellationTokenSource();
            Assert.Equal(4, await transport.WriteAsync(new byte[4], first.Token));

            using var canceled = new CancellationTokenSource();
            await canceled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => transport.WriteAsync(new byte[4], canceled.Token).AsTask());
            Assert.Equal(1, stream.WriteCalls);

            Assert.Equal(2, await transport.WriteAsync(new byte[2], CancellationToken.None));
            Assert.Equal(2, stream.WriteCalls);
        }

        /// <summary>
        /// A caller token that becomes canceled before its registration is stored is still caller cancellation,
        /// including when a timeout callback runs on that attempt.
        /// </summary>
        [Fact]
        public async Task Canceled_during_registration_stays_caller_cancellation_and_the_next_token_writes()
        {
            await using var stream = new CountingMemoryStream();
            await using var transport = new StreamCacheListenerTransport(stream, TimeSpan.FromMinutes(5), leaveInnerStreamOpen: true);
            using var first = new CancellationTokenSource();
            Assert.Equal(1, await transport.WriteAsync(new byte[1], first.Token));

            using var second = new CancellationTokenSource();
            transport.BeforeDisposeDetachedRegistration = () =>
            {
                second.Cancel();
                SetDeadlineTicks(transport, Environment.TickCount64 - 1);
                InvokeTimeout(transport);
            };

            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => transport.WriteAsync(new byte[4], second.Token).AsTask());
            Assert.IsNotType<TimeoutException>(error);

            transport.BeforeDisposeDetachedRegistration = null;
            Assert.Equal(3, await transport.WriteAsync(new byte[3], CancellationToken.None));
        }

        /// <summary>Disposal waits for an in-flight caller callback without holding the transport lock, then rejects later calls.</summary>
        [Fact]
        public async Task Dispose_during_a_caller_callback_does_not_deadlock()
        {
            await using var stream = new GateStream();
            var transport = new StreamCacheListenerTransport(stream, TimeSpan.FromMinutes(5), leaveInnerStreamOpen: true);
            using var caller = new CancellationTokenSource();
            var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.BeforeCallerCancelLock = () =>
            {
                callbackEntered.TrySetResult();
                WaitForSignal(releaseCallback.Task);
            };

            try
            {
                var read = transport.ReadAsync(new byte[4], caller.Token);
                var gate = await stream.NextReleaseAsync().WaitAsync(SafetyBound);
                var cancel = Task.Run(caller.Cancel);
                await callbackEntered.Task.WaitAsync(SafetyBound);
                var dispose = Task.Run(() => transport.DisposeAsync().AsTask());
                await Assert.ThrowsAsync<ObjectDisposedException>(() => read.AsTask().WaitAsync(SafetyBound));
                releaseCallback.TrySetResult();
                await dispose.WaitAsync(SafetyBound);
                await cancel.WaitAsync(SafetyBound);
                gate.TrySetResult();
                await Assert.ThrowsAsync<ObjectDisposedException>(
                    () => transport.WriteAsync(new byte[1], CancellationToken.None).AsTask());
            }
            finally
            {
                releaseCallback.TrySetResult();
                await transport.DisposeAsync();
            }
        }

        /// <summary>
        /// Compares one production write of a 768 KiB buffer with a reconstructed 32 KiB loop that allocates a timeout source per chunk.
        /// </summary>
        [Fact]
        public async Task Production_write_uses_fewer_calls_and_fewer_allocations_than_32_kib_chunks()
        {
            const int payloadLength = 768 * 1024;
            const int iterations = 8;
            var payload = new byte[payloadLength];
            await using var warmupStream = new DiscardStream();
            await using var warmup = new StreamCacheListenerTransport(warmupStream, TimeSpan.FromSeconds(5), leaveInnerStreamOpen: true);
            await warmup.WriteAsync(payload, CancellationToken.None);
            await ChunkedTimeoutWritesAsync(new DiscardStream(), payload);

            await using var productionStream = new DiscardStream();
            await using var production = new StreamCacheListenerTransport(productionStream, TimeSpan.FromSeconds(5), leaveInnerStreamOpen: true);
            var productionWatch = Stopwatch.StartNew();
            var productionBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < iterations; i++)
            {
                Assert.Equal(payloadLength, await production.WriteAsync(payload, CancellationToken.None));
            }

            productionWatch.Stop();
            var productionAllocated = GC.GetAllocatedBytesForCurrentThread() - productionBefore;

            await using var baselineStream = new DiscardStream();
            var baselineWatch = Stopwatch.StartNew();
            var baselineBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < iterations; i++)
            {
                await ChunkedTimeoutWritesAsync(baselineStream, payload);
            }

            baselineWatch.Stop();
            var baselineAllocated = GC.GetAllocatedBytesForCurrentThread() - baselineBefore;
            var chunksPerPayload = (payloadLength + (32 * 1024) - 1) / (32 * 1024);

            _output.WriteLine(
                "os={0}; arch={1}; payload={2}; iterations={3}; productionWrites={4}; productionAllocated={5}; productionMs={6:0.0}; baselineWrites={7}; baselineAllocated={8}; baselineMs={9:0.0}",
                RuntimeInformation.OSDescription,
                RuntimeInformation.OSArchitecture,
                payloadLength,
                iterations,
                productionStream.WriteCalls - 0,
                productionAllocated,
                productionWatch.Elapsed.TotalMilliseconds,
                baselineStream.WriteCalls,
                baselineAllocated,
                baselineWatch.Elapsed.TotalMilliseconds);
            _output.WriteLine("warmupProductionWrites={0}", warmupStream.WriteCalls);

            Assert.Equal(iterations, productionStream.WriteCalls);
            Assert.Equal(payloadLength, productionStream.LargestWrite);
            Assert.Equal(iterations * chunksPerPayload, baselineStream.WriteCalls);
            Assert.True(
                productionAllocated < baselineAllocated,
                $"production allocated {productionAllocated} bytes; reconstructed 32 KiB baseline allocated {baselineAllocated} bytes");
        }

        /// <summary>Previous write shape: one linked timeout source and one stream write per 32 KiB.</summary>
        /// <param name="stream">Destination that records each write.</param>
        /// <param name="payload">Article-sized buffer.</param>
        private static async Task ChunkedTimeoutWritesAsync(Stream stream, byte[] payload)
        {
            var offset = 0;
            while (offset < payload.Length)
            {
                var count = Math.Min(payload.Length - offset, 32 * 1024);
                using var timeout = new CancellationTokenSource();
                timeout.CancelAfter(TimeSpan.FromMinutes(1));
                await stream.WriteAsync(payload.AsMemory(offset, count), timeout.Token).ConfigureAwait(false);
                offset += count;
            }
        }

        /// <summary>Safety bound for a test that must complete because of an explicit signal. A deadlock fails this bound.</summary>
        private static readonly TimeSpan SafetyBound = TimeSpan.FromSeconds(5);

        /// <summary>Waits until <paramref name="task"/> completes because a test released it.</summary>
        /// <param name="task">Signal the test is responsible for completing.</param>
        private static void WaitForSignal(Task task)
        {
            if (!task.Wait(SafetyBound))
            {
                throw new TimeoutException("Timed out waiting for an explicit test signal.");
            }
        }

        /// <summary>Runs the private timeout callback on the calling thread.</summary>
        /// <param name="transport">Transport whose deadline callback should run.</param>
        private static void InvokeTimeout(StreamCacheListenerTransport transport)
        {
            var method = typeof(StreamCacheListenerTransport).GetMethod(
                "OnTimeout",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(transport, null);
        }

        /// <summary>Publishes a deadline the next callback will observe.</summary>
        /// <param name="transport">Transport whose armed deadline is replaced.</param>
        /// <param name="ticks"><see cref="Environment.TickCount64"/> value stored as the deadline.</param>
        private static void SetDeadlineTicks(StreamCacheListenerTransport transport, long ticks)
        {
            var field = typeof(StreamCacheListenerTransport).GetField(
                "_deadlineTicks",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field.SetValue(transport, ticks);
        }

        /// <summary>Returns the cancellation source the transport will arm for the active operation.</summary>
        /// <param name="transport">Transport that owns the source.</param>
        /// <returns>The current timeout source.</returns>
        private static CancellationTokenSource GetTimeoutSource(StreamCacheListenerTransport transport)
        {
            var field = typeof(StreamCacheListenerTransport).GetField(
                "_timeoutCts",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return Assert.IsType<CancellationTokenSource>(field.GetValue(transport));
        }

        /// <summary>Counts writes and discards the bytes so the measurement is not the cost of copying them.</summary>
        private sealed class DiscardStream : Stream
        {
            /// <summary>How many <see cref="WriteAsync"/> calls this stream observed.</summary>
            public int WriteCalls { get; private set; }

            /// <summary>Largest single <see cref="WriteAsync"/> buffer.</summary>
            public int LargestWrite { get; private set; }

            /// <inheritdoc />
            public override bool CanRead => false;

            /// <inheritdoc />
            public override bool CanSeek => false;

            /// <inheritdoc />
            public override bool CanWrite => true;

            /// <inheritdoc />
            public override long Length => 0;

            /// <inheritdoc />
            public override long Position
            {
                get => 0;
                set => throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override void Flush()
            {
            }

            /// <inheritdoc />
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            /// <inheritdoc />
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void SetLength(long value) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            /// <inheritdoc />
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                WriteCalls++;
                if (buffer.Length > LargestWrite)
                {
                    LargestWrite = buffer.Length;
                }

                return ValueTask.CompletedTask;
            }
        }

        /// <summary>Counts <see cref="WriteAsync"/> calls and keeps the bytes.</summary>
        private sealed class CountingMemoryStream : MemoryStream
        {
            /// <summary>How many <see cref="WriteAsync"/> calls this stream observed.</summary>
            public int WriteCalls { get; private set; }

            /// <summary>Largest single <see cref="WriteAsync"/> buffer.</summary>
            public int LargestWrite { get; private set; }

            /// <inheritdoc />
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                WriteCalls++;
                if (buffer.Length > LargestWrite)
                {
                    LargestWrite = buffer.Length;
                }

                return base.WriteAsync(buffer, cancellationToken);
            }
        }

        /// <summary>Delays each write, then completes the whole buffer.</summary>
        /// <param name="delay">Time spent inside each write before it returns.</param>
        private sealed class DelayStream(TimeSpan delay) : Stream
        {
            /// <summary>How many writes were started.</summary>
            public int WriteCalls { get; private set; }

            /// <inheritdoc />
            public override bool CanRead => false;

            /// <inheritdoc />
            public override bool CanSeek => false;

            /// <inheritdoc />
            public override bool CanWrite => true;

            /// <inheritdoc />
            public override long Length => throw new NotSupportedException();

            /// <inheritdoc />
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override void Flush()
            {
            }

            /// <inheritdoc />
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            /// <inheritdoc />
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void SetLength(long value) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            /// <inheritdoc />
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                WriteCalls++;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Blocks the first read or write until release or cancellation, then serves a prepared read.</summary>
        private sealed class BlockFirstCallStream : Stream
        {
            /// <summary>Completed when the first blocked call is waiting.</summary>
            private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Released by the test or by cancellation of the blocked call.</summary>
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Zero until the first call, then one.</summary>
            private int _calls;

            /// <summary>Bytes returned by the read after the blocked call.</summary>
            public byte[] NextRead { get; set; } = [];

            /// <summary>How many read or write calls started.</summary>
            public int WriteCalls { get; private set; }

            /// <summary>Completed when the blocked call is inside the wait.</summary>
            public Task Entered => _entered.Task;

            /// <summary>Lets a later call finish without waiting.</summary>
            public void Release() => _release.TrySetResult();

            /// <inheritdoc />
            public override bool CanRead => true;

            /// <inheritdoc />
            public override bool CanSeek => false;

            /// <inheritdoc />
            public override bool CanWrite => true;

            /// <inheritdoc />
            public override long Length => throw new NotSupportedException();

            /// <inheritdoc />
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override void Flush()
            {
            }

            /// <inheritdoc />
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            /// <inheritdoc />
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void SetLength(long value) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            /// <inheritdoc />
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await WaitIfFirstAsync(cancellationToken).ConfigureAwait(false);
                var count = Math.Min(buffer.Length, NextRead.Length);
                NextRead.AsSpan(0, count).CopyTo(buffer.Span);
                return count;
            }

            /// <inheritdoc />
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                WriteCalls++;
                await WaitIfFirstAsync(cancellationToken).ConfigureAwait(false);
            }

            /// <summary>The first caller waits. Later callers return as soon as <see cref="Release"/> has run.</summary>
            /// <param name="cancellationToken">Cancels the first wait.</param>
            private async Task WaitIfFirstAsync(CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref _calls) == 1)
                {
                    _entered.TrySetResult();
                    await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Throws <see cref="OperationCanceledException"/> from the stream call before a task is returned.</summary>
        private sealed class SynchronousCancelStream : Stream
        {
            /// <summary>How many read or write calls entered the stream.</summary>
            public int Calls { get; private set; }

            /// <inheritdoc />
            public override bool CanRead => true;

            /// <inheritdoc />
            public override bool CanSeek => false;

            /// <inheritdoc />
            public override bool CanWrite => true;

            /// <inheritdoc />
            public override long Length => throw new NotSupportedException();

            /// <inheritdoc />
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override void Flush()
            {
            }

            /// <inheritdoc />
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            /// <inheritdoc />
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void SetLength(long value) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            /// <inheritdoc />
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Calls++;
                throw new OperationCanceledException(cancellationToken);
            }

            /// <inheritdoc />
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Calls++;
                throw new OperationCanceledException(cancellationToken);
            }
        }

        /// <summary>Returns a completed canceled operation without throwing from the call itself.</summary>
        private sealed class CompletedCancelStream : Stream
        {
            /// <summary>Already-canceled token used to complete the returned operation.</summary>
            private static readonly CancellationToken Canceled = new(canceled: true);

            /// <summary>How many read or write calls entered the stream.</summary>
            public int Calls { get; private set; }

            /// <inheritdoc />
            public override bool CanRead => true;

            /// <inheritdoc />
            public override bool CanSeek => false;

            /// <inheritdoc />
            public override bool CanWrite => true;

            /// <inheritdoc />
            public override long Length => throw new NotSupportedException();

            /// <inheritdoc />
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override void Flush()
            {
            }

            /// <inheritdoc />
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            /// <inheritdoc />
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void SetLength(long value) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            /// <inheritdoc />
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Calls++;
                return ValueTask.FromCanceled<int>(Canceled);
            }

            /// <inheritdoc />
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Calls++;
                return ValueTask.FromCanceled(Canceled);
            }
        }

        /// <summary>Blocks every read and write until the test releases that call.</summary>
        private sealed class GateStream : Stream
        {
            /// <summary>Serializes publication of the release handle for the in-flight call.</summary>
            private readonly object _gate = new();

            /// <summary>Release handles waiting for a test to observe them.</summary>
            private readonly Queue<TaskCompletionSource> _releases = new();

            /// <summary>Test waiting for the next call to block. Null when no test is waiting.</summary>
            private TaskCompletionSource<TaskCompletionSource>? _waitingReader;

            /// <summary>Bytes copied into the destination after a read is released.</summary>
            public byte[] NextRead { get; set; } = [1, 2, 3, 4];

            /// <summary>Completes when the next blocked call has published its release handle.</summary>
            /// <returns>The handle the test completes to unblock that call.</returns>
            public Task<TaskCompletionSource> NextReleaseAsync()
            {
                lock (_gate)
                {
                    if (_releases.Count > 0)
                    {
                        return Task.FromResult(_releases.Dequeue());
                    }

                    _waitingReader = new TaskCompletionSource<TaskCompletionSource>(TaskCreationOptions.RunContinuationsAsynchronously);
                    return _waitingReader.Task;
                }
            }

            /// <inheritdoc />
            public override bool CanRead => true;

            /// <inheritdoc />
            public override bool CanSeek => false;

            /// <inheritdoc />
            public override bool CanWrite => true;

            /// <inheritdoc />
            public override long Length => throw new NotSupportedException();

            /// <inheritdoc />
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override void Flush()
            {
            }

            /// <inheritdoc />
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            /// <inheritdoc />
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void SetLength(long value) => throw new NotSupportedException();

            /// <inheritdoc />
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            /// <inheritdoc />
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await WaitForReleaseAsync(cancellationToken).ConfigureAwait(false);
                var count = Math.Min(buffer.Length, NextRead.Length);
                NextRead.AsSpan(0, count).CopyTo(buffer.Span);
                return count;
            }

            /// <inheritdoc />
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await WaitForReleaseAsync(cancellationToken).ConfigureAwait(false);
            }

            /// <summary>Publishes a release handle and waits for the test or for cancellation.</summary>
            /// <param name="cancellationToken">Cancels the wait when the transport deadline or the caller fires.</param>
            /// <returns>A task that completes when the test releases the call.</returns>
            private Task WaitForReleaseAsync(CancellationToken cancellationToken)
            {
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<TaskCompletionSource>? waiter;
                lock (_gate)
                {
                    waiter = _waitingReader;
                    _waitingReader = null;
                    if (waiter is null)
                    {
                        _releases.Enqueue(release);
                    }
                }

                waiter?.TrySetResult(release);
                return release.Task.WaitAsync(cancellationToken);
            }
        }
    }
}

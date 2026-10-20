using System.Collections.Concurrent;
using System.Diagnostics;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.BackFillerService.Nntp
{
    /// <summary>
    /// Absolute shutdown instant measured with <see cref="Stopwatch.GetTimestamp"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="None"/> is not a deadline. A direct session dispose and a single-session retirement outside a pool drain use it,
    /// and those operations keep the session's own QUIT allowance. A replacement drain may pass <see cref="None"/> through its lease wait;
    /// the pool then captures one <see cref="FromBudget"/> instant for every remaining session. That instant is not reset per session.
    /// Later sessions and providers call <see cref="Remaining"/> and <see cref="Cap"/>. They do not start a new budget.
    /// <see cref="FromBudget"/> also arms one <see cref="CancellationToken"/>. Lease waits and QUIT writes that are bounded by this
    /// deadline observe that token. A private timer sampled from <see cref="Remaining"/> can fire while <see cref="IsExpired"/> is still false
    /// and let the next worker start another QUIT. The shared token does not.
    /// The clock is monotonic. It does not follow wall-clock changes during shutdown.
    /// </remarks>
    internal readonly struct NntpShutdownDeadline : IDisposable
    {
        /// <summary>No shared deadline. Callers use their own operation timeout.</summary>
        public static NntpShutdownDeadline None => default;

        /// <summary><see cref="Stopwatch"/> timestamp at which this deadline is due. Meaningful only when <see cref="IsBounded"/> is set.</summary>
        private readonly long _dueTimestamp;

        /// <summary><see langword="true"/> when <see cref="FromBudget"/> created this value.</summary>
        private readonly bool _bounded;

        /// <summary>
        /// Cancelled once when the budget elapses, or immediately when the budget is already due.
        /// Null for <see cref="None"/>. The owner disposes it after shutdown work has finished.
        /// </summary>
        private readonly CancellationTokenSource? _cancellation;

        /// <summary>Creates a deadline that is due at <paramref name="dueTimestamp"/> and signals <paramref name="cancellation"/>.</summary>
        /// <param name="dueTimestamp">Absolute <see cref="Stopwatch.GetTimestamp"/> value.</param>
        /// <param name="cancellation">Shared token armed by <see cref="FromBudget"/>. Not null.</param>
        private NntpShutdownDeadline(long dueTimestamp, CancellationTokenSource cancellation)
        {
            _dueTimestamp = dueTimestamp;
            _bounded = true;
            _cancellation = cancellation;
        }

        /// <summary><see langword="true"/> when <see cref="Remaining"/> is finite.</summary>
        public bool IsBounded => _bounded;

        /// <summary>
        /// Token cancelled when this deadline's budget elapses.
        /// <see cref="CancellationToken.None"/> when this value is <see cref="None"/>.
        /// </summary>
        /// <remarks>
        /// Lease waits and a QUIT whose cap is this deadline wait on this token.
        /// They do not start a second timer from a later <see cref="Remaining"/> sample.
        /// </remarks>
        public CancellationToken Cancellation => _cancellation?.Token ?? CancellationToken.None;

        /// <summary>
        /// <see langword="true"/> after <see cref="FromBudget"/> has cancelled <see cref="Cancellation"/>.
        /// <see cref="None"/> is never cancelled. This can be true while <see cref="IsExpired"/> is still false:
        /// the token is the signal that stops the next QUIT, and the timestamp is the monotonic budget.
        /// </summary>
        public bool IsCancellationRequested => _cancellation?.IsCancellationRequested == true;

        /// <summary>
        /// <see langword="true"/> when this deadline was created and its instant has been reached.
        /// <see cref="None"/> is never expired.
        /// </summary>
        public bool IsExpired => _bounded && Stopwatch.GetTimestamp() >= _dueTimestamp;

        /// <summary>
        /// Time left until <see cref="_dueTimestamp"/>, or <see cref="Timeout.InfiniteTimeSpan"/> when this value is <see cref="None"/>.
        /// </summary>
        /// <remarks>A due or past deadline returns <see cref="TimeSpan.Zero"/>. The result is never negative.</remarks>
        public TimeSpan Remaining
        {
            get
            {
                if (!_bounded)
                {
                    return Timeout.InfiniteTimeSpan;
                }

                var now = Stopwatch.GetTimestamp();
                if (now >= _dueTimestamp)
                {
                    return TimeSpan.Zero;
                }

                return Stopwatch.GetElapsedTime(now, _dueTimestamp);
            }
        }

        /// <summary>Captures a deadline <paramref name="budget"/> from the current monotonic timestamp.</summary>
        /// <param name="budget">
        /// How long the shutdown operation may run. Zero or negative is already expired, so graceful QUIT is skipped.
        /// </param>
        /// <returns>The deadline used for every later remaining-time check in that operation.</returns>
        /// <remarks>
        /// The instant is <see cref="Stopwatch.GetTimestamp"/> plus the budget converted with <see cref="Stopwatch.Frequency"/>.
        /// A later call does not extend a deadline that was already created.
        /// </remarks>
        public static NntpShutdownDeadline FromBudget(TimeSpan budget)
        {
            var now = Stopwatch.GetTimestamp();
            var cancellation = new CancellationTokenSource();
            if (budget <= TimeSpan.Zero)
            {
                cancellation.Cancel();
                return new NntpShutdownDeadline(now, cancellation);
            }

            var delta = TimestampDelta(budget);
            var due = delta > long.MaxValue - now ? long.MaxValue : now + delta;
            if (budget.TotalMilliseconds <= int.MaxValue)
            {
                cancellation.CancelAfter(budget);
            }

            return new NntpShutdownDeadline(due, cancellation);
        }

        /// <summary>
        /// Cancels <see cref="Cancellation"/> so a later QUIT on this deadline does not start.
        /// </summary>
        /// <remarks>
        /// Does not move <see cref="_dueTimestamp"/>. <see cref="IsExpired"/> can remain false.
        /// <see cref="None"/> has no token. Calling this method again after the token is cancelled does nothing.
        /// </remarks>
        public void Cancel() => _cancellation?.Cancel();

        /// <summary>Releases the shared token. The owner calls this once after retirement has finished.</summary>
        /// <remarks><see cref="None"/> has nothing to release. A second call is safe.</remarks>
        public void Dispose() => _cancellation?.Dispose();

        /// <summary>
        /// Waits until <paramref name="task"/> completes, <paramref name="cancellationToken"/> cancels, or <see cref="Cancellation"/> cancels.
        /// </summary>
        /// <param name="task">Lease drain or keepalive drain already started by the caller.</param>
        /// <param name="cancellationToken">Caller cancellation. Not the shared deadline token.</param>
        /// <returns>The wait. A cancelled deadline or caller throws <see cref="OperationCanceledException"/>.</returns>
        /// <remarks>
        /// <see cref="None"/> waits only on <paramref name="cancellationToken"/>.
        /// A bounded deadline does not sample <see cref="Remaining"/> into another timer.
        /// </remarks>
        public async Task WaitAsync(Task task, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(task);
            if (!_bounded || _cancellation is null)
            {
                await task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation.Token);
            await task.WaitAsync(linked.Token).ConfigureAwait(false);
        }

        /// <summary>Returns the shorter of <paramref name="allowance"/> and <see cref="Remaining"/>.</summary>
        /// <param name="allowance">Operation cap used when this deadline is <see cref="None"/> or has more time left.</param>
        /// <returns>
        /// <paramref name="allowance"/> when this deadline is unbounded or has at least that much time left.
        /// Otherwise the time remaining, which is <see cref="TimeSpan.Zero"/> after the deadline.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="allowance"/> is negative.</exception>
        public TimeSpan Cap(TimeSpan allowance)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(allowance, TimeSpan.Zero);
            if (!_bounded)
            {
                return allowance;
            }

            var remaining = Remaining;
            return remaining < allowance ? remaining : allowance;
        }

        /// <summary>Converts <paramref name="budget"/> into a <see cref="Stopwatch"/> timestamp delta.</summary>
        /// <param name="budget">Positive duration. The caller rejects zero and negative budgets.</param>
        /// <returns>A positive timestamp delta. Very large budgets are clamped so the addition cannot overflow.</returns>
        private static long TimestampDelta(TimeSpan budget)
        {
            var frequency = Stopwatch.Frequency;
            if (budget.Ticks >= long.MaxValue / frequency)
            {
                return long.MaxValue / 4;
            }

            var delta = budget.Ticks * frequency / TimeSpan.TicksPerSecond;
            return delta <= 0 ? 1 : delta;
        }
    }

    /// <summary>
    /// Runs independent shutdown work on a fixed number of workers and still waits for every item.
    /// </summary>
    /// <remarks>
    /// The degree does not grow with the item count. A worker that is blocked in one item does not stop the other workers
    /// from taking later items. An item fault is stored and the worker continues. After every item has been attempted,
    /// the stored faults are thrown as one <see cref="AggregateException"/>. Process-integrity failures fault that worker
    /// immediately and are not stored.
    /// </remarks>
    internal static class NntpShutdownWork
    {
        /// <summary>
        /// Workers used to retire sessions in one pool and pools in one registry shutdown.
        /// One blocked QUIT occupies one worker until the shared deadline cancels that write.
        /// The next item then observes that same cancellation and does not start another QUIT.
        /// </summary>
        internal const int Parallelism = 4;

        /// <summary>Invokes <paramref name="action"/> for every item, with at most <see cref="Parallelism"/> invocations at once.</summary>
        /// <typeparam name="T">Item type. The list is not mutated.</typeparam>
        /// <param name="items">Work already snapshotted by the caller. An empty list returns without starting a worker.</param>
        /// <param name="action">One item's cleanup. It must not depend on later items in <paramref name="items"/> having finished.</param>
        /// <returns>A task that completes after every item has been attempted.</returns>
        /// <exception cref="AggregateException">One or more item actions threw an exception other than a process-integrity failure.</exception>
        /// <remarks>
        /// Process-integrity failures are not caught here. They fault the worker that observed them.
        /// <see cref="Task.WhenAll(Task[])"/> still waits for the other workers, then surfaces that failure.
        /// </remarks>
        internal static async Task RunBoundedAsync<T>(IReadOnlyList<T> items, Func<T, Task> action)
        {
            ArgumentNullException.ThrowIfNull(items);
            ArgumentNullException.ThrowIfNull(action);
            if (items.Count == 0)
            {
                return;
            }

            var workers = Math.Min(Parallelism, items.Count);
            var next = 0;
            var faults = new ConcurrentQueue<Exception>();
            var tasks = new Task[workers];
            for (var worker = 0; worker < workers; worker++)
            {
                tasks[worker] = RunWorkerAsync();
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            if (!faults.IsEmpty)
            {
                throw new AggregateException(faults);
            }

            async Task RunWorkerAsync()
            {
                while (true)
                {
                    var index = Interlocked.Increment(ref next) - 1;
                    if ((uint)index >= (uint)items.Count)
                    {
                        return;
                    }

                    try
                    {
                        await action(items[index]).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                    {
                        faults.Enqueue(ex);
                    }
                }
            }
        }
    }
}

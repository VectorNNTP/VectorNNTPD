using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.StorageService.Listener;

namespace VectorNNTPD.StorageService.Tests.Listener
{

    public sealed class StoreReservationHoldTests
    {
        [Fact]
        public async Task TryRelease_invokes_queue_release_exactly_once_under_concurrency()
        {
            var queue = new CountingQueue();
            Assert.True(queue.TryReserve(64, out var reservation));
            Assert.Equal(64, queue.ReservedBytes);

            var hold = new StoreReservationHold();
            hold.Bind(queue, reservation);
            Assert.True(hold.IsBound);

            var starter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasesWon = 0;
            var workers = new Task[8];
            for (var i = 0; i < workers.Length; i++)
            {
                workers[i] = Task.Run(async () =>
                {
                    await starter.Task.ConfigureAwait(false);
                    if (hold.TryRelease())
                    {
                        Interlocked.Increment(ref releasesWon);
                    }
                });
            }

            starter.SetResult();
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);

            Assert.Equal(1, releasesWon);
            Assert.Equal(1, queue.ReleaseCalls);
            Assert.Equal(0, queue.ReservedBytes);
            Assert.False(hold.IsBound);
        }

        /// <summary>
        /// Models <c>AuthorizeReservedBodyAsync</c> failure when the reservation was never bound
        /// (direct <c>TryReserve</c> path) while concurrent <c>RemoveStore</c> runs.
        /// </summary>
        [Fact]
        public async Task Authorize_failure_releases_unbound_parameter_exactly_once_when_hold_never_bound()
        {
            var queue = new CountingQueue();
            Assert.True(queue.TryReserve(32, out var reservation));

            var hold = new StoreReservationHold();
            var gate = new object();
            var starter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var removeStore = Task.Run(async () =>
            {
                await starter.Task.ConfigureAwait(false);
                lock (gate)
                {
                    if (!hold.Committed)
                    {
                        hold.TryRelease();
                    }
                }
            });

            var authorizeFailure = Task.Run(async () =>
            {
                await starter.Task.ConfigureAwait(false);
                var reservationAlreadyBound = hold.IsBound;
                if (!hold.TryRelease() && !reservationAlreadyBound)
                {
                    queue.Release(reservation);
                }

                lock (gate)
                {
                    if (!hold.Committed)
                    {
                        hold.TryRelease();
                    }
                }
            });

            starter.SetResult();
            await Task.WhenAll(removeStore, authorizeFailure).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);

            Assert.Equal(1, queue.ReleaseCalls);
            Assert.Equal(0, queue.ReservedBytes);
        }

        /// <summary>
        /// Models AdmitWhenCapacity already-bound reservation racing Authorize failure with RemoveStore.
        /// Capturing <c>reservationAlreadyBound</c> at entry must not cause a second Release.
        /// </summary>
        [Fact]
        public async Task Authorize_failure_skips_parameter_when_reservation_already_bound()
        {
            var queue = new CountingQueue();
            Assert.True(queue.TryReserve(32, out var reservation));

            var hold = new StoreReservationHold();
            var gate = new object();
            lock (gate)
            {
                hold.Bind(queue, reservation);
            }

            var reservationAlreadyBound = hold.IsBound;
            Assert.True(reservationAlreadyBound);

            var starter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var removeStore = Task.Run(async () =>
            {
                await starter.Task.ConfigureAwait(false);
                lock (gate)
                {
                    if (!hold.Committed)
                    {
                        hold.TryRelease();
                    }
                }
            });

            var authorizeFailure = Task.Run(async () =>
            {
                await starter.Task.ConfigureAwait(false);
                if (!hold.TryRelease() && !reservationAlreadyBound)
                {
                    queue.Release(reservation);
                }

                lock (gate)
                {
                    if (!hold.Committed)
                    {
                        hold.TryRelease();
                    }
                }
            });

            starter.SetResult();
            await Task.WhenAll(removeStore, authorizeFailure).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);

            Assert.Equal(1, queue.ReleaseCalls);
            Assert.Equal(0, queue.ReservedBytes);
            Assert.False(hold.IsBound);
        }

        /// <summary>
        /// Commit path takes hold ownership before <c>TryCommit</c>; concurrent <c>RemoveStore</c>
        /// must not invoke <c>Release</c> after the reservation is consumed into the queue.
        /// </summary>
        [Fact]
        public async Task Commit_take_wins_over_teardown_and_never_releases_consumed_reservation()
        {
            var queue = new CountingQueue();
            Assert.True(queue.TryReserve(16, out var reservation));
            var hold = new StoreReservationHold();
            hold.Bind(queue, reservation);

            var commitTaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var teardownFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var teardown = Task.Run(async () =>
            {
                await commitTaken.Task.ConfigureAwait(false);
                // RemoveStore policy: skip TryRelease only after MarkCommitOutcome(true).
                // Between Take and Mark, Committed is still false — TryRelease must lose the take.
                if (!hold.Committed)
                {
                    hold.TryRelease();
                }

                teardownFinished.SetResult();
            });

            Assert.True(hold.TryTakeForCommit(out var commitQueue, out var commitReservation));
            commitTaken.SetResult();

            Assert.True(commitQueue!.TryCommit(commitReservation, CreateArticle(16)));
            hold.MarkCommitOutcome(true);

            await teardown.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            await teardownFinished.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);

            Assert.Equal(0, queue.ReleaseCalls);
            Assert.Equal(0, queue.ReservedBytes);
            Assert.Equal(16, queue.QueuedBytes);
            Assert.Equal(1, queue.QueuedCount);
            Assert.True(hold.Committed);
            Assert.False(hold.IsBound);
            Assert.False(hold.TryRelease());
        }

        /// <summary>
        /// Teardown takes ownership before commit; the commit path must observe the lost take and
        /// leave the article unqueued with reservation bytes fully released.
        /// </summary>
        [Fact]
        public async Task Teardown_release_before_commit_take_prevents_consumption()
        {
            var queue = new CountingQueue();
            Assert.True(queue.TryReserve(16, out var reservation));
            var hold = new StoreReservationHold();
            hold.Bind(queue, reservation);

            var teardownReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var commitFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var teardown = Task.Run(async () =>
            {
                Assert.True(hold.TryRelease());
                teardownReady.SetResult();
                await commitFinished.Task.ConfigureAwait(false);
            });

            await teardownReady.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);

            var taken = hold.TryTakeForCommit(out var commitQueue, out var commitReservation);
            var committed = false;
            if (taken)
            {
                committed = commitQueue!.TryCommit(commitReservation, CreateArticle(16));
                hold.MarkCommitOutcome(committed);
            }

            commitFinished.SetResult();
            await teardown.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);

            Assert.False(taken);
            Assert.False(committed);
            Assert.Equal(1, queue.ReleaseCalls);
            Assert.Equal(0, queue.ReservedBytes);
            Assert.Equal(0, queue.QueuedBytes);
            Assert.Equal(0, queue.QueuedCount);
            Assert.False(hold.IsBound);
        }

        /// <summary>
        /// Concurrent commit-take and teardown-release: exactly one path owns the reservation;
        /// queue accounting ends with either one queued article or a full release, never both.
        /// </summary>
        [Fact]
        public async Task Concurrent_commit_take_and_teardown_release_are_mutually_exclusive()
        {
            var queue = new CountingQueue();
            Assert.True(queue.TryReserve(16, out var reservation));
            var hold = new StoreReservationHold();
            hold.Bind(queue, reservation);

            var starter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var commitWon = 0;
            var releaseWon = 0;

            var commit = Task.Run(async () =>
            {
                await starter.Task.ConfigureAwait(false);
                if (hold.TryTakeForCommit(out var commitQueue, out var commitReservation))
                {
                    Interlocked.Increment(ref commitWon);
                    var ok = commitQueue!.TryCommit(commitReservation, CreateArticle(16));
                    hold.MarkCommitOutcome(ok);
                    Assert.True(ok);
                }
            });

            var teardown = Task.Run(async () =>
            {
                await starter.Task.ConfigureAwait(false);
                if (hold.TryRelease())
                {
                    Interlocked.Increment(ref releaseWon);
                }
            });

            starter.SetResult();
            await Task.WhenAll(commit, teardown).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);

            Assert.Equal(1, commitWon + releaseWon);
            Assert.Equal(0, queue.ReservedBytes);
            if (commitWon == 1)
            {
                Assert.Equal(0, queue.ReleaseCalls);
                Assert.Equal(1, queue.QueuedCount);
                Assert.Equal(16, queue.QueuedBytes);
                Assert.True(hold.Committed);
            }
            else
            {
                Assert.Equal(1, queue.ReleaseCalls);
                Assert.Equal(0, queue.QueuedCount);
                Assert.Equal(0, queue.QueuedBytes);
                Assert.False(hold.Committed);
            }
        }

        [Fact]
        public void Failed_commit_after_take_does_not_require_hold_release()
        {
            var queue = new CountingQueue();
            Assert.True(queue.TryReserve(16, out var reservation));
            var hold = new StoreReservationHold();
            hold.Bind(queue, reservation);

            Assert.True(hold.TryTakeForCommit(out var commitQueue, out var commitReservation));
            // Size mismatch: TryCommit removes the reservation and frees reserved bytes.
            Assert.False(commitQueue!.TryCommit(commitReservation, CreateArticle(8)));
            hold.MarkCommitOutcome(false);

            Assert.False(hold.TryRelease());
            Assert.Equal(0, queue.ReleaseCalls);
            Assert.Equal(0, queue.ReservedBytes);
            Assert.Equal(0, queue.QueuedCount);
            Assert.False(hold.Committed);
        }

        [Fact]
        public async Task Admission_exit_is_exactly_once_under_concurrency()
        {
            var admission = new StoreAssemblyAdmission(4);
            Assert.True(admission.TryEnter());
            Assert.Equal(1, admission.InFlight);

            StoreAssemblyAdmission? held = admission;
            var starter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workers = new Task[8];
            for (var i = 0; i < workers.Length; i++)
            {
                workers[i] = Task.Run(async () =>
                {
                    await starter.Task.ConfigureAwait(false);
                    Interlocked.Exchange(ref held, null)?.Exit();
                });
            }

            starter.SetResult();
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);

            Assert.Equal(0, admission.InFlight);
            Assert.Null(held);
        }

        private static AcceptedArticle CreateArticle(int size)
        {
            return new AcceptedArticle(default, 1, size, new byte[size]);
        }

        /// <summary>Production queue wrapper that counts <see cref="IAcceptedArticleQueue.Release"/> calls.</summary>
        private sealed class CountingQueue : IAcceptedArticleQueue
        {
            private readonly AcceptedArticleQueue _inner = new(maxQueuedBytes: 1024 * 1024, TimeProvider.System);
            private int _releaseCalls;

            public int ReleaseCalls => Volatile.Read(ref _releaseCalls);

            public long MaxQueuedBytes => _inner.MaxQueuedBytes;

            public long QueuedBytes => _inner.QueuedBytes;

            public long ReservedBytes => _inner.ReservedBytes;

            public long AccountedBytes => _inner.AccountedBytes;

            public int AdmissionWaiters => _inner.AdmissionWaiters;

            public int QueuedCount => _inner.QueuedCount;

            public bool IsCompleted => _inner.IsCompleted;

            public bool TryEnqueue(AcceptedArticle article) => _inner.TryEnqueue(article);

            public bool TryReserve(int bytes, out QueueReservation reservation) =>
                _inner.TryReserve(bytes, out reservation);

            public ValueTask<QueueReservation?> ReserveAsync(int bytes, CancellationToken cancellationToken) =>
                _inner.ReserveAsync(bytes, cancellationToken);

            public bool TryCommit(QueueReservation reservation, AcceptedArticle article) =>
                _inner.TryCommit(reservation, article);

            public void Release(QueueReservation reservation)
            {
                Interlocked.Increment(ref _releaseCalls);
                _inner.Release(reservation);
            }

            public TimeSpan OldestAge(TimeProvider time) => _inner.OldestAge(time);

            public bool TryDequeue(out AcceptedArticleDelivery delivery) => _inner.TryDequeue(out delivery);

            public ValueTask<AcceptedArticleDelivery> DequeueAsync(CancellationToken cancellationToken) =>
                _inner.DequeueAsync(cancellationToken);

            public void Complete() => _inner.Complete();
        }
    }
}

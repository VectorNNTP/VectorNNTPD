using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Nntp
{
    /// <summary>
    /// NNTP session pool leasing, replenishment, shrink, shutdown, and DATE keep-alive with scripted transports.
    /// </summary>

    public sealed class NntpSessionPoolTests
    {
        /// <summary>
        /// EnsureDesiredSessions opens MaxSessions connections before any lease is taken.
        /// </summary>

        [Fact]

        public async Task EnsureDesiredSessions_connects_max_sessions_before_any_lease()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 3);
            var pool = CreatePool(factory, max: 3);

            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            Assert.Equal(3, pool.ActiveSessionCount);
            Assert.Equal(0, pool.ActiveLeaseCount);
            Assert.Equal(3, factory.ConnectAttempts.Count);
            await pool.DisposeAsync();
        }

        /// <summary>
        /// BindProvider shrink retires excess idle sessions without additional connect attempts.
        /// </summary>

        [Fact]

        public async Task ShrinkToBound_retires_excess_idle_sessions_without_reconnect()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 4);
            var pool = CreatePool(factory, max: 4);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
            var seen = new HashSet<NntpProviderSession>();
            for (var i = 0; i < 4; i++)
            {
                await using var lease = await pool.AcquireAsync(CancellationToken.None);
                seen.Add(lease.Session);
            }

            pool.BindProvider(
                new BackFillerProviderDefinition(
                    "Giganews",
                    "127.0.0.1",
                    119,
                    false,
                    null,
                    null,
                    0,
                    2));
            await pool.ShrinkToBoundAsync(CancellationToken.None);

            Assert.Equal(2, pool.LiveSessionCount);
            Assert.Equal(2, pool.ActiveSessionCount);
            Assert.Equal(4, factory.ConnectAttempts.Count);
            await using var reused = await pool.AcquireAsync(CancellationToken.None);
            Assert.Contains(reused.Session, seen);
            await reused.DisposeAsync();
            await pool.DisposeAsync();
        }

        /// <summary>
        /// Shrinking 4 to 2 and then to 1 leaves one acquire permit for a healthy idle session.
        /// </summary>
        [Fact]
        public async Task Successive_shrink_to_one_still_acquires_the_idle_session()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 4);
            var pool = CreatePool(factory, max: 4);
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                BindMax(pool, 2);
                await pool.ShrinkToBoundAsync(CancellationToken.None);
                BindMax(pool, 1);
                await pool.ShrinkToBoundAsync(CancellationToken.None);

                Assert.Equal(1, pool.LiveSessionCount);
                Assert.Equal(1, pool.AvailablePermitCount);
                var lease = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(1, pool.ActiveLeaseCount);
                Assert.Equal(0, pool.AvailablePermitCount);
                Assert.True(lease.Session.IsReusable);
                await lease.DisposeAsync();
                Assert.Equal(0, pool.ActiveLeaseCount);
                Assert.Equal(1, pool.AvailablePermitCount);
            }
            finally
            {
                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// Leases held across 4 to 2 to 1 still return the pool to one usable permit and do not leave a second permit behind.
        /// </summary>
        [Fact]
        public async Task Successive_shrink_with_active_leases_is_not_starved_after_return()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 4);
            var pool = CreatePool(factory, max: 4);
            var leases = new List<NntpSessionLease>();
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                for (var i = 0; i < 3; i++)
                {
                    leases.Add(await pool.AcquireAsync(CancellationToken.None));
                }

                BindMax(pool, 2);
                await pool.ShrinkToBoundAsync(CancellationToken.None);
                BindMax(pool, 1);
                await pool.ShrinkToBoundAsync(CancellationToken.None);
                Assert.Equal(3, pool.ActiveLeaseCount);
                Assert.Equal(0, pool.AvailablePermitCount);

                foreach (var lease in leases)
                {
                    await lease.DisposeAsync();
                }

                leases.Clear();
                Assert.Equal(0, pool.ActiveLeaseCount);
                Assert.Equal(1, pool.AvailablePermitCount);
                Assert.Equal(1, pool.LiveSessionCount);

                var again = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(1, pool.ActiveLeaseCount);
                Assert.Equal(0, pool.AvailablePermitCount);
                await again.DisposeAsync();
                Assert.Equal(1, pool.AvailablePermitCount);
            }
            finally
            {
                foreach (var lease in leases)
                {
                    await lease.DisposeAsync();
                }

                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// A shrink that overlaps two reserved acquires issues only the final bound and still wakes the loser after return.
        /// </summary>
        [Fact]
        public async Task Shrink_during_reserved_acquire_issues_only_the_final_bound()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 2);
            var pool = CreatePool(factory, max: 2);
            using var acquireCancellation = new CancellationTokenSource();
            var entered = 0;
            var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pool.BeforePermitIssuance = () =>
            {
                if (Interlocked.Increment(ref entered) == 2)
                {
                    bothEntered.TrySetResult();
                }

                return release.Task;
            };
            Task<NntpSessionLease>? first = null;
            Task<NntpSessionLease>? second = null;
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                first = pool.AcquireAsync(acquireCancellation.Token);
                second = pool.AcquireAsync(acquireCancellation.Token);
                await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                BindMax(pool, 1);
                await pool.ShrinkToBoundAsync(CancellationToken.None);
                release.TrySetResult();

                await WaitUntilAsync(() =>
                    (first.IsCompletedSuccessfully && !second.IsCompleted && pool.ActiveLeaseCount == 1 && pool.AvailablePermitCount == 0)
                    || (second.IsCompletedSuccessfully && !first.IsCompleted && pool.ActiveLeaseCount == 1 && pool.AvailablePermitCount == 0));
                var winner = first.IsCompletedSuccessfully ? first : second;
                var loser = ReferenceEquals(winner, first) ? second : first;
                var lease = await winner;
                Assert.False(loser.IsCompleted);
                Assert.Equal(1, pool.ActiveLeaseCount);

                await lease.DisposeAsync();
                var followed = await loser.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(1, pool.ActiveLeaseCount);
                await followed.DisposeAsync();
                Assert.Equal(0, pool.ActiveLeaseCount);
                Assert.Equal(1, pool.AvailablePermitCount);
            }
            finally
            {
                release.TrySetResult();
                pool.BeforePermitIssuance = null;
                acquireCancellation.Cancel();
                await SettleAcquireAsync(first);
                await SettleAcquireAsync(second);
                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// Growing after 4 to 2 to 1 restores three permits and serves three acquires.
        /// </summary>
        [Fact]
        public async Task Growth_after_successive_shrinks_restores_the_new_bound()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 4);
            var pool = CreatePool(factory, max: 4);
            var leases = new List<NntpSessionLease>();
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                BindMax(pool, 2);
                await pool.ShrinkToBoundAsync(CancellationToken.None);
                BindMax(pool, 1);
                await pool.ShrinkToBoundAsync(CancellationToken.None);
                EnqueueReadyServers(factory, count: 2);
                BindMax(pool, 3);
                pool.GrowToBound();
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

                Assert.Equal(3, pool.AvailablePermitCount);
                Assert.Equal(3, pool.LiveSessionCount);
                for (var i = 0; i < 3; i++)
                {
                    leases.Add(await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
                }

                Assert.Equal(3, pool.ActiveLeaseCount);
                Assert.Equal(0, pool.AvailablePermitCount);
            }
            finally
            {
                foreach (var lease in leases)
                {
                    await lease.DisposeAsync();
                }

                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// Cancelling a permit wait does not consume the held lease's permit, and dispose rejects a later acquire.
        /// </summary>
        [Fact]
        public async Task Cancelled_permit_wait_and_dispose_do_not_strand_or_leak_permits()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 1);
            var pool = CreatePool(factory, max: 1);
            NntpSessionLease? lease = null;
            var pending = Task.CompletedTask;
            using var cts = new CancellationTokenSource();
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                lease = await pool.AcquireAsync(CancellationToken.None);
                pending = pool.AcquireAsync(cts.Token);
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                Assert.Equal(1, pool.ActiveLeaseCount);
                Assert.Equal(0, pool.AvailablePermitCount);

                var dispose = pool.DisposeAsync().AsTask();
                await lease.DisposeAsync();
                lease = null;
                await dispose.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.AcquireAsync(CancellationToken.None));
            }
            finally
            {
                if (lease is not null)
                {
                    await lease.DisposeAsync();
                }

                try
                {
                    await pending.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (OperationCanceledException ex)
                {
                    // The pending acquire was cancelled and has finished. cts is still alive until this method returns.
                    ObserveExpectedCleanup(ex);
                }
                catch (ObjectDisposedException ex)
                {
                    // Pool disposal won the race with the pending acquire.
                    ObserveExpectedCleanup(ex);
                }

                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// Released healthy lease reuses the same connection when max is one.
        /// </summary>

        [Fact]

        public async Task Pool_reuses_a_healthy_session_and_does_not_exceed_max()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 2);
            var pool = CreatePool(factory, max: 1);

            await using var first = await pool.AcquireAsync(CancellationToken.None);
            Assert.Equal(1, pool.LiveSessionCount);
            await first.DisposeAsync();

            await using var second = await pool.AcquireAsync(CancellationToken.None);
            Assert.Single(factory.ConnectAttempts);
            Assert.Equal(1, pool.ActiveLeaseCount);
            await second.DisposeAsync();
            await pool.DisposeAsync();
        }

        /// <summary>
        /// Concurrent acquires receive distinct sessions up to pool capacity.
        /// </summary>

        [Fact]

        public async Task Pool_does_not_allow_concurrent_use_of_one_session()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 2);
            var pool = CreatePool(factory, max: 2);
            var first = await pool.AcquireAsync(CancellationToken.None);
            var second = await pool.AcquireAsync(CancellationToken.None);

            Assert.NotSame(first.Session, second.Session);
            Assert.Equal(2, pool.LiveSessionCount);
            await first.DisposeAsync();
            await second.DisposeAsync();
            await pool.DisposeAsync();
        }

        /// <summary>
        /// Canceled wait on full pool and dispose while waiting fail without leaking leases.
        /// </summary>

        [Fact]

        public async Task Acquire_cancellation_and_shutdown_while_waiting()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 1);
            var pool = CreatePool(factory, max: 1);
            var held = await pool.AcquireAsync(CancellationToken.None);

            using var cts = new CancellationTokenSource();
            var waiting = pool.AcquireAsync(cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

            var shutdownWait = pool.AcquireAsync(CancellationToken.None);
            var disposing = pool.DisposeAsync();
            await held.DisposeAsync();
            await disposing;
            await Assert.ThrowsAnyAsync<Exception>(() => shutdownWait);
        }

        /// <summary>
        /// Non-reusable session after protocol error is replaced on next acquire.
        /// </summary>

        [Fact]

        public async Task Broken_session_is_not_reused()
        {
            var factory = new ScriptedNntpTransportFactory();
            var firstServer = new ScriptedNntpServer();
            firstServer.Respond(static _ => "not-status\r\n");
            factory.Enqueue(firstServer);
            factory.Enqueue(new ScriptedNntpServer());
            var pool = CreatePool(factory, max: 1);

            await using (var lease = await pool.AcquireAsync(CancellationToken.None))
            {
                using var result = await lease.Session.DownloadArticleAsync("<a@b>", CancellationToken.None);
                Assert.False(result.SessionReusable);
                lease.Retire();
            }

            var replacement = await pool.AcquireAsync(CancellationToken.None);
            Assert.Equal(2, factory.ConnectAttempts.Count);
            await replacement.DisposeAsync();
            await pool.DisposeAsync();
        }

        /// <summary>
        /// Double dispose on a lease does not put the same session back twice.
        /// </summary>

        [Fact]

        public async Task Double_release_does_not_return_the_session_twice()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 1);
            var pool = CreatePool(factory, max: 1);
            var lease = await pool.AcquireAsync(CancellationToken.None);
            await lease.DisposeAsync();
            Assert.Equal(0, pool.ActiveLeaseCount);

            var again = await pool.AcquireAsync(CancellationToken.None);
            Assert.Single(factory.ConnectAttempts);
            await again.DisposeAsync();
            await pool.DisposeAsync();
        }

        /// <summary>
        /// Pool dispose waits for active lease release then rejects further acquires.
        /// </summary>

        [Fact]

        public async Task Shutdown_with_active_lease_waits_then_closes()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 1);
            var pool = CreatePool(factory, max: 1);
            var lease = await pool.AcquireAsync(CancellationToken.None);
            var dispose = pool.DisposeAsync();
            await lease.DisposeAsync();
            await dispose;

            await Assert.ThrowsAnyAsync<Exception>(() => pool.AcquireAsync(CancellationToken.None));
        }

        /// <summary>
        /// An acquire that has already passed the initial disposed check cannot take a lease after drain was observed.
        /// The created session is retired once, and the drain does not return while a new lease is outstanding.
        /// </summary>
        [Fact]
        public async Task Dispose_rejects_issuance_after_drain_was_observed()
        {
            var readyToIssue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseIssue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var drainObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = new ScriptedNntpServer();
            server.Respond(static command =>
                command.StartsWith("AUTHINFO", StringComparison.OrdinalIgnoreCase)
                    ? "281 authentication accepted\r\n"
                    : "430 missing\r\n");
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(server);
            var pool = CreatePool(factory, max: 1);
            pool.BeforeCreatedSessionIssue = () =>
            {
                readyToIssue.TrySetResult();
                return releaseIssue.Task;
            };
            pool.AfterDrainObserved = () =>
            {
                drainObserved.TrySetResult();
                return releaseDrain.Task;
            };
            var acquire = pool.AcquireAsync(CancellationToken.None);
            try
            {
                await readyToIssue.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var dispose = pool.DrainAndDisposeAsync(CancellationToken.None);
                await drainObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                releaseIssue.TrySetResult();
                await Assert.ThrowsAsync<ObjectDisposedException>(async () => await acquire);
                Assert.Equal(0, pool.ActiveLeaseCount);
                releaseDrain.TrySetResult();
                await dispose.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, pool.ActiveLeaseCount);
                Assert.Equal(0, pool.LiveSessionCount);
                Assert.Equal(1, server.ClientStreamDisposeCount);
            }
            finally
            {
                pool.BeforeCreatedSessionIssue = null;
                pool.AfterDrainObserved = null;
                releaseIssue.TrySetResult();
                releaseDrain.TrySetResult();
                try
                {
                    var leaked = await acquire.WaitAsync(TimeSpan.FromSeconds(5));
                    await leaked.DisposeAsync();
                }
                catch (Exception ex) when (ex is not (ArgumentNullException or NullReferenceException))
                {
                    _ = ex.GetType();
                }

                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// A return that reaches zero leases completes only the generation it captured.
        /// A newer acquire installs the next generation while that completion is paused, and dispose waits on the generation that still has a lease.
        /// </summary>
        [Fact]
        public async Task Returning_lease_completes_only_the_generation_it_captured()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 2);
            var pool = CreatePool(factory, max: 2);
            var generationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseGeneration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var drainEntered = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            NntpSessionLease? nextLease = null;
            NntpSessionLease? joined = null;
            Task? returning = null;
            Task? disposing = null;
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                var warmup = await pool.AcquireAsync(CancellationToken.None);
                var firstGeneration = pool.CurrentDrain;
                Assert.False(firstGeneration.IsCompleted);
                Assert.Equal(1, pool.ActiveLeaseCount);
                await warmup.DisposeAsync();
                Assert.True(firstGeneration.IsCompletedSuccessfully);
                Assert.Same(firstGeneration, pool.CurrentDrain);
                Assert.Equal(0, pool.ActiveLeaseCount);
                Assert.Equal(2, pool.AvailablePermitCount);

                var held = await pool.AcquireAsync(CancellationToken.None);
                var generation = pool.CurrentDrain;
                Assert.NotSame(firstGeneration, generation);
                Assert.False(generation.IsCompleted);
                pool.BeforeDrainGenerationCompleted = () =>
                {
                    generationEntered.TrySetResult();
                    return releaseGeneration.Task;
                };

                returning = held.DisposeAsync().AsTask();
                await generationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(0, pool.ActiveLeaseCount);
                Assert.Equal(1, pool.AvailablePermitCount);
                Assert.False(generation.IsCompleted);

                nextLease = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                var next = pool.CurrentDrain;
                Assert.NotSame(generation, next);
                Assert.False(next.IsCompleted);
                Assert.Equal(1, pool.ActiveLeaseCount);
                Assert.Equal(0, pool.AvailablePermitCount);

                releaseGeneration.TrySetResult();
                await returning.WaitAsync(TimeSpan.FromSeconds(2));
                returning = null;
                pool.BeforeDrainGenerationCompleted = null;
                Assert.True(generation.IsCompletedSuccessfully);
                Assert.False(next.IsCompleted);
                Assert.Equal(1, pool.ActiveLeaseCount);
                Assert.Equal(1, pool.AvailablePermitCount);

                joined = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Same(next, pool.CurrentDrain);
                Assert.Equal(2, pool.ActiveLeaseCount);
                Assert.Equal(0, pool.AvailablePermitCount);
                await joined.DisposeAsync();
                joined = null;
                Assert.False(next.IsCompleted);
                Assert.Equal(1, pool.ActiveLeaseCount);
                Assert.Equal(1, pool.AvailablePermitCount);

                pool.BeforeLeaseDrainWait = task =>
                {
                    drainEntered.TrySetResult(task);
                    return releaseDrain.Task;
                };
                disposing = pool.DrainAndDisposeAsync(CancellationToken.None);
                var observed = await drainEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Same(next, observed);
                Assert.False(observed.IsCompleted);
                Assert.False(disposing.IsCompleted);
                var repeated = pool.DrainAndDisposeAsync(CancellationToken.None);
                await repeated.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(next.IsCompleted);
                Assert.False(disposing.IsCompleted);
                await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.AcquireAsync(CancellationToken.None));
                Assert.Equal(1, pool.ActiveLeaseCount);
                Assert.Equal(1, pool.AvailablePermitCount);

                releaseDrain.TrySetResult();
                await nextLease.DisposeAsync();
                nextLease = null;
                Assert.True(next.IsCompletedSuccessfully);
                await disposing.WaitAsync(TimeSpan.FromSeconds(2));
                disposing = null;
                Assert.Equal(0, pool.ActiveLeaseCount);
                await pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally
            {
                releaseGeneration.TrySetResult();
                releaseDrain.TrySetResult();
                pool.BeforeDrainGenerationCompleted = null;
                pool.BeforeLeaseDrainWait = null;
                if (joined is not null)
                {
                    await joined.DisposeAsync();
                }

                if (nextLease is not null)
                {
                    await nextLease.DisposeAsync();
                }

                if (returning is not null)
                {
                    try
                    {
                        await returning.WaitAsync(TimeSpan.FromSeconds(2));
                    }
                    catch (TimeoutException ex)
                    {
                        // The paused return did not finish after its gate was released.
                        ObserveExpectedCleanup(ex);
                    }
                }

                if (disposing is not null)
                {
                    try
                    {
                        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (TimeoutException ex)
                    {
                        // Dispose did not finish after the outstanding lease was returned.
                        ObserveExpectedCleanup(ex);
                    }
                }

                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// Unreusable session after failed ARTICLE is not returned to the idle pool.
        /// </summary>

        [Fact]

        public async Task Session_failure_during_release_does_not_return_the_session()
        {
            var factory = new ScriptedNntpTransportFactory();
            var first = new ScriptedNntpServer { CompleteAfterResponse = true };
            first.Respond(static _ => "220 follows\r\nFrom: a@b\r\n\r\nbody\r\n");
            factory.Enqueue(first);
            factory.Enqueue(new ScriptedNntpServer());
            var pool = CreatePool(factory, max: 1);

            await using (var lease = await pool.AcquireAsync(CancellationToken.None))
            {
                using var result = await lease.Session.DownloadArticleAsync("<a@b>", CancellationToken.None);
                Assert.False(result.SessionReusable);
            }

            var replacement = await pool.AcquireAsync(CancellationToken.None);
            Assert.Equal(2, factory.ConnectAttempts.Count);
            await replacement.DisposeAsync();
            await pool.DisposeAsync();
        }

        /// <summary>
        /// Failed connect on acquire does not leave ActiveLeaseCount elevated.
        /// An <see cref="IOException"/> still releases live membership, the created count, and the connection number,
        /// and the single establishment permit is returned so a later acquire can connect.
        /// </summary>

        [Fact]

        public async Task Connect_failure_does_not_leak_a_lease_slot()
        {
            var factory = new ScriptedNntpTransportFactory
            {
                ConnectException = new IOException("down"),
            };
            using var gate = CreateEstablishmentGate(1);
            var pool = CreatePool(factory, max: 1, establishment: gate);

            await Assert.ThrowsAsync<NntpProviderConnectException>(() => pool.AcquireAsync(CancellationToken.None));
            AssertNoReservedCapacity(pool);

            factory.ConnectException = null;
            EnqueueReadyServers(factory, count: 1);
            await using var recovered = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, recovered.Session.ConnectionNumber);
            Assert.Equal(NntpSessionState.Ready, recovered.Session.State);
            Assert.Equal(1, pool.LiveSessionCount);
            Assert.Equal(1, CreatedSessionCount(pool));
            await recovered.DisposeAsync();
            await pool.DisposeAsync();
        }

        /// <summary>
        /// <see cref="SocketException"/> during connect fails the acquire, removes the session from the live set,
        /// and returns created-count and connection-number capacity so the next connect can use slot 1.
        /// </summary>

        [Fact]

        public async Task Socket_exception_during_connect_releases_capacity_and_the_next_acquire_succeeds()
        {
            var factory = new ScriptedNntpTransportFactory
            {
                ConnectException = new SocketException((int)SocketError.ConnectionRefused),
            };
            using var gate = CreateEstablishmentGate(1);
            await using var pool = CreatePool(factory, max: 1, establishment: gate);

            var failure = await Assert.ThrowsAsync<NntpProviderConnectException>(
                () => pool.AcquireAsync(CancellationToken.None));

            Assert.Equal(ArticleRetrievalKind.ProviderFailure, failure.Kind);
            Assert.Equal(nameof(SocketException), failure.Message);
            AssertNoReservedCapacity(pool);

            factory.ConnectException = null;
            EnqueueReadyServers(factory, count: 1);
            await using var recovered = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, recovered.Session.ConnectionNumber);
            Assert.Equal(NntpSessionState.Ready, recovered.Session.State);
            Assert.Equal(1, pool.LiveSessionCount);
            Assert.Equal(1, pool.ActiveSessionCount);
            Assert.Equal(1, CreatedSessionCount(pool));
            Assert.Equal(1, ReservedConnectionCount(pool));
        }

        /// <summary>
        /// Parallel and repeated socket failures do not accumulate live sessions or created-count reservations.
        /// After the transport recovers, the pool fills exactly <c>MaxSessions</c> and reuses connection numbers 1 and 2.
        /// </summary>

        [Fact]

        public async Task Repeated_socket_exceptions_do_not_leak_slots_and_recovery_fills_the_pool()
        {
            var factory = new ScriptedNntpTransportFactory
            {
                ConnectException = new SocketException((int)SocketError.ConnectionRefused),
            };
            await using var pool = CreatePool(factory, max: 2);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                AssertNoReservedCapacity(pool);
            }

            factory.ConnectException = null;
            EnqueueReadyServers(factory, count: 2);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            Assert.Equal(2, pool.LiveSessionCount);
            Assert.Equal(2, pool.ActiveSessionCount);
            Assert.Equal(2, CreatedSessionCount(pool));
            Assert.Equal(2, ReservedConnectionCount(pool));

            await using var first = await pool.AcquireAsync(CancellationToken.None);
            await using var second = await pool.AcquireAsync(CancellationToken.None);
            Assert.NotSame(first.Session, second.Session);
            var connectionNumbers = new[] { first.Session.ConnectionNumber, second.Session.ConnectionNumber };
            Array.Sort(connectionNumbers);
            Assert.Equal(new[] { 1, 2 }, connectionNumbers);
        }

        /// <summary>
        /// The first periodic tick keeps a socket failure inside session creation and does not log a replenish failure.
        /// The second tick throws <see cref="NotSupportedException"/>, which the replenish supervisor logs, and rolls the reservation back.
        /// The third tick restores one ready session. Shutdown then completes the loop without entering another delay.
        /// </summary>

        [Fact]

        public async Task Periodic_replenish_logs_unexpected_failure_and_restores_capacity()
        {
            var logger = new CollectingLogger<NntpSessionPool>();
            var delays = new ReplenishDelaySource();
            var factory = new ScriptedNntpTransportFactory
            {
                ConnectException = new SocketException((int)SocketError.ConnectionRefused),
            };
            await using var pool = CreatePool(factory, max: 1, time: delays, logger: logger);

            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
            AssertNoReservedCapacity(pool);

            var releaseFirst = await delays.WaitForNextAsync();
            releaseFirst();
            var releaseSecond = await delays.WaitForNextAsync();
            Assert.False(ReplenishTask(pool).IsCompleted);
            AssertNoReservedCapacity(pool);
            Assert.DoesNotContain(
                logger.Entries,
                static entry => entry.EventId.Id == 5404);

            factory.ConnectException = new NotSupportedException("replenish tick fault");
            releaseSecond();
            var releaseThird = await delays.WaitForNextAsync();
            Assert.False(ReplenishTask(pool).IsCompleted);
            Assert.False(ReplenishTask(pool).IsFaulted);
            AssertNoReservedCapacity(pool);
            Assert.Contains(
                logger.Entries,
                static entry => entry.Level == LogLevel.Warning
                    && entry.EventId.Id == 5404
                    && entry.Message.Contains("replenish tick fault", StringComparison.Ordinal));

            factory.ConnectException = null;
            EnqueueReadyServers(factory, count: 1);
            releaseThird();
            await WaitUntilAsync(() => pool.ActiveSessionCount == 1);
            Assert.Equal(1, pool.LiveSessionCount);
            Assert.Equal(1, CreatedSessionCount(pool));
            Assert.Equal(1, ReservedConnectionCount(pool));

            await using (var lease = await pool.AcquireAsync(CancellationToken.None))
            {
                Assert.Equal(1, lease.Session.ConnectionNumber);
                Assert.Equal(NntpSessionState.Ready, lease.Session.State);
            }

            _ = await delays.WaitForNextAsync();
            Assert.False(ReplenishTask(pool).IsCompleted);
            var timersAtCapacity = delays.TimersCreated;
            var shutdownField = typeof(NntpSessionPool).GetField(
                "_shutdown",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var shutdown = Assert.IsType<CancellationTokenSource>(shutdownField?.GetValue(pool));
            await shutdown.CancelAsync();
            await WaitUntilAsync(() => ReplenishTask(pool).IsCompleted);
            Assert.Equal(TaskStatus.RanToCompletion, ReplenishTask(pool).Status);
            Assert.Equal(timersAtCapacity, delays.TimersCreated);
        }

        /// <summary>
        /// A throwing active-count subscriber on a successful connect retires that session once.
        /// With one permitted session, the next acquire reuses connection number 1.
        /// </summary>

        [Fact]

        public async Task Success_notification_failure_releases_capacity_and_the_next_acquire_succeeds()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 2);
            using var gate = CreateEstablishmentGate(1);
            await using var pool = CreatePool(factory, max: 1, establishment: gate);
            var notifications = 0;
            pool.ActiveSessionCountChanged += () =>
            {
                if (Interlocked.Increment(ref notifications) == 1)
                {
                    throw new InvalidOperationException("active count subscriber");
                }
            };

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => pool.AcquireAsync(CancellationToken.None));
            Assert.Equal("active count subscriber", failure.Message);
            AssertNoReservedCapacity(pool);

            await using var recovered = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, recovered.Session.ConnectionNumber);
            Assert.Equal(NntpSessionState.Ready, recovered.Session.State);
            Assert.Equal(1, pool.LiveSessionCount);
            Assert.Equal(1, pool.ActiveLeaseCount);
            Assert.Equal(1, CreatedSessionCount(pool));
            Assert.Equal(1, ReservedConnectionCount(pool));
            Assert.Equal(3, Volatile.Read(ref notifications));
        }

        /// <summary>
        /// A completion-log failure after a ready connect retires that session once and leaves the establishment permit free.
        /// The same logger accepts the next completion, and the next acquire gets connection number 1.
        /// </summary>

        [Fact]

        public async Task Completion_log_failure_releases_capacity_and_the_next_acquire_succeeds()
        {
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 2);
            using var gate = CreateEstablishmentGate(1);
            var logger = new ThrowOnceOnCompletionLogger();
            await using var pool = CreatePool(factory, max: 1, establishment: gate, logger: logger);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => pool.AcquireAsync(CancellationToken.None));
            Assert.Equal("completion log failed", failure.Message);
            AssertNoReservedCapacity(pool);

            await using var recovered = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, recovered.Session.ConnectionNumber);
            Assert.Equal(NntpSessionState.Ready, recovered.Session.State);
            Assert.Equal(1, pool.LiveSessionCount);
            Assert.Equal(1, pool.ActiveLeaseCount);
            Assert.Equal(1, CreatedSessionCount(pool));
            Assert.Equal(1, ReservedConnectionCount(pool));
            Assert.Equal(1, logger.CompletionFailures);
        }

        /// <summary>
        /// A retirement callback that blocks cannot drop a replacement session's capacity notification.
        /// The subscriber throws after the replacement is published, and a later callback still reports that session.
        /// </summary>

        [Fact]

        public async Task Capacity_notification_during_a_blocked_retirement_callback_is_delivered()
        {
            var delays = new ReplenishDelaySource();
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 2);
            await using var pool = CreatePool(factory, max: 1, time: delays);
            var retirementEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRetirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recoveredCapacity = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observations = new ConcurrentQueue<int>();
            pool.ActiveSessionCountChanged += () =>
            {
                var count = pool.ActiveSessionCount;
                observations.Enqueue(count);
                if (count == 0 && retirementEntered.TrySetResult())
                {
                    if (!releaseRetirement.Task.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("Retirement callback was not released.");
                    }

                    throw new InvalidOperationException("retirement subscriber");
                }

                if (count > 0 && retirementEntered.Task.IsCompleted)
                {
                    recoveredCapacity.TrySetResult();
                }
            };

            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
            Assert.Equal(1, pool.ActiveSessionCount);

            var retirement = Task.Run(async () =>
            {
                await using var lease = await pool.AcquireAsync(CancellationToken.None);
                lease.Retire();
            });

            await retirementEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
            releaseRetirement.TrySetResult();
            await recoveredCapacity.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, pool.ActiveSessionCount);
            Assert.Equal(1, pool.LiveSessionCount);
            Assert.Equal(0, pool.ActiveLeaseCount);
            Assert.Equal(1, CreatedSessionCount(pool));
            Assert.Equal(1, ReservedConnectionCount(pool));
            Assert.Contains(observations, static count => count == 1);
            Assert.Equal(pool.ActiveSessionCount, observations.Last());

            await using var recovered = await pool.AcquireAsync(CancellationToken.None);
            Assert.Equal(1, recovered.Session.ConnectionNumber);
            Assert.Equal(NntpSessionState.Ready, recovered.Session.State);
        }

        /// <summary>
        /// A logger that fails while a connect failure is recorded does not replace that failure or keep the reservation.
        /// </summary>

        [Fact]

        public async Task Logger_failure_during_connect_failure_preserves_the_connect_exception_and_releases_capacity()
        {
            var factory = new ScriptedNntpTransportFactory
            {
                ConnectException = new SocketException((int)SocketError.ConnectionRefused),
            };
            using var gate = CreateEstablishmentGate(1);
            await using var pool = CreatePool(factory, max: 1, establishment: gate, logger: new ThrowingEstablishmentLogger());

            var failure = await Assert.ThrowsAsync<NntpProviderConnectException>(
                () => pool.AcquireAsync(CancellationToken.None));
            Assert.Equal(ArticleRetrievalKind.ProviderFailure, failure.Kind);
            Assert.Equal(nameof(SocketException), failure.Message);
            AssertNoReservedCapacity(pool);

            factory.ConnectException = null;
            EnqueueReadyServers(factory, count: 1);
            await using var recovered = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, recovered.Session.ConnectionNumber);
            Assert.Equal(NntpSessionState.Ready, recovered.Session.State);
            Assert.Equal(1, pool.LiveSessionCount);
            Assert.Equal(1, CreatedSessionCount(pool));
            Assert.Equal(1, ReservedConnectionCount(pool));
        }

        /// <summary>
        /// Cancelling while the new session is registered but still waiting for the establishment gate
        /// removes that registration once. The same connection number can be acquired afterward.
        /// </summary>

        [Fact]

        public async Task Cancellation_before_connect_rolls_back_the_registered_session_once()
        {
            using var gate = CreateEstablishmentGate(1);
            var gateHeld = false;
            await gate.WaitAsync(CancellationToken.None);
            gateHeld = true;
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 1);
            await using var pool = CreatePool(factory, max: 1, establishment: gate);
            try
            {
                using var cts = new CancellationTokenSource();
                var acquire = pool.AcquireAsync(cts.Token);
                await WaitUntilAsync(() => pool.LiveSessionCount == 1);
                Assert.Equal(1, CreatedSessionCount(pool));
                await cts.CancelAsync();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquire);
                AssertNoReservedCapacity(pool);

                gate.Release();
                gateHeld = false;
                await using var recovered = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, recovered.Session.ConnectionNumber);
                Assert.Equal(1, pool.LiveSessionCount);
                Assert.Equal(1, CreatedSessionCount(pool));
                Assert.Equal(1, ReservedConnectionCount(pool));
            }
            finally
            {
                if (gateHeld)
                {
                    gate.Release();
                }
            }
        }

        /// <summary>
        /// Cancelling an in-flight connect retires the registered session once and returns the establishment permit.
        /// A second acquire then uses connection number 1.
        /// </summary>

        [Fact]

        public async Task Cancellation_during_connect_rolls_back_capacity_without_double_release()
        {
            var factory = new ScriptedNntpTransportFactory
            {
                BlockConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            using var gate = CreateEstablishmentGate(1);
            await using var pool = CreatePool(factory, max: 1, establishment: gate);
            using var cts = new CancellationTokenSource();
            var acquire = pool.AcquireAsync(cts.Token);
            await WaitUntilAsync(() => factory.CurrentConcurrentConnects == 1 && pool.LiveSessionCount == 1);
            await cts.CancelAsync();

            var failure = await Assert.ThrowsAsync<NntpProviderConnectException>(
                () => acquire.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(ArticleRetrievalKind.Cancelled, failure.Kind);
            AssertNoReservedCapacity(pool);

            factory.BlockConnect = null;
            EnqueueReadyServers(factory, count: 1);
            await using var recovered = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, recovered.Session.ConnectionNumber);
            Assert.Equal(1, pool.LiveSessionCount);
            Assert.Equal(1, CreatedSessionCount(pool));
            Assert.Equal(1, ReservedConnectionCount(pool));
        }

        /// <summary>
        /// Disposing the pool while connect is blocked rolls the partial registration back once.
        /// A second retire from dispose does not drive the created count negative or free another session's number.
        /// </summary>

        [Fact]

        public async Task Dispose_during_connect_rolls_back_the_reservation_once()
        {
            var factory = new ScriptedNntpTransportFactory
            {
                BlockConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var pool = CreatePool(factory, max: 1);
            var acquire = pool.AcquireAsync(CancellationToken.None);
            await WaitUntilAsync(() => factory.CurrentConcurrentConnects == 1 && pool.LiveSessionCount == 1);
            Assert.Equal(1, CreatedSessionCount(pool));

            var disposing = pool.DisposeAsync().AsTask();
            var acquireFinished = await Task.WhenAny(acquire, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(acquire, acquireFinished);
            await Assert.ThrowsAnyAsync<Exception>(() => acquire);

            var disposeFinished = await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(disposing, disposeFinished);
            await disposing;

            AssertNoReservedCapacity(pool);
        }

        /// <summary>Enqueues scripted NNTP servers that accept AUTHINFO and otherwise return 430 missing.</summary>
        /// <param name="factory">Transport factory receiving ready server instances.</param>
        /// <param name="count">Number of identical servers to enqueue.</param>
        internal static void EnqueueReadyServers(ScriptedNntpTransportFactory factory, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var server = new ScriptedNntpServer();
                server.Respond(static command =>
                    command.StartsWith("AUTHINFO", StringComparison.OrdinalIgnoreCase)
                        ? "281 authentication accepted\r\n"
                        : "430 missing\r\n");
                factory.Enqueue(server);
            }
        }

        /// <summary>
        /// Replenish connect fault is logged; pool can drop to zero active sessions without crashing.
        /// </summary>

        [Fact]

        public async Task Unexpected_replenish_failure_is_logged_and_does_not_fault_the_unobserved_fill()
        {
            var logger = new CollectingLogger<NntpSessionPool>();
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 1);
            await using var pool = CreatePool(factory, max: 1, logger: logger);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            factory.ConnectException = new NotSupportedException("observed replenish fault");
            await using (var lease = await pool.AcquireAsync(CancellationToken.None))
            {
                lease.Retire();
            }

            await WaitUntilAsync(() => logger.Messages.Any(static message =>
                message.Contains("NNTP session replenish failed", StringComparison.Ordinal)
                && message.Contains("Giganews", StringComparison.Ordinal)
                && message.Contains("observed replenish fault", StringComparison.Ordinal)));
            Assert.Equal(0, pool.ActiveSessionCount);
        }

        /// <summary>
        /// Shutdown during gated establishment logs cancel distinct from replenish failure.
        /// </summary>

        [Fact]

        public async Task Shutdown_during_observed_fill_is_logged_separately_from_replenishment_failure()
        {
            var logger = new CollectingLogger<NntpSessionPool>();
            using var gate = CreateEstablishmentGate(1);
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 2);
            await using var pool = CreatePool(factory, max: 1, establishment: gate, logger: logger);
            var gateHeld = false;
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                await gate.WaitAsync(CancellationToken.None);
                gateHeld = true;

                await using (var lease = await pool.AcquireAsync(CancellationToken.None))
                {
                    lease.Retire();
                }

                await WaitUntilAsync(() => pool.LiveSessionCount == 1);
                var shutdownField = typeof(NntpSessionPool).GetField(
                    "_shutdown",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var shutdown = Assert.IsType<CancellationTokenSource>(shutdownField?.GetValue(pool));
                await shutdown.CancelAsync();
                await WaitUntilAsync(() => logger.Messages.Any(static message =>
                    message.Contains(
                        "Shutdown continued after observed session fill was canceled",
                        StringComparison.Ordinal)));
                Assert.DoesNotContain(
                    logger.Messages,
                    static message => message.Contains("NNTP session replenish failed", StringComparison.Ordinal));
            }
            finally
            {
                if (gateHeld)
                {
                    gate.Release();
                }
            }
        }

        /// <summary>Creates a session-establishment gate capped at <paramref name="maxConcurrent"/> parallel connects.</summary>
        /// <param name="maxConcurrent">Maximum simultaneous provider session establishments.</param>
        internal static ProviderSessionEstablishmentGate CreateEstablishmentGate(int maxConcurrent = 1024) =>
            new(maxConcurrent);

        /// <summary>Constructs an <see cref="NntpSessionPool"/> wired to a scripted factory with test timeouts and backbone label.</summary>
        /// <param name="factory">Scripted transport factory supplying NNTP connections.</param>
        /// <param name="max">Maximum concurrent sessions for the provider definition.</param>
        /// <param name="min">Minimum desired sessions.</param>
        /// <param name="keepAliveSeconds">DATE keep-alive interval; zero disables keep-alive.</param>
        /// <param name="time">Optional time provider for keep-alive timers.</param>
        /// <param name="commandTimeout">Optional per-command timeout override.</param>
        /// <param name="establishment">Optional establishment gate; defaults to a high concurrency gate.</param>
        /// <param name="backbone">Provider backbone name used in logs.</param>
        /// <param name="logger">Logger instance; defaults to null logger.</param>
        /// <summary>Binds the same scripted provider with a new <paramref name="max"/>.</summary>
        /// <param name="pool">Pool created by <see cref="CreatePool"/> for Giganews on 127.0.0.1:119.</param>
        /// <param name="max">New session bound. Must be at least 1.</param>
        private static void BindMax(NntpSessionPool pool, int max) =>
            pool.BindProvider(new BackFillerProviderDefinition(
                "Giganews",
                "127.0.0.1",
                119,
                false,
                null,
                null,
                0,
                max));

        /// <summary>Disposes a finished acquire and ignores cancellation or disposal of one that did not issue.</summary>
        /// <param name="acquire">Acquire started by the test, or null when it was never started.</param>
        private static async Task SettleAcquireAsync(Task<NntpSessionLease>? acquire)
        {
            if (acquire is null)
            {
                return;
            }

            try
            {
                var lease = await acquire.WaitAsync(TimeSpan.FromSeconds(2));
                await lease.DisposeAsync();
            }
            catch (OperationCanceledException ex)
            {
                // Cleanup cancelled the acquire before it issued a lease.
                ObserveExpectedCleanup(ex);
            }
            catch (TimeoutException ex)
            {
                // The acquire did not finish within the cleanup bound.
                ObserveExpectedCleanup(ex);
            }
            catch (ObjectDisposedException ex)
            {
                // The pool was disposed while this acquire was still settling.
                ObserveExpectedCleanup(ex);
            }
        }

        /// <summary>Asserts that DATE keepalive cancellation was logged once.</summary>
        /// <param name="logger">Logger attached to the pool under test.</param>
        private static void AssertCancellationLoggedOnce(CollectingLogger<NntpSessionPool> logger)
        {
            Assert.Equal(
                1,
                logger.Messages.Count(static message => message.Contains(
                    "Shutdown continued after DATE keepalive was canceled",
                    StringComparison.Ordinal)));
        }

        /// <summary>Records an exception that test teardown expects and does not rethrow.</summary>
        /// <param name="exception">Cancellation, timeout, or disposal observed while joining a test acquire.</param>
        private static void ObserveExpectedCleanup(Exception exception)
        {
            System.Diagnostics.Trace.TraceInformation(
                "Test cleanup ignored {0}: {1}",
                exception.GetType().Name,
                exception.Message);
        }

        internal static NntpSessionPool CreatePool(
            ScriptedNntpTransportFactory factory,
            int max,
            int min = 0,
            byte keepAliveSeconds = 0,
            TimeProvider? time = null,
            TimeSpan? commandTimeout = null,
            ProviderSessionEstablishmentGate? establishment = null,
            string backbone = "Giganews",
            ILogger? logger = null,
            TimeSpan? shutdownGrace = null)
        {
            return new NntpSessionPool(
                new BackFillerProviderDefinition(
                    backbone,
                    "127.0.0.1",
                    119,
                    false,
                    null,
                    null,
                    min,
                    max,
                    keepAliveSeconds),
                NntpSessionOptions.Default with
                {
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    CommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                },
                factory,
                logger ?? NullLogger.Instance,
                establishment ?? CreateEstablishmentGate(),
                shutdownGrace: shutdownGrace ?? TimeSpan.FromSeconds(2),
                timeProvider: time);
        }

        /// <summary>Scripted server answering DATE, AUTHINFO, and ARTICLE with optional synchronization gates.</summary>
        /// <param name="dateResponse">Raw DATE command response line including CRLF.</param>
        /// <param name="dateStarted">Optional signal when a DATE command is received.</param>
        /// <param name="blockDate">Optional gate that blocks DATE handling until released.</param>
        /// <param name="articleStarted">Optional signal when an ARTICLE command is received.</param>
        /// <param name="blockArticle">Optional gate that blocks ARTICLE handling until released.</param>
        internal static ScriptedNntpServer CreateDateAwareServer(
            string dateResponse = "111 20260926120000\r\n",
            TaskCompletionSource? dateStarted = null,
            TaskCompletionSource? blockDate = null,
            TaskCompletionSource? articleStarted = null,
            TaskCompletionSource? blockArticle = null)
        {
            var server = new ScriptedNntpServer
            {
                DateStarted = dateStarted,
                BlockDate = blockDate,
                ArticleStarted = articleStarted,
                BlockArticle = blockArticle,
            };
            server.Respond(command =>
            {
                if (command.Equals("DATE", StringComparison.OrdinalIgnoreCase))
                {
                    return dateResponse;
                }

                if (command.StartsWith("AUTHINFO", StringComparison.OrdinalIgnoreCase))
                {
                    return "281 authentication accepted\r\n";
                }

                if (command.StartsWith("ARTICLE", StringComparison.OrdinalIgnoreCase))
                {
                    return "220 follows\r\nFrom: a@b\r\n\r\nbody\r\n.\r\n";
                }

                return "430 missing\r\n";
            });
            return server;
        }

        /// <summary>
        /// Pool dispose cancels a published DATE loop that has not yet reached its delay.
        /// Publication resumes that loop asynchronously, so shutdown can win the race and the loop must still log the cancellation.
        /// </summary>
        [Fact]
        public async Task Keepalive_cancellation_is_logged_and_disposal_completes()
        {
            var logger = new CollectingLogger<NntpSessionPool>();
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 1);
            await using var pool = CreatePool(
                factory,
                max: 1,
                keepAliveSeconds: 30,
                time: new IdleTimeProvider(),
                logger: logger);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            await pool.DisposeAsync();

            AssertCancellationLoggedOnce(logger);
            Assert.Equal(0, pool.ActiveKeepAliveCount);
            Assert.Equal(0, pool.LiveSessionCount);
        }

        /// <summary>
        /// Disposal while the DATE loop is awaiting its armed delay cancels that wait and logs once.
        /// The loop has passed publication before disposal, so the cancellation arrives as an exception from the delay.
        /// </summary>
        [Fact]
        public async Task Keepalive_delay_cancellation_is_logged_once_and_disposal_completes()
        {
            var logger = new CollectingLogger<NntpSessionPool>();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 1);
            var pool = CreatePool(
                factory,
                max: 1,
                keepAliveSeconds: 30,
                time: new KeepAliveDelaySource(),
                logger: logger);
            pool.BeforeFirstKeepAliveDelay = () =>
            {
                // The test continuation is queued, then this method returns a completed task.
                // The loop therefore reaches its delay await before the test is allowed to dispose.
                entered.TrySetResult();
                return Task.CompletedTask;
            };
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

                await pool.DisposeAsync();

                AssertCancellationLoggedOnce(logger);
                Assert.Equal(0, pool.ActiveKeepAliveCount);
                Assert.Equal(0, pool.LiveSessionCount);
                Assert.Equal(0, pool.ActiveLeaseCount);
            }
            finally
            {
                pool.BeforeFirstKeepAliveDelay = null;
                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// DATE keep-alive fault retires the session and is logged.
        /// </summary>

        [Fact]

        public async Task Unexpected_keepalive_failure_retires_the_session()
        {
            var logger = new CollectingLogger<NntpSessionPool>();
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: 2);
            await using var pool = CreatePool(
                factory,
                max: 1,
                keepAliveSeconds: 30,
                time: new OnceFailingTimeProvider(),
                logger: logger);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            await WaitUntilAsync(() => logger.Messages.Any(static message =>
                message.Contains("NNTP session retired", StringComparison.Ordinal)
                && message.Contains("DATE keepalive failed", StringComparison.Ordinal)));
            Assert.Equal(0, pool.ActiveLeaseCount);
        }

        /// <summary>
        /// Repeated DATE failures replace the idle session without an acquire.
        /// Each replacement is held in its DATE delay before the counts are read, so retirement cannot run between the two observations.
        /// </summary>
        [Fact]
        public async Task Keepalive_retirement_without_acquire_does_not_accumulate_idle_sessions()
        {
            const int replacements = 8;
            var logger = new CollectingLogger<NntpSessionPool>();
            var clock = new KeepAliveDelaySource();
            var factory = new ScriptedNntpTransportFactory();
            EnqueueReadyServers(factory, count: replacements + 1);
            await using var pool = CreatePool(
                factory,
                max: 1,
                keepAliveSeconds: 30,
                time: clock,
                logger: logger);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
            Assert.Equal(1, pool.IdleSessionCount);
            Assert.Equal(0, pool.ActiveLeaseCount);

            var release = await clock.WaitForKeepAliveAsync();
            for (var attempt = 0; attempt < replacements; attempt++)
            {
                release();
                var next = await clock.WaitForKeepAliveAsync();
                var expectedConnects = attempt + 2;
                Assert.True(pool.IdleSessionCount <= pool.LiveSessionCount);
                Assert.Equal(1, pool.IdleSessionCount);
                Assert.Equal(1, pool.LiveSessionCount);
                Assert.Equal(1, pool.ActiveSessionCount);
                Assert.Equal(0, pool.ActiveLeaseCount);
                Assert.Equal(expectedConnects, factory.ConnectAttempts.Count);
                Assert.Equal(1, CreatedSessionCount(pool));
                Assert.Equal(1, ReservedConnectionCount(pool));
                release = next;
            }

            Assert.Equal(replacements, logger.Messages.Count(static message =>
                message.Contains("reason=DATE keepalive failed", StringComparison.Ordinal)));
            Assert.Equal(1, pool.IdleSessionCount);
            Assert.Equal(1, pool.LiveSessionCount);
            Assert.True(pool.IdleSessionCount <= pool.LiveSessionCount);
        }

        /// <summary>
        /// A DATE helper that already observed a taken session, and the loop it superseded, cannot cancel or remove the keepalive installed when that session is idle again.
        /// The replacement count is read only after the superseded loop task has finished its own registration removal.
        /// </summary>
        [Fact]
        public async Task Superseded_keepalive_cannot_cancel_the_later_idle_registration()
        {
            var dateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(CreateDateAwareServer(dateStarted: dateStarted));
            var clock = new KeepAliveDelaySource();
            await using var pool = CreatePool(factory, max: 2, keepAliveSeconds: 30, time: clock);
            var warmup = await pool.AcquireAsync(CancellationToken.None);
            await warmup.DisposeAsync();
            _ = await clock.WaitForKeepAliveAsync();

            var firstLoopExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var supersededLoopExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSupersededLoop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var loopExits = 0;
            var publishCalls = 0;
            var stopCalls = 0;
            Task? supersededLoop = null;
            pool.KeepAliveLoopExitGate = loopTask =>
            {
                var call = Interlocked.Increment(ref loopExits);
                if (call == 1)
                {
                    firstLoopExited.TrySetResult();
                    return Task.CompletedTask;
                }

                if (call == 2)
                {
                    supersededLoop = loopTask;
                    supersededLoopExited.TrySetResult();
                    return releaseSupersededLoop.Task;
                }

                return Task.CompletedTask;
            };
            pool.AfterKeepAlivePublished = () =>
            {
                if (Interlocked.Increment(ref publishCalls) == 1)
                {
                    published.TrySetResult();
                    return releasePublished.Task;
                }

                return Task.CompletedTask;
            };
            pool.KeepAliveStopGate = () =>
            {
                if (Interlocked.Increment(ref stopCalls) == 1)
                {
                    stopEntered.TrySetResult();
                    return releaseStop.Task;
                }

                return Task.CompletedTask;
            };

            Task? returning = null;
            NntpSessionLease? second = null;
            var first = await pool.AcquireAsync(CancellationToken.None);
            try
            {
                await firstLoopExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
                returning = first.DisposeAsync().AsTask();
                await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
                second = await pool.AcquireAsync(CancellationToken.None);
                await supersededLoopExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
                releasePublished.TrySetResult();
                await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

                await second.DisposeAsync();
                Assert.Equal(1, pool.ActiveKeepAliveCount);
                Assert.NotNull(supersededLoop);
                releaseSupersededLoop.TrySetResult();
                await supersededLoop.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, pool.ActiveKeepAliveCount);
                releaseStop.TrySetResult();
                await returning.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, pool.ActiveKeepAliveCount);
                Assert.Equal(1, pool.IdleSessionCount);
                Assert.Equal(1, pool.LiveSessionCount);
                Assert.Equal(0, pool.ActiveLeaseCount);

                _ = await clock.WaitForKeepAliveAsync();
                var currentTimer = await clock.WaitForKeepAliveAsync();
                currentTimer();
                await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, pool.IdleSessionCount);
                Assert.Equal(1, pool.ActiveKeepAliveCount);
            }
            finally
            {
                releasePublished.TrySetResult();
                releaseSupersededLoop.TrySetResult();
                releaseStop.TrySetResult();
                if (supersededLoop is not null)
                {
                    await supersededLoop.WaitAsync(TimeSpan.FromSeconds(5));
                }

                pool.AfterKeepAlivePublished = null;
                pool.KeepAliveStopGate = null;
                pool.KeepAliveLoopExitGate = null;
                if (returning is not null)
                {
                    await returning.WaitAsync(TimeSpan.FromSeconds(5));
                }

                if (second is not null)
                {
                    await second.DisposeAsync();
                }

                await first.DisposeAsync();
            }
        }

        /// <summary>
        /// A DATE loop that decided retirement was needed cannot retire the session after a newer keepalive was published.
        /// The session stays live, idle, and reusable, and the replacement registration still sends DATE.
        /// </summary>
        [Fact]
        public async Task Superseded_keepalive_cannot_retire_a_session_returned_to_idle()
        {
            var dateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var articleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockArticle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(CreateDateAwareServer(
                dateStarted: dateStarted,
                articleStarted: articleStarted,
                blockArticle: blockArticle));
            var clock = new KeepAliveDelaySource();
            await using var pool = CreatePool(factory, max: 1, keepAliveSeconds: 30, time: clock);
            var warmup = await pool.AcquireAsync(CancellationToken.None);
            await warmup.DisposeAsync();
            var firstDelay = await clock.WaitForKeepAliveAsync();

            var healthEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseHealth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retirementEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRetirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var healthCalls = 0;
            var retirementCalls = 0;
            Task? superseded = null;
            NntpSessionLease? lease = null;
            Task<ArticleRetrievalResult>? download = null;
            pool.BeforeKeepAliveReusabilityCheck = () =>
            {
                if (Interlocked.Increment(ref healthCalls) == 1)
                {
                    healthEntered.TrySetResult();
                    return releaseHealth.Task;
                }

                return Task.CompletedTask;
            };
            pool.BeforeKeepAliveRetirement = loop =>
            {
                if (Interlocked.Increment(ref retirementCalls) == 1)
                {
                    superseded = loop;
                    retirementEntered.TrySetResult();
                    return releaseRetirement.Task;
                }

                return Task.CompletedTask;
            };
            try
            {
                firstDelay();
                await healthEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                lease = await pool.AcquireAsync(CancellationToken.None);
                var session = lease.Session;
                download = session.DownloadArticleAsync("<id@example>", CancellationToken.None);
                await articleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                releaseHealth.TrySetResult();
                await retirementEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, retirementCalls);
                blockArticle.TrySetResult();
                using (var article = await download.WaitAsync(TimeSpan.FromSeconds(5)))
                {
                    Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, article.Kind);
                    Assert.True(article.SessionReusable);
                }

                await lease.DisposeAsync();
                var replacement = await clock.WaitForKeepAliveAsync();
                Assert.Equal(1, pool.ActiveKeepAliveCount);
                Assert.Equal(1, pool.LiveSessionCount);
                Assert.Equal(1, pool.IdleSessionCount);
                Assert.Equal(0, pool.ActiveLeaseCount);
                Assert.Equal(1, CreatedSessionCount(pool));
                releaseRetirement.TrySetResult();
                Assert.NotNull(superseded);
                await superseded.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, pool.ActiveKeepAliveCount);
                Assert.Equal(1, pool.LiveSessionCount);
                Assert.Equal(1, pool.IdleSessionCount);
                Assert.Equal(0, pool.ActiveLeaseCount);
                Assert.Equal(1, CreatedSessionCount(pool));
                Assert.True(session.IsReusable);
                replacement();
                await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                _ = await clock.WaitForKeepAliveAsync();
                Assert.Equal(1, pool.ActiveKeepAliveCount);
                Assert.Equal(1, pool.LiveSessionCount);
                Assert.Equal(1, pool.IdleSessionCount);
                Assert.True(session.IsReusable);
            }
            finally
            {
                pool.BeforeKeepAliveReusabilityCheck = null;
                pool.BeforeKeepAliveRetirement = null;
                releaseHealth.TrySetResult();
                releaseRetirement.TrySetResult();
                blockArticle.TrySetResult();
                if (download is not null)
                {
                    try
                    {
                        using var leftover = await download.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex) when (ex is not (ArgumentNullException or NullReferenceException))
                    {
                        _ = ex.GetType();
                    }
                }

                if (lease is not null)
                {
                    await lease.DisposeAsync();
                }

                if (superseded is not null)
                {
                    try
                    {
                        await superseded.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex) when (ex is not (ArgumentNullException or NullReferenceException))
                    {
                        _ = ex.GetType();
                    }
                }
            }
        }

        /// <summary>
        /// Idle membership and its DATE registration are published together, so acquisition cannot see an idle session without that registration.
        /// Taking the session clears both, and the next return installs a new pair. The transport stays open.
        /// </summary>
        [Fact]
        public async Task Idle_publication_installs_keepalive_before_acquisition_can_observe_it()
        {
            var server = CreateDateAwareServer();
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(server);
            var clock = new KeepAliveDelaySource();
            await using var pool = CreatePool(factory, max: 1, keepAliveSeconds: 30, time: clock);
            int? idleAtPublish = null;
            int? keepalivesAtPublish = null;
            pool.IdleKeepAliveLinearized = (idleCount, keepaliveCount) =>
            {
                idleAtPublish = idleCount;
                keepalivesAtPublish = keepaliveCount;
            };
            NntpSessionLease? lease = null;
            NntpSessionLease? taken = null;
            try
            {
                lease = await pool.AcquireAsync(CancellationToken.None);
                await lease.DisposeAsync();
                lease = null;
                Assert.Equal(1, idleAtPublish);
                Assert.Equal(1, keepalivesAtPublish);
                Assert.Equal(1, pool.IdleSessionCount);
                Assert.Equal(1, pool.ActiveKeepAliveCount);
                Assert.Equal(1, pool.LiveSessionCount);
                Assert.Equal(0, pool.ActiveLeaseCount);

                taken = await pool.AcquireAsync(CancellationToken.None);
                Assert.Equal(0, pool.IdleSessionCount);
                Assert.Equal(0, pool.ActiveKeepAliveCount);
                Assert.Equal(1, pool.ActiveLeaseCount);
                Assert.Equal(1, pool.LiveSessionCount);
                Assert.True(taken.Session.IsReusable);
                await taken.DisposeAsync();
                taken = null;
                Assert.Equal(1, idleAtPublish);
                Assert.Equal(1, keepalivesAtPublish);
                Assert.Equal(1, pool.IdleSessionCount);
                Assert.Equal(1, pool.ActiveKeepAliveCount);
                Assert.Equal(1, pool.LiveSessionCount);
                Assert.Equal(0, pool.ActiveLeaseCount);
                Assert.Equal(0, server.ClientStreamDisposeCount);
                Assert.Equal(0, server.ClientStreamDisposeAsyncCount);
            }
            finally
            {
                pool.IdleKeepAliveLinearized = null;
                if (taken is not null)
                {
                    await taken.DisposeAsync();
                }

                if (lease is not null)
                {
                    await lease.DisposeAsync();
                }
            }
        }

        /// <summary>
        /// Pool disposal waits for a superseded DATE loop that is no longer the published registration.
        /// The transport is closed once, after that loop is released, and the late exit does not close it again.
        /// </summary>
        [Fact]
        public async Task Dispose_waits_for_superseded_keepalive_and_closes_the_transport_once()
        {
            var server = CreateDateAwareServer();
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(server);
            var clock = new KeepAliveDelaySource();
            var pool = CreatePool(factory, max: 1, keepAliveSeconds: 30, time: clock);
            var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var drainEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? oldLoop = null;
            IReadOnlyList<Task>? drained = null;
            var exits = 0;
            pool.KeepAliveLoopExitGate = loop =>
            {
                if (Interlocked.Increment(ref exits) == 1)
                {
                    oldLoop = loop;
                    oldExited.TrySetResult();
                    return releaseOld.Task;
                }

                return Task.CompletedTask;
            };
            pool.KeepAliveDrainSnapshot = tasks =>
            {
                drained = tasks;
                drainEntered.TrySetResult();
                return releaseDrain.Task;
            };
            try
            {
                var lease = await pool.AcquireAsync(CancellationToken.None);
                await lease.DisposeAsync();
                _ = await clock.WaitForKeepAliveAsync();
                lease = await pool.AcquireAsync(CancellationToken.None);
                var session = lease.Session;
                await oldExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await lease.DisposeAsync();
                _ = await clock.WaitForKeepAliveAsync();
                Assert.Equal(1, pool.ActiveKeepAliveCount);
                Assert.Equal(1, pool.IdleSessionCount);
                Assert.Equal(1, pool.LiveSessionCount);
                Assert.Equal(0, pool.ActiveLeaseCount);
                Assert.True(session.IsReusable);

                var dispose = pool.DisposeAsync().AsTask();
                await drainEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(dispose.IsCompleted);
                Assert.NotNull(oldLoop);
                Assert.NotNull(drained);
                Assert.False(oldLoop.IsCompleted);
                Assert.Contains(oldLoop, drained);
                Assert.Equal(0, server.ClientStreamDisposeCount);
                Assert.Equal(0, server.ClientStreamDisposeAsyncCount);
                Assert.Equal(1, pool.LiveSessionCount);

                releaseOld.TrySetResult();
                await oldLoop.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(dispose.IsCompleted);
                releaseDrain.TrySetResult();
                await dispose.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, pool.LiveSessionCount);
                Assert.Equal(0, pool.IdleSessionCount);
                Assert.Equal(0, pool.ActiveKeepAliveCount);
                Assert.Equal(0, pool.ActiveLeaseCount);
                Assert.Equal(1, server.ClientStreamDisposeCount);
                Assert.Equal(1, server.ClientStreamDisposeAsyncCount);
            }
            finally
            {
                releaseOld.TrySetResult();
                releaseDrain.TrySetResult();
                pool.KeepAliveLoopExitGate = null;
                pool.KeepAliveDrainSnapshot = null;
                if (oldLoop is not null)
                {
                    await oldLoop.WaitAsync(TimeSpan.FromSeconds(5));
                }

                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// A keepalive that already passed cancellation still sends DATE on the shutdown token after its registration is detached.
        /// ARTICLE that passed its first check and waited for that DATE must not write after DATE marks the session unhealthy.
        /// </summary>
        [Fact]
        public async Task Article_after_failed_date_does_not_write_on_the_unhealthy_session()
        {
            var healthEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseHealth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dateFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRetirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var articleAtGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseArticle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writeFlushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = NntpSessionPoolTests.CreateDateAwareServer(dateResponse: "503 keepalive failed\r\n");
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(server);
            var clock = new KeepAliveDelaySource();
            await using var pool = CreatePool(factory, max: 1, keepAliveSeconds: 30, time: clock);
            var warmup = await pool.AcquireAsync(CancellationToken.None);
            var session = warmup.Session;
            await warmup.DisposeAsync();
            var firstDelay = await clock.WaitForKeepAliveAsync();
            pool.BeforeKeepAliveReusabilityCheck = () =>
            {
                healthEntered.TrySetResult();
                return releaseHealth.Task;
            };
            pool.BeforeKeepAliveRetirement = _ =>
            {
                dateFailed.TrySetResult();
                return releaseRetirement.Task;
            };
            session.BeforeArticleBusyWait = () =>
            {
                articleAtGate.TrySetResult();
                return releaseArticle.Task;
            };
            NntpSessionLease? lease = null;
            Task<ArticleRetrievalResult>? download = null;
            try
            {
                firstDelay();
                await healthEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                lease = await pool.AcquireAsync(CancellationToken.None);
                Assert.Same(session, lease.Session);
                server.HoldAfterCommandWrite = releaseWrite;
                server.CommandWriteFlushed = writeFlushed;
                releaseHealth.TrySetResult();
                await writeFlushed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(NntpSessionState.Busy, session.State);
                download = session.DownloadArticleAsync("<id@example>", CancellationToken.None);
                await articleAtGate.Task.WaitAsync(TimeSpan.FromSeconds(5));
                releaseWrite.TrySetResult();
                await dateFailed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(session.IsReusable);
                Assert.Equal(NntpSessionState.Retiring, session.State);
                releaseArticle.TrySetResult();
                using var article = await download.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, article.Kind);
                Assert.False(article.SessionReusable);
                Assert.Null(article.Article);
                Assert.Equal("NNTP session is not ready.", article.Reason);
                Assert.Equal(0, session.ArticleCommandStartedTimestamp);
                Assert.Contains(server.Commands, static command => command.Equals("DATE", StringComparison.Ordinal));
                Assert.DoesNotContain(server.Commands, static command => command.StartsWith("ARTICLE", StringComparison.Ordinal));
                Assert.Equal(NntpSessionState.Retiring, session.State);
                Assert.NotNull(SessionField(session, "_stream"));
            }
            finally
            {
                pool.BeforeKeepAliveReusabilityCheck = null;
                pool.BeforeKeepAliveRetirement = null;
                session.BeforeArticleBusyWait = null;
                releaseHealth.TrySetResult();
                releaseWrite.TrySetResult();
                releaseArticle.TrySetResult();
                releaseRetirement.TrySetResult();
                if (download is not null)
                {
                    try
                    {
                        using var leftover = await download.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex) when (ex is not (ArgumentNullException or NullReferenceException))
                    {
                        _ = ex.GetType();
                    }
                }

                if (lease is not null)
                {
                    await lease.DisposeAsync();
                }
            }
        }

        /// <summary>
        /// Pool disposal that retires a session after connect and before issuance does not hand that session out.
        /// Lease, created-session, and connection-number accounting stay at zero, and releasing the fill gate does not throw.
        /// </summary>
        [Fact]
        public async Task Disposal_before_issue_does_not_lease_the_retired_created_session()
        {
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(ReadyServer());
            await using var pool = CreatePool(factory, max: 1);
            var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseIssue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pool.BeforeCreatedSessionIssue = () =>
            {
                created.TrySetResult();
                return releaseIssue.Task;
            };
            var acquire = pool.AcquireAsync(CancellationToken.None);
            try
            {
                await created.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await pool.DisposeAsync();
                releaseIssue.TrySetResult();
                var thrown = await Assert.ThrowsAnyAsync<Exception>(() => acquire);
                Assert.IsNotType<NullReferenceException>(thrown);
                Assert.IsType<ObjectDisposedException>(thrown);
                Assert.Equal(0, pool.ActiveLeaseCount);
                Assert.Equal(0, pool.IdleSessionCount);
                Assert.Equal(0, pool.LiveSessionCount);
                Assert.Equal(0, CreatedSessionCount(pool));
                Assert.Equal(0, ReservedConnectionCount(pool));
            }
            finally
            {
                pool.BeforeCreatedSessionIssue = null;
                releaseIssue.TrySetResult();
                try
                {
                    await acquire.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
                {
                    _ = ex.GetType();
                }
            }
        }

        /// <summary>
        /// A keepalive retirement that has left the idle list, but not yet finished dispose, cannot be leased or returned to the queue.
        /// The pause yields, so the caller that released DATE can observe that window and then let dispose finish.
        /// The replacement lease stays usable after the retired session is disposed, and its return does not requeue the retired one.
        /// </summary>
        [Fact]
        public async Task Retirement_overlapping_acquire_does_not_lease_or_requeue_the_retired_session()
        {
            var clock = new KeepAliveDelaySource();
            var factory = new ScriptedNntpTransportFactory();
            var retiredServer = ReadyServer();
            var survivorServer = ReadyServer();
            factory.Enqueue(retiredServer);
            factory.Enqueue(survivorServer);
            await using var pool = CreatePool(factory, max: 1, keepAliveSeconds: 30, time: clock);
            var retirementEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRetirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pool.BeforeDetachedSessionDispose = () =>
            {
                retirementEntered.TrySetResult();
                return releaseRetirement.Task;
            };

            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                Assert.Equal(1, pool.IdleSessionCount);

                var releaseKeepAlive = await clock.WaitForKeepAliveAsync();
                releaseKeepAlive();
                await retirementEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, pool.IdleSessionCount);
                Assert.Equal(0, pool.LiveSessionCount);
                Assert.Equal(0, pool.ActiveLeaseCount);
                Assert.Equal(0, CreatedSessionCount(pool));
                Assert.Equal(0, ReservedConnectionCount(pool));
                Assert.Equal(0, retiredServer.ClientStreamDisposeCount);

                var lease = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                var survivor = lease.Session;
                Assert.Equal(NntpSessionState.Ready, survivor.State);
                Assert.Equal(0, pool.IdleSessionCount);
                Assert.Equal(1, pool.LiveSessionCount);
                Assert.Equal(1, pool.ActiveLeaseCount);
                Assert.Equal(1, CreatedSessionCount(pool));
                Assert.Equal(1, ReservedConnectionCount(pool));
                Assert.Equal(2, factory.ConnectAttempts.Count);

                await lease.DisposeAsync();
                Assert.Equal(1, pool.IdleSessionCount);
                Assert.Equal(1, pool.LiveSessionCount);
                Assert.Equal(0, pool.ActiveLeaseCount);
                Assert.Equal(NntpSessionState.Ready, survivor.State);
                Assert.Equal(2, factory.ConnectAttempts.Count);

                releaseRetirement.TrySetResult();
                await WaitUntilAsync(() => retiredServer.ClientStreamDisposeCount == 1);
                Assert.Equal(NntpSessionState.Ready, survivor.State);
                Assert.Equal(0, survivorServer.ClientStreamDisposeCount);
                Assert.Equal(1, pool.IdleSessionCount);
                Assert.Equal(1, pool.LiveSessionCount);
                Assert.Equal(1, CreatedSessionCount(pool));
                Assert.Equal(1, ReservedConnectionCount(pool));

                var reused = await pool.AcquireAsync(CancellationToken.None);
                Assert.Same(survivor, reused.Session);
                Assert.Equal(NntpSessionState.Ready, reused.Session.State);
                Assert.Equal(2, factory.ConnectAttempts.Count);
                await reused.DisposeAsync();
            }
            finally
            {
                releaseRetirement.TrySetResult();
                pool.BeforeDetachedSessionDispose = null;
            }
        }

        /// <summary>
        /// Cancelling an acquire while the taken session is inside DATE retires that session.
        /// The DATE loop will not retire it, because it has already left the idle list.
        /// </summary>
        [Fact]
        public async Task Cancelled_acquire_during_date_retires_the_taken_session()
        {
            var clock = new KeepAliveDelaySource();
            var factory = new ScriptedNntpTransportFactory();
            var dateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockDate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = CreateDateAwareServer(dateStarted: dateStarted, blockDate: blockDate);
            factory.Enqueue(server);
            factory.Enqueue(ReadyServer());
            await using var pool = CreatePool(factory, max: 1, keepAliveSeconds: 30, time: clock);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            var releaseKeepAlive = await clock.WaitForKeepAliveAsync();
            releaseKeepAlive();
            await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using var acquireCancel = new CancellationTokenSource();
            var acquire = pool.AcquireAsync(acquireCancel.Token);
            await WaitUntilAsync(() =>
                pool.IdleSessionCount == 0
                && pool.ActiveLeaseCount == 0
                && pool.LiveSessionCount == 1);

            acquireCancel.Cancel();
            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await acquire)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(blockDate.Task.IsCompleted);
            }
            finally
            {
                blockDate.TrySetResult();
            }

            await WaitUntilAsync(() => server.ClientStreamDisposeCount == 1);
            await WaitUntilAsync(() =>
                pool.IdleSessionCount == 1
                && pool.LiveSessionCount == 1
                && pool.ActiveLeaseCount == 0
                && factory.ConnectAttempts.Count == 2
                && CreatedSessionCount(pool) == 1
                && ReservedConnectionCount(pool) == 1);
        }

        /// <summary>
        /// An acquire that takes a session during DATE waits for that command's response before ARTICLE is written.
        /// A task that is already complete does not release the wait.
        /// </summary>
        [Fact]
        public async Task Acquire_waits_for_the_date_response_before_article()
        {
            var clock = new KeepAliveDelaySource();
            var dateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockDate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var articleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new ScriptedNntpTransportFactory();
            var server = CreateDateAwareServer(
                dateStarted: dateStarted,
                blockDate: blockDate,
                articleStarted: articleStarted);
            factory.Enqueue(server);
            await using var pool = CreatePool(factory, max: 1, keepAliveSeconds: 30, time: clock);
            NntpSessionLease? lease = null;
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                var releaseKeepAlive = await clock.WaitForKeepAliveAsync();
                releaseKeepAlive();
                await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                var acquire = pool.AcquireAsync(CancellationToken.None);
                await WaitUntilAsync(() =>
                    pool.IdleSessionCount == 0
                    && pool.ActiveLeaseCount == 0
                    && !acquire.IsCompleted);
                await Task.CompletedTask;
                Assert.False(acquire.IsCompleted);
                Assert.False(articleStarted.Task.IsCompleted);
                Assert.DoesNotContain(
                    server.Commands,
                    static command => command.StartsWith("ARTICLE", StringComparison.OrdinalIgnoreCase));

                blockDate.TrySetResult();
                lease = await acquire.WaitAsync(TimeSpan.FromSeconds(5));
                var download = lease.Session.DownloadArticleAsync("<a@b>", CancellationToken.None);
                await articleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var articleIndex = CommandIndex(server, "ARTICLE");
                var dateIndex = CommandIndex(server, "DATE");
                Assert.True(dateIndex >= 0 && articleIndex > dateIndex);
                using var result = await download.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.NotNull(result);
            }
            finally
            {
                blockDate.TrySetResult();
                if (lease is not null)
                {
                    await lease.DisposeAsync();
                }
            }
        }

        /// <summary>
        /// DATE that closes without a response releases the command gate, and the waiting acquire does not stay parked.
        /// </summary>
        [Fact]
        public async Task Date_eof_while_acquire_waits_does_not_leave_the_acquire_pending()
        {
            var clock = new KeepAliveDelaySource();
            var dateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockDate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new ScriptedNntpTransportFactory();
            var server = CreateDateAwareServer(dateStarted: dateStarted, blockDate: blockDate);
            factory.Enqueue(server);
            factory.Enqueue(ReadyServer());
            await using var pool = CreatePool(factory, max: 1, keepAliveSeconds: 30, time: clock);
            NntpSessionLease? lease = null;
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                var releaseKeepAlive = await clock.WaitForKeepAliveAsync();
                releaseKeepAlive();
                await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var acquire = pool.AcquireAsync(CancellationToken.None);
                await WaitUntilAsync(() => pool.IdleSessionCount == 0 && pool.ActiveLeaseCount == 0 && !acquire.IsCompleted);

                server.CompleteAfterDateWithoutResponse = true;
                blockDate.TrySetResult();
                lease = await acquire.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(2, factory.ConnectAttempts.Count);
                Assert.Equal(1, server.ClientStreamDisposeCount);
                Assert.Equal(1, pool.ActiveLeaseCount);
            }
            finally
            {
                blockDate.TrySetResult();
                if (lease is not null)
                {
                    await lease.DisposeAsync();
                }
            }
        }

        /// <summary>
        /// A rejected DATE status releases the command gate, and the waiting acquire takes a replacement session.
        /// </summary>
        [Fact]
        public async Task Date_rejection_while_acquire_waits_does_not_leave_the_acquire_pending()
        {
            var clock = new KeepAliveDelaySource();
            var dateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockDate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new ScriptedNntpTransportFactory();
            var server = CreateDateAwareServer(
                dateResponse: "400 date rejected\r\n",
                dateStarted: dateStarted,
                blockDate: blockDate);
            factory.Enqueue(server);
            factory.Enqueue(ReadyServer());
            await using var pool = CreatePool(factory, max: 1, keepAliveSeconds: 30, time: clock);
            NntpSessionLease? lease = null;
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                var releaseKeepAlive = await clock.WaitForKeepAliveAsync();
                releaseKeepAlive();
                await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var acquire = pool.AcquireAsync(CancellationToken.None);
                await WaitUntilAsync(() => pool.IdleSessionCount == 0 && pool.ActiveLeaseCount == 0 && !acquire.IsCompleted);

                blockDate.TrySetResult();
                lease = await acquire.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(2, factory.ConnectAttempts.Count);
                Assert.Equal(1, server.ClientStreamDisposeCount);
                Assert.Equal(1, pool.ActiveLeaseCount);
            }
            finally
            {
                blockDate.TrySetResult();
                if (lease is not null)
                {
                    await lease.DisposeAsync();
                }
            }
        }

        /// <summary>
        /// A DATE read that hits the command timeout releases the gate, and the waiting acquire takes a replacement.
        /// </summary>
        [Fact]
        public async Task Date_timeout_while_acquire_waits_does_not_leave_the_acquire_pending()
        {
            var clock = new KeepAliveDelaySource();
            var dateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockDate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new ScriptedNntpTransportFactory();
            var server = CreateDateAwareServer(dateStarted: dateStarted, blockDate: blockDate);
            factory.Enqueue(server);
            factory.Enqueue(ReadyServer());
            await using var pool = CreatePool(
                factory,
                max: 1,
                keepAliveSeconds: 30,
                time: clock,
                commandTimeout: TimeSpan.FromSeconds(1));
            NntpSessionLease? lease = null;
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                var releaseKeepAlive = await clock.WaitForKeepAliveAsync();
                releaseKeepAlive();
                await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var acquire = pool.AcquireAsync(CancellationToken.None);
                await WaitUntilAsync(() => pool.IdleSessionCount == 0 && pool.ActiveLeaseCount == 0 && !acquire.IsCompleted);

                lease = await acquire.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(blockDate.Task.IsCompleted);
                Assert.Equal(2, factory.ConnectAttempts.Count);
                Assert.Equal(1, pool.ActiveLeaseCount);
            }
            finally
            {
                blockDate.TrySetResult();
                if (lease is not null)
                {
                    await lease.DisposeAsync();
                }
            }
        }

        /// <summary>
        /// Pool disposal while an acquire is waiting for DATE completes that acquire and clears the pool.
        /// </summary>
        [Fact]
        public async Task Dispose_while_acquire_waits_for_date_completes_the_acquire()
        {
            var clock = new KeepAliveDelaySource();
            var dateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockDate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(CreateDateAwareServer(dateStarted: dateStarted, blockDate: blockDate));
            var pool = CreatePool(factory, max: 1, keepAliveSeconds: 30, time: clock);
            Task<NntpSessionLease>? pending = null;
            var observed = false;
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                var releaseKeepAlive = await clock.WaitForKeepAliveAsync();
                releaseKeepAlive();
                await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                pending = pool.AcquireAsync(CancellationToken.None);
                await WaitUntilAsync(() => pool.IdleSessionCount == 0 && pool.ActiveLeaseCount == 0 && !pending.IsCompleted);

                await pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                var thrown = await Assert.ThrowsAnyAsync<Exception>(async () => await pending);
                observed = true;
                Assert.IsNotType<NullReferenceException>(thrown);
                Assert.Equal(0, pool.LiveSessionCount);
                Assert.Equal(0, pool.ActiveLeaseCount);
            }
            finally
            {
                blockDate.TrySetResult();
                if (!observed && pending is not null)
                {
                    try
                    {
                        await pending.WaitAsync(TimeSpan.FromSeconds(2));
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException or TimeoutException)
                    {
                        // Disposal wins the race with the acquire that was waiting on DATE.
                        ObserveExpectedCleanup(ex);
                    }
                }

                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// Configuration replacement drains the pool through <see cref="NntpSessionPool.DrainAndDisposeAsync"/> while acquire is waiting for DATE.
        /// That acquire completes, and the pool does not keep the session leased.
        /// </summary>
        [Fact]
        public async Task Drain_while_acquire_waits_for_date_completes_the_acquire()
        {
            var clock = new KeepAliveDelaySource();
            var dateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockDate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(CreateDateAwareServer(dateStarted: dateStarted, blockDate: blockDate));
            var pool = CreatePool(factory, max: 1, keepAliveSeconds: 30, time: clock);
            Task<NntpSessionLease>? pending = null;
            var observed = false;
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                var releaseKeepAlive = await clock.WaitForKeepAliveAsync();
                releaseKeepAlive();
                await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                pending = pool.AcquireAsync(CancellationToken.None);
                await WaitUntilAsync(() => pool.IdleSessionCount == 0 && pool.ActiveLeaseCount == 0 && !pending.IsCompleted);

                await pool.DrainAndDisposeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                var thrown = await Assert.ThrowsAnyAsync<Exception>(async () => await pending);
                observed = true;
                Assert.IsNotType<NullReferenceException>(thrown);
                Assert.Equal(0, pool.LiveSessionCount);
                Assert.Equal(0, pool.ActiveLeaseCount);
            }
            finally
            {
                blockDate.TrySetResult();
                if (!observed && pending is not null)
                {
                    try
                    {
                        await pending.WaitAsync(TimeSpan.FromSeconds(2));
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException or TimeoutException)
                    {
                        // The replacement drain wins the race with the acquire that was waiting on DATE.
                        ObserveExpectedCleanup(ex);
                    }
                }

                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// The pool drain grace expires while ARTICLE is waiting for a response.
        /// Forced retirement aborts that read and does not write QUIT.
        /// </summary>
        [Fact]
        public async Task Drain_timeout_aborts_a_blocked_article_read_without_quit()
        {
            var articleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockArticle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = new ScriptedNntpServer
            {
                ArticleStarted = articleStarted,
                BlockArticle = blockArticle,
            };
            var logger = new CollectingLogger<NntpSessionPool>();
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(server);
            var pool = CreatePool(factory, max: 1, logger: logger, shutdownGrace: TimeSpan.FromMilliseconds(100));
            NntpSessionLease? lease = null;
            Task<ArticleRetrievalResult>? download = null;
            try
            {
                lease = await pool.AcquireAsync(CancellationToken.None);
                download = lease.Session.DownloadArticleAsync("<a@b>", CancellationToken.None);
                await articleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                await pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                using var result = await download.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
                Assert.Equal(NntpSessionState.Closed, lease.Session.State);
                Assert.Equal(0, pool.LiveSessionCount);
                Assert.Equal(1, server.ClientStreamDisposeCount);
                Assert.Contains(server.Commands, static command => command.StartsWith("ARTICLE", StringComparison.Ordinal));
                Assert.DoesNotContain(logger.Messages, static message => message.Contains("TX: QUIT", StringComparison.Ordinal));
                Assert.DoesNotContain(server.Commands, static command => command.StartsWith("QUIT", StringComparison.Ordinal));
            }
            finally
            {
                blockArticle.TrySetResult();
                if (download is not null)
                {
                    try
                    {
                        using var leftover = await download.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex) when (ex is not (ArgumentNullException or NullReferenceException))
                    {
                        ObserveExpectedCleanup(ex);
                    }
                }

                if (lease is not null)
                {
                    await lease.DisposeAsync();
                }

                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// Configuration replacement cancels the drain while ARTICLE is still reading.
        /// The session is aborted without QUIT, and the download does not stay pending.
        /// </summary>
        [Fact]
        public async Task Replacement_drain_aborts_a_blocked_article_read_without_quit()
        {
            var articleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockArticle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = new ScriptedNntpServer
            {
                ArticleStarted = articleStarted,
                BlockArticle = blockArticle,
            };
            var logger = new CollectingLogger<NntpSessionPool>();
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(server);
            var pool = CreatePool(factory, max: 1, logger: logger);
            NntpSessionLease? lease = null;
            Task<ArticleRetrievalResult>? download = null;
            try
            {
                lease = await pool.AcquireAsync(CancellationToken.None);
                download = lease.Session.DownloadArticleAsync("<a@b>", CancellationToken.None);
                await articleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();

                await pool.DrainAndDisposeAsync(cancelled.Token).WaitAsync(TimeSpan.FromSeconds(5));
                using var result = await download.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
                Assert.Equal(NntpSessionState.Closed, lease.Session.State);
                Assert.Equal(0, pool.LiveSessionCount);
                Assert.Equal(1, server.ClientStreamDisposeCount);
                Assert.DoesNotContain(logger.Messages, static message => message.Contains("TX: QUIT", StringComparison.Ordinal));
            }
            finally
            {
                blockArticle.TrySetResult();
                if (download is not null)
                {
                    try
                    {
                        using var leftover = await download.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex) when (ex is not (ArgumentNullException or NullReferenceException))
                    {
                        ObserveExpectedCleanup(ex);
                    }
                }

                if (lease is not null)
                {
                    await lease.DisposeAsync();
                }

                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// The pool drain grace expires while ARTICLE is blocked inside its write.
        /// Forced shutdown unblocks that write, does not overlap it with QUIT, and closes the transport once.
        /// </summary>
        [Fact]
        public async Task Drain_timeout_aborts_a_blocked_article_write_without_quit()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            AbortingWriteStream? gate = null;
            var server = new ScriptedNntpServer
            {
                ClientStreamDecorator = inner =>
                {
                    gate = new AbortingWriteStream(inner, entered);
                    return gate;
                },
            };
            var logger = new CollectingLogger<NntpSessionPool>();
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(server);
            var pool = CreatePool(factory, max: 1, logger: logger, shutdownGrace: TimeSpan.FromMilliseconds(100));
            NntpSessionLease? lease = null;
            Task<ArticleRetrievalResult>? download = null;
            try
            {
                lease = await pool.AcquireAsync(CancellationToken.None);
                Assert.NotNull(gate);
                gate.Arm();
                download = lease.Session.DownloadArticleAsync("<a@b>", CancellationToken.None);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

                await pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                using var result = await download.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
                Assert.Equal(nameof(IOException), result.Reason);
                Assert.Equal(1, gate.MaxWriteDepth);
                Assert.Equal(NntpSessionState.Closed, lease.Session.State);
                Assert.Equal(1, server.ClientStreamDisposeCount);
                Assert.DoesNotContain(logger.Messages, static message => message.Contains("TX: QUIT", StringComparison.Ordinal));
            }
            finally
            {
                if (download is not null)
                {
                    try
                    {
                        using var leftover = await download.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex) when (ex is not (ArgumentNullException or NullReferenceException))
                    {
                        ObserveExpectedCleanup(ex);
                    }
                }

                if (lease is not null)
                {
                    await lease.DisposeAsync();
                }

                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// Cancelling acquire while a TLS DATE write is still inside <see cref="Stream.WriteAsync"/> disposes that transport once.
        /// The session is busy, so disposal aborts the transport and does not write QUIT on top of that write.
        /// The original cancellation is what acquire throws.
        /// </summary>
        [Fact]
        public async Task Cancelled_acquire_during_tls_date_write_disposes_the_transport_once()
        {
            var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            NestedWriteTlsStream? tls = null;
            var server = new ScriptedNntpServer();
            server.Respond(static command =>
                command.Equals("DATE", StringComparison.OrdinalIgnoreCase)
                    ? "111 20260926120000\r\n"
                    : "430 missing\r\n");
            server.ClientStreamDecorator = inner =>
            {
                tls = new NestedWriteTlsStream(inner, writeEntered, releaseWrite);
                return tls;
            };
            var replacement = ReadyServer();
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(server);
            factory.Enqueue(replacement);
            var clock = new KeepAliveDelaySource();
            var logger = new CollectingLogger<NntpSessionPool>();
            await using var pool = CreatePool(factory, max: 1, keepAliveSeconds: 30, time: clock, logger: logger);
            var warmup = await pool.AcquireAsync(CancellationToken.None);
            var session = warmup.Session;
            await warmup.DisposeAsync();
            var releaseKeepAlive = await clock.WaitForKeepAliveAsync();
            Assert.NotNull(tls);
            tls.HoldWrites = true;
            releaseKeepAlive();
            await writeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var acquireCancel = new CancellationTokenSource();
            var acquire = pool.AcquireAsync(acquireCancel.Token);
            try
            {
                await WaitUntilAsync(() =>
                    pool.IdleSessionCount == 0
                    && pool.ActiveLeaseCount == 0
                    && pool.LiveSessionCount == 1);
                acquireCancel.Cancel();
                var thrown = await Assert.ThrowsAnyAsync<Exception>(() => acquire).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsType<OperationCanceledException>(thrown);
                Assert.Equal(NntpSessionState.Closed, session.State);
                Assert.Null(SessionField(session, "_stream"));
                Assert.Null(SessionField(session, "_reader"));
                Assert.False(session.IsReusable);
                Assert.Equal(1, tls.DisposeAsyncCount);
                Assert.Equal(1, server.ClientStreamDisposeCount);
                Assert.Equal(0, tls.OverlappingWriteCount);
                Assert.DoesNotContain(
                    logger.Entries,
                    static entry => entry.EventId.Id == 5417
                        && entry.Message.Contains(nameof(NotSupportedException), StringComparison.Ordinal));
                await WaitUntilAsync(() =>
                    pool.IdleSessionCount == 1
                    && pool.LiveSessionCount == 1
                    && pool.ActiveLeaseCount == 0
                    && factory.ConnectAttempts.Count == 2);
            }
            finally
            {
                releaseWrite.TrySetResult();
                try
                {
                    _ = await acquire.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex) when (ex is not (ArgumentNullException or NullReferenceException))
                {
                    _ = ex.GetType();
                }
            }
        }

        /// <summary>Scripted peer that accepts connect and answers DATE with a non-success status.</summary>
        /// <returns>A server whose DATE response retires a keepalive session.</returns>
        private static ScriptedNntpServer ReadyServer()
        {
            var server = new ScriptedNntpServer();
            server.Respond(static command =>
                command.StartsWith("AUTHINFO", StringComparison.OrdinalIgnoreCase)
                    ? "281 authentication accepted\r\n"
                    : "430 missing\r\n");
            return server;
        }

        /// <summary>
        /// Asserts a finished failed create left no live session, lease, created-count reservation, or connection number.
        /// </summary>
        /// <param name="pool">Pool observed after the failed attempt has returned.</param>
        private static void AssertNoReservedCapacity(NntpSessionPool pool)
        {
            Assert.Equal(0, pool.LiveSessionCount);
            Assert.Equal(0, pool.ActiveSessionCount);
            Assert.Equal(0, pool.ActiveLeaseCount);
            Assert.Equal(0, CreatedSessionCount(pool));
            Assert.Equal(0, ReservedConnectionCount(pool));
        }

        /// <summary>Reads <c>_created</c> after the pool's create path has quiesced.</summary>
        /// <param name="pool">Pool whose reservation count is observed.</param>
        /// <returns>The current created-session count.</returns>
        private static int CreatedSessionCount(NntpSessionPool pool)
        {
            var field = typeof(NntpSessionPool).GetField("_created", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return (int)field.GetValue(pool)!;
        }

        /// <summary>
        /// Reads how many connection numbers are reserved after creation has quiesced.
        /// The read is not taken under the pool slot lock; callers wait until no create is in progress.
        /// </summary>
        /// <param name="pool">Pool whose connection-number set is observed.</param>
        /// <returns>The number of reserved connection numbers.</returns>
        private static int ReservedConnectionCount(NntpSessionPool pool)
        {
            var field = typeof(NntpSessionPool).GetField("_usedSlots", BindingFlags.Instance | BindingFlags.NonPublic);
            var slots = Assert.IsType<HashSet<int>>(field?.GetValue(pool));
            return slots.Count;
        }

        /// <summary>Reads the periodic replenish task started by the pool.</summary>
        /// <param name="pool">Pool whose replenish loop is running.</param>
        /// <returns>The loop task. It stays incomplete while the loop is waiting for the next interval.</returns>
        private static Task ReplenishTask(NntpSessionPool pool)
        {
            var field = typeof(NntpSessionPool).GetField("_replenishTask", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var task = field.GetValue(pool) as Task;
            Assert.NotNull(task);
            return task!;
        }

        /// <summary>Returns the first command index whose text starts with <paramref name="prefix"/>, or -1.</summary>
        /// <param name="server">Scripted peer that recorded the commands.</param>
        /// <param name="prefix">Command prefix, compared without case sensitivity.</param>
        private static int CommandIndex(ScriptedNntpServer server, string prefix)
        {
            var commands = server.Commands;
            for (var i = 0; i < commands.Count; i++)
            {
                if (commands[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Polls <paramref name="predicate"/> with yields until true or a two-second timeout.</summary>
        /// <param name="predicate">Condition that must become true.</param>
        private static async Task WaitUntilAsync(Func<bool> predicate)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!predicate())
            {
                cts.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        /// <summary>
        /// Holds replenish timers until the test releases them.
        /// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> creates these timers; the production interval is unchanged.
        /// </summary>
        private sealed class ReplenishDelaySource : TimeProvider
        {
            /// <summary>Replenish timers entered and not yet observed by the test, in entry order.</summary>
            private readonly ConcurrentQueue<HeldTimer> _pending = new();

            /// <summary>How many replenish delays have been entered, including one the test has already observed.</summary>
            private int _timersCreated;

            /// <summary>How many replenish delays <see cref="CreateTimer"/> has captured.</summary>
            internal int TimersCreated => Volatile.Read(ref _timersCreated);

            /// <summary>Waits until the replenish loop enters its next delay.</summary>
            /// <returns>A callback that fires that delay.</returns>
            /// <exception cref="TimeoutException">The loop does not enter another delay within five seconds.</exception>
            public async Task<Action> WaitForNextAsync()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!timeout.IsCancellationRequested)
                {
                    if (_pending.TryDequeue(out var next))
                    {
                        return next.Release;
                    }

                    await Task.Yield();
                }

                throw new TimeoutException("Periodic replenishment did not enter its next delay.");
            }

            /// <summary>Captures the delay callback instead of arming a real timer.</summary>
            /// <param name="callback">Completion callback supplied by <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>.</param>
            /// <param name="state">State passed back to <paramref name="callback"/> when the test releases the delay.</param>
            /// <param name="dueTime">Accepted for the timer contract. The test decides when the callback runs.</param>
            /// <param name="period">Accepted for the timer contract. Replenish delays are one-shot.</param>
            /// <returns>A timer that fires only when the test releases it. Disposal does not fire it.</returns>
            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                _ = dueTime;
                _ = period;
                Interlocked.Increment(ref _timersCreated);
                var timer = new HeldTimer(callback, state);
                _pending.Enqueue(timer);
                return timer;
            }

            /// <summary>One replenish delay whose callback the test invokes.</summary>
            /// <param name="callback">Timer callback supplied by the delay.</param>
            /// <param name="state">State object supplied with <paramref name="callback"/>.</param>
            private sealed class HeldTimer(TimerCallback callback, object? state) : ITimer
            {
                /// <summary>Zero until <see cref="Release"/> has invoked the callback.</summary>
                private int _fired;

                /// <summary>Invokes the delay callback once. A disposed or already released timer does not invoke it again.</summary>
                public void Release()
                {
                    if (Interlocked.Exchange(ref _fired, 1) == 0)
                    {
                        callback(state);
                    }
                }

                /// <summary>Accepts reschedule requests. The test, not the due time, fires the callback.</summary>
                /// <param name="dueTime">Ignored.</param>
                /// <param name="period">Ignored.</param>
                /// <returns><see langword="true"/>. The timer remains under test control.</returns>
                public bool Change(TimeSpan dueTime, TimeSpan period) => true;

                /// <summary>Stops a later <see cref="Release"/> from invoking the callback.</summary>
                public void Dispose()
                {
                    Interlocked.Exchange(ref _fired, 1);
                }

                /// <summary>Stops a later <see cref="Release"/> from invoking the callback.</summary>
                /// <returns>A completed task.</returns>
                public ValueTask DisposeAsync()
                {
                    Dispose();
                    return ValueTask.CompletedTask;
                }
            }
        }

        /// <summary>
        /// Logger that throws from establishment and retirement diagnostics and accepts every other message.
        /// </summary>
        private sealed class ThrowingEstablishmentLogger : ILogger
        {
            /// <inheritdoc />
            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull => EmptyScope.Instance;

            /// <inheritdoc />
            public bool IsEnabled(LogLevel logLevel) => true;

            /// <summary>
            /// Throws <see cref="InvalidOperationException"/> for establishment-failure, establishment-cancellation,
            /// and session-retirement messages. Other messages are ignored so transport disposal can finish.
            /// </summary>
            /// <inheritdoc />
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                var message = formatter(state, exception);
                if (message.Contains("establishment failed", StringComparison.Ordinal)
                    || message.Contains("establishment cancelled", StringComparison.Ordinal)
                    || message.Contains("session retired", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("logger failed");
                }
            }

            /// <summary>Scope returned by <see cref="BeginScope{TState}"/>. Disposal does nothing.</summary>
            private sealed class EmptyScope : IDisposable
            {
                /// <summary>Shared scope instance.</summary>
                internal static EmptyScope Instance { get; } = new();

                /// <inheritdoc />
                public void Dispose()
                {
                }
            }
        }

        /// <summary>
        /// Logger that throws once from the establishment-completed event and then accepts every later call.
        /// </summary>
        private sealed class ThrowOnceOnCompletionLogger : ILogger
        {
            /// <summary>How many establishment-completed logs have thrown.</summary>
            private int _completionFailures;

            /// <summary>How many establishment-completed logs have thrown.</summary>
            internal int CompletionFailures => Volatile.Read(ref _completionFailures);

            /// <inheritdoc />
            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull => EmptyScope.Instance;

            /// <inheritdoc />
            public bool IsEnabled(LogLevel logLevel) => true;

            /// <summary>
            /// Throws <see cref="InvalidOperationException"/> the first time the establishment-completed message is logged.
            /// Later calls, including retirement diagnostics, are ignored.
            /// </summary>
            /// <inheritdoc />
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                var message = formatter(state, exception);
                if (message.Contains("establishment completed", StringComparison.Ordinal)
                    && Interlocked.CompareExchange(ref _completionFailures, 1, 0) == 0)
                {
                    throw new InvalidOperationException("completion log failed");
                }
            }

            /// <summary>Scope returned by <see cref="BeginScope{TState}"/>. Disposal does nothing.</summary>
            private sealed class EmptyScope : IDisposable
            {
                /// <summary>Shared scope instance.</summary>
                internal static EmptyScope Instance { get; } = new();

                /// <inheritdoc />
                public void Dispose()
                {
                }
            }
        }

        /// <summary>
        /// Holds DATE keepalive delays until the test releases them.
        /// Replenish delays on the same clock never fire, so replacement comes only from retirement's immediate fill.
        /// </summary>
        private sealed class KeepAliveDelaySource : TimeProvider
        {
            /// <summary>Keepalive delays entered and not yet observed, in entry order.</summary>
            private readonly ConcurrentQueue<HeldTimer> _pending = new();

            /// <summary>Waits until the next 30-second DATE delay is entered.</summary>
            /// <returns>A callback that completes that delay.</returns>
            /// <exception cref="TimeoutException">No keepalive delay is entered within five seconds.</exception>
            internal async Task<Action> WaitForKeepAliveAsync()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!timeout.IsCancellationRequested)
                {
                    if (_pending.TryDequeue(out var next))
                    {
                        return next.Release;
                    }

                    await Task.Yield();
                }

                throw new TimeoutException("DATE keepalive did not enter its delay.");
            }

            /// <summary>Holds a 30-second keepalive delay and returns an inert timer for every other delay.</summary>
            /// <inheritdoc />
            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime == TimeSpan.FromSeconds(30))
                {
                    var timer = new HeldTimer(callback, state);
                    _pending.Enqueue(timer);
                    return timer;
                }

                _ = period;
                return new IdleTimer();
            }

            /// <summary>One keepalive delay whose callback the test invokes.</summary>
            /// <param name="callback">Timer callback supplied by the delay.</param>
            /// <param name="state">State object supplied with <paramref name="callback"/>.</param>
            private sealed class HeldTimer(TimerCallback callback, object? state) : ITimer
            {
                /// <summary>Zero until <see cref="Release"/> has invoked the callback.</summary>
                private int _fired;

                /// <summary>Invokes the delay callback once.</summary>
                internal void Release()
                {
                    if (Interlocked.Exchange(ref _fired, 1) == 0)
                    {
                        callback(state);
                    }
                }

                /// <summary>Accepts reschedule requests. The test fires the callback.</summary>
                /// <param name="dueTime">Ignored.</param>
                /// <param name="period">Ignored.</param>
                /// <returns><see langword="true"/>.</returns>
                public bool Change(TimeSpan dueTime, TimeSpan period) => true;

                /// <summary>Stops a later <see cref="Release"/> from invoking the callback.</summary>
                public void Dispose()
                {
                    Interlocked.Exchange(ref _fired, 1);
                }

                /// <summary>Stops a later <see cref="Release"/> from invoking the callback.</summary>
                /// <returns>A completed task.</returns>
                public ValueTask DisposeAsync()
                {
                    Dispose();
                    return ValueTask.CompletedTask;
                }
            }
        }

        /// <summary>
        /// TimeProvider returning inert timers for keep-alive tests.
        /// </summary>

        private sealed class IdleTimeProvider : TimeProvider
        {
            /// <summary>Returns <see cref="IdleTimer"/> instances so DATE keep-alive timers never fire during the scenario.</summary>
            /// <inheritdoc />
            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
                new IdleTimer();
        }

        /// <summary>
        /// TimeProvider whose DATE keepalive timer throws to simulate a keepalive clock failure.
        /// The periodic replenish timer on the same clock stays inert so this failure stays on the DATE path.
        /// </summary>

        private sealed class OnceFailingTimeProvider : TimeProvider
        {
            /// <summary>
            /// Throws when the due time is the 30-second DATE interval used by the keepalive fault test.
            /// Replenish waits use a different due time and receive an inert timer.
            /// </summary>
            /// <inheritdoc />
            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime == TimeSpan.FromSeconds(30))
                {
                    throw new NotSupportedException("keepalive clock");
                }

                return new IdleTimer();
            }
        }

        /// <summary>
        /// No-op ITimer used when keep-alive timers should not fire.
        /// </summary>

        private sealed class IdleTimer : ITimer
        {
            /// <summary>Always returns false so the timer is never rescheduled.</summary>
            /// <param name="dueTime">Ignored.</param>
            /// <param name="period">Ignored.</param>
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            /// <summary>No-op timer disposal.</summary>
            public void Dispose()
            {
            }

            /// <summary>Completes immediately; no asynchronous timer teardown.</summary>
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        /// <summary>Reads one private instance field from <paramref name="session"/>.</summary>
        /// <param name="session">Session whose transport fields are asserted after disposal.</param>
        /// <param name="name">Field name, including the leading underscore.</param>
        /// <returns>The field value, which is null when disposal has cleared it.</returns>
        private static object? SessionField(NntpProviderSession session, string name)
        {
            var field = typeof(NntpProviderSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return field.GetValue(session);
        }

        /// <summary>
        /// <see cref="SslStream"/> that forwards I/O to an inner duplex stream and rejects a second write while the first is held.
        /// Forced disposal must not produce that second write.
        /// </summary>
        private sealed class NestedWriteTlsStream : SslStream
        {
            /// <summary>Plaintext duplex stream. Owned by this wrapper.</summary>
            private readonly Stream _inner;

            /// <summary>Completes when the held write has entered <see cref="WriteAsync"/>.</summary>
            private readonly TaskCompletionSource _entered;

            /// <summary>Released by the test to let the held write finish.</summary>
            private readonly TaskCompletionSource _release;

            /// <summary>One while the held write has not left <see cref="WriteAsync"/>.</summary>
            private int _writeDepth;

            /// <summary>Zero until <see cref="HoldWrites"/> accepts the next write.</summary>
            private int _holdArmed;

            /// <summary>Entries to <see cref="DisposeAsync"/>.</summary>
            private int _disposeAsyncCount;

            /// <summary>Writes rejected because another write was still inside <see cref="WriteAsync"/>.</summary>
            private int _overlappingWrites;

            /// <summary>Wraps <paramref name="inner"/> and closes it when this wrapper is disposed.</summary>
            /// <param name="inner">Scripted duplex stream. Ownership transfers to this wrapper.</param>
            /// <param name="entered">Signaled from inside the held write.</param>
            /// <param name="release">Awaited by the held write.</param>
            internal NestedWriteTlsStream(Stream inner, TaskCompletionSource entered, TaskCompletionSource release)
                : base(inner, leaveInnerStreamOpen: false)
            {
                _inner = inner;
                _entered = entered;
                _release = release;
            }

            /// <summary>
            /// When true, the next <see cref="WriteAsync"/> waits on the release gate.
            /// A write that arrives while that one is still inside <see cref="WriteAsync"/> throws <see cref="NotSupportedException"/>.
            /// </summary>
            internal bool HoldWrites { get; set; }

            /// <summary>Gets how many times <see cref="DisposeAsync"/> has been entered.</summary>
            internal int DisposeAsyncCount => Volatile.Read(ref _disposeAsyncCount);

            /// <summary>Gets how many writes arrived while a held write was still inside <see cref="WriteAsync"/>.</summary>
            internal int OverlappingWriteCount => Volatile.Read(ref _overlappingWrites);

            /// <inheritdoc />
            public override bool CanRead => true;

            /// <inheritdoc />
            public override bool CanWrite => true;

            /// <summary>Reads from the inner duplex stream. No TLS record layer is used.</summary>
            /// <param name="buffer">Destination for plaintext bytes.</param>
            /// <param name="cancellationToken">Cancels the inner read.</param>
            /// <returns>The inner read count.</returns>
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                _inner.ReadAsync(buffer, cancellationToken);

            /// <summary>Flushes the inner duplex stream.</summary>
            /// <param name="cancellationToken">Cancels the inner flush.</param>
            /// <returns>The inner flush task.</returns>
            public override Task FlushAsync(CancellationToken cancellationToken) =>
                _inner.FlushAsync(cancellationToken);

            /// <summary>
            /// Forwards a write, or holds the first write after <see cref="HoldWrites"/> is set.
            /// A second write while the held one is active throws <see cref="NotSupportedException"/>.
            /// </summary>
            /// <param name="buffer">Bytes to write to the inner stream after the hold, when this call is the held write.</param>
            /// <param name="cancellationToken">Cancels the inner write. The hold itself waits on the test gate.</param>
            /// <returns>A task that completes when the inner write completes, or a faulted task for the overlapping write.</returns>
            /// <exception cref="NotSupportedException">Another write is already inside this method.</exception>
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (Volatile.Read(ref _writeDepth) > 0)
                {
                    Interlocked.Increment(ref _overlappingWrites);
                    throw new NotSupportedException("SslStream does not support simultaneous writes.");
                }

                var holdThis = HoldWrites && Interlocked.Exchange(ref _holdArmed, 1) == 0;
                if (!holdThis)
                {
                    await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                    return;
                }

                Interlocked.Increment(ref _writeDepth);
                try
                {
                    _entered.TrySetResult();
                    await _release.Task.ConfigureAwait(false);
                    await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _writeDepth);
                }
            }

            /// <summary>Counts one dispose, then disposes this wrapper and the inner duplex stream.</summary>
            /// <returns>The base dispose task.</returns>
            public override ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref _disposeAsyncCount);
                return base.DisposeAsync();
            }
        }
    }
}

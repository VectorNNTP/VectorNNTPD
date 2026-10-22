using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using VectorNNTPD.BackFillerService.Accounts;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.BackFillerService.Tests.Nntp
{
    /// <summary>
    /// Shared shutdown deadline across stalled sessions, providers, and one replacement drain.
    /// </summary>
    /// <remarks>
    /// Scale pools use <see cref="SharedBudget"/> (500 ms) and <c>keepAliveSeconds: 0</c>, so replacement
    /// <see cref="NntpSessionPool.DrainAndDisposeAsync(CancellationToken)"/> spends essentially one shared
    /// retirement deadline on stalled QUITs (keepalive drain is empty). A correct run finishes near that budget
    /// plus scheduling. Regressing to per-session <see cref="NntpProviderSession"/> QuitAllowance (2 s) with
    /// <see cref="NntpShutdownWork.Parallelism"/> yields about two serial waves (~4 s) and also starts a QUIT on
    /// every session. <see cref="AssertWithinCeiling"/> rejects the QuitAllowance stack; <c>quitStarted</c> bounds
    /// reject per-session independent budgets that still finish under the ceiling.
    /// <see cref="MeasureHangTimeout"/> is only hang detection for <see cref="MeasureAsync"/> and does not replace
    /// <see cref="AssertWithinCeiling"/>.
    /// </remarks>
    public sealed class NntpShutdownDeadlineTests
    {
        /// <summary>
        /// Acceptance bound for scale shutdown elapsed time: below one QuitAllowance (2 s), with room for
        /// CancelAfter scheduling delay after a single shared <see cref="SharedBudget"/>.
        /// </summary>
        private static readonly TimeSpan SharedShutdownCeiling = TimeSpan.FromMilliseconds(1500);

        /// <summary>
        /// Hang detection for <see cref="MeasureAsync"/>. Wider than <see cref="SharedShutdownCeiling"/> so a slow
        /// but still shared-budget drain is judged by <see cref="AssertWithinCeiling"/> instead of <see cref="TimeoutException"/>.
        /// </summary>
        private static readonly TimeSpan MeasureHangTimeout = TimeSpan.FromSeconds(2);

        /// <summary>Budget captured once for the many-session and many-provider shutdown tests.</summary>
        private static readonly TimeSpan SharedBudget = TimeSpan.FromMilliseconds(500);

        /// <summary>Receives the measured budget, counts, and elapsed time.</summary>
        private readonly ITestOutputHelper _output;

        /// <summary>Captures <paramref name="output"/> for the measured shutdown lines.</summary>
        /// <param name="output">xUnit output for the shutdown-budget measurements.</param>
        public NntpShutdownDeadlineTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// An unbounded deadline keeps the caller's allowance, and an expired deadline shortens it to zero.
        /// A second deadline is not affected by the first.
        /// </summary>
        [Fact]
        public void Deadline_cap_is_local_to_the_value_that_was_captured()
        {
            var allowance = TimeSpan.FromSeconds(2);
            Assert.False(NntpShutdownDeadline.None.IsBounded);
            Assert.False(NntpShutdownDeadline.None.IsExpired);
            Assert.Equal(allowance, NntpShutdownDeadline.None.Cap(allowance));

            var expired = NntpShutdownDeadline.FromBudget(TimeSpan.Zero);
            var independent = NntpShutdownDeadline.FromBudget(TimeSpan.FromSeconds(30));
            Assert.True(expired.IsExpired);
            Assert.Equal(TimeSpan.Zero, expired.Cap(allowance));
            Assert.True(NntpShutdownDeadline.FromBudget(TimeSpan.FromMilliseconds(-5)).IsExpired);
            Assert.False(independent.IsExpired);
            Assert.Equal(allowance, independent.Cap(allowance));
            Assert.True(independent.Remaining > TimeSpan.Zero);
            Assert.True(independent.Remaining <= TimeSpan.FromSeconds(30));
        }

        /// <summary>
        /// Many idle sessions whose QUIT writes stall share one pool budget.
        /// Later sessions do not each receive a fresh two-second QUIT allowance.
        /// </summary>
        /// <remarks>
        /// The budget is 500 milliseconds and the accepted ceiling is 1500 milliseconds (below one QuitAllowance).
        /// Eight independent two-second QUIT attempts would exceed that ceiling.
        /// At most <see cref="NntpShutdownWork.Parallelism"/> writes can be in progress.
        /// The shared deadline token cancels that wave, and a later session observes the same token before it can start QUIT.
        /// A cancelled write logs <c>TaskCanceledException</c>, the runtime type thrown for the QUIT token, not the <see cref="OperationCanceledException"/> filter name.
        /// </remarks>
        [Fact]
        public async Task Many_stalled_sessions_share_one_shutdown_budget()
        {
            const int sessions = 8;
            var factory = new ScriptedNntpTransportFactory();
            var servers = EnqueueStalledQuitServers(factory, sessions);
            var logger = new CollectingLogger<NntpSessionPool>();
            var pool = NntpSessionPoolTests.CreatePool(factory, max: sessions, logger: logger, shutdownGrace: SharedBudget);
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                Assert.Equal(sessions, pool.ActiveSessionCount);

                var elapsed = await MeasureAsync(() => pool.DisposeAsync().AsTask(), servers);
                Report("many-sessions", SharedBudget, sessions, providers: 1, QuitStarts(servers), elapsed);

                var quitStarted = QuitStarts(servers);
                AssertWithinCeiling(elapsed);
                Assert.Equal(0, pool.LiveSessionCount);
                Assert.InRange(quitStarted, 1, NntpShutdownWork.Parallelism);
                Assert.True(quitStarted < sessions);
                Assert.Equal(quitStarted, CountQuit(logger));
                Assert.All(servers, static server => Assert.Equal(1, server.ClientStreamDisposeCount));
                AssertQuitCancelledByToken(logger.Messages, quitStarted);
            }
            finally
            {
                Release(servers);
                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// A drain deadline that expires while ARTICLE is blocked aborts that write and does not grant idle sessions a fresh QUIT budget.
        /// </summary>
        [Fact]
        public async Task Active_command_during_deadline_expiry_unwinds_without_per_session_quit_budgets()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            AbortingWriteStream? gate = null;
            var articleServer = new ScriptedNntpServer
            {
                ClientStreamDecorator = inner =>
                {
                    gate = new AbortingWriteStream(inner, entered);
                    return gate;
                },
            };
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(articleServer);
            var idleServers = EnqueueStalledQuitServers(factory, 2);
            var logger = new CollectingLogger<NntpSessionPool>();
            var budget = TimeSpan.FromMilliseconds(100);
            var pool = NntpSessionPoolTests.CreatePool(factory, max: 3, logger: logger, shutdownGrace: budget);
            NntpSessionLease? busy = null;
            var returned = new List<NntpSessionLease>();
            Task<ArticleRetrievalResult>? download = null;
            try
            {
                busy = await pool.AcquireAsync(CancellationToken.None);
                returned.Add(await pool.AcquireAsync(CancellationToken.None));
                returned.Add(await pool.AcquireAsync(CancellationToken.None));
                foreach (var idle in returned)
                {
                    await idle.DisposeAsync();
                }

                returned.Clear();
                Assert.NotNull(gate);
                gate.Arm();
                download = busy.Session.DownloadArticleAsync("<a@b>", CancellationToken.None);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

                var elapsed = await MeasureAsync(() => pool.DisposeAsync().AsTask(), idleServers);
                Report("active-command", budget, sessions: 3, providers: 1, QuitStarts(idleServers), elapsed);
                using var result = await download.WaitAsync(TimeSpan.FromSeconds(2));

                AssertWithinCeiling(elapsed);
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
                Assert.Equal(nameof(IOException), result.Reason);
                Assert.Equal(1, gate.MaxWriteDepth);
                Assert.Equal(NntpSessionState.Closed, busy.Session.State);
                Assert.Equal(1, articleServer.ClientStreamDisposeCount);
                Assert.Equal(0, QuitStarts(idleServers));
                Assert.Equal(0, CountQuit(logger));
                Assert.All(idleServers, static server => Assert.Equal(1, server.ClientStreamDisposeCount));
                Assert.DoesNotContain(
                    logger.Messages,
                    static message => message.Contains("QUIT failed during dispose", StringComparison.Ordinal));
            }
            finally
            {
                Release(idleServers);
                if (download is not null)
                {
                    try
                    {
                        using var leftover = await download.WaitAsync(TimeSpan.FromSeconds(2));
                    }
                    catch (Exception ex) when (ex is not (ArgumentNullException or NullReferenceException))
                    {
                        _ = ex;
                    }
                }

                if (busy is not null)
                {
                    await busy.DisposeAsync();
                }

                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// Cancelling the shared deadline token stops the next retirement wave while the Stopwatch instant is still in the future.
        /// </summary>
        /// <remarks>
        /// The first wave is held inside QUIT. Cancellation is the signal those writes already observe.
        /// A later session must see that token before it writes, including when <see cref="NntpShutdownDeadline.IsExpired"/> is still false.
        /// </remarks>
        [Fact]
        public async Task Shared_deadline_cancellation_does_not_start_the_next_quit_wave()
        {
            const int sessions = 8;
            var factory = new ScriptedNntpTransportFactory();
            var servers = EnqueueStalledQuitServers(factory, sessions);
            var pool = NntpSessionPoolTests.CreatePool(factory, max: sessions, shutdownGrace: TimeSpan.FromMinutes(1));
            using var deadline = NntpShutdownDeadline.FromBudget(TimeSpan.FromMinutes(1));
            var drain = Task.CompletedTask;
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                drain = pool.DrainAndDisposeAsync(CancellationToken.None, deadline);
                await WaitUntilAsync(() => QuitStarts(servers) == NntpShutdownWork.Parallelism);

                Assert.False(deadline.IsExpired);
                Assert.False(deadline.IsCancellationRequested);
                deadline.Cancel();
                await drain.WaitAsync(TimeSpan.FromSeconds(2));

                Assert.Equal(NntpShutdownWork.Parallelism, QuitStarts(servers));
                Assert.False(deadline.IsExpired);
                Assert.True(deadline.IsCancellationRequested);
                Assert.All(servers, static server => Assert.Equal(1, server.ClientStreamDisposeCount));
                Assert.Equal(0, pool.LiveSessionCount);
            }
            finally
            {
                deadline.Cancel();
                Release(servers);
                await drain.WaitAsync(TimeSpan.FromSeconds(5));
                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// Cancelling the deadline while the lease drain is still waiting does not let idle sessions start QUIT.
        /// The active command still unwinds through transport disposal, without a QUIT on that stream.
        /// </summary>
        [Fact]
        public async Task Cancelling_the_deadline_during_lease_drain_skips_idle_quit()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var drainEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            AbortingWriteStream? gate = null;
            var articleServer = new ScriptedNntpServer
            {
                ClientStreamDecorator = inner =>
                {
                    gate = new AbortingWriteStream(inner, entered);
                    return gate;
                },
            };
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(articleServer);
            var idleServers = EnqueueStalledQuitServers(factory, 2);
            var logger = new CollectingLogger<NntpSessionPool>();
            var pool = NntpSessionPoolTests.CreatePool(factory, max: 3, logger: logger, shutdownGrace: TimeSpan.FromMinutes(1));
            using var deadline = NntpShutdownDeadline.FromBudget(TimeSpan.FromMinutes(1));
            NntpSessionLease? busy = null;
            Task<ArticleRetrievalResult>? download = null;
            var drain = Task.CompletedTask;
            try
            {
                busy = await pool.AcquireAsync(CancellationToken.None);
                var firstIdle = await pool.AcquireAsync(CancellationToken.None);
                var secondIdle = await pool.AcquireAsync(CancellationToken.None);
                await firstIdle.DisposeAsync();
                await secondIdle.DisposeAsync();
                Assert.NotNull(gate);
                gate.Arm();
                download = busy.Session.DownloadArticleAsync("<a@b>", CancellationToken.None);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                pool.BeforeLeaseDrainWait = _ =>
                {
                    drainEntered.TrySetResult();
                    return releaseDrain.Task;
                };

                drain = pool.DrainAndDisposeAsync(CancellationToken.None, deadline);
                await drainEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(0, QuitStarts(idleServers));
                Assert.False(deadline.IsExpired);
                deadline.Cancel();
                releaseDrain.TrySetResult();
                await drain.WaitAsync(TimeSpan.FromSeconds(2));
                using var result = await download.WaitAsync(TimeSpan.FromSeconds(2));

                Assert.False(deadline.IsExpired);
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
                Assert.Equal(1, gate.MaxWriteDepth);
                Assert.Equal(0, QuitStarts(idleServers));
                Assert.Equal(0, CountQuit(logger));
                Assert.Equal(1, articleServer.ClientStreamDisposeCount);
                Assert.All(idleServers, static server => Assert.Equal(1, server.ClientStreamDisposeCount));
            }
            finally
            {
                deadline.Cancel();
                releaseDrain.TrySetResult();
                Release(idleServers);
                if (download is not null)
                {
                    try
                    {
                        using var leftover = await download.WaitAsync(TimeSpan.FromSeconds(2));
                    }
                    catch (Exception ex) when (ex is not (ArgumentNullException or NullReferenceException))
                    {
                        _ = ex;
                    }
                }

                await drain.WaitAsync(TimeSpan.FromSeconds(5));
                if (busy is not null)
                {
                    await busy.DisposeAsync();
                }

                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// Several providers with stalled sessions share the registry deadline.
        /// Teardown does not grow by one full timeout per provider.
        /// </summary>
        [Fact]
        public async Task Many_providers_share_one_registry_shutdown_budget()
        {
            const int providers = 3;
            const int sessionsPerProvider = 2;
            var transport = new ScriptedNntpTransportFactory();
            var servers = EnqueueStalledQuitServers(transport, providers * sessionsPerProvider);
            var catalog = new ProviderConfigurationCatalog();
            await using var registry = CreateRegistry(catalog, transport, SharedBudget);
            try
            {
                await registry.ApplySnapshotAsync(
                    [
                        Definition("Giganews", sessionsPerProvider),
                        Definition("Eweka", sessionsPerProvider),
                        Definition("Tweak", sessionsPerProvider),
                    ],
                    CancellationToken.None);

                var elapsed = await MeasureAsync(() => registry.DisposeAsync().AsTask(), servers);
                Report("many-providers", SharedBudget, sessionsPerProvider * providers, providers, QuitStarts(servers), elapsed);

                AssertWithinCeiling(elapsed);
                Assert.False(registry.TryGetPool("Giganews", out _));
                Assert.False(registry.TryGetPool("Eweka", out _));
                Assert.False(registry.TryGetPool("Tweak", out _));
                Assert.InRange(QuitStarts(servers), 1, servers.Count);
                Assert.All(servers, static server => Assert.Equal(1, server.ClientStreamDisposeCount));
            }
            finally
            {
                Release(servers);
            }
        }

        /// <summary>
        /// A pool whose grace is already expired aborts every idle session and does not start QUIT.
        /// </summary>
        [Fact]
        public async Task Already_expired_pool_budget_aborts_without_starting_quit()
        {
            const int sessions = 4;
            var factory = new ScriptedNntpTransportFactory();
            var servers = EnqueueStalledQuitServers(factory, sessions);
            var logger = new CollectingLogger<NntpSessionPool>();
            var pool = NntpSessionPoolTests.CreatePool(
                factory,
                max: sessions,
                logger: logger,
                shutdownGrace: TimeSpan.Zero);
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                var elapsed = await MeasureAsync(() => pool.DisposeAsync().AsTask(), servers);
                Report("expired-pool", TimeSpan.Zero, sessions, providers: 1, QuitStarts(servers), elapsed);

                AssertWithinCeiling(elapsed);
                Assert.Equal(0, QuitStarts(servers));
                Assert.Equal(0, pool.LiveSessionCount);
                Assert.DoesNotContain(logger.Messages, static message => message.Contains("TX: QUIT", StringComparison.Ordinal));
                Assert.All(servers, static server => Assert.Equal(1, server.ClientStreamDisposeCount));
            }
            finally
            {
                Release(servers);
                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// A caller token cancelled before registry stop skips QUIT and still disposes every provider's transport.
        /// </summary>
        [Fact]
        public async Task Caller_cancellation_before_registry_stop_skips_quit()
        {
            var transport = new ScriptedNntpTransportFactory();
            var servers = EnqueueStalledQuitServers(transport, 4);
            var catalog = new ProviderConfigurationCatalog();
            await using var registry = CreateRegistry(catalog, transport, TimeSpan.FromSeconds(10));
            try
            {
                await registry.ApplySnapshotAsync(
                    [Definition("Giganews", 2), Definition("Eweka", 2)],
                    CancellationToken.None);
                using var cancelled = new CancellationTokenSource();
                await cancelled.CancelAsync();

                var started = Stopwatch.GetTimestamp();
                await registry.StopAsync(cancelled.Token).WaitAsync(SharedShutdownCeiling);
                var elapsed = Stopwatch.GetElapsedTime(started);
                Report("cancel-before", TimeSpan.FromSeconds(10), sessions: 4, providers: 2, QuitStarts(servers), elapsed);

                AssertWithinCeiling(elapsed);
                Assert.Equal(0, QuitStarts(servers));
                Assert.All(servers, static server => Assert.Equal(1, server.ClientStreamDisposeCount));
                Assert.False(registry.TryGetPool("Giganews", out _));
            }
            finally
            {
                Release(servers);
            }
        }

        /// <summary>
        /// Cancelling the caller token while QUIT is stalled ends the registry shutdown before the full grace.
        /// </summary>
        [Fact]
        public async Task Caller_cancellation_during_quit_ends_the_shared_shutdown()
        {
            var transport = new ScriptedNntpTransportFactory();
            var servers = EnqueueStalledQuitServers(transport, 2);
            var catalog = new ProviderConfigurationCatalog();
            await using var registry = CreateRegistry(catalog, transport, TimeSpan.FromSeconds(10));
            var stop = Task.CompletedTask;
            using var caller = new CancellationTokenSource();
            try
            {
                await registry.ApplySnapshotAsync(
                    [Definition("Giganews", 1), Definition("Eweka", 1)],
                    CancellationToken.None);

                var started = Stopwatch.GetTimestamp();
                stop = registry.StopAsync(caller.Token);
                await WaitUntilAsync(() => QuitStarts(servers) >= 1);
                await caller.CancelAsync();
                await stop.WaitAsync(TimeSpan.FromSeconds(2));
                var elapsed = Stopwatch.GetElapsedTime(started);
                Report("cancel-during", TimeSpan.FromSeconds(10), sessions: 2, providers: 2, QuitStarts(servers), elapsed);

                Assert.True(elapsed < TimeSpan.FromSeconds(2), $"elapsed {elapsed.TotalMilliseconds:0} ms");
                Assert.All(servers, static server => Assert.Equal(1, server.ClientStreamDisposeCount));
            }
            finally
            {
                await caller.CancelAsync();
                Release(servers);
                await stop.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        /// <summary>
        /// One session that throws during transport dispose does not skip the other sessions.
        /// The fault remains visible on the pool dispose task.
        /// </summary>
        [Fact]
        public async Task One_session_fault_does_not_skip_the_other_sessions()
        {
            var factory = new ScriptedNntpTransportFactory();
            var servers = EnqueueReadyServers(factory, 3);
            servers[0].FaultClientDisposeUnexpected = true;
            var pool = NntpSessionPoolTests.CreatePool(factory, max: 3, shutdownGrace: TimeSpan.FromSeconds(2));
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            var fault = await Assert.ThrowsAsync<AggregateException>(() => pool.DisposeAsync().AsTask());

            Assert.Contains(fault.Flatten().InnerExceptions, static ex => ex is InvalidOperationException);
            Assert.Equal(0, pool.LiveSessionCount);
            Assert.All(servers, static server => Assert.Equal(1, server.ClientStreamDisposeCount));
        }

        /// <summary>
        /// One provider that throws during teardown does not skip the other provider.
        /// </summary>
        [Fact]
        public async Task One_provider_fault_does_not_skip_the_other_provider()
        {
            var transport = new ScriptedNntpTransportFactory();
            var servers = EnqueueReadyServers(transport, 2);
            servers[0].FaultClientDisposeUnexpected = true;
            var catalog = new ProviderConfigurationCatalog();
            await using var registry = CreateRegistry(catalog, transport, TimeSpan.FromSeconds(2));
            await registry.ApplySnapshotAsync(
                [Definition("Giganews", 1), Definition("Eweka", 1)],
                CancellationToken.None);

            var fault = await Assert.ThrowsAsync<AggregateException>(() => registry.DisposeAsync().AsTask());

            Assert.Contains(fault.Flatten().InnerExceptions, static ex => ex is InvalidOperationException);
            Assert.All(servers, static server => Assert.Equal(1, server.ClientStreamDisposeCount));
            Assert.False(registry.TryGetPool("Giganews", out _));
            Assert.False(registry.TryGetPool("Eweka", out _));
        }

        /// <summary>
        /// Responsive idle sessions still send QUIT and finish before the pool deadline.
        /// </summary>
        [Fact]
        public async Task Responsive_idle_sessions_quit_before_the_deadline()
        {
            const int sessions = 3;
            var factory = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(factory, sessions);
            var logger = new CollectingLogger<NntpSessionPool>();
            var grace = TimeSpan.FromSeconds(2);
            var pool = NntpSessionPoolTests.CreatePool(factory, max: sessions, logger: logger, shutdownGrace: grace);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            var started = Stopwatch.GetTimestamp();
            await pool.DisposeAsync().AsTask().WaitAsync(grace);
            var elapsed = Stopwatch.GetElapsedTime(started);
            Report("fast-shutdown", grace, sessions, providers: 1, quitStarted: CountQuit(logger), elapsed);

            Assert.True(elapsed < grace, $"elapsed {elapsed.TotalMilliseconds:0} ms");
            Assert.Equal(sessions, CountQuit(logger));
            Assert.Equal(0, pool.LiveSessionCount);
            Assert.DoesNotContain(logger.Messages, static message => message.Contains("QUIT failed during dispose", StringComparison.Ordinal));
        }

        /// <summary>
        /// Disposing one pool under a short deadline does not change the QUIT behavior of a later independent pool.
        /// </summary>
        [Fact]
        public async Task Independent_pool_dispose_does_not_reuse_another_pools_deadline()
        {
            var stalledFactory = new ScriptedNntpTransportFactory();
            var stalledServers = EnqueueStalledQuitServers(stalledFactory, 2);
            var stalled = NntpSessionPoolTests.CreatePool(
                stalledFactory,
                max: 2,
                shutdownGrace: TimeSpan.FromMilliseconds(200));
            var fastFactory = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(fastFactory, 2);
            var fastLogger = new CollectingLogger<NntpSessionPool>();
            var fast = NntpSessionPoolTests.CreatePool(
                fastFactory,
                max: 2,
                logger: fastLogger,
                shutdownGrace: TimeSpan.FromSeconds(2));
            try
            {
                await stalled.EnsureDesiredSessionsAsync(CancellationToken.None);
                await fast.EnsureDesiredSessionsAsync(CancellationToken.None);
                await MeasureAsync(() => stalled.DisposeAsync().AsTask(), stalledServers);

                await fast.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

                Assert.Equal(2, CountQuit(fastLogger));
                Assert.Equal(0, fast.LiveSessionCount);
            }
            finally
            {
                Release(stalledServers);
                await stalled.DisposeAsync();
                await fast.DisposeAsync();
            }
        }

        /// <summary>
        /// A replacement drain of many stalled sessions uses one retirement grace, not a fresh two-second QUIT for each session.
        /// </summary>
        /// <remarks>
        /// Primary shared-deadline proof: at most <see cref="NntpShutdownWork.Parallelism"/> QUITs start, fewer than
        /// <c>sessions</c>, and those writes cancel on the shared token. <see cref="AssertWithinCeiling"/> is the
        /// secondary wall-clock bound against stacking per-session QuitAllowance (2 s) waves (~4 s for eight sessions).
        /// </remarks>
        [Fact]
        public async Task Replacement_of_many_stalled_sessions_shares_one_retirement_deadline()
        {
            const int sessions = 8;
            var factory = new ScriptedNntpTransportFactory();
            var servers = EnqueueStalledQuitServers(factory, sessions);
            var logger = new CollectingLogger<NntpSessionPool>();
            var pool = NntpSessionPoolTests.CreatePool(factory, max: sessions, logger: logger, shutdownGrace: SharedBudget);
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                var elapsed = await MeasureAsync(
                    () => pool.DrainAndDisposeAsync(CancellationToken.None),
                    servers);
                var quitStarted = QuitStarts(servers);
                Report("replacement-sessions", SharedBudget, sessions, providers: 1, quitStarted, elapsed);

                Assert.InRange(quitStarted, 1, NntpShutdownWork.Parallelism);
                Assert.True(quitStarted < sessions);
                Assert.Equal(quitStarted, CountQuit(logger));
                AssertQuitCancelledByToken(logger.Messages, quitStarted);
                AssertWithinCeiling(elapsed);
                Assert.All(servers, static server => Assert.Equal(1, server.ClientStreamDisposeCount));
                Assert.Equal(0, pool.LiveSessionCount);
            }
            finally
            {
                Release(servers);
                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// A short replacement drain finishes on its own grace while a registry shutdown is still inside a longer deadline.
        /// Cancelling the registry caller then ends that shutdown without adopting the replacement budget.
        /// </summary>
        /// <remarks>
        /// Wall-clock acceptance is one standalone two-second QUIT allowance, not <see cref="SharedShutdownCeiling"/>.
        /// Concurrent registry <c>CancelAfter</c> load can delay the replacement timer past the shared scale ceiling without
        /// multiplying session budgets or adopting the registry's ten-second deadline. Four serial two-second QUITs or a
        /// ten-second shared registry budget would still fail this bound.
        /// </remarks>
        [Fact]
        public async Task Replacement_deadline_stays_independent_of_registry_shutdown()
        {
            var registryTransport = new ScriptedNntpTransportFactory();
            var registryServers = EnqueueStalledQuitServers(registryTransport, 2);
            var catalog = new ProviderConfigurationCatalog();
            await using var registry = CreateRegistry(catalog, registryTransport, TimeSpan.FromSeconds(10));
            var poolTransport = new ScriptedNntpTransportFactory();
            var poolServers = EnqueueStalledQuitServers(poolTransport, 4);
            var poolLogger = new CollectingLogger<NntpSessionPool>();
            var pool = NntpSessionPoolTests.CreatePool(
                poolTransport,
                max: 4,
                logger: poolLogger,
                shutdownGrace: SharedBudget);
            using var caller = new CancellationTokenSource();
            var stop = Task.CompletedTask;
            try
            {
                await registry.ApplySnapshotAsync(
                    [Definition("Giganews", 1), Definition("Eweka", 1)],
                    CancellationToken.None);
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

                stop = registry.StopAsync(caller.Token);
                await WaitUntilAsync(() => QuitStarts(registryServers) >= 1);
                Assert.False(stop.IsCompleted);

                // Measure with a QuitAllowance safety bound: SharedShutdownCeiling is for uncontended scale tests.
                var started = Stopwatch.GetTimestamp();
                var dispose = pool.DrainAndDisposeAsync(CancellationToken.None);
                TimeSpan elapsed;
                try
                {
                    await dispose.WaitAsync(TimeSpan.FromSeconds(2));
                    elapsed = Stopwatch.GetElapsedTime(started);
                }
                catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                {
                    Release(poolServers);
                    await dispose.WaitAsync(TimeSpan.FromSeconds(5));
                    throw;
                }

                var quitStarted = QuitStarts(poolServers);
                Report("replacement-beside-stop", SharedBudget, sessions: 4, providers: 1, quitStarted, elapsed);
                Assert.True(
                    elapsed < TimeSpan.FromSeconds(2),
                    $"elapsed {elapsed.TotalMilliseconds:0} ms must stay below one standalone QuitAllowance");
                Assert.False(stop.IsCompleted);
                Assert.InRange(quitStarted, 1, NntpShutdownWork.Parallelism);
                Assert.Equal(quitStarted, CountQuit(poolLogger));
                AssertQuitCancelledByToken(poolLogger.Messages, quitStarted);
                Assert.All(poolServers, static server => Assert.Equal(1, server.ClientStreamDisposeCount));

                await caller.CancelAsync();
                var cancelStarted = Stopwatch.GetTimestamp();
                await stop.WaitAsync(TimeSpan.FromSeconds(2));
                var cancelElapsed = Stopwatch.GetElapsedTime(cancelStarted);
                Assert.True(cancelElapsed < TimeSpan.FromSeconds(2), $"cancel elapsed {cancelElapsed.TotalMilliseconds:0} ms");
                Assert.All(registryServers, static server => Assert.Equal(1, server.ClientStreamDisposeCount));
            }
            finally
            {
                await caller.CancelAsync();
                Release(registryServers);
                Release(poolServers);
                await stop.WaitAsync(TimeSpan.FromSeconds(5));
                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// Replacement cancelled while ARTICLE is writing aborts that write and does not overlap it with QUIT.
        /// Idle sessions are not given a fresh QUIT allowance after the caller token is already cancelled.
        /// </summary>
        [Fact]
        public async Task Replacement_during_active_command_aborts_without_overlapping_quit()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            AbortingWriteStream? gate = null;
            var articleServer = new ScriptedNntpServer
            {
                ClientStreamDecorator = inner =>
                {
                    gate = new AbortingWriteStream(inner, entered);
                    return gate;
                },
            };
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(articleServer);
            var idleServers = EnqueueStalledQuitServers(factory, 2);
            var logger = new CollectingLogger<NntpSessionPool>();
            var pool = NntpSessionPoolTests.CreatePool(factory, max: 3, logger: logger, shutdownGrace: SharedBudget);
            NntpSessionLease? busy = null;
            Task<ArticleRetrievalResult>? download = null;
            try
            {
                busy = await pool.AcquireAsync(CancellationToken.None);
                var idleLease = await pool.AcquireAsync(CancellationToken.None);
                var secondIdle = await pool.AcquireAsync(CancellationToken.None);
                await idleLease.DisposeAsync();
                await secondIdle.DisposeAsync();
                Assert.NotNull(gate);
                gate.Arm();
                download = busy.Session.DownloadArticleAsync("<a@b>", CancellationToken.None);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                using var cancelled = new CancellationTokenSource();
                await cancelled.CancelAsync();

                var elapsed = await MeasureAsync(() => pool.DrainAndDisposeAsync(cancelled.Token), idleServers);
                Report("replacement-active", SharedBudget, sessions: 3, providers: 1, QuitStarts(idleServers), elapsed);
                using var result = await download.WaitAsync(TimeSpan.FromSeconds(2));

                AssertWithinCeiling(elapsed);
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
                Assert.Equal(1, gate.MaxWriteDepth);
                Assert.Equal(0, QuitStarts(idleServers));
                Assert.Equal(0, CountQuit(logger));
                Assert.Equal(1, articleServer.ClientStreamDisposeCount);
                Assert.All(idleServers, static server => Assert.Equal(1, server.ClientStreamDisposeCount));
            }
            finally
            {
                Release(idleServers);
                if (download is not null)
                {
                    try
                    {
                        using var leftover = await download.WaitAsync(TimeSpan.FromSeconds(2));
                    }
                    catch (Exception ex) when (ex is not (ArgumentNullException or NullReferenceException))
                    {
                        _output.WriteLine("replacement download cleanup: {0}", ex.GetType().Name);
                    }
                }

                if (busy is not null)
                {
                    await busy.DisposeAsync();
                }

                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// A replacement grace that is already expired aborts every session and does not start QUIT.
        /// </summary>
        [Fact]
        public async Task Expired_replacement_deadline_skips_quit()
        {
            const int sessions = 4;
            var factory = new ScriptedNntpTransportFactory();
            var servers = EnqueueStalledQuitServers(factory, sessions);
            var logger = new CollectingLogger<NntpSessionPool>();
            var pool = NntpSessionPoolTests.CreatePool(
                factory,
                max: sessions,
                logger: logger,
                shutdownGrace: TimeSpan.Zero);
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                var elapsed = await MeasureAsync(() => pool.DrainAndDisposeAsync(CancellationToken.None), servers);
                Report("replacement-expired", TimeSpan.Zero, sessions, providers: 1, QuitStarts(servers), elapsed);

                AssertWithinCeiling(elapsed);
                Assert.Equal(0, QuitStarts(servers));
                Assert.DoesNotContain(logger.Messages, static message => message.Contains("TX: QUIT", StringComparison.Ordinal));
                Assert.All(servers, static server => Assert.Equal(1, server.ClientStreamDisposeCount));
            }
            finally
            {
                Release(servers);
                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// Healthy idle sessions still send QUIT during replacement when the retirement grace has time left.
        /// </summary>
        [Fact]
        public async Task Responsive_replacement_still_sends_quit()
        {
            const int sessions = 3;
            var factory = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(factory, sessions);
            var logger = new CollectingLogger<NntpSessionPool>();
            var grace = TimeSpan.FromSeconds(2);
            var pool = NntpSessionPoolTests.CreatePool(factory, max: sessions, logger: logger, shutdownGrace: grace);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            var started = Stopwatch.GetTimestamp();
            await pool.DrainAndDisposeAsync(CancellationToken.None).WaitAsync(grace);
            var elapsed = Stopwatch.GetElapsedTime(started);
            Report("replacement-fast", grace, sessions, providers: 1, CountQuit(logger), elapsed);

            Assert.True(elapsed < grace, $"elapsed {elapsed.TotalMilliseconds:0} ms");
            Assert.Equal(sessions, CountQuit(logger));
            Assert.Equal(0, pool.LiveSessionCount);
        }

        /// <summary>
        /// A replenish delay that never fires does not hold replacement after the shutdown token is cancelled.
        /// </summary>
        /// <remarks>
        /// Responsive servers keep this case on the replenish-timer invariant alone.
        /// Stalled QUIT and shared-budget coverage stay in
        /// <see cref="Many_stalled_sessions_share_one_shutdown_budget"/> and the other replacement drain tests.
        /// Completing under <see cref="SharedShutdownCeiling"/> is well below the fifteen-second replenish interval.
        /// </remarks>
        [Fact]
        public async Task Replacement_does_not_wait_for_a_replenish_delay_that_ignores_its_timer()
        {
            const int sessions = 2;
            var factory = new ScriptedNntpTransportFactory();
            var servers = EnqueueReadyServers(factory, sessions);
            var clock = new NeverFiringTimeProvider();
            var pool = NntpSessionPoolTests.CreatePool(
                factory,
                max: sessions,
                time: clock,
                shutdownGrace: SharedBudget);
            var dispose = Task.CompletedTask;
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                await WaitUntilAsync(() => clock.TimersCreated >= 1);

                var started = Stopwatch.GetTimestamp();
                dispose = pool.DrainAndDisposeAsync(CancellationToken.None);
                await dispose.WaitAsync(SharedShutdownCeiling);
                var elapsed = Stopwatch.GetElapsedTime(started);
                Report("replacement-replenish", SharedBudget, sessions, providers: 1, quitStarted: 0, elapsed);

                AssertWithinCeiling(elapsed);
                Assert.True(
                    elapsed < TimeSpan.FromSeconds(15),
                    $"elapsed {elapsed.TotalMilliseconds:0} ms must stay below the replenish interval");
                Assert.All(servers, static server => Assert.Equal(1, server.ClientStreamDisposeCount));
                Assert.Equal(0, pool.LiveSessionCount);
            }
            finally
            {
                try
                {
                    await dispose.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    // Observed so an abandoned drain cannot outlive the test. The original failure stands.
                }

                await pool.DisposeAsync();
            }
        }

        /// <summary>Writes one measured shutdown line.</summary>
        /// <param name="name">Short test label.</param>
        /// <param name="budget">Deadline configured for that shutdown.</param>
        /// <param name="sessions">Sessions included in the shutdown.</param>
        /// <param name="providers">Providers included in the shutdown.</param>
        /// <param name="quitStarted">QUIT writes that entered the stall gate.</param>
        /// <param name="elapsed">Time from the start of the shutdown call until it completed.</param>
        private void Report(string name, TimeSpan budget, int sessions, int providers, int quitStarted, TimeSpan elapsed)
        {
            _output.WriteLine(
                "shutdown-budget name={0} budgetMs={1:0} sessions={2} providers={3} quitStarted={4} elapsedMs={5:0} ceilingMs={6:0}",
                name,
                budget.TotalMilliseconds,
                sessions,
                providers,
                quitStarted,
                elapsed.TotalMilliseconds,
                SharedShutdownCeiling.TotalMilliseconds);
        }

        /// <summary>Fails when <paramref name="elapsed"/> is not below <see cref="SharedShutdownCeiling"/>.</summary>
        /// <param name="elapsed">Measured shutdown duration.</param>
        private static void AssertWithinCeiling(TimeSpan elapsed)
        {
            Assert.True(
                elapsed < SharedShutdownCeiling,
                $"elapsed {elapsed.TotalMilliseconds:0} ms ceiling {SharedShutdownCeiling.TotalMilliseconds:0} ms");
        }

        /// <summary>
        /// Starts <paramref name="shutdown"/> and returns how long it ran.
        /// Releases stalled QUIT gates when the call exceeds <see cref="MeasureHangTimeout"/>, then waits for the same task to finish.
        /// </summary>
        /// <param name="shutdown">Starts the shutdown under test. Called once, after the clock starts.</param>
        /// <param name="servers">Servers whose QUIT gates must be released if shutdown does not finish.</param>
        /// <returns>Elapsed time of a shutdown that finished inside <see cref="MeasureHangTimeout"/>.</returns>
        /// <remarks>
        /// Callers that require the shared-budget proof still call <see cref="AssertWithinCeiling"/> on the returned elapsed time.
        /// </remarks>
        private static async Task<TimeSpan> MeasureAsync(Func<Task> shutdown, IReadOnlyList<ScriptedNntpServer> servers)
        {
            var started = Stopwatch.GetTimestamp();
            var dispose = shutdown();
            try
            {
                await dispose.WaitAsync(MeasureHangTimeout);
                return Stopwatch.GetElapsedTime(started);
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                Release(servers);
                await dispose.WaitAsync(TimeSpan.FromSeconds(5));
                throw;
            }
        }

        /// <summary>Queues <paramref name="count"/> servers that block QUIT until cancellation or <see cref="Release"/>.</summary>
        /// <param name="factory">Factory the pool or registry connects through.</param>
        /// <param name="count">How many independent stalled sessions to prepare.</param>
        /// <returns>The servers, in connect order.</returns>
        private static List<ScriptedNntpServer> EnqueueStalledQuitServers(ScriptedNntpTransportFactory factory, int count)
        {
            var servers = new List<ScriptedNntpServer>(count);
            for (var i = 0; i < count; i++)
            {
                var server = new ScriptedNntpServer
                {
                    HoldQuitWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                };
                AcceptAuthentication(server);
                factory.Enqueue(server);
                servers.Add(server);
            }

            return servers;
        }

        /// <summary>Queues <paramref name="count"/> servers that answer QUIT normally.</summary>
        /// <param name="factory">Factory the pool or registry connects through.</param>
        /// <param name="count">How many sessions to prepare.</param>
        /// <returns>The servers, in connect order.</returns>
        private static List<ScriptedNntpServer> EnqueueReadyServers(ScriptedNntpTransportFactory factory, int count)
        {
            var servers = new List<ScriptedNntpServer>(count);
            for (var i = 0; i < count; i++)
            {
                var server = new ScriptedNntpServer();
                AcceptAuthentication(server);
                factory.Enqueue(server);
                servers.Add(server);
            }

            return servers;
        }

        /// <summary>Installs the AUTHINFO response used by the pool's ready-server helper.</summary>
        /// <param name="server">Server that will accept the test session.</param>
        private static void AcceptAuthentication(ScriptedNntpServer server)
        {
            server.Respond(static command =>
                command.StartsWith("AUTHINFO", StringComparison.OrdinalIgnoreCase)
                    ? "281 authentication accepted\r\n"
                    : "430 missing\r\n");
        }

        /// <summary>Lets every stalled QUIT write continue.</summary>
        /// <param name="servers">Servers created by <see cref="EnqueueStalledQuitServers"/>.</param>
        private static void Release(IReadOnlyList<ScriptedNntpServer> servers)
        {
            foreach (var server in servers)
            {
                server.HoldQuitWrite?.TrySetResult();
            }
        }

        /// <summary>Counts QUIT writes that entered a stall gate.</summary>
        /// <param name="servers">Servers observed by the test.</param>
        /// <returns>The sum of <see cref="ScriptedNntpServer.QuitWriteStarted"/>.</returns>
        private static int QuitStarts(IReadOnlyList<ScriptedNntpServer> servers)
        {
            var count = 0;
            foreach (var server in servers)
            {
                count += server.QuitWriteStarted;
            }

            return count;
        }

        /// <summary>
        /// Asserts that every stalled QUIT was cancelled by its write token and that none failed as a socket or I/O error.
        /// </summary>
        /// <param name="messages">Wire logs from the sessions under test.</param>
        /// <param name="expected">How many QUIT writes started. Each one must log <c>error=TaskCanceledException</c>.</param>
        /// <remarks>
        /// <see cref="TaskCanceledException"/> is the runtime type thrown when the QUIT cancellation token fires.
        /// It derives from <see cref="OperationCanceledException"/>, which is the filter that contains the failure.
        /// The log stores the runtime type name, so the base type name is not the contract.
        /// </remarks>
        private static void AssertQuitCancelledByToken(IReadOnlyList<string> messages, int expected)
        {
            Assert.Equal(
                expected,
                messages.Count(static message => message.Contains(
                    "QUIT failed during dispose error=TaskCanceledException",
                    StringComparison.Ordinal)));
            Assert.DoesNotContain(
                messages,
                static message => message.Contains("QUIT failed during dispose error=SocketException", StringComparison.Ordinal)
                    || message.Contains("QUIT failed during dispose error=IOException", StringComparison.Ordinal));
        }

        /// <summary>Counts logged QUIT transmissions.</summary>
        /// <param name="logger">Pool logger that received the session wire logs.</param>
        /// <returns>How many debug messages contain <c>TX: QUIT</c>.</returns>
        private static int CountQuit(CollectingLogger<NntpSessionPool> logger) =>
            logger.Messages.Count(static message => message.Contains("TX: QUIT", StringComparison.Ordinal));

        /// <summary>Polls <paramref name="condition"/> until it is true, or throws after two seconds.</summary>
        /// <param name="condition">Observed state. Polled after each yield.</param>
        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!condition())
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        /// <summary>Builds a registry whose shutdown budget is <paramref name="shutdownGrace"/>.</summary>
        /// <param name="catalog">Catalog the snapshot updates.</param>
        /// <param name="transport">Scripted transport shared by every pool.</param>
        /// <param name="shutdownGrace">Absolute registry shutdown budget.</param>
        /// <returns>A registry that has not yet applied a snapshot.</returns>
        private static NntpProviderRegistry CreateRegistry(
            ProviderConfigurationCatalog catalog,
            ScriptedNntpTransportFactory transport,
            TimeSpan shutdownGrace)
        {
            return new NntpProviderRegistry(
                catalog,
                transport,
                NntpSessionOptions.Default with
                {
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    CommandTimeout = TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                },
                shutdownGrace,
                NullLogger<NntpProviderRegistry>.Instance);
        }

        /// <summary>Builds a cleartext provider with <paramref name="max"/> sessions.</summary>
        /// <param name="backbone">Pool key.</param>
        /// <param name="max">Maximum sessions the pool will establish.</param>
        /// <returns>The provider definition passed to <see cref="NntpProviderRegistry.ApplySnapshotAsync"/>.</returns>
        private static BackFillerProviderDefinition Definition(string backbone, int max) =>
            new(backbone, "127.0.0.1", 119, false, null, null, 0, max);

        /// <summary>
        /// Time provider whose timers never fire. Cancellation of <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> still completes the delay.
        /// </summary>
        private sealed class NeverFiringTimeProvider : TimeProvider
        {
            /// <summary>How many timers the replenish delay has created.</summary>
            private int _timersCreated;

            /// <summary>Gets how many timers have been created.</summary>
            internal int TimersCreated => Volatile.Read(ref _timersCreated);

            /// <summary>Returns a timer that does not invoke <paramref name="callback"/>.</summary>
            /// <param name="callback">Ignored. The test never releases this timer.</param>
            /// <param name="state">Ignored.</param>
            /// <param name="dueTime">Ignored.</param>
            /// <param name="period">Ignored.</param>
            /// <returns>A timer whose disposal does not complete the delay. The caller's cancellation token does.</returns>
            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                _ = callback;
                _ = state;
                _ = dueTime;
                _ = period;
                Interlocked.Increment(ref _timersCreated);
                return new SilentTimer();
            }

            /// <summary>A timer that accepts disposal and never fires.</summary>
            private sealed class SilentTimer : ITimer
            {
                /// <summary>Accepts a reschedule and does not fire.</summary>
                /// <param name="dueTime">Ignored.</param>
                /// <param name="period">Ignored.</param>
                /// <returns><see langword="true"/>.</returns>
                public bool Change(TimeSpan dueTime, TimeSpan period) => true;

                /// <summary>Releases nothing. The delay ends through its cancellation token.</summary>
                public void Dispose()
                {
                }

                /// <summary>Releases nothing. The delay ends through its cancellation token.</summary>
                /// <returns>A completed task.</returns>
                public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            }
        }
    }
}

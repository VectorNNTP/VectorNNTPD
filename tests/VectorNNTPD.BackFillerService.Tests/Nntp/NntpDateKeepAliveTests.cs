using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.Accounts;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Nntp
{
    /// <summary>
    /// DATE keep-alive on idle NNTP provider sessions: timing, health, failure recovery, and shutdown.
    /// </summary>

    public sealed class NntpDateKeepAliveTests
    {
        /// <summary>
        /// KeepAliveSeconds from provider configuration flows to transport connect and session.
        /// </summary>

        [Fact]

        public async Task Provider_and_session_construction_receive_the_mysql_keepalive()
        {
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(NntpSessionPoolTests.CreateDateAwareServer());
            await using var pool = NntpSessionPoolTests.CreatePool(factory, max: 1, keepAliveSeconds: 30);

            await using var lease = await pool.AcquireAsync(CancellationToken.None);

            Assert.Equal((byte)30, factory.ConnectAttempts[0].KeepAliveSeconds);
            Assert.Equal((byte)30, lease.Session.KeepAliveSeconds);
        }

        /// <summary>
        /// DATE is not sent before the idle interval elapses.
        /// </summary>

        [Fact]

        public async Task Date_is_issued_after_the_configured_idle_interval_and_not_before()
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
            var dateStarted = NewSource();
            var factory = new ScriptedNntpTransportFactory();
            var server = NntpSessionPoolTests.CreateDateAwareServer(dateStarted: dateStarted);
            factory.Enqueue(server);
            await using var pool = NntpSessionPoolTests.CreatePool(
                factory,
                max: 1,
                min: 1,
                keepAliveSeconds: 2,
                time: time);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            time.Advance(TimeSpan.FromSeconds(1));
            Assert.False(dateStarted.Task.IsCompleted);
            Assert.DoesNotContain(server.Commands, static command => command.Equals("DATE", StringComparison.OrdinalIgnoreCase));

            time.Advance(TimeSpan.FromSeconds(1));
            await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains(server.Commands, static command => command.Equals("DATE", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Idle admission registers the DATE timer before the keepalive loop resumes.
        /// A clock advance after admission therefore cannot miss a timer that does not exist yet.
        /// </summary>
        [Fact]
        public async Task Idle_admission_arms_the_date_timer_before_the_loop_resumes()
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
            var clock = new KeepAliveTimerCounter(time, TimeSpan.FromSeconds(2));
            var dateStarted = NewSource();
            var resumed = NewSource();
            var release = NewSource();
            var factory = new ScriptedNntpTransportFactory();
            var server = NntpSessionPoolTests.CreateDateAwareServer(dateStarted: dateStarted);
            factory.Enqueue(server);
            var pool = NntpSessionPoolTests.CreatePool(
                factory,
                max: 1,
                min: 1,
                keepAliveSeconds: 2,
                time: clock);
            var timersAtResume = -1;
            pool.BeforeFirstKeepAliveDelay = () =>
            {
                timersAtResume = clock.KeepAliveTimerCount;
                resumed.TrySetResult();
                return release.Task;
            };
            try
            {
                await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
                await resumed.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(1, timersAtResume);
                Assert.False(dateStarted.Task.IsCompleted);

                release.TrySetResult();
                time.Advance(TimeSpan.FromSeconds(1));
                Assert.False(dateStarted.Task.IsCompleted);
                Assert.DoesNotContain(server.Commands, static command => command.Equals("DATE", StringComparison.OrdinalIgnoreCase));

                time.Advance(TimeSpan.FromSeconds(1));
                await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Contains(server.Commands, static command => command.Equals("DATE", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                release.TrySetResult();
                pool.BeforeFirstKeepAliveDelay = null;
                await pool.DisposeAsync();
            }
        }

        /// <summary>
        /// Successful DATE leaves the session reusable for ARTICLE without extra connects.
        /// </summary>

        [Fact]

        public async Task Healthy_date_keeps_the_idle_session_reusable()
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
            var dateStarted = NewSource();
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(NntpSessionPoolTests.CreateDateAwareServer(dateStarted: dateStarted));
            await using var pool = NntpSessionPoolTests.CreatePool(
                factory,
                max: 1,
                min: 1,
                keepAliveSeconds: 3,
                time: time);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            time.Advance(TimeSpan.FromSeconds(3));
            await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await using var lease = await pool.AcquireAsync(CancellationToken.None);
            using var result = await lease.Session.DownloadArticleAsync("<a@b>", CancellationToken.None);
            Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, result.Kind);
            Assert.True(result.SessionReusable);
            Assert.True(lease.Session.IsReusable);
            Assert.Single(factory.ConnectAttempts);
        }

        /// <summary>
        /// Transport write failure during DATE retires the session and allows replacement.
        /// </summary>

        [Fact]

        public async Task Unexpected_date_write_failure_still_retires_the_session()
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
            var factory = new ScriptedNntpTransportFactory();
            var failing = NntpSessionPoolTests.CreateDateAwareServer();
            failing.FaultClientDateWrite = true;
            factory.Enqueue(failing);
            factory.Enqueue(NntpSessionPoolTests.CreateDateAwareServer());
            await using var pool = NntpSessionPoolTests.CreatePool(
                factory,
                max: 1,
                min: 1,
                keepAliveSeconds: 2,
                time: time);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            time.Advance(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => factory.ConnectAttempts.Count >= 2 && pool.ActiveSessionCount >= 1);

            await using var replacement = await pool.AcquireAsync(CancellationToken.None);
            Assert.Equal(2, factory.ConnectAttempts.Count);
            Assert.True(replacement.Session.IsReusable);
        }

        /// <summary>
        /// Non-success DATE response retires the session and replenishes the pool.
        /// </summary>

        [Fact]

        public async Task Date_protocol_failure_retires_the_session()
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
            var dateStarted = NewSource();
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(NntpSessionPoolTests.CreateDateAwareServer("400 no\r\n", dateStarted));
            factory.Enqueue(NntpSessionPoolTests.CreateDateAwareServer());
            await using var pool = NntpSessionPoolTests.CreatePool(
                factory,
                max: 1,
                min: 1,
                keepAliveSeconds: 2,
                time: time);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            time.Advance(TimeSpan.FromSeconds(2));
            await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => factory.ConnectAttempts.Count >= 2 && pool.ActiveSessionCount >= 1);

            await using var replacement = await pool.AcquireAsync(CancellationToken.None);
            Assert.Equal(2, factory.ConnectAttempts.Count);
            Assert.True(replacement.Session.IsReusable);
        }

        /// <summary>
        /// DATE command timeout marks the session not reusable.
        /// </summary>

        [Fact]

        public async Task Date_timeout_marks_the_session_unhealthy()
        {
            var dateStarted = NewSource();
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(
                NntpSessionPoolTests.CreateDateAwareServer(
                    dateStarted: dateStarted,
                    blockDate: NewSource()));
            var session = new NntpProviderSession(
                new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, null, null, 0, 1, 1),
                NntpSessionOptions.Default with
                {
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    CommandTimeout = TimeSpan.FromMilliseconds(80),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                },
                NullLogger.Instance);
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

            var date = session.SendDateKeepAliveAsync(CancellationToken.None);
            await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(await date.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(session.IsReusable);
            await session.DisposeAsync();
        }

        /// <summary>
        /// Peer EOF during DATE retires the session and triggers reconnect.
        /// </summary>

        [Fact]

        public async Task Date_eof_retires_the_session()
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
            var dateStarted = NewSource();
            var factory = new ScriptedNntpTransportFactory();
            var server = NntpSessionPoolTests.CreateDateAwareServer(dateStarted: dateStarted);
            server.CompleteAfterDateWithoutResponse = true;
            factory.Enqueue(server);
            factory.Enqueue(NntpSessionPoolTests.CreateDateAwareServer());
            await using var pool = NntpSessionPoolTests.CreatePool(
                factory,
                max: 1,
                min: 1,
                keepAliveSeconds: 1,
                time: time);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            time.Advance(TimeSpan.FromSeconds(1));
            await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => factory.ConnectAttempts.Count >= 2 && pool.ActiveSessionCount >= 1);

            await using var replacement = await pool.AcquireAsync(CancellationToken.None);
            Assert.Equal(2, factory.ConnectAttempts.Count);
        }

        /// <summary>
        /// Active ARTICLE suppresses concurrent DATE on the same session.
        /// </summary>

        [Fact]

        public async Task Article_on_a_leased_session_does_not_run_date()
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
            var articleStarted = NewSource();
            var blockArticle = NewSource();
            var factory = new ScriptedNntpTransportFactory();
            var server = NntpSessionPoolTests.CreateDateAwareServer(
                articleStarted: articleStarted,
                blockArticle: blockArticle);
            factory.Enqueue(server);
            await using var pool = NntpSessionPoolTests.CreatePool(
                factory,
                max: 1,
                keepAliveSeconds: 1,
                time: time);

            await using var lease = await pool.AcquireAsync(CancellationToken.None);
            var download = lease.Session.DownloadArticleAsync("<a@b>", CancellationToken.None);
            await articleStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            time.Advance(TimeSpan.FromSeconds(5));
            Assert.True(await lease.Session.SendDateKeepAliveAsync(CancellationToken.None));
            Assert.DoesNotContain(server.Commands, static command => command.Equals("DATE", StringComparison.OrdinalIgnoreCase));

            blockArticle.TrySetResult();
            using var result = await download.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, result.Kind);
            Assert.DoesNotContain(server.Commands, static command => command.Equals("DATE", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Pool disposal cancels pending DATE without completing it and clears sessions.
        /// </summary>

        [Fact]

        public async Task Shutdown_cancels_a_pending_keepalive_without_leaking_the_session()
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
            var dateStarted = NewSource();
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(NntpSessionPoolTests.CreateDateAwareServer(dateStarted: dateStarted));
            var pool = NntpSessionPoolTests.CreatePool(
                factory,
                max: 1,
                min: 1,
                keepAliveSeconds: 30,
                time: time);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            await pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(dateStarted.Task.IsCompleted);
            Assert.Equal(0, pool.LiveSessionCount);
            Assert.Equal(0, pool.ActiveLeaseCount);
        }

        /// <summary>
        /// Disposing the pool interrupts an in-flight DATE and clears live sessions.
        /// </summary>

        [Fact]

        public async Task Shutdown_interrupts_an_in_flight_date()
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
            var dateStarted = NewSource();
            var factory = new ScriptedNntpTransportFactory();
            var server = NntpSessionPoolTests.CreateDateAwareServer(
                dateStarted: dateStarted,
                blockDate: NewSource());
            factory.Enqueue(server);
            var pool = NntpSessionPoolTests.CreatePool(
                factory,
                max: 1,
                min: 1,
                keepAliveSeconds: 1,
                time: time,
                commandTimeout: TimeSpan.FromSeconds(2));
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            time.Advance(TimeSpan.FromSeconds(1));
            await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, pool.LiveSessionCount);
            Assert.Equal(0, pool.ActiveLeaseCount);
        }

        /// <summary>
        /// Configuration snapshot updates KeepAliveSeconds for new pools while existing leases keep prior values until released.
        /// </summary>

        [Fact]

        public async Task Replacement_applies_new_keepalive_to_new_sessions_and_leaves_leased_sessions_intact()
        {
            var catalog = new ProviderConfigurationCatalog();
            var transport = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(transport, count: 2);
            await using var registry = new NntpProviderRegistry(
                catalog,
                transport,
                NntpSessionOptions.Default with
                {
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    CommandTimeout = TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                },
                TimeSpan.FromSeconds(2),
                NullLogger<NntpProviderRegistry>.Instance);
            var original = new BackFillerProviderDefinition(
                "Giganews",
                "news.example.test",
                563,
                true,
                "nntp-user",
                "p",
                0,
                1,
                KeepAliveSeconds: 5);
            await registry.ApplySnapshotAsync([original], CancellationToken.None);
            Assert.True(registry.TryGetPool("Giganews", out var oldPool));
            var lease = await oldPool.AcquireAsync(CancellationToken.None);
            Assert.Equal((byte)5, lease.Session.KeepAliveSeconds);

            var replacement = original with { KeepAliveSeconds = 10 };
            var applying = registry.ApplySnapshotAsync([replacement], CancellationToken.None);
            Assert.Equal(NntpSessionState.Ready, lease.Session.State);
            Assert.Equal((byte)5, lease.Session.KeepAliveSeconds);
            Assert.True(registry.TryGetPool("Giganews", out var newPool));
            Assert.NotSame(oldPool, newPool);
            Assert.Equal((byte)10, newPool.Provider.KeepAliveSeconds);

            await using var created = await newPool.AcquireAsync(CancellationToken.None);
            Assert.Equal((byte)10, created.Session.KeepAliveSeconds);
            await created.DisposeAsync();

            await lease.DisposeAsync();
            await applying;
            Assert.True(registry.TryGetPool("Giganews", out var after));
            Assert.Same(newPool, after);
            Assert.Equal((byte)10, after.Provider.KeepAliveSeconds);
        }

        /// <summary>
        /// KeepAliveSeconds zero disables DATE commands entirely.
        /// </summary>

        [Fact]

        public async Task Zero_keepalive_does_not_issue_date()
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
            var factory = new ScriptedNntpTransportFactory();
            var server = NntpSessionPoolTests.CreateDateAwareServer();
            factory.Enqueue(server);
            await using var pool = NntpSessionPoolTests.CreatePool(
                factory,
                max: 1,
                min: 1,
                keepAliveSeconds: 0,
                time: time);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);

            time.Advance(TimeSpan.FromSeconds(30));
            Assert.DoesNotContain(server.Commands, static command => command.Equals("DATE", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Creates a run-continuations-asynchronously TaskCompletionSource for DATE synchronization.
        /// </summary>

        private static TaskCompletionSource NewSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);


        /// <summary>
        /// Polls until <paramref name="predicate"/> is true, or throws after two seconds.
        /// </summary>
        /// <param name="predicate">Condition the test is waiting to observe.</param>
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
        /// Counts keepalive timers and forwards every timer to a manual clock the test advances.
        /// </summary>
        /// <param name="clock">Clock whose <see cref="ManualTimeProvider.Advance"/> fires the forwarded timers.</param>
        /// <param name="keepAliveInterval">Due time that counts as the DATE idle interval.</param>
        private sealed class KeepAliveTimerCounter(ManualTimeProvider clock, TimeSpan keepAliveInterval) : TimeProvider
        {
            /// <summary>Timers created with the DATE idle interval.</summary>
            private int _keepAliveTimers;

            /// <summary>Timers created with the DATE idle interval.</summary>
            internal int KeepAliveTimerCount => Volatile.Read(ref _keepAliveTimers);

            /// <inheritdoc />
            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime == keepAliveInterval)
                {
                    Interlocked.Increment(ref _keepAliveTimers);
                }

                return clock.CreateTimer(callback, state, dueTime, period);
            }
        }
    }
}

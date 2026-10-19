using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Nntp
{
    /// <summary>
    /// Command write, status read, and payload read keep separate timeout budgets, and idle lease acquisition stays consistent.
    /// </summary>
    public sealed class NntpCommandBudgetTests
    {
        /// <summary>
        /// A write that uses most of the command budget and a status read that uses most of it again both succeed.
        /// One shared budget starting at the write would already have expired.
        /// </summary>
        [Fact]
        public async Task Write_and_status_each_get_a_command_timeout()
        {
            var server = new ScriptedNntpServer();
            server.Respond(static _ => "220 0 <id@example.invalid> article follows\r\nFrom: a@b\r\n\r\nbody\r\n.\r\n");
            var session = await ConnectAsync(server, commandTimeout: TimeSpan.FromMilliseconds(1500));
            var writeHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writeFlushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var statusHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var statusCopied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.HoldAfterCommandWrite = writeHold;
            server.CommandWriteFlushed = writeFlushed;
            server.HoldStatusLineReturn = statusHold;
            server.StatusLineCopied = statusCopied;
            try
            {
                var download = session.DownloadArticleAsync("<id@example.invalid>", CancellationToken.None);
                await writeFlushed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(1000);
                writeHold.TrySetResult();
                await statusCopied.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(1000);
                statusHold.TrySetResult();
                using var result = await download.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, result.Kind);
                Assert.Equal(string.Empty, result.Reason);
                Assert.True(result.SessionReusable);
            }
            finally
            {
                writeHold.TrySetResult();
                statusHold.TrySetResult();
                await session.DisposeAsync();
            }
        }

        /// <summary>A stalled command write is provider failure with the write-timeout reason and retires the session.</summary>
        [Fact]
        public async Task Stalled_write_is_command_timeout_and_not_reusable()
        {
            var server = new ScriptedNntpServer();
            server.Respond(static _ => "220 follows\r\nFrom: a@b\r\n\r\nbody\r\n.\r\n");
            var session = await ConnectAsync(server, commandTimeout: TimeSpan.FromMilliseconds(200));
            var writeHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writeFlushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.HoldAfterCommandWrite = writeHold;
            server.CommandWriteFlushed = writeFlushed;
            server.ObserveCommandHoldCancellation = true;
            try
            {
                using var result = await session.DownloadArticleAsync("<id@example.invalid>", CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
                Assert.Equal("ARTICLE timed out.", result.Reason);
                Assert.False(result.SessionReusable);
            }
            finally
            {
                writeHold.TrySetResult();
                await session.DisposeAsync();
            }
        }

        /// <summary>A stalled status read is reported as TimeoutException and retires the session.</summary>
        [Fact]
        public async Task Stalled_status_read_is_timeout_exception_and_not_reusable()
        {
            var server = new ScriptedNntpServer();
            server.Respond(static _ => "220 follows\r\nFrom: a@b\r\n\r\nbody\r\n.\r\n");
            var session = await ConnectAsync(server, commandTimeout: TimeSpan.FromMilliseconds(200));
            var statusHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var statusCopied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.HoldStatusLineReturn = statusHold;
            server.StatusLineCopied = statusCopied;
            server.ObserveCommandHoldCancellation = true;
            try
            {
                var download = session.DownloadArticleAsync("<id@example.invalid>", CancellationToken.None);
                await statusCopied.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var result = await download.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
                Assert.Equal(nameof(TimeoutException), result.Reason);
                Assert.False(result.SessionReusable);
            }
            finally
            {
                statusHold.TrySetResult();
                await session.DisposeAsync();
            }
        }

        /// <summary>Caller cancellation during the status read stays Cancelled rather than a timeout.</summary>
        [Fact]
        public async Task Caller_cancel_during_status_read_is_cancelled()
        {
            var server = new ScriptedNntpServer();
            server.Respond(static _ => "430 No such article\r\n");
            var session = await ConnectAsync(server, commandTimeout: TimeSpan.FromSeconds(2));
            var statusHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var statusCopied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.HoldStatusLineReturn = statusHold;
            server.StatusLineCopied = statusCopied;
            server.ObserveCommandHoldCancellation = true;
            using var caller = new CancellationTokenSource();
            try
            {
                var download = session.DownloadArticleAsync("<id@example.invalid>", caller.Token);
                await statusCopied.Task.WaitAsync(TimeSpan.FromSeconds(5));
                caller.Cancel();
                using var result = await download.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ArticleRetrievalKind.Cancelled, result.Kind);
                Assert.Equal("ARTICLE was cancelled.", result.Reason);
                Assert.False(result.SessionReusable);
            }
            finally
            {
                statusHold.TrySetResult();
                await session.DisposeAsync();
            }
        }

        /// <summary>The payload budget is the receive timeout, independent of the command timeout.</summary>
        [Fact]
        public async Task Payload_timeout_uses_the_receive_budget()
        {
            var bodyHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var statusWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = new ScriptedNntpServer
            {
                HoldArticleBody = bodyHold,
                ArticleStatusWritten = statusWritten,
            };
            var session = await ConnectAsync(
                server,
                commandTimeout: TimeSpan.FromSeconds(2),
                receiveTimeout: TimeSpan.FromMilliseconds(200));
            try
            {
                var download = session.DownloadArticleAsync("<id@example.invalid>", CancellationToken.None);
                await statusWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var result = await download.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
                Assert.Equal("ARTICLE receive timed out.", result.Reason);
                Assert.False(result.SessionReusable);
            }
            finally
            {
                bodyHold.TrySetResult();
                await session.DisposeAsync();
            }
        }

        /// <summary>A 430 reason keeps the status text. A 220 success does not.</summary>
        [Fact]
        public async Task Not_found_keeps_status_text_and_success_does_not()
        {
            var missing = new ScriptedNntpServer();
            missing.Respond(static _ => "430 No such article\r\n");
            var missingSession = await ConnectAsync(missing);
            using (var missed = await missingSession.DownloadArticleAsync("<id@example.invalid>", CancellationToken.None))
            {
                Assert.Equal(ArticleRetrievalKind.ArticleNotFound, missed.Kind);
                Assert.Equal("No such article", missed.Reason);
                Assert.True(missed.SessionReusable);
            }

            await missingSession.DisposeAsync();

            var found = new ScriptedNntpServer();
            found.Respond(static _ => "220 0 <id@example.invalid> article follows\r\nFrom: a@b\r\n\r\nbody\r\n.\r\n");
            var foundSession = await ConnectAsync(found);
            using (var hit = await foundSession.DownloadArticleAsync("<id@example.invalid>", CancellationToken.None))
            {
                Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, hit.Kind);
                Assert.Equal(string.Empty, hit.Reason);
            }

            await foundSession.DisposeAsync();
        }

        /// <summary>Reused idle acquisition, caller cancellation, and shutdown leave permit accounting consistent.</summary>
        [Fact]
        public async Task Idle_reuse_cancellation_and_shutdown_keep_permit_counts()
        {
            var factory = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(factory, 1);
            var pool = NntpSessionPoolTests.CreatePool(factory, max: 1);
            var shutdown = Assert.IsType<CancellationTokenSource>(
                typeof(NntpSessionPool).GetField("_shutdown", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool));

            await using (var first = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)))
            {
                Assert.Equal(NntpSessionState.Ready, first.Session.State);
                Assert.True(first.Session.IsReusable);
            }

            await using (var reused = await pool.AcquireAsync(shutdown.Token).WaitAsync(TimeSpan.FromSeconds(5)))
            {
                Assert.Equal(NntpSessionState.Ready, reused.Session.State);
                Assert.True(reused.Session.IsReusable);
                Assert.Single(factory.ConnectAttempts);
            }

            Assert.Equal(1, pool.AvailablePermitCount);
            Assert.Equal(0, pool.PermitWaiterCount);
            Assert.Equal(0, pool.PermitReservationCount);

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await using var immediate = await pool.AcquireAsync(cancelled.Token).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(NntpSessionState.Ready, immediate.Session.State);
                Assert.True(immediate.Session.IsReusable);
            }

            Assert.Equal(1, pool.AvailablePermitCount);
            Assert.Equal(0, pool.PermitWaiterCount);
            Assert.Equal(0, pool.PermitReservationCount);
            Assert.Single(factory.ConnectAttempts);

            var held = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            using (var alreadyCancelled = new CancellationTokenSource())
            {
                alreadyCancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.AcquireAsync(alreadyCancelled.Token).WaitAsync(TimeSpan.FromSeconds(5)));
            }

            Assert.Equal(0, pool.PermitWaiterCount);
            Assert.Equal(0, pool.AvailablePermitCount);
            Assert.Equal(1, pool.PermitReservationCount);

            using var caller = new CancellationTokenSource();
            var waitingCaller = pool.AcquireAsync(caller.Token);
            await WaitUntilAsync(() => pool.PermitWaiterCount == 1);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingCaller.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, pool.PermitWaiterCount);
            Assert.Equal(0, pool.AvailablePermitCount);
            Assert.Equal(1, pool.PermitReservationCount);

            var waitingShutdown = pool.AcquireAsync(CancellationToken.None);
            await WaitUntilAsync(() => pool.PermitWaiterCount == 1);
            var disposing = pool.DisposeAsync();
            await held.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await disposing.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            var idleShutdownError = await Record.ExceptionAsync(() => waitingShutdown.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(idleShutdownError is OperationCanceledException or ObjectDisposedException);
            Assert.Equal(0, pool.PermitWaiterCount);

            var concurrentFactory = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(concurrentFactory, 1);
            var concurrentPool = NntpSessionPoolTests.CreatePool(concurrentFactory, max: 1);
            var concurrentHeld = await concurrentPool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            using var concurrentCaller = new CancellationTokenSource();
            var concurrentCancel = concurrentPool.AcquireAsync(concurrentCaller.Token);
            var concurrentShutdown = concurrentPool.AcquireAsync(CancellationToken.None);
            await WaitUntilAsync(() => concurrentPool.PermitWaiterCount == 2);
            concurrentCaller.Cancel();
            var concurrentDispose = concurrentPool.DisposeAsync();
            await concurrentHeld.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await concurrentDispose.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            var cancelError = await Record.ExceptionAsync(() => concurrentCancel.WaitAsync(TimeSpan.FromSeconds(5)));
            var shutdownError = await Record.ExceptionAsync(() => concurrentShutdown.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(
                cancelError is OperationCanceledException or ObjectDisposedException,
                cancelError?.ToString() ?? "caller acquire completed without an exception");
            Assert.True(
                shutdownError is OperationCanceledException or ObjectDisposedException,
                shutdownError?.ToString() ?? "shutdown acquire completed without an exception");
            Assert.Equal(0, concurrentPool.PermitWaiterCount);
            Assert.Equal(0, concurrentPool.PermitWaitersAtSemaphoreDispose);
        }

        /// <summary>
        /// Shutdown cancellation can return while a permit waiter is still inside the semaphore.
        /// Dispose waits for that waiter to leave before it disposes the semaphore.
        /// </summary>
        [Fact]
        public async Task Shutdown_drains_a_permit_waiter_still_queued_after_cancellation()
        {
            var sawBlockedWaiter = false;
            for (var attempt = 0; attempt < 40 && !sawBlockedWaiter; attempt++)
            {
                sawBlockedWaiter = await RunConcurrentShutdownOnceAsync();
            }

            Assert.True(sawBlockedWaiter, "shutdown cancellation did not leave a permit waiter queued");
        }

        private static async Task<bool> RunConcurrentShutdownOnceAsync()
        {
            var factory = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(factory, 1);
            var pool = NntpSessionPoolTests.CreatePool(factory, max: 1);
            var held = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            using var caller = new CancellationTokenSource();
            var cancelAcquire = pool.AcquireAsync(caller.Token);
            var shutdownAcquire = pool.AcquireAsync(CancellationToken.None);
            await WaitUntilAsync(() => pool.PermitWaiterCount == 2);
            var sawBlockedWaiter = false;
            pool.BeforePermitWaiterDrain = () =>
            {
                sawBlockedWaiter = true;
                return Task.CompletedTask;
            };
            caller.Cancel();
            var disposing = pool.DisposeAsync();
            await held.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await disposing.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            var cancelError = await Record.ExceptionAsync(() => cancelAcquire.WaitAsync(TimeSpan.FromSeconds(5)));
            var shutdownError = await Record.ExceptionAsync(() => shutdownAcquire.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(
                cancelError is OperationCanceledException or ObjectDisposedException,
                cancelError?.ToString() ?? "caller acquire completed without an exception");
            Assert.True(
                shutdownError is OperationCanceledException or ObjectDisposedException,
                shutdownError?.ToString() ?? "shutdown acquire completed without an exception");
            Assert.Equal(0, pool.PermitWaiterCount);
            Assert.Equal(0, pool.PermitWaitersAtSemaphoreDispose);
            return sawBlockedWaiter;
        }

        private static async Task<NntpProviderSession> ConnectAsync(
            ScriptedNntpServer server,
            TimeSpan? commandTimeout = null,
            TimeSpan? receiveTimeout = null)
        {
            var factory = new ScriptedNntpTransportFactory();
            factory.Enqueue(server);
            var session = new NntpProviderSession(
                new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, null, null, 0, 1),
                NntpSessionOptions.Default with
                {
                    CommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(2),
                    ReceiveTimeout = receiveTimeout ?? TimeSpan.FromSeconds(2),
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                },
                NullLogger.Instance);
            Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
            return session;
        }

        private static async Task WaitUntilAsync(Func<bool> predicate)
        {
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!predicate())
            {
                safety.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }
    }
}

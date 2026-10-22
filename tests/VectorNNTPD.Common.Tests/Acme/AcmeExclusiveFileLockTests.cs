using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Tests.TestDoubles;

namespace VectorNNTPD.Common.Tests.Acme
{
    /// <summary>
    /// Regressions for exclusive ACME file-lock contention classification (issue #184).
    /// </summary>
    public sealed class AcmeExclusiveFileLockTests
    {
        /// <summary>
        /// A second exclusive open of a held lock path returns <see langword="false"/> from
        /// <see cref="AcmeExclusiveFileLock.TryAcquireOnce"/> instead of throwing, proving the
        /// platform surfaces a recognized contention condition.
        /// </summary>
        [Fact]
        public void Held_lock_is_recognized_as_contention_not_immediate_failure()
        {
            using var dir = new TempStateDir();
            var path = Path.Join(dir.Path, "held.lock");
            using var held = AcmeExclusiveFileLock.Acquire(path);

            Assert.False(AcmeExclusiveFileLock.TryAcquireOnce(path, out AcmeExclusiveFileLock? second));
            Assert.Null(second);
        }

        /// <summary>
        /// Cancellation while another holder owns the lock exits promptly with
        /// <see cref="OperationCanceledException"/> and does not acquire the lock.
        /// </summary>
        /// <returns>A task that completes after the cancelled acquire is observed.</returns>
        [Fact]
        public async Task Cancellation_while_waiting_for_held_lock_exits_promptly()
        {
            using var dir = new TempStateDir();
            var path = Path.Join(dir.Path, "wait.lock");
            using var held = AcmeExclusiveFileLock.Acquire(path);
            using var cts = new CancellationTokenSource();

            var waiting = AcmeExclusiveFileLock.AcquireAsync(path, cts.Token);
            Assert.False(AcmeExclusiveFileLock.TryAcquireOnce(path, out _));
            Assert.False(waiting.IsCompleted);

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }

        /// <summary>
        /// When the lock path is a directory, acquisition fails immediately with
        /// <see cref="UnauthorizedAccessException"/> and is not classified as contention.
        /// </summary>
        [Fact]
        public void Path_that_is_a_directory_fails_promptly_without_contention_retry()
        {
            using var dir = new TempStateDir();
            var path = Path.Join(dir.Path, "as-directory.lock");
            Directory.CreateDirectory(path);

            var ex = Assert.ThrowsAny<Exception>(() => AcmeExclusiveFileLock.Acquire(path, CancellationToken.None));
            Assert.IsType<UnauthorizedAccessException>(ex);
            Assert.False(AcmeExclusiveFileLock.IsLockContention(ex));
        }

        /// <summary>
        /// <see cref="UnauthorizedAccessException"/> is never treated as lock contention.
        /// </summary>
        [Fact]
        public void UnauthorizedAccessException_is_not_lock_contention()
        {
            Assert.False(AcmeExclusiveFileLock.IsLockContention(new UnauthorizedAccessException("denied")));
        }

        /// <summary>
        /// Disk-full shaped <see cref="IOException"/> values are not treated as contention on any platform.
        /// </summary>
        [Fact]
        public void Unrelated_io_failures_are_not_lock_contention()
        {
            // ENOSPC = 28 on Linux; ERROR_DISK_FULL / HRESULT-style codes must not match sharing codes.
            Assert.False(AcmeExclusiveFileLock.IsLockContention(new IOException("disk full", 28)));
            Assert.False(AcmeExclusiveFileLock.IsLockContention(new IOException("disk full", unchecked((int)0x80070070))));
            Assert.False(AcmeExclusiveFileLock.IsLockContention(new IOException("generic")));
        }

        /// <summary>
        /// An initialization failure after a successful open propagates immediately, does not enter the
        /// contention retry loop, and disposes the opened stream.
        /// </summary>
        /// <param name="failure">Operational or programming failure thrown during lock-file initialization.</param>
        [Theory]
        [MemberData(nameof(InitializationFailures))]
        public void Initialization_failure_propagates_and_disposes_stream(Exception failure)
        {
            using var dir = new TempStateDir();
            var path = Path.Join(dir.Path, "init-fail.lock");
            var disposed = 0;
            var openCount = 0;

            AcmeExclusiveFileLock.CreateStreamForTests = p =>
            {
                Interlocked.Increment(ref openCount);
                return new TrackingFileStream(p, () => Interlocked.Increment(ref disposed));
            };
            AcmeExclusiveFileLock.InitializeLockFileForTests = _ => throw failure;

            try
            {
                var ex = Assert.Throws(failure.GetType(), () => AcmeExclusiveFileLock.Acquire(path, CancellationToken.None));
                Assert.Same(failure, ex);
                Assert.Equal(1, Volatile.Read(ref openCount));
                Assert.Equal(1, Volatile.Read(ref disposed));
            }
            finally
            {
                AcmeExclusiveFileLock.CreateStreamForTests = null;
                AcmeExclusiveFileLock.InitializeLockFileForTests = null;
            }
        }

        /// <summary>
        /// Representative initialization failures that must dispose the open stream and must not become
        /// contention retries (ownership-transfer <c>finally</c>, not a filtered catch-all).
        /// </summary>
        public static TheoryData<Exception> InitializationFailures() =>
        [
            new IOException("simulated metadata write failure", unchecked((int)0x80070070)),
            new InvalidOperationException("simulated programming failure during lock init"),
            new UnauthorizedAccessException("simulated access denied during lock init"),
        ];

        /// <summary>
        /// Synchronous and asynchronous issuance-lock acquisition classify a directory path the same way.
        /// </summary>
        /// <returns>A task that completes after both sync and async failures are observed.</returns>
        [Fact]
        public async Task Issuance_lock_sync_and_async_classify_directory_path_consistently()
        {
            using var dir = new TempStateDir();
            const string fqdn = "backfiller01.usenet.ninja";
            AcmePaths.EnsureCertificateIdentityLayout(dir.Path, fqdn);
            var lockPath = AcmePaths.IssuanceLockPath(dir.Path, fqdn);
            if (File.Exists(lockPath))
            {
                File.Delete(lockPath);
            }

            Directory.CreateDirectory(lockPath);

            var syncEx = Assert.ThrowsAny<Exception>(
                () => AcmeIssuanceLock.Acquire(dir.Path, fqdn, CancellationToken.None));
            var asyncEx = await Assert.ThrowsAnyAsync<Exception>(
                () => AcmeIssuanceLock.AcquireAsync(dir.Path, fqdn, CancellationToken.None));

            Assert.IsType<UnauthorizedAccessException>(syncEx);
            Assert.IsType<UnauthorizedAccessException>(asyncEx);
            Assert.False(AcmeExclusiveFileLock.IsLockContention(syncEx));
            Assert.False(AcmeExclusiveFileLock.IsLockContention(asyncEx));
        }

        /// <summary>
        /// <see cref="AcmeIssuanceLock.TryAcquireOnce"/> returns <see langword="false"/> under genuine contention
        /// and does not throw, matching <see cref="AcmeExclusiveFileLock.TryAcquireOnce"/>.
        /// </summary>
        [Fact]
        public void Issuance_lock_try_acquire_once_returns_false_on_contention()
        {
            using var dir = new TempStateDir();
            const string fqdn = "nntpd01.usenet.ninja";
            using var held = AcmeIssuanceLock.Acquire(dir.Path, fqdn);

            Assert.False(AcmeIssuanceLock.TryAcquireOnce(dir.Path, fqdn, out AcmeIssuanceLock? second));
            Assert.Null(second);
        }

        /// <summary>
        /// After a holder releases the lock, a waiting acquire completes successfully.
        /// </summary>
        /// <returns>A task that completes after the waiter observes the release.</returns>
        [Fact]
        public async Task Waiting_acquire_succeeds_after_holder_releases()
        {
            using var dir = new TempStateDir();
            var path = Path.Join(dir.Path, "release.lock");

            Task<AcmeExclusiveFileLock>? waiting = null;
            try
            {
                using (AcmeExclusiveFileLock.Acquire(path))
                {
                    waiting = AcmeExclusiveFileLock.AcquireAsync(path, CancellationToken.None);
                    Assert.False(AcmeExclusiveFileLock.TryAcquireOnce(path, out _));
                    Assert.False(waiting.IsCompleted);
                }

                var acquired = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
                waiting = null;
                using (acquired)
                {
                    Assert.NotNull(acquired);
                }
            }
            finally
            {
                if (waiting is not null)
                {
                    // Holder was released above; settle the waiter and dispose its result.
                    (await waiting).Dispose();
                }
            }
        }

        /// <summary>
        /// <see cref="FileStream"/> that records dispose for lock-initialization fault tests.
        /// </summary>
        private sealed class TrackingFileStream : FileStream
        {
            private readonly Action _onDispose;
            private int _disposed;

            /// <summary>Opens <paramref name="path"/> exclusively and invokes <paramref name="onDispose"/> once on dispose.</summary>
            /// <param name="path">Lock file path.</param>
            /// <param name="onDispose">Callback invoked the first time this stream is disposed.</param>
            public TrackingFileStream(string path, Action onDispose)
                : base(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 4096, FileOptions.None)
            {
                _onDispose = onDispose;
            }

            /// <inheritdoc />
            protected override void Dispose(bool disposing)
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _onDispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}

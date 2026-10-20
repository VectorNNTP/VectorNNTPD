using System.Globalization;
using System.Text;

namespace VectorNNTPD.Common.Acme
{
    /// <summary>
    /// Cross-process exclusive lock for one filesystem path.
    /// </summary>
    /// <remarks>
    /// The lock is a <see cref="FileStream"/> opened with <see cref="FileShare.None"/>.
    /// Distinct paths are independent. The same path is mutually exclusive across
    /// processes and AppDomains. Callers wait until the lock is available or cancelled.
    /// Only a recognized sharing/lock-contention condition is retried; unrelated
    /// filesystem failures (disk full, access denied, path is a directory, and similar)
    /// propagate immediately. Metadata writes after a successful open are never treated
    /// as contention.
    /// </remarks>
    internal sealed class AcmeExclusiveFileLock : IDisposable
    {
        /// <summary>Win32 <c>ERROR_SHARING_VIOLATION</c>.</summary>
        private const int Win32ErrorSharingViolation = 32;

        /// <summary>Win32 <c>ERROR_LOCK_VIOLATION</c>.</summary>
        private const int Win32ErrorLockViolation = 33;

        /// <summary>POSIX <c>EAGAIN</c> / <c>EWOULDBLOCK</c> as surfaced by .NET on Linux for exclusive open contention.</summary>
        private const int LinuxErrnoEagain = 11;

        /// <summary>Wait between sharing-violation retries. Cancellation is observed on this wait.</summary>
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

        /// <summary>Open lock file. Disposing it releases <see cref="FileShare.None"/>.</summary>
        private readonly FileStream _stream;

        /// <summary>Non-zero after <see cref="Dispose"/> has run.</summary>
        private int _disposed;

        /// <summary>
        /// Optional test seam that replaces <see cref="FileStream"/> construction.
        /// Production code leaves this <see langword="null"/>.
        /// </summary>
        internal static Func<string, FileStream>? CreateStreamForTests { get; set; }

        /// <summary>
        /// Optional test seam that replaces lock-file metadata initialization after open.
        /// Production code leaves this <see langword="null"/>.
        /// </summary>
        internal static Action<FileStream>? InitializeLockFileForTests { get; set; }

        /// <summary>Takes ownership of an already exclusive <paramref name="stream"/>.</summary>
        /// <param name="stream">Lock stream. Disposed by <see cref="Dispose"/>.</param>
        private AcmeExclusiveFileLock(FileStream stream)
        {
            _stream = stream;
        }

        /// <summary>
        /// Acquires an exclusive lock on <paramref name="path"/>, waiting until it is
        /// available or <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        /// <param name="path">Lock file path. The parent directory is created when missing.</param>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <returns>A held lock that must be disposed to release.</returns>
        internal static AcmeExclusiveFileLock Acquire(string path, CancellationToken cancellationToken = default)
        {
            EnsureParentDirectory(path);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryAcquireOnce(path, out AcmeExclusiveFileLock? acquired))
                {
                    return acquired;
                }

                cancellationToken.ThrowIfCancellationRequested();
                cancellationToken.WaitHandle.WaitOne(RetryDelay);
            }
        }

        /// <summary>
        /// Acquires an exclusive lock on <paramref name="path"/> asynchronously, waiting until it is
        /// available or <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        /// <param name="path">Lock file path. The parent directory is created when missing.</param>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <returns>A held lock that must be disposed to release.</returns>
        internal static async Task<AcmeExclusiveFileLock> AcquireAsync(string path, CancellationToken cancellationToken)
        {
            EnsureParentDirectory(path);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryAcquireOnce(path, out AcmeExclusiveFileLock? acquired))
                {
                    return acquired;
                }

                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Attempts one exclusive open and initialization of the lock file.
        /// </summary>
        /// <param name="path">Lock file path.</param>
        /// <param name="acquired">Held lock when this method returns <see langword="true"/>.</param>
        /// <returns>
        /// <see langword="false"/> only when another holder owns the file (recognized contention).
        /// Unrelated filesystem failures are thrown.
        /// </returns>
        internal static bool TryAcquireOnce(
            string path,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AcmeExclusiveFileLock? acquired)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            acquired = null;

            FileStream? stream = null;
            try
            {
                stream = CreateExclusiveStream(path);
            }
            catch (Exception ex) when (IsLockContention(ex))
            {
                return false;
            }

            try
            {
                InitializeLockFile(stream);
                acquired = new AcmeExclusiveFileLock(stream);
                stream = null;
                return true;
            }
            finally
            {
                stream?.Dispose();
            }
        }

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="exception"/> is a platform-recognized
        /// exclusive-share / lock contention condition that may be retried.
        /// </summary>
        /// <param name="exception">Exception thrown while opening the lock file.</param>
        /// <returns><see langword="true"/> only for recognized contention; otherwise fail closed.</returns>
        /// <remarks>
        /// <see cref="UnauthorizedAccessException"/> is never contention. On Windows, only
        /// sharing and lock violation Win32 codes retry. On Linux, only <c>EAGAIN</c> (11) as
        /// surfaced by .NET for <see cref="FileShare.None"/> contention retries. Other platforms
        /// fail closed.
        /// </remarks>
        internal static bool IsLockContention(Exception exception)
        {
            if (exception is not IOException io)
            {
                return false;
            }

            if (OperatingSystem.IsWindows())
            {
                var win32 = io.HResult & 0xFFFF;
                return win32 is Win32ErrorSharingViolation or Win32ErrorLockViolation;
            }

            if (OperatingSystem.IsLinux())
            {
                // .NET surfaces flock/open contention as IOException with RawErrno EAGAIN (11).
                return io.HResult == LinuxErrnoEagain;
            }

            return false;
        }

        /// <summary>Creates the lock file's parent directory when the path includes one.</summary>
        /// <param name="path">Lock file path.</param>
        private static void EnsureParentDirectory(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        /// <summary>Opens or creates the lock file with <see cref="FileShare.None"/>.</summary>
        /// <param name="path">Lock file path.</param>
        /// <returns>The exclusive stream. Caller owns disposal until ownership transfers to a lock instance.</returns>
        private static FileStream CreateExclusiveStream(string path)
        {
            if (CreateStreamForTests is { } create)
            {
                return create(path);
            }

            return new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.None);
        }

        /// <summary>Writes the owning process id into an already exclusive lock stream.</summary>
        /// <param name="stream">Exclusive lock stream.</param>
        private static void InitializeLockFile(FileStream stream)
        {
            if (InitializeLockFileForTests is { } initialize)
            {
                initialize(stream);
                return;
            }

            stream.SetLength(0);
            var payload = Encoding.UTF8.GetBytes(
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
                + Environment.NewLine);
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }

        /// <summary>Closes the lock stream, releasing the exclusive file lock. A second call does nothing.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _stream.Dispose();
        }
    }
}

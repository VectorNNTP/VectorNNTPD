using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Sinks.File;

namespace VectorNNTPD.Common.Logging
{
    /// <summary>
    /// Daily (or other interval) rolling file sink that gzip-compresses a file when it becomes inactive
    /// and applies <c>retainedFileCountLimit</c> to both <c>.log</c> and <c>.log.gz</c> days.
    /// </summary>
    /// <remarks>
    /// When the interval rolls, this sink closes the active file, compresses that path, then opens the next file.
    /// The active path is never compressed or deleted. One lock covers roll, compression, and retention.
    /// </remarks>
    public sealed class GzipRollingFileSink : ILogEventSink, IFlushableFileSink, IDisposable, ISetLoggingFailureListener
    {
        /// <summary>Serializes roll, compression, and retention. This sink does not await while holding it.</summary>
        private readonly object _sync = new();

        /// <summary>Directory that holds the active file and archived files.</summary>
        private readonly string _directory;

        /// <summary>File-name prefix before the period token.</summary>
        private readonly string _filenamePrefix;

        /// <summary>File-name suffix after the period token, including the extension.</summary>
        private readonly string _filenameSuffix;

        /// <summary>Matches archived names so retention can group a period and sequence.</summary>
        private readonly Regex _filenameMatcher;

        /// <summary>How often the active path changes.</summary>
        private readonly RollingInterval _interval;

        /// <summary>Format applied to the checkpoint when building the file name.</summary>
        private readonly string _periodFormat;

        /// <summary>Formatter for the active file. Archived files are not rewritten.</summary>
        private readonly ITextFormatter _textFormatter;

        /// <summary>Active-file size cap. <see langword="null"/> is unlimited.</summary>
        private readonly long? _fileSizeLimitBytes;

        /// <summary>Maximum retained periods, including the active file. <see langword="null"/> keeps every period.</summary>
        private readonly int? _retainedFileCountLimit;

        /// <summary>Whether the active writer buffers until flush.</summary>
        private readonly bool _buffered;

        /// <summary>Whether a full active file advances the sequence inside the same period.</summary>
        private readonly bool _rollOnFileSizeLimit;

        /// <summary>Text encoding. <see langword="null"/> uses the active writer's default.</summary>
        private readonly Encoding? _encoding;

        /// <summary>Compression used when a file becomes inactive.</summary>
        private readonly ILogFileCompressor _compressor;

        /// <summary>Invoked with the uncompressed path after it is closed and before compression. <see langword="null"/> skips that callback.</summary>
        private readonly Action<string>? _onCompletedFile;

        /// <summary>Clock used to choose the current period.</summary>
        private readonly TimeProvider _time;

        /// <summary>Serilog failure listener. <see langword="null"/> until <see cref="SetFailureListener"/> runs.</summary>
        private ILoggingFailureListener? _failureListener;

        /// <summary>Open active file. <see langword="null"/> before the first emit and after dispose.</summary>
        private ActiveLogFileWriter? _current;

        /// <summary>Path of <see cref="_current"/>. <see langword="null"/> when no file is open.</summary>
        private string? _currentPath;

        /// <summary>Instant at which the current period ends. <see langword="null"/> when the interval is infinite.</summary>
        private DateTime? _nextCheckpoint;

        /// <summary>Size-roll sequence inside the current period. <see langword="null"/> for the first file of a period.</summary>
        private int? _currentSequence;

        /// <summary><see langword="true"/> after <see cref="Dispose"/>.</summary>
        private bool _disposed;

        /// <summary>Initializes a rolling gzip file sink.</summary>
        /// <param name="path">Serilog rolling path such as <c>logs/app-.log</c>.</param>
        /// <param name="textFormatter">Formatter for the active file. Not used to rewrite archived files.</param>
        /// <param name="fileSizeLimitBytes">Active-file size cap. <see langword="null"/> is unlimited.</param>
        /// <param name="retainedFileCountLimit">
        /// Maximum daily files including the active file. <see langword="null"/> keeps every day.
        /// A <c>.log</c> and <c>.log.gz</c> for the same period count as one file.
        /// </param>
        /// <param name="encoding">File encoding. <see langword="null"/> uses the File sink default.</param>
        /// <param name="buffered">Whether the active file buffers writes.</param>
        /// <param name="rollingInterval">How the path period is chosen.</param>
        /// <param name="rollOnFileSizeLimit">Whether a full active file rolls to the next sequence.</param>
        /// <param name="compressor">Compression used when a file becomes inactive. Defaults to gzip.</param>
        /// <param name="onCompletedFile">
        /// Invoked with the uncompressed path after it is closed and before compression.
        /// Exceptions other than <see cref="OperationCanceledException"/> are written to Serilog self-log and do not skip compression.
        /// <see cref="OperationCanceledException"/> propagates to the caller.
        /// </param>
        /// <param name="timeProvider">Clock for roll checkpoints. Defaults to <see cref="TimeProvider.System"/>.</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="path"/> is empty or its file name is <c>.</c>, <c>..</c>, or contains a directory separator.</exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="path"/> or <paramref name="textFormatter"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="fileSizeLimitBytes"/> or <paramref name="retainedFileCountLimit"/> is less than 1.</exception>
        public GzipRollingFileSink(
            string path,
            ITextFormatter textFormatter,
            long? fileSizeLimitBytes,
            int? retainedFileCountLimit,
            Encoding? encoding,
            bool buffered,
            RollingInterval rollingInterval,
            bool rollOnFileSizeLimit,
            ILogFileCompressor? compressor = null,
            Action<string>? onCompletedFile = null,
            TimeProvider? timeProvider = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(textFormatter);
            if (fileSizeLimitBytes is < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(fileSizeLimitBytes));
            }

            if (retainedFileCountLimit is < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(retainedFileCountLimit));
            }

            _textFormatter = textFormatter;
            _fileSizeLimitBytes = fileSizeLimitBytes;
            _retainedFileCountLimit = retainedFileCountLimit;
            _encoding = encoding;
            _buffered = buffered;
            _interval = rollingInterval;
            _rollOnFileSizeLimit = rollOnFileSizeLimit;
            _compressor = compressor ?? new GzipLogFileCompressor();
            _onCompletedFile = onCompletedFile;
            _time = timeProvider ?? TimeProvider.System;
            _periodFormat = PeriodFormat(rollingInterval);

            var pathDirectory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(pathDirectory))
            {
                pathDirectory = Directory.GetCurrentDirectory();
            }

            _directory = Path.GetFullPath(pathDirectory);
            _filenamePrefix = Path.GetFileNameWithoutExtension(path);
            _filenameSuffix = Path.GetExtension(path);
            if (string.IsNullOrEmpty(_filenamePrefix)
                || _filenamePrefix is "." or ".."
                || _filenamePrefix.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
                || _filenameSuffix.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            {
                throw new ArgumentException("The log path file name must be a single relative segment.", nameof(path));
            }

            _filenameMatcher = new Regex(
                "^" +
                Regex.Escape(_filenamePrefix) +
                "(?<period>\\d{" + _periodFormat.Length + "})" +
                "(?<sequence>_[0-9]{3,})?" +
                Regex.Escape(_filenameSuffix) +
                "(\\." + "gz" + ")?$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }

        /// <inheritdoc />
        public void Emit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var now = _time.GetLocalNow().DateTime;
                Align(now, nextSequence: false);
                while (_current is { } file
                    && !file.TryEmit(logEvent)
                    && _rollOnFileSizeLimit)
                {
                    Align(now, nextSequence: true);
                }
            }
        }

        /// <inheritdoc />
        public void FlushToDisk()
        {
            lock (_sync)
            {
                _current?.FlushToDisk();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _current?.Dispose();
                _current = null;
                _currentPath = null;
                _disposed = true;
            }
        }

        /// <inheritdoc />
        public void SetFailureListener(ILoggingFailureListener failureListener)
        {
            ArgumentNullException.ThrowIfNull(failureListener);
            lock (_sync)
            {
                _failureListener = failureListener;
            }
        }

        /// <summary>
        /// Opens a file when none is open, the checkpoint has passed, or <paramref name="nextSequence"/> is true.
        /// A checkpoint roll clears the sequence. A size roll starts at 1 and then increments.
        /// </summary>
        /// <param name="now">Local time used to choose the period and the next checkpoint.</param>
        /// <param name="nextSequence"><see langword="true"/> when the active file is full and size rolling is enabled.</param>
        private void Align(DateTime now, bool nextSequence)
        {
            if (_current is null || nextSequence || (_nextCheckpoint.HasValue && now >= _nextCheckpoint.Value))
            {
                var sequence = _currentSequence;
                if (nextSequence)
                {
                    sequence = sequence is null ? 1 : sequence.Value + 1;
                }
                else if (_current is not null)
                {
                    sequence = null;
                }

                CloseCurrent();
                Open(now, sequence);
            }
        }

        /// <summary>Disposes the active writer and clears the open path and checkpoint.</summary>
        private void CloseCurrent()
        {
            _current?.Dispose();
            _current = null;
            _currentPath = null;
            _nextCheckpoint = null;
        }

        /// <summary>
        /// Creates the log directory, opens the period file for <paramref name="now"/>, then archives inactive <c>.log</c> files and applies retention.
        /// </summary>
        /// <param name="now">Local time whose checkpoint becomes the open period.</param>
        /// <param name="sequence">Sequence suffix. <see langword="null"/> writes the unsequenced period file.</param>
        private void Open(DateTime now, int? sequence)
        {
            Directory.CreateDirectory(_directory);
            var checkpoint = Checkpoint(now);
            _nextCheckpoint = NextCheckpoint(now);
            _currentSequence = sequence;
            _currentPath = LogPath(checkpoint, sequence);
            _current = new ActiveLogFileWriter(_currentPath, _textFormatter, _fileSizeLimitBytes, _encoding, _buffered);
            ArchiveInactive();
            ApplyRetention();
        }

        /// <summary>
        /// Deletes leftover <c>.gz.partial</c> files, then compresses matching <c>.log</c> files other than the active path.
        /// A source whose <c>.gz</c> already exists is deleted instead of compressed again.
        /// </summary>
        private void ArchiveInactive()
        {
            if (Directory.Exists(_directory))
            {
                foreach (var partial in Directory.GetFiles(_directory, _filenamePrefix + "*.gz.partial"))
                {
                    TryDelete(partial);
                }
            }

            foreach (var path in MatchingFiles(".log"))
            {
                if (IsCurrent(path) || path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var gzip = path + ".gz";
                if (File.Exists(gzip))
                {
                    TryDelete(path);
                    continue;
                }

                try
                {
                    _onCompletedFile?.Invoke(path);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SelfLog.WriteLine("Completed-log handler failed for {0}: {1}", path, ex);
                }

                var compressed = false;
                try
                {
                    compressed = _compressor.TryCompressAndReplace(path);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SelfLog.WriteLine("Log compression threw for {0}: {1}", path, ex);
                }

                if (!compressed)
                {
                    Report($"log compression failed for {path}; the uncompressed file was kept");
                }
            }
        }

        /// <summary>
        /// Deletes matching files beyond <see cref="_retainedFileCountLimit"/>, keeping newer periods and higher sequences.
        /// The slot that contains the active path is ranked first, and the active path itself is not deleted.
        /// A null limit keeps every file.
        /// </summary>
        private void ApplyRetention()
        {
            if (_retainedFileCountLimit is null)
            {
                return;
            }

            var slots = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in MatchingFiles(".log").Concat(MatchingFiles(".log.gz")))
            {
                var name = Path.GetFileName(path);
                var match = _filenameMatcher.Match(name);
                if (!match.Success)
                {
                    continue;
                }

                var period = match.Groups["period"].Value;
                var sequenceText = match.Groups["sequence"].Value;
                var sequence = sequenceText.Length == 0
                    ? -1
                    : int.Parse(sequenceText[1..], CultureInfo.InvariantCulture);
                var key = period + "\n" + sequence.ToString(CultureInfo.InvariantCulture);
                if (!slots.TryGetValue(key, out var slot))
                {
                    slot = new Slot(period, sequence);
                    slots.Add(key, slot);
                }

                if (!slot.Paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    slot.Paths.Add(path);
                }
            }

            var ranked = slots.Values
                .OrderByDescending(static slot => slot.Period, StringComparer.Ordinal)
                .ThenByDescending(static slot => slot.Sequence)
                .ToList();
            var currentIndex = ranked.FindIndex(slot => slot.Paths.Exists(IsCurrent));
            if (currentIndex > 0)
            {
                var current = ranked[currentIndex];
                ranked.RemoveAt(currentIndex);
                ranked.Insert(0, current);
            }

            for (var index = _retainedFileCountLimit.Value; index < ranked.Count; index++)
            {
                // codeql[cs/linq/missed-where] Retention runs under _sync; Where would allocate while holding the sink lock.
                foreach (var path in ranked[index].Paths)
                {
                    if (!IsCurrent(path))
                    {
                        TryDelete(path);
                    }
                }
            }
        }

        /// <summary>Returns log-directory files whose names match the rolling pattern and the search suffix.</summary>
        /// <param name="suffix">Search suffix, <c>.log</c> or <c>.log.gz</c>.</param>
        /// <returns>An empty sequence when the log directory does not exist.</returns>
        private IEnumerable<string> MatchingFiles(string suffix)
        {
            if (!Directory.Exists(_directory))
            {
                return [];
            }

            return Directory.GetFiles(_directory, _filenamePrefix + "*" + suffix)
                .Where(path => _filenameMatcher.IsMatch(Path.GetFileName(path)));
        }

        /// <summary>Builds the full path for one period checkpoint and optional sequence.</summary>
        /// <param name="checkpoint">Period instant. <see langword="null"/> omits the period token.</param>
        /// <param name="sequence">Sequence written as <c>_</c> plus three digits. <see langword="null"/> omits it.</param>
        /// <returns>The directory, prefix, token, and suffix combined.</returns>
        private string LogPath(DateTime? checkpoint, int? sequence)
        {
            var token = checkpoint?.ToString(_periodFormat, CultureInfo.InvariantCulture) ?? string.Empty;
            if (sequence is not null)
            {
                token += "_" + sequence.Value.ToString("000", CultureInfo.InvariantCulture);
            }

            return Path.Join(_directory, _filenamePrefix + token + _filenameSuffix);
        }

        /// <summary>Returns whether <paramref name="path"/> is the open active file, using a case-insensitive comparison.</summary>
        /// <param name="path">Candidate path.</param>
        /// <returns><see langword="false"/> when no file is open or the path is not the active file.</returns>
        private bool IsCurrent(string path) =>
            _currentPath is not null && string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase);

        /// <summary>Returns the start of the rolling period that contains <paramref name="instant"/>.</summary>
        /// <param name="instant">Local time.</param>
        /// <returns><see langword="null"/> when the interval is infinite.</returns>
        /// <exception cref="InvalidOperationException">The interval is not infinite, year, month, day, hour, or minute.</exception>
        internal DateTime? Checkpoint(DateTime instant) => _interval switch
        {
            RollingInterval.Infinite => null,
            RollingInterval.Year => new DateTime(instant.Year, 1, 1, 0, 0, 0, instant.Kind),
            RollingInterval.Month => new DateTime(instant.Year, instant.Month, 1, 0, 0, 0, instant.Kind),
            RollingInterval.Day => instant.Date,
            RollingInterval.Hour => new DateTime(instant.Year, instant.Month, instant.Day, instant.Hour, 0, 0, instant.Kind),
            RollingInterval.Minute => new DateTime(
                instant.Year,
                instant.Month,
                instant.Day,
                instant.Hour,
                instant.Minute,
                0,
                instant.Kind),
            _ => throw new InvalidOperationException($"Unsupported rolling interval {_interval}."),
        };

        /// <summary>Returns the first instant of the period after the one that contains <paramref name="instant"/>.</summary>
        /// <param name="instant">Local time.</param>
        /// <returns><see langword="null"/> when the interval is infinite.</returns>
        /// <exception cref="InvalidOperationException">The interval is not infinite, year, month, day, hour, or minute.</exception>
        internal DateTime? NextCheckpoint(DateTime instant) => _interval switch
        {
            RollingInterval.Infinite => null,
            RollingInterval.Year => new DateTime(instant.Year, 1, 1, 0, 0, 0, instant.Kind).AddYears(1),
            RollingInterval.Month => new DateTime(instant.Year, instant.Month, 1, 0, 0, 0, instant.Kind).AddMonths(1),
            RollingInterval.Day => instant.Date.AddDays(1),
            RollingInterval.Hour => new DateTime(instant.Year, instant.Month, instant.Day, instant.Hour, 0, 0, instant.Kind).AddHours(1),
            RollingInterval.Minute => new DateTime(
                instant.Year,
                instant.Month,
                instant.Day,
                instant.Hour,
                instant.Minute,
                0,
                instant.Kind).AddMinutes(1),
            _ => throw new InvalidOperationException($"Unsupported rolling interval {_interval}."),
        };

        /// <summary>
        /// Writes <paramref name="message"/> to the Serilog self-log and, when a listener is set, reports
        /// <see cref="LoggingFailureKind.Permanent"/> with no events and no exception.
        /// </summary>
        /// <param name="message">Failure text. Compression includes the path whose uncompressed file was kept.</param>
        private void Report(string message)
        {
            SelfLog.WriteLine(message);
            _failureListener?.OnLoggingFailed(
                this,
                LoggingFailureKind.Permanent,
                message,
                events: null!,
                exception: null!);
        }

        /// <summary>
        /// Deletes <paramref name="path"/> when it exists.
        /// <see cref="IOException"/> and <see cref="UnauthorizedAccessException"/> are written to the Serilog self-log.
        /// </summary>
        /// <param name="path">File to delete.</param>
        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SelfLog.WriteLine("Could not delete log file {0}: {1}", path, ex);
            }
        }

        /// <summary>Returns the invariant period format for <paramref name="interval"/>.</summary>
        /// <param name="interval">Configured rolling interval.</param>
        /// <returns><c>yyyy</c>, <c>yyyyMM</c>, <c>yyyyMMdd</c>, <c>yyyyMMddHH</c>, <c>yyyyMMddHHmm</c>, or empty for an infinite interval.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> is not infinite, year, month, day, hour, or minute.</exception>
        private static string PeriodFormat(RollingInterval interval) => interval switch
        {
            RollingInterval.Infinite => string.Empty,
            RollingInterval.Year => "yyyy",
            RollingInterval.Month => "yyyyMM",
            RollingInterval.Day => "yyyyMMdd",
            RollingInterval.Hour => "yyyyMMddHH",
            RollingInterval.Minute => "yyyyMMddHHmm",
            _ => throw new ArgumentOutOfRangeException(nameof(interval)),
        };

        /// <summary>One retained period and sequence, with every path that belongs to it.</summary>
        /// <param name="period">Period token from the file name.</param>
        /// <param name="sequence">Size-roll sequence. <c>-1</c> is the unsequenced file for that period.</param>
        private sealed class Slot(string period, int sequence)
        {
            /// <summary>Gets the period token used to rank this slot.</summary>
            public string Period { get; } = period;

            /// <summary>Gets the size-roll sequence used to rank this slot.</summary>
            public int Sequence { get; } = sequence;

            /// <summary>Gets the <c>.log</c> and <c>.log.gz</c> paths that share this period and sequence.</summary>
            public List<string> Paths { get; } = [];
        }
    }
}

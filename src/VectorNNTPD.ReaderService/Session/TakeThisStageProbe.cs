using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using VectorNNTPD.ReaderService.History;

namespace VectorNNTPD.ReaderService.Session
{
    /// <summary>
    /// Off-by-default TAKETHIS pipeline stage sampler. Disabled unless
    /// <c>VECTORNNTP_TAKETHIS_TIMING</c> is set or <see cref="Enable"/> is called.
    /// When disabled, callers must not allocate per-article state.
    /// </summary>
    internal static class TakeThisStageProbe
    {
        /// <summary>Environment variable that enables server-side TAKETHIS stage timing.</summary>
        public const string EnvironmentVariableName = "VECTORNNTP_TAKETHIS_TIMING";

        private static ProbeState? _state;

        static TakeThisStageProbe()
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
            if (string.IsNullOrWhiteSpace(value) ||
                value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var dir = value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                      value.Equals("true", StringComparison.OrdinalIgnoreCase)
                ? DefaultDirectory()
                : value;
            Enable(dir);
        }

        /// <summary>Gets whether stage timestamps should be recorded.</summary>
        public static bool IsEnabled => Volatile.Read(ref _state) is not null;

        /// <summary>Gets the directory that receives session dumps, or <see langword="null"/>.</summary>
        public static string? OutputDirectory => Volatile.Read(ref _state)?.Directory;

        /// <summary>Enables sampling and writes dumps under <paramref name="outputDirectory"/>.</summary>
        public static void Enable(string outputDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
            Directory.CreateDirectory(outputDirectory);
            Volatile.Write(ref _state, new ProbeState(outputDirectory));
        }

        /// <summary>Disables sampling (tests).</summary>
        public static void Disable() => Volatile.Write(ref _state, null);

        /// <summary>Creates a per-session log when enabled; otherwise <see langword="null"/>.</summary>
        public static TakeThisStageSessionLog? CreateSessionLog() =>
            IsEnabled ? new TakeThisStageSessionLog() : null;

        /// <summary>
        /// Repo-local diagnostic dump directory.
        /// </summary>
        /// <remarks>
        /// Deliberately walks from the process working directory toward a folder that
        /// contains <c>VectorNNTPD.ReaderService.sln</c> or <c>.artifacts/</c>. This is
        /// development-only output, off unless <see cref="EnvironmentVariableName"/> is set.
        /// It is not application-local runtime state and must not be used for certs/logs.
        /// </remarks>
        internal static string DefaultDirectory()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir is not null)
            {
                if (File.Exists(Path.Join(dir.FullName, "VectorNNTPD.ReaderService.sln")) ||
                    Directory.Exists(Path.Join(dir.FullName, ".artifacts")))
                {
                    return Path.Join(dir.FullName, ".artifacts", "takethis-timing");
                }

                dir = dir.Parent;
            }

            return Path.Join(Directory.GetCurrentDirectory(), ".artifacts", "takethis-timing");
        }

        private sealed class ProbeState(string directory)
        {
            public string Directory { get; } = directory;
        }
    }

    /// <summary>Per-session collection of TAKETHIS stage samples.</summary>
    internal sealed class TakeThisStageSessionLog
    {
        private readonly ConcurrentBag<TakeThisStageSample> _samples = [];
        private int _peakOccupied;
        private int _accepted;

        /// <summary>Gets the number of recorded accepted samples.</summary>
        public int Count => _samples.Count;

        /// <summary>Gets the peak occupied slot count observed while sampling.</summary>
        public int PeakOccupied => Volatile.Read(ref _peakOccupied);

        /// <summary>Records occupied-window observations (all admits, not only accepted samples).</summary>
        public void NoteOccupied(int occupied)
        {
            var current = Volatile.Read(ref _peakOccupied);
            while (occupied > current)
            {
                var original = Interlocked.CompareExchange(ref _peakOccupied, occupied, current);
                if (original == current)
                {
                    break;
                }

                current = original;
            }
        }

        /// <summary>Adds one accepted TAKETHIS stage sample.</summary>
        public void Record(in TakeThisStageSample sample)
        {
            Interlocked.Increment(ref _accepted);
            _samples.Add(sample);
        }

        /// <summary>Writes CSV and a percentile report for this session.</summary>
        public string Write(string directory, string sessionLabel)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(directory);
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionLabel);
            Directory.CreateDirectory(directory);
            var samples = _samples.ToArray();
            Array.Sort(samples, static (a, b) => a.CommandParsedTs.CompareTo(b.CommandParsedTs));
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var safe = Sanitize(sessionLabel);
            var csvPath = Path.Join(directory, $"{stamp}-{safe}.csv");
            var txtPath = Path.Join(directory, $"{stamp}-{safe}.txt");
            File.WriteAllText(csvPath, TakeThisStageReport.FormatCsv(samples), Encoding.UTF8);
            var report = TakeThisStageReport.Format(
                samples,
                PeakOccupied,
                sessionLabel,
                csvPath);
            File.WriteAllText(txtPath, report, Encoding.UTF8);
            return txtPath;
        }

        private static string Sanitize(string value)
        {
            var chars = value.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (!char.IsAsciiLetterOrDigit(chars[i]) && chars[i] is not '-' and not '_')
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }
    }

    /// <summary>Monotonic timestamps for one TAKETHIS pipeline slot.</summary>
    internal sealed class TakeThisStageMarks
    {
        public long CommandParsedTs;
        public long PeekStartTs;
        public long PeekCompleteTs;
        public long ReceiveStartTs;
        public long ReceiveCompleteTs;
        public long EmitStartTs;
        public long EnqueueStartTs;
        public long EnqueueAcceptedTs;
        public long RememberStartTs;
        public long RememberCompleteTs;
        public long ReplyEnqueueStartTs;
        public long ReplyEnqueueCompleteTs;
        public int OccupiedAtEmit;
        public int ArticleBytes;
        public string Outcome = "accepted";

        /// <summary>Wraps <paramref name="lookup"/> so peek completion is stamped when Redis returns.</summary>
        public ValueTask<HistoryLookupResult> ObservePeek(ValueTask<HistoryLookupResult> lookup) =>
            ObservePeekAsync(this, lookup);

        public TakeThisStageSample ToSample(int peakOccupied) =>
            new(
                CommandParsedTs,
                PeekStartTs,
                PeekCompleteTs,
                ReceiveStartTs,
                ReceiveCompleteTs,
                EmitStartTs,
                EnqueueStartTs,
                EnqueueAcceptedTs,
                RememberStartTs,
                RememberCompleteTs,
                ReplyEnqueueStartTs,
                ReplyEnqueueCompleteTs,
                OccupiedAtEmit,
                peakOccupied,
                ArticleBytes,
                Outcome);

        private static async ValueTask<HistoryLookupResult> ObservePeekAsync(
            TakeThisStageMarks marks,
            ValueTask<HistoryLookupResult> lookup)
        {
            try
            {
                var result = await lookup.ConfigureAwait(false);
                marks.PeekCompleteTs = Stopwatch.GetTimestamp();
                return result;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch
            {
                marks.PeekCompleteTs = Stopwatch.GetTimestamp();
                throw;
            }
        }
    }

    /// <summary>One accepted TAKETHIS pipeline transaction (monotonic timestamps).</summary>
    internal readonly record struct TakeThisStageSample(
        long CommandParsedTs,
        long PeekStartTs,
        long PeekCompleteTs,
        long ReceiveStartTs,
        long ReceiveCompleteTs,
        long EmitStartTs,
        long EnqueueStartTs,
        long EnqueueAcceptedTs,
        long RememberStartTs,
        long RememberCompleteTs,
        long ReplyEnqueueStartTs,
        long ReplyEnqueueCompleteTs,
        int OccupiedAtEmit,
        int PeakOccupied,
        int ArticleBytes,
        string Outcome);

    /// <summary>Percentile report for TAKETHIS stage samples.</summary>
    internal static class TakeThisStageReport
    {
        public static string FormatCsv(IReadOnlyList<TakeThisStageSample> samples)
        {
            var sb = new StringBuilder();
            sb.AppendLine(
                "command_parsed_ts,peek_us,receive_us,ready_to_emit_us,enqueue_us,remember_us,reply_enqueue_us,server_txn_us,occupied_at_emit,peak_occupied,article_bytes,outcome,dominant");
            foreach (var sample in samples)
            {
                var i = Intervals(sample);
                sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{sample.CommandParsedTs},{i.PeekUs},{i.ReceiveUs},{i.ReadyToEmitUs},{i.EnqueueUs},{i.RememberUs},{i.ReplyEnqueueUs},{i.ServerTxnUs},{sample.OccupiedAtEmit},{sample.PeakOccupied},{sample.ArticleBytes},{sample.Outcome},{i.Dominant}"));
            }

            return sb.ToString();
        }

        public static string Format(
            IReadOnlyList<TakeThisStageSample> samples,
            int peakOccupied,
            string sessionLabel,
            string? csvPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("TAKETHIS PIPELINE STAGE TIMING — DIAGNOSTIC");
            sb.AppendLine();
            sb.AppendLine("Off-by-default server probe. Production path is unchanged when disabled.");
            sb.AppendLine("Clock: Stopwatch.GetTimestamp (monotonic). 239 enqueue is Channel accept, not socket flush.");
            sb.AppendLine();
            sb.AppendLine($"Session:             {sessionLabel}");
            sb.AppendLine($"Samples:             {samples.Count:N0}");
            var seen = samples.Count(static s => s.Outcome == "seen");
            var unseen = samples.Count(static s => s.Outcome == "unseen");
            sb.AppendLine($"Seen / unseen:       {seen:N0} / {unseen:N0}");
            sb.AppendLine($"Peak occupied:       {peakOccupied} (window {TakeThisPipeline.Depth})");
            if (samples.Count == 0)
            {
                sb.AppendLine();
                sb.AppendLine("No accepted samples.");
                return sb.ToString();
            }

            var maxAtEmit = samples.Max(static s => s.OccupiedAtEmit);
            sb.AppendLine($"Max occupied@emit:   {maxAtEmit}");
            sb.AppendLine($"Average article:     {samples.Average(static s => (double)s.ArticleBytes):N0} bytes");
            sb.AppendLine();

            WriteWindow(sb, "ALL SAMPLES", samples);
            if (samples.Count >= 20)
            {
                var mid = samples.Count / 2;
                WriteWindow(sb, "FIRST HALF", samples.Take(mid).ToArray());
                WriteWindow(sb, "SECOND HALF", samples.Skip(mid).ToArray());
            }

            if (csvPath is not null)
            {
                sb.AppendLine($"CSV: {csvPath}");
            }

            return sb.ToString();
        }

        private static void WriteWindow(StringBuilder sb, string title, IReadOnlyList<TakeThisStageSample> samples)
        {
            sb.AppendLine(title);
            sb.AppendLine($"{"Interval",-28} {"Mean",10} {"P50",10} {"P95",10} {"P99",10} {"Max",10}");
            foreach (var row in Rows(samples))
            {
                sb.AppendLine(row);
            }

            sb.AppendLine();
            var votes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var name in samples.Select(static sample => Intervals(sample).Dominant))
            {
                votes[name] = votes.TryGetValue(name, out var n) ? n + 1 : 1;
            }

            sb.AppendLine("Dominant interval (largest of peek / receive / ready→emit / enqueue / remember / 239 enqueue):");
            foreach (var pair in votes.OrderByDescending(static p => p.Value))
            {
                var pct = 100.0 * pair.Value / samples.Count;
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  {pair.Key,-22} {pair.Value,8:N0}  {pct,6:F1}%"));
            }

            sb.AppendLine();
        }

        private static IEnumerable<string> Rows(IReadOnlyList<TakeThisStageSample> samples)
        {
            yield return Stats("msgid → peek start", samples, static i => i.MsgidToPeekUs);
            yield return Stats("HistoryDB Peek", samples, static i => i.PeekUs);
            yield return Stats("article receive", samples, static i => i.ReceiveUs);
            yield return Stats("max(peek, receive)", samples, static i => i.OverlapUs);
            yield return Stats("peek leftover after RX", samples, static i => i.PeekAfterReceiveUs);
            yield return Stats("ready → emit (order)", samples, static i => i.ReadyToEmitUs);
            yield return Stats("queue EnqueueAsync", samples, static i => i.EnqueueUs);
            yield return Stats("HistoryDB Remember", samples, static i => i.RememberUs);
            yield return Stats("239 Channel enqueue", samples, static i => i.ReplyEnqueueUs);
            yield return Stats("server txn (parse→239)", samples, static i => i.ServerTxnUs);
        }

        private static string Stats(
            string name,
            IReadOnlyList<TakeThisStageSample> samples,
            Func<StageIntervals, double> selector)
        {
            var values = new double[samples.Count];
            double sum = 0;
            for (var i = 0; i < samples.Count; i++)
            {
                var value = selector(Intervals(samples[i]));
                values[i] = value;
                sum += value;
            }

            Array.Sort(values);
            var mean = sum / values.Length;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{name,-28} {mean,10:F1} {Percentile(values, 0.50),10:F1} {Percentile(values, 0.95),10:F1} {Percentile(values, 0.99),10:F1} {values[^1],10:F1}");
        }

        internal static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 1)
            {
                return sorted[0];
            }

            var position = p * (sorted.Length - 1);
            var lo = (int)position;
            var hi = Math.Min(lo + 1, sorted.Length - 1);
            var frac = position - lo;
            return sorted[lo] + ((sorted[hi] - sorted[lo]) * frac);
        }

        internal static StageIntervals Intervals(in TakeThisStageSample sample)
        {
            var peek = Us(sample.PeekStartTs, sample.PeekCompleteTs);
            var receive = Us(sample.ReceiveStartTs, sample.ReceiveCompleteTs);
            var ready = Math.Max(sample.PeekCompleteTs, sample.ReceiveCompleteTs);
            var readyToEmit = Us(ready, sample.EmitStartTs);
            var enqueue = Us(sample.EnqueueStartTs, sample.EnqueueAcceptedTs);
            var remember = Us(sample.RememberStartTs, sample.RememberCompleteTs);
            var reply = Us(sample.ReplyEnqueueStartTs, sample.ReplyEnqueueCompleteTs);
            var txn = Us(sample.CommandParsedTs, sample.ReplyEnqueueCompleteTs);
            var msgidToPeek = Us(sample.CommandParsedTs, sample.PeekStartTs);
            var leftover = sample.PeekCompleteTs > sample.ReceiveCompleteTs
                ? Us(sample.ReceiveCompleteTs, sample.PeekCompleteTs)
                : 0;
            var overlap = Math.Max(peek, receive);

            var dominant = "article receive";
            var best = receive;
            if (peek > best)
            {
                best = peek;
                dominant = "HistoryDB Peek";
            }

            if (readyToEmit > best)
            {
                best = readyToEmit;
                dominant = "ordered emission";
            }

            if (enqueue > best)
            {
                best = enqueue;
                dominant = "queue admission";
            }

            if (remember > best)
            {
                best = remember;
                dominant = "HistoryDB Remember";
            }

            if (reply > best)
            {
                dominant = "TX/flush (239 enqueue)";
            }

            return new StageIntervals(
                msgidToPeek,
                peek,
                receive,
                overlap,
                leftover,
                readyToEmit,
                enqueue,
                remember,
                reply,
                txn,
                dominant);
        }

        private static double Us(long start, long end)
        {
            if (start <= 0 || end <= 0 || end < start)
            {
                return 0;
            }

            return Stopwatch.GetElapsedTime(start, end).TotalMicroseconds;
        }

        internal readonly record struct StageIntervals(
            double MsgidToPeekUs,
            double PeekUs,
            double ReceiveUs,
            double OverlapUs,
            double PeekAfterReceiveUs,
            double ReadyToEmitUs,
            double EnqueueUs,
            double RememberUs,
            double ReplyEnqueueUs,
            double ServerTxnUs,
            string Dominant);
    }
}

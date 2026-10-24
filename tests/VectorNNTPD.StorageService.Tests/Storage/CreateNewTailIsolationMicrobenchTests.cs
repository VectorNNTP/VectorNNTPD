using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace VectorNNTPD.StorageService.Tests.Storage
{
    /// <summary>
    /// Opt-in filesystem microbenchmark isolating minimal <see cref="FileMode.CreateNew"/> latency
    /// from the production-style temp+write+flush+rename publication sequence.
    /// Not an E2E StorageService throughput claim. Activate with
    /// <c>VECTORNNTPD_STORAGE_CREATENEW_TAIL_BENCH=1</c>.
    /// </summary>
    public sealed class CreateNewTailIsolationMicrobenchTests
    {
        private const string EnvFlag = "VECTORNNTPD_STORAGE_CREATENEW_TAIL_BENCH";
        private const string EnvOps = "VECTORNNTPD_STORAGE_CREATENEW_TAIL_OPS";
        private const string EnvWorkers = "VECTORNNTPD_STORAGE_CREATENEW_TAIL_WORKERS";
        private const string EnvRepeats = "VECTORNNTPD_STORAGE_CREATENEW_TAIL_REPEATS";
        private const string EnvWarmup = "VECTORNNTPD_STORAGE_CREATENEW_TAIL_WARMUP";
        private const int ArticleBytes = 750 * 1024;
        private const int StallEventCap = 256;
        private const int VerifySample = 32;

        private readonly ITestOutputHelper _output;

        public CreateNewTailIsolationMicrobenchTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Optional_minimal_CreateNew_vs_production_like_publish()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(EnvFlag), "1", StringComparison.Ordinal))
            {
                _output.WriteLine(
                    $"Set {EnvFlag}=1 to run CreateNew tail isolation microbench "
                    + "(minimal CreateNew vs production-like temp+rename; not an E2E claim).");
                return;
            }

            var ops = 8000;
            if (int.TryParse(Environment.GetEnvironmentVariable(EnvOps), out var configuredOps)
                && configuredOps is >= 100 and <= 100_000)
            {
                ops = configuredOps;
            }

            var workers = 4;
            if (int.TryParse(Environment.GetEnvironmentVariable(EnvWorkers), out var configuredWorkers)
                && configuredWorkers is >= 1 and <= 32)
            {
                workers = configuredWorkers;
            }

            var repeats = 2;
            if (int.TryParse(Environment.GetEnvironmentVariable(EnvRepeats), out var configuredRepeats)
                && configuredRepeats is >= 1 and <= 5)
            {
                repeats = configuredRepeats;
            }

            var warmup = 500;
            if (int.TryParse(Environment.GetEnvironmentVariable(EnvWarmup), out var configuredWarmup)
                && configuredWarmup is >= 0 and <= 10_000)
            {
                warmup = configuredWarmup;
            }

            var payload = CreatePayload(ArticleBytes);
            var root = ResolveRoot();
            Directory.CreateDirectory(root);
            Emit(
                $"createnew-tail-config ops={ops} warmupOps={warmup} workers={workers} repeats={repeats} "
                + $"articleBytes={ArticleBytes} root={root} "
                + $"os={RuntimeInformation.OSDescription} framework={RuntimeInformation.FrameworkDescription} "
                + $"cpus={Environment.ProcessorCount} activation={EnvFlag}=1 "
                + "order=ABBA_across_repeats_Minimal_then_ProdLike_then_swap "
                + "variants=MinimalCreateNew,ProdLikeTempRename,WriteOnlyPrecreated "
                + "fileOptions=FileMode.CreateNew,buffer=128KiB,SequentialScan,FileShare.None,"
                + "Flush(flushToDisk:false) "
                + "layout=n0/n1/n2/uniqueLeaf "
                + "hypothesis=Open_stalls_are_CreateNew_intrinsic_vs_publication_sequence "
                + "note=microbench_not_E2E_art_per_s_AV_filter_drivers_not_assessed "
                + "cacheState=uncontrolled_warm_OS_cache");

            try
            {
                // ABBA across repeats: r1 Minimal→ProdLike→WriteOnly; r2 ProdLike→Minimal→WriteOnly.
                for (var r = 1; r <= repeats; r++)
                {
                    var prodFirst = r % 2 == 0;
                    Emit($"createnew-tail-repeat-order r={r} order={(prodFirst ? "BAAB_primary_ProdLike_then_Minimal" : "ABBA_primary_Minimal_then_ProdLike")}");
                    if (prodFirst)
                    {
                        RunVariant(root, payload, ops, warmup, workers, Variant.ProdLikeTempRename, r);
                        RunVariant(root, payload, ops, warmup, workers, Variant.MinimalCreateNew, r);
                    }
                    else
                    {
                        RunVariant(root, payload, ops, warmup, workers, Variant.MinimalCreateNew, r);
                        RunVariant(root, payload, ops, warmup, workers, Variant.ProdLikeTempRename, r);
                    }

                    RunVariant(root, payload, ops, warmup, workers, Variant.WriteOnlyPrecreated, r);
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException ex)
                {
                    Emit($"createnew-tail-cleanup-warning {ex.GetType().Name}:{ex.Message}");
                }
            }
        }

        private void RunVariant(
            string root,
            byte[] payload,
            int ops,
            int warmup,
            int workers,
            Variant variant,
            int repeat)
        {
            var runDir = Path.Join(root, $"{variant}-r{repeat}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(runDir);
            for (var a = 0; a < 16; a++)
            {
                for (var b = 0; b < 16; b++)
                {
                    for (var c = 0; c < 16; c++)
                    {
                        Directory.CreateDirectory(Path.Join(runDir, a.ToString("x"), b.ToString("x"), c.ToString("x")));
                    }
                }
            }

            if (variant == Variant.WriteOnlyPrecreated)
            {
                for (var b = 0; b < 256; b++)
                {
                    Directory.CreateDirectory(Path.Join(runDir, "wo", b.ToString("x2")));
                }

                PrecreateEmptyFiles(runDir, warmup + ops, workers);
            }

            // Warm-up (excluded from reported distributions). Uses ids [0, warmup).
            if (warmup > 0)
            {
                RunOps(
                    runDir,
                    payload,
                    count: warmup,
                    idOffset: 0,
                    workers,
                    variant,
                    measure: false,
                    out _,
                    out _,
                    out _);
            }

            var process = Process.GetCurrentProcess();
            process.Refresh();
            var wsStart = process.WorkingSet64;
            var cpuStart = process.TotalProcessorTime;
            var wall = Stopwatch.StartNew();
            // Measured ids [warmup, warmup+ops) so WriteOnly does not re-hit warm-up files.
            RunOps(
                runDir,
                payload,
                count: ops,
                idOffset: warmup,
                workers,
                variant,
                measure: true,
                out var metrics,
                out var completed,
                out var errors);
            wall.Stop();
            process.Refresh();
            var wsEnd = process.WorkingSet64;
            var cpu = process.TotalProcessorTime - cpuStart;
            var cpuPct = wall.Elapsed.TotalMilliseconds > 0
                ? 100d * cpu.TotalMilliseconds / (wall.Elapsed.TotalMilliseconds * Environment.ProcessorCount)
                : 0d;

            var verified = 0;
            var verifyFail = 0;
            var verifyBaseId = warmup;
            if (variant != Variant.MinimalCreateNew)
            {
                for (var i = 0; i < Math.Min(VerifySample, completed); i++)
                {
                    var id = verifyBaseId + i;
                    var path = variant == Variant.WriteOnlyPrecreated
                        ? BuildWriteOnlyPath(runDir, id)
                        : BuildFinalPath(runDir, id);

                    verified++;
                    if (!FileContentsEqual(path, payload))
                    {
                        verifyFail++;
                    }
                }
            }
            else
            {
                // Minimal creates empty files at final leaf paths; confirm existence sample.
                for (var i = 0; i < Math.Min(VerifySample, completed); i++)
                {
                    verified++;
                    var path = BuildFinalPath(runDir, verifyBaseId + i);
                    if (!File.Exists(path) || new FileInfo(path).Length != 0)
                    {
                        verifyFail++;
                    }
                }
            }

            Emit(FormatReport(
                variant,
                repeat,
                ops,
                warmup,
                workers,
                wall.Elapsed,
                completed,
                errors,
                metrics,
                verified,
                verifyFail,
                wsStart,
                wsEnd,
                cpu,
                cpuPct,
                runDir));

            Assert.Equal(0, errors);
            Assert.Equal(ops, completed);
            Assert.Equal(0, verifyFail);
            Assert.Equal(Math.Min(VerifySample, completed), verified);
        }

        private static void RunOps(
            string runDir,
            byte[] payload,
            int count,
            int idOffset,
            int workers,
            Variant variant,
            bool measure,
            out StageMetrics metrics,
            out int completed,
            out int errors)
        {
            var localMetrics = measure ? new StageMetrics() : StageMetrics.Null;
            metrics = localMetrics;
            var next = 0;
            var done = 0;
            var fail = 0;
            var tasks = new Task[workers];
            for (var w = 0; w < workers; w++)
            {
                tasks[w] = Task.Run(() =>
                {
                    while (true)
                    {
                        var local = Interlocked.Increment(ref next) - 1;
                        if (local >= count)
                        {
                            return;
                        }

                        var id = idOffset + local;
                        try
                        {
                            switch (variant)
                            {
                                case Variant.MinimalCreateNew:
                                    OpMinimalCreateNew(BuildFinalPath(runDir, id), localMetrics);
                                    break;
                                case Variant.ProdLikeTempRename:
                                    OpProdLikeTempRename(BuildFinalPath(runDir, id), payload, localMetrics);
                                    break;
                                case Variant.WriteOnlyPrecreated:
                                    OpWriteOnly(BuildWriteOnlyPath(runDir, id), payload, localMetrics);
                                    break;
                                default:
                                    throw new InvalidOperationException($"Unknown variant {variant}");
                            }

                            Interlocked.Increment(ref done);
                        }
                        catch (Exception)
                        {
                            Interlocked.Increment(ref fail);
                        }
                    }
                });
            }

            Task.WaitAll(tasks);
            completed = done;
            errors = fail;
        }

        private static void OpMinimalCreateNew(string path, StageMetrics metrics)
        {
            var totalStart = Stopwatch.GetTimestamp();
            var openStart = totalStart;
            var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            var openEnd = Stopwatch.GetTimestamp();
            var openMs = Ms(openEnd - openStart);
            var closeStart = openEnd;
            stream.Dispose();
            var closeEnd = Stopwatch.GetTimestamp();
            var closeMs = Ms(closeEnd - closeStart);
            var totalMs = Ms(closeEnd - totalStart);
            metrics.Record(openMs, writeMs: 0, flushMs: 0, moveMs: 0, closeMs, totalMs, openEnd);
        }

        private static void OpProdLikeTempRename(string final, ReadOnlySpan<byte> payload, StageMetrics metrics)
        {
            var totalStart = Stopwatch.GetTimestamp();
            var temp = final + ".writing-" + Guid.NewGuid().ToString("N");
            var openStart = Stopwatch.GetTimestamp();
            var stream = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            var openEnd = Stopwatch.GetTimestamp();
            var openMs = Ms(openEnd - openStart);

            var writeStart = openEnd;
            stream.Write(payload);
            var writeEnd = Stopwatch.GetTimestamp();
            var writeMs = Ms(writeEnd - writeStart);

            var flushStart = writeEnd;
            stream.Flush(flushToDisk: false);
            var flushEnd = Stopwatch.GetTimestamp();
            var flushMs = Ms(flushEnd - flushStart);

            var closeStart = flushEnd;
            stream.Dispose();
            var closeEnd = Stopwatch.GetTimestamp();
            var closeMs = Ms(closeEnd - closeStart);

            var moveStart = closeEnd;
            File.Move(temp, final, overwrite: true);
            var moveEnd = Stopwatch.GetTimestamp();
            var moveMs = Ms(moveEnd - moveStart);
            var totalMs = Ms(moveEnd - totalStart);
            metrics.Record(openMs, writeMs, flushMs, moveMs, closeMs, totalMs, openEnd);
        }

        private static void OpWriteOnly(string path, ReadOnlySpan<byte> payload, StageMetrics metrics)
        {
            var totalStart = Stopwatch.GetTimestamp();
            // Open existing (not CreateNew): isolates write/flush/close from create.
            var openStart = totalStart;
            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            var openEnd = Stopwatch.GetTimestamp();
            var openMs = Ms(openEnd - openStart);

            var writeStart = openEnd;
            stream.SetLength(0);
            stream.Write(payload);
            var writeEnd = Stopwatch.GetTimestamp();
            var writeMs = Ms(writeEnd - writeStart);

            var flushStart = writeEnd;
            stream.Flush(flushToDisk: false);
            var flushEnd = Stopwatch.GetTimestamp();
            var flushMs = Ms(flushEnd - flushStart);

            var closeStart = flushEnd;
            stream.Dispose();
            var closeEnd = Stopwatch.GetTimestamp();
            var closeMs = Ms(closeEnd - closeStart);
            var totalMs = Ms(closeEnd - totalStart);
            metrics.Record(openMs, writeMs, flushMs, moveMs: 0, closeMs, totalMs, openEnd);
        }

        private static void PrecreateEmptyFiles(string runDir, int count, int workers)
        {
            var next = 0;
            var tasks = new Task[workers];
            for (var w = 0; w < workers; w++)
            {
                tasks[w] = Task.Run(() =>
                {
                    while (true)
                    {
                        var id = Interlocked.Increment(ref next) - 1;
                        if (id >= count)
                        {
                            return;
                        }

                        var path = BuildWriteOnlyPath(runDir, id);
                        using var stream = new FileStream(
                            path,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            bufferSize: 128 * 1024,
                            FileOptions.SequentialScan);
                    }
                });
            }

            Task.WaitAll(tasks);
        }

        private string FormatReport(
            Variant variant,
            int repeat,
            int ops,
            int warmup,
            int workers,
            TimeSpan wall,
            int completed,
            int errors,
            StageMetrics metrics,
            int verified,
            int verifyFail,
            long wsStart,
            long wsEnd,
            TimeSpan cpu,
            double cpuPct,
            string runDir)
        {
            var open = metrics.SortedOpen();
            var write = metrics.SortedWrite();
            var flush = metrics.SortedFlush();
            var move = metrics.SortedMove();
            var close = metrics.SortedClose();
            var total = metrics.SortedTotal();
            var openGe10 = CountGe(open, 10);
            var openGe50 = CountGe(open, 50);
            var openGe100 = CountGe(open, 100);
            var totalGe10 = CountGe(total, 10);
            var totalGe50 = CountGe(total, 50);
            var totalGe100 = CountGe(total, 100);
            var n = Math.Max(1, open.Length);
            var stallSummary = metrics.FormatStallEvents();

            // Dominant stage among totals ≥50 ms (by max of open/write/flush/move/close on that op).
            var dominant = metrics.DominantStageAmongSlowTotals(50);

            return
                $"createnew-tail variant={variant} r={repeat} ops={ops} warmupExcluded={warmup} workers={workers} "
                + $"wallMs={wall.TotalMilliseconds:0} completed={completed} errors={errors} "
                + $"opsPerSec={completed / Math.Max(wall.TotalSeconds, 0.001):0.00} "
                + $"openMs_p50={Pct(open, 0.50):0.000} p95={Pct(open, 0.95):0.000} p99={Pct(open, 0.99):0.000} "
                + $"max={MaxOrZero(open):0.000} "
                + $"open_ge10ms={openGe10} ge50ms={openGe50} ge100ms={openGe100} "
                + $"openStallPer1000_ge50={1000d * openGe50 / n:0.000} "
                + $"writeMs_p50={Pct(write, 0.50):0.000} p95={Pct(write, 0.95):0.000} p99={Pct(write, 0.99):0.000} "
                + $"max={MaxOrZero(write):0.000} "
                + $"flushMs_p50={Pct(flush, 0.50):0.000} p95={Pct(flush, 0.95):0.000} max={MaxOrZero(flush):0.000} "
                + $"moveMs_p50={Pct(move, 0.50):0.000} p95={Pct(move, 0.95):0.000} p99={Pct(move, 0.99):0.000} "
                + $"max={MaxOrZero(move):0.000} "
                + $"closeMs_p50={Pct(close, 0.50):0.000} p95={Pct(close, 0.95):0.000} max={MaxOrZero(close):0.000} "
                + $"totalMs_p50={Pct(total, 0.50):0.000} p95={Pct(total, 0.95):0.000} p99={Pct(total, 0.99):0.000} "
                + $"max={MaxOrZero(total):0.000} "
                + $"total_ge10ms={totalGe10} ge50ms={totalGe50} ge100ms={totalGe100} "
                + $"totalStallPer1000_ge50={1000d * totalGe50 / Math.Max(1, total.Length):0.000} "
                + $"dominantStage_when_total_ge50ms={dominant} "
                + $"{stallSummary} "
                + $"verified={verified} verifyFail={verifyFail} "
                + $"wsStart={wsStart} wsEnd={wsEnd} wsPeakObserved={Math.Max(wsStart, wsEnd)} "
                + $"cpuMs={cpu.TotalMilliseconds:0} cpuPctOfMachine={cpuPct:0.0} "
                + $"runDir={runDir} "
                + "diskCounters=not_collected_no_admin_required_path "
                + "endpointProtection=not_assessed "
                + "semanticsNote="
                + variant switch
                {
                    Variant.MinimalCreateNew => "CreateNew_empty_file_close_no_payload_no_rename",
                    Variant.ProdLikeTempRename => "temp_CreateNew_write_flush_false_close_Move_overwrite_matches_production",
                    Variant.WriteOnlyPrecreated => "Open_existing_truncate_write_flush_false_close_no_CreateNew_no_rename",
                    _ => "unknown",
                };
        }

        private sealed class StageMetrics
        {
            public static readonly StageMetrics Null = new(discard: true);

            private readonly bool _discard;
            private readonly ConcurrentBag<double> _open = [];
            private readonly ConcurrentBag<double> _write = [];
            private readonly ConcurrentBag<double> _flush = [];
            private readonly ConcurrentBag<double> _move = [];
            private readonly ConcurrentBag<double> _close = [];
            private readonly ConcurrentBag<double> _total = [];
            private readonly ConcurrentBag<StallEvent> _stalls = [];
            private readonly ConcurrentBag<(double Open, double Write, double Flush, double Move, double Close, double Total)> _paired = [];

            public StageMetrics(bool discard = false) => _discard = discard;

            public void Record(
                double openMs,
                double writeMs,
                double flushMs,
                double moveMs,
                double closeMs,
                double totalMs,
                long openEndTicks)
            {
                if (_discard)
                {
                    return;
                }

                _open.Add(openMs);
                _write.Add(writeMs);
                _flush.Add(flushMs);
                _move.Add(moveMs);
                _close.Add(closeMs);
                _total.Add(totalMs);
                _paired.Add((openMs, writeMs, flushMs, moveMs, closeMs, totalMs));

                void Consider(string stage, double ms)
                {
                    if (ms < 50)
                    {
                        return;
                    }

                    if (_stalls.Count >= StallEventCap)
                    {
                        return;
                    }

                    _stalls.Add(new StallEvent(openEndTicks, stage, ms));
                }

                Consider("open", openMs);
                Consider("write", writeMs);
                Consider("flush", flushMs);
                Consider("move", moveMs);
                Consider("close", closeMs);
                Consider("total", totalMs);
            }

            public double[] SortedOpen() => ToSorted(_open);

            public double[] SortedWrite() => ToSorted(_write);

            public double[] SortedFlush() => ToSorted(_flush);

            public double[] SortedMove() => ToSorted(_move);

            public double[] SortedClose() => ToSorted(_close);

            public double[] SortedTotal() => ToSorted(_total);

            public string FormatStallEvents()
            {
                var events = _stalls.ToArray();
                if (events.Length == 0)
                {
                    return "stallEvents_ge50ms=0";
                }

                Array.Sort(events, static (a, b) => a.Ticks.CompareTo(b.Ticks));
                var byStage = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var e in events)
                {
                    byStage[e.Stage] = byStage.GetValueOrDefault(e.Stage) + 1;
                }

                var stageCounts = string.Join(',', byStage.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
                var first = events[0];
                var last = events[^1];
                return
                    $"stallEvents_ge50ms={events.Length} cap={StallEventCap} byStage={stageCounts} "
                    + $"firstStage={first.Stage} firstMs={first.Ms:0.000} "
                    + $"lastStage={last.Stage} lastMs={last.Ms:0.000} "
                    + "note=stall_timestamps_monotonic_do_not_imply_shared_cause";
            }

            public string DominantStageAmongSlowTotals(double totalThresholdMs)
            {
                var open = 0;
                var write = 0;
                var flush = 0;
                var move = 0;
                var close = 0;
                var n = 0;
                foreach (var p in _paired)
                {
                    if (p.Total < totalThresholdMs)
                    {
                        continue;
                    }

                    n++;
                    var max = p.Open;
                    var name = "open";
                    if (p.Write > max)
                    {
                        max = p.Write;
                        name = "write";
                    }

                    if (p.Flush > max)
                    {
                        max = p.Flush;
                        name = "flush";
                    }

                    if (p.Move > max)
                    {
                        max = p.Move;
                        name = "move";
                    }

                    if (p.Close > max)
                    {
                        name = "close";
                    }

                    switch (name)
                    {
                        case "open":
                            open++;
                            break;
                        case "write":
                            write++;
                            break;
                        case "flush":
                            flush++;
                            break;
                        case "move":
                            move++;
                            break;
                        default:
                            close++;
                            break;
                    }
                }

                return
                    $"n={n} open={open} write={write} flush={flush} move={move} close={close}";
            }
        }

        private readonly record struct StallEvent(long Ticks, string Stage, double Ms);

        private static string BuildFinalPath(string runDir, int id)
        {
            // Deterministic leaf from id (unique within a runDir) so post-run verify can reopen the file.
            var hex = id.ToString("x8");
            return Path.Join(runDir, hex[0].ToString(), hex[1].ToString(), hex[2].ToString(), hex);
        }

        private static string BuildWriteOnlyPath(string runDir, int id) =>
            Path.Join(runDir, "wo", (id % 256).ToString("x2"), id.ToString("x8") + ".bin");

        private static byte[] CreatePayload(int bytes)
        {
            var payload = new byte[bytes];
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i & 0xFF);
            }

            return payload;
        }

        private static bool FileContentsEqual(string path, ReadOnlySpan<byte> expected)
        {
            if (!File.Exists(path) || new FileInfo(path).Length != expected.Length)
            {
                return false;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            var offset = 0;
            while (offset < expected.Length)
            {
                var read = stream.Read(buffer, 0, Math.Min(buffer.Length, expected.Length - offset));
                if (read == 0 || !buffer.AsSpan(0, read).SequenceEqual(expected.Slice(offset, read)))
                {
                    return false;
                }

                offset += read;
            }

            return stream.ReadByte() < 0;
        }

        private static string ResolveRoot()
        {
            var fromRepo = Path.GetFullPath(Path.Join(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                ".artifacts",
                "perf",
                "createnew-tail-workdir"));
            return Path.Join(fromRepo, Guid.NewGuid().ToString("N"));
        }

        private void Emit(string line)
        {
            Console.WriteLine(line);
            _output.WriteLine(line);
        }

        private static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;

        private static double[] ToSorted(ConcurrentBag<double> bag)
        {
            var arr = bag.ToArray();
            Array.Sort(arr);
            return arr;
        }

        private static int CountGe(double[] sorted, double threshold)
        {
            var n = 0;
            for (var i = 0; i < sorted.Length; i++)
            {
                if (sorted[i] >= threshold)
                {
                    n++;
                }
            }

            return n;
        }

        private static double MaxOrZero(double[] sorted) => sorted.Length == 0 ? 0 : sorted[^1];

        private static double Pct(double[] sorted, double percentile)
        {
            if (sorted.Length == 0)
            {
                return 0;
            }

            var index = (int)Math.Ceiling(sorted.Length * percentile) - 1;
            if (index < 0)
            {
                index = 0;
            }

            if (index >= sorted.Length)
            {
                index = sorted.Length - 1;
            }

            return sorted[index];
        }

        private enum Variant
        {
            MinimalCreateNew,
            ProdLikeTempRename,
            WriteOnlyPrecreated,
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VectorNNTPD.Common.Core;
using VectorNNTPD.StorageService;
using Xunit.Abstractions;

namespace VectorNNTPD.StorageService.Tests.Storage
{
    /// <summary>
    /// Opt-in filesystem microbenchmark comparing production-style temp+rename publication
    /// with an experimental create-in-place write. Not equivalent to StorageService E2E throughput
    /// and must not be substituted into production (<c>SpoolArticlePublisher</c> remains temp+rename).
    /// Activate with <c>VECTORNNTPD_STORAGE_PUBLISH_STRATEGY_BENCH=1</c>.
    /// </summary>
    public sealed class PublishStrategyMicrobenchTests
    {
        private const string EnvFlag = "VECTORNNTPD_STORAGE_PUBLISH_STRATEGY_BENCH";
        private const string EnvOps = "VECTORNNTPD_STORAGE_PUBLISH_STRATEGY_OPS";
        private const string EnvWorkers = "VECTORNNTPD_STORAGE_PUBLISH_STRATEGY_WORKERS";
        private const string EnvRepeats = "VECTORNNTPD_STORAGE_PUBLISH_STRATEGY_REPEATS";
        private const int ArticleBytes = 750 * 1024;
        private const int VerifySample = 64;

        private readonly ITestOutputHelper _output;

        public PublishStrategyMicrobenchTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Optional_temp_rename_vs_create_in_place()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(EnvFlag), "1", StringComparison.Ordinal))
            {
                _output.WriteLine(
                    $"Set {EnvFlag}=1 to run publish-strategy microbench "
                    + "(temp+rename vs create-in-place; not an E2E StorageService claim).");
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

            var payload = CreatePayload(ArticleBytes);
            var root = ResolveRoot();
            Directory.CreateDirectory(root);
            Emit(
                $"pubstrat-config ops={ops} workers={workers} repeats={repeats} articleBytes={ArticleBytes} "
                + $"root={root} os={RuntimeInformation.OSDescription} cpus={Environment.ProcessorCount} "
                + $"activation={EnvFlag}=1 "
                + "variants=TempRename,CreateInPlace "
                + "fileOptions=FileMode.CreateNew,buffer=128KiB,SequentialScan,Flush(flushToDisk:false) "
                + "layout=n0/n1/n2/uniqueFinal "
                + "note=create_in_place_NOT_production_contract_readers_could_observe_partial_files "
                + "cacheState=uncontrolled_warm_OS_cache_Flush_false_not_physical_durability");

            try
            {
                // ABBA order across repeats to reduce systematic first/second bias.
                for (var r = 1; r <= repeats; r++)
                {
                    var firstInPlace = r % 2 == 0;
                    if (firstInPlace)
                    {
                        RunVariant(root, payload, ops, workers, PublishVariant.CreateInPlace, r);
                        RunVariant(root, payload, ops, workers, PublishVariant.TempRename, r);
                    }
                    else
                    {
                        RunVariant(root, payload, ops, workers, PublishVariant.TempRename, r);
                        RunVariant(root, payload, ops, workers, PublishVariant.CreateInPlace, r);
                    }
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
                    Emit($"pubstrat-cleanup-warning {ex.GetType().Name}:{ex.Message}");
                }
            }
        }

        private void RunVariant(
            string root,
            byte[] payload,
            int ops,
            int workers,
            PublishVariant variant,
            int repeat)
        {
            var runDir = Path.Join(root, $"{variant}-r{repeat}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(runDir);
            // Pre-create shard fan-out (n0/n1/n2) so leaf CreateDirectory is warm like a used spool.
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

            var openMs = new ConcurrentBag<double>();
            var writeMs = new ConcurrentBag<double>();
            var flushMs = new ConcurrentBag<double>();
            var moveMs = new ConcurrentBag<double>();
            var totalMs = new ConcurrentBag<double>();
            var finals = new ConcurrentBag<string>();
            var errors = 0;
            var nextId = 0;

            var process = Process.GetCurrentProcess();
            process.Refresh();
            var wsStart = process.WorkingSet64;
            var cpuStart = process.TotalProcessorTime;
            var wall = Stopwatch.StartNew();

            var tasks = new Task[workers];
            for (var w = 0; w < workers; w++)
            {
                tasks[w] = Task.Run(() =>
                {
                    while (true)
                    {
                        var id = Interlocked.Increment(ref nextId);
                        if (id > ops)
                        {
                            return;
                        }

                        try
                        {
                            var final = BuildFinalPath(runDir, id);
                            var started = Stopwatch.GetTimestamp();
                            if (variant == PublishVariant.TempRename)
                            {
                                PublishTempRename(final, payload, openMs, writeMs, flushMs, moveMs);
                            }
                            else
                            {
                                PublishCreateInPlace(final, payload, openMs, writeMs, flushMs);
                            }

                            totalMs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                            finals.Add(final);
                        }
                        catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                        {
                            Interlocked.Increment(ref errors);
                            ExpectedException.Ignore(ex);
                        }
                    }
                });
            }

            Task.WaitAll(tasks);
            wall.Stop();
            process.Refresh();
            var wsEnd = process.WorkingSet64;
            var cpu = process.TotalProcessorTime - cpuStart;
            var cpuPct = wall.Elapsed.TotalMilliseconds > 0
                ? 100d * cpu.TotalMilliseconds / (wall.Elapsed.TotalMilliseconds * Environment.ProcessorCount)
                : 0d;

            var completed = finals.Count;
            var opsPerSec = completed / Math.Max(wall.Elapsed.TotalSeconds, 0.001);
            var totalArr = ToSorted(totalMs);
            var openArr = ToSorted(openMs);
            var writeArr = ToSorted(writeMs);
            var flushArr = ToSorted(flushMs);
            var moveArr = ToSorted(moveMs);

            var verified = 0;
            var verifyFail = 0;
            foreach (var path in finals.Take(VerifySample))
            {
                if (FileContentsEqual(path, payload))
                {
                    verified++;
                }
                else
                {
                    verifyFail++;
                }
            }

            var leftoverTemps = Directory.EnumerateFiles(runDir, "*.writing-*", SearchOption.AllDirectories).Take(5).Count();

            Emit(
                $"pubstrat-result variant={variant} repeat={repeat} opsRequested={ops} completed={completed} errors={errors} "
                + $"workers={workers} wallMs={wall.Elapsed.TotalMilliseconds:0} opsPerSec={opsPerSec:0.00} "
                + $"articleBytes={ArticleBytes} bytesWritten={completed * (long)ArticleBytes} "
                + $"openMs_avg={Avg(openArr):0.000} p50={Pct(openArr, 0.50):0.000} p95={Pct(openArr, 0.95):0.000} p99={Pct(openArr, 0.99):0.000} "
                + $"writeMs_avg={Avg(writeArr):0.000} p50={Pct(writeArr, 0.50):0.000} p95={Pct(writeArr, 0.95):0.000} "
                + $"flushMs_avg={Avg(flushArr):0.000} p50={Pct(flushArr, 0.50):0.000} flushToDisk=false "
                + $"moveMs_avg={Avg(moveArr):0.000} p50={Pct(moveArr, 0.50):0.000} p95={Pct(moveArr, 0.95):0.000} p99={Pct(moveArr, 0.99):0.000} "
                + $"e2eOpMs_p50={Pct(totalArr, 0.50):0.000} p95={Pct(totalArr, 0.95):0.000} p99={Pct(totalArr, 0.99):0.000} "
                + $"verifySample={VerifySample} verifiedOk={verified} verifyFail={verifyFail} leftoverWritingTempsSampled={leftoverTemps} "
                + $"wsStart={wsStart} wsEnd={wsEnd} wsPeakObserved={Math.Max(wsStart, wsEnd)} "
                + $"cpuMs={cpu.TotalMilliseconds:0} cpuPctOfMachine={cpuPct:0.0} "
                + $"runDir={runDir} "
                + "semanticsNote="
                + (variant == PublishVariant.TempRename
                    ? "final_visible_only_after_rename_matches_production"
                    : "experimental_final_path_visible_during_write_NOT_production"));

            Assert.Equal(0, errors);
            Assert.Equal(ops, completed);
            Assert.Equal(0, verifyFail);
            Assert.Equal(Math.Min(VerifySample, completed), verified);
        }

        /// <summary>
        /// Mirrors production <c>FileSpoolArticleFiles.WriteNew</c> + <c>SpoolArticlePublisher</c> temp+rename:
        /// unique <c>.writing-</c> temp beside final, CreateNew write, Flush(false), Move overwrite.
        /// </summary>
        private static void PublishTempRename(
            string final,
            ReadOnlySpan<byte> payload,
            ConcurrentBag<double> openMs,
            ConcurrentBag<double> writeMs,
            ConcurrentBag<double> flushMs,
            ConcurrentBag<double> moveMs)
        {
            var temp = final + ".writing-" + Guid.NewGuid().ToString("N");
            WriteNewTimed(temp, payload, openMs, writeMs, flushMs);
            var moveStart = Stopwatch.GetTimestamp();
            File.Move(temp, final, overwrite: true);
            moveMs.Add(Stopwatch.GetElapsedTime(moveStart).TotalMilliseconds);
        }

        /// <summary>
        /// Experimental: CreateNew directly at the final path. Readers of <paramref name="final"/> may observe
        /// a partial file during the write. Production must not use this sequence.
        /// </summary>
        private static void PublishCreateInPlace(
            string final,
            ReadOnlySpan<byte> payload,
            ConcurrentBag<double> openMs,
            ConcurrentBag<double> writeMs,
            ConcurrentBag<double> flushMs)
        {
            WriteNewTimed(final, payload, openMs, writeMs, flushMs);
        }

        private static void WriteNewTimed(
            string path,
            ReadOnlySpan<byte> content,
            ConcurrentBag<double> openMs,
            ConcurrentBag<double> writeMs,
            ConcurrentBag<double> flushMs)
        {
            var openStart = Stopwatch.GetTimestamp();
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            openMs.Add(Stopwatch.GetElapsedTime(openStart).TotalMilliseconds);

            var writeStart = Stopwatch.GetTimestamp();
            stream.Write(content);
            writeMs.Add(Stopwatch.GetElapsedTime(writeStart).TotalMilliseconds);

            var flushStart = Stopwatch.GetTimestamp();
            stream.Flush(flushToDisk: false);
            flushMs.Add(Stopwatch.GetElapsedTime(flushStart).TotalMilliseconds);
        }

        private static string BuildFinalPath(string runDir, int id)
        {
            // Distribute like production n0/n1/n2/leaf using hex nibbles of id (not ArticleId; microbench only).
            var hex = id.ToString("x8");
            var n0 = hex[0];
            var n1 = hex[1];
            var n2 = hex[2];
            var leaf = hex + Guid.NewGuid().ToString("N");
            return Path.Join(runDir, n0.ToString(), n1.ToString(), n2.ToString(), leaf);
        }

        private static byte[] CreatePayload(int bytes)
        {
            var payload = new byte[bytes];
            // Deterministic non-zero pattern (avoid sparse-file shortcuts on some volumes).
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
                "publish-strategy-workdir"));
            return Path.Join(fromRepo, Guid.NewGuid().ToString("N"));
        }

        private void Emit(string line)
        {
            Console.WriteLine(line);
            _output.WriteLine(line);
        }

        private static double[] ToSorted(ConcurrentBag<double> bag)
        {
            var arr = bag.ToArray();
            Array.Sort(arr);
            return arr;
        }

        private static double Avg(double[] sorted) =>
            sorted.Length == 0 ? 0 : sorted.Sum() / sorted.Length;

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

        private enum PublishVariant
        {
            TempRename,
            CreateInPlace,
        }
    }
}

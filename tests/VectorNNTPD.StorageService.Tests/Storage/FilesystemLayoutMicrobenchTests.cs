using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace VectorNNTPD.StorageService.Tests.Storage
{
    /// <summary>
    /// Test-only microbenchmark: same-directory temp+rename publication under different directory
    /// sharding depths (flat vs production-like n0/n1/n2). Preserves complete-write-before-visibility.
    /// Does not modify production. Activate with <c>VECTORNNTPD_STORAGE_FS_LAYOUT_BENCH=1</c>.
    /// </summary>
    public sealed class FilesystemLayoutMicrobenchTests
    {
        private const string EnvFlag = "VECTORNNTPD_STORAGE_FS_LAYOUT_BENCH";
        private const string EnvOps = "VECTORNNTPD_STORAGE_FS_LAYOUT_OPS";
        private const string EnvWorkers = "VECTORNNTPD_STORAGE_FS_LAYOUT_WORKERS";
        private const string EnvRepeats = "VECTORNNTPD_STORAGE_FS_LAYOUT_REPEATS";
        private const int ArticleBytes = 750 * 1024;
        private const int VerifySample = 64;

        private readonly ITestOutputHelper _output;

        public FilesystemLayoutMicrobenchTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Optional_directory_layout_temp_rename_microbench()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(EnvFlag), "1", StringComparison.Ordinal))
            {
                _output.WriteLine(
                    $"Set {EnvFlag}=1 to run filesystem layout microbench "
                    + "(Flat vs Shard1 vs Shard3 same-dir temp+rename; not an E2E StorageService claim).");
                return;
            }

            var ops = 8000;
            if (int.TryParse(Environment.GetEnvironmentVariable(EnvOps), out var configuredOps)
                && configuredOps is >= 100 and <= 50_000)
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
                $"fslayout-config ops={ops} workers={workers} repeats={repeats} articleBytes={ArticleBytes} "
                + $"root={root} os={RuntimeInformation.OSDescription} cpus={Environment.ProcessorCount} "
                + $"activation={EnvFlag}=1 "
                + "variants=Flat,Shard1,Shard3 "
                + "hypothesis=leaf_directory_file_density_affects_same_dir_temp_rename_latency "
                + "semantics=temp_beside_final_Flush_false_Move_overwrite_no_create_in_place "
                + "fileOptions=FileMode.CreateNew,buffer=128KiB,SequentialScan,FileShare.None "
                + "cacheState=uncontrolled_warm_OS_cache_Flush_false_not_physical_durability "
                + "note=isolated_FS_microbench_NOT_E2E_art_per_s");

            try
            {
                var order = new[] { LayoutVariant.Flat, LayoutVariant.Shard1, LayoutVariant.Shard3 };
                for (var r = 1; r <= repeats; r++)
                {
                    // Rotate start index across repeats (balanced order).
                    for (var i = 0; i < order.Length; i++)
                    {
                        var variant = order[(i + r - 1) % order.Length];
                        RunVariant(root, payload, ops, workers, variant, r);
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
                    Emit($"fslayout-cleanup-warning {ex.GetType().Name}:{ex.Message}");
                }
            }
        }

        private void RunVariant(
            string root,
            byte[] payload,
            int ops,
            int workers,
            LayoutVariant variant,
            int repeat)
        {
            var runDir = Path.Join(root, $"{variant}-r{repeat}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(runDir);
            PrecreateLayout(runDir, variant);

            var existsMs = new ConcurrentBag<double>();
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
                            var final = BuildFinalPath(runDir, variant, id);
                            var started = Stopwatch.GetTimestamp();

                            var existsStart = Stopwatch.GetTimestamp();
                            _ = File.Exists(final);
                            existsMs.Add(Stopwatch.GetElapsedTime(existsStart).TotalMilliseconds);

                            var temp = final + ".writing-" + Guid.NewGuid().ToString("N");
                            WriteNewTimed(temp, payload, openMs, writeMs, flushMs);
                            var moveStart = Stopwatch.GetTimestamp();
                            File.Move(temp, final, overwrite: true);
                            moveMs.Add(Stopwatch.GetElapsedTime(moveStart).TotalMilliseconds);

                            totalMs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                            finals.Add(final);
                        }
                        catch
                        {
                            Interlocked.Increment(ref errors);
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
            var leafDirs = CountLeafDirectories(runDir, variant);
            var fileCount = Directory.Exists(runDir)
                ? Directory.EnumerateFiles(runDir, "*", SearchOption.AllDirectories).Count()
                : 0;
            var filesPerLeafApprox = leafDirs > 0 ? (double)fileCount / leafDirs : fileCount;

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

            var existsArr = ToSorted(existsMs);
            var openArr = ToSorted(openMs);
            var writeArr = ToSorted(writeMs);
            var flushArr = ToSorted(flushMs);
            var moveArr = ToSorted(moveMs);
            var totalArr = ToSorted(totalMs);

            Emit(
                $"fslayout-result variant={variant} repeat={repeat} opsRequested={ops} completed={completed} errors={errors} "
                + $"workers={workers} wallMs={wall.Elapsed.TotalMilliseconds:0} opsPerSec={opsPerSec:0.00} "
                + $"articleBytes={ArticleBytes} bytesWritten={completed * (long)ArticleBytes} "
                + $"leafDirs={leafDirs} finalFiles={fileCount} approxFilesPerLeaf={filesPerLeafApprox:0.00} "
                + $"existsMs_avg={Avg(existsArr):0.000} p50={Pct(existsArr, 0.50):0.000} "
                + $"openMs_avg={Avg(openArr):0.000} p50={Pct(openArr, 0.50):0.000} p95={Pct(openArr, 0.95):0.000} p99={Pct(openArr, 0.99):0.000} "
                + $"writeMs_avg={Avg(writeArr):0.000} p50={Pct(writeArr, 0.50):0.000} p95={Pct(writeArr, 0.95):0.000} "
                + $"flushMs_avg={Avg(flushArr):0.000} p50={Pct(flushArr, 0.50):0.000} flushToDisk=false "
                + $"moveMs_avg={Avg(moveArr):0.000} p50={Pct(moveArr, 0.50):0.000} p95={Pct(moveArr, 0.95):0.000} p99={Pct(moveArr, 0.99):0.000} "
                + $"e2eOpMs_p50={Pct(totalArr, 0.50):0.000} p95={Pct(totalArr, 0.95):0.000} p99={Pct(totalArr, 0.99):0.000} "
                + $"verifySample={VerifySample} verifiedOk={verified} verifyFail={verifyFail} leftoverWritingTempsSampled={leftoverTemps} "
                + $"wsStart={wsStart} wsEnd={wsEnd} wsPeakObserved={Math.Max(wsStart, wsEnd)} "
                + $"cpuMs={cpu.TotalMilliseconds:0} cpuPctOfMachine={cpuPct:0.0} "
                + $"runDir={runDir} semantics=same_dir_temp_rename_complete_before_final");

            Assert.Equal(0, errors);
            Assert.Equal(ops, completed);
            Assert.Equal(0, verifyFail);
            Assert.Equal(Math.Min(VerifySample, completed), verified);
            Assert.Equal(0, leftoverTemps);
        }

        private static void PrecreateLayout(string runDir, LayoutVariant variant)
        {
            switch (variant)
            {
                case LayoutVariant.Flat:
                    return;
                case LayoutVariant.Shard1:
                    for (var a = 0; a < 16; a++)
                    {
                        Directory.CreateDirectory(Path.Join(runDir, a.ToString("x")));
                    }

                    return;
                case LayoutVariant.Shard3:
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

                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(variant), variant, null);
            }
        }

        private static int CountLeafDirectories(string runDir, LayoutVariant variant) =>
            variant switch
            {
                LayoutVariant.Flat => 1,
                LayoutVariant.Shard1 => 16,
                LayoutVariant.Shard3 => 16 * 16 * 16,
                _ => 0,
            };

        private static string BuildFinalPath(string runDir, LayoutVariant variant, int id)
        {
            var hex = id.ToString("x8");
            var leaf = hex + Guid.NewGuid().ToString("N");
            return variant switch
            {
                LayoutVariant.Flat => Path.Join(runDir, leaf),
                LayoutVariant.Shard1 => Path.Join(runDir, hex[0].ToString(), leaf),
                LayoutVariant.Shard3 => Path.Join(runDir, hex[0].ToString(), hex[1].ToString(), hex[2].ToString(), leaf),
                _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
            };
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
                "..",
                ".artifacts",
                "perf",
                "fs-layout-workdir"));
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

        private static double Avg(double[] sorted) => sorted.Length == 0 ? 0 : sorted.Sum() / sorted.Length;

        private static double Pct(double[] sorted, double percentile)
        {
            if (sorted.Length == 0)
            {
                return 0;
            }

            var index = (int)Math.Ceiling(sorted.Length * percentile) - 1;
            index = Math.Clamp(index, 0, sorted.Length - 1);
            return sorted[index];
        }

        private enum LayoutVariant
        {
            Flat,
            Shard1,
            Shard3,
        }
    }
}

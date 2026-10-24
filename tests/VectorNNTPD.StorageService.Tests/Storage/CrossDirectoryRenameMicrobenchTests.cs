using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VectorNNTPD.Common.Core;
using VectorNNTPD.StorageService;
using Xunit.Abstractions;

namespace VectorNNTPD.StorageService.Tests.Storage
{
    /// <summary>
    /// Test-only ABBA microbenchmark: same-directory vs same-volume cross-directory temp+rename.
    /// Preserves complete-write-before-visibility. Does not modify production.
    /// Activate with <c>VECTORNNTPD_STORAGE_CROSS_DIR_RENAME_BENCH=1</c>.
    /// </summary>
    public sealed class CrossDirectoryRenameMicrobenchTests
    {
        private const string EnvFlag = "VECTORNNTPD_STORAGE_CROSS_DIR_RENAME_BENCH";
        private const string EnvOps = "VECTORNNTPD_STORAGE_CROSS_DIR_RENAME_OPS";
        private const string EnvWorkers = "VECTORNNTPD_STORAGE_CROSS_DIR_RENAME_WORKERS";
        private const string EnvRepeats = "VECTORNNTPD_STORAGE_CROSS_DIR_RENAME_REPEATS";
        private const int ArticleBytes = 750 * 1024;
        private const int VerifySample = 64;

        private readonly ITestOutputHelper _output;

        public CrossDirectoryRenameMicrobenchTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Optional_same_dir_vs_cross_dir_rename_microbench()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(EnvFlag), "1", StringComparison.Ordinal))
            {
                _output.WriteLine(
                    $"Set {EnvFlag}=1 to run same-dir vs same-volume cross-dir rename microbench "
                    + "(not an E2E StorageService claim).");
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
            var finalsRoot = Path.Join(root, "finals");
            var stagingRoot = Path.Join(root, "staging");
            Directory.CreateDirectory(finalsRoot);
            Directory.CreateDirectory(stagingRoot);
            PrecreateShards(finalsRoot);

            AssertSameVolume(finalsRoot, stagingRoot, out var volumeInfo);
            Emit(
                $"crossrename-config ops={ops} workers={workers} repeats={repeats} articleBytes={ArticleBytes} "
                + $"root={root} finalsRoot={finalsRoot} stagingRoot={stagingRoot} "
                + $"volumePrecondition={volumeInfo} "
                + $"os={RuntimeInformation.OSDescription} cpus={Environment.ProcessorCount} "
                + $"activation={EnvFlag}=1 variants=SameDirectory,CrossDirectorySameVolume "
                + "order=ABBA "
                + "semantics=temp_complete_write_then_Move_overwrite_Flush_false "
                + "fileOptions=FileMode.CreateNew,buffer=128KiB,SequentialScan,FileShare.None "
                + "layout=finals_n0_n1_n2 "
                + "cacheState=uncontrolled_warm_OS_cache_Flush_false_not_physical_durability "
                + "note=isolated_FS_microbench_NOT_E2E_art_per_s");

            try
            {
                for (var r = 1; r <= repeats; r++)
                {
                    var crossFirst = r % 2 == 0;
                    if (crossFirst)
                    {
                        RunVariant(finalsRoot, stagingRoot, payload, ops, workers, RenameVariant.CrossDirectorySameVolume, r);
                        RunVariant(finalsRoot, stagingRoot, payload, ops, workers, RenameVariant.SameDirectory, r);
                    }
                    else
                    {
                        RunVariant(finalsRoot, stagingRoot, payload, ops, workers, RenameVariant.SameDirectory, r);
                        RunVariant(finalsRoot, stagingRoot, payload, ops, workers, RenameVariant.CrossDirectorySameVolume, r);
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
                    Emit($"crossrename-cleanup-warning {ex.GetType().Name}:{ex.Message}");
                }
            }
        }

        private void RunVariant(
            string finalsRoot,
            string stagingRoot,
            byte[] payload,
            int ops,
            int workers,
            RenameVariant variant,
            int repeat)
        {
            var cellDir = Path.Join(finalsRoot, $"{variant}-r{repeat}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(cellDir);
            PrecreateShards(cellDir);
            var cellStaging = Path.Join(stagingRoot, $"{variant}-r{repeat}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(cellStaging);
            AssertSameVolume(cellDir, cellStaging, out _);

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
                            var final = BuildFinalPath(cellDir, id);
                            var temp = variant == RenameVariant.SameDirectory
                                ? final + ".writing-" + Guid.NewGuid().ToString("N")
                                : Path.Join(cellStaging, Guid.NewGuid().ToString("N") + ".writing");

                            // Distinct directories for CrossDirectorySameVolume (precondition of the variant).
                            if (variant == RenameVariant.CrossDirectorySameVolume)
                            {
                                var tempParent = Path.GetDirectoryName(temp)!;
                                var finalParent = Path.GetDirectoryName(final)!;
                                if (string.Equals(
                                        Path.GetFullPath(tempParent),
                                        Path.GetFullPath(finalParent),
                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    throw new InvalidOperationException("Cross-directory variant used same parent path.");
                                }
                            }

                            var started = Stopwatch.GetTimestamp();
                            WriteNewTimed(temp, payload, openMs, writeMs, flushMs);
                            var moveStart = Stopwatch.GetTimestamp();
                            File.Move(temp, final, overwrite: true);
                            moveMs.Add(Stopwatch.GetElapsedTime(moveStart).TotalMilliseconds);
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

            var leftoverTemps = Directory.EnumerateFiles(cellDir, "*.writing-*", SearchOption.AllDirectories).Take(5).Count()
                + Directory.EnumerateFiles(cellStaging, "*", SearchOption.TopDirectoryOnly).Take(5).Count();

            var openArr = ToSorted(openMs);
            var writeArr = ToSorted(writeMs);
            var flushArr = ToSorted(flushMs);
            var moveArr = ToSorted(moveMs);
            var totalArr = ToSorted(totalMs);

            Emit(
                $"crossrename-result variant={variant} repeat={repeat} opsRequested={ops} completed={completed} errors={errors} "
                + $"workers={workers} wallMs={wall.Elapsed.TotalMilliseconds:0} opsPerSec={opsPerSec:0.00} "
                + $"articleBytes={ArticleBytes} bytesWritten={completed * (long)ArticleBytes} "
                + $"openMs_avg={Avg(openArr):0.000} p50={Pct(openArr, 0.50):0.000} p95={Pct(openArr, 0.95):0.000} p99={Pct(openArr, 0.99):0.000} "
                + $"writeMs_avg={Avg(writeArr):0.000} p50={Pct(writeArr, 0.50):0.000} p95={Pct(writeArr, 0.95):0.000} "
                + $"flushMs_avg={Avg(flushArr):0.000} p50={Pct(flushArr, 0.50):0.000} flushToDisk=false "
                + $"moveMs_avg={Avg(moveArr):0.000} p50={Pct(moveArr, 0.50):0.000} p95={Pct(moveArr, 0.95):0.000} p99={Pct(moveArr, 0.99):0.000} "
                + $"e2eOpMs_p50={Pct(totalArr, 0.50):0.000} p95={Pct(totalArr, 0.95):0.000} p99={Pct(totalArr, 0.99):0.000} "
                + $"verifySample={VerifySample} verifiedOk={verified} verifyFail={verifyFail} leftoverTempsSampled={leftoverTemps} "
                + $"wsStart={wsStart} wsEnd={wsEnd} wsPeakObserved={Math.Max(wsStart, wsEnd)} "
                + $"cpuMs={cpu.TotalMilliseconds:0} cpuPctOfMachine={cpuPct:0.0} "
                + $"cellDir={cellDir} cellStaging={cellStaging}");

            Assert.Equal(0, errors);
            Assert.Equal(ops, completed);
            Assert.Equal(0, verifyFail);
            Assert.Equal(Math.Min(VerifySample, completed), verified);
            Assert.Equal(0, leftoverTemps);
        }

        private static void AssertSameVolume(string pathA, string pathB, out string detail)
        {
            var fullA = Path.GetFullPath(pathA);
            var fullB = Path.GetFullPath(pathB);
            var rootA = Path.GetPathRoot(fullA) ?? string.Empty;
            var rootB = Path.GetPathRoot(fullB) ?? string.Empty;
            if (!string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Temp and final roots differ: '{rootA}' vs '{rootB}'. Cross-volume rename is out of scope.");
            }

            var driveA = new DriveInfo(rootA);
            var driveB = new DriveInfo(rootB);
            if (!string.Equals(driveA.Name, driveB.Name, StringComparison.OrdinalIgnoreCase)
                || driveA.DriveType != driveB.DriveType)
            {
                throw new InvalidOperationException(
                    $"DriveInfo mismatch: {driveA.Name}/{driveA.DriveType} vs {driveB.Name}/{driveB.DriveType}.");
            }

            detail =
                $"pathRoot={rootA};drive={driveA.Name};driveType={driveA.DriveType};"
                + $"driveFormat={driveA.DriveFormat};isReady={driveA.IsReady}";
        }

        private static void PrecreateShards(string runDir)
        {
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
        }

        private static string BuildFinalPath(string runDir, int id)
        {
            var hex = id.ToString("x8");
            var leaf = hex + Guid.NewGuid().ToString("N");
            return Path.Join(runDir, hex[0].ToString(), hex[1].ToString(), hex[2].ToString(), leaf);
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
                "cross-dir-rename-workdir"));
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

        private enum RenameVariant
        {
            SameDirectory,
            CrossDirectorySameVolume,
        }
    }
}

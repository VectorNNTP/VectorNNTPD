using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace VectorNNTPD.StorageService.Tests.Storage
{
    /// <summary>
    /// Test-only B2 prototype: immutable blob + separate in-process visibility map.
    /// Does not modify production publication or retrieval. In-memory visibility is not crash-durable.
    /// Opt-in microbench: <c>VECTORNNTPD_STORAGE_B2_VISIBILITY_BENCH=1</c>.
    /// </summary>
    public sealed class B2VisibilityPrototypeTests
    {
        private const string EnvFlag = "VECTORNNTPD_STORAGE_B2_VISIBILITY_BENCH";
        private const string EnvOps = "VECTORNNTPD_STORAGE_B2_VISIBILITY_OPS";
        private const string EnvWorkers = "VECTORNNTPD_STORAGE_B2_VISIBILITY_WORKERS";
        private const string EnvRepeats = "VECTORNNTPD_STORAGE_B2_VISIBILITY_REPEATS";
        private const int ArticleBytes = 750 * 1024;
        private const int VerifySample = 64;

        private readonly ITestOutputHelper _output;

        public B2VisibilityPrototypeTests(ITestOutputHelper output) => _output = output;

        /// <summary>Duplicate policy: first successful visibility publish wins; later attempts return Duplicate.</summary>
        [Fact]
        public void Contract_duplicate_first_wins()
        {
            using var store = B2PrototypeStore.Create();
            var payload = CreatePayload(4096);
            var first = store.Publish("article-a", payload);
            Assert.Equal(B2PublishKind.Stored, first.Kind);
            var second = store.Publish("article-a", payload);
            Assert.Equal(B2PublishKind.Duplicate, second.Kind);
            Assert.Equal(first.BlobId, second.BlobId);
            Assert.True(store.TryRetrieve("article-a", out var read));
            Assert.True(payload.AsSpan().SequenceEqual(read));
        }

        [Fact]
        public void Contract_A_failure_before_blob_creation()
        {
            using var store = B2PrototypeStore.Create();
            var payload = CreatePayload(4096);
            var result = store.Publish(
                "a-fail-create",
                payload,
                new B2PublishHooks { FailBeforeBlobCreate = true });
            Assert.Equal(B2PublishKind.Failed, result.Kind);
            Assert.False(store.TryRetrieve("a-fail-create", out _));
            Assert.Equal(0, store.VisibilityCount);
            Assert.Equal(0, store.CountBlobFiles());
        }

        [Fact]
        public void Contract_B_failure_during_blob_write()
        {
            using var store = B2PrototypeStore.Create();
            var payload = CreatePayload(64 * 1024);
            var result = store.Publish(
                "b-fail-write",
                payload,
                new B2PublishHooks { FailAfterBytesWritten = 1024 });
            Assert.Equal(B2PublishKind.Failed, result.Kind);
            Assert.False(store.TryRetrieve("b-fail-write", out _));
            Assert.Equal(0, store.VisibilityCount);
            var orphans = store.ListInvisibleBlobFiles();
            Assert.NotEmpty(orphans);
            Assert.All(orphans, path => Assert.True(new FileInfo(path).Length < payload.Length));
        }

        [Fact]
        public void Contract_C_failure_after_write_before_visibility()
        {
            using var store = B2PrototypeStore.Create();
            var payload = CreatePayload(8192);
            var result = store.Publish(
                "c-fail-vis",
                payload,
                new B2PublishHooks { FailAfterWriteBeforeVisibility = true });
            Assert.Equal(B2PublishKind.Failed, result.Kind);
            Assert.False(store.TryRetrieve("c-fail-vis", out _));
            Assert.Equal(0, store.VisibilityCount);
            var orphans = store.ListInvisibleBlobFiles();
            Assert.Single(orphans);
            Assert.Equal(payload.Length, new FileInfo(orphans[0]).Length);
            Assert.True(store.TryIdentifyOrphan(orphans[0], out var orphanId));
            Assert.False(store.IsVisible(orphanId));
        }

        [Fact]
        public async Task Contract_D_successful_publish_concurrent_reads()
        {
            using var store = B2PrototypeStore.Create();
            var payload = CreatePayload(32 * 1024);
            Assert.Equal(B2PublishKind.Stored, store.Publish("d-concurrent", payload).Kind);

            var errors = 0;
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < 40; i++)
                {
                    if (!store.TryRetrieve("d-concurrent", out var read) || !payload.AsSpan().SequenceEqual(read))
                    {
                        Interlocked.Increment(ref errors);
                    }
                }
            })).ToArray();
            await Task.WhenAll(tasks);
            Assert.Equal(0, errors);
        }

        [Fact]
        public async Task Contract_E_concurrent_duplicate_publication()
        {
            using var store = B2PrototypeStore.Create();
            var payload = CreatePayload(16 * 1024);
            var kinds = new ConcurrentBag<B2PublishKind>();
            var barrier = new Barrier(4);
            var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                barrier.SignalAndWait();
                kinds.Add(store.Publish("e-dup", payload).Kind);
            })).ToArray();
            await Task.WhenAll(tasks);
            var stored = kinds.Count(static k => k == B2PublishKind.Stored);
            var duplicates = kinds.Count(static k => k == B2PublishKind.Duplicate);
            Assert.Equal(1, stored);
            Assert.Equal(3, duplicates);
            Assert.Equal(1, store.VisibilityCount);
            Assert.True(store.TryRetrieve("e-dup", out var read));
            Assert.True(payload.AsSpan().SequenceEqual(read));
        }

        [Fact]
        public void Contract_F_read_before_publication()
        {
            using var store = B2PrototypeStore.Create();
            Assert.False(store.TryRetrieve("missing", out _));
            Assert.False(store.TryDiscoverBlobByLogicalId("missing", out _));
        }

        [Fact]
        public void Contract_G_cleanup_blocked_while_visible_or_leased()
        {
            using var store = B2PrototypeStore.Create();
            var payload = CreatePayload(4096);
            var published = store.Publish("g-lease", payload);
            Assert.Equal(B2PublishKind.Stored, published.Kind);

            Assert.False(store.TryDeleteBlob(published.BlobId!, requireInvisible: true));

            using (store.AcquireReadLease("g-lease"))
            {
                Assert.False(store.TryUnpublish("g-lease"));
                Assert.True(store.TryRetrieve("g-lease", out _));
            }

            Assert.True(store.TryUnpublish("g-lease"));
            Assert.False(store.TryRetrieve("g-lease", out _));
            Assert.True(store.TryDeleteBlob(published.BlobId!, requireInvisible: true));
            Assert.Equal(0, store.CountBlobFiles());
        }

        [Fact]
        public void Contract_H_visibility_publication_failure()
        {
            using var store = B2PrototypeStore.Create();
            var payload = CreatePayload(4096);
            var result = store.Publish(
                "h-vis-fail",
                payload,
                new B2PublishHooks { FailVisibilityPublish = true });
            Assert.Equal(B2PublishKind.Failed, result.Kind);
            Assert.False(store.TryRetrieve("h-vis-fail", out _));
            Assert.Equal(0, store.VisibilityCount);
            Assert.Single(store.ListInvisibleBlobFiles());
        }

        [Fact]
        public void Contract_blob_path_not_derivable_from_logical_id()
        {
            using var store = B2PrototypeStore.Create();
            var payload = CreatePayload(1024);
            var published = store.Publish("logical-xyz", payload);
            Assert.NotNull(published.BlobPath);
            Assert.DoesNotContain("logical-xyz", published.BlobPath, StringComparison.Ordinal);
            Assert.Contains($"{Path.DirectorySeparatorChar}blobs{Path.DirectorySeparatorChar}", published.BlobPath, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Join(store.Root, "logical-xyz")));
        }

        [Fact]
        public void Optional_b2_visibility_vs_temp_rename_bench()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(EnvFlag), "1", StringComparison.Ordinal))
            {
                _output.WriteLine(
                    $"Set {EnvFlag}=1 to run B2 visibility vs TempRename microbench "
                    + "(not an E2E StorageService claim; in-memory visibility is not durable).");
                return;
            }

            var ops = 4000;
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
            var root = ResolveBenchRoot();
            Directory.CreateDirectory(root);
            Emit(
                $"b2vis-config ops={ops} workers={workers} repeats={repeats} articleBytes={ArticleBytes} "
                + $"root={root} os={RuntimeInformation.OSDescription} cpus={Environment.ProcessorCount} "
                + $"activation={EnvFlag}=1 variants=TempRename,ImmutableBlobVisibility "
                + "fileOptions=FileMode.CreateNew,buffer=128KiB,SequentialScan,Flush(flushToDisk:false) "
                + "visibility=test_local_in_memory_map_NOT_crash_durable "
                + "note=no_ArticleId_final_rename_in_B2_path");

            try
            {
                for (var r = 1; r <= repeats; r++)
                {
                    var b2First = r % 2 == 0;
                    if (b2First)
                    {
                        RunBenchVariant(root, payload, ops, workers, BenchVariant.ImmutableBlobVisibility, r);
                        RunBenchVariant(root, payload, ops, workers, BenchVariant.TempRename, r);
                    }
                    else
                    {
                        RunBenchVariant(root, payload, ops, workers, BenchVariant.TempRename, r);
                        RunBenchVariant(root, payload, ops, workers, BenchVariant.ImmutableBlobVisibility, r);
                    }
                }

                // Narrow concurrency probe on visibility map only (optional follow-up cell).
                RunVisibilityContentionProbe(root, payload, workers: 8, ops: Math.Min(ops, 2000));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException ex)
                {
                    Emit($"b2vis-cleanup-warning {ex.GetType().Name}:{ex.Message}");
                }
            }
        }

        private void RunBenchVariant(
            string root,
            byte[] payload,
            int ops,
            int workers,
            BenchVariant variant,
            int repeat)
        {
            var runDir = Path.Join(root, $"{variant}-r{repeat}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(runDir);

            var publishMs = new ConcurrentBag<double>();
            var visibilityMs = new ConcurrentBag<double>();
            var retrieveMs = new ConcurrentBag<double>();
            var e2eMs = new ConcurrentBag<double>();
            var errors = 0;
            var duplicates = 0;
            var orphanCount = 0;
            var nextId = 0;
            var logicalIds = new ConcurrentBag<string>();

            B2PrototypeStore? b2 = null;
            if (variant == BenchVariant.ImmutableBlobVisibility)
            {
                b2 = B2PrototypeStore.Create(Path.Join(runDir, "b2"));
            }
            else
            {
                PrecreateShards(runDir);
            }

            var process = Process.GetCurrentProcess();
            process.Refresh();
            var wsStart = process.WorkingSet64;
            var cpuStart = process.TotalProcessorTime;
            var wall = Stopwatch.StartNew();

            try
            {
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

                            var logical = $"art-{id:x8}";
                            var e2eStart = Stopwatch.GetTimestamp();
                            try
                            {
                                if (variant == BenchVariant.TempRename)
                                {
                                    var pubStart = Stopwatch.GetTimestamp();
                                    var final = BuildTempRenameFinal(runDir, id);
                                    PublishTempRename(final, payload);
                                    publishMs.Add(Stopwatch.GetElapsedTime(pubStart).TotalMilliseconds);
                                    visibilityMs.Add(0); // rename is the visibility act; timed inside publish

                                    var retStart = Stopwatch.GetTimestamp();
                                    if (!FileContentsEqual(final, payload))
                                    {
                                        Interlocked.Increment(ref errors);
                                    }

                                    retrieveMs.Add(Stopwatch.GetElapsedTime(retStart).TotalMilliseconds);
                                    logicalIds.Add(final);
                                }
                                else
                                {
                                    var pubStart = Stopwatch.GetTimestamp();
                                    var published = b2!.Publish(logical, payload, visibilityElapsedMs: out var visMs);
                                    publishMs.Add(Stopwatch.GetElapsedTime(pubStart).TotalMilliseconds);
                                    visibilityMs.Add(visMs);
                                    if (published.Kind == B2PublishKind.Duplicate)
                                    {
                                        Interlocked.Increment(ref duplicates);
                                    }
                                    else if (published.Kind != B2PublishKind.Stored)
                                    {
                                        Interlocked.Increment(ref errors);
                                        continue;
                                    }

                                    var retStart = Stopwatch.GetTimestamp();
                                    // Same streaming content check as TempRename (map resolve + FileContentsEqual).
                                    if (!b2.TryResolveVisibleBlobPath(logical, out var blobPath)
                                        || !FileContentsEqual(blobPath, payload))
                                    {
                                        Interlocked.Increment(ref errors);
                                    }

                                    retrieveMs.Add(Stopwatch.GetElapsedTime(retStart).TotalMilliseconds);
                                    logicalIds.Add(logical);
                                }

                                e2eMs.Add(Stopwatch.GetElapsedTime(e2eStart).TotalMilliseconds);
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

                if (b2 is not null)
                {
                    orphanCount = b2.ListInvisibleBlobFiles().Count;
                }

                var verified = 0;
                var verifyFail = 0;
                foreach (var key in logicalIds.Take(VerifySample))
                {
                    bool ok;
                    if (variant == BenchVariant.TempRename)
                    {
                        ok = FileContentsEqual(key, payload);
                    }
                    else
                    {
                        ok = b2!.TryResolveVisibleBlobPath(key, out var blobPath)
                            && FileContentsEqual(blobPath, payload);
                    }

                    if (ok)
                    {
                        verified++;
                    }
                    else
                    {
                        verifyFail++;
                    }
                }

                process.Refresh();
                var wsEnd = process.WorkingSet64;
                var cpu = process.TotalProcessorTime - cpuStart;
                var cpuPct = wall.Elapsed.TotalMilliseconds > 0
                    ? 100d * cpu.TotalMilliseconds / (wall.Elapsed.TotalMilliseconds * Environment.ProcessorCount)
                    : 0d;

                var completed = logicalIds.Count;
                var pubArr = ToSorted(publishMs);
                var visArr = ToSorted(visibilityMs);
                var retArr = ToSorted(retrieveMs);
                var e2eArr = ToSorted(e2eMs);
                var pubPerSec = completed / Math.Max(wall.Elapsed.TotalSeconds, 0.001);
                var retPerSec = retrieveMs.Count / Math.Max(wall.Elapsed.TotalSeconds, 0.001);

                Emit(
                    $"b2vis-result variant={variant} repeat={repeat} opsRequested={ops} completed={completed} "
                    + $"errors={errors} duplicates={duplicates} orphanInvisibleBlobs={orphanCount} "
                    + $"workers={workers} wallMs={wall.Elapsed.TotalMilliseconds:0} "
                    + $"publishOpsPerSec={pubPerSec:0.00} retrieveOpsPerSec={retPerSec:0.00} "
                    + $"articleBytes={ArticleBytes} bytesWritten={completed * (long)ArticleBytes} "
                    + $"publishMs_p50={Pct(pubArr, 0.50):0.000} p95={Pct(pubArr, 0.95):0.000} p99={Pct(pubArr, 0.99):0.000} avg={Avg(pubArr):0.000} "
                    + $"visibilityMs_p50={Pct(visArr, 0.50):0.000} p95={Pct(visArr, 0.95):0.000} avg={Avg(visArr):0.000} "
                    + $"retrieveMs_p50={Pct(retArr, 0.50):0.000} p95={Pct(retArr, 0.95):0.000} p99={Pct(retArr, 0.99):0.000} avg={Avg(retArr):0.000} "
                    + $"e2ePublishRetrieveMs_p50={Pct(e2eArr, 0.50):0.000} p95={Pct(e2eArr, 0.95):0.000} p99={Pct(e2eArr, 0.99):0.000} "
                    + $"verifySample={VerifySample} verifiedOk={verified} verifyFail={verifyFail} "
                    + $"wsStart={wsStart} wsEnd={wsEnd} wsPeakObserved={Math.Max(wsStart, wsEnd)} "
                    + $"cpuMs={cpu.TotalMilliseconds:0} cpuPctOfMachine={cpuPct:0.0} runDir={runDir}");

                Assert.Equal(0, errors);
                Assert.Equal(0, verifyFail);
                Assert.Equal(ops, completed);
            }
            finally
            {
                b2?.Dispose();
            }
        }

        private void RunVisibilityContentionProbe(string root, byte[] payload, int workers, int ops)
        {
            var probeDir = Path.Join(root, "contention-" + Guid.NewGuid().ToString("N"));
            using var store = B2PrototypeStore.Create(probeDir);
            var next = 0;
            var errors = 0;
            var publishMs = new ConcurrentBag<double>();
            var wall = Stopwatch.StartNew();
            var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
            {
                while (true)
                {
                    var id = Interlocked.Increment(ref next);
                    if (id > ops)
                    {
                        return;
                    }

                    // High contention: many workers publish distinct ids (map insert pressure) plus a shared hot id.
                    var logical = id % 17 == 0 ? "hot-id" : $"c-{id:x}";
                    var started = Stopwatch.GetTimestamp();
                    var kind = store.Publish(logical, payload).Kind;
                    publishMs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    if (kind is not (B2PublishKind.Stored or B2PublishKind.Duplicate))
                    {
                        Interlocked.Increment(ref errors);
                    }
                }
            })).ToArray();
            Task.WaitAll(tasks);
            wall.Stop();
            var arr = ToSorted(publishMs);
            Emit(
                $"b2vis-contention workers={workers} ops={ops} wallMs={wall.Elapsed.TotalMilliseconds:0} errors={errors} "
                + $"visibilityCount={store.VisibilityCount} "
                + $"publishMs_p50={Pct(arr, 0.50):0.000} p95={Pct(arr, 0.95):0.000} p99={Pct(arr, 0.99):0.000} "
                + "note=mixed_unique_and_hot_logical_id_on_in_memory_map");
            Assert.Equal(0, errors);
        }

        private void Emit(string line)
        {
            Console.WriteLine(line);
            _output.WriteLine(line);
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

        private static string ResolveBenchRoot()
        {
            // bin/x64/Release/net10.0 → five ups = tests project; six ups = repo root.
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
                "b2-visibility-workdir"));
            return Path.Join(fromRepo, Guid.NewGuid().ToString("N"));
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

        private static string BuildTempRenameFinal(string runDir, int id)
        {
            var hex = id.ToString("x8");
            return Path.Join(runDir, hex[0].ToString(), hex[1].ToString(), hex[2].ToString(), hex + Guid.NewGuid().ToString("N"));
        }

        private static void PublishTempRename(string final, ReadOnlySpan<byte> payload)
        {
            var temp = final + ".writing-" + Guid.NewGuid().ToString("N");
            WriteNew(temp, payload);
            File.Move(temp, final, overwrite: true);
        }

        private static void WriteNew(string path, ReadOnlySpan<byte> content)
        {
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            stream.Write(content);
            stream.Flush(flushToDisk: false);
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

        private enum BenchVariant
        {
            TempRename,
            ImmutableBlobVisibility,
        }

        private enum B2PublishKind
        {
            Stored,
            Duplicate,
            Failed,
        }

        private sealed class B2PublishHooks
        {
            public bool FailBeforeBlobCreate { get; init; }

            public int? FailAfterBytesWritten { get; init; }

            public bool FailAfterWriteBeforeVisibility { get; init; }

            public bool FailVisibilityPublish { get; init; }
        }

        private readonly record struct B2PublishResult(B2PublishKind Kind, string? BlobId, string? BlobPath);

        /// <summary>
        /// Test-local B2 store: blobs under <c>blobs/</c>, visibility in a process-local concurrent map.
        /// Visibility is not durable across process crash.
        /// </summary>
        private sealed class B2PrototypeStore : IDisposable
        {
            private readonly string _root;
            private readonly string _blobDir;
            private readonly ConcurrentDictionary<string, string> _logicalToBlob = new(StringComparer.Ordinal);
            private readonly ConcurrentDictionary<string, int> _readLeases = new(StringComparer.Ordinal);
            private readonly ConcurrentDictionary<string, object> _identityLocks = new(StringComparer.Ordinal);
            private int _disposed;

            private B2PrototypeStore(string root)
            {
                _root = root;
                _blobDir = Path.Join(root, "blobs");
                Directory.CreateDirectory(_blobDir);
            }

            public string Root => _root;

            public int VisibilityCount => _logicalToBlob.Count;

            public static B2PrototypeStore Create(string? root = null)
            {
                root ??= Path.Join(Path.GetTempPath(), "b2proto-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                return new B2PrototypeStore(root);
            }

            public B2PublishResult Publish(string logicalId, ReadOnlySpan<byte> payload, B2PublishHooks? hooks = null) =>
                Publish(logicalId, payload, out _, hooks);

            public B2PublishResult Publish(
                string logicalId,
                ReadOnlySpan<byte> payload,
                out double visibilityElapsedMs,
                B2PublishHooks? hooks = null)
            {
                visibilityElapsedMs = 0;
                ArgumentException.ThrowIfNullOrWhiteSpace(logicalId);
                hooks ??= new B2PublishHooks();
                var gate = _identityLocks.GetOrAdd(logicalId, static _ => new object());
                lock (gate)
                {
                    if (_logicalToBlob.TryGetValue(logicalId, out var existing))
                    {
                        return new B2PublishResult(B2PublishKind.Duplicate, existing, BlobPathFor(existing));
                    }

                    if (hooks.FailBeforeBlobCreate)
                    {
                        return new B2PublishResult(B2PublishKind.Failed, null, null);
                    }

                    var blobId = Guid.NewGuid().ToString("N");
                    var blobPath = BlobPathFor(blobId);
                    try
                    {
                        WriteBlob(blobPath, payload, hooks.FailAfterBytesWritten);
                    }
                    catch
                    {
                        return new B2PublishResult(B2PublishKind.Failed, blobId, blobPath);
                    }

                    if (hooks.FailAfterWriteBeforeVisibility || hooks.FailVisibilityPublish)
                    {
                        return new B2PublishResult(B2PublishKind.Failed, blobId, blobPath);
                    }

                    var visStart = Stopwatch.GetTimestamp();
                    if (!_logicalToBlob.TryAdd(logicalId, blobId))
                    {
                        // Lost a race after write (should be rare under lock); treat as duplicate and leave orphan.
                        visibilityElapsedMs = Stopwatch.GetElapsedTime(visStart).TotalMilliseconds;
                        return new B2PublishResult(B2PublishKind.Duplicate, _logicalToBlob[logicalId], BlobPathFor(_logicalToBlob[logicalId]));
                    }

                    visibilityElapsedMs = Stopwatch.GetElapsedTime(visStart).TotalMilliseconds;
                    return new B2PublishResult(B2PublishKind.Stored, blobId, blobPath);
                }
            }

            public bool TryResolveVisibleBlobPath(string logicalId, out string blobPath)
            {
                blobPath = string.Empty;
                if (!_logicalToBlob.TryGetValue(logicalId, out var blobId))
                {
                    return false;
                }

                blobPath = BlobPathFor(blobId);
                return File.Exists(blobPath);
            }

            public bool TryRetrieve(string logicalId, out byte[] payload)
            {
                payload = [];
                if (!TryResolveVisibleBlobPath(logicalId, out var path))
                {
                    return false;
                }

                payload = File.ReadAllBytes(path);
                return true;
            }

            public bool TryDiscoverBlobByLogicalId(string logicalId, out string blobPath)
            {
                blobPath = string.Empty;
                // Prototype rule: no filesystem discovery from logical id.
                return false;
            }

            public bool IsVisible(string blobId) => _logicalToBlob.Values.Contains(blobId, StringComparer.Ordinal);

            public bool TryIdentifyOrphan(string blobPath, out string blobId)
            {
                blobId = Path.GetFileName(blobPath);
                return blobId.Length > 0 && File.Exists(blobPath) && !IsVisible(blobId);
            }

            public List<string> ListInvisibleBlobFiles()
            {
                if (!Directory.Exists(_blobDir))
                {
                    return [];
                }

                return Directory.EnumerateFiles(_blobDir)
                    .Where(path => !IsVisible(Path.GetFileName(path)))
                    .ToList();
            }

            public int CountBlobFiles() =>
                Directory.Exists(_blobDir) ? Directory.EnumerateFiles(_blobDir).Count() : 0;

            public IDisposable AcquireReadLease(string logicalId)
            {
                if (!_logicalToBlob.TryGetValue(logicalId, out var blobId))
                {
                    throw new InvalidOperationException("Cannot lease unpublished article.");
                }

                _readLeases.AddOrUpdate(blobId, 1, static (_, c) => c + 1);
                return new LeaseRelease(this, blobId);
            }

            public bool TryUnpublish(string logicalId)
            {
                if (!_logicalToBlob.TryGetValue(logicalId, out var blobId))
                {
                    return false;
                }

                if (_readLeases.TryGetValue(blobId, out var leases) && leases > 0)
                {
                    return false;
                }

                return _logicalToBlob.TryRemove(logicalId, out _);
            }

            public bool TryDeleteBlob(string blobId, bool requireInvisible)
            {
                if (requireInvisible && IsVisible(blobId))
                {
                    return false;
                }

                if (_readLeases.TryGetValue(blobId, out var leases) && leases > 0)
                {
                    return false;
                }

                var path = BlobPathFor(blobId);
                if (!File.Exists(path))
                {
                    return true;
                }

                File.Delete(path);
                return true;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                try
                {
                    if (Directory.Exists(_root))
                    {
                        Directory.Delete(_root, recursive: true);
                    }
                }
                catch (IOException)
                {
                }
            }

            private string BlobPathFor(string blobId) => Path.Join(_blobDir, blobId);

            private static void WriteBlob(string path, ReadOnlySpan<byte> content, int? failAfterBytes)
            {
                using var stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    FileOptions.SequentialScan);
                if (failAfterBytes is int n)
                {
                    n = Math.Clamp(n, 0, content.Length);
                    if (n > 0)
                    {
                        stream.Write(content[..n]);
                    }

                    stream.Flush(flushToDisk: false);
                    throw new IOException("Injected write failure after partial bytes.");
                }

                stream.Write(content);
                stream.Flush(flushToDisk: false);
            }

            private void ReleaseLease(string blobId)
            {
                _readLeases.AddOrUpdate(
                    blobId,
                    0,
                    static (_, c) => c <= 1 ? 0 : c - 1);
                if (_readLeases.TryGetValue(blobId, out var c) && c == 0)
                {
                    _readLeases.TryRemove(blobId, out _);
                }
            }

            private sealed class LeaseRelease(B2PrototypeStore owner, string blobId) : IDisposable
            {
                private int _disposed;

                public void Dispose()
                {
                    if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    {
                        return;
                    }

                    owner.ReleaseLease(blobId);
                }
            }
        }
    }
}

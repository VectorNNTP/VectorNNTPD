using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles.Processing;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.StorageService.Storage.SegmentPools;
using VectorNNTPD.StorageService.Storage.SpoolPersistence;
using VectorNNTPD.StorageService.Storage.SpoolShards;
using Xunit.Abstractions;

namespace VectorNNTPD.StorageService.Tests.Storage
{

    /// <summary>
    /// Short filesystem publish measurement on the process temp volume.
    /// This is not a dedicated-disk run and it is not a 10 Gbps result.
    /// 10 Gbps at 768,000-byte articles is about 1,628 articles/s.
    /// </summary>
    public sealed class SpoolPersistenceBenchTests
    {
        private readonly ITestOutputHelper _output;

        public SpoolPersistenceBenchTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Temp_volume_publish_reports_rate_and_latency()
        {
            var largeCount = int.TryParse(Environment.GetEnvironmentVariable("VECTORNNTP_SPOOL_BENCH_COUNT"), out var configured) && configured > 0
                ? configured
                : 24;
            const int largeBytes = 750 * 1024;
            const int smallCount = 24;
            const int smallBytes = 4 * 1024;
            var rootA = NewRoot();
            var rootB = NewRoot();
            var files = new FileSpoolArticleFiles();
            var publisher = new SpoolArticlePublisher(files);
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var workingSet = Process.GetCurrentProcess().WorkingSet64;
            try
            {
                var large = Measure(publisher, rootA, "large", largeCount, largeBytes, duplicate: true);
                var small = Measure(publisher, rootA, "small", smallCount, smallBytes, duplicate: false);
                var other = Measure(publisher, rootB, "second-root", largeCount / 2, largeBytes, duplicate: false);
                var after = GC.GetTotalAllocatedBytes(precise: true);
                _output.WriteLine(
                    $"environment=temp-volume path={Path.GetTempPath()} os={RuntimeInformation.OSDescription} dedicatedDisk=false sameVolume=true");
                _output.WriteLine(large);
                _output.WriteLine(small);
                _output.WriteLine(other);
                _output.WriteLine(
                    $"allocatedBytes={after - before} workingSetBefore={workingSet} not-a-10Gbps-claim targetArticlesPerSec=1628");
                Assert.Contains("articlesPerSec=", large, StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(rootA, recursive: true);
                Directory.Delete(rootB, recursive: true);
            }
        }

        [Fact]
        public async Task Pipeline_reports_managed_allocations_without_a_disk()
        {
            const int count = 16;
            const int bodyBytes = 750 * 1024;
            var root = OperatingSystem.IsWindows() ? @"C:\spool-bench" : "/spool-bench";
            var probe = new FixedProbe(root, total: 1L << 40, available: 1L << 39);
            var catalog = new SegmentPoolCatalog();
            catalog.Publish([new SegmentPoolDevice("block:bench", [root])]);
            var monitor = new BlockDeviceMonitorService(
                catalog,
                new FlatCounters(),
                NullLogger<BlockDeviceMonitorService>.Instance,
                TimeSpan.FromHours(1),
                TimeProvider.System,
                probe);
            monitor.GetType().GetMethod("Sample", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(monitor, null);
            var sink = new CountingFiles();
            var processor = new SpoolArticlePersistenceProcessor(
                "cache01.usenet.ninja",
                catalog,
                monitor,
                new SpoolWriteGate(),
                new SpoolArticleIdentityGate(),
                new SpoolHealth(TimeProvider.System),
                new SpoolPressure(),
                new SpoolArticlePublisher(sink),
                new SpoolPersistenceCounters(),
                new StorageServerOptions(),
                NullLogger.Instance,
                TimeProvider.System);
            var articles = new AcceptedArticle[count];
            for (var i = 0; i < count; i++)
            {
                articles[i] = BuildArticle(i, bodyBytes);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            var started = Stopwatch.GetTimestamp();
            long payload = 0;
            for (var i = 0; i < count; i++)
            {
                var result = await processor.ProcessAsync(articles[i], CancellationToken.None);
                Assert.Equal(AcceptedArticleProcessResult.Completed, result);
                payload += articles[i].ArtSize;
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            cpu = Process.GetCurrentProcess().TotalProcessorTime - cpu;
            var perArticle = allocated / (double)count;
            var artPerSec = count / elapsed.TotalSeconds;
            var line =
                $"pipeline articles={count} elapsedMs={elapsed.TotalMilliseconds:0} articlesPerSec={artPerSec:0.0} "
                + $"payloadMegabitsPerSec={payload * 8d / elapsed.TotalSeconds / 1_000_000d:0.0} "
                + $"allocatedBytes={allocated} allocatedBytesPerArticle={perArticle:0} "
                + $"gen0={GC.CollectionCount(0) - gen0} gen1={GC.CollectionCount(1) - gen1} gen2={GC.CollectionCount(2) - gen2} "
                + $"cpuMs={cpu.TotalMilliseconds:0} writes={sink.Writes} sinkAllocated={sink.Allocated} "
                + $"os={RuntimeInformation.OSDescription} disk=false";
            Console.WriteLine(line);
            _output.WriteLine(line);
            Assert.Equal(count, sink.Writes);
        }

        [Fact]
        public async Task Allocation_breakdown_separates_rewrite_from_publish()
        {
            const int count = 16;
            const int bodyBytes = 750 * 1024;
            var articles = new AcceptedArticle[count];
            for (var i = 0; i < count; i++)
            {
                articles[i] = BuildArticle(i, bodyBytes);
            }

            var path = new AcceptedArticlePathProcessor("cache01.usenet.ninja", NullLogger.Instance);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var beforeRewrite = GC.GetTotalAllocatedBytes(precise: true);
            long rewrittenBytes = 0;
            for (var i = 0; i < count; i++)
            {
                Assert.True(path.TryRewrite(articles[i], out var rewritten));
                rewrittenBytes += rewritten.Length;
            }

            var rewriteAllocated = GC.GetTotalAllocatedBytes(precise: true) - beforeRewrite;
            var bundle = CreatePipeline(files: null, roots: null);
            var processor = bundle.Processor;
            var sink = bundle.Counting!;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var beforePipeline = GC.GetTotalAllocatedBytes(precise: true);
            for (var i = 0; i < count; i++)
            {
                var result = await processor.ProcessAsync(articles[i], CancellationToken.None);
                Assert.Equal(AcceptedArticleProcessResult.Completed, result);
            }

            var pipelineAllocated = GC.GetTotalAllocatedBytes(precise: true) - beforePipeline;
            var line =
                $"allocation-breakdown articles={count} rewriteAllocated={rewriteAllocated} rewritePerArticle={rewriteAllocated / (double)count:0} "
                + $"rewrittenBytesPerArticle={rewrittenBytes / (double)count:0} pipelineAllocated={pipelineAllocated} "
                + $"pipelinePerArticle={pipelineAllocated / (double)count:0} pipelineMinusRewritePerArticle={(pipelineAllocated - rewriteAllocated) / (double)count:0} "
                + $"sinkBytes={sink.Allocated} duplicateCompares=0 filesystem=false logging=NullLogger os={RuntimeInformation.OSDescription}";
            Console.WriteLine(line);
            _output.WriteLine(line);
        }

        [Fact]
        public void Warm_path_stage_allocations_are_reported()
        {
            const int count = 128;
            var ids = new ArticleId[count];
            for (var i = 0; i < count; i++)
            {
                ids[i] = ArticleId.FromMessageId(System.Text.Encoding.ASCII.GetBytes($"<stage-{i}@example.test>"));
            }

            var roots = new[]
            {
                OperatingSystem.IsWindows() ? @"C:\spool-bench-a" : "/spool-bench-a",
                OperatingSystem.IsWindows() ? @"C:\spool-bench-b" : "/spool-bench-b",
                OperatingSystem.IsWindows() ? @"C:\spool-bench-c" : "/spool-bench-c",
                OperatingSystem.IsWindows() ? @"C:\spool-bench-d" : "/spool-bench-d",
            };
            var catalog = new SegmentPoolCatalog();
            catalog.Publish([new SegmentPoolDevice("block:bench", roots)]);
            var monitor = new BlockDeviceMonitorService(
                catalog,
                new FlatCounters(),
                NullLogger<BlockDeviceMonitorService>.Instance,
                TimeSpan.FromHours(1),
                TimeProvider.System,
                new AnyProbe(1L << 40, 1L << 39));
            monitor.GetType().GetMethod("Sample", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(monitor, null);
            var gate = new SpoolWriteGate();
            var health = new SpoolHealth(TimeProvider.System);
            var identity = new SpoolArticleIdentityGate();
            var buffer = System.Buffers.ArrayPool<SpoolCandidate>.Shared.Rent(roots.Length);
            for (var i = 0; i < 16; i++)
            {
                using var warmup = identity.Acquire(ids[i], CancellationToken.None);
                var warmed = SpoolCandidateBuilder.Fill(catalog, monitor.Observations, gate, health, 2, TimeProvider.System, buffer);
                if (gate.TryReserve(buffer.AsSpan(0, warmed), 1024, out var chosen))
                {
                    gate.Exit(chosen.SpoolRoot);
                }

                _ = SpoolArticlePath.CombineArticle(roots[0], ids[i]);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None, CancellationToken.None);
            }

            var linkedBytes = Measure(count, i =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None, CancellationToken.None);
            });
            var identityBytes = Measure(count, i =>
            {
                using var lease = identity.Acquire(ids[i], CancellationToken.None);
            });
            var candidateBytes = Measure(count, _ =>
            {
                var written = SpoolCandidateBuilder.Fill(catalog, monitor.Observations, gate, health, 2, TimeProvider.System, buffer);
                if (gate.TryReserve(buffer.AsSpan(0, written), 1024, out var chosen))
                {
                    gate.Exit(chosen.SpoolRoot);
                }
            });
            var pathBytes = Measure(count, i =>
            {
                for (var root = 0; root < roots.Length; root++)
                {
                    _ = SpoolArticlePath.CombineArticle(roots[root], ids[i]);
                }

                _ = SpoolArticlePath.CombineArticle(roots[0], ids[i]);
            });
            var line =
                $"stage-allocations count={count} linkedCtsPerCall={linkedBytes / (double)count:0} identityPerCall={identityBytes / (double)count:0} "
                + $"fillAndReservePerCall={candidateBytes / (double)count:0} combineArticlePerCall={pathBytes / (double)count:0} "
                + $"os={RuntimeInformation.OSDescription}";
            Console.WriteLine(line);
            _output.WriteLine(line);
            System.Buffers.ArrayPool<SpoolCandidate>.Shared.Return(buffer);
        }

        private static long Measure(int count, Action<int> action)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetTotalAllocatedBytes(precise: true);
            for (var i = 0; i < count; i++)
            {
                action(i);
            }

            return GC.GetTotalAllocatedBytes(precise: true) - before;
        }

        [Fact]
        public async Task Concurrent_workers_report_cpu_sink_throughput()
        {
            // Sized for Linux CI under Coverlet: 384×750 KiB at ~5 art/s (observed concurrent
            // rate on ubuntu-latest) exceeds the 60s worker-completion wait. Keep 1/2/4/8 workers
            // and 750 KiB payloads; shrink the batch so each pool still finishes with margin.
            const int count = 64;
            const int bodyBytes = 750 * 1024;
            var articles = new AcceptedArticle[count];
            for (var i = 0; i < count; i++)
            {
                articles[i] = BuildArticle(i + 1000, bodyBytes);
            }

            var warmup = CreatePipeline(files: null, roots: null);
            for (var i = 0; i < 8; i++)
            {
                Assert.Equal(
                    AcceptedArticleProcessResult.Completed,
                    await warmup.Processor.ProcessAsync(articles[i], CancellationToken.None));
            }

            foreach (var workers in new[] { 1, 2, 4, 8 })
            {
                var line = await RunWorkerPool(articles, workers, $"cpu-sink workers={workers}");
                Console.WriteLine(line);
                _output.WriteLine(line);
            }
        }

        [Fact]
        public async Task Concurrent_workers_report_mixed_sizes_on_cpu_sink()
        {
            var articles = new AcceptedArticle[48];
            for (var i = 0; i < 40; i++)
            {
                articles[i] = BuildArticle(5000 + i, 750 * 1024);
            }

            for (var i = 0; i < 6; i++)
            {
                articles[40 + i] = BuildArticle(6000 + i, 2 * 1024 * 1024);
            }

            articles[46] = BuildArticle(7000, 4 * 1024 * 1024);
            articles[47] = BuildArticle(7001, 256 * 1024);
            var line = await RunWorkerPool(articles, 4, "cpu-sink-mix workers=4");
            Console.WriteLine(line);
            _output.WriteLine(line);
        }

        [Fact]
        public async Task Concurrent_workers_report_temp_volume_publication()
        {
            const int count = 32;
            const int workers = 4;
            var articles = new AcceptedArticle[count];
            for (var i = 0; i < count; i++)
            {
                articles[i] = BuildArticle(8000 + i, 750 * 1024);
            }

            var root = NewRoot();
            try
            {
                var files = new FileSpoolArticleFiles();
                var timing = new TimingFiles(files);
                var line = await RunWorkerPool(articles, workers, "temp-volume workers=4", timing, [root]);
                Console.WriteLine(line + " " + timing.Report() + $" path={Path.GetTempPath()} dedicatedDisk=false");
                _output.WriteLine(line);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private async Task<string> RunWorkerPool(
            AcceptedArticle[] articles,
            int workers,
            string label,
            ISpoolArticleFiles? files = null,
            string[]? roots = null)
        {
            var pipeline = CreatePipeline(files, roots);
            var latency = new LatencyProcessor(pipeline.Processor, articles.Length);
            var queue = new AcceptedArticleQueue();
            var options = new SpoolPersistenceOptions
            {
                MinWorkers = workers,
                MaxWorkers = workers,
                MaxInFlightPerSpool = 2,
                // Min==Max and ScaleDwell is long, so the control loop never scales. Use the
                // production interval: a 1 ms spin steals CPU from workers on covered CI runners.
                ControlIntervalMilliseconds = SpoolPersistenceOptions.DefaultControlIntervalMilliseconds,
                ScaleDwellMilliseconds = 60_000,
            };
            var worker = new AcceptedArticleWorker(
                queue,
                latency,
                NullLogger<AcceptedArticleWorker>.Instance,
                options,
                new SpoolPressure(),
                new SpoolPersistenceCounters(),
                TimeProvider.System);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var pause = GC.GetTotalPauseDuration();
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            var workingSet = Process.GetCurrentProcess().WorkingSet64;
            var started = Stopwatch.GetTimestamp();
            await worker.StartAsync(CancellationToken.None);
            long payload = 0;
            var enqueued = 0;
            for (var i = 0; i < articles.Length; i++)
            {
                if (!queue.TryEnqueue(articles[i]))
                {
                    break;
                }

                enqueued++;
                payload += articles[i].ArtSize;
            }

            var queuedAfterFill = queue.QueuedCount;
            queue.Complete();
            await worker.Execution!.WaitAsync(TimeSpan.FromSeconds(60));
            await worker.StopAsync(CancellationToken.None);
            var elapsed = Stopwatch.GetElapsedTime(started);
            allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            cpu = Process.GetCurrentProcess().TotalProcessorTime - cpu;
            pause = GC.GetTotalPauseDuration() - pause;
            var info = GC.GetGCMemoryInfo();
            var loh = info.GenerationInfo.Length > 3 ? info.GenerationInfo[3].SizeAfterBytes : -1;
            var samples = latency.Snapshot();
            Array.Sort(samples);
            var perArticle = enqueued == 0 ? 0 : allocated / (double)enqueued;
            return
                $"{label} articles={enqueued} elapsedMs={elapsed.TotalMilliseconds:0} articlesPerSec={enqueued / elapsed.TotalSeconds:0.0} "
                + $"payloadMegabitsPerSec={payload * 8d / elapsed.TotalSeconds / 1_000_000d:0.0} "
                + $"p50Ms={Percentile(samples, 0.50):0.00} p95Ms={Percentile(samples, 0.95):0.00} p99Ms={Percentile(samples, 0.99):0.00} "
                + $"allocatedBytesPerArticle={perArticle:0} gen0={GC.CollectionCount(0) - gen0} gen1={GC.CollectionCount(1) - gen1} gen2={GC.CollectionCount(2) - gen2} "
                + $"gcPauseMs={pause.TotalMilliseconds:0.00} heapBytes={info.HeapSizeBytes} lohBytes={loh} "
                + $"workingSetBefore={workingSet} workingSetAfter={Process.GetCurrentProcess().WorkingSet64} managedBytes={GC.GetTotalMemory(false)} "
                + $"cpuMs={cpu.TotalMilliseconds:0} queuedAfterFill={queuedAfterFill} writes={pipeline.SinkWrites} distribution={pipeline.Distribution} "
                + $"spools={pipeline.SpoolCount} sink={(files is null ? "memory" : "filesystem")} os={RuntimeInformation.OSDescription}";
        }

        private static PipelineBundle CreatePipeline(ISpoolArticleFiles? files, string[]? roots)
        {
            roots ??=
            [
                OperatingSystem.IsWindows() ? @"C:\spool-bench-a" : "/spool-bench-a",
                OperatingSystem.IsWindows() ? @"C:\spool-bench-b" : "/spool-bench-b",
                OperatingSystem.IsWindows() ? @"C:\spool-bench-c" : "/spool-bench-c",
                OperatingSystem.IsWindows() ? @"C:\spool-bench-d" : "/spool-bench-d",
            ];
            var counting = files is null ? new CountingFiles(roots) : null;
            var sink = files ?? counting!;
            var catalog = new SegmentPoolCatalog();
            catalog.Publish([new SegmentPoolDevice("block:bench", roots)]);
            var monitor = new BlockDeviceMonitorService(
                catalog,
                new FlatCounters(),
                NullLogger<BlockDeviceMonitorService>.Instance,
                TimeSpan.FromHours(1),
                TimeProvider.System,
                new AnyProbe(1L << 40, 1L << 39));
            monitor.GetType().GetMethod("Sample", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(monitor, null);
            var processor = new SpoolArticlePersistenceProcessor(
                "cache01.usenet.ninja",
                catalog,
                monitor,
                new SpoolWriteGate(),
                new SpoolArticleIdentityGate(),
                new SpoolHealth(TimeProvider.System),
                new SpoolPressure(),
                new SpoolArticlePublisher(sink),
                new SpoolPersistenceCounters(),
                new StorageServerOptions
                {
                    SpoolPersistence = new SpoolPersistenceOptions { MaxInFlightPerSpool = 2 },
                },
                NullLogger.Instance,
                TimeProvider.System);
            return new PipelineBundle(processor, counting, roots.Length, sink);
        }

        private readonly record struct PipelineBundle(
            SpoolArticlePersistenceProcessor Processor,
            CountingFiles? Counting,
            int SpoolCount,
            ISpoolArticleFiles Files)
        {
            public int SinkWrites => Files is CountingFiles counting ? counting.Writes : -1;

            public string Distribution => Files is CountingFiles counting ? counting.Distribution : "filesystem";
        }

        [Fact]
        public void Rewrite_component_times_are_reported()
        {
            const int count = 16;
            const int bodyBytes = 750 * 1024;
            var parser = new NntpArticleParser("cache01.usenet.ninja");
            var articles = new AcceptedArticle[count];
            for (var i = 0; i < count; i++)
            {
                articles[i] = BuildArticle(i, bodyBytes);
            }

            var parseTicks = Time(count, i =>
            {
                var parsed = parser.Parse(articles[i].ArtData);
                Assert.True(parsed.IsAccepted);
            });
            var materializeTicks = Time(count, i =>
            {
                var parsed = parser.Parse(articles[i].ArtData);
                var materialized = NntpArticleCanonicalMaterializer.Materialize(in parsed, ArticlePathMode.Traverse);
                Assert.True(materialized.IsAccepted);
            });
            var hashTicks = Time(count, i =>
            {
                _ = System.IO.Hashing.XxHash3.HashToUInt64(articles[i].ArtData.Span);
            });
            var factoryTicks = Time(count, i =>
            {
                var created = ArticleRecordFactory.TryCreate(parser, articles[i].ArtData, ArticlePathMode.Traverse);
                Assert.True(created.IsAccepted);
            });
            var line =
                $"rewrite-components articles={count} parseMs={Ms(parseTicks):0.00} parsePlusMaterializeMs={Ms(materializeTicks):0.00} "
                + $"xxh3Ms={Ms(hashTicks):0.00} factoryMs={Ms(factoryTicks):0.00} os={RuntimeInformation.OSDescription}";
            Console.WriteLine(line);
            _output.WriteLine(line);
        }

        private static long Time(int count, Action<int> action)
        {
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < count; i++)
            {
                action(i);
            }

            return Stopwatch.GetTimestamp() - start;
        }

        private static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;

        [Fact]
        public void Filesystem_publish_reports_managed_allocations()
        {
            const int count = 8;
            const int size = 750 * 1024;
            var root = NewRoot();
            var timing = new TimingFiles(new FileSpoolArticleFiles());
            var publisher = new SpoolArticlePublisher(timing);
            var bodies = new byte[count][];
            var ids = new ArticleId[count];
            for (var i = 0; i < count; i++)
            {
                bodies[i] = new byte[size];
                bodies[i][0] = (byte)i;
                ids[i] = ArticleId.FromMessageId(System.Text.Encoding.ASCII.GetBytes($"<fs-{i}@example.test>"));
                var final = SpoolArticlePath.CombineUnderRoot(root, SpoolArticlePath.RelativePath(ids[i]));
                Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            var started = Stopwatch.GetTimestamp();
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var result = publisher.Publish(root, ids[i], bodies[i], CancellationToken.None);
                    Assert.Equal(SpoolPublishKind.Stored, result.Kind);
                }

                var elapsed = Stopwatch.GetElapsedTime(started);
                allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
                cpu = Process.GetCurrentProcess().TotalProcessorTime - cpu;
                var line =
                    $"filesystem articles={count} elapsedMs={elapsed.TotalMilliseconds:0} articlesPerSec={count / elapsed.TotalSeconds:0.0} "
                    + $"payloadMegabitsPerSec={count * (long)size * 8d / elapsed.TotalSeconds / 1_000_000d:0.0} "
                    + $"allocatedBytes={allocated} allocatedBytesPerArticle={allocated / (double)count:0} "
                    + $"gen0={GC.CollectionCount(0) - gen0} gen1={GC.CollectionCount(1) - gen1} gen2={GC.CollectionCount(2) - gen2} "
                    + $"cpuMs={cpu.TotalMilliseconds:0} path={Path.GetTempPath()} os={RuntimeInformation.OSDescription} dedicatedDisk=false "
                + timing.Report();
                Console.WriteLine(line);
                _output.WriteLine(line);

                var leaf = Path.GetDirectoryName(SpoolArticlePath.CombineUnderRoot(root, SpoolArticlePath.RelativePath(ids[0])))!;
                var cachedStart = Stopwatch.GetTimestamp();
                for (var n = 0; n < 64; n++)
                {
                    timing.CreateDirectory(leaf);
                }

                var cachedMs = (Stopwatch.GetTimestamp() - cachedStart) * 1000d / Stopwatch.Frequency;
                var rawStart = Stopwatch.GetTimestamp();
                for (var n = 0; n < 64; n++)
                {
                    Directory.CreateDirectory(leaf);
                }

                var rawMs = (Stopwatch.GetTimestamp() - rawStart) * 1000d / Stopwatch.Frequency;
                var repeat =
                    $"directory-repeat calls=64 cachedMs={cachedMs:0.00} rawCreateDirectoryMs={rawMs:0.00} dedicatedDisk=false";
                Console.WriteLine(repeat);
                _output.WriteLine(repeat);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static AcceptedArticle BuildArticle(int index, int bodyBytes)
        {
            var parser = new NntpArticleParser("nntpd01.usenet.ninja");
            var body = new byte[bodyBytes];
            var line = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\r\n"u8;
            for (var offset = 0; offset < body.Length; offset += line.Length)
            {
                var count = Math.Min(line.Length, body.Length - offset);
                line.Slice(0, count).CopyTo(body.AsSpan(offset, count));
            }

            body[0] = (byte)index;

            var header = System.Text.Encoding.ASCII.GetBytes(
                $"Path: peer.example\r\nDate: Fri, 23 Aug 2024 07:30:10 +0000\r\nMessage-ID: <bench-{index}@example.test>\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\nSubject: bench\r\n\r\n");
            var raw = new byte[header.Length + body.Length];
            header.CopyTo(raw, 0);
            body.CopyTo(raw, header.Length);
            var created = ArticleRecordFactory.TryCreate(parser, raw);
            Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
            return new AcceptedArticle(
                created.Record.ArtId,
                created.Record.ArtHash,
                created.Record.ArtSize,
                created.Record.ArtData);
        }

        private static string Measure(
            SpoolArticlePublisher publisher,
            string root,
            string label,
            int count,
            int size,
            bool duplicate)
        {
            var store = new List<double>(count);
            var copies = new List<double>(duplicate ? count : 0);
            long bytes = 0;
            long storeTicks = 0;
            long duplicateTicks = 0;
            for (var i = 0; i < count; i++)
            {
                var body = new byte[size];
                body[0] = (byte)i;
                var id = ArticleId.FromMessageId(
                    System.Text.Encoding.ASCII.GetBytes($"<{label}-{i}-{size}@example.test>"));
                var one = Stopwatch.GetTimestamp();
                var result = publisher.Publish(root, id, body, CancellationToken.None);
                var storeElapsed = Stopwatch.GetTimestamp() - one;
                storeTicks += storeElapsed;
                store.Add(Stopwatch.GetElapsedTime(one).TotalMilliseconds);
                Assert.Equal(SpoolPublishKind.Stored, result.Kind);
                bytes += body.Length;
                if (!duplicate)
                {
                    continue;
                }

                var copy = Stopwatch.GetTimestamp();
                var again = publisher.Publish(root, id, body, CancellationToken.None);
                duplicateTicks += Stopwatch.GetTimestamp() - copy;
                copies.Add(Stopwatch.GetElapsedTime(copy).TotalMilliseconds);
                Assert.Equal(SpoolPublishKind.Replaced, again.Kind);
            }

            var storeSeconds = storeTicks / (double)Stopwatch.Frequency;
            store.Sort();
            var storeRate = count / storeSeconds;
            var megabits = bytes * 8d / storeSeconds / 1_000_000d;
            var line =
                $"label={label} articles={count} payloadBytes={bytes} storeElapsedMs={storeSeconds * 1000:0} articlesPerSec={storeRate:0.0} payloadMegabitsPerSec={megabits:0.0} storeP50Ms={Percentile(store, 0.50):0.00} storeP95Ms={Percentile(store, 0.95):0.00} storeP99Ms={Percentile(store, 0.99):0.00}";
            if (copies.Count > 0)
            {
                copies.Sort();
                var duplicateSeconds = duplicateTicks / (double)Stopwatch.Frequency;
                line += $" duplicateElapsedMs={duplicateSeconds * 1000:0} duplicatePerSec={count / duplicateSeconds:0.0} duplicateP50Ms={Percentile(copies, 0.50):0.00} duplicateP95Ms={Percentile(copies, 0.95):0.00}";
            }

            return line;
        }

        private static double Percentile(List<double> sorted, double percentile)
        {
            var index = (int)Math.Ceiling(sorted.Count * percentile) - 1;
            if (index < 0)
            {
                index = 0;
            }

            return sorted[Math.Min(index, sorted.Count - 1)];
        }

        private static double Percentile(long[] sortedTicks, double percentile)
        {
            if (sortedTicks.Length == 0)
            {
                return 0;
            }

            var index = (int)Math.Ceiling(sortedTicks.Length * percentile) - 1;
            if (index < 0)
            {
                index = 0;
            }

            index = Math.Min(index, sortedTicks.Length - 1);
            return sortedTicks[index] * 1000d / Stopwatch.Frequency;
        }

        private static string NewRoot()
        {
            var path = Path.Join(Path.GetTempPath(), "vectornntp-bench-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(path);
            return path;
        }

        private sealed class FixedProbe : ISpoolFreeSpaceProbe
        {
            private readonly string _path;
            private readonly long _total;
            private readonly long _available;

            public FixedProbe(string path, long total, long available)
            {
                _path = path;
                _total = total;
                _available = available;
            }

            public bool TryRead(string spoolRoot, out long totalBytes, out long availableBytes)
            {
                if (!string.Equals(spoolRoot, _path, StringComparison.Ordinal))
                {
                    totalBytes = 0;
                    availableBytes = 0;
                    return false;
                }

                totalBytes = _total;
                availableBytes = _available;
                return true;
            }
        }

        private sealed class FlatCounters : IBlockDeviceCounterSource
        {
            public bool TryRead(string deviceId, out BlockDeviceCounters counters, out string? error)
            {
                counters = new BlockDeviceCounters(1, 1, 1, 1, 1, 1, LatencyAvailable: true);
                error = null;
                return true;
            }
        }

        private sealed class AnyProbe(long _total, long _available) : ISpoolFreeSpaceProbe
        {
            public bool TryRead(string spoolRoot, out long totalBytes, out long availableBytes)
            {
                totalBytes = _total;
                availableBytes = _available;
                return true;
            }
        }

        private sealed class LatencyProcessor(IAcceptedArticleProcessor inner, int capacity) : IAcceptedArticleProcessor
        {
            private readonly long[] _ticks = new long[capacity];
            private int _count;

            public void EnsureReady() => inner.EnsureReady();

            public ValueTask<AcceptedArticleProcessResult> ProcessAsync(AcceptedArticle article, CancellationToken cancellationToken)
            {
                var start = Stopwatch.GetTimestamp();
                var result = inner.ProcessAsync(article, cancellationToken);
                var slot = Interlocked.Increment(ref _count) - 1;
                if ((uint)slot < (uint)_ticks.Length)
                {
                    _ticks[slot] = Stopwatch.GetTimestamp() - start;
                }

                return result;
            }

            public long[] Snapshot()
            {
                var count = Math.Min(_count, _ticks.Length);
                var copy = new long[count];
                Array.Copy(_ticks, copy, count);
                return copy;
            }
        }

        private sealed class CountingFiles : ISpoolArticleFiles
        {
            private readonly string[] _roots;
            private readonly int[] _perRoot;
            private int _writes;
            private long _allocated;

            public CountingFiles(string[]? roots = null)
            {
                _roots = roots ?? [];
                _perRoot = new int[_roots.Length];
            }

            public int Writes => _writes;

            public long Allocated => _allocated;

            public string Distribution
            {
                get
                {
                    var parts = new string[_perRoot.Length];
                    for (var i = 0; i < _perRoot.Length; i++)
                    {
                        parts[i] = _perRoot[i].ToString();
                    }

                    return string.Join(',', parts);
                }
            }

            public bool Exists(string path) => false;

            public long Length(string path) => 0;

            public bool ContentsEqual(string path, ReadOnlySpan<byte> content) => false;

            public void CreateDirectory(string path)
            {
            }

            public void Delete(string path)
            {
            }

            public void WriteAll(string path, ReadOnlySpan<byte> content, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _writes);
                Interlocked.Add(ref _allocated, content.Length);
                for (var i = 0; i < _roots.Length; i++)
                {
                    if (path.StartsWith(_roots[i], StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref _perRoot[i]);
                        break;
                    }
                }
            }

            public void MoveNew(string source, string destination)
            {
            }
        }

        private sealed class TimingFiles : ISpoolArticleFiles
        {
            private readonly ISpoolArticleFiles _inner;
            private long _exists;
            private long _directory;
            private long _write;
            private long _move;
            private int _calls;

            public TimingFiles(ISpoolArticleFiles inner) => _inner = inner;

            public string Report() =>
                $"existsMs={Ticks(_exists):0.00} directoryMs={Ticks(_directory):0.00} writeMs={Ticks(_write):0.00} moveMs={Ticks(_move):0.00} calls={_calls}";

            public bool Exists(string path)
            {
                var start = Stopwatch.GetTimestamp();
                var result = _inner.Exists(path);
                _exists += Stopwatch.GetTimestamp() - start;
                _calls++;
                return result;
            }

            public long Length(string path) => _inner.Length(path);

            public bool ContentsEqual(string path, ReadOnlySpan<byte> content) => _inner.ContentsEqual(path, content);

            public void CreateDirectory(string path)
            {
                var start = Stopwatch.GetTimestamp();
                _inner.CreateDirectory(path);
                _directory += Stopwatch.GetTimestamp() - start;
            }

            public void Delete(string path) => _inner.Delete(path);

            public void WriteAll(string path, ReadOnlySpan<byte> content, CancellationToken cancellationToken)
            {
                var start = Stopwatch.GetTimestamp();
                _inner.WriteAll(path, content, cancellationToken);
                _write += Stopwatch.GetTimestamp() - start;
            }

            public void MoveNew(string source, string destination)
            {
                var start = Stopwatch.GetTimestamp();
                _inner.MoveNew(source, destination);
                _move += Stopwatch.GetTimestamp() - start;
            }

            private static double Ticks(long ticks) => ticks * 1000d / Stopwatch.Frequency;
        }
    }
}

using System.Text.Json;

namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Opt-in BackFiller end-to-end stress entry points.
    /// Disabled unless <c>VECTORNNTPD_BACKFILLER_E2E_STRESS=1</c>.
    /// </summary>
    /// <remarks>
    /// Data path under test: publish Article Work to <c>backfiller.{backbone}</c> → BackFiller
    /// upstream ARTICLE → retention → TLS listener ARTICLE. RabbitMQ replies are metadata only.
    /// </remarks>
    public sealed class BackFillerE2EStressBench
    {
        /// <summary>When stress is enabled, discovers ids from the configured corpus and asserts non-zero valid/unique counts.</summary>
        [Fact]
        public async Task Discover_workload_from_newslogs_when_enabled()
        {
            if (!StressHarnessOptions.IsEnabled())
            {
                return;
            }

            var options = StressHarnessOptions.FromEnvironment();
            if (!Directory.Exists(options.NewsLogDirectory))
            {
                Assert.Fail($"News log directory missing: {options.NewsLogDirectory}");
            }

            var harness = new BackFillerE2EStressHarness(options);
            var stats = await harness.DiscoverAndWriteWorkloadAsync(CancellationToken.None);
            Assert.True(stats.ValidIds > 0);
            Assert.True(stats.UniqueIds > 0);
            Console.WriteLine(
                $"discovery files={stats.FileCount} entries={stats.TotalLogEntries} valid={stats.ValidIds} unique={stats.UniqueIds} malformed={stats.MalformedEntries} dupRate={stats.DuplicateRatePercent:F2}% giganews={stats.ProviderGiganews} eweka={stats.ProviderEweka}");
        }

        /// <summary>Phase A: low concurrency baseline run completes with zero outstanding at end.</summary>
        [Fact]
        public async Task PhaseA_functional_baseline_when_enabled()
        {
            if (!StressHarnessOptions.IsEnabled())
            {
                return;
            }

            var options = StressHarnessOptions.FromEnvironment();
            EnsureWorkload(options);
            var harness = new BackFillerE2EStressHarness(options);
            var backbone = StressHarnessOptions.GetBackboneOrDefault("Giganews");
            var file = Path.Join(options.WorkloadDirectory, "warmup-message-ids.txt");
            var summary = await harness.RunAsync(
                "phaseA-baseline-c1",
                backbone,
                file,
                concurrency: 1,
                maxInFlight: 2,
                duration: TimeSpan.FromSeconds(30),
                maxRequests: 10,
                CancellationToken.None);
            Console.WriteLine(summary.FormatTableRow());
            Assert.True(summary.RequestsStarted > 0);
            Assert.Equal(0, summary.OutstandingAtEnd);
        }

        /// <summary>Phase B: ramps concurrency levels until timeout rate exceeds 25% of started requests.</summary>
        [Fact]
        public async Task PhaseB_concurrency_ramp_when_enabled()
        {
            if (!StressHarnessOptions.IsEnabled())
            {
                return;
            }

            var phase = StressHarnessOptions.GetPhaseOrDefault("B");
            if (!string.Equals(phase, "B", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(phase, "ALL", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var options = StressHarnessOptions.FromEnvironment();
            EnsureWorkload(options);
            var harness = new BackFillerE2EStressHarness(options);
            var backbone = StressHarnessOptions.GetBackboneOrDefault("Giganews");
            var file = Path.Join(options.WorkloadDirectory, "main-message-ids.txt");
            var levels = ParseConcurrencyLevels();
            var durationSeconds = StressHarnessOptions.GetDurationSecondsOrDefault(60);
            foreach (var concurrency in levels)
            {
                var maxInFlight = StressHarnessOptions.GetMaxInFlightOrDefault(concurrency * 2);
                var summary = await harness.RunAsync(
                    $"phaseB-ramp-c{concurrency}",
                    backbone,
                    file,
                    concurrency,
                    maxInFlight,
                    TimeSpan.FromSeconds(durationSeconds),
                    maxRequests: null,
                    CancellationToken.None);
                Console.WriteLine(summary.FormatTableRow());
                if (summary.Timeouts > summary.RequestsStarted * 0.25)
                {
                    Console.WriteLine($"phaseB stopping ramp after c={concurrency}: elevated timeouts");
                    break;
                }
            }
        }

        /// <summary>Phase C: sustained main workload run logs whether 1 Gbps logical payload target is met.</summary>
        [Fact]
        public async Task PhaseC_sustained_throughput_when_enabled()
        {
            if (!StressHarnessOptions.IsEnabled())
            {
                return;
            }

            var phase = StressHarnessOptions.GetPhaseOrDefault("C");
            if (!string.Equals(phase, "C", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(phase, "ALL", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var options = StressHarnessOptions.FromEnvironment();
            EnsureWorkload(options);
            var harness = new BackFillerE2EStressHarness(options);
            var backbone = StressHarnessOptions.GetBackboneOrDefault("Giganews");
            var file = Path.Join(options.WorkloadDirectory, "main-message-ids.txt");
            var concurrency = StressHarnessOptions.GetConcurrencyOrDefault(100);
            var durationSeconds = StressHarnessOptions.GetDurationSecondsOrDefault(300);
            var maxInFlight = StressHarnessOptions.GetMaxInFlightOrDefault(concurrency * 2);
            var summary = await harness.RunAsync(
                $"phaseC-sustained-c{concurrency}",
                backbone,
                file,
                concurrency,
                maxInFlight,
                TimeSpan.FromSeconds(durationSeconds),
                maxRequests: null,
                CancellationToken.None);
            Console.WriteLine(summary.FormatTableRow());
            Console.WriteLine($"phaseC payloadGbps={summary.PayloadGbps:F3} target=1.0 achieved={(summary.PayloadGbps >= 1.0 ? "yes" : "no")}");
        }

        /// <summary>Phase D: repeatable workload soak asserts drain completes or timeouts were recorded.</summary>
        [Fact]
        public async Task PhaseD_stability_soak_when_enabled()
        {
            if (!StressHarnessOptions.IsEnabled())
            {
                return;
            }

            var phase = StressHarnessOptions.GetPhaseOrDefault("D");
            if (!string.Equals(phase, "D", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(phase, "ALL", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var options = StressHarnessOptions.FromEnvironment();
            EnsureWorkload(options);
            var harness = new BackFillerE2EStressHarness(options);
            var backbone = StressHarnessOptions.GetBackboneOrDefault("Giganews");
            var file = Path.Join(options.WorkloadDirectory, "repeatable-message-ids.txt");
            var concurrency = StressHarnessOptions.GetConcurrencyOrDefault(100);
            var durationSeconds = StressHarnessOptions.GetDurationSecondsOrDefault(300);
            var summary = await harness.RunAsync(
                $"phaseD-soak-c{concurrency}",
                backbone,
                file,
                concurrency,
                maxInFlight: concurrency * 2,
                TimeSpan.FromSeconds(durationSeconds),
                maxRequests: null,
                CancellationToken.None);
            Console.WriteLine(summary.FormatTableRow());
            Assert.True(summary.OutstandingAtEnd == 0 || summary.Timeouts > 0);
        }

        /// <summary>Provider phase: runs warmup workload separately for Giganews and Eweka backbones.</summary>
        [Fact]
        public async Task Isolated_backbone_runs_when_enabled()
        {
            if (!StressHarnessOptions.IsEnabled())
            {
                return;
            }

            var phase = StressHarnessOptions.GetPhaseOrDefault("provider");
            if (!string.Equals(phase, "provider", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(phase, "ALL", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var options = StressHarnessOptions.FromEnvironment();
            EnsureWorkload(options);
            var harness = new BackFillerE2EStressHarness(options);
            var file = Path.Join(options.WorkloadDirectory, "warmup-message-ids.txt");
            foreach (var backbone in new[] { "Giganews", "Eweka" })
            {
                var summary = await harness.RunAsync(
                    $"provider-{backbone}-c25",
                    backbone,
                    file,
                    concurrency: 25,
                    maxInFlight: 50,
                    duration: TimeSpan.FromSeconds(90),
                    maxRequests: null,
                    CancellationToken.None);
                Console.WriteLine(summary.FormatTableRow());
                await File.WriteAllTextAsync(
                    Path.Join(options.ReportDirectory, $"provider-{backbone}-redacted-config.json"),
                    JsonSerializer.Serialize(options.ToRedactedReportObject(), new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        /// <summary>
        /// Concurrent Eweka + Giganews sustained flood soak. Opt-in phase
        /// <c>VECTORNNTPD_BACKFILLER_E2E_STRESS_PHASE=DualSoak</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Measured duration defaults to 1800 s. Per-backbone <c>maxInFlight</c> is
        /// sessions + ready target (Eweka 50+75=125; Giganews observed MaxSessions + 150)
        /// so RPC waiters keep a ready backlog while consumers stay saturated.
        /// </para>
        /// <para>
        /// Independent <see cref="BackFillerE2EStressHarness"/> instances start measured load
        /// together via <see cref="Task.WhenAll"/>. No shared warm-up drain gates the other
        /// backbone. Abort uses completed-RPC rates (see <see cref="StressAbortDecision"/>),
        /// and DualSoak raises the abort thresholds so ramp-up outstanding work cannot cancel
        /// one backbone while the other continues alone.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task DualSoak_when_enabled()
        {
            if (!StressHarnessOptions.IsEnabled())
            {
                return;
            }

            var phase = StressHarnessOptions.GetPhaseOrDefault("DualSoak");
            if (!string.Equals(phase, "DualSoak", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(phase, "ALL", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var baseOptions = StressHarnessOptions.FromEnvironment();
            EnsureWorkload(baseOptions);
            // Dual flood must not abort on delivery lag during ramp (prior defect: Giganews
            // cancelled after ~3 s while Eweka alone kept flooding).
            var options = CloneForDualFlood(baseOptions);
            var durationSeconds = StressHarnessOptions.GetDurationSecondsOrDefault(1800);
            var mainFile = Path.Join(options.WorkloadDirectory, "main-message-ids.txt");

            const int ewekaSessions = 50;
            // Target 100 concurrent Giganews sessions (50+100=150 aggregate). Production MaxSessions
            // still comes from MySQL maxconnections; the pool will not exceed that value.
            const int giganewsSessions = 100;
            const int ewekaMinReady = 75;
            const int giganewsMinReady = 150;
            var ewekaMaxInFlight = ewekaSessions + ewekaMinReady;
            var giganewsMaxInFlight = giganewsSessions + giganewsMinReady;

            Console.WriteLine(
                $"DualSoak durationSec={durationSeconds} ewekaSessions={ewekaSessions} ewekaMaxInFlight={ewekaMaxInFlight} "
                + $"giganewsSessions={giganewsSessions} giganewsMaxInFlight={giganewsMaxInFlight} "
                + $"abortAfter={options.AbortAfterRequests} abortFailRate={options.AbortOnFailureRate}");

            // Bounded readiness: wait for residual accepted work to settle (no purge). Independent
            // per-backbone waits run concurrently so one backlog cannot stall the other's check.
            var cleanEweka = StressQueuePreflight.WaitUntilCleanAsync(
                options,
                "Eweka",
                label: "dual-soak-start-eweka",
                timeout: TimeSpan.FromMinutes(3),
                CancellationToken.None);
            var cleanGiga = StressQueuePreflight.WaitUntilCleanAsync(
                options,
                "Giganews",
                label: "dual-soak-start-giganews",
                timeout: TimeSpan.FromMinutes(3),
                CancellationToken.None);
            await Task.WhenAll(cleanEweka, cleanGiga);

            var ewekaHarness = new BackFillerE2EStressHarness(options);
            var gigaHarness = new BackFillerE2EStressHarness(options);

            // Short independent health probes (maxRequests), not duration-based warm-ups that
            // can block measured start for ReplyTo dispose-drain (observed 120 s on Giganews).
            var probeEweka = ewekaHarness.RunAsync(
                "dual-soak-probe-Eweka",
                "Eweka",
                mainFile,
                concurrency: 5,
                maxInFlight: 5,
                duration: TimeSpan.FromMinutes(2),
                maxRequests: 5,
                CancellationToken.None);
            var probeGiga = gigaHarness.RunAsync(
                "dual-soak-probe-Giganews",
                "Giganews",
                mainFile,
                concurrency: 5,
                maxInFlight: 5,
                duration: TimeSpan.FromMinutes(2),
                maxRequests: 5,
                CancellationToken.None);
            var probes = await Task.WhenAll(probeEweka, probeGiga);
            Console.WriteLine(probes[0].FormatTableRow());
            Console.WriteLine(probes[1].FormatTableRow());
            Assert.True(
                probes[0].RequestsStarted > 0 && probes[1].RequestsStarted > 0,
                "DualSoak probe did not start requests on both backbones.");

            // Re-check queues are not poisoned after probes (ready/unacked may be briefly non-zero
            // during drain; allow a small residual then start measured).
            await Task.Delay(TimeSpan.FromSeconds(2));

            var measuredStartedUtc = DateTimeOffset.UtcNow;
            Console.WriteLine($"DualSoak measured start utc={measuredStartedUtc:o}");

            var measuredEwekaTask = ewekaHarness.RunAsync(
                "dual-soak-Eweka",
                "Eweka",
                mainFile,
                concurrency: ewekaMaxInFlight,
                maxInFlight: ewekaMaxInFlight,
                duration: TimeSpan.FromSeconds(durationSeconds),
                maxRequests: null,
                CancellationToken.None);
            var measuredGigaTask = gigaHarness.RunAsync(
                "dual-soak-Giganews",
                "Giganews",
                mainFile,
                concurrency: giganewsMaxInFlight,
                maxInFlight: giganewsMaxInFlight,
                duration: TimeSpan.FromSeconds(durationSeconds),
                maxRequests: null,
                CancellationToken.None);
            var measured = await Task.WhenAll(measuredEwekaTask, measuredGigaTask);
            var measuredEndedUtc = DateTimeOffset.UtcNow;

            var eweka = measured[0];
            var giga = measured[1];
            Console.WriteLine(eweka.FormatTableRow());
            Console.WriteLine(giga.FormatTableRow());
            Console.WriteLine(
                $"DualSoak combined art/s={eweka.ArticlesPerSecond + giga.ArticlesPerSecond:F2} "
                + $"combinedGbps={eweka.PayloadGbps + giga.PayloadGbps:F3} "
                + $"ewekaElapsed={eweka.ElapsedSeconds:F1}s gigaElapsed={giga.ElapsedSeconds:F1}s "
                + $"ewekaTimeouts={eweka.Timeouts} gigaTimeouts={giga.Timeouts}");

            await File.WriteAllTextAsync(
                Path.Join(options.ReportDirectory, "dual-soak-combined-summary.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        MeasuredStartedUtc = measuredStartedUtc,
                        MeasuredEndedUtc = measuredEndedUtc,
                        ConfiguredMeasuredDurationSeconds = durationSeconds,
                        EwekaSessionsTarget = ewekaSessions,
                        GiganewsSessionsTarget = giganewsSessions,
                        EwekaMinReadyTarget = ewekaMinReady,
                        GiganewsMinReadyTarget = giganewsMinReady,
                        EwekaMaxInFlight = ewekaMaxInFlight,
                        GiganewsMaxInFlight = giganewsMaxInFlight,
                        EwekaElapsedSeconds = eweka.ElapsedSeconds,
                        GiganewsElapsedSeconds = giga.ElapsedSeconds,
                        Eweka = eweka,
                        Giganews = giga,
                        CombinedArticlesPerSecond = eweka.ArticlesPerSecond + giga.ArticlesPerSecond,
                        CombinedPayloadGbps = eweka.PayloadGbps + giga.PayloadGbps,
                    },
                    new JsonSerializerOptions { WriteIndented = true }));

            Assert.True(
                eweka.ElapsedSeconds >= durationSeconds * 0.9
                && giga.ElapsedSeconds >= durationSeconds * 0.9,
                $"DualSoak measured elapsed too short (eweka={eweka.ElapsedSeconds:F1}s giga={giga.ElapsedSeconds:F1}s; need ~{durationSeconds}s).");
        }

        /// <summary>
        /// Copies live stress options with DualSoak abort policy (never cancel on ramp lag).
        /// </summary>
        private static StressHarnessOptions CloneForDualFlood(StressHarnessOptions source) =>
            new()
            {
                RabbitMqHost = source.RabbitMqHost,
                RabbitMqPort = source.RabbitMqPort,
                RabbitMqVirtualHost = source.RabbitMqVirtualHost,
                RabbitMqUsername = source.RabbitMqUsername,
                RabbitMqPassword = source.RabbitMqPassword,
                WorkReplyTimeout = source.WorkReplyTimeout,
                ReplyQueueDisposeDrainTimeout = source.ReplyQueueDisposeDrainTimeout,
                ArticleTimeout = source.ArticleTimeout,
                NewsLogDirectory = source.NewsLogDirectory,
                WorkloadDirectory = source.WorkloadDirectory,
                ReportDirectory = source.ReportDirectory,
                ListenerHostOverride = source.ListenerHostOverride,
                ListenerPortOverride = source.ListenerPortOverride,
                Seed = source.Seed,
                // Probes validate retrieval; skip per-run article preflight so measured
                // WhenAll starts together without serializing on another backbone's preflight.
                ArticlePreflightCount = 0,
                // Completed-RPC based; DualSoak still disables practical abort so one backbone
                // cannot leave the other running alone for the full soak.
                AbortOnTimeoutRate = 1.0,
                AbortOnFailureRate = 1.0,
                AbortAfterRequests = int.MaxValue,
            };

        /// <summary>
        /// Controlled concurrency sweep with stage instrumentation. Opt-in phase
        /// <c>VECTORNNTPD_BACKFILLER_E2E_STRESS_PHASE=ConcurrencySweep</c>.
        /// Does not change production MaxSessions; levels above known pool size measure queueing.
        /// </summary>
        [Fact]
        public async Task ConcurrencySweep_when_enabled()
        {
            if (!StressHarnessOptions.IsEnabled())
            {
                return;
            }

            var phase = StressHarnessOptions.GetPhaseOrDefault("ConcurrencySweep");
            if (!string.Equals(phase, "ConcurrencySweep", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(phase, "ALL", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var options = StressHarnessOptions.FromEnvironment();
            EnsureWorkload(options);
            var harness = new BackFillerE2EStressHarness(options);
            var backbone = StressHarnessOptions.GetBackboneOrDefault("Giganews");
            var mainFile = Path.Join(options.WorkloadDirectory, "main-message-ids.txt");
            var warmupFile = Path.Join(options.WorkloadDirectory, "warmup-message-ids.txt");
            var durationSeconds = StressHarnessOptions.GetDurationSecondsOrDefault(90);
            var levels = ParseSweepConcurrencyLevels();

            // Known production MaxSessions from prior runtime logs (not modified here).
            var knownMaxSessions = string.Equals(backbone, "Eweka", StringComparison.OrdinalIgnoreCase)
                ? 50
                : 99;
            Console.WriteLine(
                $"ConcurrencySweep backbone={backbone} knownMaxSessions={knownMaxSessions} levels={string.Join(',', levels)} durationSec={durationSeconds}");

            // Fail visibly on poisoned backlog. Never purge from the harness.
            await StressQueuePreflight.RequireCleanAsync(
                options,
                backbone,
                label: "concurrency-sweep-start",
                CancellationToken.None);

            var warmup = await harness.RunAsync(
                $"sweep-{backbone}-warmup-c5",
                backbone,
                warmupFile,
                concurrency: 5,
                maxInFlight: 10,
                duration: TimeSpan.FromSeconds(20),
                maxRequests: null,
                CancellationToken.None);
            Console.WriteLine(warmup.FormatTableRow());
            Assert.True(warmup.ArticlesDelivered > 0, "Warm-up delivered zero articles; aborting sweep.");
            Assert.Equal(0, warmup.OutstandingAtEnd);

            StressRunSummary? previous = null;
            foreach (var concurrency in levels)
            {
                if (concurrency > knownMaxSessions * 2)
                {
                    Console.WriteLine(
                        $"ConcurrencySweep stopping before c={concurrency}: exceeds 2× known MaxSessions={knownMaxSessions} (unsafe backlog risk without production changes).");
                    break;
                }

                // Keep maxInFlight == concurrency so PeakOutstanding can reach the sweep level.
                // Do not inherit a stale VECTORNNTPD_BACKFILLER_E2E_STRESS_MAX_INFLIGHT from prior phases.
                var maxInFlight = concurrency;
                var summary = await harness.RunAsync(
                    $"sweep-{backbone}-c{concurrency}",
                    backbone,
                    mainFile,
                    concurrency,
                    maxInFlight,
                    TimeSpan.FromSeconds(durationSeconds),
                    maxRequests: null,
                    CancellationToken.None);
                Console.WriteLine(summary.FormatTableRow());
                WriteSweepRow(options.ReportDirectory, summary, knownMaxSessions);

                var littlesW = summary.ArticlesPerSecond > 0
                    ? summary.MeanOutstanding / summary.ArticlesPerSecond
                    : double.NaN;
                Console.WriteLine(
                    $"  stages replyMean={summary.WorkReplyLatencyMs.Mean:F0}ms e2eMean={summary.EndToEndSuccessLatencyMs.Mean:F0}ms littlesW={littlesW:F2}s drainMs={summary.DrainMilliseconds} outstandingEnd={summary.OutstandingAtEnd}");

                var unhealthy =
                    summary.Timeouts > summary.RequestsStarted * 0.20
                    || summary.ArticlesDelivered == 0
                    || summary.OutstandingAtEnd > 0
                    || (previous is not null
                        && previous.PayloadGbps > 0.01
                        && summary.PayloadGbps < previous.PayloadGbps * 0.35
                        && summary.Timeouts > 10);

                if (unhealthy)
                {
                    Console.WriteLine(
                        $"ConcurrencySweep stopping after c={concurrency}: unhealthy (timeouts={summary.Timeouts} ok={summary.ArticlesDelivered} outstandingEnd={summary.OutstandingAtEnd}).");
                    break;
                }

                previous = summary;
            }
        }

        /// <summary>Appends one tab-separated concurrency sweep row to the report directory table file.</summary>
        private static void WriteSweepRow(string reportDirectory, StressRunSummary summary, int knownMaxSessions)
        {
            var path = Path.Join(reportDirectory, "concurrency-sweep-table.txt");
            var line =
                $"{summary.RunName}\t{summary.Backbone}\t{summary.ConfiguredConcurrency}\t{knownMaxSessions}\t{summary.MeanOutstanding:F2}\t{summary.PeakOutstanding}\t{summary.ArticlesPerSecond:F2}\t{summary.PayloadGbps:F4}\t{summary.WorkReplyLatencyMs.Mean:F1}\t{summary.EndToEndSuccessLatencyMs.Mean:F1}\t{summary.MeanArticleLogicalBytes:F0}\t{summary.Timeouts}\t{summary.DrainMilliseconds}\t{summary.OutstandingAtEnd}";
            File.AppendAllText(path, line + Environment.NewLine);
        }

        /// <summary>Blocks until workload manifest and SHA provenance checks pass.</summary>
        private static void EnsureWorkload(StressHarnessOptions options)
        {
            // Synchronous gate used by opt-in benches; provenance check is authoritative.
            StressWorkloadGuard.RequireProvenancedWorkloadAsync(options, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }

        /// <summary>Parses phase-B concurrency list from env or returns default ramp steps.</summary>
        private static int[] ParseConcurrencyLevels()
        {
            var raw = Environment.GetEnvironmentVariable(StressHarnessOptions.ConcurrencyEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return [1, 10, 25, 50, 100, 150, 200];
            }

            if (raw.Contains(',', StringComparison.Ordinal))
            {
                return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(static part => int.Parse(part))
                    .Where(static value => value > 0)
                    .ToArray();
            }

            if (int.TryParse(raw, out var single) && single > 0)
            {
                return [single];
            }

            return [1, 10, 25, 50, 100, 150, 200];
        }

        /// <summary>Sweep defaults to 25…200; override with comma-separated CONCURRENCY env.</summary>
        private static int[] ParseSweepConcurrencyLevels()
        {
            var raw = Environment.GetEnvironmentVariable(StressHarnessOptions.ConcurrencyEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return [25, 50, 75, 100, 150, 200];
            }

            if (raw.Contains(',', StringComparison.Ordinal))
            {
                return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(static part => int.Parse(part))
                    .Where(static value => value > 0)
                    .ToArray();
            }

            if (int.TryParse(raw, out var single) && single > 0)
            {
                return [single];
            }

            return [25, 50, 75, 100, 150, 200];
        }
    }
}

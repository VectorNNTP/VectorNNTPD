using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Core;
using VectorNNTPD.StorageService;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Ingress;
using VectorNNTPD.StorageService.Listener;
using VectorNNTPD.StorageService.Storage.ArticleIndex;
using VectorNNTPD.StorageService.Storage.SegmentPools;
using VectorNNTPD.StorageService.Storage.SpoolPersistence;
using VectorNNTPD.StorageService.Storage.SpoolShards;
using VectorNNTPD.StorageService.Tests.Fixtures;
using VectorNNTPD.StorageService.Tests.Storage;
using VectorNNTPD.StorageService.Tests.TestDoubles;
using Xunit.Abstractions;

namespace VectorNNTPD.StorageService.Tests.Ingress
{
    /// <summary>
    /// End-to-end performance and resilience measurements for StorageService.
    /// Runs only when <c>VECTORNNTPD_STORAGE_E2E_BENCH=1</c>.
    /// Results are observational; they do not assert throughput targets.
    /// </summary>
    public sealed class StorageEndToEndResilienceBench
    {
        private const int ArticleBytes = 750 * 1024;
        private const string EnvFlag = "VECTORNNTPD_STORAGE_E2E_BENCH";
        private const string EnvSeconds = "VECTORNNTPD_STORAGE_E2E_SECONDS";
        private const string EnvRoot = "VECTORNNTPD_STORAGE_BENCH_ROOT";
        private const string EnvBottleneck = "VECTORNNTPD_STORAGE_BOTTLENECK_BENCH";
        private const string EnvBottleneckSeconds = "VECTORNNTPD_STORAGE_BOTTLENECK_SECONDS";
        /// <summary>Dedicated client fan-out matrix; does not enable the full bottleneck matrix.</summary>
        private const string EnvFanout = "VECTORNNTPD_STORAGE_CLIENT_FANOUT_BENCH";
        private const string EnvFanoutSeconds = "VECTORNNTPD_STORAGE_CLIENT_FANOUT_SECONDS";
        /// <summary>Comma-separated client counts, e.g. <c>1</c> or <c>1,2,4</c>. Required when fan-out bench is enabled.</summary>
        private const string EnvFanoutClients = "VECTORNNTPD_STORAGE_CLIENT_FANOUT_CLIENTS";
        private const string EnvFanoutWorkers = "VECTORNNTPD_STORAGE_CLIENT_FANOUT_WORKERS";
        private const string EnvFanoutRepeats = "VECTORNNTPD_STORAGE_CLIENT_FANOUT_REPEATS";
        /// <summary>Optional queue byte budget for fan-out only. Default 64 MiB. Allowed 16 MiB–256 MiB.</summary>
        private const string EnvFanoutMaxQueuedBytes = "VECTORNNTPD_STORAGE_CLIENT_FANOUT_MAX_QUEUED_BYTES";
        private const long DefaultFanoutMaxQueuedBytes = 64L << 20;
        /// <summary>
        /// Stage isolation only (sink / filesystem / full indexed) at fixed c=8, w=4.
        /// Does not enable bottleneck, fan-out, or E2E matrices.
        /// </summary>
        private const string EnvStage = "VECTORNNTPD_STORAGE_STAGE_ISOLATION_BENCH";
        private const string EnvStageSeconds = "VECTORNNTPD_STORAGE_STAGE_ISOLATION_SECONDS";
        /// <summary>Repeats for the FullIndexed cell only (default 2). Sink and FS run once unless raised.</summary>
        private const string EnvStageIndexedRepeats = "VECTORNNTPD_STORAGE_STAGE_ISOLATION_INDEXED_REPEATS";
        /// <summary>
        /// Filesystem publication substage attribution at fixed c=8, w=4, FilesystemNoIndex.
        /// Does not enable stage-isolation, bottleneck, fan-out, or E2E matrices.
        /// </summary>
        private const string EnvFsPub = "VECTORNNTPD_STORAGE_FS_PUB_ATTRIBUTION_BENCH";
        private const string EnvFsPubSeconds = "VECTORNNTPD_STORAGE_FS_PUB_ATTRIBUTION_SECONDS";
        private const string EnvFsPubRepeats = "VECTORNNTPD_STORAGE_FS_PUB_ATTRIBUTION_REPEATS";
        /// <summary>
        /// Sink vs FilesystemNoIndex at c=8/w=4 with test-only publish/process concurrency samples.
        /// Does not enable stage-isolation, FS-pub, bottleneck, fan-out, or layout matrices.
        /// </summary>
        private const string EnvE2eFsBottleneck = "VECTORNNTPD_STORAGE_E2E_FS_BOTTLENECK_BENCH";
        private const string EnvE2eFsBottleneckSeconds = "VECTORNNTPD_STORAGE_E2E_FS_BOTTLENECK_SECONDS";
        /// <summary>
        /// CompletingSink vs CompletingSink+calibrated busy delay vs FilesystemNoIndex at c=8/w=4.
        /// Does not enable stage-isolation, FS-pub, bottleneck, fan-out, or layout matrices.
        /// </summary>
        private const string EnvCalibratedDelay = "VECTORNNTPD_STORAGE_CALIBRATED_DELAY_BENCH";
        private const string EnvCalibratedDelaySeconds = "VECTORNNTPD_STORAGE_CALIBRATED_DELAY_SECONDS";
        private const string EnvCalibratedDelayMs = "VECTORNNTPD_STORAGE_CALIBRATED_DELAY_MS";
        private const double DefaultCalibratedDelayMs = 1.8;
        /// <summary>
        /// CompletingSink vs FilesystemNoIndex with bounded per-article FS-stage correlation for tail attribution.
        /// </summary>
        private const string EnvFsTail = "VECTORNNTPD_STORAGE_FS_TAIL_BENCH";
        private const string EnvFsTailSeconds = "VECTORNNTPD_STORAGE_FS_TAIL_SECONDS";
        private const string EnvFsTailRepeats = "VECTORNNTPD_STORAGE_FS_TAIL_REPEATS";
        /// <summary>
        /// FilesystemNoIndex w=4 then w=1 with correlated stage samples; absolute slow thresholds
        /// from the w=4 cell are applied to both. Does not enable <see cref="EnvFsTail"/>.
        /// </summary>
        private const string EnvFsTailWorkerIsolation = "VECTORNNTPD_STORAGE_FS_TAIL_WORKER_ISOLATION_BENCH";
        private const string EnvFsTailWorkerIsolationSeconds = "VECTORNNTPD_STORAGE_FS_TAIL_WORKER_ISOLATION_SECONDS";
        private const string EnvFsTailWorkerIsolationRepeats = "VECTORNNTPD_STORAGE_FS_TAIL_WORKER_ISOLATION_REPEATS";
        /// <summary>
        /// FilesystemNoIndex c=8/w=4 with client-side aggregate admission pacing to vary queue headroom.
        /// Does not enable <see cref="EnvFsTail"/> or worker-isolation benches.
        /// </summary>
        private const string EnvAdmissionHeadroom = "VECTORNNTPD_STORAGE_FS_TAIL_ADMISSION_HEADROOM_BENCH";
        private const string EnvAdmissionHeadroomSeconds = "VECTORNNTPD_STORAGE_FS_TAIL_ADMISSION_HEADROOM_SECONDS";
        private const string EnvAdmissionHeadroomRepeats = "VECTORNNTPD_STORAGE_FS_TAIL_ADMISSION_HEADROOM_REPEATS";
        /// <summary>
        /// FilesystemNoIndex c=8/w=4 baseline vs lightweight host telemetry (process/GC/thread-pool).
        /// Does not enable <see cref="EnvFsTail"/> or other FS-tail variants.
        /// </summary>
        private const string EnvHostTelemetry = "VECTORNNTPD_STORAGE_FS_TAIL_HOST_TELEMETRY_BENCH";
        private const string EnvHostTelemetrySeconds = "VECTORNNTPD_STORAGE_FS_TAIL_HOST_TELEMETRY_SECONDS";
        private const string EnvHostTelemetryRepeats = "VECTORNNTPD_STORAGE_FS_TAIL_HOST_TELEMETRY_REPEATS";
        private const string EnvHostTelemetryIntervalMs = "VECTORNNTPD_STORAGE_FS_TAIL_HOST_TELEMETRY_INTERVAL_MS";
        /// <summary>
        /// FilesystemNoIndex c=8/w=4 with optional same-volume low-rate CreateNew sidecar
        /// (ETW DiskIO/FileIO fallback when elevation is unavailable).
        /// </summary>
        private const string EnvFsTailSidecar = "VECTORNNTPD_STORAGE_FS_TAIL_SIDECAR_BENCH";
        private const string EnvFsTailSidecarSeconds = "VECTORNNTPD_STORAGE_FS_TAIL_SIDECAR_SECONDS";
        private const string EnvFsTailSidecarRepeats = "VECTORNNTPD_STORAGE_FS_TAIL_SIDECAR_REPEATS";
        private const string EnvFsTailSidecarRate = "VECTORNNTPD_STORAGE_FS_TAIL_SIDECAR_OPS_PER_SEC";

        private readonly ITestOutputHelper _output;

        public StorageEndToEndResilienceBench(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Optional_e2e_750kib_tls_persist_index_sustained()
        {
            if (!Enabled())
            {
                Skip();
                return;
            }

            var duration = ResolveDuration(defaultSeconds: 60);
            var root = ResolveBenchRoot();
            WriteEnvPreamble(root, duration);

            await RunE2EAsync(root, duration, workers: 4, outstanding: 2, label: "e2e-w4-o2");
            var shortDuration = TimeSpan.FromSeconds(Math.Min(15, Math.Max(5, duration.TotalSeconds / 4)));
            await RunE2EAsync(root, shortDuration, workers: 1, outstanding: 1, label: "e2e-w1-o1");
        }


        [Fact]
        public async Task Optional_timed_capacity_recovery()
        {
            if (!Enabled())
            {
                Skip();
                return;
            }

            var notifySamples = new List<double>(32);
            var resumeSamples = new List<double>(32);
            var completeSamples = new List<double>(32);
            var waitSamples = new List<double>(32);

            for (var i = 0; i < 24; i++)
            {
                var sample = await MeasureNotifyWakeAsync();
                notifySamples.Add(sample.NotifyToResumeMs);
                resumeSamples.Add(sample.ResumeToCompleteMs);
                completeSamples.Add(sample.NotifyToCompleteMs);
                waitSamples.Add(sample.CapacityWaitMs);
            }

            notifySamples.Sort();
            resumeSamples.Sort();
            completeSamples.Sort();
            waitSamples.Sort();

            var line =
                "capacity-recovery-notify samples=24 "
                + $"notifyToResumeMs min={notifySamples[0]:0.000} med={Median(notifySamples):0.000} p95={Percentile(notifySamples, 0.95):0.000} max={notifySamples[^1]:0.000} "
                + $"resumeToCompleteMs min={resumeSamples[0]:0.000} med={Median(resumeSamples):0.000} p95={Percentile(resumeSamples, 0.95):0.000} max={resumeSamples[^1]:0.000} "
                + $"notifyToCompleteMs min={completeSamples[0]:0.000} med={Median(completeSamples):0.000} p95={Percentile(completeSamples, 0.95):0.000} max={completeSamples[^1]:0.000} "
                + $"capacityWaitMs min={waitSamples[0]:0.000} med={Median(waitSamples):0.000} p95={Percentile(waitSamples, 0.95):0.000} max={waitSamples[^1]:0.000} "
                + "wakePath=SpoolCapacityWaitSignal.Notify pollIntervalMs=60000 (not used when notify wins)";
            Emit(line);

            var sampleWake = await MeasureSamplePublishedWakeAsync();
            Emit(
                "capacity-recovery-sample "
                + $"eligibleObservedToNotifyMs={sampleWake.EligibleToNotifyMs:0.000} "
                + $"notifyToResumeMs={sampleWake.NotifyToResumeMs:0.000} "
                + $"notifyToCompleteMs={sampleWake.NotifyToCompleteMs:0.000} "
                + "wakePath=BlockDeviceMonitorService.CapacitySamplePublished→SpoolCapacityWaitSignal");

            var eviction = await MeasureEvictionFreedWakeAsync();
            Emit(
                "capacity-recovery-eviction-signal "
                + $"noteFreedToPublishedMs={eviction.FreedToNotifyMs:0.000} "
                + $"publishedToResumeMs={eviction.NotifyToResumeMs:0.000} "
                + $"publishedToCompleteMs={eviction.NotifyToCompleteMs:0.000} "
                + "wakePath=BlockDeviceMonitorService.NoteFreedBytes→CapacitySamplePublished→SpoolCapacityWaitSignal");

            var critical = await MeasureCriticalSpoolExcludedAsync();
            Emit(
                "capacity-critical-excluded "
                + $"processResult={critical.Result} deferred={critical.Deferred} "
                + $"elapsedMs={critical.ElapsedMs:0.000} "
                + "contract=spool_at_or_above_CriticalPercent_not_selected");

            var multi = await MeasureMultiSpoolIndependentRecoveryAsync();
            Emit(
                "capacity-multi-spool "
                + $"blockedSpoolDeferred={multi.BlockedDeferred} healthyStored={multi.HealthyStored} "
                + $"recoveryNotifyToCompleteMs={multi.NotifyToCompleteMs:0.000} "
                + "contract=one_spool_recovery_does_not_require_unrelated_spool");

            Assert.True(Median(notifySamples) >= 0);
            Assert.True(sampleWake.NotifyToCompleteMs >= 0);
            Assert.Equal(AcceptedArticleProcessResult.WaitingForCapacity, critical.Result);
            Assert.True(multi.HealthyStored >= 1);
        }

        [Fact]
        public async Task Optional_shutdown_under_backlog_wall_clock()
        {
            if (!Enabled())
            {
                Skip();
                return;
            }

            var root = ResolveBenchRoot();
            WriteEnvPreamble(root, TimeSpan.Zero);

            await MeasureShutdownDrainAsync(root, articleCount: 32, label: "shutdown-normal");
            await MeasureShutdownDrainAsync(root, articleCount: 128, label: "shutdown-backlog");
            await MeasureShutdownCancelFailsAsync(label: "shutdown-host-cancel");
            await MeasureDisconnectAfterAdmitAsync(root, label: "shutdown-disconnect-after-admit");
        }

        /// <summary>
        /// Worker scaling, client scaling, and stage-isolation attribution.
        /// Runs only when <c>VECTORNNTPD_STORAGE_BOTTLENECK_BENCH=1</c>.
        /// Does not run when only <c>VECTORNNTPD_STORAGE_CLIENT_FANOUT_BENCH</c> is set.
        /// </summary>
        [Fact]
        public async Task Optional_bottleneck_attribution_scaling_and_stages()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(EnvBottleneck), "1", StringComparison.Ordinal))
            {
                _output.WriteLine($"Set {EnvBottleneck}=1 to run bottleneck attribution benches.");
                return;
            }

            var duration = ResolveBottleneckDuration(defaultSeconds: 30);
            var root = ResolveBenchRoot();
            WriteEnvPreamble(root, duration);
            Emit(
                $"bottleneck-storage deviceHint=NVMe_or_HDD_see_report filesystem=NTFS dedicatedDisk={root.IsDedicated} "
                + "note=C_drive_physical_disk_recorded_in_BOTTLENECK_ATTRIBUTION_REPORT");

            // Stage isolation (fixed w=4, c=2): completing sink vs FS-only vs full indexed.
            await RunE2EAsync(root, duration, workers: 4, outstanding: 2, label: "stage-completing-sink", HostMode.CompletingSink);
            await RunE2EAsync(root, duration, workers: 4, outstanding: 2, label: "stage-fs-no-index", HostMode.FilesystemNoIndex);
            await RunE2EAsync(root, duration, workers: 4, outstanding: 2, label: "stage-full-indexed", HostMode.FullIndexed);

            // Worker scaling: hold client connections=2.
            foreach (var workers in new[] { 1, 2, 4, 8 })
            {
                await RunE2EAsync(root, duration, workers, outstanding: 2, label: $"scale-w{workers}-c2-r1", HostMode.FullIndexed);
                await RunE2EAsync(root, duration, workers, outstanding: 2, label: $"scale-w{workers}-c2-r2", HostMode.FullIndexed);
            }

            // Client scaling: hold workers=4.
            foreach (var clients in new[] { 1, 2, 4 })
            {
                await RunE2EAsync(root, duration, workers: 4, outstanding: clients, label: $"scale-w4-c{clients}-r1", HostMode.FullIndexed);
            }
        }

        /// <summary>
        /// Client fan-out only (workers fixed). Activate with
        /// <c>VECTORNNTPD_STORAGE_CLIENT_FANOUT_BENCH=1</c> and
        /// <c>VECTORNNTPD_STORAGE_CLIENT_FANOUT_CLIENTS=1</c> (or <c>1,2,4</c>).
        /// Does not enable <see cref="Optional_bottleneck_attribution_scaling_and_stages"/>.
        /// </summary>
        [Fact]
        public async Task Optional_client_fanout_scaling()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(EnvFanout), "1", StringComparison.Ordinal))
            {
                _output.WriteLine(
                    $"Set {EnvFanout}=1 and {EnvFanoutClients}=N (comma-separated) to run client fan-out benches.");
                return;
            }

            var clientsList = ParsePositiveIntList(Environment.GetEnvironmentVariable(EnvFanoutClients));
            if (clientsList.Count == 0)
            {
                _output.WriteLine(
                    $"{EnvFanout}=1 but {EnvFanoutClients} is missing/empty; skipping (refuse unbounded matrix).");
                return;
            }

            var duration = ResolveFanoutDuration(defaultSeconds: 60);
            var workers = 4;
            if (int.TryParse(Environment.GetEnvironmentVariable(EnvFanoutWorkers), out var configuredWorkers)
                && configuredWorkers is >= 1 and <= 64)
            {
                workers = configuredWorkers;
            }

            var repeats = 1;
            if (int.TryParse(Environment.GetEnvironmentVariable(EnvFanoutRepeats), out var configuredRepeats)
                && configuredRepeats is >= 1 and <= 5)
            {
                repeats = configuredRepeats;
            }

            var maxQueuedBytes = DefaultFanoutMaxQueuedBytes;
            if (long.TryParse(Environment.GetEnvironmentVariable(EnvFanoutMaxQueuedBytes), out var configuredQueued)
                && configuredQueued is >= (16L << 20) and <= (256L << 20))
            {
                maxQueuedBytes = configuredQueued;
            }

            var root = ResolveBenchRoot();
            WriteEnvPreamble(root, duration);
            Emit(
                $"fanout-config workers={workers} clients=[{string.Join(',', clientsList)}] repeats={repeats} "
                + $"maxQueuedBytes={maxQueuedBytes} durationSec={duration.TotalSeconds:0} dedicatedDisk={root.IsDedicated} "
                + $"activation={EnvFanout}=1 {EnvFanoutClients}={Environment.GetEnvironmentVariable(EnvFanoutClients)}");

            foreach (var clients in clientsList)
            {
                if (clients is < 1 or > 64)
                {
                    Emit($"fanout-skip clients={clients} reason=out_of_range");
                    continue;
                }

                for (var r = 1; r <= repeats; r++)
                {
                    await RunE2EAsync(
                        root,
                        duration,
                        workers,
                        outstanding: clients,
                        label: $"fanout-w{workers}-c{clients}-r{r}",
                        HostMode.FullIndexed,
                        maxQueuedBytes);
                }
            }
        }

        /// <summary>
        /// Stage isolation at saturated client offer: CompletingSink vs FilesystemNoIndex vs FullIndexed.
        /// Activate with <c>VECTORNNTPD_STORAGE_STAGE_ISOLATION_BENCH=1</c>.
        /// Fixed workers=4, clients=8, MaxQueuedBytes=64 MiB; does not enable other optional benches.
        /// </summary>
        [Fact]
        public async Task Optional_stage_isolation_c8_w4()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(EnvStage), "1", StringComparison.Ordinal))
            {
                _output.WriteLine($"Set {EnvStage}=1 to run stage-isolation benches (c=8, w=4).");
                return;
            }

            var duration = ResolveStageDuration(defaultSeconds: 60);
            var indexedRepeats = 2;
            if (int.TryParse(Environment.GetEnvironmentVariable(EnvStageIndexedRepeats), out var configuredRepeats)
                && configuredRepeats is >= 1 and <= 5)
            {
                indexedRepeats = configuredRepeats;
            }

            const int workers = 4;
            const int clients = 8;
            var root = ResolveBenchRoot();
            WriteEnvPreamble(root, duration);
            Emit(
                $"stage-config workers={workers} clients={clients} maxQueuedBytes={DefaultFanoutMaxQueuedBytes} "
                + $"indexedRepeats={indexedRepeats} durationSec={duration.TotalSeconds:0} dedicatedDisk={root.IsDedicated} "
                + $"activation={EnvStage}=1 "
                + "modes=CompletingSink,FilesystemNoIndex,FullIndexed "
                + "note=same_TLS_TAKETHIS_ingress_and_queue_for_all_modes");

            await RunE2EAsync(
                root,
                duration,
                workers,
                outstanding: clients,
                label: "stage-c8-w4-completing-sink-r1",
                HostMode.CompletingSink,
                DefaultFanoutMaxQueuedBytes);

            await RunE2EAsync(
                root,
                duration,
                workers,
                outstanding: clients,
                label: "stage-c8-w4-fs-no-index-r1",
                HostMode.FilesystemNoIndex,
                DefaultFanoutMaxQueuedBytes);

            for (var r = 1; r <= indexedRepeats; r++)
            {
                await RunE2EAsync(
                    root,
                    duration,
                    workers,
                    outstanding: clients,
                    label: $"stage-c8-w4-full-indexed-r{r}",
                    HostMode.FullIndexed,
                    DefaultFanoutMaxQueuedBytes);
            }
        }

        /// <summary>
        /// FilesystemNoIndex publication substage timing at saturated offer (c=8, w=4).
        /// Activate with <c>VECTORNNTPD_STORAGE_FS_PUB_ATTRIBUTION_BENCH=1</c>.
        /// Also runs a labeled Path-rewrite CPU microbench (no disk) for residual context.
        /// </summary>
        [Fact]
        public async Task Optional_filesystem_publication_attribution()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(EnvFsPub), "1", StringComparison.Ordinal))
            {
                _output.WriteLine($"Set {EnvFsPub}=1 to run filesystem publication attribution (c=8, w=4, FilesystemNoIndex).");
                return;
            }

            var duration = ResolveFsPubDuration(defaultSeconds: 60);
            var repeats = 2;
            if (int.TryParse(Environment.GetEnvironmentVariable(EnvFsPubRepeats), out var configuredRepeats)
                && configuredRepeats is >= 1 and <= 5)
            {
                repeats = configuredRepeats;
            }

            const int workers = 4;
            const int clients = 8;
            var root = ResolveBenchRoot();
            WriteEnvPreamble(root, duration);
            Emit(
                $"fspub-config workers={workers} clients={clients} maxQueuedBytes={DefaultFanoutMaxQueuedBytes} "
                + $"repeats={repeats} durationSec={duration.TotalSeconds:0} dedicatedDisk={root.IsDedicated} "
                + $"activation={EnvFsPub}=1 mode=FilesystemNoIndex "
                + "instrumentation=test_only_TimingSpoolFiles_open_write_flush_move_exists_dir "
                + "note=same_TLS_TAKETHIS_ingress_as_stage_isolation");

            MeasurePathRewriteCpuMicrobench(iterations: 2000);

            for (var r = 1; r <= repeats; r++)
            {
                await RunE2EAsync(
                    root,
                    duration,
                    workers,
                    outstanding: clients,
                    label: $"fspub-c8-w4-fs-no-index-r{r}",
                    HostMode.FilesystemNoIndex,
                    DefaultFanoutMaxQueuedBytes);
            }
        }

        /// <summary>
        /// Discriminating E2E cell: CompletingSink then FilesystemNoIndex at saturated c=8/w=4,
        /// reporting max concurrent ProcessAsync / WriteAll / MoveNew (test-only).
        /// Hypothesis: FS publish I/O overlaps across workers (not a single global serialize lock);
        /// sink→FS gap is worker busy-time + admit coupling. Activate with
        /// <c>VECTORNNTPD_STORAGE_E2E_FS_BOTTLENECK_BENCH=1</c>.
        /// </summary>
        [Fact]
        public async Task Optional_e2e_fs_bottleneck_concurrency()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(EnvE2eFsBottleneck), "1", StringComparison.Ordinal))
            {
                _output.WriteLine(
                    $"Set {EnvE2eFsBottleneck}=1 to run sink vs FilesystemNoIndex concurrency attribution "
                    + "(c=8, w=4; not an optimization claim).");
                return;
            }

            var duration = ResolveE2eFsBottleneckDuration(defaultSeconds: 60);
            const int workers = 4;
            const int clients = 8;
            var root = ResolveBenchRoot();
            WriteEnvPreamble(root, duration);
            Emit(
                $"e2efs-config workers={workers} clients={clients} maxQueuedBytes={DefaultFanoutMaxQueuedBytes} "
                + $"durationSec={duration.TotalSeconds:0} dedicatedDisk={root.IsDedicated} "
                + $"activation={EnvE2eFsBottleneck}=1 "
                + "modes=CompletingSink,FilesystemNoIndex "
                + "instrumentation=test_only_max_concurrent_Process_WriteAll_Move "
                + "hypothesis=fs_publish_overlaps_across_workers_gap_is_busy_time_plus_admit_coupling "
                + "competing=global_serialize_lock_on_publish_io "
                + "note=same_TLS_TAKETHIS_ingress_as_stage_isolation_no_FullIndexed_cell");

            await RunE2EAsync(
                root,
                duration,
                workers,
                outstanding: clients,
                label: "e2efs-c8-w4-completing-sink",
                HostMode.CompletingSink,
                DefaultFanoutMaxQueuedBytes);
            await RunE2EAsync(
                root,
                duration,
                workers,
                outstanding: clients,
                label: "e2efs-c8-w4-fs-no-index",
                HostMode.FilesystemNoIndex,
                DefaultFanoutMaxQueuedBytes);
        }

        /// <summary>
        /// CompletingSink vs CompletingSink with calibrated worker busy-wait (~1.8 ms, no FS I/O)
        /// vs FilesystemNoIndex at c=8/w=4. Tests whether generic process busy-time + admit/queue
        /// coupling can reproduce the sink→FS throughput gap. Activate with
        /// <c>VECTORNNTPD_STORAGE_CALIBRATED_DELAY_BENCH=1</c>.
        /// Delay holds the worker synchronously (Stopwatch spin) because production publish Write/Move
        /// are synchronous on the worker; <see cref="Task.Delay"/> would release the worker and
        /// mis-model concurrency. Not equivalent to disk I/O.
        /// </summary>
        [Fact]
        public async Task Optional_calibrated_delay_vs_filesystem()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(EnvCalibratedDelay), "1", StringComparison.Ordinal))
            {
                _output.WriteLine(
                    $"Set {EnvCalibratedDelay}=1 to run CompletingSink vs calibrated-delay sink vs FilesystemNoIndex "
                    + $"(c=8, w=4; default delayMs={DefaultCalibratedDelayMs}).");
                return;
            }

            var duration = ResolveCalibratedDelayDuration(defaultSeconds: 60);
            var delayMs = DefaultCalibratedDelayMs;
            if (double.TryParse(
                    Environment.GetEnvironmentVariable(EnvCalibratedDelayMs),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var configuredDelay)
                && configuredDelay is > 0 and <= 50)
            {
                delayMs = configuredDelay;
            }

            const int workers = 4;
            const int clients = 8;
            var root = ResolveBenchRoot();
            WriteEnvPreamble(root, duration);
            Emit(
                $"caldelay-config workers={workers} clients={clients} maxQueuedBytes={DefaultFanoutMaxQueuedBytes} "
                + $"durationSec={duration.TotalSeconds:0} delayMs={delayMs:0.###} dedicatedDisk={root.IsDedicated} "
                + $"activation={EnvCalibratedDelay}=1 "
                + "modes=CompletingSink,CompletingSinkCalibratedDelay,FilesystemNoIndex "
                + "delayMechanism=Stopwatch_busy_wait_sync_on_worker_no_TaskDelay_no_FS_IO "
                + "delayPlacement=CompletingSinkProcessor_ProcessAsync_before_Completed "
                + "hypothesis=generic_busy_time_plus_admit_queue_coupling_reproduces_FS_throughput_band "
                + "note=delay_NOT_equivalent_to_disk_IO_even_if_rates_match");

            await RunE2EAsync(
                root,
                duration,
                workers,
                outstanding: clients,
                label: "caldelay-c8-w4-completing-sink",
                HostMode.CompletingSink,
                DefaultFanoutMaxQueuedBytes);
            await RunE2EAsync(
                root,
                duration,
                workers,
                outstanding: clients,
                label: "caldelay-c8-w4-sink-delay",
                HostMode.CompletingSinkCalibratedDelay,
                DefaultFanoutMaxQueuedBytes,
                sinkDelay: TimeSpan.FromMilliseconds(delayMs));
            await RunE2EAsync(
                root,
                duration,
                workers,
                outstanding: clients,
                label: "caldelay-c8-w4-fs-no-index",
                HostMode.FilesystemNoIndex,
                DefaultFanoutMaxQueuedBytes);
        }

        /// <summary>
        /// CompletingSink vs FilesystemNoIndex at c=8/w=4 with bounded correlated open/write/move/process
        /// samples and before/after AccountedBytes snapshots for queue-pressure event ordering.
        /// Activate with <c>VECTORNNTPD_STORAGE_FS_TAIL_BENCH=1</c>.
        /// </summary>
        [Fact]
        public async Task Optional_filesystem_tail_attribution()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(EnvFsTail), "1", StringComparison.Ordinal))
            {
                _output.WriteLine(
                    $"Set {EnvFsTail}=1 to run CompletingSink vs FilesystemNoIndex tail attribution "
                    + "(c=8, w=4; bounded correlated stage samples).");
                return;
            }

            var duration = ResolveFsTailDuration(defaultSeconds: 60);
            var repeats = 1;
            if (int.TryParse(Environment.GetEnvironmentVariable(EnvFsTailRepeats), out var configuredRepeats)
                && configuredRepeats is >= 1 and <= 3)
            {
                repeats = configuredRepeats;
            }

            const int workers = 4;
            const int clients = 8;
            var root = ResolveBenchRoot();
            WriteEnvPreamble(root, duration);
            Emit(
                $"fstail-config workers={workers} clients={clients} maxQueuedBytes={DefaultFanoutMaxQueuedBytes} "
                + $"durationSec={duration.TotalSeconds:0} repeats={repeats} dedicatedDisk={root.IsDedicated} "
                + $"activation={EnvFsTail}=1 "
                + "modes=CompletingSink,FilesystemNoIndex "
                + "instrumentation=test_only_AsyncLocal_correlated_stages_plus_AccountedBytes_before_after_open_write_move "
                + "sampleCap=8192 "
                + "hypothesis=queue_pressure_precedes_vs_follows_slow_FS_ops "
                + "note=AccountedBytes_is_payload_accounting_not_disk_queue_depth");

            for (var r = 1; r <= repeats; r++)
            {
                await RunE2EAsync(
                    root,
                    duration,
                    workers,
                    outstanding: clients,
                    label: $"fstail-c8-w4-completing-sink-r{r}",
                    HostMode.CompletingSink,
                    DefaultFanoutMaxQueuedBytes,
                    enableTailCorrelation: false);
                await RunE2EAsync(
                    root,
                    duration,
                    workers,
                    outstanding: clients,
                    label: $"fstail-c8-w4-fs-no-index-r{r}",
                    HostMode.FilesystemNoIndex,
                    DefaultFanoutMaxQueuedBytes,
                    enableTailCorrelation: true);
            }
        }

        /// <summary>
        /// FilesystemNoIndex c=8 with w=4 then w=1 to isolate multi-worker Acc feedback from
        /// persistent Open/Move spikes. Absolute slow thresholds from w=4 are applied to both cells.
        /// Activate with <c>VECTORNNTPD_STORAGE_FS_TAIL_WORKER_ISOLATION_BENCH=1</c>.
        /// </summary>
        [Fact]
        public async Task Optional_filesystem_tail_worker_isolation()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(EnvFsTailWorkerIsolation),
                    "1",
                    StringComparison.Ordinal))
            {
                _output.WriteLine(
                    $"Set {EnvFsTailWorkerIsolation}=1 to run FilesystemNoIndex w=4 vs w=1 "
                    + "FS-tail isolation (c=8; absolute slow thresholds from w=4).");
                return;
            }

            var duration = ResolveFsTailWorkerIsolationDuration(defaultSeconds: 60);
            var repeats = 1;
            if (int.TryParse(
                    Environment.GetEnvironmentVariable(EnvFsTailWorkerIsolationRepeats),
                    out var configuredRepeats)
                && configuredRepeats is >= 1 and <= 3)
            {
                repeats = configuredRepeats;
            }

            const int clients = 8;
            var root = ResolveBenchRoot();
            WriteEnvPreamble(root, duration);
            Emit(
                $"fstail-worker-isolation-config clients={clients} maxQueuedBytes={DefaultFanoutMaxQueuedBytes} "
                + $"durationSec={duration.TotalSeconds:0} repeats={repeats} dedicatedDisk={root.IsDedicated} "
                + $"activation={EnvFsTailWorkerIsolation}=1 "
                + "mode=FilesystemNoIndex workerCells=4,1 "
                + "instrumentation=test_only_AsyncLocal_correlated_stages_plus_AccountedBytes_before_after "
                + "sampleCap=8192 "
                + "hypothesis=Open_Move_spikes_persist_at_w1_vs_multi_worker_Acc_feedback "
                + "note=only_worker_count_changes_clients_article_queue_budget_constant");

            for (var r = 1; r <= repeats; r++)
            {
                AbsoluteSlowThresholds? baseline = null;
                await RunE2EAsync(
                    root,
                    duration,
                    workers: 4,
                    outstanding: clients,
                    label: $"fstail-isol-c8-w4-fs-no-index-r{r}",
                    HostMode.FilesystemNoIndex,
                    DefaultFanoutMaxQueuedBytes,
                    enableTailCorrelation: true,
                    // Self-baseline: absolute analysis uses this cell's own p95 (identity check).
                    absoluteSlowThresholds: null,
                    onLocalSlowThresholds: t => baseline = t,
                    applyLocalThresholdsAsAbsolute: true);

                Assert.True(baseline.HasValue, "expected w=4 correlator percentile thresholds");
                Emit(
                    $"fstail-isol-absolute-baseline-r{r} "
                    + $"processP95Ms={baseline.Value.ProcessP95Ms:0.000} "
                    + $"openP95Ms={baseline.Value.OpenP95Ms:0.000} "
                    + $"writeP95Ms={baseline.Value.WriteP95Ms:0.000} "
                    + $"moveP95Ms={baseline.Value.MoveP95Ms:0.000} "
                    + "appliedTo=w4_self_and_w1");

                await RunE2EAsync(
                    root,
                    duration,
                    workers: 1,
                    outstanding: clients,
                    label: $"fstail-isol-c8-w1-fs-no-index-r{r}",
                    HostMode.FilesystemNoIndex,
                    DefaultFanoutMaxQueuedBytes,
                    enableTailCorrelation: true,
                    absoluteSlowThresholds: baseline,
                    onLocalSlowThresholds: null);
            }
        }

        /// <summary>
        /// FilesystemNoIndex c=8/w=4 with optional client-side aggregate admission pacing to keep
        /// AccountedBytes below the queue cap (queue-headroom isolation). Activate with
        /// <c>VECTORNNTPD_STORAGE_FS_TAIL_ADMISSION_HEADROOM_BENCH=1</c>.
        /// </summary>
        [Fact]
        public async Task Optional_filesystem_tail_admission_headroom()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(EnvAdmissionHeadroom),
                    "1",
                    StringComparison.Ordinal))
            {
                _output.WriteLine(
                    $"Set {EnvAdmissionHeadroom}=1 to run FilesystemNoIndex admission-rate / queue-headroom "
                    + "isolation (c=8, w=4; client-side aggregate pacing).");
                return;
            }

            var duration = ResolveAdmissionHeadroomDuration(defaultSeconds: 60);
            var repeats = 2;
            if (int.TryParse(
                    Environment.GetEnvironmentVariable(EnvAdmissionHeadroomRepeats),
                    out var configuredRepeats)
                && configuredRepeats is >= 1 and <= 3)
            {
                repeats = configuredRepeats;
            }

            const int workers = 4;
            const int clients = 8;
            // Tentative aggregate admit rates (art/s). Fixed for measured cells after short calibration.
            // Chosen below prior unrestricted ~930–1008 art/s so the queue can drain between admits.
            const double lowAdmitPerSec = 400;
            const double midAdmitPerSec = 650;
            var root = ResolveBenchRoot();
            WriteEnvPreamble(root, duration);
            Emit(
                $"fstail-admission-headroom-config workers={workers} clients={clients} "
                + $"maxQueuedBytes={DefaultFanoutMaxQueuedBytes} durationSec={duration.TotalSeconds:0} "
                + $"repeats={repeats} dedicatedDisk={root.IsDedicated} activation={EnvAdmissionHeadroom}=1 "
                + "mode=FilesystemNoIndex "
                + $"cells=unrestricted,mid_{midAdmitPerSec:0}/s,low_{lowAdmitPerSec:0}/s "
                + "pacing=test_only_shared_token_bucket_before_TakeThis "
                + "instrumentation=AsyncLocal_correlated_stages_plus_full_bag_open_stall_counts "
                + "sampleCap=8192 "
                + "hypothesis=Open_stalls_persist_when_AccountedBytes_stays_below_cap");

            // Short calibration (not used as primary evidence): confirm occupancy separation.
            var calibDuration = TimeSpan.FromSeconds(15);
            Emit($"fstail-admission-calib-start durationSec={calibDuration.TotalSeconds:0}");
            await RunE2EAsync(
                root,
                calibDuration,
                workers,
                outstanding: clients,
                label: "fstail-adm-calib-unrestricted",
                HostMode.FilesystemNoIndex,
                DefaultFanoutMaxQueuedBytes,
                enableTailCorrelation: true,
                aggregateAdmitPerSec: null);
            await RunE2EAsync(
                root,
                calibDuration,
                workers,
                outstanding: clients,
                label: $"fstail-adm-calib-mid-{midAdmitPerSec:0}",
                HostMode.FilesystemNoIndex,
                DefaultFanoutMaxQueuedBytes,
                enableTailCorrelation: true,
                aggregateAdmitPerSec: midAdmitPerSec);
            await RunE2EAsync(
                root,
                calibDuration,
                workers,
                outstanding: clients,
                label: $"fstail-adm-calib-low-{lowAdmitPerSec:0}",
                HostMode.FilesystemNoIndex,
                DefaultFanoutMaxQueuedBytes,
                enableTailCorrelation: true,
                aggregateAdmitPerSec: lowAdmitPerSec);
            Emit(
                $"fstail-admission-calib-done fixedRatesForMeasured=unrestricted,{midAdmitPerSec:0},{lowAdmitPerSec:0} "
                + "note=rates_not_adjusted_during_measured_cells");

            for (var r = 1; r <= repeats; r++)
            {
                await RunE2EAsync(
                    root,
                    duration,
                    workers,
                    outstanding: clients,
                    label: $"fstail-adm-unrestricted-r{r}",
                    HostMode.FilesystemNoIndex,
                    DefaultFanoutMaxQueuedBytes,
                    enableTailCorrelation: true,
                    aggregateAdmitPerSec: null);
                await RunE2EAsync(
                    root,
                    duration,
                    workers,
                    outstanding: clients,
                    label: $"fstail-adm-mid-{midAdmitPerSec:0}-r{r}",
                    HostMode.FilesystemNoIndex,
                    DefaultFanoutMaxQueuedBytes,
                    enableTailCorrelation: true,
                    aggregateAdmitPerSec: midAdmitPerSec);
                await RunE2EAsync(
                    root,
                    duration,
                    workers,
                    outstanding: clients,
                    label: $"fstail-adm-low-{lowAdmitPerSec:0}-r{r}",
                    HostMode.FilesystemNoIndex,
                    DefaultFanoutMaxQueuedBytes,
                    enableTailCorrelation: true,
                    aggregateAdmitPerSec: lowAdmitPerSec);
            }
        }

        /// <summary>
        /// FilesystemNoIndex c=8/w=4: baseline FS-tail then lightweight host telemetry, to correlate
        /// ≥50 ms Open stalls with process/GC/thread-pool samples. Activate with
        /// <c>VECTORNNTPD_STORAGE_FS_TAIL_HOST_TELEMETRY_BENCH=1</c>.
        /// </summary>
        [Fact]
        public async Task Optional_filesystem_tail_host_telemetry()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(EnvHostTelemetry),
                    "1",
                    StringComparison.Ordinal))
            {
                _output.WriteLine(
                    $"Set {EnvHostTelemetry}=1 to run FilesystemNoIndex host-telemetry isolation "
                    + "(c=8, w=4; baseline then lightweight process/GC/thread-pool sampling).");
                return;
            }

            var duration = ResolveHostTelemetryDuration(defaultSeconds: 60);
            var repeats = 2;
            if (int.TryParse(
                    Environment.GetEnvironmentVariable(EnvHostTelemetryRepeats),
                    out var configuredRepeats)
                && configuredRepeats is >= 1 and <= 3)
            {
                repeats = configuredRepeats;
            }

            var intervalMs = 100;
            if (int.TryParse(
                    Environment.GetEnvironmentVariable(EnvHostTelemetryIntervalMs),
                    out var configuredInterval)
                && configuredInterval is >= 50 and <= 1000)
            {
                intervalMs = configuredInterval;
            }

            const int workers = 4;
            const int clients = 8;
            var root = ResolveBenchRoot();
            WriteEnvPreamble(root, duration);
            Emit(
                $"fstail-host-telemetry-config workers={workers} clients={clients} "
                + $"maxQueuedBytes={DefaultFanoutMaxQueuedBytes} durationSec={duration.TotalSeconds:0} "
                + $"repeats={repeats} sampleIntervalMs={intervalMs} dedicatedDisk={root.IsDedicated} "
                + $"activation={EnvHostTelemetry}=1 mode=FilesystemNoIndex "
                + "cells=baseline_fs_tail,lightweight_host_telemetry "
                + "diskCounters=not_instrumented_no_PerformanceCounter_package "
                + "gcPause=GC.GetTotalPauseDuration_delta_per_sample "
                + "hypothesis=correlate_Open_ge50ms_with_process_GC_threadpool_samples "
                + "note=telemetry_exceptions_recorded_do_not_alter_article_processing");

            for (var r = 1; r <= repeats; r++)
            {
                await RunE2EAsync(
                    root,
                    duration,
                    workers,
                    outstanding: clients,
                    label: $"fstail-hosttel-baseline-r{r}",
                    HostMode.FilesystemNoIndex,
                    DefaultFanoutMaxQueuedBytes,
                    enableTailCorrelation: true,
                    enableHostTelemetry: false,
                    hostTelemetryIntervalMs: intervalMs);
                await RunE2EAsync(
                    root,
                    duration,
                    workers,
                    outstanding: clients,
                    label: $"fstail-hosttel-telem-r{r}",
                    HostMode.FilesystemNoIndex,
                    DefaultFanoutMaxQueuedBytes,
                    enableTailCorrelation: true,
                    enableHostTelemetry: true,
                    hostTelemetryIntervalMs: intervalMs);
            }
        }

        /// <summary>
        /// FilesystemNoIndex c=8/w=4 baseline vs same-volume low-rate CreateNew sidecar to test
        /// whether unrelated creates stall in the same windows as E2E Open bursts. Activate with
        /// <c>VECTORNNTPD_STORAGE_FS_TAIL_SIDECAR_BENCH=1</c>. Used when elevated ETW is unavailable.
        /// </summary>
        [Fact]
        public async Task Optional_filesystem_tail_same_volume_sidecar()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(EnvFsTailSidecar),
                    "1",
                    StringComparison.Ordinal))
            {
                _output.WriteLine(
                    $"Set {EnvFsTailSidecar}=1 to run FilesystemNoIndex same-volume CreateNew sidecar "
                    + "(c=8, w=4; E2E-only then E2E+sidecar).");
                return;
            }

            var duration = ResolveFsTailSidecarDuration(defaultSeconds: 60);
            var repeats = 2;
            if (int.TryParse(
                    Environment.GetEnvironmentVariable(EnvFsTailSidecarRepeats),
                    out var configuredRepeats)
                && configuredRepeats is >= 1 and <= 3)
            {
                repeats = configuredRepeats;
            }

            var sidecarRate = 20d;
            if (double.TryParse(
                    Environment.GetEnvironmentVariable(EnvFsTailSidecarRate),
                    out var configuredRate)
                && configuredRate is >= 1 and <= 200)
            {
                sidecarRate = configuredRate;
            }

            const int workers = 4;
            const int clients = 8;
            var root = ResolveBenchRoot();
            WriteEnvPreamble(root, duration);
            Emit(
                $"fstail-sidecar-config workers={workers} clients={clients} "
                + $"maxQueuedBytes={DefaultFanoutMaxQueuedBytes} durationSec={duration.TotalSeconds:0} "
                + $"repeats={repeats} sidecarOpsPerSec={sidecarRate:0.###} dedicatedDisk={root.IsDedicated} "
                + $"activation={EnvFsTailSidecar}=1 mode=FilesystemNoIndex "
                + "cells=e2e_only,e2e_plus_same_volume_CreateNew_sidecar "
                + "etwStatus=WPR_DiskIO_FileIO_AccessDenied_0x80070005_not_elevated "
                + "hypothesis=unrelated_CreateNew_on_same_volume_stalls_with_E2E_Open_bursts "
                + "note=sidecar_cannot_alone_establish_root_cause");

            for (var r = 1; r <= repeats; r++)
            {
                await RunE2EAsync(
                    root,
                    duration,
                    workers,
                    outstanding: clients,
                    label: $"fstail-sidecar-e2eonly-r{r}",
                    HostMode.FilesystemNoIndex,
                    DefaultFanoutMaxQueuedBytes,
                    enableTailCorrelation: true,
                    enableSameVolumeSidecar: false,
                    sidecarOpsPerSec: sidecarRate);
                await RunE2EAsync(
                    root,
                    duration,
                    workers,
                    outstanding: clients,
                    label: $"fstail-sidecar-withside-r{r}",
                    HostMode.FilesystemNoIndex,
                    DefaultFanoutMaxQueuedBytes,
                    enableTailCorrelation: true,
                    enableSameVolumeSidecar: true,
                    sidecarOpsPerSec: sidecarRate);
            }
        }

        private async Task RunE2EAsync(
            BenchRoot benchRoot,
            TimeSpan duration,
            int workers,
            int outstanding,
            string label,
            HostMode mode = HostMode.FullIndexed,
            long maxQueuedBytes = DefaultFanoutMaxQueuedBytes,
            TimeSpan? sinkDelay = null,
            bool enableTailCorrelation = false,
            AbsoluteSlowThresholds? absoluteSlowThresholds = null,
            Action<AbsoluteSlowThresholds>? onLocalSlowThresholds = null,
            bool applyLocalThresholdsAsAbsolute = false,
            double? aggregateAdmitPerSec = null,
            bool enableHostTelemetry = false,
            int hostTelemetryIntervalMs = 100,
            bool enableSameVolumeSidecar = false,
            double sidecarOpsPerSec = 20)
        {
            var runRoot = Path.Join(benchRoot.Path, label + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runRoot);
            var spool = Path.Join(runRoot, "spool");
            var db = Path.Join(runRoot, "db");
            Directory.CreateDirectory(spool);
            Directory.CreateDirectory(db);

            await using var host = await EndToEndHost.StartAsync(
                spool,
                db,
                workers,
                mode,
                maxQueuedBytes,
                sinkDelay,
                enableTailCorrelation);
            // Sequential TLS TAKETHIS (write then read on one SslStream). ArticlePlacementClient's
            // pipelined reader+writer deadlocks SslStream on large articles in this environment.
            var clients = new SequentialTlsClient[Math.Max(1, outstanding)];
            for (var i = 0; i < clients.Length; i++)
            {
                clients[i] = await SequentialTlsClient.ConnectAsync(host.Port).ConfigureAwait(false);
            }

            var admitStarts = new ConcurrentDictionary<ArticleId, long>(concurrencyLevel: Environment.ProcessorCount, capacity: 4096);
            var latenciesMs = new ConcurrentBag<double>();
            var admitLatencyMs = new ConcurrentBag<double>();
            var clientWriteMs = new ConcurrentBag<double>();
            var clientWait239Ms = new ConcurrentBag<double>();
            host.AttachCompletionObserver((id, completedTicks) =>
            {
                if (admitStarts.TryRemove(id, out var started))
                {
                    latenciesMs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            });
            if (enableTailCorrelation)
            {
                host.AttachTailQueueSnapshot(() =>
                    (host.Queue.AccountedBytes, host.Queue.QueuedCount, host.Queue.InFlightBytes));
                host.AttachTailAdmitStartLookup(id => admitStarts.TryGetValue(id, out var started) ? started : null);
            }

            var process = Process.GetCurrentProcess();
            process.Refresh();
            var wsStart = process.WorkingSet64;
            var cpuStart = process.TotalProcessorTime;
            var admitted = 0;
            var admitRejected = 0;
            var transportFailed = 0;
            var nextId = 0;
            var peakAccounted = 0L;
            var peakQueued = 0;
            var peakInFlight = 0L;
            var wireBytes = 0L;
            var samples = new List<(double t, long accounted, int queued, long inflight, long stored)>(512);
            var gate = new object();
            var stop = false;
            AggregateAdmissionPacer? admitPacer = aggregateAdmitPerSec is double rate && rate > 0
                ? new AggregateAdmissionPacer(rate)
                : null;

            var clock = Stopwatch.StartNew();
            var benchOriginTicks = Stopwatch.GetTimestamp();
            var benchOriginUtc = DateTime.UtcNow;
            host.SetBenchOrigin(benchOriginTicks, benchOriginUtc);
            HostTelemetrySampler? telemetry = null;
            if (enableHostTelemetry && !IsCompletingSinkMode(mode))
            {
                telemetry = new HostTelemetrySampler(
                    TimeSpan.FromMilliseconds(hostTelemetryIntervalMs),
                    benchOriginTicks,
                    capacity: 4096);
                telemetry.Start();
            }

            SameVolumeCreateNewSidecar? sidecar = null;
            string? sidecarRoot = null;
            if (enableSameVolumeSidecar && !IsCompletingSinkMode(mode))
            {
                sidecarRoot = Path.Join(
                    benchRoot.Path,
                    "sidecar-" + label + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(sidecarRoot);
                sidecar = new SameVolumeCreateNewSidecar(
                    sidecarRoot,
                    sidecarOpsPerSec,
                    benchOriginTicks,
                    runId: label);
                sidecar.Start();
            }

            try
            {
                var producers = clients.Select(client => Task.Run(async () =>
                {
                    while (true)
                    {
                        lock (gate)
                        {
                            if (stop || clock.Elapsed >= duration)
                            {
                                return;
                            }
                        }

                        if (admitPacer is not null)
                        {
                            await admitPacer.WaitTurnAsync().ConfigureAwait(false);
                            lock (gate)
                            {
                                if (stop || clock.Elapsed >= duration)
                                {
                                    return;
                                }
                            }
                        }

                        var index = Interlocked.Increment(ref nextId);
                        var built = BuildWireArticle(index, ArticleBytes);
                        var placeStarted = Stopwatch.GetTimestamp();
                        TakeThisTiming timing;
                        try
                        {
                            timing = await client.TakeThisTimedAsync(built).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (
                            ex is not OperationCanceledException && !ExceptionHelpers.IsFatal(ex))
                        {
                            Interlocked.Increment(ref transportFailed);
                            ExpectedException.Ignore(ex);
                            continue;
                        }

                        clientWriteMs.Add(timing.WriteMs);
                        clientWait239Ms.Add(timing.Wait239Ms);
                        if (timing.Code == 239)
                        {
                            Interlocked.Increment(ref admitted);
                            Interlocked.Add(ref wireBytes, built.WireBytes);
                            admitStarts[built.ArtId] = placeStarted;
                            admitLatencyMs.Add(timing.WriteMs + timing.Wait239Ms);
                        }
                        else if (timing.Code is 439 or 400)
                        {
                            Interlocked.Increment(ref admitRejected);
                        }
                        else
                        {
                            Interlocked.Increment(ref transportFailed);
                        }

                        var accounted = host.Queue.AccountedBytes;
                        var queued = host.Queue.QueuedCount;
                        var inflight = host.Queue.InFlightBytes;
                        lock (gate)
                        {
                            peakAccounted = Math.Max(peakAccounted, accounted);
                            peakQueued = Math.Max(peakQueued, queued);
                            peakInFlight = Math.Max(peakInFlight, inflight);
                            if (samples.Count == 0 || clock.Elapsed.TotalSeconds - samples[^1].t >= 1.0)
                            {
                                samples.Add((clock.Elapsed.TotalSeconds, accounted, queued, inflight, host.Counters.Stored));
                            }
                        }
                    }
                })).ToArray();

                await Task.Delay(duration).ConfigureAwait(false);
                lock (gate)
                {
                    stop = true;
                }

                await Task.WhenAll(producers).ConfigureAwait(false);

                // Stop sidecar at sustain end (before drain) so correlation covers publish load only.
                if (sidecar is not null)
                {
                    await sidecar.StopAsync().ConfigureAwait(false);
                }

                var durableDone = () => IsCompletingSinkMode(mode)
                    ? host.ObservingCompleted
                    : host.Counters.Stored + host.Counters.Duplicates;
                var remainingAtStop = Math.Max(0, admitted - (int)durableDone());
                var drainDeadline = TimeSpan.FromSeconds(Math.Clamp(120 + remainingAtStop * 2, 120, 900));
                var drainClock = Stopwatch.StartNew();
                while ((host.Queue.QueuedCount > 0 || host.Queue.InFlightBytes > 0 || host.Queue.AccountedBytes > 0
                        || durableDone() < admitted)
                       && drainClock.Elapsed < drainDeadline)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }

                var completed = durableDone();
                var drained = host.Queue.QueuedCount == 0
                    && host.Queue.InFlightBytes == 0
                    && host.Queue.AccountedBytes == 0
                    && completed >= admitted;

                process.Refresh();
                var wsPeak = Math.Max(wsStart, process.WorkingSet64);
                var cpu = process.TotalProcessorTime - cpuStart;
                var elapsed = clock.Elapsed;
                var sustainElapsed = duration;
                var payloadBytes = host.Counters.StoredBytes;
                if (payloadBytes == 0 && completed > 0)
                {
                    payloadBytes = completed * (long)ArticleBytes;
                }

                var payloadGbit = payloadBytes * 8d / Math.Max(sustainElapsed.TotalSeconds, 0.001) / 1_000_000_000d;
                var payloadGbitCompleted = completed > 0
                    ? ((double)completed * ArticleBytes * 8d)
                        / Math.Max(sustainElapsed.TotalSeconds, 0.001)
                        / 1_000_000_000d
                    : 0d;
                // Application framing bytes (TAKETHIS + article + terminator). TLS record overhead not measured.
                var wireGbitEst = wireBytes * 8d / Math.Max(sustainElapsed.TotalSeconds, 0.001) / 1_000_000_000d;

                var latency = latenciesMs.ToArray();
                Array.Sort(latency);
                var admitLat = admitLatencyMs.ToArray();
                Array.Sort(admitLat);
                var writeLat = clientWriteMs.ToArray();
                Array.Sort(writeLat);
                var wait239 = clientWait239Ms.ToArray();
                Array.Sort(wait239);
                var processLat = host.ProcessLatencyMs.ToArray();
                Array.Sort(processLat);

                var indexedOk = 0;
                var spoolOk = 0;
                var checkedCount = 0;
                if (!IsCompletingSinkMode(mode) && host.Index is not null)
                {
                    foreach (var id in host.CompletedIds.Take(Math.Min(admitted, 256)))
                    {
                        checkedCount++;
                        if (host.Index.Contains(id))
                        {
                            indexedOk++;
                        }

                        if (host.Index.TryGetRetrievableArticle(id, out var path) && File.Exists(path))
                        {
                            spoolOk++;
                        }
                    }
                }
                else
                {
                    checkedCount = Math.Min(admitted, 256);
                    indexedOk = checkedCount;
                    spoolOk = checkedCount;
                }

                var cpuPct = elapsed.TotalMilliseconds > 0
                    ? 100d * cpu.TotalMilliseconds / (elapsed.TotalMilliseconds * Environment.ProcessorCount)
                    : 0d;
                if (telemetry is not null)
                {
                    await telemetry.StopAsync().ConfigureAwait(false);
                }

                var occupancy = FormatOccupancyStats(samples, maxQueuedBytes);
                var line =
                    $"{label} mode={mode} sustainMs={sustainElapsed.TotalMilliseconds:0} wallMs={elapsed.TotalMilliseconds:0} drainMs={drainClock.Elapsed.TotalMilliseconds:0} "
                    + $"articleBytes={ArticleBytes} workers={workers} outstanding={outstanding} maxQueuedBytes={maxQueuedBytes} "
                    + $"aggregateAdmitPerSecCfg={(aggregateAdmitPerSec is double cfg ? cfg.ToString("0.###") : "unrestricted")} "
                    + $"hostTelemetry={(enableHostTelemetry ? $"on_intervalMs={hostTelemetryIntervalMs}" : "off")} "
                    + $"sameVolumeSidecar={(enableSameVolumeSidecar ? $"on_opsPerSec={sidecarOpsPerSec:0.###}" : "off")} "
                    + $"dedicatedDisk={benchRoot.IsDedicated} storageRoot={runRoot} filesystem=NTFS "
                    + $"admitted={admitted} admitRejected={admitRejected} transportFailed={transportFailed} "
                    + $"persistedStored={host.Counters.Stored} duplicates={host.Counters.Duplicates} deferred={host.Counters.Deferred} "
                    + $"ioFailures={host.Counters.IoFailures} diskFull={host.Counters.DiskFull} conflicts={host.Counters.Conflicts} "
                    + $"admittedPerSec={admitted / sustainElapsed.TotalSeconds:0.00} completedPerSec={completed / sustainElapsed.TotalSeconds:0.00} "
                    + $"payloadEquivalentGbitPerSec_completedArtSize={payloadGbitCompleted:0.000} "
                    + $"payloadGbitPerSec_storedBytesCounter={payloadGbit:0.000} "
                    + $"wireGbitPerSec_appPayloadFraming={wireGbitEst:0.000} wireNote=takethis_plus_article_plus_terminator_not_tls_record_overhead "
                    + $"tlsClient=sequential_SslStream connections={clients.Length} "
                    + $"e2eLatencyMs_p50={Percentile(latency, 0.50):0.00} p95={Percentile(latency, 0.95):0.00} p99={Percentile(latency, 0.99):0.00} samples={latency.Length} "
                    + $"admitLatencyMs_p50={Percentile(admitLat, 0.50):0.00} p95={Percentile(admitLat, 0.95):0.00} "
                    + $"(clientWriteMs_p50={Percentile(writeLat, 0.50):0.00} clientWait239Ms_p50={Percentile(wait239, 0.50):0.00}) "
                    + $"processMs_p50={Percentile(processLat, 0.50):0.00} p95={Percentile(processLat, 0.95):0.00} p99={Percentile(processLat, 0.99):0.00} "
                    + $"{host.PublishTimingReport} "
                    + $"{host.PublishExtremeStallReport(sustainElapsed.TotalSeconds)} "
                    + $"{host.PublishResidualReport(Percentile(processLat, 0.50))} "
                    + $"{host.ConcurrencyReport} "
                    + $"{host.TailCorrelationReport} "
                    + $"{occupancy} "
                    + $"peakAccounted={peakAccounted} peakQueued={peakQueued} peakInFlight={peakInFlight} "
                    + $"finalAccounted={host.Queue.AccountedBytes} finalQueued={host.Queue.QueuedCount} finalInFlight={host.Queue.InFlightBytes} "
                    + $"drained={drained} remainingAtStop={remainingAtStop} consistencyChecked={checkedCount} indexedOk={indexedOk} spoolOk={spoolOk} "
                    + $"wsStart={wsStart} wsEnd={process.WorkingSet64} wsPeakObserved={wsPeak} "
                    + $"cpuMs={cpu.TotalMilliseconds:0} cpuPctOfMachine={cpuPct:0.0} managedBytes={GC.GetTotalMemory(false)} "
                    + $"{host.SlowOpEventReport(benchOriginTicks)} "
                    + $"{(telemetry?.Report(benchOriginTicks) ?? "hostTelemetrySamples=n/a")} "
                    + $"{(sidecar?.Report(sustainElapsed.TotalSeconds) ?? "sidecar=n/a")}";
                Emit(line);

                if (enableTailCorrelation && host.TryGetLocalSlowThresholds(out var localThresholds))
                {
                    onLocalSlowThresholds?.Invoke(localThresholds);
                    var absolute = absoluteSlowThresholds
                        ?? (applyLocalThresholdsAsAbsolute ? localThresholds : (AbsoluteSlowThresholds?)null);
                    if (absolute is AbsoluteSlowThresholds abs)
                    {
                        Emit(
                            $"{label} absoluteSlowAnalysis "
                            + host.TailAbsoluteEventOrderReport(abs));
                    }
                }

                var e2eSlow = host.EnumerateSlowOpEvents(benchOriginTicks);
                foreach (var slow in e2eSlow)
                {
                    Emit($"  {slow}");
                    if (telemetry is not null)
                    {
                        Emit($"    {telemetry.FormatAlignment(slow.OpenStartTicks, slow.OpenEndTicks, benchOriginTicks)}");
                    }
                }

                if (sidecar is not null)
                {
                    Emit($"{label} {sidecar.CorrelationReport(e2eSlow, benchOriginTicks)}");
                    foreach (var lineSidecar in sidecar.EnumerateSlowEvents(benchOriginTicks))
                    {
                        Emit($"  {lineSidecar}");
                    }
                }

                foreach (var s in samples)
                {
                    Emit($"  sample t={s.t:0.0}s accounted={s.accounted} queued={s.queued} inflight={s.inflight} stored={s.stored}");
                }

                if (telemetry is not null && telemetry.FaultCount > 0)
                {
                    Assert.Fail($"host telemetry recorded {telemetry.FaultCount} sampler fault(s); see hostTelemetry report");
                }

                if (sidecar is not null && sidecar.FaultCount > 0)
                {
                    Assert.Fail($"same-volume sidecar recorded {sidecar.FaultCount} fault(s); see sidecar report");
                }

                Assert.True(admitted > 0, "expected at least one admitted article");
                Assert.True(drained, $"accepted work did not drain within {drainDeadline.TotalSeconds:0}s (completed={completed} admitted={admitted})");
                if (IsCompletingSinkMode(mode))
                {
                    Assert.True(host.ObservingCompleted >= admitted);
                    // Sink modes (including calibrated delay) never attach TimingSpoolFiles.
                    Assert.Equal("publishTiming=n/a", host.PublishTimingReport);
                }
                else
                {
                    Assert.Equal(admitted, completed);
                    Assert.Equal(checkedCount, indexedOk);
                    Assert.Equal(checkedCount, spoolOk);
                }
            }
            finally
            {
                if (sidecar is not null)
                {
                    try
                    {
                        await sidecar.StopAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                    {
                        Emit($"{label} sidecar-stop-warning {ex.GetType().Name}:{ex.Message}");
                    }
                }

                if (telemetry is not null)
                {
                    try
                    {
                        await telemetry.StopAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                    {
                        Emit($"{label} hostTelemetry-stop-warning {ex.GetType().Name}:{ex.Message}");
                    }
                }

                foreach (var client in clients)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }

                if (sidecarRoot is not null)
                {
                    try
                    {
                        Directory.Delete(sidecarRoot, recursive: true);
                    }
                    catch (IOException ex)
                    {
                        Emit($"{label} sidecar-cleanup-warning {ex.GetType().Name}:{ex.Message}");
                    }
                }

                try
                {
                    Directory.Delete(runRoot, recursive: true);
                }
                catch (IOException ex)
                {
                    // Best-effort cleanup of the isolated run directory.
                    ExpectedException.Ignore(ex);
                }
            }
        }

        private async Task MeasureShutdownDrainAsync(BenchRoot benchRoot, int articleCount, string label)
        {
            var runRoot = Path.Join(benchRoot.Path, label + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runRoot);
            var spool = Path.Join(runRoot, "spool");
            var db = Path.Join(runRoot, "db");
            Directory.CreateDirectory(spool);
            Directory.CreateDirectory(db);

            await using var host = await EndToEndHost.StartAsync(spool, db, workers: 4);
            for (var i = 0; i < articleCount; i++)
            {
                Assert.True(host.Queue.TryEnqueue(BuildAccepted(i, ArticleBytes)));
            }

            var acceptedAtStop = host.Queue.EnqueuedCount;
            var queuedAtStop = host.Queue.QueuedCount;
            var inFlightAtStop = host.Queue.InFlightBytes;
            var accountedAtStop = host.Queue.AccountedBytes;

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var sw = Stopwatch.StartNew();
            Exception? stopError = null;
            try
            {
                await host.StopWorkerAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopError = ex;
            }
            catch (OperationCanceledException oce)
            {
                stopError = oce;
            }

            sw.Stop();
            var completed = host.Counters.Stored + host.Counters.Duplicates;
            var line =
                $"{label} articleBytes={ArticleBytes} acceptedAtStop={acceptedAtStop} queuedAtStop={queuedAtStop} "
                + $"inFlightAtStop={inFlightAtStop} accountedAtStop={accountedAtStop} "
                + $"shutdownMs={sw.Elapsed.TotalMilliseconds:0} completed={completed} "
                + $"finalQueued={host.Queue.QueuedCount} finalAccounted={host.Queue.AccountedBytes} finalInFlight={host.Queue.InFlightBytes} "
                + $"stopError={(stopError is null ? "none" : stopError.GetType().Name + ":" + stopError.Message)} "
                + $"persistedOk={(stopError is null && completed == acceptedAtStop && host.Queue.AccountedBytes == 0)}";
            Emit(line);

            Assert.Null(stopError);
            Assert.Equal(acceptedAtStop, completed);
            Assert.Equal(0, host.Queue.AccountedBytes);

            try
            {
                Directory.Delete(runRoot, recursive: true);
            }
            catch (IOException ex)
            {
                ExpectedException.Ignore(ex);
            }
        }

        private async Task MeasureShutdownCancelFailsAsync(string label)
        {
            var time = TimeProvider.System;
            var queue = new AcceptedArticleQueue(1 << 30, time);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var processor = new HoldingProcessor(entered);
            var worker = new AcceptedArticleWorker(
                queue,
                processor,
                NullLogger<AcceptedArticleWorker>.Instance,
                new SpoolPersistenceOptions { MinWorkers = 1, MaxWorkers = 1 },
                counters: new SpoolPersistenceCounters(),
                time: time);
            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.True(queue.TryEnqueue(BuildAccepted(0, ArticleBytes)));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            var acceptedAtStop = queue.EnqueuedCount;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var sw = Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.StopAsync(timeout.Token));
            sw.Stop();
            Emit(
                $"{label} acceptedAtStop={acceptedAtStop} shutdownMs={sw.Elapsed.TotalMilliseconds:0} "
                + $"completed={processor.Completed} outstandingAccounted={queue.AccountedBytes} "
                + $"failureSurfaced=true message={ex.Message}");
            Assert.Equal(0, processor.Completed);
            Assert.Contains("did not finish", ex.Message, StringComparison.Ordinal);
        }

        private async Task MeasureDisconnectAfterAdmitAsync(BenchRoot benchRoot, string label)
        {
            var runRoot = Path.Join(benchRoot.Path, label + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runRoot);
            var spool = Path.Join(runRoot, "spool");
            var db = Path.Join(runRoot, "db");
            Directory.CreateDirectory(spool);
            Directory.CreateDirectory(db);

            await using var host = await EndToEndHost.StartAsync(spool, db, workers: 2);
            WireArticle built;
            await using (var client = await SequentialTlsClient.ConnectAsync(host.Port).ConfigureAwait(false))
            {
                built = BuildWireArticle(1, ArticleBytes);
                var code = await client.TakeThisAsync(built).ConfigureAwait(false);
                Assert.Equal(239, code);
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (host.Counters.Stored + host.Counters.Duplicates < 1 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20).ConfigureAwait(false);
            }

            var completed = host.Counters.Stored + host.Counters.Duplicates;
            var indexed = host.Index is not null && host.Index.Contains(built.ArtId);
            var retrievable = host.Index is not null
                && host.Index.TryGetRetrievableArticle(built.ArtId, out var path)
                && File.Exists(path);
            Emit(
                $"{label} admitted=1 completed={completed} indexed={indexed} retrievable={retrievable} "
                + $"finalAccounted={host.Queue.AccountedBytes} silentlyDropped={completed == 0}");
            Assert.Equal(1, completed);
            Assert.True(indexed);
            Assert.True(retrievable);

            try
            {
                Directory.Delete(runRoot, recursive: true);
            }
            catch (IOException ex)
            {
                ExpectedException.Ignore(ex);
            }
        }

        private async Task<WakeSample> MeasureNotifyWakeAsync()
        {
            var time = TimeProvider.System;
            var queue = new AcceptedArticleQueue(1 << 20, time);
            var signal = CreateSignal();
            var processor = new TimedCapacityProcessor();
            var worker = new AcceptedArticleWorker(
                queue,
                processor,
                NullLogger<AcceptedArticleWorker>.Instance,
                new SpoolPersistenceOptions
                {
                    MinWorkers = 1,
                    MaxWorkers = 1,
                    IneligibleRetryMilliseconds = 60_000,
                    ControlIntervalMilliseconds = 60_000,
                },
                new SpoolPressure(signal),
                new SpoolPersistenceCounters(),
                time,
                signal);
            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                Assert.True(queue.TryEnqueue(BuildAccepted(Random.Shared.Next(), 4096)));
                await WaitAsync(() => processor.WaitingCalls >= 1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var waitStarted = processor.LastWaitTicks;
                processor.AllowComplete = true;
                var notifyTicks = Stopwatch.GetTimestamp();
                signal.Notify();
                await WaitAsync(() => processor.Completed >= 1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var resumeTicks = processor.FirstResumeTicks;
                var completeTicks = processor.CompleteTicks;
                return new WakeSample(
                    Stopwatch.GetElapsedTime(notifyTicks, resumeTicks).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(resumeTicks, completeTicks).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(notifyTicks, completeTicks).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(waitStarted, notifyTicks).TotalMilliseconds);
            }
            finally
            {
                processor.AllowComplete = true;
                signal.Notify();
                await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        private async Task<SampleWake> MeasureSamplePublishedWakeAsync()
        {
            var root = Path.Join(ResolveBenchRoot().Path, "cap-sample-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var probe = new ControllableProbe();
                probe.Set(root, total: 100L << 30, available: 1L << 20);
                var catalog = new SegmentPoolCatalog();
                catalog.Publish([new SegmentPoolDevice("block:cap", [Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))])]);
                var monitor = new BlockDeviceMonitorService(
                    catalog,
                    new FlatCounters(),
                    NullLogger<BlockDeviceMonitorService>.Instance,
                    TimeSpan.FromHours(1),
                    TimeProvider.System,
                    probe);
                InvokeSample(monitor);
                var signal = new SpoolCapacityWaitSignal(monitor);
                var time = TimeProvider.System;
                var queue = new AcceptedArticleQueue(1 << 20, time);
                var counters = new SpoolPersistenceCounters();
                var processor = new SpoolArticlePersistenceProcessor(
                    "cache01.usenet.ninja",
                    catalog,
                    monitor,
                    new SpoolWriteGate(),
                    new SpoolArticleIdentityGate(),
                    new SpoolHealth(time),
                    new SpoolPressure(signal),
                    new SpoolArticlePublisher(new FileSpoolArticleFiles()),
                    counters,
                    new StorageServerOptions(),
                    NullLogger.Instance,
                    time);
                var worker = new AcceptedArticleWorker(
                    queue,
                    processor,
                    NullLogger<AcceptedArticleWorker>.Instance,
                    new SpoolPersistenceOptions
                    {
                        MinWorkers = 1,
                        MaxWorkers = 1,
                        IneligibleRetryMilliseconds = 60_000,
                        ControlIntervalMilliseconds = 60_000,
                    },
                    new SpoolPressure(signal),
                    counters,
                    time,
                    signal);
                await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    Assert.True(queue.TryEnqueue(BuildAccepted(1, 8192)));
                    await WaitAsync(() => counters.Deferred >= 1 || queue.InFlightBytes > 0, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    await Task.Delay(50).ConfigureAwait(false);
                    Assert.Equal(0, counters.Stored);

                    probe.Set(root, total: 100L << 30, available: 80L << 30);
                    var eligibleTicks = Stopwatch.GetTimestamp();
                    long notifyTicks = 0;
                    void OnSample() => notifyTicks = Stopwatch.GetTimestamp();
                    monitor.CapacitySamplePublished += OnSample;
                    try
                    {
                        InvokeSample(monitor);
                        Assert.True(notifyTicks > 0, "CapacitySamplePublished did not fire");
                        var eligibleToNotify = Stopwatch.GetElapsedTime(eligibleTicks, notifyTicks).TotalMilliseconds;
                        await WaitAsync(() => counters.Stored >= 1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        var completeTicks = Stopwatch.GetTimestamp();
                        return new SampleWake(
                            eligibleToNotify,
                            Stopwatch.GetElapsedTime(notifyTicks, completeTicks).TotalMilliseconds,
                            Stopwatch.GetElapsedTime(notifyTicks, completeTicks).TotalMilliseconds);
                    }
                    finally
                    {
                        monitor.CapacitySamplePublished -= OnSample;
                    }
                }
                finally
                {
                    await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
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
                    ExpectedException.Ignore(ex);
                }
            }
        }

        private async Task<EvictionWake> MeasureEvictionFreedWakeAsync()
        {
            var root = Path.Join(ResolveBenchRoot().Path, "cap-evict-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var probe = new ControllableProbe();
                probe.Set(root, total: 100L << 30, available: 1L << 20);
                var catalog = new SegmentPoolCatalog();
                var spool = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                catalog.Publish([new SegmentPoolDevice("block:evict", [spool])]);
                var monitor = new BlockDeviceMonitorService(
                    catalog,
                    new FlatCounters(),
                    NullLogger<BlockDeviceMonitorService>.Instance,
                    TimeSpan.FromHours(1),
                    TimeProvider.System,
                    probe);
                InvokeSample(monitor);
                var signal = new SpoolCapacityWaitSignal(monitor);
                var time = TimeProvider.System;
                var queue = new AcceptedArticleQueue(1 << 20, time);
                var processor = new TimedCapacityProcessor();
                var worker = new AcceptedArticleWorker(
                    queue,
                    processor,
                    NullLogger<AcceptedArticleWorker>.Instance,
                    new SpoolPersistenceOptions
                    {
                        MinWorkers = 1,
                        MaxWorkers = 1,
                        IneligibleRetryMilliseconds = 60_000,
                        ControlIntervalMilliseconds = 60_000,
                    },
                    new SpoolPressure(signal),
                    new SpoolPersistenceCounters(),
                    time,
                    signal);
                await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    Assert.True(queue.TryEnqueue(BuildAccepted(2, 4096)));
                    await WaitAsync(() => processor.WaitingCalls >= 1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    processor.AllowComplete = true;
                    long publishedTicks = 0;
                    void OnPublished() => publishedTicks = Stopwatch.GetTimestamp();
                    monitor.CapacitySamplePublished += OnPublished;
                    try
                    {
                        var freedTicks = Stopwatch.GetTimestamp();
                        monitor.NoteFreedBytes(spool, 1L << 30);
                        Assert.True(publishedTicks > 0);
                        await WaitAsync(() => processor.Completed >= 1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        return new EvictionWake(
                            Stopwatch.GetElapsedTime(freedTicks, publishedTicks).TotalMilliseconds,
                            Stopwatch.GetElapsedTime(publishedTicks, processor.FirstResumeTicks).TotalMilliseconds,
                            Stopwatch.GetElapsedTime(publishedTicks, processor.CompleteTicks).TotalMilliseconds);
                    }
                    finally
                    {
                        monitor.CapacitySamplePublished -= OnPublished;
                    }
                }
                finally
                {
                    processor.AllowComplete = true;
                    signal.Notify();
                    await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
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
                    ExpectedException.Ignore(ex);
                }
            }
        }

        private async Task<CriticalSample> MeasureCriticalSpoolExcludedAsync()
        {
            var root = Path.Join(ResolveBenchRoot().Path, "cap-crit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var probe = new ControllableProbe();
                // ~99% used → above default CriticalPercent 85.
                probe.Set(root, total: 100L << 30, available: 1L << 20);
                var catalog = new SegmentPoolCatalog();
                catalog.Publish([new SegmentPoolDevice("block:crit", [Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))])]);
                var monitor = new BlockDeviceMonitorService(
                    catalog,
                    new FlatCounters(),
                    NullLogger<BlockDeviceMonitorService>.Instance,
                    TimeSpan.FromHours(1),
                    TimeProvider.System,
                    probe);
                InvokeSample(monitor);
                var counters = new SpoolPersistenceCounters();
                var processor = new SpoolArticlePersistenceProcessor(
                    "cache01.usenet.ninja",
                    catalog,
                    monitor,
                    new SpoolWriteGate(),
                    new SpoolArticleIdentityGate(),
                    new SpoolHealth(TimeProvider.System),
                    new SpoolPressure(),
                    new SpoolArticlePublisher(new FileSpoolArticleFiles()),
                    counters,
                    new StorageServerOptions(),
                    NullLogger.Instance,
                    TimeProvider.System);
                var started = Stopwatch.GetTimestamp();
                var result = await processor.ProcessAsync(BuildAccepted(3, 8192), CancellationToken.None).ConfigureAwait(false);
                return new CriticalSample(result, counters.Deferred, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException ex)
                {
                    ExpectedException.Ignore(ex);
                }
            }
        }

        private async Task<MultiSpoolSample> MeasureMultiSpoolIndependentRecoveryAsync()
        {
            var rootA = Path.Join(ResolveBenchRoot().Path, "cap-ms-a-" + Guid.NewGuid().ToString("N"));
            var rootB = Path.Join(ResolveBenchRoot().Path, "cap-ms-b-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootA);
            Directory.CreateDirectory(rootB);
            try
            {
                var probe = new ControllableProbe();
                probe.Set(rootA, total: 100L << 30, available: 1L << 20);
                probe.Set(rootB, total: 100L << 30, available: 80L << 30);
                var catalog = new SegmentPoolCatalog();
                var pathA = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootA));
                var pathB = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootB));
                catalog.Publish(
                [
                    new SegmentPoolDevice("block:a", [pathA]),
                    new SegmentPoolDevice("block:b", [pathB]),
                ]);
                var monitor = new BlockDeviceMonitorService(
                    catalog,
                    new FlatCounters(),
                    NullLogger<BlockDeviceMonitorService>.Instance,
                    TimeSpan.FromHours(1),
                    TimeProvider.System,
                    probe);
                InvokeSample(monitor);
                var counters = new SpoolPersistenceCounters();
                var processor = new SpoolArticlePersistenceProcessor(
                    "cache01.usenet.ninja",
                    catalog,
                    monitor,
                    new SpoolWriteGate(),
                    new SpoolArticleIdentityGate(),
                    new SpoolHealth(TimeProvider.System),
                    new SpoolPressure(),
                    new SpoolArticlePublisher(new FileSpoolArticleFiles()),
                    counters,
                    new StorageServerOptions(),
                    NullLogger.Instance,
                    TimeProvider.System);

                var healthy = await processor.ProcessAsync(BuildAccepted(4, 8192), CancellationToken.None).ConfigureAwait(false);
                Assert.Equal(AcceptedArticleProcessResult.Completed, healthy);
                var healthyStored = counters.Stored;

                probe.Set(rootB, total: 100L << 30, available: 1L << 20);
                InvokeSample(monitor);
                var blockedDeferredBefore = counters.Deferred;
                var blocked = await processor.ProcessAsync(BuildAccepted(5, 8192), CancellationToken.None).ConfigureAwait(false);
                Assert.Equal(AcceptedArticleProcessResult.WaitingForCapacity, blocked);

                probe.Set(rootB, total: 100L << 30, available: 80L << 30);
                var notifyTicks = Stopwatch.GetTimestamp();
                InvokeSample(monitor);
                var recovered = await processor.ProcessAsync(BuildAccepted(6, 8192), CancellationToken.None).ConfigureAwait(false);
                Assert.Equal(AcceptedArticleProcessResult.Completed, recovered);
                return new MultiSpoolSample(
                    counters.Deferred - blockedDeferredBefore,
                    healthyStored,
                    Stopwatch.GetElapsedTime(notifyTicks).TotalMilliseconds);
            }
            finally
            {
                try
                {
                    Directory.Delete(rootA, recursive: true);
                }
                catch (IOException ex)
                {
                    ExpectedException.Ignore(ex);
                }

                try
                {
                    Directory.Delete(rootB, recursive: true);
                }
                catch (IOException ex)
                {
                    ExpectedException.Ignore(ex);
                }
            }
        }

        private bool Enabled() =>
            string.Equals(Environment.GetEnvironmentVariable(EnvFlag), "1", StringComparison.Ordinal);

        private void Skip() => _output.WriteLine($"Set {EnvFlag}=1 to run end-to-end resilience benches.");

        private void Emit(string line)
        {
            Console.WriteLine(line);
            _output.WriteLine(line);
        }

        private void WriteEnvPreamble(BenchRoot root, TimeSpan duration)
        {
            Emit(
                $"e2e-env os={RuntimeInformation.OSDescription} arch={RuntimeInformation.OSArchitecture} "
                + $"framework={RuntimeInformation.FrameworkDescription} cpus={Environment.ProcessorCount} "
                + $"storageRoot={root.Path} dedicatedDisk={root.IsDedicated} filesystem=NTFS "
                + $"articleBytes={ArticleBytes} durationSec={duration.TotalSeconds:0} "
                + $"benchRootEnv={(Environment.GetEnvironmentVariable(EnvRoot) ?? "unset")}");
        }

        private static TimeSpan ResolveDuration(int defaultSeconds)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable(EnvSeconds), out var seconds) && seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }

            return TimeSpan.FromSeconds(defaultSeconds);
        }

        private static TimeSpan ResolveBottleneckDuration(int defaultSeconds)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable(EnvBottleneckSeconds), out var seconds) && seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }

            return TimeSpan.FromSeconds(defaultSeconds);
        }

        private static TimeSpan ResolveFanoutDuration(int defaultSeconds)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable(EnvFanoutSeconds), out var seconds) && seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }

            return TimeSpan.FromSeconds(defaultSeconds);
        }

        private static TimeSpan ResolveStageDuration(int defaultSeconds)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable(EnvStageSeconds), out var seconds) && seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }

            return TimeSpan.FromSeconds(defaultSeconds);
        }

        private static TimeSpan ResolveFsPubDuration(int defaultSeconds)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable(EnvFsPubSeconds), out var seconds) && seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }

            return TimeSpan.FromSeconds(defaultSeconds);
        }

        private static TimeSpan ResolveE2eFsBottleneckDuration(int defaultSeconds)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable(EnvE2eFsBottleneckSeconds), out var seconds) && seconds > 0)
            {
                return TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 180));
            }

            return TimeSpan.FromSeconds(defaultSeconds);
        }

        private static TimeSpan ResolveCalibratedDelayDuration(int defaultSeconds)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable(EnvCalibratedDelaySeconds), out var seconds) && seconds > 0)
            {
                return TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 180));
            }

            return TimeSpan.FromSeconds(defaultSeconds);
        }

        private static TimeSpan ResolveFsTailDuration(int defaultSeconds)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable(EnvFsTailSeconds), out var seconds) && seconds > 0)
            {
                return TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 180));
            }

            return TimeSpan.FromSeconds(defaultSeconds);
        }

        private static TimeSpan ResolveFsTailWorkerIsolationDuration(int defaultSeconds)
        {
            if (int.TryParse(
                    Environment.GetEnvironmentVariable(EnvFsTailWorkerIsolationSeconds),
                    out var seconds)
                && seconds > 0)
            {
                return TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 180));
            }

            return TimeSpan.FromSeconds(defaultSeconds);
        }

        private static TimeSpan ResolveAdmissionHeadroomDuration(int defaultSeconds)
        {
            if (int.TryParse(
                    Environment.GetEnvironmentVariable(EnvAdmissionHeadroomSeconds),
                    out var seconds)
                && seconds > 0)
            {
                return TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 180));
            }

            return TimeSpan.FromSeconds(defaultSeconds);
        }

        private static TimeSpan ResolveHostTelemetryDuration(int defaultSeconds)
        {
            if (int.TryParse(
                    Environment.GetEnvironmentVariable(EnvHostTelemetrySeconds),
                    out var seconds)
                && seconds > 0)
            {
                return TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 180));
            }

            return TimeSpan.FromSeconds(defaultSeconds);
        }

        private static TimeSpan ResolveFsTailSidecarDuration(int defaultSeconds)
        {
            if (int.TryParse(
                    Environment.GetEnvironmentVariable(EnvFsTailSidecarSeconds),
                    out var seconds)
                && seconds > 0)
            {
                return TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 180));
            }

            return TimeSpan.FromSeconds(defaultSeconds);
        }

        private static string FormatOccupancyStats(
            List<(double t, long accounted, int queued, long inflight, long stored)> samples,
            long maxQueuedBytes)
        {
            if (samples.Count == 0)
            {
                return "occupancySamples=0";
            }

            var accounted = new double[samples.Count];
            var nearCap = 0;
            var nearCapThreshold = (long)(maxQueuedBytes * 0.90);
            for (var i = 0; i < samples.Count; i++)
            {
                accounted[i] = samples[i].accounted;
                if (samples[i].accounted >= nearCapThreshold)
                {
                    nearCap++;
                }
            }

            Array.Sort(accounted);
            return
                $"occupancySamples={samples.Count} "
                + $"accountedSample_p50={Percentile(accounted, 0.50):0} "
                + $"p95={Percentile(accounted, 0.95):0} "
                + $"max={accounted[^1]:0} "
                + $"fracNearCap_ge90pctMaxQueued={nearCap / (double)samples.Count:0.000} "
                + $"nearCapThresholdBytes={nearCapThreshold}";
        }

        private static bool IsCompletingSinkMode(HostMode mode) =>
            mode is HostMode.CompletingSink or HostMode.CompletingSinkCalibratedDelay;

        /// <summary>
        /// Lightweight background sampler of process/GC/thread-pool counters (test-only).
        /// Disk PDH counters are not collected (no PerformanceCounter package). Samples use the same
        /// Stopwatch timeline as slow-op events; wall UTC is recorded for external alignment notes.
        /// </summary>
        private sealed class HostTelemetrySampler
        {
            private readonly TimeSpan _interval;
            private readonly long _originTicks;
            private readonly TelemetrySample[] _samples;
            private readonly ConcurrentBag<string> _faults = [];
            private int _next;
            private int _overwrites;
            private int _count;
            private CancellationTokenSource? _cts;
            private Task? _loop;
            private readonly Process _process = Process.GetCurrentProcess();

            public HostTelemetrySampler(TimeSpan interval, long originTicks, int capacity)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 64);
                _interval = interval;
                _originTicks = originTicks;
                _samples = new TelemetrySample[capacity];
            }

            public int FaultCount => _faults.Count;

            public void Start()
            {
                _cts = new CancellationTokenSource();
                _loop = Task.Run(() => LoopAsync(_cts.Token));
            }

            public async Task StopAsync()
            {
                if (_cts is null)
                {
                    return;
                }

                _cts.Cancel();
                if (_loop is not null)
                {
                    try
                    {
                        await _loop.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex)
                    {
                        ExpectedException.Ignore(ex);
                    }
                    catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                    {
                        _faults.Add($"{ex.GetType().Name}:{ex.Message}");
                    }
                }

                _cts.Dispose();
                _cts = null;
                _loop = null;
            }

            private async Task LoopAsync(CancellationToken cancellationToken)
            {
                var lastCpu = _process.TotalProcessorTime;
                var lastPause = GC.GetTotalPauseDuration();
                var lastTicks = Stopwatch.GetTimestamp();
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
                        var nowTicks = Stopwatch.GetTimestamp();
                        _process.Refresh();
                        var cpu = _process.TotalProcessorTime;
                        var pause = GC.GetTotalPauseDuration();
                        var dt = (nowTicks - lastTicks) / (double)Stopwatch.Frequency;
                        var cpuDelta = (cpu - lastCpu).TotalSeconds;
                        var cpuPct = dt > 0
                            ? 100d * cpuDelta / (dt * Environment.ProcessorCount)
                            : 0;
                        var pauseDeltaMs = (pause - lastPause).TotalMilliseconds;
                        ThreadPool.GetAvailableThreads(out var availWorker, out var availIo);
                        ThreadPool.GetMaxThreads(out var maxWorker, out var maxIo);
                        var sample = new TelemetrySample(
                            nowTicks,
                            DateTime.UtcNow,
                            cpuPct,
                            _process.WorkingSet64,
                            _process.PrivateMemorySize64,
                            _process.Threads.Count,
                            GC.CollectionCount(0),
                            GC.CollectionCount(1),
                            GC.CollectionCount(2),
                            pauseDeltaMs,
                            GC.GetTotalMemory(false),
                            maxWorker - availWorker,
                            maxIo - availIo,
                            ThreadPool.PendingWorkItemCount);

                        var index = Interlocked.Increment(ref _next) - 1;
                        if (index >= _samples.Length)
                        {
                            Interlocked.Increment(ref _overwrites);
                        }

                        _samples[index % _samples.Length] = sample;
                        Volatile.Write(ref _count, Math.Min(index + 1, _samples.Length));
                        lastCpu = cpu;
                        lastPause = pause;
                        lastTicks = nowTicks;
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                    {
                        _faults.Add($"{ex.GetType().Name}:{ex.Message}");
                    }
                }
            }

            public string Report(long benchOriginTicks)
            {
                var n = Volatile.Read(ref _count);
                var overwrites = Volatile.Read(ref _overwrites);
                if (n <= 0)
                {
                    return
                        $"hostTelemetrySamples=0 intervalMs={_interval.TotalMilliseconds:0} "
                        + $"cap={_samples.Length} overwrites={overwrites} faults={_faults.Count} "
                        + "diskCounters=unavailable_no_PerformanceCounter_package";
                }

                double sumCpu = 0, maxCpu = 0;
                long maxWs = 0, maxPrivate = 0;
                double sumPause = 0, maxPause = 0;
                var maxPending = 0;
                var maxThreads = 0;
                for (var i = 0; i < n; i++)
                {
                    var s = _samples[i];
                    sumCpu += s.CpuPctOfMachine;
                    maxCpu = Math.Max(maxCpu, s.CpuPctOfMachine);
                    maxWs = Math.Max(maxWs, s.WorkingSet);
                    maxPrivate = Math.Max(maxPrivate, s.PrivateBytes);
                    sumPause += s.GcPauseDeltaMs;
                    maxPause = Math.Max(maxPause, s.GcPauseDeltaMs);
                    maxPending = Math.Max(maxPending, (int)s.PendingWorkItems);
                    maxThreads = Math.Max(maxThreads, s.ThreadCount);
                }

                _ = benchOriginTicks;
                return
                    $"hostTelemetrySamples={n} intervalMs={_interval.TotalMilliseconds:0} "
                    + $"cap={_samples.Length} overwrites={overwrites} faults={_faults.Count} "
                    + $"cpuPct_mean={sumCpu / n:0.0} max={maxCpu:0.0} "
                    + $"wsPeak={maxWs} privatePeak={maxPrivate} threadPeak={maxThreads} "
                    + $"gcPauseDeltaMs_sum={sumPause:0.000} maxSample={maxPause:0.000} "
                    + $"pendingWorkPeak={maxPending} "
                    + "diskCounters=unavailable_no_PerformanceCounter_package "
                    + "note=gcPauseDelta_is_sample_window_not_per_collection_cause";
            }

            public string FormatAlignment(long openStartTicks, long openEndTicks, long benchOriginTicks)
            {
                var n = Volatile.Read(ref _count);
                if (n <= 0)
                {
                    return "align samples=0";
                }

                TelemetrySample? before = null;
                TelemetrySample? duringOrAfter = null;
                for (var i = 0; i < n; i++)
                {
                    var s = _samples[i];
                    if (s.Ticks <= openStartTicks)
                    {
                        before = s;
                    }

                    if (s.Ticks >= openStartTicks && duringOrAfter is null)
                    {
                        duringOrAfter = s;
                    }

                    if (s.Ticks >= openEndTicks && duringOrAfter is not null)
                    {
                        duringOrAfter = s;
                        break;
                    }
                }

                duringOrAfter ??= before;
                static string Fmt(TelemetrySample? s, long origin)
                {
                    if (s is null)
                    {
                        return "none";
                    }

                    var t = (s.Value.Ticks - origin) / (double)Stopwatch.Frequency;
                    return
                        $"t={t:0.000}s cpuPct={s.Value.CpuPctOfMachine:0.0} ws={s.Value.WorkingSet} "
                        + $"threads={s.Value.ThreadCount} gc01={s.Value.Gen0}/{s.Value.Gen1}/{s.Value.Gen2} "
                        + $"pauseDeltaMs={s.Value.GcPauseDeltaMs:0.000} pending={s.Value.PendingWorkItems} "
                        + $"busyWorkers={s.Value.BusyWorkers}";
                }

                var openT0 = (openStartTicks - benchOriginTicks) / (double)Stopwatch.Frequency;
                var openT1 = (openEndTicks - benchOriginTicks) / (double)Stopwatch.Frequency;
                return
                    $"align openWindow=[{openT0:0.000},{openT1:0.000}]s "
                    + $"before=({Fmt(before, benchOriginTicks)}) "
                    + $"near=({Fmt(duringOrAfter, benchOriginTicks)}) "
                    + $"sampleIntervalMayExceedStall_note=100ms_default_vs_50ms_event";
            }

            private readonly record struct TelemetrySample(
                long Ticks,
                DateTime Utc,
                double CpuPctOfMachine,
                long WorkingSet,
                long PrivateBytes,
                int ThreadCount,
                int Gen0,
                int Gen1,
                int Gen2,
                double GcPauseDeltaMs,
                long ManagedBytes,
                int BusyWorkers,
                int BusyIo,
                long PendingWorkItems);
        }

        /// <summary>
        /// Low-rate same-volume <see cref="FileMode.CreateNew"/> sidecar (test-only). Runs in a
        /// separate directory under the bench root while E2E publish continues. Used when elevated
        /// ETW DiskIO/FileIO capture is unavailable; cannot alone establish root cause.
        /// </summary>
        private sealed class SameVolumeCreateNewSidecar
        {
            private const int EventCap = 4096;
            private readonly string _root;
            private readonly double _opsPerSec;
            private readonly long _originTicks;
            private readonly string _runId;
            private readonly ConcurrentBag<double> _openMs = [];
            private readonly ConcurrentBag<double> _closeMs = [];
            private readonly ConcurrentBag<double> _totalMs = [];
            private readonly ConcurrentBag<SidecarOpEvent> _slowEvents = [];
            private readonly ConcurrentBag<string> _faults = [];
            private readonly ConcurrentBag<string> _cleanupWarnings = [];
            private CancellationTokenSource? _cts;
            private Task? _loop;
            private long _ops;
            private int _stopped;

            public SameVolumeCreateNewSidecar(
                string root,
                double opsPerSec,
                long originTicks,
                string runId)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(root);
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(opsPerSec, 0);
                _root = root;
                _opsPerSec = opsPerSec;
                _originTicks = originTicks;
                _runId = runId;
            }

            public int FaultCount => _faults.Count;

            public void Start()
            {
                _cts = new CancellationTokenSource();
                _loop = Task.Run(() => LoopAsync(_cts.Token));
            }

            public async Task StopAsync()
            {
                if (Interlocked.Exchange(ref _stopped, 1) != 0)
                {
                    return;
                }

                if (_cts is null)
                {
                    return;
                }

                _cts.Cancel();
                if (_loop is not null)
                {
                    try
                    {
                        await _loop.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex)
                    {
                        ExpectedException.Ignore(ex);
                    }
                    catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                    {
                        _faults.Add($"{ex.GetType().Name}:{ex.Message}");
                    }
                }

                _cts.Dispose();
                _cts = null;
                _loop = null;
            }

            private async Task LoopAsync(CancellationToken cancellationToken)
            {
                var interval = TimeSpan.FromSeconds(1d / _opsPerSec);
                var seq = 0L;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var slotStart = Stopwatch.GetTimestamp();
                    try
                    {
                        seq++;
                        var path = Path.Join(
                            _root,
                            $"sidecar-{_runId}-{seq:D8}-{Environment.CurrentManagedThreadId}.tmp");
                        var totalStart = Stopwatch.GetTimestamp();
                        var openStart = totalStart;
                        long openEnd;
                        long closeEnd;
                        using (new FileStream(
                            path,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            bufferSize: 4096,
                            FileOptions.SequentialScan))
                        {
                            openEnd = Stopwatch.GetTimestamp();
                        }

                        closeEnd = Stopwatch.GetTimestamp();
                        var openMs = Ms(openEnd - openStart);
                        var closeMs = Ms(closeEnd - openEnd);
                        var totalMs = Ms(closeEnd - totalStart);
                        _openMs.Add(openMs);
                        _closeMs.Add(closeMs);
                        _totalMs.Add(totalMs);
                        Interlocked.Increment(ref _ops);

                        if ((openMs >= 10d || closeMs >= 10d || totalMs >= 10d)
                            && _slowEvents.Count < EventCap)
                        {
                            _slowEvents.Add(new SidecarOpEvent(
                                openMs,
                                closeMs,
                                totalMs,
                                openStart,
                                openEnd,
                                closeEnd,
                                Environment.CurrentManagedThreadId,
                                DateTime.UtcNow,
                                path));
                        }

                        try
                        {
                            File.Delete(path);
                        }
                        catch (IOException ex)
                        {
                            // Cleanup-only; do not treat as a sidecar operation fault.
                            _cleanupWarnings.Add($"{ex.GetType().Name}:{ex.Message}");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                    {
                        _faults.Add($"{ex.GetType().Name}:{ex.Message}");
                    }

                    var elapsed = TimeSpan.FromSeconds(
                        (Stopwatch.GetTimestamp() - slotStart) / (double)Stopwatch.Frequency);
                    var remaining = interval - elapsed;
                    if (remaining > TimeSpan.Zero)
                    {
                        try
                        {
                            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }
            }

            public string Report(double sustainSeconds)
            {
                var opens = _openMs.ToArray();
                var closes = _closeMs.ToArray();
                var totals = _totalMs.ToArray();
                var n = opens.Length;
                var ops = Interlocked.Read(ref _ops);
                if (n == 0)
                {
                    return
                        $"sidecar ops={ops} n=0 opsPerSecCfg={_opsPerSec:0.###} "
                        + $"root={_root} runId={_runId} faults={_faults.Count} "
                        + $"cleanupWarnings={_cleanupWarnings.Count} "
                        + "note=same_volume_CreateNew_empty_file_close_delete";
                }

                Array.Sort(opens);
                Array.Sort(closes);
                Array.Sort(totals);
                var openGe10 = CountGe(opens, 10);
                var openGe50 = CountGe(opens, 50);
                var openGe100 = CountGe(opens, 100);
                var closeGe10 = CountGe(closes, 10);
                var closeGe50 = CountGe(closes, 50);
                var totalGe10 = CountGe(totals, 10);
                var totalGe50 = CountGe(totals, 50);
                var totalGe100 = CountGe(totals, 100);
                var rate = sustainSeconds > 0 ? ops / sustainSeconds : 0;
                return
                    $"sidecar ops={ops} n={n} opsPerSecCfg={_opsPerSec:0.###} "
                    + $"opsPerSecObs={rate:0.###} runId={_runId} faults={_faults.Count} "
                    + $"cleanupWarnings={_cleanupWarnings.Count} "
                    + $"openMs_p50={Percentile(opens, 0.50):0.000} p95={Percentile(opens, 0.95):0.000} "
                    + $"p99={Percentile(opens, 0.99):0.000} max={opens[^1]:0.000} "
                    + $"open_ge10={openGe10} ge50={openGe50} ge100={openGe100} "
                    + $"openGe50_per1000={(n > 0 ? 1000d * openGe50 / n : 0):0.000} "
                    + $"closeMs_p50={Percentile(closes, 0.50):0.000} p95={Percentile(closes, 0.95):0.000} "
                    + $"max={closes[^1]:0.000} close_ge10={closeGe10} ge50={closeGe50} "
                    + $"totalMs_p50={Percentile(totals, 0.50):0.000} p95={Percentile(totals, 0.95):0.000} "
                    + $"max={totals[^1]:0.000} total_ge10={totalGe10} ge50={totalGe50} ge100={totalGe100} "
                    + $"slowEventsRetained={_slowEvents.Count} cap={EventCap} "
                    + "note=same_volume_CreateNew_empty_file_close_delete_not_root_cause_alone";
            }

            public string CorrelationReport(
                IReadOnlyList<SlowOpEventView> e2eSlow,
                long benchOriginTicks)
            {
                var sidecarSlow = _slowEvents.ToArray();
                Array.Sort(sidecarSlow, static (a, b) => a.OpenStartTicks.CompareTo(b.OpenStartTicks));
                // Retain only ≥50 ms opens for burst windows (slow-op ring already ≥50).
                var e2eOpen = e2eSlow
                    .Where(static e =>
                        e.Line.Contains("stage=open", StringComparison.Ordinal)
                        && e.Line.Contains("ms=", StringComparison.Ordinal))
                    .ToList();

                var windowTicks = (long)(0.060 * Stopwatch.Frequency);
                var sidecarInE2eWindow = 0;
                var e2eWindowsWithSidecar = 0;
                var sidecarAloneGe50 = 0;
                foreach (var s in sidecarSlow)
                {
                    if (s.OpenMs < 50d)
                    {
                        continue;
                    }

                    sidecarAloneGe50++;
                    var hit = false;
                    foreach (var e in e2eOpen)
                    {
                        // Overlap or within 60 ms of either edge.
                        if (IntervalsNear(
                                s.OpenStartTicks,
                                s.OpenEndTicks,
                                e.OpenStartTicks,
                                e.OpenEndTicks,
                                windowTicks))
                        {
                            hit = true;
                            break;
                        }
                    }

                    if (hit)
                    {
                        sidecarInE2eWindow++;
                    }
                }

                foreach (var e in e2eOpen)
                {
                    foreach (var s in sidecarSlow)
                    {
                        if (s.OpenMs < 10d)
                        {
                            continue;
                        }

                        if (IntervalsNear(
                                s.OpenStartTicks,
                                s.OpenEndTicks,
                                e.OpenStartTicks,
                                e.OpenEndTicks,
                                windowTicks))
                        {
                            e2eWindowsWithSidecar++;
                            break;
                        }
                    }
                }

                _ = benchOriginTicks;
                return
                    $"sidecarCorr e2eOpenSlowN={e2eOpen.Count} sidecarSlowRetained={sidecarSlow.Length} "
                    + $"sidecarOpenGe50={sidecarAloneGe50} "
                    + $"sidecarGe50_nearE2eOpen60ms={sidecarInE2eWindow} "
                    + $"e2eOpen_withSidecarGe10_near60ms={e2eWindowsWithSidecar} "
                    + "window=60ms_overlap_or_edge "
                    + "note=shared_volume_coincidence_not_disk_IO_proof";
            }

            public IEnumerable<string> EnumerateSlowEvents(long benchOriginTicks)
            {
                var events = _slowEvents.ToArray();
                Array.Sort(events, static (a, b) => a.OpenStartTicks.CompareTo(b.OpenStartTicks));
                foreach (var e in events)
                {
                    var t0 = (e.OpenStartTicks - benchOriginTicks) / (double)Stopwatch.Frequency;
                    var t1 = (e.OpenEndTicks - benchOriginTicks) / (double)Stopwatch.Frequency;
                    var tClose = (e.CloseEndTicks - benchOriginTicks) / (double)Stopwatch.Frequency;
                    yield return
                        $"sidecarSlow openMs={e.OpenMs:0.000} closeMs={e.CloseMs:0.000} "
                        + $"totalMs={e.TotalMs:0.000} tOpen=[{t0:0.000},{t1:0.000}]s "
                        + $"tCloseEnd={tClose:0.000}s thread={e.ThreadId} utc={e.Utc:O} "
                        + $"file={Path.GetFileName(e.Path)}";
                }
            }

            private static bool IntervalsNear(
                long a0,
                long a1,
                long b0,
                long b1,
                long windowTicks)
            {
                // Overlap
                if (a0 <= b1 && b0 <= a1)
                {
                    return true;
                }

                // Gap between intervals ≤ window
                if (a1 < b0)
                {
                    return b0 - a1 <= windowTicks;
                }

                if (b1 < a0)
                {
                    return a0 - b1 <= windowTicks;
                }

                return false;
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

            private static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;

            private readonly record struct SidecarOpEvent(
                double OpenMs,
                double CloseMs,
                double TotalMs,
                long OpenStartTicks,
                long OpenEndTicks,
                long CloseEndTicks,
                int ThreadId,
                DateTime Utc,
                string Path);
        }

        /// <summary>
        /// Test-only shared token-bucket pacing across TLS client producers. Limits aggregate
        /// TakeThis start rate without changing server admission or queue semantics.
        /// </summary>
        private sealed class AggregateAdmissionPacer
        {
            private readonly long _intervalTicks;
            private long _nextSlotTicks;

            public AggregateAdmissionPacer(double admitsPerSecond)
            {
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(admitsPerSecond, 0);
                _intervalTicks = (long)(Stopwatch.Frequency / admitsPerSecond);
                ArgumentOutOfRangeException.ThrowIfLessThan(_intervalTicks, 1);
            }

            public async Task WaitTurnAsync()
            {
                while (true)
                {
                    var now = Stopwatch.GetTimestamp();
                    var slot = Volatile.Read(ref _nextSlotTicks);
                    var start = Math.Max(now, slot);
                    var next = start + _intervalTicks;
                    if (Interlocked.CompareExchange(ref _nextSlotTicks, next, slot) != slot)
                    {
                        continue;
                    }

                    var delayTicks = start - now;
                    if (delayTicks > 0)
                    {
                        var delay = TimeSpan.FromSeconds(delayTicks / (double)Stopwatch.Frequency);
                        if (delay > TimeSpan.Zero)
                        {
                            await Task.Delay(delay).ConfigureAwait(false);
                        }
                    }

                    return;
                }
            }
        }

        /// <summary>
        /// Absolute slow-event thresholds (typically w=4 local p95) applied across worker cells.
        /// </summary>
        private readonly record struct AbsoluteSlowThresholds(
            double ProcessP95Ms,
            double OpenP95Ms,
            double WriteP95Ms,
            double MoveP95Ms);

        /// <summary>
        /// CPU-only Path rewrite microbench (no spool I/O). Labeled separately from E2E FilesystemNoIndex.
        /// Uses the same <see cref="AcceptedArticlePathProcessor"/> path as production ProcessCore.
        /// </summary>
        private void MeasurePathRewriteCpuMicrobench(int iterations)
        {
            var article = BuildAccepted(42, ArticleBytes);
            var path = new AcceptedArticlePathProcessor("cache01.usenet.ninja", NullLogger.Instance);
            path.EnsureReady();
            // Warm one rewrite so parser/materializer steady-state is represented.
            if (!path.TryRewriteRented(article, out var warm, out _) || warm is null)
            {
                Emit("fspub-path-rewrite-cpu FAILED warm rewrite");
                return;
            }

            AcceptedArticlePathProcessor.ReturnRewriteBuffer(warm);

            var samples = new double[iterations];
            var wall = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++)
            {
                var started = Stopwatch.GetTimestamp();
                if (!path.TryRewriteRented(article, out var rented, out _) || rented is null)
                {
                    Emit($"fspub-path-rewrite-cpu FAILED iteration={i}");
                    return;
                }

                AcceptedArticlePathProcessor.ReturnRewriteBuffer(rented);
                samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }

            wall.Stop();
            Array.Sort(samples);
            var perSec = iterations / Math.Max(wall.Elapsed.TotalSeconds, 0.001);
            Emit(
                $"fspub-path-rewrite-cpu iterations={iterations} wallMs={wall.Elapsed.TotalMilliseconds:0} "
                + $"rewritesPerSec={perSec:0.00} rewriteMs_p50={Percentile(samples, 0.50):0.000} "
                + $"p95={Percentile(samples, 0.95):0.000} p99={Percentile(samples, 0.99):0.000} "
                + "scope=AcceptedArticlePathProcessor.TryRewriteRented_only "
                + "note=no_disk_no_spool_select_no_TLS_not_equivalent_to_E2E_processMs");
        }

        private static List<int> ParsePositiveIntList(string? raw)
        {
            var list = new List<int>();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return list;
            }

            list.AddRange(
                raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(static part => int.TryParse(part, out var value) ? value : 0)
                    .Where(static value => value > 0));

            return list;
        }

        private enum HostMode
        {
            FullIndexed,
            FilesystemNoIndex,
            CompletingSink,
            /// <summary>Test-only CompletingSink that busy-waits a calibrated duration with no FS I/O.</summary>
            CompletingSinkCalibratedDelay,
        }

        private static BenchRoot ResolveBenchRoot()
        {
            var configured = Environment.GetEnvironmentVariable(EnvRoot);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var path = Path.GetFullPath(configured);
                Directory.CreateDirectory(path);
                return new BenchRoot(path, IsDedicated: true);
            }

            // AppContext.BaseDirectory is typically tests/.../bin/x64/Release/net10.0 — five levels up is the repo root.
            var fromRepo = Path.GetFullPath(Path.Join(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                ".artifacts",
                "perf",
                "e2e-workdir"));
            Directory.CreateDirectory(fromRepo);
            return new BenchRoot(fromRepo, IsDedicated: false);
        }

        private static SpoolCapacityWaitSignal CreateSignal()
        {
            var catalog = new SegmentPoolCatalog();
            catalog.Publish([new SegmentPoolDevice("block:e2e", [Path.GetTempPath()])]);
            var monitor = new BlockDeviceMonitorService(
                catalog,
                new FlatCounters(),
                NullLogger<BlockDeviceMonitorService>.Instance,
                TimeSpan.FromHours(1),
                TimeProvider.System,
                freeSpace: null);
            return new SpoolCapacityWaitSignal(monitor);
        }

        private static void InvokeSample(BlockDeviceMonitorService monitor) =>
            monitor.GetType()
                .GetMethod("Sample", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(monitor, null);

        private static async Task WaitAsync(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!condition())
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException("condition not met within " + timeout);
                }

                await Task.Delay(5).ConfigureAwait(false);
            }
        }

        private static WireArticle BuildWireArticle(int index, int bodyBytes)
        {
            var messageId = "<e2e-" + index + "-" + Guid.NewGuid().ToString("N") + "@seg.test>";
            var header = Encoding.ASCII.GetBytes(
                "Path: peer.example\r\nDate: Fri, 23 Aug 2024 07:30:10 +0000\r\nMessage-ID: "
                + messageId
                + "\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\nSubject: e2e\r\n\r\n");
            var body = new byte[Math.Max(80, bodyBytes - header.Length)];
            var line = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\r\n"u8;
            for (var offset = 0; offset < body.Length; offset += line.Length)
            {
                var count = Math.Min(line.Length, body.Length - offset);
                line.Slice(0, count).CopyTo(body.AsSpan(offset, count));
            }

            body[0] = (byte)(index & 0xFF);
            // Multiline terminator is recognized only at a line start; the logical article must end with LF.
            if (body.Length >= 2
                && (body[^2] != (byte)'\r' || body[^1] != (byte)'\n'))
            {
                body[^2] = (byte)'\r';
                body[^1] = (byte)'\n';
            }

            var logical = new byte[header.Length + body.Length];
            header.CopyTo(logical, 0);
            body.CopyTo(logical, header.Length);
            var created = ArticleRecordFactory.TryCreate(
                new NntpArticleParser("cache01.usenet.ninja"),
                logical,
                ArticlePathMode.Normalize);
            Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
            var command = Encoding.ASCII.GetBytes("TAKETHIS " + messageId + "\r\n");
            var stuffed = DotStuff(logical);
            var wireBytes = command.Length + stuffed.Length + 3; // .\r\n
            return new WireArticle(created.Record.ArtId, messageId, logical, stuffed, command, wireBytes);
        }

        private static AcceptedArticle BuildAccepted(int index, int bodyBytes)
        {
            var built = BuildWireArticle(index, bodyBytes);
            var created = ArticleRecordFactory.TryCreate(
                new NntpArticleParser("cache01.usenet.ninja"),
                built.Logical,
                ArticlePathMode.Normalize);
            Assert.True(created.IsAccepted);
            return new AcceptedArticle(
                created.Record.ArtId,
                created.Record.ArtHash,
                created.Record.ArtSize,
                created.Record.ArtData);
        }

        private static byte[] DotStuff(ReadOnlySpan<byte> article)
        {
            var length = article.Length;
            var atLineStart = true;
            for (var i = 0; i < article.Length; i++)
            {
                if (atLineStart && article[i] == (byte)'.')
                {
                    length++;
                }

                atLineStart = article[i] == (byte)'\n';
            }

            var stuffed = new byte[length];
            var w = 0;
            atLineStart = true;
            for (var i = 0; i < article.Length; i++)
            {
                if (atLineStart && article[i] == (byte)'.')
                {
                    stuffed[w++] = (byte)'.';
                }

                stuffed[w++] = article[i];
                atLineStart = article[i] == (byte)'\n';
            }

            return stuffed;
        }

        private readonly record struct WireArticle(
            ArticleId ArtId,
            string MessageId,
            byte[] Logical,
            byte[] Stuffed,
            byte[] Command,
            long WireBytes);

        /// <summary>
        /// Client-side TAKETHIS timing. <see cref="WriteMs"/> is local SSL write of command+body+terminator.
        /// <see cref="Wait239Ms"/> is wait for the status line (server receive+admit+response, plus network).
        /// </summary>
        private readonly record struct TakeThisTiming(int Code, double WriteMs, double Wait239Ms);

        private sealed class SequentialTlsClient : IAsyncDisposable
        {
            private readonly TcpClient _tcp;
            private readonly SslStream _ssl;
            private readonly List<byte> _pending = [];
            private readonly byte[] _readBuffer = new byte[64 * 1024];

            private SequentialTlsClient(TcpClient tcp, SslStream ssl)
            {
                _tcp = tcp;
                _ssl = ssl;
            }

            public static async Task<SequentialTlsClient> ConnectAsync(int port)
            {
                var tcp = new TcpClient { NoDelay = true };
                try
                {
                    tcp.SendBufferSize = 1 << 20;
                    tcp.ReceiveBufferSize = 1 << 20;
                }
                catch (SocketException ex)
                {
                    ExpectedException.Ignore(ex);
                }

                await tcp.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions
                        {
                            TargetHost = "localhost",
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                var client = new SequentialTlsClient(tcp, ssl);
                Assert.Equal(200, await client.ReadStatusAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false));
                return client;
            }

            public async Task<int> TakeThisAsync(WireArticle article)
            {
                var timing = await TakeThisTimedAsync(article).ConfigureAwait(false);
                return timing.Code;
            }

            public async Task<TakeThisTiming> TakeThisTimedAsync(WireArticle article)
            {
                var writeStarted = Stopwatch.GetTimestamp();
                await _ssl.WriteAsync(article.Command).ConfigureAwait(false);
                const int chunk = 64 * 1024;
                for (var offset = 0; offset < article.Stuffed.Length; offset += chunk)
                {
                    var length = Math.Min(chunk, article.Stuffed.Length - offset);
                    await _ssl.WriteAsync(article.Stuffed.AsMemory(offset, length)).ConfigureAwait(false);
                }

                await _ssl.WriteAsync(".\r\n"u8.ToArray()).ConfigureAwait(false);
                var writeMs = Stopwatch.GetElapsedTime(writeStarted).TotalMilliseconds;
                var waitStarted = Stopwatch.GetTimestamp();
                var code = await ReadStatusAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
                var waitMs = Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds;
                return new TakeThisTiming(code, writeMs, waitMs);
            }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    await _ssl.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    ExpectedException.Ignore(ex);
                }

                _tcp.Dispose();
            }

            private async Task<int> ReadStatusAsync(TimeSpan timeout)
            {
                using var cts = new CancellationTokenSource(timeout);
                while (true)
                {
                    for (var i = 0; i < _pending.Count - 1; i++)
                    {
                        if (_pending[i] == (byte)'\r' && _pending[i + 1] == (byte)'\n')
                        {
                            var line = Encoding.ASCII.GetString(_pending.GetRange(0, i).ToArray());
                            _pending.RemoveRange(0, i + 2);
                            return ((line[0] - '0') * 100) + ((line[1] - '0') * 10) + (line[2] - '0');
                        }
                    }

                    var read = await _ssl.ReadAsync(_readBuffer, cts.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    for (var i = 0; i < read; i++)
                    {
                        _pending.Add(_readBuffer[i]);
                    }
                }
            }
        }

        private static double Median(IReadOnlyList<double> sorted) => Percentile(sorted, 0.50);

        private static double Percentile(IReadOnlyList<double> sorted, double percentile)
        {
            if (sorted.Count == 0)
            {
                return 0;
            }

            var index = (int)Math.Ceiling(sorted.Count * percentile) - 1;
            if (index < 0)
            {
                index = 0;
            }

            return sorted[Math.Min(index, sorted.Count - 1)];
        }

        private readonly record struct BenchRoot(string Path, bool IsDedicated);

        private readonly record struct WakeSample(
            double NotifyToResumeMs,
            double ResumeToCompleteMs,
            double NotifyToCompleteMs,
            double CapacityWaitMs);

        private readonly record struct SampleWake(
            double EligibleToNotifyMs,
            double NotifyToResumeMs,
            double NotifyToCompleteMs);

        private readonly record struct EvictionWake(
            double FreedToNotifyMs,
            double NotifyToResumeMs,
            double NotifyToCompleteMs);

        private readonly record struct CriticalSample(
            AcceptedArticleProcessResult Result,
            long Deferred,
            double ElapsedMs);

        private readonly record struct MultiSpoolSample(
            long BlockedDeferred,
            long HealthyStored,
            double NotifyToCompleteMs);

        private sealed class EndToEndHost : IAsyncDisposable
        {
            private readonly StorageNntpListenerService _listener;
            private readonly AcceptedArticleWorker _worker;
            private readonly BlockDeviceMonitorService? _monitor;
            private readonly ArticleIndexService? _index;
            private readonly TimingSpoolFiles? _timingFiles;
            private Action<ArticleId, long>? _onCompleted;

            private EndToEndHost(
                int port,
                AcceptedArticleQueue queue,
                StorageNntpListenerService listener,
                AcceptedArticleWorker worker,
                BlockDeviceMonitorService? monitor,
                ArticleIndexService? index,
                SpoolPersistenceCounters counters,
                ObservingProcessor observing,
                TimingSpoolFiles? timingFiles,
                PublishTailCorrelator? tailCorrelator)
            {
                Port = port;
                Queue = queue;
                _listener = listener;
                _worker = worker;
                _monitor = monitor;
                _index = index;
                Counters = counters;
                Observing = observing;
                _timingFiles = timingFiles;
                _tailCorrelator = tailCorrelator;
            }

            public int Port { get; }

            public AcceptedArticleQueue Queue { get; }

            public SpoolPersistenceCounters Counters { get; }

            public ArticleIndexService? Index => _index;

            public ConcurrentBag<ArticleId> CompletedIds => Observing.CompletedIds;

            public int ObservingCompleted => Observing.CompletedCount;

            public ConcurrentBag<double> ProcessLatencyMs => Observing.ProcessLatencyMs;

            public string PublishTimingReport => _timingFiles?.Report() ?? "publishTiming=n/a";

            public string PublishExtremeStallReport(double sustainSeconds) =>
                _timingFiles?.ExtremeStallReport(sustainSeconds) ?? "extremeStalls=n/a";

            public string SlowOpEventReport(long benchOriginTicks) =>
                _timingFiles?.SlowOpEventReport(benchOriginTicks) ?? "slowOpEvents=n/a";

            public IReadOnlyList<SlowOpEventView> EnumerateSlowOpEvents(long benchOriginTicks) =>
                _timingFiles?.EnumerateSlowOpEvents(benchOriginTicks) ?? Array.Empty<SlowOpEventView>();

            public void SetBenchOrigin(long originTicks, DateTime originUtc) =>
                _timingFiles?.SetBenchOrigin(originTicks, originUtc);

            public string PublishResidualReport(double processMsP50) =>
                _timingFiles?.ResidualReport(processMsP50) ?? "publishResidual=n/a";

            public string ConcurrencyReport =>
                Observing.ConcurrencyReport
                + " "
                + (_timingFiles?.ConcurrencyReport() ?? "publishConcurrency=n/a");

            public string TailCorrelationReport =>
                _tailCorrelator?.Report() ?? "tailCorrelation=n/a";

            public bool TryGetLocalSlowThresholds(out AbsoluteSlowThresholds thresholds)
            {
                if (_tailCorrelator is null || !_tailCorrelator.TryGetLocalPercentileThresholds(out thresholds))
                {
                    thresholds = default;
                    return false;
                }

                return true;
            }

            public string TailAbsoluteEventOrderReport(AbsoluteSlowThresholds absolute) =>
                _tailCorrelator?.AbsoluteEventOrderReport(absolute)
                ?? "absoluteSlowAnalysis=n/a";

            private ObservingProcessor Observing { get; }

            private readonly PublishTailCorrelator? _tailCorrelator;

            public void AttachCompletionObserver(Action<ArticleId, long> onCompleted) => _onCompleted = onCompleted;

            public void AttachTailQueueSnapshot(Func<(long Accounted, int Queued, long InFlight)> snapshot) =>
                Observing.AttachTailQueueSnapshot(snapshot);

            public void AttachTailAdmitStartLookup(Func<ArticleId, long?> lookup) =>
                Observing.AttachTailAdmitStartLookup(lookup);

            public Task StopWorkerAsync(CancellationToken cancellationToken) => _worker.StopAsync(cancellationToken);

            public static Task<EndToEndHost> StartAsync(string spoolRoot, string dbDir, int workers) =>
                StartAsync(spoolRoot, dbDir, workers, HostMode.FullIndexed, DefaultFanoutMaxQueuedBytes);

            public static Task<EndToEndHost> StartAsync(string spoolRoot, string dbDir, int workers, HostMode mode) =>
                StartAsync(spoolRoot, dbDir, workers, mode, DefaultFanoutMaxQueuedBytes);

            public static async Task<EndToEndHost> StartAsync(
                string spoolRoot,
                string dbDir,
                int workers,
                HostMode mode,
                long maxQueuedBytes,
                TimeSpan? sinkDelay = null,
                bool enableTailCorrelation = false)
            {
                var spool = Path.TrimEndingDirectorySeparator(Path.GetFullPath(spoolRoot));
                var catalog = ArticleIndexTestSupport.Catalog(spool);
                var probe = new GenerousProbe();
                // SampleFreshness is 5s; without a fresh free-space sample every spool is ineligible.
                var monitor = new BlockDeviceMonitorService(
                    catalog,
                    new FlatCounters(),
                    NullLogger<BlockDeviceMonitorService>.Instance,
                    TimeSpan.FromMilliseconds(250),
                    TimeProvider.System,
                    probe);
                InvokeSample(monitor);

                ArticleIndexService? index = null;
                if (mode == HostMode.FullIndexed)
                {
                    index = await ArticleIndexTestSupport.StartAsync(dbDir, catalog, ArticleIndexTestSupport.FastSettings())
                        .ConfigureAwait(false);
                }

                var counters = new SpoolPersistenceCounters();
                var signal = new SpoolCapacityWaitSignal(monitor);
                TimingSpoolFiles? timingFiles = null;
                PublishTailCorrelator? tailCorrelator = enableTailCorrelation && mode == HostMode.FilesystemNoIndex
                    ? new PublishTailCorrelator(capacity: 8192)
                    : null;
                IAcceptedArticleProcessor inner;
                if (mode == HostMode.CompletingSink)
                {
                    inner = new CompletingSinkProcessor();
                }
                else if (mode == HostMode.CompletingSinkCalibratedDelay)
                {
                    var delay = sinkDelay ?? TimeSpan.FromMilliseconds(DefaultCalibratedDelayMs);
                    if (delay <= TimeSpan.Zero)
                    {
                        throw new ArgumentOutOfRangeException(nameof(sinkDelay), delay, "Calibrated sink delay must be positive.");
                    }

                    inner = new CompletingSinkCalibratedDelayProcessor(delay);
                }
                else
                {
                    timingFiles = new TimingSpoolFiles(new FileSpoolArticleFiles());
                    inner = new SpoolArticlePersistenceProcessor(
                        "cache01.usenet.ninja",
                        catalog,
                        monitor,
                        new SpoolWriteGate(),
                        new SpoolArticleIdentityGate(),
                        new SpoolHealth(TimeProvider.System),
                        new SpoolPressure(signal),
                        new SpoolArticlePublisher(timingFiles),
                        counters,
                        new StorageServerOptions
                        {
                            DbDir = dbDir,
                            SpoolPersistence = new SpoolPersistenceOptions
                            {
                                MaxInFlightPerSpool = Math.Max(8, workers * 2),
                            },
                        },
                        NullLogger.Instance,
                        TimeProvider.System,
                        index);
                }

                var observing = new ObservingProcessor(inner, tailCorrelator);
                // Bound the queue so TLS admission back-pressures instead of building multi-GB backlog on temp volume.
                // Fan-out may override via VECTORNNTPD_STORAGE_CLIENT_FANOUT_MAX_QUEUED_BYTES (test-only; default 64 MiB).
                var queue = new AcceptedArticleQueue(maxQueuedBytes, TimeProvider.System);
                var worker = new AcceptedArticleWorker(
                    queue,
                    observing,
                    NullLogger<AcceptedArticleWorker>.Instance,
                    new SpoolPersistenceOptions
                    {
                        MinWorkers = workers,
                        MaxWorkers = workers,
                        ControlIntervalMilliseconds = 100,
                    },
                    new SpoolPressure(signal),
                    counters,
                    TimeProvider.System,
                    signal);

                var port = StorageServerTestOptions.GetFreeTcpPort();
                var identity = StorageServerTestOptions.CreateValid();
                identity.BindAddress = ["127.0.0.1"];
                identity.BindPortTls = port;
                var acme = StorageServerTestOptions.CreateValidAcme(identity);
                acme.BindAddress = ["127.0.0.1"];
                acme.BindPortTls = port;
                var runtime = StorageServerRuntimeOptionsFactory.Create(identity, acme);
                var readiness = new AcmeCertificateReadiness();
                readiness.MarkReady();
                var listener = new StorageNntpListenerService(
                    runtime,
                    TestListenerCertificates.CreatePublishedProvider(),
                    readiness,
                    NullLogger<StorageNntpListenerService>.Instance,
                    acceptedArticles: queue,
                    assemblyAdmission: new StoreAssemblyAdmission(64));

                await monitor.StartAsync(CancellationToken.None).ConfigureAwait(false);
                await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
                await listener.StartAsync(CancellationToken.None).ConfigureAwait(false);

                var host = new EndToEndHost(
                    port,
                    queue,
                    listener,
                    worker,
                    monitor,
                    index,
                    counters,
                    observing,
                    timingFiles,
                    tailCorrelator);
                observing.SetObserver((id, ticks) => host._onCompleted?.Invoke(id, ticks));
                return host;
            }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    await _listener.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                {
                    // Listener dispose is best-effort during bench teardown.
                    ExpectedException.Ignore(ex);
                }

                try
                {
                    await _worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // Drain failure already recorded by the scenario.
                }

                if (_monitor is not null)
                {
                    await _monitor.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }

                if (_index is not null)
                {
                    await ArticleIndexTestSupport.StopAsync(_index).ConfigureAwait(false);
                }
            }
        }

        private sealed class ObservingProcessor : IAcceptedArticleProcessor
        {
            private readonly IAcceptedArticleProcessor _inner;
            private readonly PublishTailCorrelator? _tailCorrelator;
            private Action<ArticleId, long> _observer;
            private Func<(long Accounted, int Queued, long InFlight)>? _queueSnapshot;
            private Func<ArticleId, long?>? _admitStartLookup;
            private int _completed;
            private int _processInFlight;
            private int _processMaxInFlight;

            public ObservingProcessor(IAcceptedArticleProcessor inner, PublishTailCorrelator? tailCorrelator = null)
            {
                _inner = inner;
                _tailCorrelator = tailCorrelator;
                _observer = static (_, _) => { };
            }

            public ConcurrentBag<ArticleId> CompletedIds { get; } = [];

            public ConcurrentBag<double> ProcessLatencyMs { get; } = [];

            public int CompletedCount => Volatile.Read(ref _completed);

            public string ConcurrencyReport =>
                $"processInFlightMax={Volatile.Read(ref _processMaxInFlight)}";

            public void SetObserver(Action<ArticleId, long> observer) => _observer = observer;

            public void AttachTailQueueSnapshot(Func<(long Accounted, int Queued, long InFlight)> snapshot) =>
                _queueSnapshot = snapshot;

            public void AttachTailAdmitStartLookup(Func<ArticleId, long?> lookup) =>
                _admitStartLookup = lookup;

            public void EnsureReady() => _inner.EnsureReady();

            public async ValueTask<AcceptedArticleProcessResult> ProcessAsync(
                AcceptedArticle article,
                CancellationToken cancellationToken)
            {
                var inFlight = Interlocked.Increment(ref _processInFlight);
                UpdateMax(ref _processMaxInFlight, inFlight);
                var started = Stopwatch.GetTimestamp();
                var stages = _tailCorrelator is not null
                    ? PublishStageAccumulator.Begin(_queueSnapshot)
                    : null;

                try
                {
                    var result = await _inner.ProcessAsync(article, cancellationToken).ConfigureAwait(false);
                    var processMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    ProcessLatencyMs.Add(processMs);
                    if (result == AcceptedArticleProcessResult.Completed)
                    {
                        if (stages is not null)
                        {
                            stages.SnapProcessEnd();
                            var admitStart = _admitStartLookup?.Invoke(article.ArtId);
                            var e2eMs = admitStart is long ticks
                                ? Stopwatch.GetElapsedTime(ticks).TotalMilliseconds
                                : double.NaN;
                            _tailCorrelator!.TryAdd(article.ArtId, processMs, stages, e2eMs);
                        }

                        Interlocked.Increment(ref _completed);
                        CompletedIds.Add(article.ArtId);
                        _observer(article.ArtId, Stopwatch.GetTimestamp());
                    }

                    return result;
                }
                finally
                {
                    PublishStageAccumulator.Clear();
                    Interlocked.Decrement(ref _processInFlight);
                }
            }

            private static void UpdateMax(ref int target, int candidate)
            {
                var current = Volatile.Read(ref target);
                while (candidate > current)
                {
                    var previous = Interlocked.CompareExchange(ref target, candidate, current);
                    if (previous == current)
                    {
                        return;
                    }

                    current = previous;
                }
            }
        }

        private sealed class CompletingSinkProcessor : IAcceptedArticleProcessor
        {
            public void EnsureReady()
            {
            }

            public ValueTask<AcceptedArticleProcessResult> ProcessAsync(
                AcceptedArticle article,
                CancellationToken cancellationToken) =>
                ValueTask.FromResult(AcceptedArticleProcessResult.Completed);
        }

        /// <summary>
        /// Test-only CompletingSink that holds the worker for <see cref="_delay"/> using a Stopwatch
        /// busy-wait (no filesystem I/O). Production publish Write/Move are synchronous on the worker;
        /// <see cref="Task.Delay"/> would release the worker and mis-model that occupancy. Windows
        /// timer coarseness also makes sub-2 ms <see cref="Task.Delay"/> unreliable. This is not
        /// equivalent to disk I/O even when elapsed times match.
        /// </summary>
        private sealed class CompletingSinkCalibratedDelayProcessor : IAcceptedArticleProcessor
        {
            private readonly long _delayTicks;

            public CompletingSinkCalibratedDelayProcessor(TimeSpan delay)
            {
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero);
                _delayTicks = (long)(delay.TotalSeconds * Stopwatch.Frequency);
            }

            public void EnsureReady()
            {
            }

            public ValueTask<AcceptedArticleProcessResult> ProcessAsync(
                AcceptedArticle article,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var deadline = Stopwatch.GetTimestamp() + _delayTicks;
                while (Stopwatch.GetTimestamp() < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return ValueTask.FromResult(AcceptedArticleProcessResult.Completed);
            }
        }

        /// <summary>
        /// Test-only filesystem adapter timings for publication substages.
        /// WriteAll is instrumented by mirroring <see cref="FileSpoolArticleFiles"/> WriteNew
        /// (CreateNew + Write + Flush(false)) so open/write/flush can be separated without production hooks.
        /// Timings are application-level wall time, not device service time. Stages are exclusive of each other
        /// within WriteAll; CreateDirectory/Exists/Move are exclusive of WriteAll.
        /// </summary>
        private sealed class TimingSpoolFiles : ISpoolArticleFiles
        {
            private readonly ISpoolArticleFiles _inner;
            private long _writeTotalTicks;
            private long _openTicks;
            private long _writeBytesTicks;
            private long _flushTicks;
            private long _moveTicks;
            private long _directoryTicks;
            private long _existsTicks;
            private int _writeCalls;
            private int _moveCalls;
            private int _directoryCalls;
            private int _existsCalls;
            private int _flushCalls;
            private int _writeInFlight;
            private int _writeMaxInFlight;
            private int _moveInFlight;
            private int _moveMaxInFlight;
            private readonly ConcurrentBag<double> _writeTotalMs = [];
            private readonly ConcurrentBag<double> _openMs = [];
            private readonly ConcurrentBag<double> _writeBytesMs = [];
            private readonly ConcurrentBag<double> _flushMs = [];
            private readonly ConcurrentBag<double> _moveMs = [];
            private readonly ConcurrentBag<SlowOpEvent> _slowOps = [];
            private const int SlowOpCap = 512;
            private long _benchOriginTicks;
            private DateTime _benchOriginUtc;

            public TimingSpoolFiles(ISpoolArticleFiles inner) => _inner = inner;

            public void SetBenchOrigin(long originTicks, DateTime originUtc)
            {
                _benchOriginTicks = originTicks;
                _benchOriginUtc = originUtc;
            }

            private void RecordSlowOp(
                string stage,
                double ms,
                long startTicks,
                long endTicks,
                long accBefore,
                long accAfter,
                int queuedBefore,
                int queuedAfter)
            {
                if (ms < 50 || _slowOps.Count >= SlowOpCap)
                {
                    return;
                }

                _slowOps.Add(new SlowOpEvent(
                    stage,
                    ms,
                    startTicks,
                    endTicks,
                    Environment.CurrentManagedThreadId,
                    accBefore,
                    accAfter,
                    queuedBefore,
                    queuedAfter,
                    DateTime.UtcNow));
            }

            public string SlowOpEventReport(long benchOriginTicks)
            {
                var events = _slowOps.ToArray();
                if (events.Length == 0)
                {
                    return $"slowOpEvents n=0 cap={SlowOpCap}";
                }

                Array.Sort(events, static (a, b) => a.StartTicks.CompareTo(b.StartTicks));
                var openN = 0;
                var moveN = 0;
                var clusters = 0;
                var windowTicks = (long)(0.010 * Stopwatch.Frequency);
                long? prevOpenEnd = null;
                foreach (var e in events)
                {
                    if (e.Stage == "open")
                    {
                        openN++;
                        if (prevOpenEnd is long prev && e.StartTicks - prev <= windowTicks)
                        {
                            clusters++;
                        }

                        prevOpenEnd = e.EndTicks;
                    }
                    else if (e.Stage == "move")
                    {
                        moveN++;
                    }
                }

                var first = events[0];
                var last = events[^1];
                return
                    $"slowOpEvents n={events.Length} cap={SlowOpCap} openGe50={openN} moveGe50={moveN} "
                    + $"openClusterPairs_le10ms={clusters} "
                    + $"firstT={TicksToSec(first.StartTicks, benchOriginTicks):0.000}s stage={first.Stage} ms={first.Ms:0.000} "
                    + $"lastT={TicksToSec(last.StartTicks, benchOriginTicks):0.000}s stage={last.Stage} ms={last.Ms:0.000} "
                    + "clock=Stopwatch_monotonic_plus_UtcNow_at_record";
            }

            public IReadOnlyList<SlowOpEventView> EnumerateSlowOpEvents(long benchOriginTicks)
            {
                var events = _slowOps.ToArray();
                Array.Sort(events, static (a, b) => a.StartTicks.CompareTo(b.StartTicks));
                var list = new List<SlowOpEventView>(events.Length);
                foreach (var e in events)
                {
                    var t0 = TicksToSec(e.StartTicks, benchOriginTicks);
                    var t1 = TicksToSec(e.EndTicks, benchOriginTicks);
                    list.Add(new SlowOpEventView(
                        e.StartTicks,
                        e.EndTicks,
                        $"slowOp stage={e.Stage} ms={e.Ms:0.000} tStart={t0:0.000}s tEnd={t1:0.000}s "
                        + $"thread={e.ThreadId} accBefore={e.AccBefore} accAfter={e.AccAfter} "
                        + $"accDelta={e.AccAfter - e.AccBefore} queuedBefore={e.QueuedBefore} queuedAfter={e.QueuedAfter} "
                        + $"utc={e.Utc:O}"));
                }

                return list;
            }

            private static double TicksToSec(long ticks, long origin) =>
                (ticks - origin) / (double)Stopwatch.Frequency;

            public string ConcurrencyReport() =>
                $"publishWriteInFlightMax={Volatile.Read(ref _writeMaxInFlight)} "
                + $"publishMoveInFlightMax={Volatile.Read(ref _moveMaxInFlight)} "
                + "concurrencyNote=test_only_atomics_around_WriteAll_MoveNew";

            /// <summary>
            /// Counts Open/Move stalls from the full ConcurrentBag of every call (not the correlator ring).
            /// Rates use observed open/move call counts as the denominator.
            /// </summary>
            public string ExtremeStallReport(double sustainSeconds)
            {
                var opens = _openMs.ToArray();
                var moves = _moveMs.ToArray();
                var openN = opens.Length;
                var moveN = moves.Length;
                var openGe10 = 0;
                var openGe50 = 0;
                var openGe100 = 0;
                var moveGe10 = 0;
                var moveGe50 = 0;
                var moveGe100 = 0;
                double openMax = 0;
                double moveMax = 0;
                for (var i = 0; i < openN; i++)
                {
                    var ms = opens[i];
                    openMax = Math.Max(openMax, ms);
                    if (ms >= 10)
                    {
                        openGe10++;
                    }

                    if (ms >= 50)
                    {
                        openGe50++;
                    }

                    if (ms >= 100)
                    {
                        openGe100++;
                    }
                }

                for (var i = 0; i < moveN; i++)
                {
                    var ms = moves[i];
                    moveMax = Math.Max(moveMax, ms);
                    if (ms >= 10)
                    {
                        moveGe10++;
                    }

                    if (ms >= 50)
                    {
                        moveGe50++;
                    }

                    if (ms >= 100)
                    {
                        moveGe100++;
                    }
                }

                Array.Sort(opens);
                Array.Sort(moves);
                var minutes = Math.Max(sustainSeconds / 60d, 1e-9);
                return
                    $"extremeStalls openObservedN={openN} "
                    + $"open_p50={(openN > 0 ? Percentile(opens, 0.50) : 0):0.000} "
                    + $"p95={(openN > 0 ? Percentile(opens, 0.95) : 0):0.000} "
                    + $"p99={(openN > 0 ? Percentile(opens, 0.99) : 0):0.000} "
                    + $"max={openMax:0.000} "
                    + $"open_ge10ms={openGe10} ge50ms={openGe50} ge100ms={openGe100} "
                    + $"openStallPer1000_ge50={(openN > 0 ? 1000d * openGe50 / openN : 0):0.000} "
                    + $"openStallPerMin_ge50={openGe50 / minutes:0.000} "
                    + $"moveObservedN={moveN} "
                    + $"move_p50={(moveN > 0 ? Percentile(moves, 0.50) : 0):0.000} "
                    + $"p95={(moveN > 0 ? Percentile(moves, 0.95) : 0):0.000} "
                    + $"p99={(moveN > 0 ? Percentile(moves, 0.99) : 0):0.000} "
                    + $"max={moveMax:0.000} "
                    + $"move_ge10ms={moveGe10} ge50ms={moveGe50} ge100ms={moveGe100} "
                    + $"moveStallPer1000_ge50={(moveN > 0 ? 1000d * moveGe50 / moveN : 0):0.000} "
                    + "denom=full_TimingSpoolFiles_bags_not_correlator_ring";
            }

            public string Report()
            {
                var writes = Math.Max(1, _writeCalls);
                var moves = Math.Max(1, _moveCalls);
                var exists = Math.Max(1, _existsCalls);
                var dirs = Math.Max(1, _directoryCalls);
                var writeArr = _writeTotalMs.ToArray();
                var openArr = _openMs.ToArray();
                var bytesArr = _writeBytesMs.ToArray();
                var flushArr = _flushMs.ToArray();
                var moveArr = _moveMs.ToArray();
                Array.Sort(writeArr);
                Array.Sort(openArr);
                Array.Sort(bytesArr);
                Array.Sort(flushArr);
                Array.Sort(moveArr);
                return
                    $"publishWriteMs_avg={Ms(_writeTotalTicks) / writes:0.000} "
                    + $"publishOpenMs_avg={Ms(_openTicks) / writes:0.000} "
                    + $"publishWriteBytesMs_avg={Ms(_writeBytesTicks) / writes:0.000} "
                    + $"publishFlushMs_avg={Ms(_flushTicks) / writes:0.000} flushToDisk=false flushCalls={_flushCalls} "
                    + $"publishMoveMs_avg={Ms(_moveTicks) / moves:0.000} "
                    + $"publishDirectoryMs_avg={Ms(_directoryTicks) / dirs:0.000} directoryCalls={_directoryCalls} "
                    + $"publishExistsMs_avg={Ms(_existsTicks) / exists:0.000} existsCalls={_existsCalls} "
                    + $"publishWriteCalls={_writeCalls} publishMoveCalls={_moveCalls} "
                    + $"publishWriteMs_p50={Percentile(writeArr, 0.50):0.000} p95={Percentile(writeArr, 0.95):0.000} p99={Percentile(writeArr, 0.99):0.000} "
                    + $"publishOpenMs_p50={Percentile(openArr, 0.50):0.000} p95={Percentile(openArr, 0.95):0.000} "
                    + $"publishWriteBytesMs_p50={Percentile(bytesArr, 0.50):0.000} p95={Percentile(bytesArr, 0.95):0.000} "
                    + $"publishFlushMs_p50={Percentile(flushArr, 0.50):0.000} p95={Percentile(flushArr, 0.95):0.000} "
                    + $"publishMoveMs_p50={Percentile(moveArr, 0.50):0.000} p95={Percentile(moveArr, 0.95):0.000} p99={Percentile(moveArr, 0.99):0.000} "
                    + "timingScope=ISpoolArticleFiles_adapter_exclusive_stages";
            }

            /// <summary>
            /// Estimates non-FS process time as process p50 minus mean exclusive FS adapter stages.
            /// Residual includes Path rewrite, identity gate, spool selection, path string work, and wait/contention
            /// not captured by adapter timers. Not a causal attribution by itself.
            /// </summary>
            public string ResidualReport(double processMsP50)
            {
                var writes = Math.Max(1, _writeCalls);
                var moves = Math.Max(1, _moveCalls);
                var exists = Math.Max(1, _existsCalls);
                var dirs = Math.Max(1, _directoryCalls);
                var fsSum =
                    (Ms(_directoryTicks) / dirs)
                    + (Ms(_existsTicks) / exists)
                    + (Ms(_writeTotalTicks) / writes)
                    + (Ms(_moveTicks) / moves);
                var residual = processMsP50 - fsSum;
                return
                    $"fsStagesSumAvgMs={fsSum:0.000} processMs_p50={processMsP50:0.000} "
                    + $"nonFsProcessMs_est={residual:0.000} "
                    + "residualIncludes=path_rewrite_identity_spool_select_path_strings_unaccounted_wait "
                    + "note=avgs_not_same_article_as_process_p50_concurrent_workers";
            }

            public bool Exists(string path)
            {
                var start = Stopwatch.GetTimestamp();
                var result = _inner.Exists(path);
                var elapsed = Stopwatch.GetTimestamp() - start;
                Interlocked.Add(ref _existsTicks, elapsed);
                Interlocked.Increment(ref _existsCalls);
                PublishStageAccumulator.Current?.AddExists(Ms(elapsed));
                return result;
            }

            public long Length(string path) => _inner.Length(path);

            public bool ContentsEqual(string path, ReadOnlySpan<byte> content) => _inner.ContentsEqual(path, content);

            public void CreateDirectory(string path)
            {
                var start = Stopwatch.GetTimestamp();
                _inner.CreateDirectory(path);
                var elapsed = Stopwatch.GetTimestamp() - start;
                Interlocked.Add(ref _directoryTicks, elapsed);
                Interlocked.Increment(ref _directoryCalls);
                PublishStageAccumulator.Current?.AddDirectory(Ms(elapsed));
            }

            public void Delete(string path) => _inner.Delete(path);

            public void WriteAll(string path, ReadOnlySpan<byte> content, CancellationToken cancellationToken)
            {
                var inFlight = Interlocked.Increment(ref _writeInFlight);
                UpdateMax(ref _writeMaxInFlight, inFlight);
                try
                {
                    try
                    {
                        WriteNewTimed(path, content, cancellationToken);
                    }
                    catch (DirectoryNotFoundException)
                    {
                        var parent = Path.GetDirectoryName(path);
                        if (parent is null)
                        {
                            throw;
                        }

                        // Mirror FileSpoolArticleFiles retry: recreate parent then rewrite.
                        Directory.CreateDirectory(parent);
                        WriteNewTimed(path, content, cancellationToken);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _writeInFlight);
                }
            }

            public void MoveNew(string source, string destination)
            {
                var inFlight = Interlocked.Increment(ref _moveInFlight);
                UpdateMax(ref _moveMaxInFlight, inFlight);
                try
                {
                    var stages = PublishStageAccumulator.Current;
                    var before = stages?.SnapQueue() ?? default;
                    var start = Stopwatch.GetTimestamp();
                    _inner.MoveNew(source, destination);
                    var end = Stopwatch.GetTimestamp();
                    var elapsed = end - start;
                    Interlocked.Add(ref _moveTicks, elapsed);
                    Interlocked.Increment(ref _moveCalls);
                    var moveMs = Ms(elapsed);
                    _moveMs.Add(moveMs);
                    var after = stages?.SnapQueue() ?? default;
                    stages?.AddMove(moveMs, before, after, start, end);
                    RecordSlowOp(
                        "move",
                        moveMs,
                        start,
                        end,
                        before.Accounted,
                        after.Accounted,
                        before.Queued,
                        after.Queued);
                }
                finally
                {
                    Interlocked.Decrement(ref _moveInFlight);
                }
            }

            private static void UpdateMax(ref int target, int candidate)
            {
                var current = Volatile.Read(ref target);
                while (candidate > current)
                {
                    var previous = Interlocked.CompareExchange(ref target, candidate, current);
                    if (previous == current)
                    {
                        return;
                    }

                    current = previous;
                }
            }

            /// <summary>
            /// Mirrors <see cref="FileSpoolArticleFiles"/> WriteNew with exclusive open/write/flush timers.
            /// Flush uses <c>flushToDisk: false</c> (same as production). No device flush exists in production path.
            /// </summary>
            private void WriteNewTimed(string path, ReadOnlySpan<byte> content, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stages = PublishStageAccumulator.Current;
                var totalStart = Stopwatch.GetTimestamp();
                long openElapsed;
                long writeElapsed;
                long flushElapsed;
                long openEnd;
                long writeStart;
                long writeEnd;
                long flushStart;
                long flushEnd;
                var beforeOpen = stages?.SnapQueue() ?? default;
                var openStart = Stopwatch.GetTimestamp();
                using (var stream = new FileStream(
                           path,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 128 * 1024,
                           FileOptions.SequentialScan))
                {
                    openEnd = Stopwatch.GetTimestamp();
                    openElapsed = openEnd - openStart;
                    var afterOpen = stages?.SnapQueue() ?? default;
                    var beforeWrite = afterOpen;
                    writeStart = Stopwatch.GetTimestamp();
                    stream.Write(content);
                    writeEnd = Stopwatch.GetTimestamp();
                    writeElapsed = writeEnd - writeStart;
                    var afterWrite = stages?.SnapQueue() ?? default;
                    cancellationToken.ThrowIfCancellationRequested();
                    var beforeFlush = afterWrite;
                    flushStart = Stopwatch.GetTimestamp();
                    stream.Flush(flushToDisk: false);
                    flushEnd = Stopwatch.GetTimestamp();
                    flushElapsed = flushEnd - flushStart;
                    var afterFlush = stages?.SnapQueue() ?? default;
                    stages?.AddWrite(
                        Ms(openElapsed),
                        Ms(writeElapsed),
                        Ms(flushElapsed),
                        beforeOpen,
                        afterOpen,
                        beforeWrite,
                        afterWrite,
                        beforeFlush,
                        afterFlush,
                        openStart,
                        openEnd,
                        writeStart,
                        writeEnd,
                        flushStart,
                        flushEnd);
                }

                var totalElapsed = Stopwatch.GetTimestamp() - totalStart;
                Interlocked.Add(ref _openTicks, openElapsed);
                Interlocked.Add(ref _writeBytesTicks, writeElapsed);
                Interlocked.Add(ref _flushTicks, flushElapsed);
                Interlocked.Add(ref _writeTotalTicks, totalElapsed);
                Interlocked.Increment(ref _writeCalls);
                Interlocked.Increment(ref _flushCalls);
                var openMs = Ms(openElapsed);
                var writeMs = Ms(writeElapsed);
                var flushMs = Ms(flushElapsed);
                _openMs.Add(openMs);
                _writeBytesMs.Add(writeMs);
                _flushMs.Add(flushMs);
                _writeTotalMs.Add(Ms(totalElapsed));
                RecordSlowOp(
                    "open",
                    openMs,
                    openStart,
                    openEnd,
                    beforeOpen.Accounted,
                    /* after open snapshot is inside using; re-read from stages if present */
                    stages?.AccAfterOpen ?? beforeOpen.Accounted,
                    beforeOpen.Queued,
                    stages?.QueuedAfterOpen ?? beforeOpen.Queued);
            }

            private static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;

            private readonly record struct SlowOpEvent(
                string Stage,
                double Ms,
                long StartTicks,
                long EndTicks,
                int ThreadId,
                long AccBefore,
                long AccAfter,
                int QueuedBefore,
                int QueuedAfter,
                DateTime Utc);
        }

        /// <summary>Emitted line plus monotonic tick bounds for telemetry alignment.</summary>
        private readonly record struct SlowOpEventView(long OpenStartTicks, long OpenEndTicks, string Line)
        {
            public override string ToString() => Line;
        }

        /// <summary>
        /// Per-ProcessAsync FS stage accumulator via <see cref="AsyncLocal{T}"/> (test-only).
        /// Captures AccountedBytes/Queued/InFlight snapshots immediately before and after instrumented
        /// Open/Write/Flush/Move via a lock-free queue snapshot delegate. Concurrent workers each have
        /// their own accumulator; snapshots are event-time samples, not an exact global queue history.
        /// </summary>
        private sealed class PublishStageAccumulator
        {
            private static readonly AsyncLocal<PublishStageAccumulator?> Ambient = new();

            private readonly Func<(long Accounted, int Queued, long InFlight)>? _queueSnapshot;

            public double OpenMs;
            public double WriteBytesMs;
            public double FlushMs;
            public double MoveMs;
            public double ExistsMs;
            public double DirectoryMs;

            public long ProcessStartTicks;
            public long ProcessEndTicks;
            public long AccProcessStart;
            public long AccProcessEnd;
            public int QueuedProcessStart;
            public int QueuedProcessEnd;
            public long InFlightProcessStart;
            public long InFlightProcessEnd;

            public long AccBeforeOpen;
            public long AccAfterOpen;
            public long AccBeforeWrite;
            public long AccAfterWrite;
            public long AccBeforeFlush;
            public long AccAfterFlush;
            public long AccBeforeMove;
            public long AccAfterMove;
            public int QueuedBeforeOpen;
            public int QueuedAfterOpen;
            public int QueuedBeforeWrite;
            public int QueuedAfterWrite;
            public int QueuedBeforeMove;
            public int QueuedAfterMove;
            public long InFlightBeforeOpen;
            public long InFlightAfterOpen;
            public long InFlightBeforeWrite;
            public long InFlightAfterWrite;
            public long InFlightBeforeMove;
            public long InFlightAfterMove;

            public long OpenStartTicks;
            public long OpenEndTicks;
            public long WriteStartTicks;
            public long WriteEndTicks;
            public long MoveStartTicks;
            public long MoveEndTicks;

            private PublishStageAccumulator(Func<(long Accounted, int Queued, long InFlight)>? queueSnapshot)
            {
                _queueSnapshot = queueSnapshot;
            }

            public static PublishStageAccumulator? Current => Ambient.Value;

            public static PublishStageAccumulator Begin(Func<(long Accounted, int Queued, long InFlight)>? queueSnapshot)
            {
                var created = new PublishStageAccumulator(queueSnapshot);
                created.ProcessStartTicks = Stopwatch.GetTimestamp();
                var snap = created.SnapQueue();
                created.AccProcessStart = snap.Accounted;
                created.QueuedProcessStart = snap.Queued;
                created.InFlightProcessStart = snap.InFlight;
                Ambient.Value = created;
                return created;
            }

            public static void Clear() => Ambient.Value = null;

            public QueueSnap SnapQueue()
            {
                if (_queueSnapshot is null)
                {
                    return default;
                }

                var (accounted, queued, inFlight) = _queueSnapshot();
                return new QueueSnap(accounted, queued, inFlight);
            }

            public void SnapProcessEnd()
            {
                ProcessEndTicks = Stopwatch.GetTimestamp();
                var snap = SnapQueue();
                AccProcessEnd = snap.Accounted;
                QueuedProcessEnd = snap.Queued;
                InFlightProcessEnd = snap.InFlight;
            }

            public void AddWrite(
                double openMs,
                double writeBytesMs,
                double flushMs,
                QueueSnap beforeOpen,
                QueueSnap afterOpen,
                QueueSnap beforeWrite,
                QueueSnap afterWrite,
                QueueSnap beforeFlush,
                QueueSnap afterFlush,
                long openStartTicks,
                long openEndTicks,
                long writeStartTicks,
                long writeEndTicks,
                long flushStartTicks,
                long flushEndTicks)
            {
                _ = flushStartTicks;
                _ = flushEndTicks;
                OpenMs += openMs;
                WriteBytesMs += writeBytesMs;
                FlushMs += flushMs;
                AccBeforeOpen = beforeOpen.Accounted;
                AccAfterOpen = afterOpen.Accounted;
                AccBeforeWrite = beforeWrite.Accounted;
                AccAfterWrite = afterWrite.Accounted;
                AccBeforeFlush = beforeFlush.Accounted;
                AccAfterFlush = afterFlush.Accounted;
                QueuedBeforeOpen = beforeOpen.Queued;
                QueuedAfterOpen = afterOpen.Queued;
                QueuedBeforeWrite = beforeWrite.Queued;
                QueuedAfterWrite = afterWrite.Queued;
                InFlightBeforeOpen = beforeOpen.InFlight;
                InFlightAfterOpen = afterOpen.InFlight;
                InFlightBeforeWrite = beforeWrite.InFlight;
                InFlightAfterWrite = afterWrite.InFlight;
                OpenStartTicks = openStartTicks;
                OpenEndTicks = openEndTicks;
                WriteStartTicks = writeStartTicks;
                WriteEndTicks = writeEndTicks;
            }

            public void AddMove(
                double moveMs,
                QueueSnap before,
                QueueSnap after,
                long startTicks,
                long endTicks)
            {
                MoveMs += moveMs;
                AccBeforeMove = before.Accounted;
                AccAfterMove = after.Accounted;
                QueuedBeforeMove = before.Queued;
                QueuedAfterMove = after.Queued;
                InFlightBeforeMove = before.InFlight;
                InFlightAfterMove = after.InFlight;
                MoveStartTicks = startTicks;
                MoveEndTicks = endTicks;
            }

            public void AddExists(double existsMs) => ExistsMs += existsMs;

            public void AddDirectory(double directoryMs) => DirectoryMs += directoryMs;

            public double FsSumMs => OpenMs + WriteBytesMs + FlushMs + MoveMs + ExistsMs + DirectoryMs;

            public readonly record struct QueueSnap(long Accounted, int Queued, long InFlight);
        }

        /// <summary>
        /// Bounded ring of correlated per-article process/FS/e2e/queue samples (test-only).
        /// Overwrites oldest when full. Includes before/after AccountedBytes per FS stage for
        /// event-order analysis (precedes vs follows). Concurrent workers make across-article
        /// ordering approximate; do not treat snapshots as an exact exclusive queue history.
        /// </summary>
        private sealed class PublishTailCorrelator
        {
            private readonly Sample[] _samples;
            private int _next;
            private int _retained;
            private int _attempts;
            private int _overwrites;

            public PublishTailCorrelator(int capacity)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 64);
                _samples = new Sample[capacity];
            }

            public void TryAdd(
                ArticleId artId,
                double processMs,
                PublishStageAccumulator stages,
                double e2eMs)
            {
                var sample = new Sample(
                    artId,
                    processMs,
                    stages.OpenMs,
                    stages.WriteBytesMs,
                    stages.FlushMs,
                    stages.MoveMs,
                    stages.ExistsMs,
                    stages.DirectoryMs,
                    stages.FsSumMs,
                    processMs - stages.FsSumMs,
                    e2eMs,
                    stages.ProcessStartTicks,
                    stages.ProcessEndTicks,
                    stages.AccProcessStart,
                    stages.AccProcessEnd,
                    stages.QueuedProcessStart,
                    stages.QueuedProcessEnd,
                    stages.InFlightProcessStart,
                    stages.InFlightProcessEnd,
                    stages.AccBeforeOpen,
                    stages.AccAfterOpen,
                    stages.AccBeforeWrite,
                    stages.AccAfterWrite,
                    stages.AccBeforeMove,
                    stages.AccAfterMove,
                    stages.QueuedBeforeOpen,
                    stages.QueuedAfterOpen,
                    stages.QueuedBeforeWrite,
                    stages.QueuedAfterWrite,
                    stages.QueuedBeforeMove,
                    stages.QueuedAfterMove,
                    stages.OpenStartTicks,
                    stages.OpenEndTicks,
                    stages.WriteStartTicks,
                    stages.WriteEndTicks,
                    stages.MoveStartTicks,
                    stages.MoveEndTicks);

                var index = Interlocked.Increment(ref _next) - 1;
                Interlocked.Increment(ref _attempts);
                if (index >= _samples.Length)
                {
                    Interlocked.Increment(ref _overwrites);
                }

                var slot = index % _samples.Length;
                _samples[slot] = sample;
                var retained = Math.Min(index + 1, _samples.Length);
                Volatile.Write(ref _retained, retained);
            }

            public bool TryGetLocalPercentileThresholds(out AbsoluteSlowThresholds thresholds)
            {
                var attempts = Volatile.Read(ref _attempts);
                var n = Math.Min(attempts, _samples.Length);
                if (n <= 0)
                {
                    thresholds = default;
                    return false;
                }

                var copy = Snapshot(n);
                thresholds = new AbsoluteSlowThresholds(
                    Percentile(SelectSorted(copy, static s => s.ProcessMs), 0.95),
                    Percentile(SelectSorted(copy, static s => s.OpenMs), 0.95),
                    Percentile(SelectSorted(copy, static s => s.WriteBytesMs), 0.95),
                    Percentile(SelectSorted(copy, static s => s.MoveMs), 0.95));
                return true;
            }

            /// <summary>
            /// Event-order analysis using fixed absolute thresholds (e.g. w=4 p95), plus extreme
            /// spike counts at 10 ms / 50 ms that do not move with per-run percentiles.
            /// </summary>
            public string AbsoluteEventOrderReport(AbsoluteSlowThresholds absolute)
            {
                var attempts = Volatile.Read(ref _attempts);
                var overwrites = Volatile.Read(ref _overwrites);
                var n = Math.Min(attempts, _samples.Length);
                if (n <= 0)
                {
                    return $"absoluteSlowAnalysis samples=0 attempts={attempts} overwrites={overwrites}";
                }

                var copy = Snapshot(n);
                var openP50 = Percentile(SelectSorted(copy, static s => s.OpenMs), 0.50);
                var writeP50 = Percentile(SelectSorted(copy, static s => s.WriteBytesMs), 0.50);
                var moveP50 = Percentile(SelectSorted(copy, static s => s.MoveMs), 0.50);
                var processP50 = Percentile(SelectSorted(copy, static s => s.ProcessMs), 0.50);

                var orderOpen = AnalyzeOpOrder(
                    copy,
                    static s => s.OpenMs,
                    absolute.OpenP95Ms,
                    static s => s.AccBeforeOpen,
                    static s => s.AccAfterOpen,
                    static s => s.QueuedBeforeOpen,
                    static s => s.OpenStartTicks,
                    static s => s.OpenEndTicks,
                    openP50);
                var orderWrite = AnalyzeOpOrder(
                    copy,
                    static s => s.WriteBytesMs,
                    absolute.WriteP95Ms,
                    static s => s.AccBeforeWrite,
                    static s => s.AccAfterWrite,
                    static s => s.QueuedBeforeWrite,
                    static s => s.WriteStartTicks,
                    static s => s.WriteEndTicks,
                    writeP50);
                var orderMove = AnalyzeOpOrder(
                    copy,
                    static s => s.MoveMs,
                    absolute.MoveP95Ms,
                    static s => s.AccBeforeMove,
                    static s => s.AccAfterMove,
                    static s => s.QueuedBeforeMove,
                    static s => s.MoveStartTicks,
                    static s => s.MoveEndTicks,
                    moveP50);

                var slowProcessN = 0;
                double slowAccDeltaOpen = 0;
                double fastAccDeltaOpen = 0;
                var slowProcessForAcc = 0;
                var fastProcessForAcc = 0;
                var openGe10 = 0;
                var openGe50 = 0;
                var moveGe10 = 0;
                var moveGe50 = 0;
                var writeGe10 = 0;
                for (var i = 0; i < n; i++)
                {
                    var s = copy[i];
                    if (s.OpenMs >= 10)
                    {
                        openGe10++;
                    }

                    if (s.OpenMs >= 50)
                    {
                        openGe50++;
                    }

                    if (s.MoveMs >= 10)
                    {
                        moveGe10++;
                    }

                    if (s.MoveMs >= 50)
                    {
                        moveGe50++;
                    }

                    if (s.WriteBytesMs >= 10)
                    {
                        writeGe10++;
                    }

                    if (s.ProcessMs >= absolute.ProcessP95Ms)
                    {
                        slowProcessN++;
                        slowAccDeltaOpen += s.AccAfterOpen - s.AccBeforeOpen;
                        slowProcessForAcc++;
                    }
                    else if (s.ProcessMs <= processP50)
                    {
                        fastAccDeltaOpen += s.AccAfterOpen - s.AccBeforeOpen;
                        fastProcessForAcc++;
                    }
                }

                var verdict = DecideOrderVerdict(orderOpen, orderWrite, orderMove);
                return
                    $"absoluteSlowAnalysis samplesRetained={n} attempts={attempts} overwrites={overwrites} "
                    + $"absProcessP95Ms={absolute.ProcessP95Ms:0.000} absOpenP95Ms={absolute.OpenP95Ms:0.000} "
                    + $"absWriteP95Ms={absolute.WriteP95Ms:0.000} absMoveP95Ms={absolute.MoveP95Ms:0.000} "
                    + $"slowProcess_ge_absP95_n={slowProcessN} "
                    + $"meanAccDeltaOpen_when_process_ge_absP95={(slowProcessForAcc > 0 ? slowAccDeltaOpen / slowProcessForAcc : 0):0} "
                    + $"meanAccDeltaOpen_when_process_le_localP50={(fastProcessForAcc > 0 ? fastAccDeltaOpen / fastProcessForAcc : 0):0} "
                    + $"extremeOpen_ge10ms={openGe10} ge50ms={openGe50} "
                    + $"extremeWrite_ge10ms={writeGe10} "
                    + $"extremeMove_ge10ms={moveGe10} ge50ms={moveGe50} "
                    + $"eventOrderAbs_open={FormatOpOrder(orderOpen)} "
                    + $"eventOrderAbs_write={FormatOpOrder(orderWrite)} "
                    + $"eventOrderAbs_move={FormatOpOrder(orderMove)} "
                    + $"eventOrderAbsVerdict={verdict} "
                    + "note=absolute_thresholds_from_w4_baseline_not_local_p95";
            }

            private Sample[] Snapshot(int n)
            {
                var copy = new Sample[n];
                Array.Copy(_samples, 0, copy, 0, n);
                return copy;
            }

            private static double[] SelectSorted(Sample[] copy, Func<Sample, double> selector)
            {
                var arr = new double[copy.Length];
                for (var i = 0; i < copy.Length; i++)
                {
                    arr[i] = selector(copy[i]);
                }

                Array.Sort(arr);
                return arr;
            }

            public string Report()
            {
                var attempts = Volatile.Read(ref _attempts);
                var overwrites = Volatile.Read(ref _overwrites);
                var n = Math.Min(attempts, _samples.Length);
                if (n <= 0)
                {
                    return $"tailCorrelation samples=0 attempts={attempts} overwrites={overwrites}";
                }

                var copy = new Sample[n];
                Array.Copy(_samples, 0, copy, 0, n);

                double[] Select(Func<Sample, double> selector)
                {
                    var arr = new double[n];
                    for (var i = 0; i < n; i++)
                    {
                        arr[i] = selector(copy[i]);
                    }

                    Array.Sort(arr);
                    return arr;
                }

                var process = Select(static s => s.ProcessMs);
                var open = Select(static s => s.OpenMs);
                var write = Select(static s => s.WriteBytesMs);
                var flush = Select(static s => s.FlushMs);
                var move = Select(static s => s.MoveMs);
                var exists = Select(static s => s.ExistsMs);
                var dir = Select(static s => s.DirectoryMs);
                var fsSum = Select(static s => s.FsSumMs);
                var residual = Select(static s => s.ResidualMs);
                var e2e = Select(static s => double.IsNaN(s.E2eMs) ? 0 : s.E2eMs);
                var accountedEnd = Select(static s => s.AccProcessEnd);
                var queuedEnd = Select(static s => s.QueuedProcessEnd);

                var processP95 = Percentile(process, 0.95);
                var processP50 = Percentile(process, 0.50);
                var openP95 = Percentile(open, 0.95);
                var writeP95 = Percentile(write, 0.95);
                var moveP95 = Percentile(move, 0.95);
                var openP50 = Percentile(open, 0.50);
                var writeP50 = Percentile(write, 0.50);
                var moveP50 = Percentile(move, 0.50);

                var slowOpen = 0;
                var slowWrite = 0;
                var slowMove = 0;
                var slowResidual = 0;
                var slowCount = 0;
                double slowAccountedEnd = 0;
                double fastAccountedEnd = 0;
                var fastCount = 0;
                for (var i = 0; i < n; i++)
                {
                    var s = copy[i];
                    if (s.ProcessMs >= processP95)
                    {
                        slowCount++;
                        slowAccountedEnd += s.AccProcessEnd;
                        var dominant = DominantStage(s);
                        switch (dominant)
                        {
                            case "open":
                                slowOpen++;
                                break;
                            case "write":
                                slowWrite++;
                                break;
                            case "move":
                                slowMove++;
                                break;
                            default:
                                slowResidual++;
                                break;
                        }
                    }
                    else if (s.ProcessMs <= processP50)
                    {
                        fastCount++;
                        fastAccountedEnd += s.AccProcessEnd;
                    }
                }

                var orderOpen = AnalyzeOpOrder(
                    copy,
                    static s => s.OpenMs,
                    openP95,
                    static s => s.AccBeforeOpen,
                    static s => s.AccAfterOpen,
                    static s => s.QueuedBeforeOpen,
                    static s => s.OpenStartTicks,
                    static s => s.OpenEndTicks,
                    openP50);
                var orderWrite = AnalyzeOpOrder(
                    copy,
                    static s => s.WriteBytesMs,
                    writeP95,
                    static s => s.AccBeforeWrite,
                    static s => s.AccAfterWrite,
                    static s => s.QueuedBeforeWrite,
                    static s => s.WriteStartTicks,
                    static s => s.WriteEndTicks,
                    writeP50);
                var orderMove = AnalyzeOpOrder(
                    copy,
                    static s => s.MoveMs,
                    moveP95,
                    static s => s.AccBeforeMove,
                    static s => s.AccAfterMove,
                    static s => s.QueuedBeforeMove,
                    static s => s.MoveStartTicks,
                    static s => s.MoveEndTicks,
                    moveP50);

                var slowArticleAccBeforeOpen = MeanWhere(
                    copy,
                    s => s.ProcessMs >= processP95,
                    static s => s.AccBeforeOpen);
                var fastArticleAccBeforeOpen = MeanWhere(
                    copy,
                    s => s.ProcessMs <= processP50,
                    static s => s.AccBeforeOpen);
                var slowArticleAccAfterMove = MeanWhere(
                    copy,
                    s => s.ProcessMs >= processP95,
                    static s => s.AccAfterMove);
                var fastArticleAccAfterMove = MeanWhere(
                    copy,
                    s => s.ProcessMs <= processP50,
                    static s => s.AccAfterMove);
                var slowArticleAccDeltaProcess = MeanWhere(
                    copy,
                    s => s.ProcessMs >= processP95,
                    static s => s.AccProcessEnd - s.AccProcessStart);
                var fastArticleAccDeltaProcess = MeanWhere(
                    copy,
                    s => s.ProcessMs <= processP50,
                    static s => s.AccProcessEnd - s.AccProcessStart);

                var corrProcessMove = Pearson(copy, static s => s.ProcessMs, static s => s.MoveMs);
                var corrProcessOpen = Pearson(copy, static s => s.ProcessMs, static s => s.OpenMs);
                var corrProcessWrite = Pearson(copy, static s => s.ProcessMs, static s => s.WriteBytesMs);
                var corrProcessE2e = Pearson(
                    copy,
                    static s => s.ProcessMs,
                    static s => double.IsNaN(s.E2eMs) ? s.ProcessMs : s.E2eMs);
                var corrProcessAccountedEnd = Pearson(copy, static s => s.ProcessMs, static s => s.AccProcessEnd);
                var corrOpenVsAccBefore = Pearson(copy, static s => s.OpenMs, static s => s.AccBeforeOpen);
                var corrOpenVsAccAfter = Pearson(copy, static s => s.OpenMs, static s => s.AccAfterOpen);
                var corrMoveVsAccBefore = Pearson(copy, static s => s.MoveMs, static s => s.AccBeforeMove);
                var corrMoveVsAccAfter = Pearson(copy, static s => s.MoveMs, static s => s.AccAfterMove);
                var corrOpenVsAccDelta = Pearson(
                    copy,
                    static s => s.OpenMs,
                    static s => s.AccAfterOpen - s.AccBeforeOpen);

                var verdict = DecideOrderVerdict(orderOpen, orderWrite, orderMove);

                static string Fmt(double[] sorted, string name) =>
                    $"{name}_p50={Percentile(sorted, 0.50):0.000} p95={Percentile(sorted, 0.95):0.000} "
                    + $"p99={Percentile(sorted, 0.99):0.000} max={sorted[^1]:0.000} "
                    + $"tailRatio_p99_over_p50={(Percentile(sorted, 0.50) > 0 ? Percentile(sorted, 0.99) / Percentile(sorted, 0.50) : 0):0.00}";

                return
                    $"tailCorrelation samplesRetained={n} attempts={attempts} overwrites={overwrites} cap={_samples.Length} "
                    + $"slowThreshold=process_ge_p95_open_ge_p95_write_ge_p95_move_ge_p95 "
                    + $"processP95Ms={processP95:0.000} openP95Ms={openP95:0.000} writeP95Ms={writeP95:0.000} moveP95Ms={moveP95:0.000} "
                    + $"{Fmt(process, "corrProcessMs")} "
                    + $"{Fmt(open, "corrOpenMs")} "
                    + $"{Fmt(write, "corrWriteBytesMs")} "
                    + $"{Fmt(flush, "corrFlushMs")} "
                    + $"{Fmt(move, "corrMoveMs")} "
                    + $"{Fmt(exists, "corrExistsMs")} "
                    + $"{Fmt(dir, "corrDirectoryMs")} "
                    + $"{Fmt(fsSum, "corrFsSumMs")} "
                    + $"{Fmt(residual, "corrResidualMs")} "
                    + $"{Fmt(e2e, "corrE2eMs")} "
                    + $"corrAccountedEnd_p50={Percentile(accountedEnd, 0.50):0} p95={Percentile(accountedEnd, 0.95):0} "
                    + $"corrQueuedEnd_p50={Percentile(queuedEnd, 0.50):0} p95={Percentile(queuedEnd, 0.95):0} "
                    + $"slowProcess_ge_p95_n={slowCount} dominantOpen={slowOpen} dominantWrite={slowWrite} "
                    + $"dominantMove={slowMove} dominantResidual={slowResidual} "
                    + $"meanAccountedEnd_when_process_ge_p95={(slowCount > 0 ? slowAccountedEnd / slowCount : 0):0} "
                    + $"meanAccountedEnd_when_process_le_p50={(fastCount > 0 ? fastAccountedEnd / fastCount : 0):0} "
                    + $"meanAccBeforeOpen_slowArticle={slowArticleAccBeforeOpen:0} "
                    + $"meanAccBeforeOpen_fastArticle={fastArticleAccBeforeOpen:0} "
                    + $"meanAccAfterMove_slowArticle={slowArticleAccAfterMove:0} "
                    + $"meanAccAfterMove_fastArticle={fastArticleAccAfterMove:0} "
                    + $"meanAccDeltaProcess_slowArticle={slowArticleAccDeltaProcess:0} "
                    + $"meanAccDeltaProcess_fastArticle={fastArticleAccDeltaProcess:0} "
                    + $"eventOrder_open={FormatOpOrder(orderOpen)} "
                    + $"eventOrder_write={FormatOpOrder(orderWrite)} "
                    + $"eventOrder_move={FormatOpOrder(orderMove)} "
                    + $"eventOrderVerdict={verdict} "
                    + $"pearson_process_vs_move={corrProcessMove:0.000} "
                    + $"pearson_process_vs_open={corrProcessOpen:0.000} "
                    + $"pearson_process_vs_write={corrProcessWrite:0.000} "
                    + $"pearson_process_vs_e2e={corrProcessE2e:0.000} "
                    + $"pearson_process_vs_accountedEnd={corrProcessAccountedEnd:0.000} "
                    + $"pearson_open_vs_accBefore={corrOpenVsAccBefore:0.000} "
                    + $"pearson_open_vs_accAfter={corrOpenVsAccAfter:0.000} "
                    + $"pearson_open_vs_accDelta={corrOpenVsAccDelta:0.000} "
                    + $"pearson_move_vs_accBefore={corrMoveVsAccBefore:0.000} "
                    + $"pearson_move_vs_accAfter={corrMoveVsAccAfter:0.000} "
                    + "note=AccountedBytes_payload_accounting_not_disk_queue_depth;"
                    + "snapshots_are_event_time_not_exact_history;"
                    + "concurrent_workers_blur_across_article_ordering";
            }

            private readonly record struct OpOrderStats(
                int SlowN,
                int NormalN,
                double MeanAccBeforeSlow,
                double MeanAccBeforeNormal,
                double MeanAccAfterSlow,
                double MeanAccAfterNormal,
                double MeanAccDeltaSlow,
                double MeanAccDeltaNormal,
                double MeanQueuedBeforeSlow,
                double MeanQueuedBeforeNormal,
                double MeanSubsequentOpMsAfterSlow,
                double MeanSubsequentOpMsAfterNormal,
                int ClusterPairsWithin10Ms,
                double AccBeforeRatioSlowOverNormal,
                double AccDeltaRatioSlowOverNormal,
                double SubsequentRatioSlowOverNormal);

            private static OpOrderStats AnalyzeOpOrder(
                Sample[] samples,
                Func<Sample, double> opMs,
                double slowThresholdMs,
                Func<Sample, long> accBefore,
                Func<Sample, long> accAfter,
                Func<Sample, int> queuedBefore,
                Func<Sample, long> startTicks,
                Func<Sample, long> endTicks,
                double baselineOpP50)
            {
                double sumAccBeforeSlow = 0, sumAccBeforeNormal = 0;
                double sumAccAfterSlow = 0, sumAccAfterNormal = 0;
                double sumDeltaSlow = 0, sumDeltaNormal = 0;
                double sumQueuedBeforeSlow = 0, sumQueuedBeforeNormal = 0;
                var slowN = 0;
                var normalN = 0;
                var slowEnds = new List<(long EndTicks, double OpMs)>(64);
                var normalEnds = new List<(long EndTicks, double OpMs)>(256);
                var slowStarts = new List<long>(64);

                for (var i = 0; i < samples.Length; i++)
                {
                    var s = samples[i];
                    var ms = opMs(s);
                    if (ms <= 0 && startTicks(s) == 0)
                    {
                        continue;
                    }

                    var before = accBefore(s);
                    var after = accAfter(s);
                    var delta = after - before;
                    var queued = queuedBefore(s);
                    if (ms >= slowThresholdMs)
                    {
                        slowN++;
                        sumAccBeforeSlow += before;
                        sumAccAfterSlow += after;
                        sumDeltaSlow += delta;
                        sumQueuedBeforeSlow += queued;
                        slowEnds.Add((endTicks(s), ms));
                        slowStarts.Add(startTicks(s));
                    }
                    else
                    {
                        normalN++;
                        sumAccBeforeNormal += before;
                        sumAccAfterNormal += after;
                        sumDeltaNormal += delta;
                        sumQueuedBeforeNormal += queued;
                        normalEnds.Add((endTicks(s), ms));
                    }
                }

                var subsequentAfterSlow = MeanSubsequentOpMs(samples, opMs, startTicks, slowEnds, maxFollowers: 4);
                var subsequentAfterNormal = MeanSubsequentOpMs(samples, opMs, startTicks, normalEnds, maxFollowers: 4);

                var clusterPairs = 0;
                if (slowStarts.Count > 1)
                {
                    slowStarts.Sort();
                    var windowTicks = (long)(0.010 * Stopwatch.Frequency);
                    for (var i = 1; i < slowStarts.Count; i++)
                    {
                        if (slowStarts[i] - slowStarts[i - 1] <= windowTicks)
                        {
                            clusterPairs++;
                        }
                    }
                }

                var meanAccBeforeSlow = slowN > 0 ? sumAccBeforeSlow / slowN : 0;
                var meanAccBeforeNormal = normalN > 0 ? sumAccBeforeNormal / normalN : 0;
                var meanAccDeltaSlow = slowN > 0 ? sumDeltaSlow / slowN : 0;
                var meanAccDeltaNormal = normalN > 0 ? sumDeltaNormal / normalN : 0;
                _ = baselineOpP50;

                return new OpOrderStats(
                    slowN,
                    normalN,
                    meanAccBeforeSlow,
                    meanAccBeforeNormal,
                    slowN > 0 ? sumAccAfterSlow / slowN : 0,
                    normalN > 0 ? sumAccAfterNormal / normalN : 0,
                    meanAccDeltaSlow,
                    meanAccDeltaNormal,
                    slowN > 0 ? sumQueuedBeforeSlow / slowN : 0,
                    normalN > 0 ? sumQueuedBeforeNormal / normalN : 0,
                    subsequentAfterSlow,
                    subsequentAfterNormal,
                    clusterPairs,
                    meanAccBeforeNormal > 0 ? meanAccBeforeSlow / meanAccBeforeNormal : 0,
                    Math.Abs(meanAccDeltaNormal) > 1 ? meanAccDeltaSlow / meanAccDeltaNormal : 0,
                    subsequentAfterNormal > 0 ? subsequentAfterSlow / subsequentAfterNormal : 0);
            }

            private static double MeanSubsequentOpMs(
                Sample[] samples,
                Func<Sample, double> opMs,
                Func<Sample, long> startTicks,
                List<(long EndTicks, double OpMs)> anchors,
                int maxFollowers)
            {
                if (anchors.Count == 0)
                {
                    return 0;
                }

                // Cap anchors to keep O(anchors * n) bounded for the 8192 ring.
                var take = Math.Min(anchors.Count, 256);
                double sum = 0;
                var count = 0;
                var starts = new (long Start, double Ms)[samples.Length];
                var startCount = 0;
                for (var i = 0; i < samples.Length; i++)
                {
                    var start = startTicks(samples[i]);
                    if (start <= 0)
                    {
                        continue;
                    }

                    starts[startCount++] = (start, opMs(samples[i]));
                }

                Array.Sort(starts, 0, startCount, Comparer<(long Start, double Ms)>.Create(static (x, y) => x.Start.CompareTo(y.Start)));

                for (var a = 0; a < take; a++)
                {
                    var end = anchors[a].EndTicks;
                    if (end <= 0)
                    {
                        continue;
                    }

                    // Binary search first start > end.
                    var lo = 0;
                    var hi = startCount;
                    while (lo < hi)
                    {
                        var mid = (lo + hi) >> 1;
                        if (starts[mid].Start <= end)
                        {
                            lo = mid + 1;
                        }
                        else
                        {
                            hi = mid;
                        }
                    }

                    var taken = 0;
                    for (var i = lo; i < startCount && taken < maxFollowers; i++, taken++)
                    {
                        sum += starts[i].Ms;
                        count++;
                    }
                }

                return count > 0 ? sum / count : 0;
            }

            private static string FormatOpOrder(OpOrderStats s) =>
                $"slowN={s.SlowN}/normalN={s.NormalN} "
                + $"meanAccBefore_slow={s.MeanAccBeforeSlow:0} normal={s.MeanAccBeforeNormal:0} "
                + $"ratioBefore={s.AccBeforeRatioSlowOverNormal:0.00} "
                + $"meanAccAfter_slow={s.MeanAccAfterSlow:0} normal={s.MeanAccAfterNormal:0} "
                + $"meanAccDelta_slow={s.MeanAccDeltaSlow:0} normal={s.MeanAccDeltaNormal:0} "
                + $"ratioDelta={s.AccDeltaRatioSlowOverNormal:0.00} "
                + $"meanQueuedBefore_slow={s.MeanQueuedBeforeSlow:0.00} normal={s.MeanQueuedBeforeNormal:0.00} "
                + $"meanSubsequentOpMs_afterSlow={s.MeanSubsequentOpMsAfterSlow:0.000} "
                + $"afterNormal={s.MeanSubsequentOpMsAfterNormal:0.000} "
                + $"subsequentRatio={s.SubsequentRatioSlowOverNormal:0.00} "
                + $"slowClusterPairs_le10ms={s.ClusterPairsWithin10Ms}";

            private static string DecideOrderVerdict(OpOrderStats open, OpOrderStats write, OpOrderStats move)
            {
                // Combine open/write/move: elevated AccBefore favors precedes; elevated AccDelta during op
                // favors consequence (backlog grows while blocked); both elevated favors feedback.
                var beforeElevated =
                    (open.AccBeforeRatioSlowOverNormal >= 1.5 ? 1 : 0)
                    + (write.AccBeforeRatioSlowOverNormal >= 1.5 ? 1 : 0)
                    + (move.AccBeforeRatioSlowOverNormal >= 1.5 ? 1 : 0);
                var deltaElevated =
                    (open.MeanAccDeltaSlow > open.MeanAccDeltaNormal + (768_000 * 0.5) ? 1 : 0)
                    + (write.MeanAccDeltaSlow > write.MeanAccDeltaNormal + (768_000 * 0.5) ? 1 : 0)
                    + (move.MeanAccDeltaSlow > move.MeanAccDeltaNormal + (768_000 * 0.5) ? 1 : 0);
                var subsequentElevated =
                    (open.SubsequentRatioSlowOverNormal >= 1.25 ? 1 : 0)
                    + (write.SubsequentRatioSlowOverNormal >= 1.25 ? 1 : 0)
                    + (move.SubsequentRatioSlowOverNormal >= 1.25 ? 1 : 0);

                if (beforeElevated >= 2 && deltaElevated >= 2)
                {
                    return "both_patterns_feedback_candidate";
                }

                if (beforeElevated >= 2 && subsequentElevated >= 1)
                {
                    return "queue_pressure_precedes_slow_ops";
                }

                if (beforeElevated >= 2 && deltaElevated <= 1)
                {
                    return "queue_pressure_precedes_slow_ops";
                }

                if (deltaElevated >= 2 && beforeElevated <= 1)
                {
                    return "queue_pressure_primarily_rises_after_or_during_slow_ops";
                }

                if (beforeElevated >= 1 && deltaElevated >= 1)
                {
                    return "both_patterns_feedback_candidate";
                }

                return "no_reliable_ordering";
            }

            private static double MeanWhere(Sample[] samples, Func<Sample, bool> pred, Func<Sample, double> sel)
            {
                double sum = 0;
                var n = 0;
                for (var i = 0; i < samples.Length; i++)
                {
                    if (!pred(samples[i]))
                    {
                        continue;
                    }

                    sum += sel(samples[i]);
                    n++;
                }

                return n > 0 ? sum / n : 0;
            }

            private static string DominantStage(Sample s)
            {
                var open = s.OpenMs;
                var write = s.WriteBytesMs;
                var move = s.MoveMs;
                var residual = Math.Max(0, s.ResidualMs);
                var max = open;
                var name = "open";
                if (write > max)
                {
                    max = write;
                    name = "write";
                }

                if (move > max)
                {
                    max = move;
                    name = "move";
                }

                if (residual > max)
                {
                    name = "residual";
                }

                return name;
            }

            private static double Pearson(Sample[] samples, Func<Sample, double> xSel, Func<Sample, double> ySel)
            {
                var n = samples.Length;
                if (n < 3)
                {
                    return 0;
                }

                double sumX = 0, sumY = 0, sumXX = 0, sumYY = 0, sumXY = 0;
                for (var i = 0; i < n; i++)
                {
                    var x = xSel(samples[i]);
                    var y = ySel(samples[i]);
                    sumX += x;
                    sumY += y;
                    sumXX += x * x;
                    sumYY += y * y;
                    sumXY += x * y;
                }

                var num = (n * sumXY) - (sumX * sumY);
                var den = Math.Sqrt(((n * sumXX) - (sumX * sumX)) * ((n * sumYY) - (sumY * sumY)));
                return den > 0 ? num / den : 0;
            }

            private readonly record struct Sample(
                ArticleId ArtId,
                double ProcessMs,
                double OpenMs,
                double WriteBytesMs,
                double FlushMs,
                double MoveMs,
                double ExistsMs,
                double DirectoryMs,
                double FsSumMs,
                double ResidualMs,
                double E2eMs,
                long ProcessStartTicks,
                long ProcessEndTicks,
                long AccProcessStart,
                long AccProcessEnd,
                int QueuedProcessStart,
                int QueuedProcessEnd,
                long InFlightProcessStart,
                long InFlightProcessEnd,
                long AccBeforeOpen,
                long AccAfterOpen,
                long AccBeforeWrite,
                long AccAfterWrite,
                long AccBeforeMove,
                long AccAfterMove,
                int QueuedBeforeOpen,
                int QueuedAfterOpen,
                int QueuedBeforeWrite,
                int QueuedAfterWrite,
                int QueuedBeforeMove,
                int QueuedAfterMove,
                long OpenStartTicks,
                long OpenEndTicks,
                long WriteStartTicks,
                long WriteEndTicks,
                long MoveStartTicks,
                long MoveEndTicks);
        }

        private sealed class TimedCapacityProcessor : IAcceptedArticleProcessor
        {
            private int _waiting;
            private int _completed;

            public int WaitingCalls => Volatile.Read(ref _waiting);

            public int Completed => Volatile.Read(ref _completed);

            public bool AllowComplete { get; set; }

            public long LastWaitTicks { get; private set; }

            public long FirstResumeTicks { get; private set; }

            public long CompleteTicks { get; private set; }

            public void EnsureReady()
            {
            }

            public ValueTask<AcceptedArticleProcessResult> ProcessAsync(
                AcceptedArticle article,
                CancellationToken cancellationToken)
            {
                if (!AllowComplete)
                {
                    LastWaitTicks = Stopwatch.GetTimestamp();
                    Interlocked.Increment(ref _waiting);
                    return ValueTask.FromResult(AcceptedArticleProcessResult.WaitingForCapacity);
                }

                if (FirstResumeTicks == 0)
                {
                    FirstResumeTicks = Stopwatch.GetTimestamp();
                }

                CompleteTicks = Stopwatch.GetTimestamp();
                Interlocked.Increment(ref _completed);
                return ValueTask.FromResult(AcceptedArticleProcessResult.Completed);
            }
        }

        private sealed class HoldingProcessor : IAcceptedArticleProcessor
        {
            private readonly TaskCompletionSource _entered;
            private int _completed;

            public HoldingProcessor(TaskCompletionSource entered) => _entered = entered;

            public int Completed => Volatile.Read(ref _completed);

            public void EnsureReady()
            {
            }

            public async ValueTask<AcceptedArticleProcessResult> ProcessAsync(
                AcceptedArticle article,
                CancellationToken cancellationToken)
            {
                _entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _completed);
                return AcceptedArticleProcessResult.Completed;
            }
        }

        private sealed class ControllableProbe : ISpoolFreeSpaceProbe
        {
            private readonly object _gate = new();
            private readonly Dictionary<string, (long Total, long Available)> _byPath = new(StringComparer.OrdinalIgnoreCase);

            public void Set(string spoolRoot, long total, long available)
            {
                lock (_gate)
                {
                    _byPath[Path.TrimEndingDirectorySeparator(Path.GetFullPath(spoolRoot))] = (total, available);
                }
            }

            public bool TryRead(string spoolRoot, out long totalBytes, out long availableBytes)
            {
                lock (_gate)
                {
                    var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(spoolRoot));
                    if (_byPath.TryGetValue(key, out var values))
                    {
                        totalBytes = values.Total;
                        availableBytes = values.Available;
                        return true;
                    }
                }

                totalBytes = 0;
                availableBytes = 0;
                return false;
            }
        }

        private sealed class GenerousProbe : ISpoolFreeSpaceProbe
        {
            public bool TryRead(string spoolRoot, out long totalBytes, out long availableBytes)
            {
                totalBytes = 1L << 40;
                availableBytes = 1L << 39;
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
    }
}

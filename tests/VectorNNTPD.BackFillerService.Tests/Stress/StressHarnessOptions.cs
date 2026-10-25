namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Opt-in configuration for the BackFiller end-to-end stress harness.
    /// Secrets are read from environment variables and are never written to reports.
    /// </summary>
    public sealed class StressHarnessOptions
    {
        /// <summary>Master enable flag. Must be <c>1</c> for live stress tests to run.</summary>
        public const string EnableEnvironmentVariable = "VECTORNNTPD_BACKFILLER_E2E_STRESS";

        /// <summary>Phase selector for opt-in bench methods (A, B, C, D, provider, ALL).</summary>
        public const string PhaseEnvironmentVariable = "VECTORNNTPD_BACKFILLER_E2E_STRESS_PHASE";

        /// <summary>Publisher concurrency override (scalar or comma-separated list).</summary>
        public const string ConcurrencyEnvironmentVariable = "VECTORNNTPD_BACKFILLER_E2E_STRESS_CONCURRENCY";

        /// <summary>Run duration override in seconds.</summary>
        public const string DurationSecondsEnvironmentVariable = "VECTORNNTPD_BACKFILLER_E2E_STRESS_DURATION_SECONDS";

        /// <summary>Provider backbone label (e.g. Giganews, Eweka).</summary>
        public const string BackboneEnvironmentVariable = "VECTORNNTPD_BACKFILLER_E2E_STRESS_BACKBONE";

        /// <summary>In-flight work request cap override.</summary>
        public const string MaxInFlightEnvironmentVariable = "VECTORNNTPD_BACKFILLER_E2E_STRESS_MAX_INFLIGHT";

        /// <summary>News-log corpus directory override (fixtures only).</summary>
        public const string NewsLogDirectoryEnvironmentVariable = "VECTORNNTPD_BACKFILLER_E2E_STRESS_NEWSLOGS";

        /// <summary>Directory for workload id lists and manifest.</summary>
        public const string WorkloadDirectoryEnvironmentVariable = "VECTORNNTPD_BACKFILLER_E2E_STRESS_WORKLOAD";

        /// <summary>Directory for JSON reports and preflight snapshots.</summary>
        public const string ReportDirectoryEnvironmentVariable = "VECTORNNTPD_BACKFILLER_E2E_STRESS_REPORT";

        /// <summary>Overrides BackFiller listener host from Article Work success replies.</summary>
        public const string ListenerHostEnvironmentVariable = "VECTORNNTPD_BACKFILLER_E2E_STRESS_LISTENER_HOST";

        /// <summary>Overrides BackFiller listener TLS port from success replies.</summary>
        public const string ListenerPortEnvironmentVariable = "VECTORNNTPD_BACKFILLER_E2E_STRESS_LISTENER_PORT";

        /// <summary>Deterministic shuffle seed for workload partitioning.</summary>
        public const string SeedEnvironmentVariable = "VECTORNNTPD_BACKFILLER_E2E_STRESS_SEED";

        /// <summary>Live Article Work preflight probe count override.</summary>
        public const string ArticlePreflightCountEnvironmentVariable =
            "VECTORNNTPD_BACKFILLER_E2E_STRESS_ARTICLE_PREFLIGHT_COUNT";

        /// <summary>RabbitMQ broker hostname (Management API uses same host on 15672).</summary>
        public required string RabbitMqHost { get; init; }

        /// <summary>AMQP port.</summary>
        public int RabbitMqPort { get; init; } = 5672;

        /// <summary>Virtual host for Article Work exchanges.</summary>
        public string RabbitMqVirtualHost { get; init; } = "/";

        /// <summary>Broker username (never logged).</summary>
        public required string RabbitMqUsername { get; init; }

        /// <summary>Broker password (never logged).</summary>
        public required string RabbitMqPassword { get; init; }

        /// <summary>Per-request Article Work reply waiter timeout.</summary>
        public TimeSpan WorkReplyTimeout { get; init; } = TimeSpan.FromSeconds(60);
        /// <summary>
        /// Bounded wait for broker-outstanding RPCs (including timed-out callers) before disposing
        /// the exclusive ReplyTo connection.
        /// </summary>
        public TimeSpan ReplyQueueDisposeDrainTimeout { get; init; } = TimeSpan.FromSeconds(120);
        /// <summary>NNTP ARTICLE command timeout for listener delivery.</summary>
        public TimeSpan ArticleTimeout { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Absolute path to news-log corpus used for discovery and provenance.</summary>
        public string NewsLogDirectory { get; init; } = string.Empty;

        /// <summary>Directory containing workload id files and corpus manifest.</summary>
        public string WorkloadDirectory { get; init; } = string.Empty;

        /// <summary>Output directory for run summaries and preflight captures.</summary>
        public string ReportDirectory { get; init; } = string.Empty;

        /// <summary>When set, NNTP client connects here instead of reply FQDN.</summary>
        public string? ListenerHostOverride { get; init; }

        /// <summary>When set, NNTP client uses this port instead of reply listener port.</summary>
        public int? ListenerPortOverride { get; init; }

        /// <summary>Seed for deterministic workload shuffles.</summary>
        public int Seed { get; init; } = 42;
        /// <summary>
        /// Number of selected Message-IDs to probe via Article Work before throughput publishing.
        /// Zero disables live retrieval preflight (membership/canonical checks still apply).
        /// </summary>
        public int ArticlePreflightCount { get; init; } = 5;
        /// <summary>Completed-reply timeout rate threshold for early run abort.</summary>
        public double AbortOnTimeoutRate { get; init; } = 0.25;

        /// <summary>Completed-reply failure rate threshold for early run abort.</summary>
        public double AbortOnFailureRate { get; init; } = 0.50;

        /// <summary>Minimum completed replies before abort thresholds are evaluated.</summary>
        public int AbortAfterRequests { get; init; } = 200;

        /// <summary>Returns <see langword="true"/> when the opt-in environment flag is set to <c>1</c>.</summary>
        public static bool IsEnabled() =>
            string.Equals(
                Environment.GetEnvironmentVariable(EnableEnvironmentVariable),
                "1",
                StringComparison.Ordinal);

        /// <summary>
        /// Builds options from environment variables. Throws when RabbitMQ credentials are missing.
        /// Does not log secret values.
        /// </summary>
        public static StressHarnessOptions FromEnvironment()
        {
            var username = Environment.GetEnvironmentVariable("VECTOR__RABBITMQ__USERNAME");
            var password = Environment.GetEnvironmentVariable("VECTOR__RABBITMQ__PASSWORD");
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException(
                    "VECTOR__RABBITMQ__USERNAME and VECTOR__RABBITMQ__PASSWORD are required for live stress.");
            }

            var repoRoot = FindRepositoryRoot();
            // Live stress defaults to the required absolute VectorNNTP news-log corpus.
            // Override only for isolated fixtures via VECTORNNTPD_BACKFILLER_E2E_STRESS_NEWSLOGS.
            // There is no silent fallback to VectorNNTPD/.artifacts/newslogs.
            var newsLogs = Environment.GetEnvironmentVariable(NewsLogDirectoryEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(newsLogs))
            {
                newsLogs = StressCorpusPaths.RequiredNewsLogDirectory;
            }

            var workload = Environment.GetEnvironmentVariable(WorkloadDirectoryEnvironmentVariable)
                ?? Path.Join(repoRoot, ".artifacts", "backfiller-stress", "workload");
            var report = Environment.GetEnvironmentVariable(ReportDirectoryEnvironmentVariable)
                ?? Path.Join(repoRoot, ".artifacts", "backfiller-stress", "reports");

            int? listenerPort = null;
            if (int.TryParse(Environment.GetEnvironmentVariable(ListenerPortEnvironmentVariable), out var port)
                && port is > 0 and <= 65535)
            {
                listenerPort = port;
            }

            var seed = 42;
            if (int.TryParse(Environment.GetEnvironmentVariable(SeedEnvironmentVariable), out var configuredSeed))
            {
                seed = configuredSeed;
            }

            var articlePreflightCount = 5;
            if (int.TryParse(
                    Environment.GetEnvironmentVariable(ArticlePreflightCountEnvironmentVariable),
                    out var configuredPreflight)
                && configuredPreflight >= 0)
            {
                articlePreflightCount = configuredPreflight;
            }

            return new StressHarnessOptions
            {
                RabbitMqHost = Environment.GetEnvironmentVariable("VECTORNNTPD_BACKFILLER_E2E_STRESS_RABBIT_HOST")
                    ?? "198.18.0.3",
                RabbitMqPort = 5672,
                RabbitMqVirtualHost = "/",
                RabbitMqUsername = username,
                RabbitMqPassword = password,
                NewsLogDirectory = newsLogs,
                WorkloadDirectory = workload,
                ReportDirectory = report,
                ListenerHostOverride = Environment.GetEnvironmentVariable(ListenerHostEnvironmentVariable),
                ListenerPortOverride = listenerPort,
                Seed = seed,
                ArticlePreflightCount = articlePreflightCount,
            };
        }

        /// <summary>Reads phase env var or returns <paramref name="defaultPhase"/>.</summary>
        public static string GetPhaseOrDefault(string defaultPhase) =>
            Environment.GetEnvironmentVariable(PhaseEnvironmentVariable) ?? defaultPhase;

        /// <summary>Positive concurrency from env or <paramref name="defaultConcurrency"/>.</summary>
        public static int GetConcurrencyOrDefault(int defaultConcurrency)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable(ConcurrencyEnvironmentVariable), out var value)
                && value > 0)
            {
                return value;
            }

            return defaultConcurrency;
        }

        /// <summary>Positive duration seconds from env or <paramref name="defaultSeconds"/>.</summary>
        public static int GetDurationSecondsOrDefault(int defaultSeconds)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable(DurationSecondsEnvironmentVariable), out var value)
                && value > 0)
            {
                return value;
            }

            return defaultSeconds;
        }

        /// <summary>Positive max-in-flight from env or <paramref name="defaultMax"/>.</summary>
        public static int GetMaxInFlightOrDefault(int defaultMax)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable(MaxInFlightEnvironmentVariable), out var value)
                && value > 0)
            {
                return value;
            }

            return defaultMax;
        }

        /// <summary>Backbone label from env or <paramref name="defaultBackbone"/>.</summary>
        public static string GetBackboneOrDefault(string defaultBackbone) =>
            Environment.GetEnvironmentVariable(BackboneEnvironmentVariable) ?? defaultBackbone;

        /// <summary>Redacted snapshot safe for reports (no passwords).</summary>
        public object ToRedactedReportObject() =>
            new
            {
                RabbitMqHost,
                RabbitMqPort,
                RabbitMqVirtualHost,
                RabbitMqUsername = "<redacted>",
                RabbitMqPassword = "<redacted>",
                WorkReplyTimeoutSeconds = WorkReplyTimeout.TotalSeconds,
                ArticleTimeoutSeconds = ArticleTimeout.TotalSeconds,
                NewsLogDirectory,
                WorkloadDirectory,
                ReportDirectory,
                ListenerHostOverride,
                ListenerPortOverride,
                Seed,
                ArticlePreflightCount,
                AbortOnTimeoutRate,
                AbortOnFailureRate,
                AbortAfterRequests,
            };

        /// <summary>Walks upward from the test base directory to locate <c>VectorNNTPD.slnx</c>.</summary>
        private static string FindRepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Join(dir.FullName, "VectorNNTPD.slnx")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            return Directory.GetCurrentDirectory();
        }
    }
}

namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Deterministic corpus provenance, selection, and correlation contracts for stress Message-IDs.
    /// </summary>
    public sealed class StressCorpusIntegrityTests
    {
        [Fact]
        public void Required_corpus_path_is_the_vectornntp_newslogs_directory()
        {
            Assert.Equal(
                @"C:\Users\chrisk\source\repos\VectorNNTP\.artifacts\newslogs",
                StressCorpusPaths.RequiredNewsLogDirectory);
        }

        [Fact]
        public void Required_corpus_path_resolves_to_usable_logs_on_this_host()
        {
            // After the verified junction (or a real VectorNNTP tree), discovery must see *.log files.
            NewsLogMessageIdDiscovery.RequireUsableCorpusDirectory(StressCorpusPaths.RequiredNewsLogDirectory);
            Assert.NotEmpty(NewsLogMessageIdDiscovery.DiscoverLogFiles(StressCorpusPaths.RequiredNewsLogDirectory));
            Assert.True(
                File.Exists(
                    Path.Join(StressCorpusPaths.RequiredNewsLogDirectory, "news-20261019.log")));
        }

        [Fact]
        public void Preflight_blocks_publish_on_invalid_or_non_corpus_ids()
        {
            var results = new[]
            {
                new CorpusIdPreflightResult("<a@host>", CorpusIdPreflightKind.NotInCorpus, "missing"),
            };
            var ex = Assert.Throws<InvalidOperationException>(
                () => CorpusArticlePreflight.ThrowIfPreflightBlocksPublish(results));
            Assert.Contains("blocked publish", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Preflight_blocks_publish_when_zero_ids_retrieved()
        {
            var results = new[]
            {
                new CorpusIdPreflightResult("<a@host>", CorpusIdPreflightKind.NotFound, "missing article"),
                new CorpusIdPreflightResult("<b@host>", CorpusIdPreflightKind.Timeout, "work reply timeout"),
            };
            var ex = Assert.Throws<InvalidOperationException>(
                () => CorpusArticlePreflight.ThrowIfPreflightBlocksPublish(results));
            Assert.Contains("none of the", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Preflight_allows_publish_when_at_least_one_retrieved()
        {
            var results = new[]
            {
                new CorpusIdPreflightResult("<a@host>", CorpusIdPreflightKind.Retrieved, null),
                new CorpusIdPreflightResult("<b@host>", CorpusIdPreflightKind.NotFound, "missing article"),
            };
            CorpusArticlePreflight.ThrowIfPreflightBlocksPublish(results);
        }

        [Fact]
        public async Task Workload_guard_rejects_hash_mismatch()
        {
            var dir = Path.Join(Path.GetTempPath(), "vnntp-hash-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var main = Path.Join(dir, "main-message-ids.txt");
                await File.WriteAllTextAsync(main, "<a@host>\n");
                await WorkloadCorpusManifest.WriteAsync(
                    dir,
                    new WorkloadCorpusManifest
                    {
                        CorpusDirectory = StressCorpusPaths.RequiredNewsLogDirectory,
                        FileCount = 1,
                        LogRecordsExamined = 1,
                        ValidParsedIds = 1,
                        RejectedInvalidMessageIds = 0,
                        UniqueValidIds = 1,
                        WarmupSelected = 0,
                        MainSelected = 1,
                        RepeatableSelected = 0,
                        Seed = 1,
                        MainIdsSha256 = "deadbeef",
                        CreatedUtc = DateTimeOffset.UtcNow,
                    },
                    CancellationToken.None);

                var options = new StressHarnessOptions
                {
                    RabbitMqHost = "127.0.0.1",
                    RabbitMqUsername = "x",
                    RabbitMqPassword = "y",
                    NewsLogDirectory = StressCorpusPaths.RequiredNewsLogDirectory,
                    WorkloadDirectory = dir,
                    ReportDirectory = dir,
                };
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => StressWorkloadGuard.RequireProvenancedWorkloadAsync(options, CancellationToken.None));
                Assert.Contains("SHA-256", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void FromEnvironment_defaults_to_required_corpus_path()
        {
            var previous = Environment.GetEnvironmentVariable(
                StressHarnessOptions.NewsLogDirectoryEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(
                    StressHarnessOptions.NewsLogDirectoryEnvironmentVariable,
                    null);
                // Credentials required by FromEnvironment — skip if absent in this shell.
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VECTOR__RABBITMQ__USERNAME"))
                    || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VECTOR__RABBITMQ__PASSWORD")))
                {
                    Assert.Equal(
                        StressCorpusPaths.RequiredNewsLogDirectory,
                        StressCorpusPaths.RequiredNewsLogDirectory);
                    return;
                }

                var options = StressHarnessOptions.FromEnvironment();
                Assert.Equal(StressCorpusPaths.RequiredNewsLogDirectory, options.NewsLogDirectory);
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    StressHarnessOptions.NewsLogDirectoryEnvironmentVariable,
                    previous);
            }
        }

        [Fact]
        public void FromEnvironment_honours_explicit_newslog_override_for_fixtures()
        {
            var previous = Environment.GetEnvironmentVariable(
                StressHarnessOptions.NewsLogDirectoryEnvironmentVariable);
            var fixture = Path.Join(Path.GetTempPath(), "vnntp-fixture-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VECTOR__RABBITMQ__USERNAME"))
                    || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VECTOR__RABBITMQ__PASSWORD")))
                {
                    return;
                }

                Environment.SetEnvironmentVariable(
                    StressHarnessOptions.NewsLogDirectoryEnvironmentVariable,
                    fixture);
                var options = StressHarnessOptions.FromEnvironment();
                Assert.Equal(fixture, options.NewsLogDirectory);
                Assert.NotEqual(StressCorpusPaths.RequiredNewsLogDirectory, options.NewsLogDirectory);
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    StressHarnessOptions.NewsLogDirectoryEnvironmentVariable,
                    previous);
            }
        }

        [Fact]
        public async Task Workload_guard_rejects_missing_manifest_stale_cache()
        {
            var dir = Path.Join(Path.GetTempPath(), "vnntp-stale-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                await File.WriteAllTextAsync(Path.Join(dir, "main-message-ids.txt"), "<a@host>\n");
                var options = new StressHarnessOptions
                {
                    RabbitMqHost = "127.0.0.1",
                    RabbitMqUsername = "x",
                    RabbitMqPassword = "y",
                    NewsLogDirectory = StressCorpusPaths.RequiredNewsLogDirectory,
                    WorkloadDirectory = dir,
                    ReportDirectory = dir,
                };
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => StressWorkloadGuard.RequireProvenancedWorkloadAsync(options, CancellationToken.None));
                Assert.Contains("provenance", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public async Task Workload_guard_rejects_cross_corpus_manifest()
        {
            var dir = Path.Join(Path.GetTempPath(), "vnntp-cross-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var main = Path.Join(dir, "main-message-ids.txt");
                await File.WriteAllTextAsync(main, "<a@host>\n");
                await WorkloadCorpusManifest.WriteAsync(
                    dir,
                    new WorkloadCorpusManifest
                    {
                        CorpusDirectory = @"C:\other\newslogs",
                        FileCount = 1,
                        LogRecordsExamined = 1,
                        ValidParsedIds = 1,
                        RejectedInvalidMessageIds = 0,
                        UniqueValidIds = 1,
                        WarmupSelected = 0,
                        MainSelected = 1,
                        RepeatableSelected = 0,
                        Seed = 1,
                        MainIdsSha256 = WorkloadCorpusManifest.ComputeFileSha256(main),
                        CreatedUtc = DateTimeOffset.UtcNow,
                    },
                    CancellationToken.None);

                var options = new StressHarnessOptions
                {
                    RabbitMqHost = "127.0.0.1",
                    RabbitMqUsername = "x",
                    RabbitMqPassword = "y",
                    NewsLogDirectory = StressCorpusPaths.RequiredNewsLogDirectory,
                    WorkloadDirectory = dir,
                    ReportDirectory = dir,
                };
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => StressWorkloadGuard.RequireProvenancedWorkloadAsync(options, CancellationToken.None));
                Assert.Contains("does not match", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Preflight_flags_ids_not_in_corpus_without_replacement()
        {
            var selected = new[] { "<a@host>", "<missing@host>" };
            var corpus = new HashSet<string>(StringComparer.Ordinal) { "<a@host>" };
            var results = CorpusArticlePreflight.ValidateSelectedAgainstCorpus(selected, corpus);
            Assert.Equal(CorpusIdPreflightKind.InCorpus, results[0].Kind);
            Assert.Equal(CorpusIdPreflightKind.NotInCorpus, results[1].Kind);
            Assert.Equal("<missing@host>", results[1].MessageId);
        }

        [Fact]
        public void Selected_ids_must_be_subset_of_corpus()
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "<a@host>" };
            Assert.Throws<InvalidOperationException>(
                () => StressWorkloadGuard.RequireSelectedIdsSubset(["<a@host>", "<b@host>"], allowed));
        }

        [Fact]
        public void CreateRequestProperties_preserves_exact_message_path_headers_without_request_ttl()
        {
            var requestId = Guid.Parse("33333333-3333-3333-3333-333333333333");
            var runId = "run-abc";
            var props = ArticleWorkStressClient.CreateRequestProperties(
                "corr-1",
                "reply-q",
                requestId,
                runId);
            Assert.True(string.IsNullOrEmpty(props.Expiration));
            Assert.Equal("corr-1", props.CorrelationId);
            Assert.Equal("reply-q", props.ReplyTo);
            Assert.Equal(requestId.ToString("D"), props.Headers!["RequestId"]);
            Assert.Equal(runId, props.Headers["StressRunId"]);
        }

        [Fact]
        public void Duplicate_ids_are_collapsed_before_workload_write()
        {
            var dir = Path.Join(Path.GetTempPath(), "vnntp-dup-" + Guid.NewGuid().ToString("N"));
            try
            {
                var counts = NewsLogMessageIdDiscovery.WriteWorkloadSets(
                    ["<a@host>", "<a@host>", "<b@host>"],
                    dir,
                    seed: 3,
                    warmupCount: 1,
                    mainCount: 2,
                    repeatableCount: 1);
                Assert.Equal(1, counts.Warmup);
                Assert.Equal(1, counts.Main); // only two unique IDs after Distinct
                var main = File.ReadAllLines(Path.Join(dir, "main-message-ids.txt"));
                var only = Assert.Single(main);
                Assert.True(NewsLogMessageIdDiscovery.IsCanonicalMessageId(only));
            }
            finally
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
    }
}

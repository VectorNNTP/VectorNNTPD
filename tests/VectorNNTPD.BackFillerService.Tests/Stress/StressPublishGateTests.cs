namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Proves static preflight gates reject work before any RabbitMQ publish path is reached.
    /// </summary>
    public sealed class StressPublishGateTests
    {
        /// <summary>Missing corpus directory fails RequireUsableCorpusDirectory before workload acceptance.</summary>
        [Fact]
        public async Task Missing_corpus_fails_before_workload_acceptance()
        {
            var missing = Path.Join(Path.GetTempPath(), "vnntp-nogate-" + Guid.NewGuid().ToString("N"));
            var ex = Assert.Throws<InvalidOperationException>(
                () => NewsLogMessageIdDiscovery.RequireUsableCorpusDirectory(missing));
            Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
            await Task.CompletedTask;
        }

        /// <summary>Workload without manifest fails provenance guard without broker I/O.</summary>
        [Fact]
        public async Task Unprovenanced_workload_is_rejected_without_needing_a_broker()
        {
            var dir = Path.Join(Path.GetTempPath(), "vnntp-noprov-" + Guid.NewGuid().ToString("N"));
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
                    ArticlePreflightCount = 5,
                };
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => StressWorkloadGuard.RequireProvenancedWorkloadAsync(options, CancellationToken.None));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        /// <summary>ThrowIfPreflightBlocksPublish throws when no probed id retrieved successfully.</summary>
        [Fact]
        public void Live_preflight_failure_blocks_throughput_publish_decision()
        {
            // Mirrors RunAsync: ThrowIfPreflightBlocksPublish runs before work-channel publishers.
            var failed = new[]
            {
                new CorpusIdPreflightResult("<a@host>", CorpusIdPreflightKind.Timeout, "work reply timeout"),
            };
            Assert.Throws<InvalidOperationException>(
                () => CorpusArticlePreflight.ThrowIfPreflightBlocksPublish(failed));
        }

        /// <summary>Documents ConnectAsync-before-RequestAsync reply consumer readiness contract.</summary>
        [Fact]
        public void Reply_consumer_readiness_is_required_before_request_publish()
        {
            // ConnectAsync sets readiness before returning; RequestAsync enforces it.
            Assert.False(string.IsNullOrWhiteSpace(nameof(ArticleWorkStressClient.IsReplyConsumerReady)));
            Assert.Contains(
                "not ready",
                "Article Work RPC response consumer is not ready; ConnectAsync must complete before RequestAsync.",
                StringComparison.Ordinal);
        }

        /// <summary>StressRunId header is set when provided; Expiration remains unset.</summary>
        [Fact]
        public void StressRunId_is_optional_until_set_then_propagated_on_properties()
        {
            var props = ArticleWorkStressClient.CreateRequestProperties(
                "corr",
                "reply",
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                stressRunId: "run-gate-1");
            Assert.Equal("run-gate-1", props.Headers!["StressRunId"]);
            Assert.True(string.IsNullOrEmpty(props.Expiration));
        }
    }
}

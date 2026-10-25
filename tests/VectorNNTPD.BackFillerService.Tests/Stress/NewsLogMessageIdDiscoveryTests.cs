namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    public sealed class NewsLogMessageIdDiscoveryTests
    {
        [Theory]
        [InlineData(
            "Oct 17 00:00:00.026 + giganews <ebb7ba983a25481087a6b20e26d0c2df@ngPost> 740121 ?",
            '+',
            "giganews",
            "<ebb7ba983a25481087a6b20e26d0c2df@ngPost>",
            740121L)]
        [InlineData(
            "Oct 17 00:00:00.075 j giganews <OvPrSpByQoTtFnSyVrIgIyVf-1792185621388@PRiVATE> 741054 newsgroup not carried: x",
            'j',
            "giganews",
            "<OvPrSpByQoTtFnSyVrIgIyVf-1792185621388@PRiVATE>",
            741054L)]
        [InlineData(
            "Oct 17 00:00:01.000 - eweka <abc@example.test> 12 fail",
            '-',
            "eweka",
            "<abc@example.test>",
            12L)]
        public void TryParseLine_accepts_known_formats(
            string line,
            char status,
            string provider,
            string messageId,
            long size)
        {
            Assert.True(NewsLogMessageIdDiscovery.TryParseLine(line.AsSpan(), out var entry));
            Assert.Equal(status, entry.Status);
            Assert.Equal(provider, entry.Provider);
            Assert.Equal(messageId, entry.MessageId);
            Assert.Equal(size, entry.LoggedSize);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not a log line")]
        [InlineData("Oct 17 00:00:00.026 + giganews missing-brackets 1")]
        public void TryParseLine_rejects_malformed(string line)
        {
            Assert.False(NewsLogMessageIdDiscovery.TryParseLine(line.AsSpan(), out _));
        }

        [Fact]
        public async Task DiscoverAsync_streams_and_deduplicates()
        {
            var root = Path.Join(Path.GetTempPath(), "vnntp-newslog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var path = Path.Join(root, "sample.log");
                await File.WriteAllLinesAsync(
                    path,
                    [
                        "Oct 17 00:00:00.026 + giganews <a@host> 10 ?",
                        "Oct 17 00:00:00.027 + giganews <a@host> 10 ?",
                        "Oct 17 00:00:00.028 + eweka <b@host> 20 ?",
                        "garbage",
                    ]);

                var unique = new HashSet<string>(StringComparer.Ordinal);
                var stats = await NewsLogMessageIdDiscovery.DiscoverAsync(root, unique);
                Assert.Equal(1, stats.FileCount);
                Assert.Equal(4, stats.TotalLogEntries);
                Assert.Equal(3, stats.ValidIds);
                Assert.Equal(2, stats.UniqueIds);
                Assert.Equal(1, stats.MalformedEntries);
                Assert.Equal(0, stats.RejectedInvalidMessageIds);
                Assert.Equal(2, stats.ProviderGiganews);
                Assert.Equal(1, stats.ProviderEweka);
                Assert.Contains("<a@host>", unique);
                Assert.Contains("<b@host>", unique);

                var workloadDir = Path.Join(root, "workload");
                var partitions = NewsLogMessageIdDiscovery.WriteWorkloadSets(
                    unique,
                    workloadDir,
                    seed: 7,
                    warmupCount: 1,
                    mainCount: 1,
                    repeatableCount: 1);
                Assert.Equal(1, partitions.Warmup);
                Assert.Equal(1, partitions.Main);
                Assert.True(File.Exists(Path.Join(workloadDir, "warmup-message-ids.txt")));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void RequireUsableCorpusDirectory_fails_when_missing()
        {
            var missing = Path.Join(Path.GetTempPath(), "vnntp-missing-" + Guid.NewGuid().ToString("N"));
            var ex = Assert.Throws<InvalidOperationException>(
                () => NewsLogMessageIdDiscovery.RequireUsableCorpusDirectory(missing));
            Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(StressCorpusPaths.RequiredNewsLogDirectory, ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RequireUsableCorpusDirectory_fails_when_empty()
        {
            var root = Path.Join(Path.GetTempPath(), "vnntp-empty-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(
                    () => NewsLogMessageIdDiscovery.RequireUsableCorpusDirectory(root));
                Assert.Contains("no *.log", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task DiscoverAsync_rejects_non_canonical_message_ids_without_fabricating()
        {
            var root = Path.Join(Path.GetTempPath(), "vnntp-badid-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                await File.WriteAllLinesAsync(
                    Path.Join(root, "sample.log"),
                    [
                        // Quoted local-part is not RFC 5536 msg-id; must not enter the unique set.
                        "Oct 17 00:00:00.026 + giganews <\"quoted\"@host> 10 ?",
                        "Oct 17 00:00:00.027 + giganews <good@host> 11 ?",
                    ]);

                var unique = new HashSet<string>(StringComparer.Ordinal);
                var stats = await NewsLogMessageIdDiscovery.DiscoverAsync(root, unique);
                Assert.Equal(1, stats.ValidIds);
                Assert.Equal(1, stats.UniqueIds);
                Assert.True(stats.RejectedInvalidMessageIds >= 1);
                Assert.Contains("<good@host>", unique);
                Assert.DoesNotContain("<\"quoted\"@host>", unique);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task DiscoverAsync_fails_when_corpus_yields_no_usable_ids()
        {
            var root = Path.Join(Path.GetTempPath(), "vnntp-nousable-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                await File.WriteAllLinesAsync(
                    Path.Join(root, "sample.log"),
                    ["garbage", "still garbage"]);
                var unique = new HashSet<string>(StringComparer.Ordinal);
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => NewsLogMessageIdDiscovery.DiscoverAsync(root, unique));
                Assert.Contains("no usable Message-IDs", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Empty(unique);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void WriteWorkloadSets_is_reproducible_for_same_seed()
        {
            string[] source = ["<c@host>", "<a@host>", "<b@host>"];
            var dir1 = Path.Join(Path.GetTempPath(), "vnntp-w1-" + Guid.NewGuid().ToString("N"));
            var dir2 = Path.Join(Path.GetTempPath(), "vnntp-w2-" + Guid.NewGuid().ToString("N"));
            try
            {
                NewsLogMessageIdDiscovery.WriteWorkloadSets(source, dir1, seed: 42, 1, 2, 1);
                NewsLogMessageIdDiscovery.WriteWorkloadSets(source, dir2, seed: 42, 1, 2, 1);
                Assert.Equal(
                    File.ReadAllText(Path.Join(dir1, "main-message-ids.txt")),
                    File.ReadAllText(Path.Join(dir2, "main-message-ids.txt")));
            }
            finally
            {
                if (Directory.Exists(dir1))
                {
                    Directory.Delete(dir1, recursive: true);
                }

                if (Directory.Exists(dir2))
                {
                    Directory.Delete(dir2, recursive: true);
                }
            }
        }

        [Fact]
        public void WriteWorkloadSets_fails_on_empty_input_without_synthetic_fallback()
        {
            var dir = Path.Join(Path.GetTempPath(), "vnntp-wempty-" + Guid.NewGuid().ToString("N"));
            var ex = Assert.Throws<InvalidOperationException>(
                () => NewsLogMessageIdDiscovery.WriteWorkloadSets(
                    Array.Empty<string>(),
                    dir,
                    seed: 1,
                    warmupCount: 1,
                    mainCount: 1,
                    repeatableCount: 1));
            Assert.Contains("no Message-IDs", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}

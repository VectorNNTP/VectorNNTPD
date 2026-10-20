using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Tests.Fixtures;

namespace VectorNNTPD.BackFillerService.Tests.Configuration
{
    /// <summary>
    /// Unit-tests Linux and Windows physical memory parsing used by retention validation.
    /// </summary>

    public sealed class OsPhysicalMemoryProviderTests
    {
        /// <summary>
        /// Contract: ParseLinuxMemTotalBytes converts MemTotal kibibytes to bytes.
        /// </summary>

        [Fact]
        public void ParseLinuxMemTotalBytes_converts_MemTotal_kibibytes_to_bytes()
        {
            using var reader = new StringReader(
                """
                MemTotal:       65536000 kB
                MemFree:           12345 kB
                MemAvailable:      54321 kB
                SwapTotal:       9999999 kB
                """);

            Assert.Equal(65536000L * 1024, OsPhysicalMemoryProvider.ParseLinuxMemTotalBytes(reader));
        }

        /// <summary>
        /// Contract: ParseLinuxMemTotalBytes ignores available free and swap.
        /// </summary>

        [Fact]
        public void ParseLinuxMemTotalBytes_ignores_available_free_and_swap()
        {
            using var reader = new StringReader(
                """
                MemAvailable:  104857600 kB
                MemFree:       104857600 kB
                SwapTotal:     104857600 kB
                MemTotal:       65536000 kB
                """);

            var bytes = OsPhysicalMemoryProvider.ParseLinuxMemTotalBytes(reader);
            Assert.Equal(65536000L * 1024, bytes);
            Assert.NotEqual(104857600L * 1024, bytes);
        }

        /// <summary>
        /// The operation throws InvalidOperationException with the expected contract.
        /// </summary>

        [Fact]
        public void ParseLinuxMemTotalBytes_fails_when_MemTotal_is_missing()
        {
            using var reader = new StringReader(
                """
                MemAvailable:  104857600 kB
                MemFree:           12345 kB
                """);

            var ex = Assert.Throws<InvalidOperationException>(
                () => OsPhysicalMemoryProvider.ParseLinuxMemTotalBytes(reader));
            Assert.Contains("MemTotal entry was not found", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The operation throws InvalidOperationException with the expected contract.
        /// </summary>

        [Fact]
        public void ParseLinuxMemTotalBytes_fails_when_MemTotal_format_is_invalid()
        {
            using var reader = new StringReader("MemTotal:       65536000 MB\n");

            var ex = Assert.Throws<InvalidOperationException>(
                () => OsPhysicalMemoryProvider.ParseLinuxMemTotalBytes(reader));
            Assert.Contains("format is invalid", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Contract: ReadLinuxMemTotalBytes reads MemTotal from a file.
        /// </summary>

        [Fact]
        public void ReadLinuxMemTotalBytes_reads_MemTotal_from_a_file()
        {
            var path = Path.Join(Path.GetTempPath(), "vectornntp-meminfo-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(
                path,
                """
                MemAvailable:      11111 kB
                MemTotal:       65536000 kB
                MemFree:           22222 kB
                """);
            try
            {
                Assert.Equal(65536000L * 1024, OsPhysicalMemoryProvider.ReadLinuxMemTotalBytes(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// The operation throws InvalidOperationException with the expected contract.
        /// </summary>

        [Fact]
        public void ReadLinuxMemTotalBytes_fails_when_the_file_is_missing()
        {
            var path = Path.Join(Path.GetTempPath(), "vectornntp-meminfo-missing-" + Guid.NewGuid().ToString("N"));
            var ex = Assert.Throws<InvalidOperationException>(
                () => OsPhysicalMemoryProvider.ReadLinuxMemTotalBytes(path));
            Assert.Contains("file does not exist", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Contract: SelectWindowsTotalPhysicalMemoryBytes uses ullTotalPhys not available.
        /// </summary>

        [Fact]
        public void SelectWindowsTotalPhysicalMemoryBytes_uses_ullTotalPhys_not_available()
        {
            const ulong totalPhys = 64UL * 1024 * 1024 * 1024;
            const ulong availPhys = 8UL * 1024 * 1024 * 1024;

            var selected = OsPhysicalMemoryProvider.SelectWindowsTotalPhysicalMemoryBytes(totalPhys, availPhys);

            Assert.Equal((long)totalPhys, selected);
            Assert.NotEqual((long)availPhys, selected);
        }

        /// <summary>
        /// The operation throws InvalidOperationException with the expected contract.
        /// </summary>

        [Fact]
        public void SelectWindowsTotalPhysicalMemoryBytes_fails_when_ullTotalPhys_is_zero()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => OsPhysicalMemoryProvider.SelectWindowsTotalPhysicalMemoryBytes(0, 8UL * 1024 * 1024 * 1024));
            Assert.Contains("ullTotalPhys", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Contract: Production provider type is not gc based.
        /// </summary>

        [Fact]
        public void Production_provider_type_is_not_gc_based()
        {
            Assert.Equal("OsPhysicalMemoryProvider", typeof(OsPhysicalMemoryProvider).Name);
            Assert.DoesNotContain("Gc", typeof(OsPhysicalMemoryProvider).Name, StringComparison.Ordinal);
            Assert.Null(typeof(OsPhysicalMemoryProvider).Assembly.GetType(
                "VectorNNTPD.BackFillerService.Configuration.GcPhysicalMemoryProvider"));
        }

        /// <summary>
        /// Invalid or incomplete configuration is rejected with an expected failure message.
        /// </summary>

        [Fact]
        public void Eighty_percent_guard_uses_injected_physical_memory_not_gc()
        {
            var options = BackFillerTestOptions.CreateValid();
            // 8 GiB injected host → truncated 80% ceiling is 6 GiB; retention of 7 must fail the ceiling check.
            options.ArticleRetention.MaximumRetainedPayloadGigabytes = 7;
            var result = BackFillerTestOptions
                .CreateValidator(memory: new FakePhysicalMemoryProvider(BackFillerArticleRetentionOptions.MinimumPhysicalMemoryBytes))
                .Validate(null, options);

            Assert.True(result.Failed);
            Assert.Contains(
                result.Failures!,
                static f => f.Contains("80% physical-memory ceiling (6 GiB)", StringComparison.Ordinal));
            Assert.DoesNotContain(
                result.Failures!,
                static f => f.Contains("of physical RAM; detected", StringComparison.Ordinal));
        }

        /// <summary>
        /// Contract: Configured retention capacity is independent of physical memory.
        /// </summary>

        [Fact]
        public void Configured_retention_capacity_is_independent_of_physical_memory()
        {
            var options = BackFillerTestOptions.CreateValid();
            options.ArticleRetention.MaximumRetainedPayloadGigabytes = 4;

            Assert.True(
                BackFillerTestOptions.CreateValidator(memory: new FakePhysicalMemoryProvider(16L * 1024 * 1024 * 1024))
                    .Validate(null, options)
                    .Succeeded);
            Assert.True(
                BackFillerTestOptions.CreateValidator(memory: new FakePhysicalMemoryProvider(128L * 1024 * 1024 * 1024))
                    .Validate(null, options)
                    .Succeeded);

            var runtimeFrom16 = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb());
            options.ArticleRetention.MaximumRetainedPayloadGigabytes = 4;
            var runtimeFrom128 = BackFillerRuntimeOptionsFactory.Create(
                options,
                BackFillerTestOptions.CreateValidNntpDb());

            Assert.Equal(4L * 1024 * 1024 * 1024, runtimeFrom16.ArticleRetention.MaximumRetainedPayloadBytes);
            Assert.Equal(4L * 1024 * 1024 * 1024, runtimeFrom128.ArticleRetention.MaximumRetainedPayloadBytes);
        }
    }
}

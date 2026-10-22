using Microsoft.Extensions.Configuration;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Storage.SegmentPools;
using VectorNNTPD.StorageService.Tests.Fixtures;

namespace VectorNNTPD.StorageService.Tests.Configuration
{

    public sealed class SegmentPoolsOptionsTests
    {
        [Fact]
        public void Defaults_are_85_79_75_and_spool_cache()
        {
            var options = new SegmentPoolsOptions();
            Assert.Equal(85, options.CriticalPercent);
            Assert.Equal(79, options.HighPercent);
            Assert.Equal(75, options.LowPercent);
            Assert.Equal(["spool/cache"], options.Directories);
        }

        [Fact]
        public void Json_section_binds_thresholds_and_directories()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["StorageServer:SegmentPools:CriticalPercent"] = "90",
                    ["StorageServer:SegmentPools:HighPercent"] = "80",
                    ["StorageServer:SegmentPools:LowPercent"] = "70",
                    ["StorageServer:SegmentPools:Directories:0"] = "spool/cache",
                    ["StorageServer:SegmentPools:Directories:1"] = "spool/other",
                })
                .Build();
            var options = new StorageServerOptions();
            configuration.GetSection("StorageServer").Bind(options);
            SegmentPoolsOptions.ReplaceConfiguredDirectories(options, configuration);

            Assert.Equal(90, options.SegmentPools.CriticalPercent);
            Assert.Equal(80, options.SegmentPools.HighPercent);
            Assert.Equal(70, options.SegmentPools.LowPercent);
            Assert.Equal(["spool/cache", "spool/other"], options.SegmentPools.Directories);
        }

        [Fact]
        public void Explicit_empty_directory_array_is_not_replaced_with_the_default()
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                """{"StorageServer":{"SegmentPools":{"Directories":[]}}}"""));
            var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
            var options = StorageServerTestOptions.CreateValid();
            configuration.GetSection("StorageServer").Bind(options);
            SegmentPoolsOptions.ReplaceConfiguredDirectories(options, configuration);
            Assert.Empty(options.SegmentPools.Directories);
            var result = new StorageServerOptionsValidator().Validate(null, options);
            Assert.Contains(result.Failures!, static failure => failure.Contains("Directories", StringComparison.Ordinal));
        }

        [Fact]
        public void Omitted_section_keeps_the_defaults()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["StorageServer:ServerId"] = "1" })
                .Build();
            var options = new StorageServerOptions();
            configuration.GetSection("StorageServer").Bind(options);

            Assert.Equal(85, options.SegmentPools.CriticalPercent);
            Assert.Equal(79, options.SegmentPools.HighPercent);
            Assert.Equal(75, options.SegmentPools.LowPercent);
            Assert.Equal(["spool/cache"], options.SegmentPools.Directories);
        }

        [Fact]
        public void Shipped_json_contains_the_segment_pool_section()
        {
            var root = FindRepositoryRoot();
            var json = File.ReadAllText(Path.Join(root, "src", "VectorNNTPD.StorageService", "VectorNNTPD.StorageService.json"));
            Assert.Contains("\"SegmentPools\"", json, StringComparison.Ordinal);
            Assert.Contains("\"CriticalPercent\": 85", json, StringComparison.Ordinal);
            Assert.Contains("\"HighPercent\": 79", json, StringComparison.Ordinal);
            Assert.Contains("\"LowPercent\": 75", json, StringComparison.Ordinal);
            Assert.Contains("\"spool/cache\"", json, StringComparison.Ordinal);
            Assert.Contains("\"BindPortTls\": 1191", json, StringComparison.Ordinal);
            Assert.Contains("198.18.0.66", json, StringComparison.Ordinal);
        }

        [Fact]
        public void Relative_directory_resolves_against_the_supplied_base_not_another_directory()
        {
            var root = Directory.CreateTempSubdirectory("vectornntp-base-");
            var other = Directory.CreateTempSubdirectory("vectornntp-other-");
            try
            {
                var resolved = ApplicationLocalPath.ResolveApplicationLocalPath("spool/cache", root.FullName);
                var normalizedRoot = SegmentPoolPaths.Normalize(root.FullName);
                Assert.StartsWith(normalizedRoot, resolved, SegmentPoolPaths.Comparison);
                Assert.EndsWith(Path.Join("spool", "cache"), resolved, SegmentPoolPaths.Comparison);
                Assert.DoesNotContain(SegmentPoolPaths.Normalize(other.FullName), resolved, SegmentPoolPaths.Comparison);
            }
            finally
            {
                root.Delete(recursive: true);
                other.Delete(recursive: true);
            }
        }

        [Fact]
        public void Default_thresholds_pass_validation()
        {
            var result = new StorageServerOptionsValidator().Validate(null, StorageServerTestOptions.CreateValid());
            Assert.True(result.Succeeded);
        }

        [Theory]
        [InlineData(101, 79, 75, "CriticalPercent")]
        [InlineData(85, -1, 75, "HighPercent")]
        [InlineData(85, 79, 200, "LowPercent")]
        public void Percentages_outside_0_to_100_fail(int critical, int high, int low, string key)
        {
            var options = StorageServerTestOptions.CreateValid();
            options.SegmentPools.CriticalPercent = critical;
            options.SegmentPools.HighPercent = high;
            options.SegmentPools.LowPercent = low;
            var result = new StorageServerOptionsValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, failure => failure.Contains(key, StringComparison.Ordinal) && failure.Contains("0–100", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(85, 85, 75)]
        [InlineData(85, 79, 79)]
        [InlineData(70, 80, 90)]
        public void Thresholds_must_be_strictly_ordered(int critical, int high, int low)
        {
            var options = StorageServerTestOptions.CreateValid();
            options.SegmentPools.CriticalPercent = critical;
            options.SegmentPools.HighPercent = high;
            options.SegmentPools.LowPercent = low;
            var result = new StorageServerOptionsValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, static failure => failure.Contains("LowPercent < HighPercent < CriticalPercent", StringComparison.Ordinal));
        }

        [Fact]
        public void Empty_directory_collection_fails()
        {
            var options = StorageServerTestOptions.CreateValid();
            options.SegmentPools.Directories = [];
            var result = new StorageServerOptionsValidator().Validate(null, options);
            Assert.Contains(result.Failures!, static failure => failure.Contains("Directories", StringComparison.Ordinal));
        }

        [Fact]
        public void Null_directory_collection_fails()
        {
            var options = StorageServerTestOptions.CreateValid();
            options.SegmentPools.Directories = null!;
            var result = new StorageServerOptionsValidator().Validate(null, options);
            Assert.Contains(result.Failures!, static failure => failure.Contains("Directories", StringComparison.Ordinal));
        }

        [Fact]
        public void Blank_directory_entry_fails()
        {
            var options = StorageServerTestOptions.CreateValid();
            options.SegmentPools.Directories = ["  "];
            var result = new StorageServerOptionsValidator().Validate(null, options);
            Assert.Contains(result.Failures!, static failure => failure.Contains("Directories:0", StringComparison.Ordinal));
        }

        [Fact]
        public void Invalid_path_characters_fail()
        {
            var options = StorageServerTestOptions.CreateValid();
            options.SegmentPools.Directories = ["spool/\0cache"];
            var result = new StorageServerOptionsValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, static failure => failure.Contains("Directories:0", StringComparison.Ordinal));
        }

        [Fact]
        public void Duplicate_canonical_paths_fail()
        {
            var root = Directory.CreateTempSubdirectory("vectornntp-pool-config-");
            try
            {
                var options = StorageServerTestOptions.CreateValid();
                options.SegmentPools.Directories = ["spool/cache", "spool/cache/"];
                var result = new StorageServerOptionsValidator(root.FullName).Validate(null, options);
                Assert.Contains(
                    result.Failures!,
                    static failure => failure.Contains("Directories:1", StringComparison.Ordinal)
                        && failure.Contains("duplicates", StringComparison.Ordinal));
            }
            finally
            {
                root.Delete(recursive: true);
            }
        }

        [Fact]
        public void Separator_and_case_equivalent_paths_fail_on_windows()
        {
            var root = Directory.CreateTempSubdirectory("vectornntp-pool-config-");
            try
            {
                var options = StorageServerTestOptions.CreateValid();
                options.SegmentPools.Directories = ["spool/cache", "spool\\cache"];
                var result = new StorageServerOptionsValidator(root.FullName).Validate(null, options);
                if (OperatingSystem.IsWindows())
                {
                    Assert.Contains(result.Failures!, static failure => failure.Contains("duplicates", StringComparison.Ordinal));
                }
                else
                {
                    Assert.DoesNotContain(result.Failures ?? [], static failure => failure.Contains("duplicates", StringComparison.Ordinal));
                }
            }
            finally
            {
                root.Delete(recursive: true);
            }
        }

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

            throw new InvalidOperationException("Could not locate VectorNNTPD.slnx from " + AppContext.BaseDirectory);
        }
    }
}

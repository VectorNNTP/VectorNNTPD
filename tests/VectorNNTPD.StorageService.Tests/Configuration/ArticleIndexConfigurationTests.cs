using Microsoft.Extensions.Configuration;
using VectorNNTPD.Common.Configuration;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Storage.ArticleIndex;
using VectorNNTPD.StorageService.Tests.Fixtures;
using VectorNNTPD.StorageService.Tests.Storage;

namespace VectorNNTPD.StorageService.Tests.Configuration
{

    public sealed class ArticleIndexConfigurationTests
    {
        [Fact]
        public void Omitted_DbDir_defaults_to_db()
        {
            var options = new StorageServerOptions();
            Assert.Equal("db", options.DbDir);
            Assert.Equal(StorageServerOptions.DefaultDbDir, options.DbDir);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["StorageServer:ServerId"] = "1" })
                .Build();
            var bound = new StorageServerOptions();
            configuration.GetSection("StorageServer").Bind(bound);
            Assert.Equal("db", bound.DbDir);
        }

        [Fact]
        public void Explicit_relative_and_absolute_directories_bind()
        {
            var absolute = Path.Combine(Path.GetTempPath(), "vectornntp-db-absolute");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["StorageServer:DbDir"] = "indexes/local",
                })
                .Build();
            var relative = new StorageServerOptions();
            configuration.GetSection("StorageServer").Bind(relative);
            Assert.Equal("indexes/local", relative.DbDir);

            var absoluteConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["StorageServer:DbDir"] = absolute })
                .Build();
            var bound = new StorageServerOptions();
            absoluteConfiguration.GetSection("StorageServer").Bind(bound);
            Assert.Equal(absolute, bound.DbDir);
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(absolute)),
                ApplicationLocalPath.ResolveApplicationLocalPath(bound.DbDir, AppContext.BaseDirectory));
        }

        [Fact]
        public void Empty_and_invalid_DbDir_fail_validation()
        {
            var empty = StorageServerTestOptions.CreateValid();
            empty.DbDir = " ";
            var emptyResult = new StorageServerOptionsValidator().Validate(null, empty);
            Assert.True(emptyResult.Failed);
            Assert.NotNull(emptyResult.Failures);
            Assert.Contains(
                emptyResult.Failures,
                static failure => failure.Contains("StorageServer:DbDir", StringComparison.Ordinal));

            // '|' is invalid on Windows but legal on Unix. Use a character that
            // Path.GetInvalidPathChars reports on every platform (NUL).
            var invalid = StorageServerTestOptions.CreateValid();
            invalid.DbDir = "db" + Path.GetInvalidPathChars()[0] + "not-a-path";
            var invalidResult = new StorageServerOptionsValidator().Validate(null, invalid);
            Assert.True(invalidResult.Failed);
            Assert.NotNull(invalidResult.Failures);
            Assert.Contains(
                invalidResult.Failures,
                static failure => failure.Contains("invalid path characters", StringComparison.Ordinal));
        }

        [Fact]
        public void DbDir_nested_with_a_spool_fails_validation()
        {
            var spool = Path.Combine(Path.GetTempPath(), "vectornntp-spool-overlap-" + Guid.NewGuid().ToString("N"));
            var options = StorageServerTestOptions.CreateValid();
            options.SegmentPools.Directories = [spool];
            options.DbDir = spool;
            var same = new StorageServerOptionsValidator().Validate(null, options);
            Assert.Contains(same.Failures!, static failure => failure.Contains("does not fall back", StringComparison.Ordinal));

            options.DbDir = Path.Combine(spool, "db");
            var child = new StorageServerOptionsValidator().Validate(null, options);
            Assert.Contains(child.Failures!, static failure => failure.Contains("StorageServer:DbDir must not", StringComparison.Ordinal));
        }

        [Fact]
        public void Shipped_json_sets_DbDir_to_db()
        {
            var root = FindRepositoryRoot();
            var json = File.ReadAllText(Path.Combine(root, "src", "VectorNNTPD.StorageService", "VectorNNTPD.StorageService.json"));
            Assert.Contains("\"DbDir\": \"db\"", json, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Startup_creates_a_relative_directory_under_the_binary_directory()
        {
            var name = "vectornntp-db-" + Guid.NewGuid().ToString("N");
            var expected = ApplicationLocalPath.ResolveApplicationLocalPath(name, AppContext.BaseDirectory);
            var spool = ArticleIndexTestSupport.NewDirectory();
            var service = ArticleIndexTestSupport.Create(name, ArticleIndexTestSupport.Catalog(spool));
            try
            {
                await service.StartAsync(CancellationToken.None);
                Assert.True(service.IsReady);
                Assert.Equal(expected, service.DatabaseDirectory);
                Assert.True(Directory.Exists(expected));
                Assert.StartsWith(expected, service.DatabasePath, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("article-index-", service.DatabasePath, StringComparison.Ordinal);
                Assert.True(File.Exists(service.DatabasePath));
                Assert.True(File.Exists(Path.Combine(spool, ArticleIndexSettings.SpoolIdentityFileName)));
                var path = service.DatabasePath;
                await service.StopAsync(CancellationToken.None);
                var again = ArticleIndexTestSupport.Create(name, ArticleIndexTestSupport.Catalog(spool));
                await again.StartAsync(CancellationToken.None);
                Assert.Equal(path, again.DatabasePath);
                await again.StopAsync(CancellationToken.None);
            }
            finally
            {
                if (service.Execution is not null)
                {
                    await service.StopAsync(CancellationToken.None);
                }

                ArticleIndexTestSupport.Delete(expected);
                ArticleIndexTestSupport.Delete(spool);
            }
        }

        [Fact]
        public async Task Startup_fails_when_DbDir_is_a_file_and_does_not_substitute_a_spool()
        {
            var file = Path.Combine(Path.GetTempPath(), "vectornntp-db-file-" + Guid.NewGuid().ToString("N"));
            await File.WriteAllBytesAsync(file, [1]);
            var service = ArticleIndexTestSupport.Create(file);
            try
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
                Assert.Contains(file, error.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("does not fall back", error.Message, StringComparison.Ordinal);
                Assert.False(service.IsReady);
            }
            finally
            {
                ArticleIndexTestSupport.Delete(file);
            }
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "VectorNNTPD.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Repository root was not found.");
        }
    }
}

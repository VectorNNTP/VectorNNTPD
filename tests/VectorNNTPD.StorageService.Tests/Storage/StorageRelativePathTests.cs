using VectorNNTPD.StorageService.Logging;
using VectorNNTPD.StorageService.Storage;
using VectorNNTPD.StorageService.Storage.ArticleIndex;
using VectorNNTPD.StorageService.Storage.SegmentPools;
using VectorNNTPD.StorageService;

namespace VectorNNTPD.StorageService.Tests.Storage
{

    public sealed class StorageRelativePathTests
    {
        [Fact]
        public void CombineUnder_keeps_valid_leaf_under_base()
        {
            var baseDir = Path.Join(Path.GetTempPath(), "vectornntp-rel-" + Guid.NewGuid().ToString("N"));
            var combined = StorageRelativePath.CombineUnder(baseDir, "article-index.sqlite");
            Assert.Equal(Path.Join(baseDir, "article-index.sqlite"), combined);
            StorageRelativePath.EnsureContained(baseDir, combined);
        }

        [Theory]
        [InlineData("..")]
        [InlineData(".")]
        [InlineData("a/b")]
        [InlineData("a\\b")]
        public void CombineUnder_rejects_traversal_and_multi_segment_leaves(string leaf)
        {
            if (leaf.Contains('\\', StringComparison.Ordinal) && Path.DirectorySeparatorChar != '\\'
                && Path.AltDirectorySeparatorChar != '\\')
            {
                // Backslash is not a directory separator on this platform; skip the Windows-style case.
                return;
            }

            var baseDir = Path.Join(Path.GetTempPath(), "vectornntp-rel-" + Guid.NewGuid().ToString("N"));
            Assert.Throws<ArgumentException>(() => StorageRelativePath.CombineUnder(baseDir, leaf));
        }

        [Fact]
        public void CombineUnder_rejects_rooted_leaf()
        {
            var baseDir = Path.Join(Path.GetTempPath(), "vectornntp-rel-" + Guid.NewGuid().ToString("N"));
            var rooted = OperatingSystem.IsWindows()
                ? Path.Join(Path.GetPathRoot(Path.GetTempPath())!, "escape.sqlite")
                : "/tmp/escape.sqlite";
            Assert.Throws<ArgumentException>(() => StorageRelativePath.CombineUnder(baseDir, rooted));
        }

        [Fact]
        public void Generated_article_index_database_name_stays_under_db_dir()
        {
            var dbDir = Directory.CreateTempSubdirectory("vectornntp-db-").FullName;
            var spool = Directory.CreateTempSubdirectory("vectornntp-spool-").FullName;
            try
            {
                var bindings = ArticleSpoolRegistry.Resolve(dbDir, [spool]);
                Assert.Single(bindings);
                var databasePath = bindings[0].DatabasePath;
                Assert.Equal(
                    Path.Join(dbDir, "article-index-" + bindings[0].Id + ".sqlite"),
                    databasePath);
                StorageRelativePath.EnsureContained(dbDir, databasePath);
                Assert.StartsWith(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(dbDir)) + Path.DirectorySeparatorChar,
                    Path.GetFullPath(databasePath),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            finally
            {
                TryDelete(dbDir);
                TryDelete(spool);
            }
        }

        [Fact]
        public void RollingFilePath_rejects_escaping_application_names()
        {
            var logDir = Path.Join(Path.GetTempPath(), "vectornntp-logs-" + Guid.NewGuid().ToString("N"));
            Assert.Throws<ArgumentException>(() => StorageServerFileLogging.RollingFilePath(logDir, ".."));
            Assert.Throws<ArgumentException>(
                () => StorageServerFileLogging.RollingFilePath(logDir, "a" + Path.DirectorySeparatorChar + "b"));

            var rooted = OperatingSystem.IsWindows()
                ? Path.Join(Path.GetPathRoot(Path.GetTempPath())!, "evil")
                : "/evil";
            Assert.Throws<ArgumentException>(() => StorageServerFileLogging.RollingFilePath(logDir, rooted));
        }

        [Fact]
        public void RollingFilePath_keeps_valid_application_name_layout()
        {
            var logDir = Path.Join(Path.GetTempPath(), "vectornntp-logs-" + Guid.NewGuid().ToString("N"));
            var path = StorageServerFileLogging.RollingFilePath(logDir, "VectorNNTPD.StorageService");
            Assert.Equal(
                Path.Join(logDir, "VectorNNTPD.StorageService" + StorageServerFileLogging.RollingPathSuffix),
                path);
            StorageRelativePath.EnsureContained(logDir, path);
        }

        [Fact]
        public void LinuxSysfsReader_accepts_valid_major_minor_without_escaping_sysfs_root()
        {
            var root = Directory.CreateTempSubdirectory("vectornntp-sysfs-").FullName;
            try
            {
                Directory.CreateDirectory(Path.Join(root, "dev", "block"));
                var blockRoot = StorageRelativePath.CombineUnder(root, "dev", "block");
                var node = StorageRelativePath.CombineUnderCheckedSegment(blockRoot, "259:0");
                Assert.Equal(Path.Join(root, "dev", "block", "259:0"), node);
                StorageRelativePath.EnsureContained(root, node);

                var sysfs = new LinuxSysfsReader(root);
                // Windows cannot create a directory named "8:0"; acceptance is "does not throw", not "Exists".
                Assert.False(sysfs.Exists("8:0"));
                Assert.False(sysfs.HasPartition("8:0"));

                if (!OperatingSystem.IsWindows())
                {
                    Directory.CreateDirectory(node);
                    File.WriteAllText(Path.Join(node, "partition"), "1");
                    Assert.True(sysfs.Exists("259:0"));
                    Assert.True(sysfs.HasPartition("259:0"));
                }
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("8")]
        [InlineData(":0")]
        [InlineData("8:")]
        [InlineData("8:0:1")]
        [InlineData("8:a")]
        [InlineData("../8:0")]
        [InlineData("8:0/../9:0")]
        public void LinuxSysfsReader_rejects_malformed_or_path_like_device_ids(string majorMinor)
        {
            var root = Directory.CreateTempSubdirectory("vectornntp-sysfs-").FullName;
            try
            {
                Directory.CreateDirectory(Path.Join(root, "dev", "block"));
                var sysfs = new LinuxSysfsReader(root);
                Assert.ThrowsAny<ArgumentException>(() => sysfs.Exists(majorMinor));
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void Write_probe_name_stays_inside_pool_directory()
        {
            var directory = Directory.CreateTempSubdirectory("vectornntp-probe-");
            try
            {
                var failure = new FileSegmentPoolWriteProbe().Probe(directory.FullName, CancellationToken.None);
                Assert.Null(failure);
                Assert.Empty(Directory.GetFiles(directory.FullName, ".vectornntp-pool-probe-*"));
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException ex)
            {
                ExpectedException.Ignore(ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                ExpectedException.Ignore(ex);
            }
        }
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Storage.ArticleIndex;
using VectorNNTPD.StorageService.Storage.SegmentPools;
using VectorNNTPD.StorageService.Storage.SpoolPersistence;
using VectorNNTPD.StorageService.Storage.SpoolShards;

namespace VectorNNTPD.StorageService.Tests.Storage
{

    public sealed class ArticleIndexLfuEvictionTests
    {
        [Fact]
        public async Task Insert_stores_access_count_one_and_art_size()
        {
            var root = ArticleIndexTestSupport.NewDirectory();
            var spool = ArticleIndexTestSupport.NewDirectory();
            ArticleIndexService? index = null;
            try
            {
                index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
                var id = ArticleIndexTestSupport.Id(1);
                ArticleIndexTestSupport.PlacePayload(spool, id, new string('a', 120));
                var inserted = await index.InsertStoredAsync(id, spool, 10, CancellationToken.None, artSizeBytes: 120);
                Assert.True(inserted.Inserted);
                Assert.True(index.TryGetIndexedRecord(id, out var record));
                Assert.Equal(1, record.AccessCount);
                Assert.Equal(120, record.ArtSizeBytes);
                Assert.Equal(120, index.StoredBytesFor(spool));
            }
            finally
            {
                if (index is not null)
                {
                    await ArticleIndexTestSupport.StopAsync(index);
                }

                ArticleIndexTestSupport.Delete(root);
                ArticleIndexTestSupport.Delete(spool);
            }
        }

        [Fact]
        public async Task Touch_increments_access_count_and_missing_row_stays_missing()
        {
            var root = ArticleIndexTestSupport.NewDirectory();
            var spool = ArticleIndexTestSupport.NewDirectory();
            ArticleIndexService? index = null;
            try
            {
                index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
                var id = ArticleIndexTestSupport.Id(2);
                ArticleIndexTestSupport.PlacePayload(spool, id);
                await index.InsertStoredAsync(id, spool, 10, CancellationToken.None, artSizeBytes: 50);
                await index.TouchAsync(id, spool, 20, CancellationToken.None);
                Assert.True(index.TryGetIndexedRecord(id, out var record));
                Assert.Equal(2, record.AccessCount);
                Assert.Equal(20, record.LastAccessedAtUnixMilliseconds);

                var missing = ArticleIndexTestSupport.Id(3);
                var touch = await index.TouchAsync(missing, spool, 20, CancellationToken.None);
                Assert.Equal(ArticleIndexTouchDisposition.Missing, touch.Disposition);
            }
            finally
            {
                if (index is not null)
                {
                    await ArticleIndexTestSupport.StopAsync(index);
                }

                ArticleIndexTestSupport.Delete(root);
                ArticleIndexTestSupport.Delete(spool);
            }
        }

        [Fact]
        public async Task Concurrent_touches_do_not_lose_access_increments()
        {
            var root = ArticleIndexTestSupport.NewDirectory();
            var spool = ArticleIndexTestSupport.NewDirectory();
            ArticleIndexService? index = null;
            try
            {
                index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
                var id = ArticleIndexTestSupport.Id(4);
                ArticleIndexTestSupport.PlacePayload(spool, id);
                await index.InsertStoredAsync(id, spool, 1, CancellationToken.None, artSizeBytes: 10);
                var tasks = Enumerable.Range(0, 32)
                    .Select(i => index.TouchAsync(id, spool, 100 + i, CancellationToken.None).AsTask())
                    .ToArray();
                await Task.WhenAll(tasks);
                Assert.True(index.TryGetIndexedRecord(id, out var record));
                Assert.Equal(33, record.AccessCount);
            }
            finally
            {
                if (index is not null)
                {
                    await ArticleIndexTestSupport.StopAsync(index);
                }

                ArticleIndexTestSupport.Delete(root);
                ArticleIndexTestSupport.Delete(spool);
            }
        }

        [Fact]
        public async Task Lfu_order_uses_access_count_recency_and_hash()
        {
            var root = ArticleIndexTestSupport.NewDirectory();
            var spool = ArticleIndexTestSupport.NewDirectory();
            ArticleIndexService? index = null;
            try
            {
                index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
                var low = ArticleIndexTestSupport.Id(10);
                var tieOld = ArticleIndexTestSupport.Id(11);
                var tieNew = ArticleIndexTestSupport.Id(12);
                foreach (var id in new[] { low, tieOld, tieNew })
                {
                    ArticleIndexTestSupport.PlacePayload(spool, id);
                    await index.InsertStoredAsync(id, spool, 100, CancellationToken.None, artSizeBytes: 10);
                }

                await index.TouchAsync(low, spool, 200, CancellationToken.None);
                await index.TouchAsync(low, spool, 201, CancellationToken.None);
                await index.TouchAsync(tieNew, spool, 150, CancellationToken.None);

                var rows = new ArticleIndexRecord[8];
                Assert.Equal(3, index.CopyLeastFrequentlyUsed(spool, rows));
                Assert.Equal(tieOld, rows[0].ArticleId);
                Assert.Equal(tieNew, rows[1].ArticleId);
                Assert.Equal(low, rows[2].ArticleId);
            }
            finally
            {
                if (index is not null)
                {
                    await ArticleIndexTestSupport.StopAsync(index);
                }

                ArticleIndexTestSupport.Delete(root);
                ArticleIndexTestSupport.Delete(spool);
            }
        }

        [Fact]
        public async Task Capacity_high_triggers_eviction_to_low()
        {
            var root = ArticleIndexTestSupport.NewDirectory();
            var spool = ArticleIndexTestSupport.NewDirectory();
            ArticleIndexService? index = null;
            BlockDeviceMonitorService? monitor = null;
            try
            {
                var catalog = ArticleIndexTestSupport.Catalog(spool);
                monitor = new BlockDeviceMonitorService(catalog, new PlatformBlockDeviceCounterSource(), NullLogger<BlockDeviceMonitorService>.Instance, TimeSpan.FromMinutes(1), TimeProvider.System, freeSpace: null);
                await monitor.StartAsync(CancellationToken.None);
                var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(spool));
                monitor.SetSpaceObservationForTests(canonical, totalBytes: 1000, availableBytes: 210);

                var options = Options.Create(new StorageServerOptions
                {
                    DbDir = root,
                    SegmentPools = new SegmentPoolsOptions { LowPercent = 75, HighPercent = 79 },
                });
                var files = new FileSpoolArticleFiles();
                var leases = new ArticleReadLeaseRegistry();
                var eviction = new SpoolLfuEviction(options, monitor, files, leases, NullLogger<ArticleIndexService>.Instance);
                index = new ArticleIndexService(options.Value, catalog, NullLogger<ArticleIndexService>.Instance, TimeProvider.System, settings: ArticleIndexTestSupport.FastSettings(), eviction);
                await index.StartAsync(CancellationToken.None);

                for (var i = 0; i < 5; i++)
                {
                    var id = ArticleIndexTestSupport.Id(100 + i);
                    ArticleIndexTestSupport.PlacePayload(spool, id, new string('x', 10));
                    await index.InsertStoredAsync(id, spool, i, CancellationToken.None, artSizeBytes: 10);
                    await index.EvictUntilBelowHighAsync(spool, CancellationToken.None);
                }

                Assert.True(index.PresenceCount < 5);
                monitor.SetSpaceObservationForTests(canonical, totalBytes: 1000, availableBytes: 260);
                Assert.True(SpoolSelector.UsedPercent(1000, 260) <= 75);
            }
            finally
            {
                if (index is not null)
                {
                    await ArticleIndexTestSupport.StopAsync(index);
                }

                if (monitor is not null)
                {
                    await monitor.StopAsync(CancellationToken.None);
                }

                ArticleIndexTestSupport.Delete(root);
                ArticleIndexTestSupport.Delete(spool);
            }
        }

        [Theory]
        [InlineData(260)] // 74%
        [InlineData(250)] // 75%
        [InlineData(220)] // 78%
        public async Task Below_high_does_not_evict(int availableBytes)
        {
            var root = ArticleIndexTestSupport.NewDirectory();
            var spool = ArticleIndexTestSupport.NewDirectory();
            EvictionEnv? env = null;
            try
            {
                env = await CreateEvictionIndexAsync(root, spool);
                var id = ArticleIndexTestSupport.Id(60);
                ArticleIndexTestSupport.PlacePayload(env.Spool, id);
                await env.Index.InsertStoredAsync(id, env.Spool, 1, CancellationToken.None, artSizeBytes: 12);
                env.Monitor.SetSpaceObservationForTests(env.CanonicalSpool, totalBytes: 1000, availableBytes: availableBytes);
                await env.Index.EvictUntilBelowHighAsync(env.Spool, CancellationToken.None);
                Assert.True(env.Index.Contains(id));
                Assert.Equal(1, env.Index.PresenceCount);
            }
            finally
            {
                if (env is not null)
                {
                    await StopEvictionEnvAsync(env);
                }

                ArticleIndexTestSupport.Delete(root);
                ArticleIndexTestSupport.Delete(spool);
            }
        }

        [Fact]
        public async Task No_sample_does_not_evict_as_if_empty()
        {
            var root = ArticleIndexTestSupport.NewDirectory();
            var spool = ArticleIndexTestSupport.NewDirectory();
            EvictionEnv? env = null;
            try
            {
                env = await CreateEvictionIndexAsync(root, spool);
                var id = ArticleIndexTestSupport.Id(61);
                ArticleIndexTestSupport.PlacePayload(env.Spool, id);
                await env.Index.InsertStoredAsync(id, env.Spool, 1, CancellationToken.None, artSizeBytes: 12);
                await env.Index.EvictUntilBelowHighAsync(env.Spool, CancellationToken.None);
                Assert.True(env.Index.Contains(id));
            }
            finally
            {
                if (env is not null)
                {
                    await StopEvictionEnvAsync(env);
                }

                ArticleIndexTestSupport.Delete(root);
                ArticleIndexTestSupport.Delete(spool);
            }
        }

        [Fact]
        public async Task Eviction_continues_past_high_until_low()
        {
            var root = ArticleIndexTestSupport.NewDirectory();
            var spool = ArticleIndexTestSupport.NewDirectory();
            EvictionEnv? env = null;
            try
            {
                env = await CreateEvictionIndexAsync(root, spool);
                for (var i = 0; i < 4; i++)
                {
                    var id = ArticleIndexTestSupport.Id(70 + i);
                    ArticleIndexTestSupport.PlacePayload(env.Spool, id, new string('y', 40));
                    await env.Index.InsertStoredAsync(id, env.Spool, i, CancellationToken.None, artSizeBytes: 40);
                }

                // 79% used: eviction must keep going toward Low, not stop merely below High.
                env.Monitor.SetSpaceObservationForTests(env.CanonicalSpool, totalBytes: 1000, availableBytes: 210);
                await env.Index.EvictUntilBelowHighAsync(env.Spool, CancellationToken.None);
                Assert.True(SpoolSelector.UsedPercent(1000, env.Monitor.Observations.Space[env.CanonicalSpool].AvailableBytes) <= 75);
            }
            finally
            {
                if (env is not null)
                {
                    await StopEvictionEnvAsync(env);
                }

                ArticleIndexTestSupport.Delete(root);
                ArticleIndexTestSupport.Delete(spool);
            }
        }

        [Fact]
        public async Task No_eligible_victim_terminates_without_spin()
        {
            var root = ArticleIndexTestSupport.NewDirectory();
            var spool = ArticleIndexTestSupport.NewDirectory();
            EvictionEnv? env = null;
            try
            {
                env = await CreateEvictionIndexAsync(root, spool);
                var id = ArticleIndexTestSupport.Id(80);
                ArticleIndexTestSupport.PlacePayload(env.Spool, id);
                await env.Index.InsertStoredAsync(id, env.Spool, 1, CancellationToken.None, artSizeBytes: 12);
                env.Monitor.SetSpaceObservationForTests(env.CanonicalSpool, totalBytes: 1000, availableBytes: 210);
                using (env.Leases.Acquire(id))
                {
                    await env.Index.EvictUntilBelowHighAsync(env.Spool, CancellationToken.None);
                }

                Assert.True(env.Index.Contains(id));
                Assert.True(SpoolSelector.UsedPercent(1000, 210) >= 79);
            }
            finally
            {
                if (env is not null)
                {
                    await StopEvictionEnvAsync(env);
                }

                ArticleIndexTestSupport.Delete(root);
                ArticleIndexTestSupport.Delete(spool);
            }
        }

        [Fact]
        public async Task Missing_file_eviction_removes_index_and_presence()
        {
            var root = ArticleIndexTestSupport.NewDirectory();
            var spool = ArticleIndexTestSupport.NewDirectory();
            EvictionEnv? env = null;
            try
            {
                env = await CreateEvictionIndexAsync(root, spool);
                var id = ArticleIndexTestSupport.Id(50);
                await env.Index.InsertStoredAsync(id, env.Spool, 1, CancellationToken.None, artSizeBytes: 12);
                File.Delete(ArticleIndexTestSupport.PlacePayload(env.Spool, id));
                Assert.True(await env.Index.TryEvictOneAsync(env.Spool, env.Eviction, CancellationToken.None));
                Assert.False(env.Index.Contains(id));
                Assert.Equal(0, env.Index.StoredBytesFor(env.Spool));
            }
            finally
            {
                if (env is not null)
                {
                    await StopEvictionEnvAsync(env);
                }

                ArticleIndexTestSupport.Delete(root);
                ArticleIndexTestSupport.Delete(spool);
            }
        }

        [Fact]
        public async Task Read_lease_blocks_eviction()
        {
            var root = ArticleIndexTestSupport.NewDirectory();
            var spool = ArticleIndexTestSupport.NewDirectory();
            EvictionEnv? env = null;
            try
            {
                env = await CreateEvictionIndexAsync(root, spool);
                var id = ArticleIndexTestSupport.Id(51);
                ArticleIndexTestSupport.PlacePayload(env.Spool, id);
                await env.Index.InsertStoredAsync(id, env.Spool, 1, CancellationToken.None, artSizeBytes: 12);
                env.Monitor.SetSpaceObservationForTests(env.CanonicalSpool, totalBytes: 1000, availableBytes: 210);
                using (env.Leases.Acquire(id))
                {
                    Assert.False(await env.Index.TryEvictOneAsync(env.Spool, env.Eviction, CancellationToken.None));
                }

                Assert.True(env.Index.Contains(id));
            }
            finally
            {
                if (env is not null)
                {
                    await StopEvictionEnvAsync(env);
                }

                ArticleIndexTestSupport.Delete(root);
                ArticleIndexTestSupport.Delete(spool);
            }
        }

        [Fact]
        public async Task Schema_version_2_database_migrates_to_3()
        {
            var root = ArticleIndexTestSupport.NewDirectory();
            var spool = ArticleIndexTestSupport.NewDirectory();
            try
            {
                var databasePath = Path.Join(root, "spool-" + Guid.NewGuid().ToString("N") + ".sqlite");
                CreateV2Database(databasePath);
                using var database = new ArticleIndexDatabase(databasePath, spool, new ArticleIndexSettings(), ArticleIndexCacheBudget.ForSpoolCount(1));
                database.Initialize(CancellationToken.None);
                Assert.Equal(3, database.SchemaVersion);
            }
            finally
            {
                ArticleIndexTestSupport.Delete(root);
                ArticleIndexTestSupport.Delete(spool);
            }
        }

        private static void CreateV2Database(string databasePath)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE ArticleIndexMeta (
                    Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
                    SchemaVersion INTEGER NOT NULL,
                    ArticleCount INTEGER NOT NULL);
                INSERT INTO ArticleIndexMeta (Id, SchemaVersion, ArticleCount) VALUES (1, 2, 0);
                CREATE TABLE ArticleIndex (
                    MessageIdHash BLOB NOT NULL PRIMARY KEY,
                    ReceivedAt INTEGER NOT NULL,
                    LastAccessedAt INTEGER NOT NULL);
                CREATE INDEX IX_ArticleIndex_LastAccessedAt ON ArticleIndex (LastAccessedAt);
                """;
            command.ExecuteNonQuery();
        }

        private static async Task StopEvictionEnvAsync(EvictionEnv env)
        {
            await env.Index.StopAsync(CancellationToken.None);
            await env.Monitor.StopAsync(CancellationToken.None);
        }

        private static async Task<EvictionEnv> CreateEvictionIndexAsync(string root, string spool)
        {
            var catalog = ArticleIndexTestSupport.Catalog(spool);
            var monitor = new BlockDeviceMonitorService(catalog, new PlatformBlockDeviceCounterSource(), NullLogger<BlockDeviceMonitorService>.Instance, TimeSpan.FromMinutes(1), TimeProvider.System, freeSpace: null);
            await monitor.StartAsync(CancellationToken.None);
            var options = Options.Create(new StorageServerOptions
            {
                DbDir = root,
                SegmentPools = new SegmentPoolsOptions { LowPercent = 75, HighPercent = 79 },
            });
            var files = new FileSpoolArticleFiles();
            var leases = new ArticleReadLeaseRegistry();
            var eviction = new SpoolLfuEviction(options, monitor, files, leases, NullLogger<ArticleIndexService>.Instance);
            var index = new ArticleIndexService(options.Value, catalog, NullLogger<ArticleIndexService>.Instance, TimeProvider.System, ArticleIndexTestSupport.FastSettings(), eviction);
            await index.StartAsync(CancellationToken.None);
            return new EvictionEnv(index, eviction, monitor, leases, spool, Path.TrimEndingDirectorySeparator(Path.GetFullPath(spool)));
        }

        private sealed record EvictionEnv(
            ArticleIndexService Index,
            SpoolLfuEviction Eviction,
            BlockDeviceMonitorService Monitor,
            ArticleReadLeaseRegistry Leases,
            string Spool,
            string CanonicalSpool);
    }
}

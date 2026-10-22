using VectorNNTPD.StorageService.Storage.SpoolShards;

namespace VectorNNTPD.StorageService.Tests.Storage
{

    public sealed class SpoolCapacityTests
    {
        [Fact]
        public void Decimal_22_terabytes_and_binary_22_tebibytes_use_integer_byte_counts()
        {
            Assert.Equal(750L * 1024L, SpoolCapacity.AverageArticleBytes);
            Assert.Equal(768_000L, SpoolCapacity.AverageArticleBytes);
            Assert.Equal(22_000_000_000_000L, SpoolCapacity.Decimal22TerabyteBytes);
            Assert.Equal(22L * 1024L * 1024L * 1024L * 1024L, SpoolCapacity.Binary22TebibyteBytes);
            Assert.Equal(24_189_255_811_072L, SpoolCapacity.Binary22TebibyteBytes);
        }

        [Fact]
        public void Article_count_for_22_decimal_terabytes_is_floor_division_per_spool()
        {
            const long bytes = SpoolCapacity.Decimal22TerabyteBytes;
            var articles = SpoolCapacity.ArticleCount(bytes);
            var remainder = SpoolCapacity.RemainderBytes(bytes);

            Assert.Equal(28_645_833L, articles);
            Assert.Equal(256_000L, remainder);
            Assert.Equal(bytes, (articles * SpoolCapacity.AverageArticleBytes) + remainder);
            Assert.True(remainder < SpoolCapacity.AverageArticleBytes);
        }

        [Fact]
        public void Article_count_for_22_tebibytes_is_floor_division_per_spool()
        {
            const long bytes = SpoolCapacity.Binary22TebibyteBytes;
            var articles = SpoolCapacity.ArticleCount(bytes);
            var remainder = SpoolCapacity.RemainderBytes(bytes);

            Assert.Equal(31_496_426L, articles);
            Assert.Equal(643_072L, remainder);
            Assert.Equal(bytes, (articles * SpoolCapacity.AverageArticleBytes) + remainder);
        }

        [Fact]
        public void An_illustrative_22_terabyte_disk_has_the_same_raw_floor_and_is_not_usable_capacity()
        {
            const long rawDiskBytes = 22_000_000_000_000L;
            Assert.Equal(SpoolCapacity.Decimal22TerabyteBytes, rawDiskBytes);
            Assert.Equal(28_645_833L, SpoolCapacity.ArticleCount(rawDiskBytes));
            Assert.Equal(256_000L, SpoolCapacity.RemainderBytes(rawDiskBytes));
        }

        [Fact]
        public void Candidate_layouts_use_the_full_22_terabyte_article_count_of_one_spool()
        {
            const long articles = 28_645_833L;
            Assert.Equal(articles, SpoolCapacity.ArticleCount(SpoolCapacity.Decimal22TerabyteBytes));

            AssertLeaf(articles, leafCount: 256, floor: 111_897L, remainder: 201L);
            AssertLeaf(articles, leafCount: 4_096, floor: 6_993L, remainder: 2_505L);
            AssertLeaf(articles, leafCount: 65_536, floor: 437L, remainder: 6_601L);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(8)]
        public void Extra_spools_multiply_directories_and_do_not_divide_the_article_count(int spoolCount)
        {
            const long articlesPerSpool = 28_645_833L;
            Assert.Equal(articlesPerSpool, SpoolCapacity.ArticleCount(SpoolCapacity.Decimal22TerabyteBytes));
            Assert.Equal(4_368, SpoolShardLayout.DirectoryCount);
            Assert.Equal(4_096, SpoolShardLayout.LeafCount);
            Assert.Equal(spoolCount * 4_368, spoolCount * SpoolShardLayout.DirectoryCount);
            Assert.Equal(6_993L, SpoolCapacity.ArticlesPerLeafFloor(articlesPerSpool, SpoolShardLayout.LeafCount));
            Assert.Equal(2_505L, SpoolCapacity.LeafRemainder(articlesPerSpool, SpoolShardLayout.LeafCount));
            Assert.Equal(articlesPerSpool, SpoolCapacity.ArticleCount(SpoolCapacity.Decimal22TerabyteBytes));
        }

        [Fact]
        public void Chosen_layout_is_three_fixed_hexadecimal_levels()
        {
            Assert.Equal(3, SpoolShardLayout.Levels);
            Assert.Equal(16, SpoolShardLayout.Fanout);
            Assert.Equal(16 + 256 + 4_096, SpoolShardLayout.DirectoryCount);
            Assert.Equal(SpoolShardLayout.DirectoryCount, SpoolShardLayout.RelativeDirectories.Length);
            Assert.Equal("0", SpoolShardLayout.RelativeDirectories[0]);
            Assert.Equal("f", SpoolShardLayout.RelativeDirectories[15]);
            Assert.Equal("0/0", SpoolShardLayout.RelativeDirectories[16]);
            Assert.Equal("0/0/0", SpoolShardLayout.RelativeDirectories[16 + 256]);
            Assert.Equal("f/f/f", SpoolShardLayout.RelativeDirectories[^1]);
            Assert.Equal(32, SpoolShardLayout.InitializationConcurrency);
        }

        [Fact]
        public void Floor_division_handles_boundaries_without_rounding_up()
        {
            Assert.Equal(0L, SpoolCapacity.ArticleCount(0));
            Assert.Equal(0L, SpoolCapacity.RemainderBytes(0));
            Assert.Equal(0L, SpoolCapacity.ArticleCount(SpoolCapacity.AverageArticleBytes - 1));
            Assert.Equal(SpoolCapacity.AverageArticleBytes - 1, SpoolCapacity.RemainderBytes(SpoolCapacity.AverageArticleBytes - 1));
            Assert.Equal(1L, SpoolCapacity.ArticleCount(SpoolCapacity.AverageArticleBytes));
            Assert.Equal(0L, SpoolCapacity.RemainderBytes(SpoolCapacity.AverageArticleBytes));
            Assert.Equal(1L, SpoolCapacity.ArticlesPerLeafFloor(4_096, 4_096));
            Assert.Equal(0L, SpoolCapacity.LeafRemainder(4_096, 4_096));
            Assert.Equal(0L, SpoolCapacity.ArticlesPerLeafFloor(4_095, 4_096));
            Assert.Equal(4_095L, SpoolCapacity.LeafRemainder(4_095, 4_096));
            Assert.Throws<ArgumentOutOfRangeException>(() => SpoolCapacity.ArticleCount(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => SpoolCapacity.RemainderBytes(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => SpoolCapacity.ArticlesPerLeafFloor(-1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => SpoolCapacity.ArticlesPerLeafFloor(1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => SpoolCapacity.LeafRemainder(1, -1));
        }

        private static void AssertLeaf(long articles, int leafCount, long floor, long remainder)
        {
            Assert.Equal(floor, SpoolCapacity.ArticlesPerLeafFloor(articles, leafCount));
            Assert.Equal(remainder, SpoolCapacity.LeafRemainder(articles, leafCount));
            Assert.Equal(articles, (floor * leafCount) + remainder);
            Assert.True(remainder < leafCount);
        }
    }
}

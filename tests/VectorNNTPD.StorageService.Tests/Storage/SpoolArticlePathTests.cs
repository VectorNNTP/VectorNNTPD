using VectorNNTPD.Common.Articles;
using VectorNNTPD.StorageService.Storage.SpoolShards;

namespace VectorNNTPD.StorageService.Tests.Storage
{

    public sealed class SpoolArticlePathTests
    {
        private const string Hex = "ab0cdef0123456789abcdef0123456789abcdef0123456789abcdef012345678";

        [Fact]
        public void The_same_canonical_id_resolves_to_the_same_relative_path()
        {
            var first = ArticleId.ParseLowerHex(Hex);
            var second = ArticleId.ParseLowerHex(Hex);

            var path = SpoolArticlePath.RelativePath(first);
            Assert.Equal(path, SpoolArticlePath.RelativePath(second));
            Assert.True(SpoolArticlePath.TryRelativePath(Hex, out var parsed));
            Assert.Equal(path, parsed);
            Assert.Equal("a/b/0/" + Hex, path);
            Assert.Equal(3, path.Count(static c => c == '/'));
            Assert.Equal(Hex, first.ToLowerHexString());
        }

        [Fact]
        public void Path_components_are_the_leading_nibbles_of_the_blake3_hex_not_a_runtime_hash()
        {
            var id = ArticleId.ParseLowerHex(Hex);
            var path = SpoolArticlePath.RelativePath(id);
            var segments = path.Split('/');

            Assert.Equal(4, segments.Length);
            Assert.Equal("a", segments[0]);
            Assert.Equal("b", segments[1]);
            Assert.Equal("0", segments[2]);
            Assert.Equal(Hex, segments[3]);
            Assert.Equal(64, segments[3].Length);
            Assert.NotEqual(id.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture), segments[0]);
            Assert.DoesNotContain("..", path, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("")]
        [InlineData("ab")]
        [InlineData("ab0cdef0123456789abcdef0123456789abcdef0123456789abcdef01234567")]
        [InlineData("ab0cdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
        [InlineData("AB0cdef0123456789abcdef0123456789abcdef0123456789abcdef012345678")]
        [InlineData("gb0cdef0123456789abcdef0123456789abcdef0123456789abcdef012345678")]
        [InlineData("../cdef0123456789abcdef0123456789abcdef0123456789abcdef012345678")]
        [InlineData("ab0cdef0123456789abcdef0123456789abcdef0123456789abcdef01234567/")]
        public void Invalid_encodings_do_not_produce_a_path(string hex)
        {
            Assert.False(SpoolArticlePath.TryRelativePath(hex, out var path));
            Assert.Equal(string.Empty, path);
        }

        [Fact]
        public void Combine_under_root_keeps_the_same_relative_path_on_every_spool()
        {
            var relative = SpoolArticlePath.RelativePath(ArticleId.ParseLowerHex(Hex));
            var first = SpoolArticlePath.CombineUnderRoot(Path.Join(Path.GetTempPath(), "spool-a"), relative);
            var second = SpoolArticlePath.CombineUnderRoot(Path.Join(Path.GetTempPath(), "spool-b"), relative);

            Assert.EndsWith(Path.Join("a", "b", "0", Hex), first, StringComparison.Ordinal);
            Assert.EndsWith(Path.Join("a", "b", "0", Hex), second, StringComparison.Ordinal);
            Assert.NotEqual(first, second);
        }

        [Fact]
        public void Combine_article_matches_combine_under_root()
        {
            var id = ArticleId.ParseLowerHex(Hex);
            var relative = SpoolArticlePath.RelativePath(id);
            var root = Path.Join(Path.GetTempPath(), "spool-combine-article");
            var combined = SpoolArticlePath.CombineUnderRoot(root, relative);
            var direct = SpoolArticlePath.CombineArticle(root, id);

            Assert.Equal(combined, direct);
            Assert.Equal(direct, SpoolArticlePath.CombineArticle(root, id));
        }

        [Theory]
        [InlineData("a/../b")]
        [InlineData("../a")]
        [InlineData("a/b/c/../../x")]
        [InlineData("/a/b")]
        [InlineData("a//b")]
        [InlineData("a/B/0")]
        [InlineData("a/g/0")]
        public void Combine_under_root_rejects_traversal_and_invalid_segments(string relative)
        {
            var root = Path.Join(Path.GetTempPath(), "spool-root");
            Assert.Throws<InvalidOperationException>(() => SpoolArticlePath.CombineUnderRoot(root, relative));
        }
    }
}


using VectorNNTPD.Common.Articles.Parsing;
using System.Text;

namespace VectorNNTPD.BackFillerService.Tests.Articles
{
    /// <summary>
    /// Unit regressions for <see cref="ArticleCorpusBench.ClassifyArticle"/> and read-failure classification
    /// without running the full corpus bench.
    /// </summary>
    public sealed class ArticleCorpusClassificationTests
    {
        /// <summary>Empty bytes classify as invalid with a parse reason, not as unsupported.</summary>
        [Fact]
        public void Malformed_article_is_invalid_with_a_parse_reason()
        {
            var parser = new NntpArticleParser("backfiller.test");
            var outcome = ArticleCorpusBench.ClassifyArticle(parser, [], out var reason);

            Assert.Equal(ArticleCorpusBench.CorpusOutcome.Invalid, outcome);
            Assert.Equal("parse EmptyArticle", reason);
        }

        /// <summary>A minimal well-formed article classifies as valid with an empty reason string.</summary>
        [Fact]
        public void Ordinary_article_is_valid()
        {
            var parser = new NntpArticleParser("backfiller.test");
            var article = Encoding.ASCII.GetBytes(
                "Path: news.example.com!not-for-mail\r\n" +
                "Newsgroups: example.test\r\n" +
                "Subject: corpus\r\n" +
                "From: user@example.com\r\n" +
                "Date: Sat, 06 Mar 2004 21:39:44 -0800\r\n" +
                "Message-ID: <corpus-classify@example.com>\r\n" +
                "\r\n" +
                "Hello\r\n");

            var outcome = ArticleCorpusBench.ClassifyArticle(parser, article, out var reason);

            Assert.Equal(ArticleCorpusBench.CorpusOutcome.Valid, outcome);
            Assert.Equal("", reason);
        }

        /// <summary>
        /// IO and access exceptions map to read reasons; programming errors propagate instead of being swallowed as corpus outcomes.
        /// </summary>
        [Fact]
        public void Read_failures_are_invalid_and_other_exceptions_are_not_classified()
        {
            Assert.True(ArticleCorpusBench.TryClassifyReadFailure(new IOException("disk"), out var readReason));
            Assert.Equal("read IOException", readReason);
            Assert.True(ArticleCorpusBench.TryClassifyReadFailure(
                new UnauthorizedAccessException("denied"),
                out var deniedReason));
            Assert.Equal("read UnauthorizedAccessException", deniedReason);

            var unexpected = new InvalidOperationException("programming failure");
            Assert.False(ArticleCorpusBench.TryClassifyReadFailure(unexpected, out _));
            var surfaced = Assert.Throws<InvalidOperationException>(() =>
            {
                if (!ArticleCorpusBench.TryClassifyReadFailure(unexpected, out _))
                {
                    throw unexpected;
                }
            });
            Assert.Same(unexpected, surfaced);
        }
    }
}

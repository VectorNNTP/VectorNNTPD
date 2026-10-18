using System.Buffers;
using System.Text;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles.Processing;

namespace VectorNNTPD.Common.Tests.Articles.Processing
{
    /// <summary>
    /// Article-size ceiling for the pre-stuff projection and the stuffed wire image.
    /// </summary>
    public sealed class NntpArticleCanonicalMaterializerSizeTests
    {
        /// <summary>Under the byte ceiling, canonical length equals the stuffed wire length and rents once at that size.</summary>
        [Fact]
        public void Plain_article_below_the_limit_keeps_its_stuffed_length()
        {
            var article = Article("Hello\r\n");
            var parsed = Parse(article, 1024 * 1024);
            var measured = Measure(in parsed);
            Assert.Equal(0, measured.ExtraDots);
            Assert.Equal(measured.WireLength, measured.PreStuffLength);

            var pool = new RecordingArrayPool();
            var accepted = Materialize(in parsed, measured.WireLength + 64, pool);

            Assert.True(accepted.IsAccepted);
            Assert.Equal(measured.WireLength, accepted.CanonicalLength);
            Assert.Equal(1, pool.RentCalls);
            Assert.Equal(measured.WireLength, pool.LastMinimumLength);
        }

        /// <summary>Plain articles exactly at the configured byte ceiling are accepted without overshooting rent.</summary>
        [Fact]
        public void Plain_article_exactly_at_the_limit_is_accepted()
        {
            var article = Article("Hello\r\n");
            var parsed = Parse(article, 1024 * 1024);
            var measured = Measure(in parsed);
            Assert.Equal(0, measured.ExtraDots);

            var pool = new RecordingArrayPool();
            var accepted = Materialize(in parsed, measured.WireLength, pool);

            Assert.True(accepted.IsAccepted);
            Assert.Equal(measured.WireLength, accepted.CanonicalLength);
            Assert.Equal(measured.WireLength, pool.LastMinimumLength);
        }

        /// <summary>One byte over the stuffed limit rejects with <see cref="NntpArticleCanonicalFailureCode.ArticleTooLarge"/> before array rent.</summary>
        [Fact]
        public void Plain_article_one_byte_over_the_limit_is_rejected_before_rent()
        {
            var article = Article("Hello\r\n");
            var parsed = Parse(article, 1024 * 1024);
            var measured = Measure(in parsed);

            var pool = new RecordingArrayPool();
            var rejected = Materialize(in parsed, measured.WireLength - 1, pool);

            Assert.False(rejected.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.ArticleTooLarge, rejected.FailureCode);
            Assert.Null(rejected.ArticleBytes);
            Assert.Equal(0, rejected.CanonicalLength);
            Assert.Equal(0, pool.RentCalls);
        }

        /// <summary>Limit checked against stuffed length: pre-stuff budget alone is insufficient when dots expand the wire image.</summary>
        [Fact]
        public void Stuffing_dots_that_cross_the_limit_are_rejected_before_rent()
        {
            var article = Article(DotLines(8));
            var parsed = Parse(article, 1024 * 1024);
            var measured = Measure(in parsed);
            Assert.Equal(8, measured.ExtraDots);
            Assert.True(measured.PreStuffLength < measured.WireLength);

            var pool = new RecordingArrayPool();
            var rejected = Materialize(in parsed, measured.PreStuffLength, pool);

            Assert.False(rejected.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.ArticleTooLarge, rejected.FailureCode);
            Assert.Null(rejected.ArticleBytes);
            Assert.Equal(0, pool.RentCalls);
        }

        /// <summary>Dot-heavy bodies at the stuffed byte ceiling accept and rent for the expanded canonical length.</summary>
        [Fact]
        public void Dot_heavy_article_exactly_at_the_stuffed_limit_is_accepted()
        {
            var article = Article(DotLines(32));
            var parsed = Parse(article, 1024 * 1024);
            var measured = Measure(in parsed);
            Assert.Equal(32, measured.ExtraDots);

            var pool = new RecordingArrayPool();
            var accepted = Materialize(in parsed, measured.WireLength, pool);

            Assert.True(accepted.IsAccepted);
            Assert.Equal(measured.WireLength, accepted.CanonicalLength);
            Assert.Equal(measured.PreStuffLength + measured.ExtraDots, accepted.CanonicalLength);
            Assert.Equal(measured.WireLength, pool.LastMinimumLength);
        }

        /// <summary>Dot-heavy articles one stuffed byte over the limit reject before renting output buffers.</summary>
        [Fact]
        public void Dot_heavy_article_one_byte_over_the_stuffed_limit_is_rejected_before_rent()
        {
            var article = Article(DotLines(32));
            var parsed = Parse(article, 1024 * 1024);
            var measured = Measure(in parsed);
            Assert.True(measured.PreStuffLength <= measured.WireLength - 1);

            var pool = new RecordingArrayPool();
            var rejected = Materialize(in parsed, measured.WireLength - 1, pool);

            Assert.False(rejected.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.ArticleTooLarge, rejected.FailureCode);
            Assert.Null(rejected.ArticleBytes);
            Assert.Equal(0, pool.RentCalls);
        }

        /// <summary>Empty parse failures and ordinary accepted articles keep prior failure codes and length semantics.</summary>
        [Fact]
        public void Empty_and_ordinary_articles_keep_their_existing_results()
        {
            var emptyParsed = Parse([], ArticleResourceLimits.MaxArticleBytes);
            Assert.False(emptyParsed.IsAccepted);
            var emptyPool = new RecordingArrayPool();
            var empty = Materialize(in emptyParsed, ArticleResourceLimits.MaxArticleBytes, emptyPool);
            Assert.Equal(NntpArticleCanonicalFailureCode.ParseNotAccepted, empty.FailureCode);
            Assert.Null(empty.ArticleBytes);
            Assert.Equal(0, emptyPool.RentCalls);

            var ordinary = Article("Hello\r\n");
            var parsed = new NntpArticleParser("backfiller.test").Parse(ordinary);
            Assert.True(parsed.IsAccepted, parsed.FailureCode.ToString());
            var materialized = NntpArticleCanonicalMaterializer.Materialize(in parsed, ArticlePathMode.Normalize);
            Assert.True(materialized.IsAccepted, materialized.FailureCode.ToString());
            Assert.NotNull(materialized.ArticleBytes);
            Assert.Equal(materialized.CanonicalLength, materialized.ArticleBytes.Length);
            Assert.InRange(materialized.CanonicalLength, 1, ArticleResourceLimits.MaxArticleBytes);
        }

        /// <summary>Materializes once and derives stuffed versus pre-stuff lengths from body dot counts.</summary>
        /// <param name="parsed">Accepted parse result to measure.</param>
        /// <returns>Wire length, pre-stuff length, and extra dot count.</returns>
        private static MeasuredArticle Measure(in NntpArticleParseResult parsed)
        {
            var pool = new RecordingArrayPool();
            var accepted = Materialize(in parsed, 1024 * 1024, pool);
            Assert.True(accepted.IsAccepted, accepted.FailureCode.ToString());
            var extra = ArticleBodyWireStuffer.CountExtraDots(parsed.ArticleBytes.Span[parsed.HeaderBytes.Length..]);
            Assert.Equal(1, pool.RentCalls);
            Assert.Equal(accepted.CanonicalLength, pool.LastMinimumLength);
            return new MeasuredArticle(accepted.CanonicalLength, accepted.CanonicalLength - extra, extra);
        }

        /// <summary>Parses test articles with organizational path host and asserts acceptance except for intentional empty input.</summary>
        /// <param name="article">Logical article bytes.</param>
        /// <param name="maxArticleBytes">Parser size ceiling.</param>
        /// <returns>Parse result passed to materialize tests.</returns>
        private static NntpArticleParseResult Parse(byte[] article, int maxArticleBytes)
        {
            var parsed = new NntpArticleParser("backfiller.test").Parse(
                article,
                maxArticleBytes,
                ArticlePathCanonicalizer.OrganizationalTrackerHost);
            Assert.True(parsed.IsAccepted || article.Length == 0, parsed.FailureCode.ToString());
            return parsed;
        }

        /// <summary>Materializes with normalize path mode and an injectable array pool for rent assertions.</summary>
        /// <param name="parsed">Accepted parse result.</param>
        /// <param name="maxArticleBytes">Materializer byte ceiling.</param>
        /// <param name="pool">Pool that records rent calls.</param>
        /// <returns>Materialize outcome under test.</returns>
        private static NntpArticleCanonicalMaterializeResult Materialize(
            in NntpArticleParseResult parsed,
            int maxArticleBytes,
            ArrayPool<byte> pool) =>
            NntpArticleCanonicalMaterializer.Materialize(
                in parsed,
                ArticlePathMode.Normalize,
                maxArticleBytes,
                ArticlePathCanonicalizer.OrganizationalTrackerHost,
                pool);

        /// <summary>Builds a minimal valid header set with the supplied body bytes.</summary>
        /// <param name="body">Body after the CRLF separator.</param>
        /// <returns>Full logical article bytes.</returns>
        private static byte[] Article(string body)
        {
            var text =
                "Path: news.example.com!not-for-mail\r\n" +
                "Newsgroups: example.test\r\n" +
                "Subject: size boundary\r\n" +
                "From: user@example.com\r\n" +
                "Date: Sat, 06 Mar 2004 21:39:44 -0800\r\n" +
                "Message-ID: <size-boundary@example.com>\r\n" +
                "\r\n" +
                body;
            return Encoding.ASCII.GetBytes(text);
        }

        /// <summary>Repeats dot-leading CRLF body lines to inflate stuffed size.</summary>
        /// <param name="count">Number of <c>.\r\n</c> lines.</param>
        /// <returns>Concatenated body fragment.</returns>
        private static string DotLines(int count) => string.Concat(Enumerable.Repeat(".\r\n", count));

        /// <summary>Canonical wire length, logical pre-stuff length, and counted stuffing dots for boundary tests.</summary>
        /// <param name="WireLength">Accepted canonical byte length.</param>
        /// <param name="PreStuffLength">Length before dot stuffing expansion.</param>
        /// <param name="ExtraDots">Number of extra leading dots added in the body.</param>
        private readonly record struct MeasuredArticle(int WireLength, int PreStuffLength, int ExtraDots);

        /// <summary>Array pool test double that counts rents and records the last minimum length requested.</summary>
        private sealed class RecordingArrayPool : ArrayPool<byte>
        {
            /// <summary>Number of <see cref="Rent"/> calls observed.</summary>
            public int RentCalls { get; private set; }

            /// <summary>Most recent minimum length passed to <see cref="Rent"/>.</summary>
            public int LastMinimumLength { get; private set; }

            /// <inheritdoc />
            public override byte[] Rent(int minimumLength)
            {
                RentCalls++;
                LastMinimumLength = minimumLength;
                return GC.AllocateUninitializedArray<byte>(minimumLength);
            }

            /// <inheritdoc />
            public override void Return(byte[] array, bool clearArray = false)
            {
            }
        }
    }
}

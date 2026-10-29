using System.Text;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles.Processing;
using VectorNNTPD.Common.Articles.YEnc;

namespace VectorNNTPD.Common.Tests.Articles.Processing
{
    /// <summary>
    /// Asserts that accepted articles are rewritten with a canonical date and Path while body bytes and separator style stay intact.
    /// </summary>
    public sealed class NntpArticleCanonicalMaterializerTests
    {
        /// <summary>
        /// Application identity supplied to the parser for materialization cases that do not use a boundary-length name.
        /// </summary>
        private const string LocalFqdn = "backfiller01.usenet.ninja";
        /// <summary>
        /// Tracker hop plus its separator, used to build the Path text these tests expect.
        /// </summary>
        private const string OrganizationalTrackerPrefix = "news.usenet.ninja!";

        /// <summary>
        /// Rematerializing CanonicalV1 ArtData with <see cref="CanonicalBodyMode.PreserveCanonicalBody"/>
        /// must not add another stuffing layer to dot-leading body lines.
        /// </summary>
        [Fact]
        public void Materialize_PreserveCanonicalBody_DoesNotRestuffAlreadyStuffedBody()
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var destuffed = BuildArticle(
                [
                    "Path: peer.example",
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <preserve-body@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Subject: dots",
                ],
                "Hello\r\n.leading\r\n..leading\r\n");
            var first = ArticleRecordFactory.TryCreate(parser, destuffed, ArticlePathMode.Normalize);
            Assert.True(first.IsAccepted);
            var stuffedBody = Encoding.ASCII.GetString(GetBodyBytes(first.Record.ArtData.Span));
            Assert.Equal("Hello\r\n..leading\r\n...leading\r\n", stuffedBody);

            var parse = parser.Parse(first.Record.ArtData);
            Assert.True(parse.IsAccepted);
            var restuffed = NntpArticleCanonicalMaterializer.Materialize(in parse, ArticlePathMode.Traverse);
            Assert.True(restuffed.IsAccepted);
            Assert.Equal(
                "Hello\r\n...leading\r\n....leading\r\n",
                Encoding.ASCII.GetString(GetBodyBytes(restuffed.ArticleBytes.AsSpan(0, restuffed.CanonicalLength))));

            var preserved = NntpArticleCanonicalMaterializer.Materialize(
                in parse,
                ArticlePathMode.Traverse,
                ArticleResourceLimits.MaxArticleBytes,
                ArticlePathCanonicalizer.OrganizationalTrackerHost,
                rentFrom: null,
                CanonicalBodyMode.PreserveCanonicalBody);
            Assert.True(preserved.IsAccepted);
            Assert.Equal(
                stuffedBody,
                Encoding.ASCII.GetString(GetBodyBytes(preserved.ArticleBytes.AsSpan(0, preserved.CanonicalLength))));
        }

        /// <summary>
        /// A missing Path is inserted as the tracker plus the local identity, the Date is rewritten to canonical UTC, and the body is unchanged.
        /// </summary>
        [Fact]
        public void Materialize_WhenPathMissing_InsertsCanonicalPathHeaderAndRewritesDate()
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var originalArticle = BuildArticle(
                [
                    "Date: Tue, 10 May 2011 13:48:50 -0500",
                    "Message-ID: <materialize-path-missing@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Subject: keep",
                ],
                "body-1\r\n");

            var parse = parser.Parse(originalArticle);
            Assert.True(parse.IsAccepted);

            var materialized = MaterializeAccepted(parse);
            var headers = GetHeaderText(materialized);
            Assert.Contains("Date: Tue, 10 May 2011 18:48:50 +0000\r\n", headers, StringComparison.Ordinal);
            Assert.Contains($"Path: {OrganizationalTrackerPrefix}{LocalFqdn}", headers, StringComparison.Ordinal);
            Assert.Contains("Subject: keep\r\n", headers, StringComparison.Ordinal);
            Assert.Equal(parse.BodyBytes.ToArray(), GetBodyBytes(materialized));
        }

        /// <summary>
        /// An existing Path is replaced by the canonical value, an unrelated custom header is kept, and the body is unchanged.
        /// </summary>
        [Fact]
        public void Materialize_WhenPathPresentWithoutLocalFqdn_ReplacesPathValueWithCanonicalPath()
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var originalArticle = BuildArticle(
                [
                    "Date: Tue, 10 May 2011 13:48:50 -0500",
                    "Message-ID: <materialize-path-replace@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Path: news.example.org!feed2",
                    "X-Custom: preserve",
                ],
                "body-2\r\n");

            var parse = parser.Parse(originalArticle);
            Assert.True(parse.IsAccepted);
            Assert.Equal("news.usenet.ninja!backfiller01.usenet.ninja!news.example.org!feed2", FormatCanonicalPath(parse));

            var materialized = MaterializeAccepted(parse);
            var headers = GetHeaderText(materialized);
            Assert.Contains("Path: news.usenet.ninja!backfiller01.usenet.ninja!news.example.org!feed2", headers, StringComparison.Ordinal);
            Assert.DoesNotContain("Path: news.example.org!feed2", headers, StringComparison.Ordinal);
            Assert.Contains("X-Custom: preserve", headers, StringComparison.Ordinal);
            Assert.Equal(parse.BodyBytes.ToArray(), GetBodyBytes(materialized));
        }

        /// <summary>
        /// Materialization prepends the local identity again when it is already leftmost, and the tracker appears once.
        /// </summary>
        [Fact]
        public void Materialize_WhenPathAlreadyContainsLocalFqdn_PrependsAgain()
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var originalArticle = BuildArticle(
                [
                    "Date: Tue, 10 May 2011 13:48:50 -0500",
                    "Message-ID: <materialize-path-existing-fqdn@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Path: backfiller01.usenet.ninja!news.example.org",
                ],
                "body-3\r\n");

            var parse = parser.Parse(originalArticle);
            Assert.True(parse.IsAccepted);

            var headers = GetHeaderText(MaterializeAccepted(parse));
            Assert.Contains("Path: news.usenet.ninja!backfiller01.usenet.ninja!backfiller01.usenet.ninja!news.example.org", headers, StringComparison.Ordinal);
            Assert.Equal(2, CountOccurrences(headers, "backfiller01.usenet.ninja"));
            Assert.Equal(1, CountOccurrences(headers, "news.usenet.ninja"));
        }

        /// <summary>
        /// A path with no tracker is rewritten with the tracker before the local identity, and trailing separators are dropped.
        /// </summary>
        [Fact]
        public void Materialize_WhenPathLacksTracker_InsertsTrackerBeforeApplicationHop()
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var originalArticle = BuildArticle(
                [
                    "Date: Tue, 10 May 2011 13:48:50 -0500",
                    "Message-ID: <materialize-path-tracker-absent@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Path: foo!bar!",
                ],
                "body\r\n");

            var parse = parser.Parse(originalArticle);
            Assert.True(parse.IsAccepted);
            Assert.False(parse.ContainsOrganizationalTracker);

            var materialized = MaterializeAccepted(parse);
            var headers = GetHeaderText(materialized);
            Assert.Contains("Path: news.usenet.ninja!backfiller01.usenet.ninja!foo!bar", headers, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(headers, "news.usenet.ninja"));
            Assert.Equal(parse.BodyBytes.ToArray(), GetBodyBytes(materialized));
        }

        /// <summary>
        /// A path that already starts with the tracker is rewritten by prepending only the local identity.
        /// </summary>
        [Fact]
        public void Materialize_WhenPathAlreadyStartsWithTracker_DoesNotDuplicateTracker()
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var originalArticle = BuildArticle(
                [
                    "Date: Tue, 10 May 2011 13:48:50 -0500",
                    "Message-ID: <materialize-path-tracker-first@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Path: news.usenet.ninja!foo!",
                ],
                "body\r\n");

            var parse = parser.Parse(originalArticle);
            Assert.True(parse.IsAccepted);
            Assert.True(parse.ContainsOrganizationalTracker);

            var headers = GetHeaderText(MaterializeAccepted(parse));
            Assert.Contains("Path: backfiller01.usenet.ninja!news.usenet.ninja!foo", headers, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(headers, "news.usenet.ninja"));
        }

        /// <summary>
        /// A tracker hop that is not leftmost still prevents a second tracker from being inserted.
        /// </summary>
        [Fact]
        public void Materialize_WhenTrackerOccursLater_DoesNotDuplicateTracker()
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var originalArticle = BuildArticle(
                [
                    "Date: Tue, 10 May 2011 13:48:50 -0500",
                    "Message-ID: <materialize-path-tracker-later@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Path: foo!news.usenet.ninja!bar!",
                ],
                "body\r\n");

            var parse = parser.Parse(originalArticle);
            Assert.True(parse.IsAccepted);
            Assert.True(parse.ContainsOrganizationalTracker);

            var headers = GetHeaderText(MaterializeAccepted(parse));
            Assert.Contains("Path: backfiller01.usenet.ninja!foo!news.usenet.ninja!bar", headers, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(headers, "news.usenet.ninja"));
        }

        /// <summary>
        /// When Injection-Date wins date resolution, that header is rewritten to canonical UTC and Date is not invented.
        /// </summary>
        [Fact]
        public void Materialize_WhenDateComesFromInjectionDate_RewritesWinningCandidateHeader()
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var originalArticle = BuildArticle(
                [
                    "Injection-Date: Tue, 10 May 2011 13:48:50 -0500",
                    "Message-ID: <materialize-injection-date@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                "body-4\r\n");

            var parse = parser.Parse(originalArticle);
            Assert.True(parse.IsAccepted);
            Assert.Equal(NntpArticleHeaderName.InjectionDate, parse.SelectedDateHeaderName);

            var headers = GetHeaderText(MaterializeAccepted(parse));
            Assert.Contains("Injection-Date: Tue, 10 May 2011 18:48:50 +0000\r\n", headers, StringComparison.Ordinal);
        }

        /// <summary>
        /// A CR-only article gains a CR-terminated Path and canonical Date, keeps the CR CR body boundary, and gains no CRLF.
        /// </summary>
        [Fact]
        public void Materialize_WhenCrOnlyAndPathMissing_InsertsPathWithCrOnlyAndPreservesBodyBoundary()
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var originalArticle = BuildArticle(
                [
                    "Date: Tue, 10 May 2011 13:48:50 -0500",
                    "Message-ID: <materialize-cr-only-path-missing@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Subject: keep",
                ],
                "body-cr-only\r",
                "\r");

            var parse = parser.Parse(originalArticle);
            Assert.True(parse.IsAccepted);

            var materialized = MaterializeAccepted(parse);
            var article = Encoding.ASCII.GetString(materialized);
            Assert.Contains($"Path: {OrganizationalTrackerPrefix}{LocalFqdn}\r", article, StringComparison.Ordinal);
            Assert.Contains("Date: Tue, 10 May 2011 18:48:50 +0000\r", article, StringComparison.Ordinal);
            Assert.Contains("\r\rbody-cr-only\r", article, StringComparison.Ordinal);
            Assert.DoesNotContain("\r\n", article, StringComparison.Ordinal);
            Assert.Equal(parse.BodyBytes.ToArray(), GetBodyBytes(materialized, "\r"));
        }

        /// <summary>
        /// Rewriting a present Path on a CR-only article keeps CR separators and the original body bytes.
        /// </summary>
        [Fact]
        public void Materialize_WhenCrOnlyAndPathPresent_RewritesPathAndPreservesCrOnlySeparators()
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var originalArticle = BuildArticle(
                [
                    "Date: Tue, 10 May 2011 13:48:50 -0500",
                    "Message-ID: <materialize-cr-only-path-present@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Path: news.example.org!feed2",
                ],
                "body-cr-only-path\r",
                "\r");

            var parse = parser.Parse(originalArticle);
            Assert.True(parse.IsAccepted);

            var materialized = MaterializeAccepted(parse);
            var article = Encoding.ASCII.GetString(materialized);
            Assert.Contains($"Path: {OrganizationalTrackerPrefix}{LocalFqdn}!news.example.org!feed2\r", article, StringComparison.Ordinal);
            Assert.DoesNotContain("\r\n", article, StringComparison.Ordinal);
            Assert.Equal(parse.BodyBytes.ToArray(), GetBodyBytes(materialized, "\r"));
        }

        /// <summary>
        /// CRLF, LF, and CR articles each keep that separator on the rewritten Date and inserted Path, and the body bytes match the parse.
        /// </summary>
        /// <param name="separator">Header and boundary terminator applied to every line of the synthetic article.</param>
        [Theory]
        [InlineData("\r\n")]
        [InlineData("\n")]
        [InlineData("\r")]
        public void Materialize_PreservesHeaderSeparatorStyleAndBodyBytesAcrossSupportedSeparators(string separator)
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var originalArticle = BuildArticle(
                [
                    "Date: Tue, 10 May 2011 13:48:50 -0500",
                    "Message-ID: <materialize-separator-variant@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Subject: separator-variant",
                ],
                $"body-{separator.Length}x{(int)separator[0]}\r",
                separator);

            var parse = parser.Parse(originalArticle);
            Assert.True(parse.IsAccepted);

            var materialized = MaterializeAccepted(parse);
            var article = Encoding.ASCII.GetString(materialized);
            Assert.Contains($"Path: {OrganizationalTrackerPrefix}{LocalFqdn}{separator}", article, StringComparison.Ordinal);
            Assert.Contains($"Date: Tue, 10 May 2011 18:48:50 +0000{separator}", article, StringComparison.Ordinal);
            Assert.Contains($"{separator}{separator}", article, StringComparison.Ordinal);
            Assert.Equal(parse.BodyBytes.ToArray(), GetBodyBytes(materialized, separator));
        }

        /// <summary>
        /// An inserted Path uses the last header's terminator, the mixed boundary still separates the same body after a reparse.
        /// </summary>
        /// <param name="lastHeaderTerminator">Terminator of the final header, also required on the inserted Path.</param>
        /// <param name="boundaryTerminator">Empty-line terminator between that header and the body.</param>
        [Theory]
        [InlineData("\n", "\r\n")]
        [InlineData("\r\n", "\r")]
        [InlineData("\r", "\r\n")]
        public void Materialize_WhenPathMissingAndBoundaryUsesMixedTerminators_PreservesBodyAndMaterializes(
            string lastHeaderTerminator,
            string boundaryTerminator)
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var body = Encoding.ASCII.GetBytes("body-a\r\nbody-b\nbody-c\r\n\rmarker");
            var article = BuildArticleWithCustomBoundary(
                [
                    ("Date: Tue, 10 May 2011 13:48:50 -0500", "\r\n"),
                    ("Message-ID: <materialize-mixed-path-missing@example.test>", "\r\n"),
                    ("Newsgroups: alt.test", "\r\n"),
                    ("From: user@example.test", "\r\n"),
                    ("X-Keep: separator-contract", lastHeaderTerminator),
                ],
                boundaryTerminator,
                body);

            var parse = parser.Parse(article);
            Assert.True(parse.IsAccepted);

            var materialized = MaterializeAccepted(parse);
            var materializedText = Encoding.ASCII.GetString(materialized);
            Assert.Contains($"Path: {OrganizationalTrackerPrefix}{LocalFqdn}{lastHeaderTerminator}", materializedText, StringComparison.Ordinal);
            Assert.Contains("Date: Tue, 10 May 2011 18:48:50 +0000\r\n", materializedText, StringComparison.Ordinal);
            Assert.Contains($"X-Keep: separator-contract{lastHeaderTerminator}", materializedText, StringComparison.Ordinal);

            var reparsed = parser.Parse(materialized);
            Assert.True(reparsed.IsAccepted);
            Assert.Equal(parse.BodyBytes.ToArray(), reparsed.BodyBytes.ToArray());
        }

        /// <summary>
        /// Rewriting a final Path header keeps its original terminator and the mixed boundary, so a reparse sees the same body.
        /// </summary>
        /// <param name="lastHeaderTerminator">Terminator that ends the Path header before the boundary.</param>
        /// <param name="boundaryTerminator">Empty-line terminator between Path and the body.</param>
        [Theory]
        [InlineData("\n", "\r\n")]
        [InlineData("\r\n", "\r")]
        public void Materialize_WhenPathPresentAsFinalHeaderAndBoundaryUsesMixedTerminators_RewritesPathWithoutChangingBody(
            string lastHeaderTerminator,
            string boundaryTerminator)
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var body = Encoding.ASCII.GetBytes("body-path-last\r\n\ntrailer");
            var article = BuildArticleWithCustomBoundary(
                [
                    ("Date: Tue, 10 May 2011 13:48:50 -0500", "\r\n"),
                    ("Message-ID: <materialize-mixed-path-last@example.test>", "\r\n"),
                    ("Newsgroups: alt.test", "\r\n"),
                    ("From: user@example.test", "\r\n"),
                    ("Path: upstream.example.test!feed2", lastHeaderTerminator),
                ],
                boundaryTerminator,
                body);

            var parse = parser.Parse(article);
            Assert.True(parse.IsAccepted);
            Assert.Equal($"{OrganizationalTrackerPrefix}{LocalFqdn}!upstream.example.test!feed2", FormatCanonicalPath(parse));

            var materializedText = Encoding.ASCII.GetString(MaterializeAccepted(parse));
            Assert.Contains($"Path: {OrganizationalTrackerPrefix}{LocalFqdn}!upstream.example.test!feed2{lastHeaderTerminator}", materializedText, StringComparison.Ordinal);
            Assert.DoesNotContain("Path: upstream.example.test!feed2", materializedText, StringComparison.Ordinal);

            var reparsed = parser.Parse(Encoding.ASCII.GetBytes(materializedText));
            Assert.True(reparsed.IsAccepted);
            Assert.Equal(parse.BodyBytes.ToArray(), reparsed.BodyBytes.ToArray());
        }

        /// <summary>
        /// Rewriting a final Date keeps that header's terminator, inserts Path with the same terminator, and preserves the body across a reparse.
        /// </summary>
        /// <param name="lastHeaderTerminator">Terminator that ends the Date header.</param>
        /// <param name="boundaryTerminator">Empty-line terminator between Date and the body.</param>
        [Theory]
        [InlineData("\n", "\r\n")]
        [InlineData("\r\n", "\r")]
        public void Materialize_WhenDateIsFinalHeaderAndBoundaryUsesMixedTerminators_RewritesDateAndPreservesBody(
            string lastHeaderTerminator,
            string boundaryTerminator)
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var body = Encoding.ASCII.GetBytes("body-date-last\n\r\nEND");
            var article = BuildArticleWithCustomBoundary(
                [
                    ("Message-ID: <materialize-mixed-date-last@example.test>", "\r\n"),
                    ("Newsgroups: alt.test", "\r\n"),
                    ("From: user@example.test", "\r\n"),
                    ("Date: Tue, 10 May 2011 13:48:50 -0500", lastHeaderTerminator),
                ],
                boundaryTerminator,
                body);

            var parse = parser.Parse(article);
            Assert.True(parse.IsAccepted);

            var materialized = MaterializeAccepted(parse);
            var materializedText = Encoding.ASCII.GetString(materialized);
            Assert.Contains($"Date: Tue, 10 May 2011 18:48:50 +0000{lastHeaderTerminator}", materializedText, StringComparison.Ordinal);
            Assert.Contains($"Path: {OrganizationalTrackerPrefix}{LocalFqdn}{lastHeaderTerminator}", materializedText, StringComparison.Ordinal);

            var reparsed = parser.Parse(materialized);
            Assert.True(reparsed.IsAccepted);
            Assert.Equal(parse.BodyBytes.ToArray(), reparsed.BodyBytes.ToArray());
        }

        /// <summary>
        /// A rejected parse is not materialized: the result is <see cref="NntpArticleCanonicalFailureCode.ParseNotAccepted"/> and carries no article bytes.
        /// </summary>
        [Fact]
        public void Materialize_WhenArticleIsRejected_DoesNotBypassValidation()
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var originalArticle = BuildArticle(
                [
                    "Date: INVALID",
                    "Message-ID: <materialize-invalid-date@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                "body-5\r\n");

            var parse = parser.Parse(originalArticle);
            Assert.False(parse.IsAccepted);

            var result = NntpArticleCanonicalMaterializer.Materialize(parse, ArticlePathMode.Traverse);
            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.ParseNotAccepted, result.FailureCode);
            Assert.Null(result.ArticleBytes);
        }

        /// <summary>
        /// An accepted single-part yEnc body is copied through materialization without re-encoding.
        /// </summary>
        [Fact]
        public void Materialize_WhenYEncArticleAccepted_PreservesOriginalValidatedYEncBodyBytes()
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var yEncBody = BuildSyntheticSinglePartYEncBody([0x00, 0x2E, 0x3D, 0x41, 0x20]);
            var originalArticle = BuildArticle(
                [
                    "Date: Tue, 10 May 2011 13:48:50 -0500",
                    "Message-ID: <materialize-yenc-preserve@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                yEncBody);

            var parse = parser.Parse(originalArticle);
            Assert.True(parse.IsAccepted);
            Assert.True(parse.YEncDetected);
            Assert.Equal(YEncArticleValidationStatus.ValidSinglePart, parse.YEncValidation.Status);

            var materializedBody = GetBodyBytes(MaterializeAccepted(parse));
            Assert.Equal(parse.BodyBytes.ToArray(), materializedBody);
            Assert.Equal(yEncBody, materializedBody);
        }

        /// <summary>
        /// An accepted article whose canonical Date and Path growth would pass the hard article-size limit is rejected
        /// with <see cref="NntpArticleCanonicalFailureCode.ArticleTooLarge"/> and no output buffer.
        /// </summary>
        [Fact]
        public void Materialize_WhenCanonicalDestinationWouldExceedHardArticleBytes_RejectsBeforeAllocatingSuccess()
        {
            var parser = new NntpArticleParser(LocalFqdn);
            string[] headers =
            [
                "Date: Tue, 10 May 2011 13:48:50 -0500",
                "Message-ID: <materialize-size-boundary@example.test>",
                "Newsgroups: alt.test",
                "From: user@example.test",
                "Path: b",
            ];

            var baselineArticle = BuildArticle(headers, []);
            var baselineParse = parser.Parse(baselineArticle);
            Assert.True(baselineParse.IsAccepted);

            var canonicalGrowth = (FormatCanonicalUtc(baselineParse).Length - baselineParse.OriginalDateValue.Length)
                + (FormatCanonicalPath(baselineParse).Length - baselineParse.OriginalPathValue.Length);
            Assert.True(canonicalGrowth > 0);

            var targetAcceptedLength = ArticleResourceLimits.MaxArticleBytes - canonicalGrowth + 1;
            var bodyLength = targetAcceptedLength - baselineParse.HeaderBytes.Length;
            Assert.True(bodyLength > 0);

            var article = BuildArticle(headers, BuildSafeBody(bodyLength));
            var parse = parser.Parse(article);
            Assert.True(parse.IsAccepted);
            Assert.Equal(targetAcceptedLength, parse.ArticleBytes.Length);

            var expectedCanonicalLength = parse.ArticleBytes.Length
                + FormatCanonicalUtc(parse).Length
                - parse.OriginalDateValue.Length
                + FormatCanonicalPath(parse).Length
                - parse.OriginalPathValue.Length;
            Assert.True(expectedCanonicalLength > ArticleResourceLimits.MaxArticleBytes);

            var result = NntpArticleCanonicalMaterializer.Materialize(parse, ArticlePathMode.Traverse);
            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.ArticleTooLarge, result.FailureCode);
            Assert.Null(result.ArticleBytes);
        }

        /// <summary>
        /// A canonical Path rewrite whose <c>Path:</c> line would exceed the hard line limit is rejected
        /// with <see cref="NntpArticleCanonicalFailureCode.PathRewriteLineTooLong"/> and no output buffer.
        /// </summary>
        [Fact]
        public void Materialize_WhenCanonicalPathRewriteWouldExceedHardLineBytes_RejectsMaterialization()
        {
            var longFqdn = new string('a', 1017);
            var parser = new NntpArticleParser(longFqdn);
            var article = BuildArticle(
                [
                    "Date: Tue, 10 May 2011 13:48:50 -0500",
                    "Message-ID: <materialize-path-line-limit@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Path: b",
                ],
                "body\r\n");

            var parse = parser.Parse(article);
            Assert.True(parse.IsAccepted);
            Assert.True(FormatCanonicalPath(parse).Length + "Path: ".Length > ArticleResourceLimits.MaxArticleLineBytes);

            var result = NntpArticleCanonicalMaterializer.Materialize(parse, ArticlePathMode.Traverse);
            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.PathRewriteLineTooLong, result.FailureCode);
            Assert.Null(result.ArticleBytes);
        }

        /// <summary>
        /// Inserting a Path whose content is exactly the hard line limit succeeds and the written line has that length.
        /// </summary>
        [Fact]
        public void Materialize_WhenCanonicalPathInsertionIsExactlyHardBoundary_SucceedsAtBoundary()
        {
            var boundaryFqdn = new string('a', ArticleResourceLimits.MaxArticleLineBytes - "Path: ".Length - OrganizationalTrackerPrefix.Length);
            var parser = new NntpArticleParser(boundaryFqdn);
            var article = BuildArticle(
                [
                    "Date: Tue, 10 May 2011 13:48:50 -0500",
                    "Message-ID: <materialize-path-insert-boundary@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                "body\r\n");

            var parse = parser.Parse(article);
            Assert.True(parse.IsAccepted);
            Assert.Equal(OrganizationalTrackerPrefix + boundaryFqdn, FormatCanonicalPath(parse));

            var headers = GetHeaderText(MaterializeAccepted(parse));
            var pathLine = FindHeaderLine(headers, "Path:");
            Assert.NotNull(pathLine);
            Assert.Equal($"Path: {OrganizationalTrackerPrefix}{boundaryFqdn}", pathLine);
            Assert.Equal(ArticleResourceLimits.MaxArticleLineBytes, Encoding.ASCII.GetByteCount(pathLine));
        }

        /// <summary>
        /// A Path-line rejection leaves both the caller's article buffer and the parse result's article bytes unchanged.
        /// </summary>
        [Fact]
        public void Materialize_WhenRejectedForCanonicalBoundaries_DoesNotMutateSourceBytes()
        {
            var longFqdn = new string('a', 1017);
            var parser = new NntpArticleParser(longFqdn);
            var article = BuildArticle(
                [
                    "Date: Tue, 10 May 2011 13:48:50 -0500",
                    "Message-ID: <materialize-source-owner-preserved@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Path: b",
                ],
                "body\r\n");

            var parse = parser.Parse(article);
            Assert.True(parse.IsAccepted);

            var result = NntpArticleCanonicalMaterializer.Materialize(parse, ArticlePathMode.Traverse);
            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.PathRewriteLineTooLong, result.FailureCode);
            Assert.Equal((byte)'D', article[0]);
            Assert.True(parse.ArticleBytes.Span.SequenceEqual(article));
        }

        /// <summary>
        /// Path insertion content of 1024 bytes succeeds for CRLF, LF, and CR, and 1025 bytes is
        /// <see cref="NntpArticleCanonicalFailureCode.PathInsertionLineTooLong"/>. The terminator is not part of the 1024-byte content.
        /// </summary>
        /// <param name="separator">Line terminator used for the synthetic article.</param>
        /// <param name="pathContentLength">Byte length of the <c>Path:</c> line excluding its terminator. The test accepts 1024 and rejects 1025.</param>
        [Theory]
        [InlineData("\r\n", 1024)]
        [InlineData("\r\n", 1025)]
        [InlineData("\n", 1024)]
        [InlineData("\n", 1025)]
        [InlineData("\r", 1024)]
        [InlineData("\r", 1025)]
        public void Materialize_WhenPathInsertionLineContentAtOrAboveBoundary_EnforcesContentByteLimit(
            string separator,
            int pathContentLength)
        {
            var fqdn = new string('a', pathContentLength - "Path: ".Length - OrganizationalTrackerPrefix.Length);
            var parser = new NntpArticleParser(fqdn);
            var article = BuildArticle(
                [
                    "Date: Tue, 10 May 2011 13:48:50 -0500",
                    "Message-ID: <materialize-path-insert-exact-line-content-boundary@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                "body\r\n",
                separator);

            var parse = parser.Parse(article);
            Assert.True(parse.IsAccepted);

            if (pathContentLength == 1024)
            {
                var materializedText = Encoding.ASCII.GetString(MaterializeAccepted(parse));
                var pathLine = ExtractHeaderLine(materializedText, "Path:", separator);
                Assert.Equal(1024, Encoding.ASCII.GetByteCount(pathLine));
                Assert.Equal(separator.Length, DetermineLineTerminatorLength(materializedText, "Path:", separator));
            }
            else
            {
                var result = NntpArticleCanonicalMaterializer.Materialize(parse, ArticlePathMode.Traverse);
                Assert.False(result.IsAccepted);
                Assert.Equal(NntpArticleCanonicalFailureCode.PathInsertionLineTooLong, result.FailureCode);
            }
        }

        /// <summary>
        /// A rewritten Path line of 1024 content bytes succeeds, and one more upstream byte is
        /// <see cref="NntpArticleCanonicalFailureCode.PathRewriteLineTooLong"/>.
        /// </summary>
        [Fact]
        public void Materialize_WhenPathRewriteLineContentAt1024_SucceedsAnd1025Rejects()
        {
            const string localPrefix = "bf";
            var rewritePrefixLength = OrganizationalTrackerPrefix.Length + localPrefix.Length + 1;
            var pathValueAt1024 = 1024 - "Path: ".Length;

            var upstreamAt1024 = new string('x', pathValueAt1024 - rewritePrefixLength);
            var upstreamAt1025 = new string('x', pathValueAt1024 - rewritePrefixLength + 1);
            var parser = new NntpArticleParser(localPrefix);

            var acceptedArticle = BuildArticle(
                [
                    "Date: Tue, 10 May 2011 13:48:50 -0500",
                    "Message-ID: <materialize-path-rewrite-line-content-1024@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    $"Path: {upstreamAt1024}",
                ],
                "body\r\n");
            var acceptedParse = parser.Parse(acceptedArticle);
            Assert.True(acceptedParse.IsAccepted);

            var acceptedPathLine = FindHeaderLine(GetHeaderText(MaterializeAccepted(acceptedParse)), "Path:");
            Assert.NotNull(acceptedPathLine);
            Assert.Equal(1024, Encoding.ASCII.GetByteCount(acceptedPathLine));

            var rejectedArticle = BuildArticle(
                [
                    "Date: Tue, 10 May 2011 13:48:50 -0500",
                    "Message-ID: <materialize-path-rewrite-line-content-1025@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    $"Path: {upstreamAt1025}",
                ],
                "body\r\n");
            var rejectedParse = parser.Parse(rejectedArticle);
            Assert.True(rejectedParse.IsAccepted);

            var result = NntpArticleCanonicalMaterializer.Materialize(rejectedParse, ArticlePathMode.Traverse);
            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.PathRewriteLineTooLong, result.FailureCode);
        }

        /// <summary>
        /// A padded Injection-Date rewrite of 1024 content bytes succeeds, and one extra pad byte is
        /// <see cref="NntpArticleCanonicalFailureCode.DateLineTooLong"/>.
        /// </summary>
        [Fact]
        public void Materialize_WhenDateRewriteAt1024LineContentBoundary_SucceedsAnd1025Rejects()
        {
            const string dateHeaderName = "Injection-Date";
            const string shortDateValue = "Wed, 1 Jun 2011 13:48:50 -0500";
            const string canonicalDate = "Wed, 01 Jun 2011 18:48:50 +0000";
            var paddingForAccepted = 1023 - (dateHeaderName.Length + 2 + shortDateValue.Length);
            var paddingForRejected = 1024 - (dateHeaderName.Length + 2 + shortDateValue.Length);

            string BuildDateValue(int leadingPadding) => $"{new string(' ', leadingPadding)}{shortDateValue}";

            var parser = new NntpArticleParser(LocalFqdn, NntpArticleParserOptions.Default with { MaxHeaderValueBytes = 200_000 });

            var acceptedArticle = BuildArticle(
                [
                    $"{dateHeaderName}: {BuildDateValue(paddingForAccepted)}",
                    "Message-ID: <materialize-date-rewrite-line-content-1024@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Path: x",
                ],
                "body\r\n");
            var acceptedParse = parser.Parse(acceptedArticle);
            Assert.True(acceptedParse.IsAccepted);
            Assert.Equal(canonicalDate, FormatCanonicalUtc(acceptedParse));

            var acceptedDateLine = FindHeaderLine(GetHeaderText(MaterializeAccepted(acceptedParse)), $"{dateHeaderName}:");
            Assert.NotNull(acceptedDateLine);
            Assert.Equal(1024, Encoding.ASCII.GetByteCount(acceptedDateLine));

            var rejectedArticle = BuildArticle(
                [
                    $"{dateHeaderName}: {BuildDateValue(paddingForRejected)}",
                    "Message-ID: <materialize-date-rewrite-line-content-1025@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Path: x",
                ],
                "body\r\n");
            var rejectedParse = parser.Parse(rejectedArticle);
            Assert.True(rejectedParse.IsAccepted);
            Assert.Equal(canonicalDate, FormatCanonicalUtc(rejectedParse));

            var result = NntpArticleCanonicalMaterializer.Materialize(rejectedParse, ArticlePathMode.Traverse);
            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.DateLineTooLong, result.FailureCode);
        }

        /// <summary>
        /// Materializes <paramref name="parse"/> in traverse mode and requires an accepted article.
        /// </summary>
        /// <param name="parse">Accepted parse result to materialize.</param>
        /// <returns>The materialized article bytes.</returns>
        private static byte[] MaterializeAccepted(in NntpArticleParseResult parse)
        {
            var result = NntpArticleCanonicalMaterializer.Materialize(parse, ArticlePathMode.Traverse);
            Assert.True(result.IsAccepted, result.FailureCode.ToString());
            Assert.NotNull(result.ArticleBytes);
            return result.ArticleBytes;
        }

        /// <summary>
        /// Formats the canonical UTC date from <paramref name="parse"/> and requires the formatter to succeed.
        /// </summary>
        /// <param name="parse">Parse result that owns the canonical date.</param>
        /// <returns>ASCII text of the canonical UTC date.</returns>
        private static string FormatCanonicalUtc(in NntpArticleParseResult parse)
        {
            Span<byte> destination = stackalloc byte[40];
            Assert.True(parse.TryFormatCanonicalUtc(destination, out var written));
            return Encoding.ASCII.GetString(destination[..written]);
        }

        /// <summary>
        /// Formats the traverse-mode canonical Path from <paramref name="parse"/> and requires the writer to succeed.
        /// </summary>
        /// <param name="parse">Parse result that owns the canonical Path.</param>
        /// <returns>ASCII text of the canonical Path.</returns>
        private static string FormatCanonicalPath(in NntpArticleParseResult parse)
        {
            Span<byte> destination = stackalloc byte[ArticlePathCanonicalizer.MaxPathLength + 256];
            Assert.True(parse.TryWriteCanonicalPath(ArticlePathMode.Traverse, destination, out var written));
            return Encoding.ASCII.GetString(destination[..written]);
        }

        /// <summary>
        /// Builds a CRLF article from header lines and an ASCII body string.
        /// </summary>
        /// <param name="headers">Header lines without terminators.</param>
        /// <param name="body">ASCII body text, including any terminator the case wants preserved.</param>
        /// <returns>The concatenated article bytes.</returns>
        private static byte[] BuildArticle(IReadOnlyList<string> headers, string body)
            => BuildArticle(headers, Encoding.ASCII.GetBytes(body));

        /// <summary>
        /// Builds an article whose header lines and header/body boundary may use different terminators.
        /// </summary>
        /// <param name="headerLines">Header text paired with the terminator for that physical line.</param>
        /// <param name="boundaryTerminator">Terminator text written as the empty separator line after the last header.</param>
        /// <param name="body">Raw body bytes appended after the boundary.</param>
        /// <returns>The concatenated article bytes.</returns>
        private static byte[] BuildArticleWithCustomBoundary(
            IReadOnlyList<(string Header, string Terminator)> headerLines,
            string boundaryTerminator,
            byte[] body)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < headerLines.Count; i++)
            {
                _ = builder.Append(headerLines[i].Header).Append(headerLines[i].Terminator);
            }

            _ = builder.Append(boundaryTerminator);
            var headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
            var article = new byte[headerBytes.Length + body.Length];
            Buffer.BlockCopy(headerBytes, 0, article, 0, headerBytes.Length);
            Buffer.BlockCopy(body, 0, article, headerBytes.Length, body.Length);
            return article;
        }

        /// <summary>
        /// Builds a body of <c>A</c> bytes with CRLF breaks so the line-length limit is not what the size test rejects.
        /// </summary>
        /// <param name="length">Exact body length. Zero or less yields an empty buffer.</param>
        /// <returns>The body bytes.</returns>
        private static byte[] BuildSafeBody(int length)
        {
            if (length <= 0)
            {
                return [];
            }

            var body = new byte[length];
            Array.Fill(body, (byte)'A');
            for (var i = 100; i + 1 < body.Length; i += 102)
            {
                body[i] = (byte)'\r';
                body[i + 1] = (byte)'\n';
            }

            return body;
        }

        /// <summary>
        /// Builds an article from an ASCII body using <paramref name="separator"/> for headers and the blank boundary.
        /// </summary>
        /// <param name="headers">Header lines without terminators.</param>
        /// <param name="body">ASCII body text.</param>
        /// <param name="separator">Terminator written after every header and twice at the boundary.</param>
        /// <returns>The concatenated article bytes.</returns>
        private static byte[] BuildArticle(IReadOnlyList<string> headers, string body, string separator)
            => BuildArticle(headers, Encoding.ASCII.GetBytes(body), separator);

        /// <summary>
        /// Builds a CRLF article from header lines and raw body bytes.
        /// </summary>
        /// <param name="headers">Header lines without terminators.</param>
        /// <param name="body">Body bytes copied after the header/body separator.</param>
        /// <returns>The concatenated article bytes.</returns>
        private static byte[] BuildArticle(IReadOnlyList<string> headers, byte[] body)
            => BuildArticle(headers, body, "\r\n");

        /// <summary>
        /// Builds an article from raw body bytes using <paramref name="separator"/> for headers and the blank boundary.
        /// </summary>
        /// <param name="headers">Header lines without terminators.</param>
        /// <param name="body">Body bytes copied after the boundary.</param>
        /// <param name="separator">Terminator written after every header and twice at the boundary.</param>
        /// <returns>The concatenated article bytes.</returns>
        private static byte[] BuildArticle(IReadOnlyList<string> headers, byte[] body, string separator)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < headers.Count; i++)
            {
                _ = builder.Append(headers[i]).Append(separator);
            }

            _ = builder.Append(separator);
            var headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
            var article = new byte[headerBytes.Length + body.Length];
            Buffer.BlockCopy(headerBytes, 0, article, 0, headerBytes.Length);
            Buffer.BlockCopy(body, 0, article, headerBytes.Length, body.Length);
            return article;
        }

        /// <summary>
        /// Returns the ASCII header section up to, and not including, the blank header/body separator.
        /// </summary>
        /// <param name="article">Materialized or original article bytes.</param>
        /// <returns>The header text before the separator.</returns>
        private static string GetHeaderText(ReadOnlySpan<byte> article)
        {
            var headerEnd = FindHeaderSeparator(article, out _);
            return Encoding.ASCII.GetString(article[..headerEnd]);
        }

        /// <summary>
        /// Returns the body of a CRLF article, after the CRLF CRLF separator.
        /// </summary>
        /// <param name="article">Article bytes that use CRLF.</param>
        /// <returns>The body bytes.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the located separator is not two bytes.</exception>
        private static byte[] GetBodyBytes(ReadOnlySpan<byte> article)
            => GetBodyBytes(article, "\r\n");

        /// <summary>
        /// Returns the body after a blank separator whose width matches <paramref name="separator"/>.
        /// </summary>
        /// <param name="article">Article bytes.</param>
        /// <param name="separator">Terminator the caller expects. CRLF has length 2; CR and LF have length 1.</param>
        /// <returns>The bytes after the blank separator.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the located separator width differs from <paramref name="separator"/>.</exception>
        private static byte[] GetBodyBytes(ReadOnlySpan<byte> article, string separator)
        {
            var headerEnd = FindHeaderSeparator(article, out var separatorLength);
            if (separatorLength != separator.Length)
            {
                throw new InvalidOperationException($"Expected separator length {separator.Length} but found {separatorLength}.");
            }

            return article[(headerEnd + separatorLength + separatorLength)..].ToArray();
        }

        /// <summary>
        /// Finds the first CRLF CRLF boundary, otherwise the first LF LF or CR CR boundary.
        /// </summary>
        /// <param name="article">Article bytes to scan.</param>
        /// <param name="separatorLength">Receives 2 for CRLF or 1 for a single-character terminator.</param>
        /// <returns>The index of the first byte of the blank separator.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no accepted blank separator is present.</exception>
        private static int FindHeaderSeparator(ReadOnlySpan<byte> article, out int separatorLength)
        {
            for (var i = 0; i <= article.Length - 4; i++)
            {
                if (article[i] == (byte)'\r'
                    && article[i + 1] == (byte)'\n'
                    && article[i + 2] == (byte)'\r'
                    && article[i + 3] == (byte)'\n')
                {
                    separatorLength = 2;
                    return i;
                }
            }

            for (var i = 0; i <= article.Length - 2; i++)
            {
                if (article[i] == (byte)'\n' && article[i + 1] == (byte)'\n')
                {
                    separatorLength = 1;
                    return i;
                }

                if (article[i] == (byte)'\r' && article[i + 1] == (byte)'\r')
                {
                    separatorLength = 1;
                    return i;
                }
            }

            throw new InvalidOperationException("Article did not contain an accepted header separator.");
        }

        /// <summary>
        /// Counts non-overlapping ordinal occurrences of <paramref name="value"/> in <paramref name="source"/>.
        /// </summary>
        /// <param name="source">Text to search.</param>
        /// <param name="value">Substring to count.</param>
        /// <returns>The occurrence count.</returns>
        private static int CountOccurrences(string source, string value)
        {
            var count = 0;
            var index = 0;
            while (true)
            {
                index = source.IndexOf(value, index, StringComparison.Ordinal);
                if (index < 0)
                {
                    return count;
                }

                count++;
                index += value.Length;
            }
        }

        /// <summary>
        /// Returns the first CRLF-separated header line that starts with <paramref name="prefix"/>.
        /// </summary>
        /// <param name="headers">Header section text split on CRLF.</param>
        /// <param name="prefix">Ordinal prefix, including the colon when the caller supplies one.</param>
        /// <returns>The matching line, or <see langword="null"/> when no line starts with <paramref name="prefix"/>.</returns>
        private static string? FindHeaderLine(string headers, string prefix)
        {
            var lines = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith(prefix, StringComparison.Ordinal))
                {
                    return lines[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the header line that starts with <paramref name="prefix"/> when the article is split on <paramref name="separator"/>.
        /// </summary>
        /// <param name="materializedArticle">Full materialized article text.</param>
        /// <param name="prefix">Ordinal line prefix to find.</param>
        /// <param name="separator">Separator used both as the line split and as the expected terminator.</param>
        /// <returns>The matching header line without its terminator.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no line starts with <paramref name="prefix"/>.</exception>
        private static string ExtractHeaderLine(string materializedArticle, string prefix, string separator)
        {
            var lines = materializedArticle.Split(separator, StringSplitOptions.None);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith(prefix, StringComparison.Ordinal))
                {
                    return lines[i];
                }
            }

            throw new InvalidOperationException($"Header line with prefix '{prefix}' was not found.");
        }

        /// <summary>
        /// Requires that <paramref name="separator"/> occurs after <paramref name="prefix"/> and returns that separator's length.
        /// </summary>
        /// <param name="materializedArticle">Full materialized article text.</param>
        /// <param name="prefix">Header prefix that must exist.</param>
        /// <param name="separator">Terminator that must follow the prefix.</param>
        /// <returns>The length of <paramref name="separator"/>.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the prefix or the following separator is absent.</exception>
        private static int DetermineLineTerminatorLength(string materializedArticle, string prefix, string separator)
        {
            var index = materializedArticle.IndexOf(prefix, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new InvalidOperationException($"Header line with prefix '{prefix}' was not found.");
            }

            var lineEnd = materializedArticle.IndexOf(separator, index, StringComparison.Ordinal);
            if (lineEnd < 0)
            {
                throw new InvalidOperationException($"Header line with prefix '{prefix}' did not contain separator '{separator}'.");
            }

            return separator.Length;
        }

        /// <summary>
        /// Wraps <paramref name="payload"/> in a single-part yEnc body whose trailer CRC is <see cref="YEncCrc32.Compute"/>.
        /// </summary>
        /// <param name="payload">Decoded bytes to encode between <c>=ybegin</c> and <c>=yend</c>.</param>
        /// <returns>The yEnc body, including control lines.</returns>
        private static byte[] BuildSyntheticSinglePartYEncBody(ReadOnlySpan<byte> payload)
        {
            var encodedPayload = EncodeYEncPayload(payload);
            var crc = YEncCrc32.Compute(payload);
            var prefix = Encoding.ASCII.GetBytes($"=ybegin line=128 size={payload.Length} name=test.bin\r\n");
            var suffix = Encoding.ASCII.GetBytes($"\r\n=yend size={payload.Length} crc32={crc:x8}\r\n");
            var body = new byte[prefix.Length + encodedPayload.Length + suffix.Length];
            Buffer.BlockCopy(prefix, 0, body, 0, prefix.Length);
            Buffer.BlockCopy(encodedPayload, 0, body, prefix.Length, encodedPayload.Length);
            Buffer.BlockCopy(suffix, 0, body, prefix.Length + encodedPayload.Length, suffix.Length);
            return body;
        }

        /// <summary>
        /// Encodes <paramref name="decoded"/> with the yEnc offset and escapes NUL, TAB, LF, CR, space, dot, and <c>=</c>.
        /// </summary>
        /// <param name="decoded">Decoded payload bytes.</param>
        /// <returns>Encoded bytes ending in LF.</returns>
        private static byte[] EncodeYEncPayload(ReadOnlySpan<byte> decoded)
        {
            var output = new List<byte>();
            for (var i = 0; i < decoded.Length; i++)
            {
                var encoded = unchecked((byte)(decoded[i] + 42));
                var mustEscape = encoded is 0 or 9 or 10 or 13 or 32 or 46 or 61;
                if (mustEscape)
                {
                    output.Add((byte)'=');
                    output.Add(unchecked((byte)(encoded + 64)));
                }
                else
                {
                    output.Add(encoded);
                }
            }

            if (output.Count == 0 || output[^1] != (byte)'\n')
            {
                output.Add((byte)'\r');
                output.Add((byte)'\n');
            }

            return [.. output];
        }
    }
}

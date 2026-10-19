using System.Text;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.DateParser;
using VectorNNTPD.Common.Articles.OverviewDb;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles.YEnc;
using VectorNNTPD.Common.Transport.ArticleTransfer;

namespace VectorNNTPD.Common.Tests.Articles.Parsing
{
    /// <summary>
    /// Verifies deterministic parser behavior for valid, malformed, and hostile NNTP article inputs.
    /// </summary>
    public sealed class NntpArticleParserTests
    {
        /// <summary>
        /// Canonical local identity used for Path augmentation assertions.
        /// </summary>
        private const string LocalFqdn = "bf01.usenet.ninja";

        /// <summary>
        /// Directory of SABCTools yEnc fixtures resolved once for parser integration tests.
        /// </summary>
        private static readonly string FixtureRoot = ResolveFixtureRoot();

        /// <summary>
        /// Fixture names stored with NNTP dot-stuffing. The parser suite currently lists only <c>test_bad_crc_end.yenc</c>.
        /// </summary>
        private static readonly HashSet<string> WireFormDotStuffedFixtureNames = new(StringComparer.Ordinal)
        {
            "test_bad_crc_end.yenc",
        };

        /// <summary>
        /// Verifies basic text article parsing and canonical metadata extraction.
        /// </summary>
        [Fact]
        public void Parse_WhenValidTextArticle_ReturnsAcceptedTextResult()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0200",
                    "Message-ID: <m1@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Subject: hello",
                ],
                body: "This is text.\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal(NntpArticleType.Text, result.ArticleType);
            Assert.Equal("Fri, 23 Aug 2024 05:30:10 +0000", FormatCanonicalUtc(result));
            Assert.Equal("news.usenet.ninja!" + LocalFqdn, FormatCanonicalPath(result));
            Assert.Equal("<m1@example.test>", Encoding.ASCII.GetString(result.OriginalMessageIdValue.Span));
            Assert.False(result.YEncDetected);
            Assert.Equal(YEncArticleValidationStatus.ValidNonYEnc, result.YEncValidation.Status);
            Assert.Equal(1, result.BodyLineCount);
        }

        /// <summary>
        /// Verifies References is identified as a known header without changing other header rules.
        /// </summary>
        [Fact]
        public void Parse_WhenReferencesPresent_ExposesKnownHeaderValueRange()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m-ref@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Subject: reply",
                    "References: <prev@example.test> <older@example.test>",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            NntpArticleHeaderEntry references = FindHeaderEntry(result.Headers, NntpArticleHeaderName.References);
            Assert.Equal(
                "<prev@example.test> <older@example.test>",
                Encoding.ASCII.GetString(article.AsSpan(references.ValueOffset, references.ValueLength)));
        }

        /// <summary>
        /// Duplicate References is not a new rejection; existing required-header rules still apply.
        /// </summary>
        [Fact]
        public void Parse_WhenDuplicateReferences_StillAcceptsWhenRequiredHeadersAreValid()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m-ref2@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "References: <first@example.test>",
                    "References: <second@example.test>",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.None, result.FailureCode);
            int referencesCount = 0;
            for (int i = 0; i < result.HeaderCount; i++)
            {
                if (result.GetHeader(i).KnownName == NntpArticleHeaderName.References)
                {
                    referencesCount++;
                }
            }

            Assert.Equal(2, referencesCount);
        }

        /// <summary>
        /// Body line count uses the parser walk: empty body is 0; unterminated tail counts.
        /// </summary>
        [Fact]
        public void Parse_BodyLineCount_MatchesParserLineWalk()
        {
            NntpArticleParser parser = new(LocalFqdn);
            NntpArticleParseResult emptyBody = parser.Parse(BuildArticle(
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <lines-empty@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: string.Empty));
            NntpArticleParseResult twoLines = parser.Parse(BuildArticle(
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <lines-two@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: "one\r\ntwo\r\n"));
            NntpArticleParseResult unterminated = parser.Parse(BuildArticle(
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <lines-tail@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: "one\r\ntwo"));

            Assert.True(emptyBody.IsAccepted);
            Assert.Equal(0, emptyBody.BodyLineCount);
            Assert.True(twoLines.IsAccepted);
            Assert.Equal(2, twoLines.BodyLineCount);
            Assert.True(unterminated.IsAccepted);
            Assert.Equal(2, unterminated.BodyLineCount);
        }

        /// <summary>
        /// Verifies MIME multipart classification from Content-Type hint.
        /// </summary>
        [Fact]
        public void Parse_WhenMultipartContentTypePresent_ClassifiesMimeMultipart()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m2@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Content-Type: multipart/mixed; boundary=abc",
                ],
                body: "--abc\r\npart\r\n--abc--\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal(NntpArticleType.MimeMultipart, result.ArticleType);
        }

        /// <summary>
        /// Verifies binary/encoded classification from transfer-encoding hint.
        /// </summary>
        [Fact]
        public void Parse_WhenBase64EncodingPresent_ClassifiesBinaryEncoded()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m3@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Content-Transfer-Encoding: base64",
                ],
                body: "QUJDREVGRw==\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal(NntpArticleType.BinaryEncoded, result.ArticleType);
        }

        /// <summary>
        /// Verifies date parse failures are rejected with explicit date failure classification.
        /// </summary>
        [Fact]
        public void Parse_WhenDateInvalid_RejectsWithDateFailureReason()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: INVALID-DATE",
                    "Message-ID: <m4@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.MissingOrInvalidDate, result.FailureCode);
            Assert.Equal(DateParseFailureReason.ParseFailed, result.DateFailureReason);
        }

        /// <summary>
        /// Verifies duplicate Message-ID headers are rejected deterministically.
        /// </summary>
        [Fact]
        public void Parse_WhenMessageIdDuplicated_Rejects()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m5@example.test>",
                    "Message-ID: <m6@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.DuplicateMessageId, result.FailureCode);
        }

        /// <summary>
        /// Two parsable <c>Date</c> headers are rejected before a canonical instant is published.
        /// </summary>
        [Fact]
        public void Parse_WhenTwoValidDateHeadersDiffer_RejectsDuplicateBeforeCanonicalUtc()
        {
            AssertDuplicateDateRejected(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                "Date: Sat, 24 Aug 2024 08:00:00 +0000");
        }

        /// <summary>
        /// An unparsable first <c>Date</c> does not fall through to a later <c>Date</c>.
        /// </summary>
        [Fact]
        public void Parse_WhenFirstDateIsInvalidAndSecondIsValid_RejectsDuplicate()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: not-a-date",
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <dup-date-invalid-first@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);
            ArticleRecordCreateResult created = ArticleRecordFactory.TryCreate(parser, article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.DuplicateDate, result.FailureCode);
            Assert.Equal(default, result.CanonicalUtc);
            Assert.Equal(NntpArticleHeaderName.Unknown, result.SelectedDateHeaderName);
            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.DuplicateDate, created.ParseFailure);
            Assert.Equal(default, created.Record.CanonicalUtc);
            Assert.Equal(0, created.Record.ArtSize);
        }

        /// <summary>
        /// A valid first <c>Date</c> followed by an unparsable second <c>Date</c> is still a duplicate.
        /// </summary>
        [Fact]
        public void Parse_WhenFirstDateIsValidAndSecondIsInvalid_RejectsDuplicate()
        {
            AssertDuplicateDateRejected(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                "Date: not-a-date");
        }

        /// <summary>
        /// Equivalent instants with different date text are still two <c>Date</c> headers.
        /// </summary>
        [Fact]
        public void Parse_WhenDuplicateDatesAreEquivalentInstants_RejectsDuplicate()
        {
            AssertDuplicateDateRejected(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                "Date: Fri, 23 Aug 2024 07:30:10 GMT");
        }

        /// <summary>
        /// <c>date</c> and <c>Date</c> are the same header name.
        /// </summary>
        [Fact]
        public void Parse_WhenDateHeaderCaseDiffers_RejectsDuplicate()
        {
            AssertDuplicateDateRejected(
                "date: Fri, 23 Aug 2024 07:30:10 +0000",
                "DATE: Sat, 24 Aug 2024 08:00:00 +0000");
        }

        /// <summary>
        /// A folded continuation stays one <c>Date</c>. A later <c>Date</c> line is the duplicate.
        /// </summary>
        [Fact]
        public void Parse_WhenFoldedDateIsFollowedByAnotherDate_RejectsDuplicate()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024\r\n" +
                "\t07:30:10 +0000\r\n" +
                "Date: Sat, 24 Aug 2024 08:00:00 +0000\r\n" +
                "Message-ID: <dup-date-folded@example.test>\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "\r\n" +
                "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.DuplicateDate, result.FailureCode);
            Assert.Equal(default, result.CanonicalUtc);
        }

        /// <summary>
        /// One <c>Date</c> still canonicalizes, and overview and transfer re-read that same stored value.
        /// </summary>
        [Fact]
        public void Parse_WhenSingleDateHeader_KeepsCanonicalUtcOverviewAndTransferOnTheSameValue()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0200",
                    "Message-ID: <single-date@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Subject: one date",
                ],
                body: "body\r\n");

            ArticleRecordCreateResult created = ArticleRecordFactory.TryCreate(parser, article);

            Assert.True(created.IsAccepted);
            Assert.Equal(NntpArticleHeaderName.Date, created.SelectedDateHeaderName);
            ArticleRecord record = created.Record;
            Assert.True(NewsDateParser.TryGetCanonicalUtc(record.Date, out DateTime storedUtc, out DateParseFailureReason storedFailure));
            Assert.Equal(DateParseFailureReason.None, storedFailure);
            Assert.Equal(record.CanonicalUtc, storedUtc);
            Assert.Equal("Fri, 23 Aug 2024 05:30:10 +0000", Encoding.ASCII.GetString(record.Date));

            OverviewArticleV1 overview = OverviewArticleV1Codec.Decode(OverviewArticleV1Codec.Encode(record));
            Assert.Equal(Encoding.ASCII.GetString(record.Date), overview.Date);

            byte[] artData = record.ArtData.Span[..record.ArtSize].ToArray();
            ArticleCanonicalTransferMeta meta = ArticleCanonicalTransferMeta.FromRecord(record, created.SelectedDateHeaderName);
            ArticleRecordCreateResult transferred = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, record.ArtId);

            Assert.True(transferred.IsAccepted);
            Assert.Equal(record.CanonicalUtc, transferred.Record.CanonicalUtc);
            Assert.True(record.Date.SequenceEqual(transferred.Record.Date));
        }

        /// <summary>
        /// Verifies Path augmentation prepends local host exactly once.
        /// </summary>
        [Fact]
        public void Parse_WhenPathPresentAndMissingLocalHost_PrependsLocalHost()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m7@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Path: news.example.org!feed2",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal("news.usenet.ninja!bf01.usenet.ninja!news.example.org!feed2", FormatCanonicalPath(result));
            Assert.False(result.ContainsOrganizationalTracker);
        }

        /// <summary>
        /// Verifies a traverse prepends the local host again when it is already leftmost.
        /// </summary>
        [Fact]
        public void Parse_WhenPathAlreadyContainsLocalHost_PrependsAgain()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m8@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Path: bf01.usenet.ninja!news.example.org",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal("news.usenet.ninja!bf01.usenet.ninja!bf01.usenet.ninja!news.example.org", FormatCanonicalPath(result));
            Assert.False(result.ContainsOrganizationalTracker);
        }

        /// <summary>
        /// Verifies an existing organizational tracker token is not duplicated when the application hop is prepended.
        /// </summary>
        [Fact]
        public void Parse_WhenPathAlreadyContainsOrganizationalTracker_DoesNotDuplicateTracker()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m8b@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Path: news.usenet.ninja!foo",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.True(result.ContainsOrganizationalTracker);
            Assert.Equal("bf01.usenet.ninja!news.usenet.ninja!foo", FormatCanonicalPath(result));
        }

        /// <summary>
        /// Verifies a later organizational tracker token is still treated as already present.
        /// </summary>
        [Fact]
        public void Parse_WhenOrganizationalTrackerOccursLater_DoesNotDuplicateTracker()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m8c@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Path: foo!news.usenet.ninja!bar",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.True(result.ContainsOrganizationalTracker);
            Assert.Equal("bf01.usenet.ninja!foo!news.usenet.ninja!bar", FormatCanonicalPath(result));
        }

        /// <summary>
        /// Verifies malformed path bytes are rejected.
        /// </summary>
        [Fact]
        public void Parse_WhenPathContainsControlByte_Rejects()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticleRaw(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m9@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                rawPath: "feed1\u001f!feed2",
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.ContainsIllegalControlByte, result.FailureCode);
        }

        /// <summary>
        /// Verifies missing header/body separator is rejected.
        /// </summary>
        [Fact]
        public void Parse_WhenHeaderBodySeparatorMissing_Rejects()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                "Message-ID: <m10@example.test>\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "NoTerminator");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.MissingHeaderBodySeparator, result.FailureCode);
        }

        /// <summary>
        /// Verifies header continuation lines append to preceding header value without rejection.
        /// </summary>
        [Fact]
        public void Parse_WhenHeaderContinuationPresent_Accepts()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                "Message-ID: <m11@example.test>\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "Subject: first\r\n" +
                "\tcontinued\r\n" +
                "\r\n" +
                "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
        }

        /// <summary>
        /// Verifies folded and unfolded Date values with equivalent semantics produce equivalent canonical UTC results.
        /// </summary>
        [Fact]
        public void Parse_WhenDateIsFoldedAndSemanticallyValid_AcceptsAndMatchesUnfoldedCanonicalDate()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] unfoldedArticle = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0200",
                    "Message-ID: <m11a@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: "body\r\n");

            byte[] foldedArticle = Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024\r\n" +
                "\t07:30:10 +0200\r\n" +
                "Message-ID: <m11b@example.test>\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "\r\n" +
                "body\r\n");

            byte[] foldedArticleBeforeParse = [.. foldedArticle];

            NntpArticleParseResult unfoldedResult = parser.Parse(unfoldedArticle);
            NntpArticleParseResult foldedResult = parser.Parse(foldedArticle);

            Assert.True(unfoldedResult.IsAccepted);
            Assert.True(foldedResult.IsAccepted);
            Assert.Equal(FormatCanonicalUtc(unfoldedResult), FormatCanonicalUtc(foldedResult));
            Assert.Equal(NntpArticleHeaderName.Date, foldedResult.SelectedDateHeaderName);
            Assert.Equal("Fri, 23 Aug 2024\r\n\t07:30:10 +0200", Encoding.ASCII.GetString(foldedResult.OriginalDateValue.Span));
            Assert.Equal(foldedArticleBeforeParse, foldedArticle);
            Assert.Equal(foldedArticle, foldedResult.ArticleBytes.ToArray());
        }

        /// <summary>
        /// Verifies folded Date values using single-character line terminators are semantically unfolded and canonicalized equivalently.
        /// </summary>
        /// <param name="lineTerminator">Header line terminator used for the folded Date boundary.</param>
        [Theory]
        [InlineData("\r")]
        [InlineData("\n")]
        public void Parse_WhenDateIsFoldedWithSingleCharacterTerminator_AcceptsAndMatchesUnfoldedCanonicalDate(string lineTerminator)
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] unfoldedArticle = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0200",
                    "Message-ID: <m11crlfu@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: "body\r\n");

            string foldedDateRaw = "Fri, 23 Aug 2024" + lineTerminator + " 07:30:10 +0200";
            byte[] foldedArticle = Encoding.ASCII.GetBytes(
                "Date: " + foldedDateRaw + lineTerminator +
                "Message-ID: <m11k@example.test>" + lineTerminator +
                "Newsgroups: alt.test" + lineTerminator +
                "From: user@example.test" + lineTerminator +
                lineTerminator +
                "body" + lineTerminator);

            byte[] foldedArticleBeforeParse = [.. foldedArticle];

            NntpArticleParseResult unfoldedResult = parser.Parse(unfoldedArticle);
            NntpArticleParseResult foldedResult = parser.Parse(foldedArticle);

            Assert.True(unfoldedResult.IsAccepted);
            Assert.True(foldedResult.IsAccepted);
            Assert.Equal(FormatCanonicalUtc(unfoldedResult), FormatCanonicalUtc(foldedResult));
            Assert.Equal(foldedDateRaw, Encoding.ASCII.GetString(foldedResult.OriginalDateValue.Span));
            Assert.Equal(foldedArticleBeforeParse, foldedArticle);
            Assert.Equal(foldedArticle, foldedResult.ArticleBytes.ToArray());
        }

        /// <summary>
        /// Verifies Date values spanning multiple continuation lines are unfolded consistently for semantic date parsing.
        /// </summary>
        [Fact]
        public void Parse_WhenDateUsesMultipleContinuationLines_AcceptsAndCanonicalizes()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = Encoding.ASCII.GetBytes(
                "Date: Fri,\r\n" +
                " 23 Aug\r\n" +
                " 2024 07:30:10\r\n" +
                " +0200\r\n" +
                "Message-ID: <m11c@example.test>\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "\r\n" +
                "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal("Fri, 23 Aug 2024 05:30:10 +0000", FormatCanonicalUtc(result));
            Assert.Equal("Fri,\r\n 23 Aug\r\n 2024 07:30:10\r\n +0200", Encoding.ASCII.GetString(result.OriginalDateValue.Span));
        }

        /// <summary>
        /// Verifies semantically invalid folded Date values remain rejected.
        /// </summary>
        [Fact]
        public void Parse_WhenFoldedDateIsSemanticallyInvalid_Rejects()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = Encoding.ASCII.GetBytes(
                "Date: BAD\r\n" +
                " DATE\r\n" +
                "Message-ID: <m11d@example.test>\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "\r\n" +
                "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.MissingOrInvalidDate, result.FailureCode);
        }

        /// <summary>
        /// Verifies malformed folded Date values still allow existing candidate fallback resolution.
        /// </summary>
        [Fact]
        public void Parse_WhenFoldedDateMalformedAndInjectionDateValid_UsesFallbackAndAccepts()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = Encoding.ASCII.GetBytes(
                "Date: BAD\r\n" +
                " DATE\r\n" +
                "Injection-Date: Fri, 23 Aug 2024 07:30:10 +0200\r\n" +
                "Message-ID: <m11e@example.test>\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "\r\n" +
                "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal(NntpArticleHeaderName.InjectionDate, result.SelectedDateHeaderName);
            Assert.Equal("Fri, 23 Aug 2024 05:30:10 +0000", FormatCanonicalUtc(result));
        }

        /// <summary>
        /// Verifies folded and unfolded From values with equivalent semantics produce equivalent acceptance behavior.
        /// </summary>
        [Fact]
        public void Parse_WhenFromIsFoldedAndSemanticallyEquivalent_MatchesUnfoldedAcceptance()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] unfoldedArticle = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m11f@example.test>",
                    "Newsgroups: alt.test",
                    "From: poster <user@example.test>",
                ],
                body: "body\r\n");

            byte[] foldedArticle = Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                "Message-ID: <m11g@example.test>\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: poster\r\n" +
                " <user@example.test>\r\n" +
                "\r\n" +
                "body\r\n");

            NntpArticleParseResult unfoldedResult = parser.Parse(unfoldedArticle);
            NntpArticleParseResult foldedResult = parser.Parse(foldedArticle);

            Assert.True(unfoldedResult.IsAccepted);
            Assert.True(foldedResult.IsAccepted);
            Assert.Equal(unfoldedResult.IsAccepted, foldedResult.IsAccepted);
        }

        /// <summary>
        /// Verifies folded From values using single-character line terminators are semantically unfolded and accepted equivalently to unfolded values.
        /// </summary>
        /// <param name="lineTerminator">Header line terminator used for the folded From boundary.</param>
        [Theory]
        [InlineData("\r")]
        [InlineData("\n")]
        public void Parse_WhenFromIsFoldedWithSingleCharacterTerminator_AcceptsAndMatchesUnfoldedAcceptance(string lineTerminator)
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] unfoldedArticle = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m11l-unfolded@example.test>",
                    "Newsgroups: alt.test",
                    "From: poster <user@example.test>",
                ],
                body: "body\r\n");

            byte[] foldedArticle = Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000" + lineTerminator +
                "Message-ID: <m11l-folded@example.test>" + lineTerminator +
                "Newsgroups: alt.test" + lineTerminator +
                "From: poster" + lineTerminator +
                " <user@example.test>" + lineTerminator +
                lineTerminator +
                "body" + lineTerminator);

            byte[] foldedArticleBeforeParse = [.. foldedArticle];

            NntpArticleParseResult unfoldedResult = parser.Parse(unfoldedArticle);
            NntpArticleParseResult foldedResult = parser.Parse(foldedArticle);

            Assert.True(unfoldedResult.IsAccepted);
            Assert.True(foldedResult.IsAccepted);
            Assert.Equal(unfoldedResult.IsAccepted, foldedResult.IsAccepted);
            Assert.Equal(foldedArticleBeforeParse, foldedArticle);
            Assert.Equal(foldedArticle, foldedResult.ArticleBytes.ToArray());
        }

        /// <summary>
        /// Verifies semantically invalid folded From values remain rejected after unfolding.
        /// </summary>
        [Fact]
        public void Parse_WhenFoldedFromIsSemanticallyInvalid_Rejects()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                "Message-ID: <m11h@example.test>\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: invalid\r\n" +
                " value-without-at-sign\r\n" +
                "\r\n" +
                "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.InvalidFrom, result.FailureCode);
        }

        /// <summary>
        /// Verifies folded From semantic length at the existing maximum boundary is accepted and one byte beyond remains rejected.
        /// </summary>
        [Fact]
        public void Parse_WhenFoldedFromSemanticLengthAtLimit_EnforcesBoundary()
        {
            const int maxFromLength = 2048;
            const int foldBoundaryCount = 2;
            const int perLineValueChunk = 900;
            NntpArticleParser parser = new(LocalFqdn);

            static string FoldHeaderValueForLineLimit(string value, int chunkSize)
            {
                if (value.Length <= chunkSize)
                {
                    return value;
                }

                StringBuilder builder = new(value.Length + ((value.Length / chunkSize) * 3));
                int index = 0;
                builder.Append(value.AsSpan(index, chunkSize));
                index += chunkSize;

                while (index < value.Length)
                {
                    int remaining = value.Length - index;
                    int take = Math.Min(chunkSize, remaining);
                    builder.Append("\r\n\t");
                    builder.Append(value.AsSpan(index, take));
                    index += take;
                }

                return builder.ToString();
            }

            int semanticLengthAtLimit = maxFromLength - foldBoundaryCount;
            string localAtLimit = new('a', 1023);
            string domainAtLimit = new('b', semanticLengthAtLimit - localAtLimit.Length - 1);
            string semanticAtLimitBeforeFold = localAtLimit + "@" + domainAtLimit;
            string foldedAtLimit = FoldHeaderValueForLineLimit(semanticAtLimitBeforeFold, perLineValueChunk);

            byte[] acceptedArticle = Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                "Message-ID: <m11i@example.test>\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: " + foldedAtLimit + "\r\n" +
                "\r\n" +
                "body\r\n");

            string domainTooLong = new('b', domainAtLimit.Length + 1);
            string semanticTooLongBeforeFold = localAtLimit + "@" + domainTooLong;
            string foldedTooLong = FoldHeaderValueForLineLimit(semanticTooLongBeforeFold, perLineValueChunk);
            byte[] rejectedArticle = Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                "Message-ID: <m11j@example.test>\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: " + foldedTooLong + "\r\n" +
                "\r\n" +
                "body\r\n");

            NntpArticleParseResult acceptedResult = parser.Parse(acceptedArticle);
            NntpArticleParseResult rejectedResult = parser.Parse(rejectedArticle);

            Assert.True(acceptedResult.IsAccepted);
            Assert.False(rejectedResult.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.InvalidFrom, rejectedResult.FailureCode);
        }

        /// <summary>
        /// Verifies continuation without a preceding header is rejected.
        /// </summary>
        [Fact]
        public void Parse_WhenContinuationWithoutPrecedingHeader_Rejects()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = Encoding.ASCII.GetBytes("\tbroken\r\n\r\nbody\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.MalformedHeaderContinuation, result.FailureCode);
        }

        /// <summary>
        /// Verifies non-yEnc articles do not trigger expensive yEnc rejection semantics.
        /// </summary>
        [Fact]
        public void Parse_WhenNoYEncMarkers_DoesNotRejectAsYEnc()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m12@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: "normal text body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.False(result.YEncDetected);
            Assert.Equal(YEncArticleValidationStatus.ValidNonYEnc, result.YEncValidation.Status);
        }

        /// <summary>
        /// Verifies parser integrates with yEnc validator and accepts valid synthetic single-part yEnc content.
        /// </summary>
        [Fact]
        public void Parse_WhenYEncFixtureValid_AcceptsAndClassifiesYEnc()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] body = BuildSyntheticSinglePartYEncBody(4096, "fixture-compat.bin");
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m13@example.test>",
                    "Newsgroups: alt.binaries.test",
                    "From: user@example.test",
                ],
                bodyBytes: body);

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.True(result.YEncDetected);
            Assert.Equal(NntpArticleType.YEnc, result.ArticleType);
            Assert.Equal(YEncArticleValidationStatus.ValidSinglePart, result.YEncValidation.Status);
        }

        /// <summary>
        /// Verifies yEnc failures are rejected with yEnc-decoding-failed semantics.
        /// </summary>
        [Fact]
        public void Parse_WhenYEncFixtureInvalid_RejectsAsYEncDecodingFailed()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] fixtureBytes = File.ReadAllBytes(Path.Combine(FixtureRoot, "test_bad_crc_end.yenc"));
            byte[] body = NormalizeFixtureBodyForParserContract("test_bad_crc_end.yenc", fixtureBytes);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m14@example.test>",
                    "Newsgroups: alt.binaries.test",
                    "From: user@example.test",
                ],
                bodyBytes: body);

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.True(result.YEncDetected);
            Assert.Equal(NntpArticleParseFailureCode.YEncDecodingFailed, result.FailureCode);
            Assert.Equal(YEncArticleValidationStatus.CrcMismatch, result.YEncValidation.Status);
        }

        /// <summary>
        /// Verifies parser rejects NUL bytes in headers as hostile input.
        /// </summary>
        [Fact]
        public void Parse_WhenHeaderContainsNul_Rejects()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                "Message-ID: <m15@example.test>\0\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n\r\nbody\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.ContainsIllegalControlByte, result.FailureCode);
        }

        /// <summary>
        /// Verifies CR-only separators are accepted for both header lines and header/body boundary.
        /// </summary>
        [Fact]
        public void Parse_WhenArticleUsesCrOnlySeparators_AcceptsAndSeparatesBodyCorrectly()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r" +
                "Message-ID: <m16@example.test>\r" +
                "Newsgroups: alt.test\r" +
                "From: user@example.test\r" +
                "\r" +
                "body-cr-only\r");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal(NntpArticleType.Text, result.ArticleType);
            Assert.NotEqual(0, result.HeaderBytes.Length);
            Assert.NotEqual(0, result.BodyBytes.Length);
            Assert.Equal("body-cr-only\r", Encoding.ASCII.GetString(result.BodyBytes.Span));
        }

        /// <summary>
        /// Verifies parser accepts mixed blank-line boundary terminator combinations when each physical terminator token is individually valid.
        /// </summary>
        /// <param name="lastHeaderTerminator">Terminator ending the final physical header line.</param>
        /// <param name="boundaryTerminator">Terminator used for the empty separator line before the body.</param>
        [Theory]
        [InlineData("\r\n", "\n")]
        [InlineData("\n", "\r\n")]
        [InlineData("\n", "\r")]
        [InlineData("\r", "\r\n")]
        [InlineData("\r\n", "\r")]
        public void Parse_WhenHeaderBodyBoundaryUsesMixedTerminators_AcceptsAndPreservesBodySplit(string lastHeaderTerminator, string boundaryTerminator)
        {
            NntpArticleParser parser = new(LocalFqdn);
            string body = "body-line-1\r\nbody-line-2\nbody-line-3\rEND";
            string articleText =
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                "Message-ID: <m16-mixed-boundary@example.test>\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test" + lastHeaderTerminator +
                boundaryTerminator +
                body;

            byte[] article = Encoding.ASCII.GetBytes(articleText);
            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal(body, Encoding.ASCII.GetString(result.BodyBytes.Span));
            Assert.Equal(articleText.Length - body.Length, result.HeaderBytes.Length);

            byte[] boundaryBytes = Encoding.ASCII.GetBytes(lastHeaderTerminator + boundaryTerminator);
            Assert.True(result.HeaderBytes.Span.EndsWith(boundaryBytes));
        }

        /// <summary>
        /// Verifies yEnc body classification remains deterministic when yEnc detection scan bytes are smaller than the marker offset.
        /// </summary>
        [Fact]
        public void Parse_WhenYEncMarkerIsBeyondDetectionScanWindow_DoesNotClassifyAsYEnc()
        {
            NntpArticleParser parser = new(
                LocalFqdn,
                NntpArticleParserOptions.Default with
                {
                    YEncDetectionScanBytes = 64,
                });

            byte[] yEncBody = BuildSyntheticSinglePartYEncBody(512, "late-marker.bin");
            byte[] paddedBody = new byte[1026 + yEncBody.Length];
            for (int i = 0; i < 1024; i++)
            {
                paddedBody[i] = (byte)'A';
            }

            paddedBody[1024] = (byte)'\r';
            paddedBody[1025] = (byte)'\n';
            Buffer.BlockCopy(yEncBody, 0, paddedBody, 1026, yEncBody.Length);

            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m17@example.test>",
                    "Newsgroups: alt.binaries.test",
                    "From: user@example.test",
                ],
                bodyBytes: paddedBody);

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.False(result.YEncDetected);
            Assert.NotEqual(NntpArticleType.YEnc, result.ArticleType);
            Assert.Equal(YEncArticleValidationStatus.ValidNonYEnc, result.YEncValidation.Status);
        }

        /// <summary>
        /// Verifies malformed Path separators are normalized by dropping empty path components while preserving deterministic local prepend behavior.
        /// </summary>
        [Fact]
        public void Parse_WhenPathContainsRepeatedSeparators_NormalizesAndPrependsLocalHostOnce()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m18@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Path: !!feed1!!!feed2!!",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal("news.usenet.ninja!bf01.usenet.ninja!feed1!feed2", FormatCanonicalPath(result));
        }

        /// <summary>
        /// Verifies duplicate Path headers are rejected deterministically.
        /// </summary>
        [Fact]
        public void Parse_WhenPathDuplicated_Rejects()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m19@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Path: feed1",
                    "Path: feed2",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.DuplicatePath, result.FailureCode);
        }

        /// <summary>
        /// Verifies body lines that begin with dot sequences preserve bytes exactly and are not treated as transport terminators.
        /// </summary>
        [Fact]
        public void Parse_WhenBodyContainsDotLines_PreservesBodyBytesExactly()
        {
            NntpArticleParser parser = new(LocalFqdn);
            string body = ".\r\n..\r\n...\r\n....\r\n";
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m20@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: body);

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal(body, Encoding.ASCII.GetString(result.BodyBytes.Span));
        }

        /// <summary>
        /// Verifies an article with headers and an explicitly empty body is accepted with a zero-length body slice.
        /// </summary>
        [Fact]
        public void Parse_WhenHeaderOnlyArticleHasSeparator_AcceptsWithEmptyBody()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m21@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: string.Empty);

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal(0, result.BodyBytes.Length);
            Assert.Equal(NntpArticleType.Text, result.ArticleType);
        }

        /// <summary>
        /// Verifies invalid Newsgroups header values with consecutive separators are rejected.
        /// </summary>
        [Fact]
        public void Parse_WhenNewsgroupsContainsEmptyToken_Rejects()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m22@example.test>",
                    "Newsgroups: alt.test,,alt.misc",
                    "From: user@example.test",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.InvalidNewsgroups, result.FailureCode);
        }

        /// <summary>
        /// Verifies date resolver integration uses fallback candidate headers when primary Date is absent.
        /// </summary>
        [Fact]
        public void Parse_WhenDateMissingAndInjectionDatePresent_UsesFallbackDateHeader()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Injection-Date: Fri, 23 Aug 2024 07:30:10 +0200",
                    "Message-ID: <m23@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal("Fri, 23 Aug 2024 05:30:10 +0000", FormatCanonicalUtc(result));
        }

        /// <summary>
        /// Verifies malformed Date with a valid fallback date header is accepted using resolver candidate ordering.
        /// </summary>
        [Fact]
        public void Parse_WhenDateIsMalformedButInjectionDateValid_UsesFallbackAndAccepts()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: BAD-DATE",
                    "Injection-Date: Fri, 23 Aug 2024 07:30:10 +0200",
                    "Message-ID: <m24@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal("Fri, 23 Aug 2024 05:30:10 +0000", FormatCanonicalUtc(result));
        }

        /// <summary>
        /// Verifies malformed Date is rejected when no fallback date headers can be resolved.
        /// </summary>
        [Fact]
        public void Parse_WhenAllCandidateDateHeadersAreMalformed_RejectsWithInvalidDate()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: BAD-DATE",
                    "Injection-Date: ALSO-BAD",
                    "NNTP-Posting-Date: STILL-BAD",
                    "Message-ID: <m25@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.MissingOrInvalidDate, result.FailureCode);
        }

        /// <summary>
        /// Verifies body bytes can include NUL and arbitrary binary content without parser rejection when headers are valid.
        /// </summary>
        [Fact]
        public void Parse_WhenBodyContainsBinaryBytesIncludingNul_AcceptsAndPreservesBody()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] body = [0x00, 0x01, 0x02, 0x03, (byte)'A', (byte)'\r', (byte)'\n', 0xFF];
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m26@example.test>",
                    "Newsgroups: alt.binaries.test",
                    "From: user@example.test",
                    "Content-Transfer-Encoding: binary",
                ],
                bodyBytes: body);

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal(NntpArticleType.BinaryEncoded, result.ArticleType);
            Assert.Equal(body, result.BodyBytes.ToArray());
        }

        /// <summary>
        /// Verifies parser rejects overly long header sections using configured guardrails without throwing.
        /// </summary>
        [Fact]
        public void Parse_WhenHeaderSectionExceedsConfiguredLimit_RejectsWithHeaderSectionTooLarge()
        {
            NntpArticleParser parser = new(
                LocalFqdn,
                NntpArticleParserOptions.Default with
                {
                    MaxHeaderSectionBytes = 128,
                });

            StringBuilder oversizedHeaderValueBuilder = new(512);
            for (int i = 0; i < 256; i++)
            {
                _ = oversizedHeaderValueBuilder.Append('x');
            }

            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m27@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    $"X-Long: {oversizedHeaderValueBuilder}",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.HeaderSectionTooLarge, result.FailureCode);
        }

        /// <summary>
        /// Verifies parser rejects missing required Newsgroups header.
        /// </summary>
        [Fact]
        public void Parse_WhenNewsgroupsMissing_Rejects()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m28@example.test>",
                    "From: user@example.test",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.MissingNewsgroups, result.FailureCode);
        }

        /// <summary>
        /// Verifies parser rejects missing required Message-ID header.
        /// </summary>
        [Fact]
        public void Parse_WhenMessageIdMissing_Rejects()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.MissingMessageId, result.FailureCode);
        }

        /// <summary>
        /// Verifies malformed Message-ID syntax is rejected.
        /// </summary>
        [Fact]
        public void Parse_WhenMessageIdMalformed_Rejects()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: malformed-id",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.InvalidMessageId, result.FailureCode);
            Assert.True(result.OriginalMessageIdValue.IsEmpty);
        }

        /// <summary>
        /// Verifies parser rejects duplicate Newsgroups headers.
        /// </summary>
        [Fact]
        public void Parse_WhenNewsgroupsDuplicated_Rejects()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m29@example.test>",
                    "Newsgroups: alt.test",
                    "Newsgroups: alt.misc",
                    "From: user@example.test",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.DuplicateNewsgroups, result.FailureCode);
        }

        /// <summary>
        /// Verifies malformed first line without colon is treated as non-header input and accepted as article body.
        /// </summary>
        [Fact]
        public void Parse_WhenFirstLineHasNoColon_TreatsWholeArticleAsBodyAndRejectsByMissingRequiredHeaders()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = Encoding.ASCII.GetBytes("not-a-header-line\r\nsecond line\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.MissingMessageId, result.FailureCode);
            Assert.Equal(0, result.HeaderBytes.Length);
            Assert.Equal(article, result.BodyBytes.ToArray());
        }

        /// <summary>
        /// Verifies parser accepts LF-only header/body separators and preserves body bytes.
        /// </summary>
        [Fact]
        public void Parse_WhenArticleUsesLfOnlySeparators_AcceptsAndSeparatesBodyCorrectly()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\n" +
                "Message-ID: <m30@example.test>\n" +
                "Newsgroups: alt.test\n" +
                "From: user@example.test\n" +
                "\n" +
                "body-lf-only\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal("body-lf-only\n", Encoding.ASCII.GetString(result.BodyBytes.Span));
        }

        /// <summary>
        /// Verifies parser classifies MIME multipart without requiring yEnc detection.
        /// </summary>
        [Fact]
        public void Parse_WhenMimeMultipartAndNoYEncMarkers_ClassifiesAsMimeMultipart()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m31@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Content-Type: multipart/alternative; boundary=b",
                ],
                body: "--b\r\ntext\r\n--b--\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal(NntpArticleType.MimeMultipart, result.ArticleType);
            Assert.False(result.YEncDetected);
            Assert.Equal(YEncArticleValidationStatus.ValidNonYEnc, result.YEncValidation.Status);
        }

        /// <summary>
        /// Verifies parser classifies binary transfer encoded content without yEnc markers.
        /// </summary>
        [Fact]
        public void Parse_WhenBinaryTransferEncodingAndNoYEncMarkers_ClassifiesAsBinaryEncoded()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m32@example.test>",
                    "Newsgroups: alt.binaries.test",
                    "From: user@example.test",
                    "Content-Transfer-Encoding: binary",
                ],
                body: "\u0001\u0002\u0003\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal(NntpArticleType.BinaryEncoded, result.ArticleType);
            Assert.False(result.YEncDetected);
            Assert.Equal(YEncArticleValidationStatus.ValidNonYEnc, result.YEncValidation.Status);
        }

        /// <summary>
        /// Verifies parser accepts article size one byte below the fixed 5 MiB hard boundary.
        /// </summary>
        [Fact]
        public void Parse_WhenArticleSizeIsLimitMinusOne_Accepts()
        {
            NntpArticleParser parser = new(LocalFqdn);
            int payloadBytes = ArticleResourceLimits.MaxArticleBytes - BuildArticleHeaderBytes("<m16-parse-minus-one@example.test>").Length - 1;
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m16-parse-minus-one@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                bodyBytes: CreateLineBoundedBodyBytes(payloadBytes, (byte)'A'));

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal(ArticleResourceLimits.MaxArticleBytes - 1, result.ArticleBytes.Length);
        }

        /// <summary>
        /// Verifies parser accepts article size exactly at the fixed 5 MiB hard boundary.
        /// </summary>
        [Fact]
        public void Parse_WhenArticleSizeIsExactlyLimit_Accepts()
        {
            NntpArticleParser parser = new(LocalFqdn);
            int payloadBytes = ArticleResourceLimits.MaxArticleBytes - BuildArticleHeaderBytes("<m16-parse-exact@example.test>").Length;
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m16-parse-exact@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                bodyBytes: CreateLineBoundedBodyBytes(payloadBytes, (byte)'B'));

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal(ArticleResourceLimits.MaxArticleBytes, result.ArticleBytes.Length);
        }

        /// <summary>
        /// Verifies parser rejects article size one byte above the fixed 5 MiB hard boundary.
        /// </summary>
        [Fact]
        public void Parse_WhenArticleSizeIsLimitPlusOne_RejectsArticleTooLarge()
        {
            NntpArticleParser parser = new(LocalFqdn);
            int payloadBytes = ArticleResourceLimits.MaxArticleBytes - BuildArticleHeaderBytes("<m16-parse-plus-one@example.test>").Length + 1;
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m16-parse-plus-one@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                bodyBytes: CreateFilledBytes(payloadBytes, (byte)'C'));

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.ArticleTooLarge, result.FailureCode);
        }

        /// <summary>
        /// Verifies parser respects caller article limit when configured below the hard 5 MiB ceiling.
        /// </summary>
        [Fact]
        public void Parse_WhenCallerArticleLimitIsBelowHardCeiling_RemainsEffective()
        {
            const int callerArticleLimit = 1024 * 1024;
            NntpArticleParser parser = new(
                LocalFqdn,
                NntpArticleParserOptions.Default with
                {
                    MaxArticleBytes = callerArticleLimit,
                });

            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m16-caller-article-below@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                bodyBytes: CreateFilledBytes(callerArticleLimit + 1 - BuildArticleHeaderBytes("<m16-caller-article-below@example.test>").Length, (byte)'D'));

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.ArticleTooLarge, result.FailureCode);
        }

        /// <summary>
        /// Verifies parser respects caller article limit when configured exactly at the hard 5 MiB ceiling.
        /// </summary>
        [Fact]
        public void Parse_WhenCallerArticleLimitIsExactlyHardCeiling_RemainsEffective()
        {
            NntpArticleParser parser = new(
                LocalFqdn,
                NntpArticleParserOptions.Default with
                {
                    MaxArticleBytes = ArticleResourceLimits.MaxArticleBytes,
                });

            int payloadBytes = ArticleResourceLimits.MaxArticleBytes - BuildArticleHeaderBytes("<m16-caller-article-eq@example.test>").Length;
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m16-caller-article-eq@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                bodyBytes: CreateLineBoundedBodyBytes(payloadBytes, (byte)'E'));

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal(ArticleResourceLimits.MaxArticleBytes, result.ArticleBytes.Length);
        }

        /// <summary>
        /// Verifies a caller article limit above 5 MiB is honored. The old global ceiling is not applied.
        /// </summary>
        [Fact]
        public void Parse_WhenCallerArticleLimitExceedsFormerHardCeiling_HonorsCallerLimit()
        {
            int callerLimit = ArticleResourceLimits.MaxArticleBytes + 1;
            NntpArticleParser parser = new(
                LocalFqdn,
                NntpArticleParserOptions.Default with
                {
                    MaxArticleBytes = callerLimit,
                });

            int payloadBytes = callerLimit - BuildArticleHeaderBytes("<m16-caller-article-above@example.test>").Length;
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m16-caller-article-above@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                bodyBytes: CreateLineBoundedBodyBytes(payloadBytes, (byte)'F'));

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal(callerLimit, result.ArticleBytes.Length);
        }

        /// <summary>
        /// Verifies parser accepts 1023-character header line and 1024-character header line, then rejects 1025-character header line.
        /// </summary>
        [Fact]
        public void Parse_WhenHeaderLineLengthHits1023And1024And1025_EnforcesBoundary()
        {
            NntpArticleParser parser = new(LocalFqdn);

            byte[] line1023 = BuildHeaderLineLengthArticle("<m16-line-1023@example.test>", 1023);
            byte[] line1024 = BuildHeaderLineLengthArticle("<m16-line-1024@example.test>", 1024);
            byte[] line1025 = BuildHeaderLineLengthArticle("<m16-line-1025@example.test>", 1025);

            NntpArticleParseResult result1023 = parser.Parse(line1023);
            NntpArticleParseResult result1024 = parser.Parse(line1024);
            NntpArticleParseResult result1025 = parser.Parse(line1025);

            Assert.True(result1023.IsAccepted);
            Assert.True(result1024.IsAccepted);
            Assert.False(result1025.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.HeaderLineTooLong, result1025.FailureCode);
        }

        /// <summary>
        /// Verifies parser keeps caller header line limit below hard ceiling effective for header parsing.
        /// </summary>
        [Fact]
        public void Parse_WhenCallerLineLimitIsBelowHardCeilingForHeaders_RemainsEffective()
        {
            const int callerLineLimit = 256;
            NntpArticleParser parser = new(
                LocalFqdn,
                NntpArticleParserOptions.Default with
                {
                    MaxHeaderLineBytes = callerLineLimit,
                });

            byte[] article = BuildHeaderLineLengthArticle("<m16-line-caller-below@example.test>", callerLineLimit + 1);
            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.HeaderLineTooLong, result.FailureCode);
        }

        /// <summary>
        /// Verifies parser keeps caller line limit exactly at hard ceiling effective.
        /// </summary>
        [Fact]
        public void Parse_WhenCallerLineLimitIsExactlyHardCeiling_RemainsEffective()
        {
            NntpArticleParser parser = new(
                LocalFqdn,
                NntpArticleParserOptions.Default with
                {
                    MaxHeaderLineBytes = ArticleResourceLimits.MaxArticleLineBytes,
                });

            byte[] line1024 = BuildHeaderLineLengthArticle("<m16-line-caller-eq@example.test>", 1024);
            byte[] line1025 = BuildHeaderLineLengthArticle("<m16-line-caller-eq-plus@example.test>", 1025);

            NntpArticleParseResult accepted = parser.Parse(line1024);
            NntpArticleParseResult rejected = parser.Parse(line1025);

            Assert.True(accepted.IsAccepted);
            Assert.False(rejected.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.HeaderLineTooLong, rejected.FailureCode);
        }

        /// <summary>
        /// Verifies parser caps caller line limit above hard ceiling to 1024 for header parsing.
        /// </summary>
        [Fact]
        public void Parse_WhenCallerLineLimitExceedsHardCeiling_IsCappedAtHardCeilingForHeaders()
        {
            NntpArticleParser parser = new(
                LocalFqdn,
                NntpArticleParserOptions.Default with
                {
                    MaxHeaderLineBytes = ArticleResourceLimits.MaxArticleLineBytes + 256,
                });

            byte[] article = BuildHeaderLineLengthArticle("<m16-line-caller-above@example.test>", 1025);
            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.HeaderLineTooLong, result.FailureCode);
        }

        /// <summary>
        /// Verifies parser keeps caller line limit below hard ceiling effective for body-line validation.
        /// </summary>
        [Fact]
        public void Parse_WhenCallerLineLimitIsBelowHardCeilingForBody_RemainsEffective()
        {
            const int callerLineLimit = 256;
            NntpArticleParser parser = new(
                LocalFqdn,
                NntpArticleParserOptions.Default with
                {
                    MaxHeaderLineBytes = callerLineLimit,
                });

            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m16-body-line-caller-below@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: new string('b', callerLineLimit + 1) + "\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.BodyLineTooLong, result.FailureCode);
        }

        /// <summary>
        /// Verifies parser caps caller line limit above hard ceiling to 1024 for body-line validation.
        /// </summary>
        [Fact]
        public void Parse_WhenCallerLineLimitExceedsHardCeiling_IsCappedAtHardCeilingForBody()
        {
            NntpArticleParser parser = new(
                LocalFqdn,
                NntpArticleParserOptions.Default with
                {
                    MaxHeaderLineBytes = ArticleResourceLimits.MaxArticleLineBytes + 512,
                });

            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m16-body-line-caller-above@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: new string('c', 1025) + "\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.BodyLineTooLong, result.FailureCode);
        }

        /// <summary>
        /// Verifies newline-free header scanning remains bounded at 1024-character line limit.
        /// </summary>
        [Fact]
        public void Parse_WhenHeaderLineHasNoNewlineUntilAfterBoundary_RejectsBoundedly()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                "Message-ID: <m16-newline-free@example.test>\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "Subject: " + new string('x', 1025) +
                "\r\n\r\n" +
                "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.HeaderLineTooLong, result.FailureCode);

            int subjectPrefixIndex = Encoding.ASCII.GetString(article).IndexOf("Subject: ", StringComparison.Ordinal);
            Assert.True(subjectPrefixIndex >= 0);
            Assert.Equal(subjectPrefixIndex + 1024, result.HeaderBytes.Length);
        }

        /// <summary>
        /// Verifies body line boundaries enforce 1023/1024 accepted and 1025 rejected.
        /// </summary>
        [Fact]
        public void Parse_WhenBodyLineLengthHits1023And1024And1025_EnforcesBoundary()
        {
            NntpArticleParser parser = new(LocalFqdn);

            byte[] bodyLine1023 = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m16-body-line-1023@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: new string('a', 1023) + "\r\n");

            byte[] bodyLine1024 = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m16-body-line-1024@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: new string('b', 1024) + "\r\n");

            byte[] bodyLine1025 = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m16-body-line-1025@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: new string('c', 1025) + "\r\n");

            NntpArticleParseResult result1023 = parser.Parse(bodyLine1023);
            NntpArticleParseResult result1024 = parser.Parse(bodyLine1024);
            NntpArticleParseResult result1025 = parser.Parse(bodyLine1025);

            Assert.True(result1023.IsAccepted);
            Assert.True(result1024.IsAccepted);
            Assert.False(result1025.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.BodyLineTooLong, result1025.FailureCode);
        }

        /// <summary>
        /// Verifies newline-free body scanning rejects once the 1024-character boundary is crossed.
        /// </summary>
        [Fact]
        public void Parse_WhenBodyLineHasNoNewlineUntilAfterBoundary_RejectsBoundedly()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m16-body-newline-free@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: new string('z', 1025) + "\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.BodyLineTooLong, result.FailureCode);
        }

        /// <summary>
        /// Verifies newline-free overlength body lines are rejected at the bounded 1024-character contract position, not after scanning the full trailing payload.
        /// </summary>
        [Fact]
        public void Parse_WhenBodyLineExceedsBoundaryBeforeLargeTrailingPayload_RejectsBoundedly()
        {
            NntpArticleParser parser = new(LocalFqdn);
            string oversizedLine = new('n', 1025);
            string trailingPayload = new('t', 256 * 1024);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m16-body-bounded-scan@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: oversizedLine + trailingPayload);

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.BodyLineTooLong, result.FailureCode);
        }

        /// <summary>
        /// Verifies parser rejects completely empty input deterministically.
        /// </summary>
        [Fact]
        public void Parse_WhenArticleIsEmpty_RejectsAsEmptyArticle()
        {
            NntpArticleParser parser = new(LocalFqdn);

            NntpArticleParseResult result = parser.Parse(ReadOnlyMemory<byte>.Empty);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.EmptyArticle, result.FailureCode);
        }

        /// <summary>
        /// Verifies parser rejects malformed header continuation with no preceding header while using CR-only separators.
        /// </summary>
        [Fact]
        public void Parse_WhenCrOnlyArticleStartsWithContinuation_RejectsMalformedHeaderContinuation()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = Encoding.ASCII.GetBytes("\tbroken\r\rbody\r");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.MalformedHeaderContinuation, result.FailureCode);
        }

        /// <summary>
        /// Verifies malformed yEnc payload is rejected after detection and validator integration.
        /// </summary>
        [Fact]
        public void Parse_WhenYEncPayloadHasInvalidEscape_RejectsAsYEncDecodingFailed()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] body = File.ReadAllBytes(Path.Combine(FixtureRoot, "test_invalid_escape.yenc"));
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m33@example.test>",
                    "Newsgroups: alt.binaries.test",
                    "From: user@example.test",
                ],
                bodyBytes: body);

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.True(result.YEncDetected);
            Assert.Equal(NntpArticleParseFailureCode.YEncDecodingFailed, result.FailureCode);
            Assert.Equal(YEncArticleValidationStatus.InvalidEscapeSequence, result.YEncValidation.Status);
        }

        /// <summary>
        /// Verifies parser rejects Path values that contain only separators and whitespace by normalizing to local FQDN.
        /// </summary>
        [Fact]
        public void Parse_WhenPathContainsOnlySeparatorsAndWhitespace_UsesLocalFqdnCanonicalPath()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m34@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Path:   ! !   !!  ",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.Equal("news.usenet.ninja!" + LocalFqdn, FormatCanonicalPath(result));
        }

        /// <summary>
        /// Verifies parser retains unknown headers without rejecting valid articles.
        /// </summary>
        [Fact]
        public void Parse_WhenUnknownHeadersPresent_AcceptsAndPreservesHeaderEntries()
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m35@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "X-Custom-One: alpha",
                    "X-Custom-Two: beta",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.True(result.IsAccepted);
            Assert.True(result.Headers.Count >= 6);
        }

        /// <summary>
        /// Verifies folded-header aggregate value length is enforced at limit-1, exact limit, and limit+1 boundaries.
        /// </summary>
        [Fact]
        public void Parse_WhenFoldedHeaderAggregateValueCrossesConfiguredBoundary_EnforcesExactLimit()
        {
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m36@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Subject: " + new string('s', 48),
                    " " + new string('t', 48),
                    "\t" + new string('u', 48),
                ],
                body: "body\r\n");

            NntpArticleParser baselineParser = new(LocalFqdn);
            NntpArticleParseResult baseline = baselineParser.Parse(article);
            Assert.True(baseline.IsAccepted);

            NntpArticleHeaderEntry subject = FindHeaderEntry(baseline.Headers, NntpArticleHeaderName.Subject);
            Assert.True(subject.ValueLength > 1);

            NntpArticleParser belowLimitParser = new(
                LocalFqdn,
                NntpArticleParserOptions.Default with
                {
                    MaxHeaderValueBytes = subject.ValueLength - 1,
                });

            NntpArticleParser exactLimitParser = new(
                LocalFqdn,
                NntpArticleParserOptions.Default with
                {
                    MaxHeaderValueBytes = subject.ValueLength,
                });

            NntpArticleParser aboveLimitParser = new(
                LocalFqdn,
                NntpArticleParserOptions.Default with
                {
                    MaxHeaderValueBytes = subject.ValueLength + 1,
                });

            NntpArticleParseResult belowLimitResult = belowLimitParser.Parse(article);
            NntpArticleParseResult exactLimitResult = exactLimitParser.Parse(article);
            NntpArticleParseResult aboveLimitResult = aboveLimitParser.Parse(article);

            Assert.False(belowLimitResult.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.HeaderValueTooLong, belowLimitResult.FailureCode);
            Assert.True(exactLimitResult.IsAccepted);
            Assert.True(aboveLimitResult.IsAccepted);
        }

        /// <summary>
        /// Verifies parser accepts header counts at limit-1 and exact limit.
        /// </summary>
        [Fact]
        public void Parse_WhenHeaderCountAtOrBelowLimit_Accepts()
        {
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m37@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Subject: first",
                    " second",
                ],
                body: "body\r\n");

            NntpArticleParser limitMinusOneParser = new(
                LocalFqdn,
                NntpArticleParserOptions.Default with
                {
                    MaxHeaderCount = 6,
                });

            NntpArticleParser exactLimitParser = new(
                LocalFqdn,
                NntpArticleParserOptions.Default with
                {
                    MaxHeaderCount = 5,
                });

            NntpArticleParseResult limitMinusOneResult = limitMinusOneParser.Parse(article);
            NntpArticleParseResult exactLimitResult = exactLimitParser.Parse(article);

            Assert.True(limitMinusOneResult.IsAccepted);
            Assert.True(exactLimitResult.IsAccepted);
        }

        /// <summary>
        /// Verifies parser rejects when adding one header would exceed the exact header-count limit.
        /// </summary>
        [Fact]
        public void Parse_WhenHeaderCountExceedsLimitByOne_Rejects()
        {
            byte[] article = BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    "Message-ID: <m38@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    "Subject: first",
                    " second",
                    "X-Extra: value",
                ],
                body: "body\r\n");

            NntpArticleParser parser = new(
                LocalFqdn,
                NntpArticleParserOptions.Default with
                {
                    MaxHeaderCount = 5,
                });

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.TooManyHeaders, result.FailureCode);
        }

        /// <summary>
        /// Verifies deterministic parser behavior for random hostile input and absence of runtime exceptions.
        /// </summary>
        [Fact]
        public void Parse_WhenFuzzedInputProvided_DoesNotThrowAndIsDeterministic()
        {
            NntpArticleParser parser = new(LocalFqdn);
            Random random = new(12345);

            for (int i = 0; i < 500; i++)
            {
                int length = random.Next(0, 8192);
                byte[] data = new byte[length];
                random.NextBytes(data);

                Exception? ex1 = Record.Exception(() => parser.Parse(data));
                Exception? ex2 = Record.Exception(() => parser.Parse(data));
                Assert.Null(ex1);
                Assert.Null(ex2);

                NntpArticleParseResult first = parser.Parse(data);
                NntpArticleParseResult second = parser.Parse(data);
                Assert.Equal(first.IsAccepted, second.IsAccepted);
                Assert.Equal(first.FailureCode, second.FailureCode);
                Assert.Equal(first.ArticleType, second.ArticleType);
                Assert.Equal(first.YEncDetected, second.YEncDetected);
                Assert.Equal(first.YEncValidation.Status, second.YEncValidation.Status);
            }
        }

        /// <summary>
        /// Formats the parse result's canonical UTC date as ASCII.
        /// </summary>
        /// <param name="result">Accepted or rejected parse result whose date formatter is invoked.</param>
        /// <returns>The formatted date, or an empty string when the formatter declines the destination.</returns>
        private static string FormatCanonicalUtc(NntpArticleParseResult result)
        {
            Span<byte> destination = stackalloc byte[40];
            if (!result.TryFormatCanonicalUtc(destination, out int written))
            {
                return string.Empty;
            }

            return Encoding.ASCII.GetString(destination[..written]);
        }

        /// <summary>
        /// Formats the parse result's canonical Path using <see cref="ArticlePathMode.Traverse"/>.
        /// </summary>
        /// <param name="result">Parse result whose Path writer is invoked.</param>
        /// <returns>The canonical Path text, or an empty string when the writer declines the destination.</returns>
        private static string FormatCanonicalPath(NntpArticleParseResult result)
        {
            Span<byte> destination = stackalloc byte[1024];
            if (!result.TryWriteCanonicalPath(ArticlePathMode.Traverse, destination, out int written))
            {
                return string.Empty;
            }

            return Encoding.ASCII.GetString(destination[..written]);
        }

        /// <summary>
        /// Finds one parsed header entry by known header name.
        /// </summary>
        /// <param name="headers">Header entries from the parse result.</param>
        /// <param name="knownName">Known header to locate.</param>
        /// <returns>The first entry whose known name equals <paramref name="knownName"/>.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no entry has <paramref name="knownName"/>.</exception>
        private static NntpArticleHeaderEntry FindHeaderEntry(IReadOnlyList<NntpArticleHeaderEntry> headers, NntpArticleHeaderName knownName)
        {
            for (int i = 0; i < headers.Count; i++)
            {
                if (headers[i].KnownName == knownName)
                {
                    return headers[i];
                }
            }

            throw new InvalidOperationException($"Header '{knownName}' was not found in parse output.");
        }

        /// <summary>
        /// Parses two <c>Date</c> lines and asserts the article is rejected with no canonical instant.
        /// </summary>
        /// <param name="firstDate">First Date header line, without CRLF.</param>
        /// <param name="secondDate">Second Date header line, without CRLF.</param>
        private static void AssertDuplicateDateRejected(string firstDate, string secondDate)
        {
            NntpArticleParser parser = new(LocalFqdn);
            byte[] article = BuildArticle(
                headers:
                [
                    firstDate,
                    secondDate,
                    "Message-ID: <dup-date@example.test>",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                ],
                body: "body\r\n");

            NntpArticleParseResult result = parser.Parse(article);

            Assert.False(result.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.DuplicateDate, result.FailureCode);
            Assert.Equal(default, result.CanonicalUtc);
            Assert.Equal(NntpArticleHeaderName.Unknown, result.SelectedDateHeaderName);
            Assert.True(result.OriginalDateValue.IsEmpty);
        }

        /// <summary>
        /// Builds an article with explicit header lines and optional byte body.
        /// </summary>
        /// <param name="headers">Header lines without CRLF.</param>
        /// <param name="body">Body text when <paramref name="bodyBytes"/> is null.</param>
        /// <param name="bodyBytes">Raw body bytes.</param>
        /// <returns>Article bytes with CRLF separator.</returns>
        private static byte[] BuildArticle(IEnumerable<string> headers, string? body = null, byte[]? bodyBytes = null)
        {
            byte[] prefix = BuildArticlePrefixBytes(headers);

            bodyBytes ??= Encoding.ASCII.GetBytes(body ?? string.Empty);

            byte[] article = new byte[prefix.Length + bodyBytes.Length];
            Buffer.BlockCopy(prefix, 0, article, 0, prefix.Length);
            Buffer.BlockCopy(bodyBytes, 0, article, prefix.Length, bodyBytes.Length);
            return article;
        }

        /// <summary>
        /// Joins header lines with CRLF and appends the blank line that separates headers from the body.
        /// </summary>
        /// <param name="headers">Header lines without their terminating CRLF.</param>
        /// <returns>ASCII header-section bytes, including the header/body separator.</returns>
        private static byte[] BuildArticlePrefixBytes(IEnumerable<string> headers)
        {
            StringBuilder sb = new();
            foreach (string header in headers)
            {
                _ = sb.Append(header).Append("\r\n");
            }

            _ = sb.Append("\r\n");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        /// <summary>
        /// Builds the fixed Date, Message-ID, Newsgroups, and From prefix used by size-limit articles.
        /// </summary>
        /// <param name="messageId">Message-ID value, including angle brackets, placed after <c>Message-ID: </c>.</param>
        /// <returns>ASCII header bytes including the header/body separator.</returns>
        private static byte[] BuildArticleHeaderBytes(string messageId)
        {
            return BuildArticlePrefixBytes(
            [
                "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                $"Message-ID: {messageId}",
                "Newsgroups: alt.test",
                "From: user@example.test",
            ]);
        }

        /// <summary>
        /// Allocates a buffer of <paramref name="length"/> bytes filled with <paramref name="value"/>.
        /// </summary>
        /// <param name="length">Buffer length.</param>
        /// <param name="value">Byte written into every position.</param>
        /// <returns>The filled buffer.</returns>
        private static byte[] CreateFilledBytes(int length, byte value)
        {
            byte[] bytes = new byte[length];
            Array.Fill(bytes, value);
            return bytes;
        }

        /// <summary>
        /// Fills <paramref name="length"/> bytes with <paramref name="value"/>, inserting CRLF so no line exceeds the article line limit.
        /// A final leftover byte is separated with LF when two bytes do not remain.
        /// </summary>
        /// <param name="length">Exact body length, including inserted terminators.</param>
        /// <param name="value">Payload byte used between terminators.</param>
        /// <returns>A body whose physical lines stay within <see cref="ArticleResourceLimits.MaxArticleLineCharacters"/>.</returns>
        private static byte[] CreateLineBoundedBodyBytes(int length, byte value)
        {
            byte[] bytes = new byte[length];
            int written = 0;

            while (written < length)
            {
                int remaining = length - written;
                int lineBytes = Math.Min(ArticleResourceLimits.MaxArticleLineCharacters, remaining);
                for (int i = 0; i < lineBytes; i++)
                {
                    bytes[written + i] = value;
                }

                written += lineBytes;
                if (written >= length)
                {
                    break;
                }

                if (length - written >= 2)
                {
                    bytes[written++] = (byte)'\r';
                    bytes[written++] = (byte)'\n';
                }
                else
                {
                    bytes[written++] = (byte)'\n';
                }
            }

            return bytes;
        }

        /// <summary>
        /// Builds an otherwise valid article whose Subject line has the requested physical length.
        /// </summary>
        /// <param name="messageId">Message-ID value, including angle brackets.</param>
        /// <param name="physicalLineLength">Subject line length before its CRLF. Must be at least the length of <c>Subject: </c>.</param>
        /// <returns>Article bytes whose Subject line is exactly <paramref name="physicalLineLength"/> bytes.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="physicalLineLength"/> is shorter than <c>Subject: </c>.</exception>
        private static byte[] BuildHeaderLineLengthArticle(string messageId, int physicalLineLength)
        {
            string subjectPrefix = "Subject: ";
            if (physicalLineLength < subjectPrefix.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(physicalLineLength));
            }

            int valueLength = physicalLineLength - subjectPrefix.Length;
            return BuildArticle(
                headers:
                [
                    "Date: Fri, 23 Aug 2024 07:30:10 +0000",
                    $"Message-ID: {messageId}",
                    "Newsgroups: alt.test",
                    "From: user@example.test",
                    subjectPrefix + new string('x', valueLength),
                ],
                body: "body\r\n");
        }

        /// <summary>
        /// Builds article with a raw path value that may include control bytes.
        /// </summary>
        /// <param name="headers">All headers except Path.</param>
        /// <param name="rawPath">Raw Path value literal.</param>
        /// <param name="body">Body text.</param>
        /// <returns>Article bytes.</returns>
        private static byte[] BuildArticleRaw(IEnumerable<string> headers, string rawPath, string body)
        {
            StringBuilder sb = new();
            foreach (string header in headers)
            {
                _ = sb.Append(header).Append("\r\n");
            }

            _ = sb.Append("Path: ").Append(rawPath).Append("\r\n\r\n").Append(body);
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        /// <summary>
        /// Builds a deterministic valid single-part yEnc body used by parser acceptance tests.
        /// </summary>
        /// <param name="payloadLength">Decoded payload length.</param>
        /// <param name="name">Payload name metadata.</param>
        /// <returns>Valid yEnc body bytes.</returns>
        private static byte[] BuildSyntheticSinglePartYEncBody(int payloadLength, string name)
        {
            byte[] payload = new byte[payloadLength];
            Random random = new(17);
            random.NextBytes(payload);

            byte[] encodedPayload = EncodeYEncPayload(payload);
            uint crc = ComputeCrc32(payload);

            byte[] prefix = Encoding.ASCII.GetBytes($"=ybegin line=128 size={payload.Length} name={name}\r\n");
            byte[] suffix = Encoding.ASCII.GetBytes($"\r\n=yend size={payload.Length} crc32={crc:x8}\r\n");

            byte[] body = new byte[prefix.Length + encodedPayload.Length + suffix.Length];
            Buffer.BlockCopy(prefix, 0, body, 0, prefix.Length);
            Buffer.BlockCopy(encodedPayload, 0, body, prefix.Length, encodedPayload.Length);
            Buffer.BlockCopy(suffix, 0, body, prefix.Length + encodedPayload.Length, suffix.Length);
            return body;
        }

        /// <summary>
        /// Encodes decoded payload bytes to yEnc payload bytes using the validator test escape rules.
        /// </summary>
        /// <param name="decoded">Decoded payload bytes.</param>
        /// <returns>yEnc-encoded payload bytes with CRLF line wrapping.</returns>
        private static byte[] EncodeYEncPayload(byte[] decoded)
        {
            List<byte> output = new(decoded.Length + (decoded.Length / 32));
            int lineCount = 0;

            for (int i = 0; i < decoded.Length; i++)
            {
                byte encoded = unchecked((byte)(decoded[i] + 42));
                bool mustEscape = encoded is 0 or 9 or 10 or 13 or 32 or 46 or 61;

                if (mustEscape)
                {
                    output.Add((byte)'=');
                    output.Add(unchecked((byte)(encoded + 64)));
                    lineCount += 2;
                }
                else
                {
                    output.Add(encoded);
                    lineCount++;
                }

                if (lineCount >= 128)
                {
                    output.Add((byte)'\r');
                    output.Add((byte)'\n');
                    lineCount = 0;
                }
            }

            if (output.Count == 0 || output[^1] != (byte)'\n')
            {
                output.Add((byte)'\r');
                output.Add((byte)'\n');
            }

            return [.. output];
        }

        /// <summary>
        /// Computes CRC32 for decoded payload bytes using the yEnc polynomial.
        /// </summary>
        /// <param name="data">Decoded payload bytes.</param>
        /// <returns>CRC32 checksum.</returns>
        private static uint ComputeCrc32(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++)
            {
                crc ^= data[i];
                for (int bit = 0; bit < 8; bit++)
                {
                    uint mask = (uint)-(int)(crc & 1);
                    crc = (crc >> 1) ^ (0xEDB88320u & mask);
                }
            }

            return ~crc;
        }

        /// <summary>
        /// Returns fixture bytes unchanged unless <paramref name="fixtureName"/> is dot-stuffed wire form,
        /// in which case one line-start dot is removed between the payload and <c>=yend</c>.
        /// </summary>
        /// <param name="fixtureName">Fixture file name used to decide whether unstuffing applies.</param>
        /// <param name="fixtureBytes">Raw fixture bytes.</param>
        /// <returns>Bytes suitable for the parser, or <paramref name="fixtureBytes"/> when unstuffing does not apply or the markers are missing.</returns>
        private static byte[] NormalizeFixtureBodyForParserContract(string fixtureName, byte[] fixtureBytes)
        {
            if (!WireFormDotStuffedFixtureNames.Contains(fixtureName))
            {
                return fixtureBytes;
            }

            int payloadStart = FindPayloadStartOffset(fixtureBytes);
            int yEndLineStart = FindYEndLineStartOffset(fixtureBytes, payloadStart);
            if (payloadStart < 0 || yEndLineStart < 0 || yEndLineStart < payloadStart)
            {
                return fixtureBytes;
            }

            byte[] normalizedPayload = UnstuffNntpWireDotPrefixedLines(fixtureBytes.AsSpan(payloadStart, yEndLineStart - payloadStart));
            byte[] normalized = new byte[payloadStart + normalizedPayload.Length + (fixtureBytes.Length - yEndLineStart)];
            Buffer.BlockCopy(fixtureBytes, 0, normalized, 0, payloadStart);
            Buffer.BlockCopy(normalizedPayload, 0, normalized, payloadStart, normalizedPayload.Length);
            Buffer.BlockCopy(fixtureBytes, yEndLineStart, normalized, payloadStart + normalizedPayload.Length, fixtureBytes.Length - yEndLineStart);
            return normalized;
        }

        /// <summary>
        /// Locates the first payload byte after <c>=ypart</c> when that line exists, otherwise after <c>=ybegin</c>.
        /// </summary>
        /// <param name="fixtureBytes">Fixture bytes searched as ASCII text.</param>
        /// <returns>The index after the control line's LF, or -1 when the anchor or its LF is missing.</returns>
        private static int FindPayloadStartOffset(byte[] fixtureBytes)
        {
            string text = Encoding.ASCII.GetString(fixtureBytes);
            int partIndex = text.IndexOf("=ypart ", StringComparison.Ordinal);
            int beginIndex = text.IndexOf("=ybegin ", StringComparison.Ordinal);
            int anchor = partIndex >= 0 ? partIndex : beginIndex;
            if (anchor < 0)
            {
                return -1;
            }

            int lineFeed = text.IndexOf('\n', anchor);
            return lineFeed < 0 ? -1 : lineFeed + 1;
        }

        /// <summary>
        /// Locates the <c>=yend</c> line that ends the payload region.
        /// </summary>
        /// <param name="fixtureBytes">Fixture bytes searched as ASCII text.</param>
        /// <param name="searchStart">Index at which the payload search begins.</param>
        /// <returns>The index of <c>=yend</c> after a CRLF or LF, or -1 when neither form is found.</returns>
        private static int FindYEndLineStartOffset(byte[] fixtureBytes, int searchStart)
        {
            string text = Encoding.ASCII.GetString(fixtureBytes);
            int crlfCandidate = text.IndexOf("\r\n=yend ", searchStart, StringComparison.Ordinal);
            if (crlfCandidate >= 0)
            {
                return crlfCandidate + 2;
            }

            int lfCandidate = text.IndexOf("\n=yend ", searchStart, StringComparison.Ordinal);
            return lfCandidate >= 0 ? lfCandidate + 1 : -1;
        }

        /// <summary>
        /// Removes one leading dot from a line that begins with <c>..</c> and copies every other payload byte.
        /// </summary>
        /// <param name="payload">Wire-form payload between the yEnc control lines.</param>
        /// <returns>The payload after one transport unstuffing pass.</returns>
        private static byte[] UnstuffNntpWireDotPrefixedLines(ReadOnlySpan<byte> payload)
        {
            List<byte> output = new(payload.Length);
            bool atLineStart = true;

            for (int i = 0; i < payload.Length; i++)
            {
                byte current = payload[i];
                if (atLineStart && current == (byte)'.' && i + 1 < payload.Length && payload[i + 1] == (byte)'.')
                {
                    output.Add((byte)'.');
                    i++;
                    atLineStart = false;
                    continue;
                }

                output.Add(current);
                atLineStart = current is (byte)'\r' or (byte)'\n';
            }

            return [.. output];
        }

        /// <summary>
        /// Resolves the SABCTools yEnc fixture directory from the test output directory or by walking up to <c>VectorNNTPD.slnx</c>.
        /// </summary>
        /// <returns>The absolute fixture directory.</returns>
        /// <exception cref="DirectoryNotFoundException">Thrown when neither candidate directory exists.</exception>
        private static string ResolveFixtureRoot()
        {
            string outputCandidate = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SabctoolsYEnc");
            if (Directory.Exists(outputCandidate))
            {
                return outputCandidate;
            }

            string? current = AppContext.BaseDirectory;

            while (!string.IsNullOrWhiteSpace(current))
            {
                if (File.Exists(Path.Combine(current, "VectorNNTPD.slnx")))
                {
                    string root = Path.Combine(current, "tests", "VectorNNTPD.Common.Tests", "Fixtures", "SabctoolsYEnc");
                    if (Directory.Exists(root))
                    {
                        return root;
                    }

                    break;
                }

                current = Directory.GetParent(current)?.FullName;
            }

            throw new DirectoryNotFoundException("Unable to locate SABCTools fixture root for parser tests.");
        }
    }

}

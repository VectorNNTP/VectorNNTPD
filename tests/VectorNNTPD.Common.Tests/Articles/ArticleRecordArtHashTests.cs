using System.IO.Hashing;
using System.Text;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles.Processing;

namespace VectorNNTPD.Common.Tests.Articles
{
    /// <summary>
    /// ArtHash is one-shot XXH3-64 of final canonical ArtData.
    /// </summary>
    public sealed class ArticleRecordArtHashTests
    {
        /// <summary>Local hop inserted into canonical Path by the parser and materializer.</summary>
        private const string LocalFqdn = "backfiller01.usenet.ninja";

        /// <summary>XXH3-64 of ASCII <c>123456789</c> with default seed 0.</summary>
        public const ulong XxHash3KnownVector = 0x72DCB18B67A17DFFUL;

        /// <summary>
        /// <see cref="XxHash3.HashToUInt64(ReadOnlySpan{byte}, long)"/> of ASCII <c>123456789</c> equals <see cref="XxHash3KnownVector"/> and is stable across two calls.
        /// </summary>
        [Fact]
        public void XxHash3_KnownVector_IsDeterministic()
        {
            Assert.Equal(XxHash3KnownVector, XxHash3.HashToUInt64("123456789"u8));
            Assert.Equal(XxHash3.HashToUInt64("123456789"u8), XxHash3.HashToUInt64("123456789"u8));
        }

        /// <summary>
        /// An accepted record's <c>ArtHash</c> equals an independent XXH3-64 of its final <c>ArtData</c>.
        /// </summary>
        [Fact]
        public void TryCreate_ArtHash_MatchesIndependentXxHash3OfArtData()
        {
            var record = CreateAccepted(
                path: "peer.example",
                date: "Fri, 23 Aug 2024 07:30:10 +0000",
                messageId: "<independent@example.test>",
                body: "payload\r\n");
            Assert.Equal(XxHash3.HashToUInt64(record.ArtData.Span), record.ArtHash);
        }

        /// <summary>
        /// Two accepts of the same destuffed bytes produce equal <c>ArtData</c> and the same <c>ArtHash</c>.
        /// </summary>
        [Fact]
        public void TryCreate_ArtHash_IsStableForIdenticalCanonicalArtData()
        {
            var destuffed = BuildDestuffed("peer.example", "Fri, 23 Aug 2024 07:30:10 +0000", "<stable@example.test>", "body\r\n");
            var first = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), destuffed);
            var second = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), destuffed);
            Assert.True(first.IsAccepted);
            Assert.True(second.IsAccepted);
            Assert.True(first.Record.ArtData.Span.SequenceEqual(second.Record.ArtData.Span));
            Assert.Equal(first.Record.ArtHash, second.Record.ArtHash);
        }

        /// <summary>
        /// Flipping one byte of a copy of <c>ArtData</c> changes the XXH3-64 result relative to the stored <c>ArtHash</c>.
        /// </summary>
        [Fact]
        public void TryCreate_ArtHash_ChangesWhenCanonicalContentChanges()
        {
            var destuffed = BuildDestuffed("peer.example", "Fri, 23 Aug 2024 07:30:10 +0000", "<change@example.test>", "body\r\n");
            var created = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), destuffed);
            Assert.True(created.IsAccepted);
            var record = created.Record;
            var mutated = record.ArtData.ToArray();
            mutated[^2] ^= 0x01;
            Assert.NotEqual(record.ArtHash, XxHash3.HashToUInt64(mutated));
        }

        /// <summary>
        /// Changing only the body keeps <c>ArtId</c> equal and changes <c>ArtHash</c>.
        /// </summary>
        [Fact]
        public void TryCreate_ArtId_UnchangedWhenArticleBodyChanges()
        {
            var first = CreateAccepted("peer.example", "Fri, 23 Aug 2024 07:30:10 +0000", "<same-id@example.test>", "aaa\r\n");
            var second = CreateAccepted("peer.example", "Fri, 23 Aug 2024 07:30:10 +0000", "<same-id@example.test>", "bbb\r\n");
            Assert.Equal(first.ArtId, second.ArtId);
            Assert.NotEqual(first.ArtHash, second.ArtHash);
        }

        /// <summary>
        /// <c>ArtId</c> is the BLAKE3 of the original Message-ID value even when the stored Date and Path differ from the destuffed header text.
        /// </summary>
        [Fact]
        public void TryCreate_ArtId_UnchangedByDateAndPathCanonicalization()
        {
            var destuffed = BuildDestuffed("peer.example", "Fri, 23 Aug 2024 09:30:10 +0200", "<canon-id@example.test>", "body\r\n");
            var parse = new NntpArticleParser(LocalFqdn).Parse(destuffed);
            Assert.True(parse.IsAccepted);
            var created = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), destuffed);
            Assert.True(created.IsAccepted);
            Assert.Equal(ArticleId.FromMessageId("<canon-id@example.test>"u8), created.Record.ArtId);
            Assert.False(created.Record.Date.SequenceEqual("Fri, 23 Aug 2024 09:30:10 +0200"u8));
            Assert.False(created.Record.Path.SequenceEqual("peer.example"u8));
        }

        /// <summary>
        /// A <c>+0200</c> date is stored as <c>Fri, 23 Aug 2024 07:30:10 +0000</c>, and <c>ArtHash</c> matches XXH3-64 of that rewritten <c>ArtData</c>.
        /// </summary>
        [Fact]
        public void TryCreate_DateRewrite_ProducesExpectedArtDataAndArtHash()
        {
            const string date = "Fri, 23 Aug 2024 09:30:10 +0200";
            var destuffed = BuildDestuffed("peer.example", date, "<date-rewrite@example.test>", "body\r\n");
            var created = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), destuffed);
            Assert.True(created.IsAccepted);
            var record = created.Record;
            AssertRecordInvariants(record, destuffed, expectedLines: 1);
            Assert.False(record.Date.SequenceEqual(Encoding.ASCII.GetBytes(date)));
            Assert.True(record.Date.SequenceEqual("Fri, 23 Aug 2024 07:30:10 +0000"u8));
            Assert.Equal(XxHash3.HashToUInt64(record.ArtData.Span), record.ArtHash);
        }

        /// <summary>
        /// An existing Path is prefixed with <c>news.usenet.ninja!backfiller01.usenet.ninja!</c>, and <c>ArtHash</c> covers that rewritten article.
        /// </summary>
        [Fact]
        public void TryCreate_PathRewrite_ProducesExpectedArtDataAndArtHash()
        {
            var destuffed = BuildDestuffed("peer.example", "Fri, 23 Aug 2024 07:30:10 +0000", "<path-rewrite@example.test>", "body\r\n");
            var created = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), destuffed);
            Assert.True(created.IsAccepted);
            var record = created.Record;
            AssertRecordInvariants(record, destuffed, expectedLines: 1);
            Assert.True(record.Path.SequenceEqual("news.usenet.ninja!backfiller01.usenet.ninja!peer.example"u8));
            Assert.Equal(XxHash3.HashToUInt64(record.ArtData.Span), record.ArtHash);
        }

        /// <summary>
        /// An article with no Path gains a Path field that starts with <c>news.usenet.ninja!backfiller01.usenet.ninja</c>, and <c>ArtHash</c> covers the inserted article.
        /// </summary>
        [Fact]
        public void TryCreate_PathInsertion_ProducesExpectedArtDataAndArtHash()
        {
            var destuffed = BuildDestuffedWithoutPath("Fri, 23 Aug 2024 07:30:10 +0000", "<path-insert@example.test>", "body\r\n");
            var created = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), destuffed);
            Assert.True(created.IsAccepted);
            var record = created.Record;
            AssertRecordInvariants(record, destuffed, expectedLines: 1);
            Assert.True(record.Fields.Path.IsPresent);
            Assert.True(record.Path.StartsWith("news.usenet.ninja!backfiller01.usenet.ninja"u8));
            Assert.Equal(XxHash3.HashToUInt64(record.ArtData.Span), record.ArtHash);
        }

        /// <summary>
        /// CR, LF, CRLF, and mixed body terminators still produce <see cref="ArticleParseStatus.CanonicalV1"/> records whose <c>ArtHash</c> matches <c>ArtData</c> and whose <c>ArtSize</c> is <c>ArtData.Length</c>.
        /// </summary>
        /// <param name="name">Label embedded in the Message-ID so each terminator case has a distinct article.</param>
        /// <param name="body">Body text using the terminator pattern under test.</param>
        [Theory]
        [InlineData("crlf", "a\r\nb\r\n")]
        [InlineData("lf", "a\nb\n")]
        [InlineData("cr", "a\rb\r")]
        [InlineData("mixed", "a\r\nb\nc\r")]
        public void TryCreate_LineTerminators_ArtHashMatchesArtData(string name, string body)
        {
            _ = name;
            var destuffed = BuildDestuffed("peer.example", "Fri, 23 Aug 2024 07:30:10 +0000", $"<{name}@example.test>", body);
            var created = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), destuffed);
            Assert.True(created.IsAccepted, created.ParseFailure + "/" + created.MaterializeFailure);
            var record = created.Record;
            Assert.Equal(XxHash3.HashToUInt64(record.ArtData.Span), record.ArtHash);
            Assert.Equal(ArticleParseStatus.CanonicalV1, record.ParseStatus);
            Assert.Equal(record.ArtData.Length, record.ArtSize);
        }

        /// <summary>
        /// A one-line body under 512 bytes is accepted and its <c>ArtHash</c> matches XXH3-64 of <c>ArtData</c>.
        /// </summary>
        [Fact]
        public void TryCreate_SmallArticle_Works()
        {
            var destuffed = BuildDestuffed("p", "Fri, 23 Aug 2024 07:30:10 +0000", "<small@example.test>", "x\r\n");
            var created = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), destuffed);
            Assert.True(created.IsAccepted);
            var record = created.Record;
            AssertRecordInvariants(record, destuffed, expectedLines: 1);
            Assert.True(record.ArtSize < 512);
            Assert.Equal(XxHash3.HashToUInt64(record.ArtData.Span), record.ArtHash);
        }

        /// <summary>
        /// Empty input is rejected with <see cref="NntpArticleParseFailureCode.EmptyArticle"/>, <see cref="ArticleParseStatus.None"/>, and a zero <c>ArtHash</c>.
        /// </summary>
        [Fact]
        public void TryCreate_EmptyInput_DoesNotProduceRecord()
        {
            var created = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), ReadOnlyMemory<byte>.Empty);
            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleParseFailureCode.EmptyArticle, created.ParseFailure);
            Assert.Equal(ArticleParseStatus.None, created.Record.ParseStatus);
            Assert.Equal(0UL, created.Record.ArtHash);
        }

        /// <summary>
        /// After warmup, one more <see cref="ArticleRecordFactory.TryCreate(NntpArticleParser, ReadOnlyMemory{byte})"/> allocates at least one article-sized buffer and less than twice <c>ArtSize</c>.
        /// </summary>
        [Fact]
        public void TryCreate_DoesNotAllocateSecondArticleSizedBuffer()
        {
            var destuffed = BuildDestuffed("peer.example", "Fri, 23 Aug 2024 07:30:10 +0000", "<onebuf@example.test>", "body\r\n");
            var parser = new NntpArticleParser(LocalFqdn);
            _ = ArticleRecordFactory.TryCreate(parser, destuffed);
            var created = ArticleRecordFactory.TryCreate(parser, destuffed);
            Assert.True(created.IsAccepted);
            var artSize = created.Record.ArtSize;

            var before = GC.GetAllocatedBytesForCurrentThread();
            var again = ArticleRecordFactory.TryCreate(parser, destuffed);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(again.IsAccepted);
            Assert.Equal(XxHash3.HashToUInt64(again.Record.ArtData.Span), again.Record.ArtHash);
            Assert.True(allocated >= artSize);
            Assert.True(allocated < artSize * 2, $"allocated {allocated} for ArtSize {artSize}");
        }

        /// <summary>
        /// Factory <c>ArtData</c> matches the canonical materializer bytes, <c>ArtHash</c> is one XXH3-64 of those bytes,
        /// and size, line count, and article type match the parse and classifier results.
        /// </summary>
        [Fact]
        public void Factory_ArtData_AliasesMaterializerBuffer_AndArtHashIsOneShot()
        {
            var destuffed = BuildDestuffed("peer.example", "Fri, 23 Aug 2024 09:30:10 +0200", "<alias@example.test>", "z\r\n");
            var parser = new NntpArticleParser(LocalFqdn);
            var parse = parser.Parse(destuffed);
            Assert.True(parse.IsAccepted);
            var materialize = NntpArticleCanonicalMaterializer.Materialize(in parse, ArticlePathMode.Traverse);
            Assert.True(materialize.IsAccepted);
            Assert.NotNull(materialize.ArticleBytes);

            var created = ArticleRecordFactory.TryCreate(parser, destuffed);
            Assert.True(created.IsAccepted);
            var record = created.Record;
            Assert.True(record.ArtData.Span.SequenceEqual(materialize.ArticleBytes));
            Assert.Equal(XxHash3.HashToUInt64(materialize.ArticleBytes), record.ArtHash);
            Assert.Equal(materialize.ArticleBytes.Length, record.ArtSize);
            Assert.Equal(parse.BodyLineCount, record.ArtLines);
            Assert.Equal(ArticleTypeClassifier.Classify(parse.HeaderBytes.Span, parse.BodyBytes.Span), record.ArtType);
        }

        /// <summary>
        /// Dot-leading body lines are stuffed once. ArtHash is XXH3-64 of that stuffed buffer, ArtId stays the Message-ID digest, and a second materialization matches byte for byte.
        /// </summary>
        [Fact]
        public void TryCreate_StuffedBody_ArtHashCoversRetainedBytes_AndRepeats()
        {
            var body = ".\r\n..\r\nplain\r\n";
            var first = CreateAccepted("peer.example", "Fri, 23 Aug 2024 07:30:10 +0000", "<stuffed@example.test>", body);
            var second = CreateAccepted("peer.example", "Fri, 23 Aug 2024 07:30:10 +0000", "<stuffed@example.test>", body);
            Assert.Equal(first.ArtHash, second.ArtHash);
            Assert.True(first.ArtData.Span.SequenceEqual(second.ArtData.Span));
            Assert.Equal(XxHash3.HashToUInt64(first.ArtData.Span), first.ArtHash);
            Assert.Equal(ArticleId.FromMessageId("<stuffed@example.test>"u8), first.ArtId);

            var separator = first.ArtData.Span.IndexOf("\r\n\r\n"u8);
            Assert.True(separator > 0);
            Assert.Equal("..\r\n...\r\nplain\r\n"u8, first.ArtData.Span[(separator + 4)..]);
            Assert.Equal(3, first.ArtLines);
            Assert.False(first.ArtData.Span.EndsWith("\r\n.\r\n"u8));
        }

        /// <summary>
        /// A rejected oversize article does not return a record with ArtData.
        /// </summary>
        [Fact]
        public void TryCreate_WhenMaterializationRejects_ReturnsNoRecord()
        {
            var created = ArticleRecordFactory.TryCreate(
                new NntpArticleParser(LocalFqdn),
                new byte[ArticleResourceLimits.MaxArticleBytes + 1]);
            Assert.False(created.IsAccepted);
            Assert.Equal(0, created.Record.ArtSize);
            Assert.True(created.Record.ArtData.IsEmpty);
        }

        /// <summary>
        /// Builds a destuffed article and returns the accepted <see cref="ArticleRecord"/>.
        /// </summary>
        /// <param name="path">Path header value before canonical insertion.</param>
        /// <param name="date">Date header value before canonical UTC rewrite.</param>
        /// <param name="messageId">Message-ID header value, including angle brackets.</param>
        /// <param name="body">Article body, including its line terminators.</param>
        /// <returns>The accepted canonical record.</returns>
        private static ArticleRecord CreateAccepted(string path, string date, string messageId, string body)
        {
            var destuffed = BuildDestuffed(path, date, messageId, body);
            var created = ArticleRecordFactory.TryCreate(new NntpArticleParser(LocalFqdn), destuffed);
            Assert.True(created.IsAccepted);
            return created.Record;
        }

        /// <summary>
        /// Asserts canonical status, size, materializer byte equality, line count, ArtId, type, field presence, Message-ID slice, and ArtHash.
        /// </summary>
        /// <param name="record">Record produced by the factory.</param>
        /// <param name="destuffed">Original destuffed article bytes used to reparse and rematerialize.</param>
        /// <param name="expectedLines">Expected body line count.</param>
        private static void AssertRecordInvariants(ArticleRecord record, ReadOnlyMemory<byte> destuffed, int expectedLines)
        {
            var parser = new NntpArticleParser(LocalFqdn);
            var parse = parser.Parse(destuffed);
            Assert.True(parse.IsAccepted);
            var materialize = NntpArticleCanonicalMaterializer.Materialize(in parse, ArticlePathMode.Traverse);
            Assert.True(materialize.IsAccepted);
            Assert.NotNull(materialize.ArticleBytes);

            Assert.Equal(ArticleParseStatus.CanonicalV1, record.ParseStatus);
            Assert.Equal(record.ArtData.Length, record.ArtSize);
            Assert.Equal(materialize.ArticleBytes.Length, record.ArtSize);
            Assert.True(record.ArtData.Span.SequenceEqual(materialize.ArticleBytes));
            Assert.Equal(parse.BodyLineCount, record.ArtLines);
            Assert.Equal(expectedLines, record.ArtLines);
            Assert.Equal(ArticleId.FromMessageId(record.MessageId), record.ArtId);
            Assert.Equal(ArticleTypeClassifier.Classify(parse.HeaderBytes.Span, parse.BodyBytes.Span), record.ArtType);
            Assert.True(record.Fields.MessageId.IsPresent);
            Assert.True(record.Fields.Newsgroups.IsPresent);
            Assert.True(record.Fields.Date.IsPresent);
            Assert.True(record.Fields.MessageId.Offset + record.Fields.MessageId.Length <= record.ArtSize);
            Assert.True(record.ArtData.Span.Slice(record.Fields.MessageId.Offset, record.Fields.MessageId.Length)
                .SequenceEqual(record.MessageId));
            Assert.Equal(XxHash3.HashToUInt64(record.ArtData.Span), record.ArtHash);
        }

        /// <summary>
        /// Builds an ASCII article with Path, Date, Message-ID, Newsgroups, From, Subject, and <paramref name="body"/>.
        /// </summary>
        /// <param name="path">Path header value.</param>
        /// <param name="date">Date header value.</param>
        /// <param name="messageId">Message-ID header value.</param>
        /// <param name="body">Body text appended after the blank line.</param>
        /// <returns>ASCII bytes of the destuffed article.</returns>
        private static byte[] BuildDestuffed(string path, string date, string messageId, string body)
        {
            return Encoding.ASCII.GetBytes(
                "Path: " + path + "\r\n" +
                "Date: " + date + "\r\n" +
                "Message-ID: " + messageId + "\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "Subject: hash\r\n" +
                "\r\n" +
                body);
        }

        /// <summary>
        /// Builds an ASCII article that omits Path so the factory must insert one.
        /// </summary>
        /// <param name="date">Date header value.</param>
        /// <param name="messageId">Message-ID header value.</param>
        /// <param name="body">Body text appended after the blank line.</param>
        /// <returns>ASCII bytes of the destuffed article.</returns>
        private static byte[] BuildDestuffedWithoutPath(string date, string messageId, string body)
        {
            return Encoding.ASCII.GetBytes(
                "Date: " + date + "\r\n" +
                "Message-ID: " + messageId + "\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "Subject: hash\r\n" +
                "\r\n" +
                body);
        }
    }
}

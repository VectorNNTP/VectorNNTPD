using System.IO.Hashing;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles.Processing;
using VectorNNTPD.Common.Transport.ArticleTransfer;

namespace VectorNNTPD.Common.Tests.Transport.ArticleTransfer
{
    /// <summary>
    /// Acceptance and rejection contracts for <see cref="ArticleRecordFactory.TryCreateFromCanonicalTransfer"/>.
    /// </summary>
    public sealed class ArticleCanonicalTransferFactoryTests
    {
        /// <summary>
        /// A consistent canonical transfer is accepted as <see cref="ArticleParseStatus.CanonicalV1"/>
        /// and preserves identity, hash, type, line count, canonical time, field table, and the caller's article bytes.
        /// </summary>
        [Fact]
        public void TryCreateFromCanonicalTransfer_ValidRoundTrip_MatchesFactoryRecord()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var meta = ArticleCanonicalTransferMeta.FromRecord(record, selected);
            var expected = record.ArtId;

            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
            var transferred = created.Record;

            Assert.Equal(ArticleParseStatus.CanonicalV1, transferred.ParseStatus);
            Assert.Equal(record.ArtId, transferred.ArtId);
            Assert.Equal(record.ArtHash, transferred.ArtHash);
            Assert.Equal(record.ArtType, transferred.ArtType);
            Assert.Equal(record.ArtLines, transferred.ArtLines);
            Assert.Equal(record.CanonicalUtc, transferred.CanonicalUtc);
            Assert.Equal(record.Fields, transferred.Fields);
            Assert.Equal(record.Framing.IsFrameable, transferred.Framing.IsFrameable);
            Assert.Equal(record.Framing.BodyStart, transferred.Framing.BodyStart);
            var live = CanonicalArticleFraming.Analyze(transferred.ArtData.Span);
            Assert.Equal(transferred.Framing.IsFrameable, live.IsFrameable);
            Assert.Equal(transferred.Framing.BodyStart, live.BodyStart);
            Assert.Equal(transferred.ArtHash, System.IO.Hashing.XxHash3.HashToUInt64(transferred.ArtData.Span));
            Assert.True(transferred.ArtData.Span.SequenceEqual(artData));
            Assert.True(IsSameBuffer(transferred.ArtData, artData));
        }

        /// <summary>
        /// The accepted record wraps the caller's article buffer, so a later mutation of that buffer is visible through <c>ArtData</c>.
        /// </summary>
        [Fact]
        public void TryCreateFromCanonicalTransfer_DoesNotCopyArtData()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var meta = ArticleCanonicalTransferMeta.FromRecord(record, selected);
            var expected = record.ArtId;
            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            Assert.True(created.IsAccepted);
            Assert.True(IsSameBuffer(created.Record.ArtData, artData));
            artData[0] ^= 0xFF;
            Assert.Equal(artData[0], created.Record.ArtData.Span[0]);
        }

        /// <summary>
        /// Meta whose article size is one byte above the buffer is rejected as <see cref="NntpArticleCanonicalFailureCode.TransferArtSizeMismatch"/>.
        /// </summary>
        [Fact]
        public void TryCreateFromCanonicalTransfer_ArtSizeMismatch_Rejected()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var meta = new ArticleCanonicalTransferMeta(
                record.ArtHash,
                record.ArtLines,
                record.ArtSize + 1,
                selected,
                record.Fields);
            var expected = record.ArtId;
            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferArtSizeMismatch, created.MaterializeFailure);
        }

        /// <summary>
        /// Meta whose hash differs from the article bytes is rejected as <see cref="NntpArticleCanonicalFailureCode.TransferArtHashMismatch"/>
        /// and leaves <see cref="ArticleParseStatus.None"/>.
        /// </summary>
        [Fact]
        public void TryCreateFromCanonicalTransfer_ArtHashMismatch_Rejected()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var meta = new ArticleCanonicalTransferMeta(
                record.ArtHash ^ 1UL,
                record.ArtLines,
                record.ArtSize,
                selected,
                record.Fields);
            var expected = record.ArtId;
            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferArtHashMismatch, created.MaterializeFailure);
            Assert.Equal(ArticleParseStatus.None, created.Record.ParseStatus);
        }

        /// <summary>
        /// Meta that matches the article but an expected id for a different Message-ID is rejected as <see cref="NntpArticleCanonicalFailureCode.TransferArticleIdMismatch"/>.
        /// </summary>
        [Fact]
        public void TryCreateFromCanonicalTransfer_ArticleIdMismatch_Rejected()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var meta = ArticleCanonicalTransferMeta.FromRecord(record, selected);
            var wrong = ArticleId.FromMessageId("<other@example.test>"u8);
            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in wrong);
            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferArticleIdMismatch, created.MaterializeFailure);
        }

        /// <summary>
        /// A Message-ID range that extends past the article buffer is rejected as <see cref="NntpArticleCanonicalFailureCode.TransferInvalidFieldRange"/>.
        /// </summary>
        [Fact]
        public void TryCreateFromCanonicalTransfer_FieldRangeOutsideBuffer_Rejected()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var badFields = new ArticleFieldTable(
                new ArticleByteRange(artData.Length - 1, 5),
                record.Fields.Newsgroups,
                record.Fields.Subject,
                record.Fields.From,
                record.Fields.Date,
                record.Fields.References,
                record.Fields.Path);
            var meta = new ArticleCanonicalTransferMeta(
                record.ArtHash,
                record.ArtLines,
                record.ArtSize,
                selected,
                badFields);
            var expected = record.ArtId;
            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferInvalidFieldRange, created.MaterializeFailure);
        }

        /// <summary>
        /// A Subject range that stays inside the buffer but no longer matches the bytes is rejected as <see cref="NntpArticleCanonicalFailureCode.TransferFieldTableMismatch"/>
        /// even when the meta hash is recomputed over the article.
        /// </summary>
        [Fact]
        public void TryCreateFromCanonicalTransfer_LocateMismatch_Rejected()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            // Shift Subject range by one while keeping bounds valid.
            var subject = record.Fields.Subject;
            Assert.True(subject.IsPresent && subject.Length > 1);
            var shifted = new ArticleByteRange(subject.Offset + 1, subject.Length - 1);
            var badFields = new ArticleFieldTable(
                record.Fields.MessageId,
                record.Fields.Newsgroups,
                shifted,
                record.Fields.From,
                record.Fields.Date,
                record.Fields.References,
                record.Fields.Path);
            var meta = new ArticleCanonicalTransferMeta(
                XxHash3.HashToUInt64(artData),
                record.ArtLines,
                record.ArtSize,
                selected,
                badFields);
            var expected = record.ArtId;
            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferFieldTableMismatch, created.MaterializeFailure);
        }

        /// <summary>
        /// <see cref="NntpArticleHeaderName.Unknown"/> as the selected date header is rejected as <see cref="NntpArticleCanonicalFailureCode.TransferInvalidSelectedDateHeader"/>.
        /// </summary>
        [Fact]
        public void TryCreateFromCanonicalTransfer_InvalidSelectedDateHeader_Rejected()
        {
            var (record, _, artData) = VatpTestArticles.CreateCanonical();
            var meta = new ArticleCanonicalTransferMeta(
                record.ArtHash,
                record.ArtLines,
                record.ArtSize,
                NntpArticleHeaderName.Unknown,
                record.Fields);
            var expected = record.ArtId;
            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferInvalidSelectedDateHeader, created.MaterializeFailure);
        }

        /// <summary>
        /// A negative line count is rejected as <see cref="NntpArticleCanonicalFailureCode.TransferInvalidArtLines"/>.
        /// </summary>
        [Fact]
        public void TryCreateFromCanonicalTransfer_InvalidArtLines_Rejected()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var meta = new ArticleCanonicalTransferMeta(
                record.ArtHash,
                -1,
                record.ArtSize,
                selected,
                record.Fields);
            var expected = record.ArtId;
            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferInvalidArtLines, created.MaterializeFailure);
        }

        /// <summary>
        /// A Message-ID with a tagged local part, an IPv4 domain literal, or an IPv6 domain literal is accepted and keeps the expected article id.
        /// </summary>
        /// <param name="messageId">Message-ID text, including angle brackets, used to build the article and the expected id.</param>
        [Theory]
        [InlineData("<part.one+tag@news-server.example.net>")]
        [InlineData("<foo@[1.2.3.4]>")]
        [InlineData("<foo@[IPv6:2001:db8::1]>")]
        public void TryCreateFromCanonicalTransfer_ValidMessageId_Accepted(string messageId)
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical(messageId);
            var meta = ArticleCanonicalTransferMeta.FromRecord(record, selected);
            var expected = record.ArtId;

            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);

            Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
            Assert.Equal(expected, created.Record.ArtId);
        }

        /// <summary>
        /// A structurally invalid Message-ID is rejected as <see cref="NntpArticleCanonicalFailureCode.TransferMissingMessageId"/>
        /// when the expected article id is derived from those same bytes.
        /// </summary>
        /// <param name="messageId">Malformed Message-ID text, including missing separators, illegal dots, spaces, or broken domain literals.</param>
        [Theory]
        [InlineData("<fooexample.test>")]
        [InlineData("<double..dot@example.test>")]
        [InlineData("<.leading@example.test>")]
        [InlineData("<trailing.@example.test>")]
        [InlineData("<foo@.example.test>")]
        [InlineData("<foo@example.>")]
        [InlineData("<foo bar@example.test>")]
        [InlineData("<foo@[1.2.3.4>")]
        [InlineData("<foo@[[1.2.3.4]>")]
        [InlineData("<foo@bar>baz>")]
        public void TryCreateFromCanonicalTransfer_MalformedMessageId_RejectedWhenArticleIdMatches(string messageId)
        {
            var created = CreateConsistentTransfer(System.Text.Encoding.ASCII.GetBytes(messageId));

            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferMissingMessageId, created.MaterializeFailure);
        }

        /// <summary>
        /// A 251-byte Message-ID is rejected as <see cref="NntpArticleCanonicalFailureCode.TransferMissingMessageId"/>
        /// when the expected article id is derived from those same bytes.
        /// </summary>
        [Fact]
        public void TryCreateFromCanonicalTransfer_MessageIdLongerThan250_RejectedWhenArticleIdMatches()
        {
            var messageId = new byte[251];
            messageId[0] = (byte)'<';
            messageId.AsSpan(1, 247).Fill((byte)'a');
            messageId[248] = (byte)'@';
            messageId[249] = (byte)'b';
            messageId[250] = (byte)'>';

            var created = CreateConsistentTransfer(messageId);

            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferMissingMessageId, created.MaterializeFailure);
        }

        /// <summary>
        /// Rejecting a Message-ID with trailing junk allocates no bytes on the calling thread after the failure path has been warmed up.
        /// </summary>
        [Fact]
        public void TryCreateFromCanonicalTransfer_InvalidMessageId_DoesNotAllocate()
        {
            var artData = BuildArticle("<foo@bar>baz>"u8);
            var fields = ArticleFieldTable.Locate(artData, NntpArticleHeaderName.Date);
            var messageId = fields.MessageId.Slice(artData).ToArray();
            var expected = ArticleId.FromMessageId(messageId);
            var meta = new ArticleCanonicalTransferMeta(
                XxHash3.HashToUInt64(artData),
                1,
                artData.Length,
                NntpArticleHeaderName.Date,
                fields);

            var warmup = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferMissingMessageId, warmup.MaterializeFailure);

            var before = GC.GetAllocatedBytesForCurrentThread();
            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(NntpArticleCanonicalFailureCode.TransferMissingMessageId, created.MaterializeFailure);
            Assert.Equal(0, allocated);
        }

        /// <summary>
        /// Flipping the last article byte after meta is built from the original hash is rejected as <see cref="NntpArticleCanonicalFailureCode.TransferArtHashMismatch"/>.
        /// </summary>
        [Fact]
        public void TryCreateFromCanonicalTransfer_MutatedArtData_HashFails()
        {
            var (record, selected, artData) = VatpTestArticles.CreateCanonical();
            var meta = ArticleCanonicalTransferMeta.FromRecord(record, selected);
            artData[^1] ^= 0x01;
            var expected = record.ArtId;
            var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
            Assert.False(created.IsAccepted);
            Assert.Equal(NntpArticleCanonicalFailureCode.TransferArtHashMismatch, created.MaterializeFailure);
        }

        /// <summary>
        /// Builds an article whose Message-ID bytes, hash, size, line count, date header, and expected <see cref="ArticleId"/> agree, then materializes it.
        /// </summary>
        /// <param name="messageId">Message-ID bytes placed in the article and used to derive the expected article id.</param>
        /// <returns>The factory result for that consistent transfer.</returns>
        private static ArticleRecordCreateResult CreateConsistentTransfer(ReadOnlySpan<byte> messageId)
        {
            var artData = BuildArticle(messageId);
            var fields = ArticleFieldTable.Locate(artData, NntpArticleHeaderName.Date);
            var value = fields.MessageId.Slice(artData);
            var expected = ArticleId.FromMessageId(value);
            var meta = new ArticleCanonicalTransferMeta(
                XxHash3.HashToUInt64(artData),
                1,
                artData.Length,
                NntpArticleHeaderName.Date,
                fields);
            return ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, in meta, in expected);
        }

        /// <summary>
        /// Builds one ASCII article with fixed Path, Date, Newsgroups, From, and Subject headers around <paramref name="messageId"/> and a single body line.
        /// </summary>
        /// <param name="messageId">Raw Message-ID bytes inserted after the Message-ID header label.</param>
        /// <returns>The concatenated header and body bytes.</returns>
        private static byte[] BuildArticle(ReadOnlySpan<byte> messageId)
        {
            var prefix = "Path: peer.example\r\nDate: Fri, 23 Aug 2024 07:30:10 +0000\r\nMessage-ID: "u8;
            var suffix = "\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\nSubject: vatp\r\n\r\nline1\r\n"u8;
            var artData = new byte[prefix.Length + messageId.Length + suffix.Length];
            prefix.CopyTo(artData);
            messageId.CopyTo(artData.AsSpan(prefix.Length));
            suffix.CopyTo(artData.AsSpan(prefix.Length + messageId.Length));
            return artData;
        }

        /// <summary>
        /// Reports whether <paramref name="memory"/> is the whole <paramref name="array"/> at offset zero.
        /// </summary>
        /// <param name="memory">Memory claimed to wrap the article buffer.</param>
        /// <param name="array">Array passed into canonical transfer creation.</param>
        /// <returns><see langword="true"/> when the segment array, offset, and count match <paramref name="array"/> exactly.</returns>
        private static bool IsSameBuffer(ReadOnlyMemory<byte> memory, byte[] array) =>
            System.Runtime.InteropServices.MemoryMarshal.TryGetArray(memory, out var segment)
            && ReferenceEquals(segment.Array, array)
            && segment.Offset == 0
            && segment.Count == array.Length;
    }
}

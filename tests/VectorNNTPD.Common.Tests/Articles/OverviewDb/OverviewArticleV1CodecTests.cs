using System.Text;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.OverviewDb;
using VectorNNTPD.Common.Articles.Parsing;

namespace VectorNNTPD.Common.Tests.Articles.OverviewDb
{
    /// <summary>OverviewDB protobuf payload contracts produced from CanonicalV1 ArticleRecord.</summary>
    public sealed class OverviewArticleV1CodecTests
    {
        /// <summary>Local hop used when parsing articles before overview encoding.</summary>
        private const string LocalFqdn = "nntpd01.usenet.ninja";

        /// <summary>
        /// The payload schema is <see cref="OverviewArticleV1.CurrentSchemaVersion"/>, <c>ArticleId</c> is the 32-byte ArtId,
        /// and <c>MessageId</c> is the clear-text id including angle brackets. Those two fields are not the same bytes.
        /// </summary>
        [Fact]
        public void Encode_ContainsArticleIdAndClearTextMessageId_AsDistinctFields()
        {
            var record = CreateRecord(
                Destuffed(
                    "<id@example.test>",
                    body: UniqueBody,
                    newsgroups: "alt.test,rec.test",
                    subject: "overview-subject",
                    from: "poster@example.test",
                    references: "<prev@example.test>"));

            var payload = OverviewArticleV1Codec.Encode(record);
            var decoded = OverviewArticleV1Codec.Decode(payload);

            Span<byte> artId = stackalloc byte[ArticleId.Length];
            record.ArtId.CopyTo(artId);
            Assert.Equal(OverviewArticleV1.CurrentSchemaVersion, decoded.SchemaVersion);
            Assert.Equal(artId.ToArray(), decoded.ArticleId);
            AssertAsciiEqual("<id@example.test>", decoded.MessageId);
            Assert.NotEqual(decoded.MessageId, decoded.ArticleId);
            Assert.Equal(32, decoded.ArticleId.Length);
            Assert.Equal((byte)'<', decoded.MessageId[0]);
            Assert.Equal((byte)'>', decoded.MessageId[^1]);
        }

        /// <summary>
        /// Subject, From, canonical Date, References, byte count, line count, and every newsgroup token are present, with surrounding spaces removed from group names.
        /// </summary>
        [Fact]
        public void Encode_ContainsCanonicalOverviewFieldsAndAllNewsgroups()
        {
            var record = CreateRecord(
                Destuffed(
                    "<multi@example.test>",
                    body: UniqueBody,
                    newsgroups: "alt.test, rec.test ,comp.test",
                    subject: "overview-subject",
                    from: "poster@example.test",
                    references: "<prev@example.test>"));

            var payload = OverviewArticleV1Codec.Encode(record);
            var decoded = OverviewArticleV1Codec.Decode(payload);

            AssertAsciiEqual("overview-subject", decoded.Subject);
            AssertAsciiEqual("poster@example.test", decoded.From);
            Assert.True(record.Date.SequenceEqual(decoded.Date));
            AssertAsciiEqual("<prev@example.test>", decoded.References);
            Assert.Equal((uint)record.ArtSize, decoded.Bytes);
            Assert.Equal((uint)record.ArtLines, decoded.Lines);
            AssertAsciiGroups(["alt.test", "rec.test", "comp.test"], decoded.Newsgroups);
            Assert.Equal(record.ArtSize, (int)decoded.Bytes);
            Assert.Equal(record.ArtLines, (int)decoded.Lines);
        }

        /// <summary>
        /// The payload does not contain the unique body token or the full article, the record is 341 bytes, and the payload is 140 bytes.
        /// </summary>
        [Fact]
        public void Encode_DoesNotContainArticleBody_AndRemainsCompact()
        {
            var record = CreateRecord(
                Destuffed(
                    "<body@example.test>",
                    body: UniqueBody,
                    newsgroups: "alt.test"));

            var payload = OverviewArticleV1Codec.Encode(record);

            Assert.False(OverviewArticleV1Codec.ContainsCompleteArticle(payload, record.ArtData.Span));
            Assert.DoesNotContain(Encoding.ASCII.GetBytes("UNIQUE-OVERVIEW-BODY-TOKEN"), payload);
            Assert.Equal(341, record.ArtSize);
            Assert.Equal(140, payload.Length);
        }

        /// <summary>
        /// Newsgroup tokens stay in source order rather than being sorted.
        /// </summary>
        [Fact]
        public void Encode_PreservesNewsgroupOrder_FromCanonicalHeader()
        {
            var record = CreateRecord(
                Destuffed(
                    "<order@example.test>",
                    newsgroups: "group.z,group.a,group.m"));

            var decoded = OverviewArticleV1Codec.Decode(OverviewArticleV1Codec.Encode(record));
            AssertAsciiGroups(["group.z", "group.a", "group.m"], decoded.Newsgroups);
        }

        /// <summary>
        /// The overview date is the canonical header value <c>Fri, 23 Aug 2024 05:30:10 +0000</c>, matching <c>ArticleRecord.Date</c>.
        /// It is not the round-trip <c>DateTime</c> text, the original <c>+0200</c> clock time, or the unrewritten offset.
        /// </summary>
        [Fact]
        public void Encode_DateIsCanonicalHeaderValue_NotCanonicalUtcDateTime()
        {
            var destuffed =
                "Path: peer.example\r\n" +
                "Date: Fri, 23 Aug 2024 07:30:10 +0200\r\n" +
                "Message-ID: <date-semantics@example.test>\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "Subject: date-semantics\r\n" +
                "\r\n" +
                "body\r\n";
            var record = CreateRecord(destuffed);
            var payload = OverviewArticleV1Codec.Encode(record);
            var decoded = OverviewArticleV1Codec.Decode(payload);

            Assert.Equal(new DateTime(2024, 8, 23, 5, 30, 10, DateTimeKind.Utc), record.CanonicalUtc);
            Assert.Equal("Fri, 23 Aug 2024 05:30:10 +0000", Encoding.ASCII.GetString(record.Date));
            Assert.True(record.Date.SequenceEqual(decoded.Date));
            AssertAsciiEqual("Fri, 23 Aug 2024 05:30:10 +0000", decoded.Date);
            Assert.NotEqual(record.CanonicalUtc.ToString("O"), Encoding.ASCII.GetString(decoded.Date));
            Assert.True(decoded.Date.AsSpan().IndexOf("07:30:10"u8) < 0);
            Assert.True(decoded.Date.AsSpan().IndexOf("+0200"u8) < 0);
        }

        /// <summary>
        /// Decoded newsgroups are the trimmed tokens in source order, while the record still stores the original header text including spaces.
        /// </summary>
        [Fact]
        public void Encode_Newsgroups_AreExactTokensInSourceOrder()
        {
            var record = CreateRecord(
                Destuffed(
                    "<groups@example.test>",
                    newsgroups: "group.one, group.two, group.three"));

            var decoded = OverviewArticleV1Codec.Decode(OverviewArticleV1Codec.Encode(record));
            AssertAsciiGroups(["group.one", "group.two", "group.three"], decoded.Newsgroups);
            Assert.Equal("group.one, group.two, group.three", Encoding.ASCII.GetString(record.Newsgroups));
        }

        /// <summary>
        /// A missing References field encodes as empty octets. Subject and From still come from the article headers.
        /// </summary>
        [Fact]
        public void Encode_MissingReferences_IsEmptyRatherThanInvented()
        {
            var record = CreateRecord(
                Destuffed(
                    "<noref@example.test>",
                    newsgroups: "alt.test"));

            Assert.False(record.Fields.References.IsPresent);
            Assert.True(record.References.IsEmpty);
            var decoded = OverviewArticleV1Codec.Decode(OverviewArticleV1Codec.Encode(record));
            Assert.Empty(decoded.References);
            AssertAsciiEqual("ingress-test", decoded.Subject);
            AssertAsciiEqual("user@example.test", decoded.From);
        }

        /// <summary>
        /// Encoding the same record twice yields identical bytes, and decoding restores schema version, Message-ID, groups, overview fields, counts, and the 32-byte article id.
        /// </summary>
        [Fact]
        public void EncodeDecode_RoundTrip_IsEquivalent()
        {
            var record = CreateRecord(
                Destuffed(
                    "<roundtrip@example.test>",
                    body: UniqueBody,
                    newsgroups: "alt.test, rec.test",
                    subject: "overview-subject",
                    from: "poster@example.test",
                    references: "<prev@example.test>"));

            var first = OverviewArticleV1Codec.Encode(record);
            var decoded = OverviewArticleV1Codec.Decode(first);
            var second = OverviewArticleV1Codec.Encode(record);

            Assert.Equal(first, second);
            Assert.Equal(OverviewArticleV1.CurrentSchemaVersion, decoded.SchemaVersion);
            AssertAsciiEqual("<roundtrip@example.test>", decoded.MessageId);
            AssertAsciiGroups(["alt.test", "rec.test"], decoded.Newsgroups);
            AssertAsciiEqual("overview-subject", decoded.Subject);
            AssertAsciiEqual("poster@example.test", decoded.From);
            Assert.True(record.Date.SequenceEqual(decoded.Date));
            AssertAsciiEqual("<prev@example.test>", decoded.References);
            Assert.Equal((uint)record.ArtSize, decoded.Bytes);
            Assert.Equal((uint)record.ArtLines, decoded.Lines);

            Span<byte> artId = stackalloc byte[ArticleId.Length];
            record.ArtId.CopyTo(artId);
            Assert.Equal(artId.ToArray(), decoded.ArticleId);
            Assert.Equal(decoded.ArticleId, OverviewArticleV1Codec.Decode(second).ArticleId);
        }

        /// <summary>
        /// A CRLF-folded Newsgroups header is accepted, and overview stores each comma-separated group.
        /// </summary>
        [Fact]
        public void FoldedNewsgroups_AreAccepted_AndOverviewStoresEachGroup()
        {
            var destuffed =
                "Path: peer.example\r\n" +
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                "Message-ID: <folded-ng@example.test>\r\n" +
                "Newsgroups: group.one, group.two,\r\n" +
                " group.three\r\n" +
                "From: user@example.test\r\n" +
                "Subject: folded-newsgroups\r\n" +
                "\r\n" +
                "body\r\n";
            var created = ArticleRecordFactory.TryCreate(
                new NntpArticleParser(LocalFqdn),
                Encoding.ASCII.GetBytes(destuffed));

            Assert.True(created.IsAccepted);
            var overview = OverviewArticleV1Codec.Decode(OverviewArticleV1Codec.Encode(created.Record));
            AssertAsciiGroups(["group.one", "group.two", "group.three"], overview.Newsgroups);
        }

        /// <summary>
        /// ASCII-only Subject octets round-trip unchanged through encode and decode.
        /// </summary>
        [Fact]
        public void EncodeDecode_WhenSubjectIsAscii_PreservesExactBytes()
        {
            var record = CreateRecord(
                Destuffed("<ascii-subject@example.test>", subject: "plain-ascii-subject"));
            var decoded = OverviewArticleV1Codec.Decode(OverviewArticleV1Codec.Encode(record));

            Assert.True(record.Subject.SequenceEqual(decoded.Subject));
            AssertAsciiEqual("plain-ascii-subject", decoded.Subject);
        }

        /// <summary>
        /// A valid UTF-8 Subject (multi-byte <c>é</c>) keeps the same octets and the same Unicode meaning under UTF-8 decode.
        /// </summary>
        [Fact]
        public void EncodeDecode_WhenSubjectIsValidUtf8_PreservesOctetsAndUtf8Meaning()
        {
            var subject = new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xC3, 0xA9 };
            var record = CreateRecordFromBytes(BuildArticleWithSubjectBytes("<utf8-subject@example.test>", subject));
            var decoded = OverviewArticleV1Codec.Decode(OverviewArticleV1Codec.Encode(record));

            Assert.True(record.Subject.SequenceEqual(decoded.Subject));
            Assert.Equal(subject, decoded.Subject);
            Assert.Equal("café", Encoding.UTF8.GetString(decoded.Subject));
        }

        /// <summary>
        /// A lone Latin-1 <c>0xE9</c> Subject byte (not valid UTF-8 alone) round-trips as that octet, not as UTF-8 U+FFFD bytes.
        /// </summary>
        [Fact]
        public void EncodeDecode_WhenSubjectHasLatin1Byte_PreservesExactOctet()
        {
            var subject = new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9 };
            var record = CreateRecordFromBytes(BuildArticleWithSubjectBytes("<latin1-subject@example.test>", subject));
            var payload = OverviewArticleV1Codec.Encode(record);
            var decoded = OverviewArticleV1Codec.Decode(payload);

            Assert.True(record.Subject.SequenceEqual(decoded.Subject));
            Assert.Equal(subject, decoded.Subject);
            Assert.Contains((byte)0xE9, decoded.Subject);
            // U+FFFD as UTF-8 is EF BF BD; the codec must not have substituted that into the field.
            Assert.True(decoded.Subject.AsSpan().IndexOf("\uFFFD"u8) < 0);
            Assert.Contains((byte)0xE9, payload);
        }

        /// <summary>
        /// Multiple high bytes and a malformed UTF-8 sequence round-trip byte-for-byte through the codec.
        /// </summary>
        [Fact]
        public void EncodeDecode_WhenSubjectHasHighAndMalformedUtf8Bytes_PreservesExactOctets()
        {
            // 0x80, 0xFF, and truncated UTF-8 lead 0xC3 without a continuation.
            var subject = new byte[] { (byte)'x', 0x80, 0xFF, 0xC3, (byte)'y' };
            var record = CreateRecordFromBytes(BuildArticleWithSubjectBytes("<binary-subject@example.test>", subject));
            var decoded = OverviewArticleV1Codec.Decode(OverviewArticleV1Codec.Encode(record));

            Assert.True(record.Subject.SequenceEqual(decoded.Subject));
            Assert.Equal(subject, decoded.Subject);
        }

        /// <summary>
        /// A payload that still carries non-UTF-8 Subject octets (as historically written into length-delimited fields)
        /// decodes those octets without UTF-8 replacement when read with the bytes contract.
        /// </summary>
        [Fact]
        public void Decode_WhenLegacyLengthDelimitedSubjectHasHighByte_PreservesOctet()
        {
            var subject = new byte[] { (byte)'L', 0xE9 };
            var record = CreateRecordFromBytes(BuildArticleWithSubjectBytes("<legacy-high@example.test>", subject));
            var payload = OverviewArticleV1Codec.Encode(record);

            // Re-decode the same wire bytes: string-vs-bytes labeling does not change length-delimited framing.
            var decoded = OverviewArticleV1Codec.Decode(payload);
            Assert.Equal(subject, decoded.Subject);
            Assert.Equal(OverviewArticleV1.CurrentSchemaVersion, decoded.SchemaVersion);
        }

        /// <summary>Multi-line body containing a token that must not appear in the overview payload.</summary>
        private const string UniqueBody = "UNIQUE-OVERVIEW-BODY-TOKEN\r\n" +
            "line2-padding-padding-padding-padding-padding-padding\r\n" +
            "line3-padding-padding-padding-padding-padding-padding\r\n";

        /// <summary>
        /// Builds a destuffed article. A null or empty <paramref name="references"/> omits the References header.
        /// </summary>
        /// <param name="messageId">Message-ID header value.</param>
        /// <param name="body">Body text. Defaults to <c>body</c> plus CRLF.</param>
        /// <param name="newsgroups">Newsgroups header value.</param>
        /// <param name="subject">Subject header value.</param>
        /// <param name="from">From header value.</param>
        /// <param name="references">References header value, or null or empty to omit the header.</param>
        /// <returns>The destuffed article text.</returns>
        private static string Destuffed(
            string messageId,
            string body = "body\r\n",
            string newsgroups = "alt.test",
            string subject = "ingress-test",
            string from = "user@example.test",
            string? references = null)
        {
            var referencesHeader = string.IsNullOrEmpty(references)
                ? string.Empty
                : "References: " + references + "\r\n";
            return
                "Path: peer.example\r\n" +
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                "Message-ID: " + messageId + "\r\n" +
                "Newsgroups: " + newsgroups + "\r\n" +
                "From: " + from + "\r\n" +
                "Subject: " + subject + "\r\n" +
                referencesHeader +
                "\r\n" +
                body;
        }

        /// <summary>
        /// Builds destuffed article bytes with an arbitrary Subject value (may include <c>0x80</c>–<c>0xFF</c>).
        /// </summary>
        /// <param name="messageId">Message-ID header value.</param>
        /// <param name="subject">Subject header value octets.</param>
        /// <returns>Destuffed article bytes.</returns>
        private static byte[] BuildArticleWithSubjectBytes(string messageId, ReadOnlySpan<byte> subject)
        {
            var prefix = Encoding.ASCII.GetBytes(
                "Path: peer.example\r\n" +
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                "Message-ID: " + messageId + "\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "Subject: ");
            var suffix = "\r\n\r\nbody\r\n"u8;
            var article = new byte[prefix.Length + subject.Length + suffix.Length];
            prefix.CopyTo(article);
            subject.CopyTo(article.AsSpan(prefix.Length));
            suffix.CopyTo(article.AsSpan(prefix.Length + subject.Length));
            return article;
        }

        /// <summary>
        /// Parses <paramref name="destuffed"/> with <see cref="LocalFqdn"/> and returns the accepted record.
        /// </summary>
        /// <param name="destuffed">Destuffed article text.</param>
        /// <returns>The accepted canonical record.</returns>
        private static ArticleRecord CreateRecord(string destuffed)
        {
            return CreateRecordFromBytes(Encoding.ASCII.GetBytes(destuffed));
        }

        /// <summary>
        /// Parses <paramref name="destuffed"/> with <see cref="LocalFqdn"/> and returns the accepted record.
        /// </summary>
        /// <param name="destuffed">Destuffed article bytes.</param>
        /// <returns>The accepted canonical record.</returns>
        private static ArticleRecord CreateRecordFromBytes(byte[] destuffed)
        {
            var created = ArticleRecordFactory.TryCreate(
                new NntpArticleParser(LocalFqdn),
                destuffed);
            Assert.True(created.IsAccepted);
            return created.Record;
        }

        /// <summary>Asserts <paramref name="actual"/> equals the ASCII encoding of <paramref name="expected"/>.</summary>
        /// <param name="expected">Expected ASCII text.</param>
        /// <param name="actual">Decoded overview field octets.</param>
        private static void AssertAsciiEqual(string expected, byte[] actual)
            => Assert.Equal(Encoding.ASCII.GetBytes(expected), actual);

        /// <summary>Asserts decoded newsgroup tokens match ASCII group names in order.</summary>
        /// <param name="expected">Expected group names.</param>
        /// <param name="actual">Decoded newsgroup token octets.</param>
        private static void AssertAsciiGroups(string[] expected, IReadOnlyList<byte[]> actual)
        {
            Assert.Equal(expected.Length, actual.Count);
            for (var i = 0; i < expected.Length; i++)
            {
                AssertAsciiEqual(expected[i], actual[i]);
            }
        }
    }
}

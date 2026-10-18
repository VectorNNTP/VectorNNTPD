using System.IO.Hashing;
using VectorNNTPD.BackFillerService.ArticleWork;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.BackFillerService.Tests.Retention;
using System.Text;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.BackFillerService.Tests.Articles
{
    /// <summary>
    /// Proves destuffing, canonicalization, body restuffing, and retention compose once.
    /// </summary>
    public sealed class ArticleWireFormatRetentionTests
    {
        /// <summary>Message-ID shared by the dot-heavy logical fixture and retention admission.</summary>
        private const string MessageId = "<dot-wire@example.test>";

        /// <summary>Logical article with dot-leading header and body lines used for end-to-end wire/retention proof.</summary>
        private static readonly byte[] LogicalArticle = Encoding.ASCII.GetBytes(
            "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
            "Message-ID: <dot-wire@example.test>\r\n" +
            "Newsgroups: alt.test\r\n" +
            "From: user@example.test\r\n" +
            "Subject: dots\r\n" +
            "Path: peer.example\r\n" +
            ".X-Dot: kept\r\n" +
            "\r\n" +
            ".only\r\n" +
            "..two\r\n" +
            "...three\r\n" +
            "plain\n" +
            "mid.dot\r\n" +
            "\r\n" +
            ".last\r\n");

        /// <summary>
        /// Destuffed NNTP payload, canonical record, and retention admission keep a single stuffed body image without listener framing.
        /// </summary>
        [Fact]
        public async Task Retrieval_canonicalization_and_retention_keep_one_stuffed_body()
        {
            var wireResponse = FrameStuffedMultiline(LogicalArticle);
            var destuffed = await ReadPayloadAsync(wireResponse);
            Assert.Equal(LogicalArticle, destuffed);

            var parser = new NntpArticleParser("backfiller.test");
            var created = ArticleRecordFactory.TryCreate(parser, destuffed, ArticlePathMode.Traverse);
            Assert.True(created.IsAccepted, created.ParseFailure.ToString());
            var retained = created.Record.ArtData.ToArray();

            var separator = retained.AsSpan().IndexOf("\r\n\r\n"u8);
            Assert.True(separator > 0);
            var header = retained.AsSpan(0, separator + 4);
            var body = retained.AsSpan(separator + 4);
            Assert.Contains(".X-Dot: kept\r\n"u8, header);
            Assert.DoesNotContain("..X-Dot:"u8, header);
            Assert.Equal(
                "..only\r\n...two\r\n....three\r\nplain\nmid.dot\r\n\r\n..last\r\n"u8,
                body);
            Assert.False(body.EndsWith("\r\n.\r\n"u8));
            Assert.Equal(ArticleId.FromMessageId(Encoding.ASCII.GetBytes(MessageId)), created.Record.ArtId);

            await using var authority = ArticleRetentionAuthorityTests.Create(TimeProvider.System, maxBytes: 1024 * 1024);
            var requestId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            var retention = authority.RetainCanonical(
                MessageId,
                requestId,
                created.Record,
                created.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.Retained, retention.Kind);
            var beforeBytes = authority.RetainedPayloadBytes;
            var claimed = authority.TryClaimForDelivery(Encoding.ASCII.GetBytes(MessageId));
            Assert.Equal(ArticleDeliveryClaimKind.NotFrameable, claimed.Kind);
            Assert.Null(claimed.Claim);
            Assert.Equal(1, authority.RetainedCount);
            Assert.Equal(beforeBytes, authority.RetainedPayloadBytes);
            Assert.True(created.Record.ArtData.Span.SequenceEqual(retained));
        }

        /// <summary>
        /// BackFiller retention uses Common's factory. The retained body is stuffed once, the header dot is not stuffed, and ArtHash covers those exact bytes.
        /// </summary>
        [Fact]
        public void Common_factory_is_the_retained_stuffed_representation()
        {
            var parser = new NntpArticleParser("backfiller.test");
            var created = ArticleRecordFactory.TryCreate(parser, LogicalArticle, ArticlePathMode.Traverse);
            Assert.True(created.IsAccepted, created.ParseFailure.ToString());
            Assert.Equal(XxHash3.HashToUInt64(created.Record.ArtData.Span), created.Record.ArtHash);
            Assert.Equal(ArticleId.FromMessageId(Encoding.ASCII.GetBytes(MessageId)), created.Record.ArtId);

            var separator = created.Record.ArtData.Span.IndexOf("\r\n\r\n"u8);
            Assert.True(separator > 0);
            var header = created.Record.ArtData.Span[..(separator + 4)];
            Assert.Contains(".X-Dot: kept\r\n"u8, header);
            Assert.DoesNotContain("..X-Dot:"u8, header);
            Assert.Equal(
                "..only\r\n...two\r\n....three\r\nplain\nmid.dot\r\n\r\n..last\r\n"u8,
                created.Record.ArtData.Span[(separator + 4)..]);
        }

        /// <summary>
        /// A CRLF article from Common is frameable. Delivery stuffs the dot-leading header once and writes the already-stuffed body from the retained slice.
        /// </summary>
        [Fact]
        public void Delivery_does_not_stuff_a_common_body_a_second_time()
        {
            var logical = Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
                "Message-ID: <dot-frame@example.test>\r\n" +
                "Newsgroups: alt.test\r\n" +
                "From: user@example.test\r\n" +
                "Subject: dots\r\n" +
                "Path: peer.example\r\n" +
                ".X-Dot: kept\r\n" +
                "\r\n" +
                "\r\n" +
                ".\r\n" +
                "..\r\n" +
                "...\r\n" +
                "plain\r\n");
            var created = ArticleRecordFactory.TryCreate(
                new NntpArticleParser("backfiller.test"),
                logical,
                ArticlePathMode.Normalize);
            Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
            var retained = created.Record.ArtData.Span;
            var bodyStart = created.Record.Framing.BodyStart;
            Assert.True(created.Record.Framing.IsFrameable);
            Assert.True(bodyStart > 0 && bodyStart < retained.Length);
            Assert.True(ArticleWorkDeliveryFraming.TryCreateStuffedHeader(retained, bodyStart, out var header));
            Assert.NotNull(header);
            Assert.True(header.Length < retained.Length);
            var wire = new byte[header.Length + retained.Length - bodyStart];
            header.CopyTo(wire);
            retained[bodyStart..].CopyTo(wire.AsSpan(header.Length));
            Assert.Equal(retained.ToArray(), created.Record.ArtData.ToArray());
            Assert.True(wire.AsSpan(header.Length).SequenceEqual(retained[bodyStart..]));

            var separator = wire.AsSpan().IndexOf("\r\n\r\n"u8);
            Assert.True(separator > 0);
            Assert.Contains("..X-Dot: kept\r\n"u8, wire);
            Assert.Equal("\r\n..\r\n...\r\n....\r\nplain\r\n"u8, wire.AsSpan(separator + 4));
            Assert.False(wire.AsSpan().EndsWith("\r\n.\r\n"u8));
            Assert.False(created.Record.ArtData.Span.EndsWith("\r\n.\r\n"u8));
        }

        /// <summary>
        /// Applies NNTP multiline stuffing to every line of a logical article and appends the terminator.
        /// </summary>
        private static byte[] FrameStuffedMultiline(byte[] logical)
        {
            var buffer = new byte[logical.Length * 2 + 8];
            var written = 0;
            var index = 0;
            while (index < logical.Length)
            {
                var end = index;
                while (end < logical.Length && logical[end] != (byte)'\n')
                {
                    end++;
                }

                var contentEnd = end;
                if (contentEnd > index && logical[contentEnd - 1] == (byte)'\r')
                {
                    contentEnd--;
                }

                if (contentEnd > index && logical[index] == (byte)'.')
                {
                    buffer[written++] = (byte)'.';
                }

                logical.AsSpan(index, end - index).CopyTo(buffer.AsSpan(written));
                written += end - index;
                if (end < logical.Length)
                {
                    buffer[written++] = logical[end];
                    index = end + 1;
                }
                else
                {
                    break;
                }
            }

            buffer[written++] = (byte)'.';
            buffer[written++] = (byte)'\r';
            buffer[written++] = (byte)'\n';
            return buffer.AsSpan(0, written).ToArray();
        }

        /// <summary>Regression matrix for multiline stuffing edge cases used when building wire responses.</summary>
        [Fact]
        public void Stuffed_multiline_framing_covers_empty_final_and_dot_leading_lines()
        {
            Assert.Equal(".\r\n"u8.ToArray(), FrameStuffedMultiline([]));
            Assert.Equal("plain.\r\n"u8.ToArray(), FrameStuffedMultiline("plain"u8.ToArray()));
            Assert.Equal("a\r\n.\r\n"u8.ToArray(), FrameStuffedMultiline("a\r\n"u8.ToArray()));
            Assert.Equal("a\n.\r\n"u8.ToArray(), FrameStuffedMultiline("a\n"u8.ToArray()));
            Assert.Equal("..\r\n.\r\n"u8.ToArray(), FrameStuffedMultiline(".\r\n"u8.ToArray()));
            Assert.Equal("...\r\n"u8.ToArray(), FrameStuffedMultiline("."u8.ToArray()));
            Assert.Equal("a\r\nb\n..\r\n.\r\n"u8.ToArray(), FrameStuffedMultiline("a\r\nb\n.\r\n"u8.ToArray()));
        }

        /// <summary>Destuffs an NNTP multiline response through the production stream reader.</summary>
        /// <param name="response">Framed article bytes including terminator.</param>
        /// <returns>Logical article bytes after unstuffing.</returns>
        private static async Task<byte[]> ReadPayloadAsync(byte[] response)
        {
            var reader = new NntpStreamReader(new MemoryStream(response, writable: false), 64);
            return await reader.ReadArticlePayloadAsync(1024 * 1024, TimeSpan.FromSeconds(5), CancellationToken.None);
        }
    }
}

using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;

namespace VectorNNTPD.Common.Tests.Articles
{
    /// <summary>
    /// Framing and hash contracts for a logical canonical article, including rented-array slack.
    /// </summary>
    public sealed class CanonicalArticleFramingTests
    {
        /// <summary>
        /// The body starts at the first byte after <c>CRLF CRLF</c>. An empty body uses the logical length.
        /// </summary>
        [Fact]
        public void Body_start_is_the_first_byte_after_crlf_crlf()
        {
            var emptyBody = Create("Subject: empty\r\n", string.Empty);
            Assert.True(emptyBody.Record.Framing.IsFrameable);
            Assert.Equal(emptyBody.Record.ArtSize, emptyBody.Record.Framing.BodyStart);
            Assert.Equal(0, emptyBody.Record.ArtData.Span[emptyBody.Record.Framing.BodyStart..].Length);
            Assert.Equal(emptyBody.Record.ArtHash, XxHash3.HashToUInt64(emptyBody.Record.ArtData.Span));

            var withBody = Create("Subject: body\r\n", "line\r\n");
            var separator = withBody.Record.ArtData.Span.IndexOf("\r\n\r\n"u8);
            Assert.Equal(separator + 4, withBody.Record.Framing.BodyStart);
            Assert.Equal("line\r\n"u8.ToArray(), withBody.Record.ArtData.Span[withBody.Record.Framing.BodyStart..].ToArray());
        }

        /// <summary>
        /// A destuffed dot-leading body is stored already stuffed. Framing stays on the logical bytes and the hash matches one shot.
        /// </summary>
        [Fact]
        public void A_dot_leading_body_keeps_its_stuffing_dot_in_the_logical_image()
        {
            var created = Create("Subject: dots\r\n", ".\r\n..\r\n");
            Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
            var body = created.Record.ArtData.Span[created.Record.Framing.BodyStart..];
            Assert.Equal("..\r\n...\r\n"u8.ToArray(), body.ToArray());
            Assert.Equal(created.Record.ArtHash, XxHash3.HashToUInt64(created.Record.ArtData.Span));
            Assert.NotEqual(created.Record.ArtHash, XxHash3.HashToUInt64(".\r\n..\r\n"u8));
        }

        /// <summary>
        /// Path traversal rewrites the stored header. The hash and the body index follow the canonical image.
        /// </summary>
        [Fact]
        public void Header_canonicalization_hashes_and_frames_the_rewritten_image()
        {
            var source = Article("<rewrite@example.test>", "body\r\n", "Path: peer.example\r\n");
            var created = ArticleRecordFactory.TryCreate(new NntpArticleParser("backfiller.test"), source, ArticlePathMode.Traverse);
            Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
            Assert.False(created.Record.ArtData.Span.SequenceEqual(source));
            Assert.Equal(created.Record.ArtHash, XxHash3.HashToUInt64(created.Record.ArtData.Span));
            Assert.NotEqual(created.Record.ArtHash, XxHash3.HashToUInt64(source));
            var separator = created.Record.ArtData.Span.IndexOf("\r\n\r\n"u8);
            Assert.Equal(separator + 4, created.Record.Framing.BodyStart);
            Assert.Equal("body\r\n"u8.ToArray(), created.Record.ArtData.Span[created.Record.Framing.BodyStart..].ToArray());
            Assert.True(created.Record.ArtData.Span[..created.Record.Framing.BodyStart].IndexOf("backfiller.test"u8) >= 0);
        }

        /// <summary>
        /// Chunking the finished logical image matches the stored one-shot hash. Slack and a different chunk size do not.
        /// </summary>
        [Fact]
        public void Chunked_hash_of_the_finished_logical_image_matches_the_one_shot_hash()
        {
            var created = Create(".X-Dot: kept\r\n", ".\r\nplain\r\n");
            var logical = created.Record.ArtData.Span;
            Assert.Equal(created.Record.ArtHash, XxHash3.HashToUInt64(logical));
            foreach (var chunkSize in new[] { 1, 2, 3, 7, 64, logical.Length })
            {
                var hasher = new XxHash3();
                for (var offset = 0; offset < logical.Length; offset += chunkSize)
                {
                    var count = Math.Min(chunkSize, logical.Length - offset);
                    hasher.Append(logical.Slice(offset, count));
                }

                Assert.Equal(created.Record.ArtHash, hasher.GetCurrentHashAsUInt64());
            }

            var padded = new byte[logical.Length + 32];
            logical.CopyTo(padded);
            padded.AsSpan(logical.Length).Fill(0xA5);
            Assert.NotEqual(created.Record.ArtHash, XxHash3.HashToUInt64(padded));
            Assert.Equal(created.Record.ArtHash, XxHash3.HashToUInt64(padded.AsSpan(0, logical.Length)));
        }

        /// <summary>
        /// Bytes past the logical length do not change framing. A later write into the logical bytes is a new analysis.
        /// </summary>
        [Fact]
        public void Framing_uses_the_logical_span_and_does_not_follow_a_later_write()
        {
            var created = Create("Subject: live\r\n", "body\r\n");
            var snapshot = created.Record.Framing;
            Assert.True(snapshot.IsFrameable);
            Assert.True(MemoryMarshal.TryGetArray(created.Record.ArtData, out var segment));
            var array = segment.Array!;
            var padded = new byte[array.Length + 16];
            array.AsSpan(0, created.Record.ArtSize).CopyTo(padded);
            padded.AsSpan(created.Record.ArtSize).Fill(0);
            var logical = CanonicalArticleFraming.Analyze(padded.AsSpan(0, created.Record.ArtSize));
            Assert.Equal(snapshot.IsFrameable, logical.IsFrameable);
            Assert.Equal(snapshot.BodyStart, logical.BodyStart);
            Assert.False(CanonicalArticleFraming.Analyze(padded).IsFrameable);

            var body = created.Record.ArtData.Span.IndexOf("body\r\n"u8);
            array[segment.Offset + body] = 0;
            Assert.True(created.Record.Framing.IsFrameable);
            Assert.Equal(snapshot.BodyStart, created.Record.Framing.BodyStart);
            Assert.False(CanonicalArticleFraming.Analyze(created.Record.ArtData.Span).IsFrameable);
        }

        /// <summary>Empty, bare CR or LF, NUL, and a separator-less dot line are not frameable.</summary>
        [Fact]
        public void Malformed_spans_are_not_frameable()
        {
            AssertRejected([]);
            AssertRejected([(byte)'\r']);
            AssertRejected([(byte)'\n']);
            AssertRejected([(byte)'\0']);
            AssertRejected(".\r\n"u8.ToArray());
            AssertRejected("Subject: s\r\n\r\nno-final-crlf"u8.ToArray());
            AssertRejected("Subject: s\r\n\0\r\n\r\nbody\r\n"u8.ToArray());
        }

        /// <summary>
        /// A single bare LF between otherwise CRLF header lines is not frameable (RFC 3977 §3.1.1 exclusive CRLF).
        /// Matches the observed .artifacts/Articles defect: one bare LF in the header region, exclusive CRLF body.
        /// </summary>
        [Fact]
        public void Single_bare_lf_in_header_region_is_not_frameable()
        {
            var article = "Message-ID: <bare-lf@example.test>\r\nPath: peer!for-mail\nLines: 1\r\n\r\nbody\r\n"u8.ToArray();
            var framing = CanonicalArticleFraming.Analyze(article);
            Assert.False(framing.IsFrameable);
            Assert.True(framing.BodyStart > 0);
        }

        /// <summary>
        /// Inserting CR before that bare LF yields exclusive CRLF and a frameable article; bodies stay unchanged.
        /// </summary>
        [Fact]
        public void Normalizing_header_bare_lf_to_crlf_makes_article_frameable()
        {
            var bare = "Message-ID: <bare-lf@example.test>\r\nPath: peer!for-mail\nLines: 1\r\n\r\nbody\r\n"u8.ToArray();
            Assert.False(CanonicalArticleFraming.Analyze(bare).IsFrameable);

            var normalized = NormalizeBareLfToCrlf(bare);
            var framing = CanonicalArticleFraming.Analyze(normalized);
            Assert.True(framing.IsFrameable);
            Assert.Equal("body\r\n"u8, normalized.AsSpan(framing.BodyStart));
            Assert.Equal(bare.Length + 1, normalized.Length);
        }

        /// <summary>Replaces each bare LF with CRLF; existing CRLF pairs are left intact.</summary>
        private static byte[] NormalizeBareLfToCrlf(ReadOnlySpan<byte> source)
        {
            var buffer = new byte[source.Length + 16];
            var written = 0;
            byte previous = 0;
            foreach (var value in source)
            {
                if (value == (byte)'\n' && previous != (byte)'\r')
                {
                    buffer[written++] = (byte)'\r';
                }

                buffer[written++] = value;
                previous = value;
            }

            return buffer.AsSpan(0, written).ToArray();
        }

        /// <summary>Asserts <paramref name="article"/> is not a multiline block.</summary>
        private static void AssertRejected(byte[] article)
        {
            var framing = CanonicalArticleFraming.Analyze(article);
            Assert.False(framing.IsFrameable);
            var separator = article.AsSpan().IndexOf("\r\n\r\n"u8);
            Assert.Equal(separator < 0 ? -1 : separator + 4, framing.BodyStart);
        }

        /// <summary>Builds one accepted article and returns the factory result.</summary>
        private static ArticleRecordCreateResult Create(string extraHeader, string body)
        {
            var created = ArticleRecordFactory.TryCreate(
                new NntpArticleParser("backfiller.test"),
                Article("<frame@example.test>", body, extraHeader),
                ArticlePathMode.Traverse);
            Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
            return created;
        }

        /// <summary>Destuffed article bytes. <paramref name="extraHeader"/> is inserted before the separator and includes its CRLF.</summary>
        private static byte[] Article(string messageId, string body, string extraHeader) =>
            Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n"
                + "Message-ID: " + messageId + "\r\n"
                + "Newsgroups: alt.test\r\n"
                + "From: user@example.test\r\n"
                + "Subject: s\r\n"
                + extraHeader
                + "\r\n"
                + body);
    }
}

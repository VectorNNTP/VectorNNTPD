using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.Common.Tests.Articles
{
    /// <summary>
    /// Shared CanonicalV1 → NNTP multiline delivery framing: header-only transport stuffing and rejection.
    /// </summary>
    public sealed class CanonicalArticleDeliveryFramingTests
    {
        /// <summary>NUL anywhere in the article makes framing and header stuffing fail without mutating input bytes.</summary>
        [Fact]
        public void Nul_is_not_frameable()
        {
            var article = "Subject: s\r\n\r\na\0b\r\n"u8.ToArray();
            Assert.False(CanonicalArticleDeliveryFraming.IsFrameable(article));
            Assert.False(CanonicalArticleDeliveryFraming.TryStuffHeaderLines(article, out var stuffed));
            Assert.Null(stuffed);
            Assert.Equal("Subject: s\r\n\r\na\0b\r\n"u8, article);
        }

        /// <summary>Dot-leading content before a header/body separator is not treated as a deliverable NNTP article.</summary>
        [Fact]
        public void A_dot_leading_line_without_a_header_body_separator_is_not_frameable()
        {
            var article = ".X-Dot: kept\r\n"u8.ToArray();
            Assert.False(CanonicalArticleDeliveryFraming.IsFrameable(article));
            Assert.False(CanonicalArticleDeliveryFraming.TryStuffHeaderLines(article, out var stuffed));
            Assert.Null(stuffed);
        }

        /// <summary>Dot-leading headers gain one stuffing dot while the caller's article array is not modified in place.</summary>
        [Fact]
        public void A_dot_leading_header_is_stuffed_and_the_retained_bytes_stay_unchanged()
        {
            var article = ".X-Dot: kept\r\n\r\n.only\r\n"u8.ToArray();
            var retained = article.ToArray();
            Assert.True(CanonicalArticleDeliveryFraming.IsFrameable(article));
            Assert.True(CanonicalArticleDeliveryFraming.TryStuffHeaderLines(article, out var stuffed));
            Assert.Equal("..X-Dot: kept\r\n\r\n"u8.ToArray(), stuffed);
            Assert.Equal(".only\r\n"u8.ToArray(), article.AsSpan(".X-Dot: kept\r\n\r\n"u8.Length).ToArray());
            Assert.Equal(retained, article);
        }

        /// <summary>Body lines that already carry CanonicalV1 stuffing are not part of the stuffed-header buffer.</summary>
        [Fact]
        public void Stuffing_headers_does_not_copy_or_restuff_the_body()
        {
            var article = ".X-One: a\r\n\r\n.only\r\n..\r\n...three\r\n"u8.ToArray();
            Assert.True(CanonicalArticleDeliveryFraming.TryStuffHeaderLines(article, out var stuffed));
            Assert.Equal("..X-One: a\r\n\r\n"u8.ToArray(), stuffed);
            Assert.Equal(".only\r\n..\r\n...three\r\n"u8.ToArray(), article.AsSpan(".X-One: a\r\n\r\n"u8.Length).ToArray());
        }

        /// <summary>No header dot means the caller keeps the retained article and no socket image is allocated.</summary>
        [Fact]
        public void A_header_without_a_leading_dot_does_not_allocate_a_socket_image()
        {
            var article = "Subject: s\r\n\r\n.only\r\n"u8.ToArray();
            var bodyStart = CanonicalArticleFraming.Analyze(article).BodyStart;
            Assert.False(CanonicalArticleDeliveryFraming.TryCreateStuffedHeader(article, bodyStart, out var stuffed));
            Assert.Null(stuffed);
        }
    }
}

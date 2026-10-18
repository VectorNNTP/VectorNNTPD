using VectorNNTPD.BackFillerService.ArticleWork;

namespace VectorNNTPD.BackFillerService.Tests.Articles
{
    /// <summary>
    /// Regressions for listener delivery framing: header dot-stuffing and rejection when a socket image cannot be formed safely.
    /// </summary>
    public sealed class ArticleWorkDeliveryFramingTests
    {
        /// <summary>NUL anywhere in the article makes framing and header stuffing fail without mutating input bytes.</summary>
        [Fact]
        public void Nul_is_not_frameable()
        {
            var article = "Subject: s\r\n\r\na\0b\r\n"u8.ToArray();
            Assert.False(ArticleWorkDeliveryFraming.IsFrameable(article));
            Assert.False(ArticleWorkDeliveryFraming.TryStuffHeaderLines(article, out var stuffed));
            Assert.Null(stuffed);
            Assert.Equal("Subject: s\r\n\r\na\0b\r\n"u8, article);
        }

        /// <summary>Dot-leading content before a header/body separator is not treated as a deliverable NNTP article.</summary>
        [Fact]
        public void A_dot_leading_line_without_a_header_body_separator_is_not_frameable()
        {
            var article = ".X-Dot: kept\r\n"u8.ToArray();
            Assert.False(ArticleWorkDeliveryFraming.IsFrameable(article));
            Assert.False(ArticleWorkDeliveryFraming.TryStuffHeaderLines(article, out var stuffed));
            Assert.Null(stuffed);
            Assert.Equal(".X-Dot: kept\r\n"u8, article);
        }

        /// <summary>Header-only CRLF articles need no stuffing and leave the retained buffer unchanged.</summary>
        [Fact]
        public void A_crlf_article_without_a_separator_or_a_dot_line_is_sent_unchanged()
        {
            var article = "Subject: s\r\n"u8.ToArray();
            Assert.True(ArticleWorkDeliveryFraming.IsFrameable(article));
            Assert.False(ArticleWorkDeliveryFraming.TryStuffHeaderLines(article, out var stuffed));
            Assert.Null(stuffed);
        }

        /// <summary>Dot-leading headers gain one stuffing dot while the caller's article array is not modified in place.</summary>
        [Fact]
        public void A_dot_leading_header_is_stuffed_and_the_retained_bytes_stay_unchanged()
        {
            var article = ".X-Dot: kept\r\n\r\n.only\r\n"u8.ToArray();
            var retained = article.ToArray();
            Assert.True(ArticleWorkDeliveryFraming.IsFrameable(article));
            Assert.True(ArticleWorkDeliveryFraming.TryStuffHeaderLines(article, out var stuffed));
            Assert.Equal("..X-Dot: kept\r\n\r\n.only\r\n"u8.ToArray(), stuffed);
            Assert.Equal(retained, article);
        }

        /// <summary>Bare LF inside a dot-leading header rejects framing so no delivery bytes are produced.</summary>
        [Fact]
        public void A_bare_lf_in_a_dot_leading_header_does_not_produce_a_socket_image()
        {
            AssertRejected(".X-Dot: kept\n\r\n\r\nbody\r\n"u8.ToArray());
        }

        /// <summary>Bare CR inside a dot-leading header rejects framing so no delivery bytes are produced.</summary>
        [Fact]
        public void A_bare_cr_in_a_dot_leading_header_does_not_produce_a_socket_image()
        {
            AssertRejected(".X-Dot: kept\rX\r\n\r\nbody\r\n"u8.ToArray());
        }

        /// <summary>NUL in a dot-leading header rejects framing and leaves the input snapshot unchanged.</summary>
        [Fact]
        public void A_nul_in_a_dot_leading_header_does_not_produce_a_socket_image()
        {
            AssertRejected(".X-Dot: ke\0pt\r\n\r\nbody\r\n"u8.ToArray());
        }

        /// <summary>NUL after the separator in a dot-leading-header article rejects framing.</summary>
        [Fact]
        public void A_nul_in_the_body_of_a_dot_leading_header_does_not_produce_a_socket_image()
        {
            AssertRejected(".X-Dot: kept\r\n\r\nbo\0dy\r\n"u8.ToArray());
        }

        /// <summary>Bare LF in the body rejects framing even when the header would otherwise be stuffable.</summary>
        [Fact]
        public void A_bare_lf_in_the_body_of_a_dot_leading_header_does_not_produce_a_socket_image()
        {
            AssertRejected(".X-Dot: kept\r\n\r\nbo\ndy\r\n"u8.ToArray());
        }

        /// <summary>Bare CR in the body rejects framing even when the header would otherwise be stuffable.</summary>
        [Fact]
        public void A_bare_cr_in_the_body_of_a_dot_leading_header_does_not_produce_a_socket_image()
        {
            AssertRejected(".X-Dot: kept\r\n\r\nbo\rdy\r\n"u8.ToArray());
        }

        /// <summary>Missing header/body separator rejects framing for dot-leading header articles.</summary>
        [Fact]
        public void A_missing_header_body_separator_does_not_produce_a_socket_image()
        {
            AssertRejected(".X-Dot: kept\r\nbody\r\n"u8.ToArray());
        }

        /// <summary>LF-only separators are not accepted for delivery framing when headers need stuffing.</summary>
        [Fact]
        public void An_lf_only_separator_does_not_produce_a_socket_image()
        {
            AssertRejected(".X-Dot: kept\n\nbody\n"u8.ToArray());
        }

        /// <summary>Asserts framing and header stuffing both fail without altering the caller's byte snapshot.</summary>
        /// <param name="article">Article bytes that must not produce a deliverable socket image.</param>
        private static void AssertRejected(byte[] article)
        {
            var retained = article.ToArray();
            Assert.False(ArticleWorkDeliveryFraming.IsFrameable(article));
            Assert.False(ArticleWorkDeliveryFraming.TryStuffHeaderLines(article, out var stuffed));
            Assert.Null(stuffed);
            Assert.Equal(retained, article);
        }
    }
}

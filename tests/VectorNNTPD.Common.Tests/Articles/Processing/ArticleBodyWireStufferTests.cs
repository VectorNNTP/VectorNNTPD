using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles.Processing;
using System.Text;

namespace VectorNNTPD.Common.Tests.Articles.Processing
{
    /// <summary>Body dot-stuffing invariants: headers untouched, one extra dot per dot-leading body line, no spurious terminator.</summary>
    public sealed class ArticleBodyWireStufferTests
    {
        /// <summary>Each body line that begins with a dot receives exactly one additional leading dot; headers stay unstuffed.</summary>
        [Fact]
        public void Body_lines_starting_with_a_dot_gain_exactly_one_dot()
        {
            var logical = Encoding.ASCII.GetBytes(
                "From: a@b.test\r\n.X-Dot: kept\r\n\r\n.only\r\n..two\r\n...three\r\nlater\r\n.last\r\n");
            var wire = ArticleBodyWireStuffer.StuffBody(logical);

            Assert.Equal(
                Encoding.ASCII.GetBytes(
                    "From: a@b.test\r\n.X-Dot: kept\r\n\r\n..only\r\n...two\r\n....three\r\nlater\r\n..last\r\n"),
                wire);
            Assert.False(wire.AsSpan().EndsWith(".\r\n.\r\n"u8));
        }

        /// <summary>When the body needs no stuffing, the stuffer returns the original array instance.</summary>
        [Fact]
        public void Header_dot_is_not_stuffed_and_an_unstuffed_body_is_the_same_array()
        {
            var logical = Encoding.ASCII.GetBytes("From: a@b.test\r\n.X-Dot: kept\r\n\r\nplain\n");
            var wire = ArticleBodyWireStuffer.StuffBody(logical);

            Assert.Same(logical, wire);
        }

        /// <summary>Parameterized body fragments keep the one-dot-per-line-start rule across CRLF, LF, CR, and mixed endings.</summary>
        /// <param name="body">Logical body bytes after the header separator.</param>
        /// <param name="stuffed">Expected stuffed body including any added dots.</param>
        [Theory]
        [InlineData("", "")]
        [InlineData("\n", "\n")]
        [InlineData("\r\n", "\r\n")]
        [InlineData(".", "..")]
        [InlineData(".\r", "..\r")]
        [InlineData(".\n", "..\n")]
        [InlineData(".\r\n", "..\r\n")]
        [InlineData("..\r\n", "...\r\n")]
        [InlineData("...\r\n", "....\r\n")]
        [InlineData("a.b\r\n", "a.b\r\n")]
        [InlineData("plain\r\n.\r\n..x\n...y\r\n.last", "plain\r\n..\r\n...x\n....y\r\n..last")]
        [InlineData("hello\r\n.secret\r\n", "hello\r\n..secret\r\n")]
        [InlineData("hello\r.secret\n", "hello\r..secret\n")]
        [InlineData("hello\r\r.secret\r\n", "hello\r\r..secret\r\n")]
        [InlineData("hello\n.secret\r\n", "hello\n..secret\r\n")]
        [InlineData(".\r\n.\n.\r", "..\r\n..\n..\r")]
        public void Logical_body_lines_keep_one_stuffed_dot_at_the_line_start(string body, string stuffed)
        {
            var logical = Encoding.ASCII.GetBytes("From: a@b.test\r\n\r\n" + body);
            var wire = ArticleBodyWireStuffer.StuffBody(logical);

            Assert.Equal(Encoding.ASCII.GetBytes("From: a@b.test\r\n\r\n" + stuffed), wire);
        }

        /// <summary>Issue #168: a bare CR ends the preceding line so the following dot-leading line is stuffed.</summary>
        [Fact]
        public void Issue168_BareCrBeforeDotLeadingLine_IsStuffed()
        {
            AssertStuffedBody("hello\r.secret\n", "hello\r..secret\n");
        }

        /// <summary>A CRLF before a dot-leading line still stuffs exactly once.</summary>
        [Fact]
        public void DotFollowingCrlf_IsStuffed()
        {
            AssertStuffedBody("hello\r\n.secret\r\n", "hello\r\n..secret\r\n");
        }

        /// <summary>A bare LF before a dot-leading line still stuffs exactly once.</summary>
        [Fact]
        public void DotFollowingBareLf_IsStuffed()
        {
            AssertStuffedBody("hello\n.secret\r\n", "hello\n..secret\r\n");
        }

        /// <summary>Consecutive bare CR bytes each end a line, so a later dot-leading line is stuffed.</summary>
        [Fact]
        public void ConsecutiveBareCr_BeforeDotLeadingLine_IsStuffed()
        {
            AssertStuffedBody("hello\r\r.secret\r\n", "hello\r\r..secret\r\n");
        }

        /// <summary>A body that begins with a dot is stuffed, including consecutive dot-leading lines.</summary>
        [Fact]
        public void BodyBeginningWithDot_AndConsecutiveDotLines_AreStuffed()
        {
            AssertStuffedBody(".\r\n.second\r\n", "..\r\n..second\r\n");
        }

        /// <summary>An empty body and a CRLF-only body need no stuffing and keep their exact bytes.</summary>
        [Fact]
        public void EmptyAndCrlfOnlyBodies_RemainUnchanged()
        {
            AssertStuffedBody(string.Empty, string.Empty);
            AssertStuffedBody("\r\n", "\r\n");
        }

        /// <summary>LF header separators and mixed line endings affect only the body region after the first blank line.</summary>
        [Fact]
        public void Lf_separator_and_mixed_endings_stuff_only_the_body()
        {
            var logical = Encoding.ASCII.GetBytes("From: a@b.test\n\n.\n..x\r\n");
            var wire = ArticleBodyWireStuffer.StuffBody(logical);

            Assert.Equal(Encoding.ASCII.GetBytes("From: a@b.test\n\n..\n...x\r\n"), wire);
        }

        /// <summary>
        /// Materializing the Issue #168 bare-CR body stores the stuffed image and remains not frameable for NNTP delivery.
        /// </summary>
        [Fact]
        public void Materialize_WhenBodyUsesBareCrBeforeDot_StoresStuffedBytesAndIsNotFrameable()
        {
            var parser = new NntpArticleParser("bf01.usenet.ninja");
            var article = Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n"
                + "Message-ID: <bare-cr-dot@example.test>\r\n"
                + "Newsgroups: alt.test\r\n"
                + "From: user@example.test\r\n"
                + "\r\n"
                + "hello\r.secret\n");

            var created = ArticleRecordFactory.TryCreate(parser, article);

            Assert.True(created.IsAccepted, created.ParseFailure.ToString());
            Assert.Equal(
                Encoding.ASCII.GetBytes("hello\r..secret\n"),
                created.Record.ArtData.Span[created.Record.Framing.BodyStart..].ToArray());
            Assert.False(created.Record.Framing.IsFrameable);
            Assert.False(CanonicalArticleFraming.Analyze(created.Record.ArtData.Span).IsFrameable);
        }

        /// <summary>
        /// A CRLF body with a dot-leading line is stuffed and remains frameable.
        /// </summary>
        [Fact]
        public void Materialize_WhenBodyUsesCrlfBeforeDot_StoresStuffedBytesAndIsFrameable()
        {
            var parser = new NntpArticleParser("bf01.usenet.ninja");
            var article = Encoding.ASCII.GetBytes(
                "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n"
                + "Message-ID: <crlf-dot@example.test>\r\n"
                + "Newsgroups: alt.test\r\n"
                + "From: user@example.test\r\n"
                + "\r\n"
                + "hello\r\n.secret\r\n");

            var created = ArticleRecordFactory.TryCreate(parser, article);

            Assert.True(created.IsAccepted, created.ParseFailure.ToString());
            Assert.Equal(
                Encoding.ASCII.GetBytes("hello\r\n..secret\r\n"),
                created.Record.ArtData.Span[created.Record.Framing.BodyStart..].ToArray());
            Assert.True(created.Record.Framing.IsFrameable);
        }

        /// <summary>Asserts stuffing of a body fragment after a fixed CRLF header separator.</summary>
        /// <param name="body">Logical body.</param>
        /// <param name="stuffed">Expected stuffed body.</param>
        private static void AssertStuffedBody(string body, string stuffed)
        {
            var logical = Encoding.ASCII.GetBytes("From: a@b.test\r\n\r\n" + body);
            var wire = ArticleBodyWireStuffer.StuffBody(logical);
            Assert.Equal(Encoding.ASCII.GetBytes("From: a@b.test\r\n\r\n" + stuffed), wire);
        }
    }
}

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

        /// <summary>Parameterized body fragments keep the one-dot-per-line-start rule across CRLF, LF, and mixed endings.</summary>
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
        public void Logical_body_lines_keep_one_stuffed_dot_at_the_line_start(string body, string stuffed)
        {
            var logical = Encoding.ASCII.GetBytes("From: a@b.test\r\n\r\n" + body);
            var wire = ArticleBodyWireStuffer.StuffBody(logical);

            Assert.Equal(Encoding.ASCII.GetBytes("From: a@b.test\r\n\r\n" + stuffed), wire);
        }

        /// <summary>LF header separators and mixed line endings affect only the body region after the first blank line.</summary>
        [Fact]
        public void Lf_separator_and_mixed_endings_stuff_only_the_body()
        {
            var logical = Encoding.ASCII.GetBytes("From: a@b.test\n\n.\n..x\r\n");
            var wire = ArticleBodyWireStuffer.StuffBody(logical);

            Assert.Equal(Encoding.ASCII.GetBytes("From: a@b.test\n\n..\n...x\r\n"), wire);
        }
    }
}

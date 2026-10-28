namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    public sealed class NntpWireFramerTests
    {
        [Fact]
        public void Frame_appends_terminator_and_stuffs_leading_dot_lines()
        {
            var logical = "Message-ID: <a@b>\r\n\r\n.leading\r\nbody\r\n"u8.ToArray();
            var framed = NntpWireFramer.FrameLogicalArticle(logical);
            Assert.True(framed.AsSpan().EndsWith(".\r\n"u8));
            var framedText = System.Text.Encoding.ASCII.GetString(framed);
            // Leading '.' on a content line is stuffed as '..' on the same line (RFC 3977).
            Assert.Contains("..leading\r\n", framedText, StringComparison.Ordinal);
            Assert.False(logical.AsSpan().EndsWith(".\r\n"u8));
        }

        [Fact]
        public void Frame_does_not_treat_already_framed_input_as_logical()
        {
            var logical = "Message-ID: <a@b>\r\n\r\nx\r\n"u8.ToArray();
            var framed = NntpWireFramer.FrameLogicalArticle(logical);
            var again = NntpWireFramer.FrameLogicalArticle(framed);
            // Framing a wire body again stuffs the terminator line — peers must frame logical files once.
            Assert.NotEqual(framed, again);
            Assert.Contains("..\r\n", System.Text.Encoding.ASCII.GetString(again), StringComparison.Ordinal);
        }

        [Fact]
        public void Empty_logical_article_still_emits_terminator()
        {
            var framed = NntpWireFramer.FrameLogicalArticle([]);
            Assert.Equal(".\r\n"u8.ToArray(), framed);
        }

        /// <summary>
        /// The lab peer preserves logical EOLs (stuffing only). A bare LF in the corpus remains on the wire,
        /// so callers must supply exclusive-CRLF logical articles rather than relying on framing to normalize.
        /// </summary>
        [Fact]
        public void Frame_preserves_bare_lf_in_logical_article()
        {
            var logical = "Message-ID: <a@b>\r\nPath: x\n\r\nbody\r\n"u8.ToArray();
            var framed = NntpWireFramer.FrameLogicalArticle(logical);
            Assert.Contains((byte)'\n', logical);
            Assert.True(framed.AsSpan().IndexOf("Path: x\n\r\n"u8) >= 0);
            Assert.True(framed.AsSpan().EndsWith(".\r\n"u8));
        }

    }
}

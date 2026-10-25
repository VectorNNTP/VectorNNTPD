using System.Net.Sockets;
using VectorNNTPD.BackFillerService.Nntp;

namespace VectorNNTPD.BackFillerService.Tests.Nntp
{
    /// <summary>
    /// Unit coverage for <see cref="NntpExceptionDiagnostics.FormatReason"/> bounds, inners, and fallbacks.
    /// </summary>
    public sealed class NntpExceptionDiagnosticsTests
    {
        /// <summary>
        /// Non-empty messages become <c>{Type}: {message}</c> with no inner.
        /// </summary>
        [Fact]
        public void FormatReason_includes_type_and_trimmed_message()
        {
            var reason = NntpExceptionDiagnostics.FormatReason(new IOException("  connection reset  "));
            Assert.Equal("IOException: connection reset", reason);
        }

        /// <summary>
        /// Blank messages fall back to the type name alone when no useful inner is present.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void FormatReason_blank_message_returns_type_name(string message)
        {
            Assert.Equal(nameof(BlankMessageException), NntpExceptionDiagnostics.FormatReason(new BlankMessageException(message)));
        }

        /// <summary>
        /// A useful inner is appended when the outer message does not already contain it.
        /// </summary>
        [Fact]
        public void FormatReason_appends_useful_inner_exception()
        {
            var inner = new SocketException((int)SocketError.TimedOut);
            var reason = NntpExceptionDiagnostics.FormatReason(new IOException("transport failed", inner));
            Assert.Equal("IOException: transport failed <- " + nameof(SocketException) + ": " + inner.Message, reason);
        }

        /// <summary>
        /// When the outer message already embeds the inner text (typical transport wrappers), the inner is not repeated.
        /// </summary>
        [Fact]
        public void FormatReason_skips_inner_when_message_already_embedded()
        {
            var inner = new SocketException((int)SocketError.ConnectionReset);
            var outer = new IOException("Unable to read data from the transport connection: " + inner.Message, inner);
            var reason = NntpExceptionDiagnostics.FormatReason(outer);
            Assert.Equal("IOException: Unable to read data from the transport connection: " + inner.Message, reason);
            Assert.DoesNotContain(" <- ", reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// An empty outer message still surfaces a useful inner type and message.
        /// </summary>
        [Fact]
        public void FormatReason_blank_outer_preserves_useful_inner()
        {
            var inner = new SocketException((int)SocketError.ConnectionRefused);
            var reason = NntpExceptionDiagnostics.FormatReason(new IOException(string.Empty, inner));
            Assert.Equal("IOException <- " + nameof(SocketException) + ": " + inner.Message, reason);
        }

        /// <summary>
        /// Chains that exceed the 256-character budget are truncated with a trailing marker.
        /// </summary>
        [Fact]
        public void FormatReason_truncates_long_chain_at_256_characters_with_marker()
        {
            var inner = new InvalidOperationException(new string('y', 200));
            var outer = new IOException(new string('x', 200), inner);
            var reason = NntpExceptionDiagnostics.FormatReason(outer);
            Assert.Equal(256, reason.Length);
            Assert.EndsWith("...", reason, StringComparison.Ordinal);
            Assert.StartsWith("IOException: ", reason, StringComparison.Ordinal);
            Assert.Contains(" <- ", reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// A long outer-only message is truncated to 256 characters with a marker.
        /// </summary>
        [Fact]
        public void FormatReason_truncates_long_messages_at_256_characters()
        {
            var longMessage = new string('x', 400);
            var reason = NntpExceptionDiagnostics.FormatReason(new IOException(longMessage));
            Assert.Equal(256, reason.Length);
            Assert.StartsWith("IOException: ", reason, StringComparison.Ordinal);
            Assert.EndsWith("...", reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// Deep chains stop after three inners so formatting stays bounded.
        /// </summary>
        [Fact]
        public void FormatReason_limits_inner_depth()
        {
            Exception chain = new InvalidOperationException("level-5");
            chain = new InvalidOperationException("level-4", chain);
            chain = new InvalidOperationException("level-3", chain);
            chain = new InvalidOperationException("level-2", chain);
            chain = new InvalidOperationException("level-1", chain);

            var reason = NntpExceptionDiagnostics.FormatReason(chain);
            Assert.Equal(
                "InvalidOperationException: level-1 <- InvalidOperationException: level-2 <- InvalidOperationException: level-3 <- InvalidOperationException: level-4",
                reason);
            Assert.DoesNotContain("level-5", reason, StringComparison.Ordinal);
        }

        private sealed class BlankMessageException : Exception
        {
            public BlankMessageException(string message)
                : base(message)
            {
            }
        }
    }
}

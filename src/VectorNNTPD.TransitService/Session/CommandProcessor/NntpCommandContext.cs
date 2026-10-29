using VectorNNTPD.TransitService.Networking.Transport;
using VectorNNTPD.TransitService.Session.Framing;

namespace VectorNNTPD.TransitService.Session.CommandProcessor
{
    /// <summary>Per-command execution context supplied to handlers after syntactic validation.</summary>
    public sealed class NntpCommandContext
    {
        /// <summary>Initializes a new instance of the <see cref="NntpCommandContext"/> class.</summary>
        public NntpCommandContext(
            NntpSession session,
            NntpCommand command,
            ReadOnlyMemory<byte> line,
            NntpResponseWriter response,
            NntpMultilineReadResult? preReadArticle = null)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(response);
            if (!command.IsValid)
            {
                throw new ArgumentException("Handlers receive only syntactically valid commands.", nameof(command));
            }

            Session = session;
            Command = command;
            Line = line;
            Response = response;
            PreReadArticle = preReadArticle;
        }

        /// <summary>Gets the owning session.</summary>
        public NntpSession Session { get; }

        /// <summary>Gets the validated command (indexes into <see cref="Line"/>).</summary>
        public NntpCommand Command { get; }

        /// <summary>
        /// Gets the current command-line buffer. Valid until the next command is parsed.
        /// Pipelined CHECK copies the Message-ID before the next parse; do not retain this
        /// memory across a later command.
        /// </summary>
        public ReadOnlyMemory<byte> Line { get; }

        /// <summary>Gets the response writer for this command.</summary>
        public NntpResponseWriter Response { get; }

        /// <summary>
        /// Semantic first/status line for TX Debug logging. Set only when Debug is enabled;
        /// first assignment wins. Never derived from wire bytes.
        /// </summary>
        internal string? StatusLine { get; set; }

        /// <summary>
        /// Gets an article already consumed by the continuous RX scanner, when the session
        /// pre-read a TAKETHIS body so the handler must not read the Pipe again.
        /// </summary>
        public NntpMultilineReadResult? PreReadArticle { get; }

        /// <summary>
        /// Optional TX completion detail set by the handler after successful work
        /// (for example negotiated TLS parameters after STARTTLS).
        /// </summary>
        public string? CompletionDetail { get; set; }

        /// <summary>Gets the underlying transport connection.</summary>
        public INntpConnection Connection => Session.Connection;

        /// <summary>Gets the argument bytes from the current command buffer.</summary>
        public ReadOnlySpan<byte> ArgumentSpan => Command.ArgumentSpan(Line.Span);

        /// <summary>
        /// Gets the argument bytes as memory. Safe across <c>await</c> only while this command's
        /// parser scratch remains the current line; CHECK pipeline slots copy the Message-ID first.
        /// </summary>
        public ReadOnlyMemory<byte> ArgumentMemory => Command.ArgumentMemory(Line);
    }
}

namespace VectorNNTPD.ReaderService.Session.CommandProcessor
{
    /// <summary>
    /// Immutable parse result: enums and indexes only. Safe to retain across <c>await</c>.
    /// Argument bytes live in the current command buffer, not in this struct.
    /// </summary>
    public readonly struct NntpCommand
    {
        /// <summary>Initializes a new instance of the <see cref="NntpCommand"/> struct.</summary>
        public NntpCommand(
            NntpVerb verb,
            NntpVerb qualifier,
            int argumentStart,
            int argumentLength,
            int tokenCount,
            NntpParseStatus status)
        {
            Verb = verb;
            Qualifier = qualifier;
            ArgumentStart = argumentStart;
            ArgumentLength = argumentLength;
            TokenCount = tokenCount;
            Status = status;
        }

        /// <summary>Gets the classified verb.</summary>
        public NntpVerb Verb { get; }

        /// <summary>Gets the qualifier (MODE/AUTHINFO/LIST), or <see cref="NntpVerb.None"/>.</summary>
        public NntpVerb Qualifier { get; }

        /// <summary>Gets the argument start index in the current command buffer.</summary>
        public int ArgumentStart { get; }

        /// <summary>Gets the argument length in the current command buffer.</summary>
        public int ArgumentLength { get; }

        /// <summary>Gets the number of tokens in the argument region.</summary>
        public int TokenCount { get; }

        /// <summary>Gets the syntactic status. Only <see cref="NntpParseStatus.Ok"/> may reach a handler.</summary>
        public NntpParseStatus Status { get; }

        /// <summary>Gets whether the command may be dispatched to a normal handler.</summary>
        public bool IsValid => Status == NntpParseStatus.Ok;

        /// <summary>
        /// A command line that exceeded <see cref="NntpCommandLineReader.MaxCommandLineBytes"/>.
        /// Not a parsed prefix. Handlers must not run.
        /// </summary>
        public static NntpCommand OverlongLine { get; } = new(
            NntpVerb.None,
            NntpVerb.None,
            argumentStart: 0,
            argumentLength: 0,
            tokenCount: 0,
            NntpParseStatus.CommandLineTooLong);

        /// <summary>Returns the argument bytes from a still-valid command buffer.</summary>
        public ReadOnlySpan<byte> ArgumentSpan(ReadOnlySpan<byte> line)
        {
            if (ArgumentLength <= 0
                || ArgumentStart < 0
                || ArgumentStart + ArgumentLength > line.Length)
            {
                return ReadOnlySpan<byte>.Empty;
            }

            return line.Slice(ArgumentStart, ArgumentLength);
        }

        /// <summary>Returns the argument bytes from a still-valid command buffer.</summary>
        public ReadOnlyMemory<byte> ArgumentMemory(ReadOnlyMemory<byte> line)
        {
            if (ArgumentLength <= 0
                || ArgumentStart < 0
                || ArgumentStart + ArgumentLength > line.Length)
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            return line.Slice(ArgumentStart, ArgumentLength);
        }
    }
}

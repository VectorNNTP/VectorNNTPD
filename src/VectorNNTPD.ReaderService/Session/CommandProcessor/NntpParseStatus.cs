namespace VectorNNTPD.ReaderService.Session.CommandProcessor
{
    /// <summary>Syntactic outcome of <see cref="NntpCommandParser.Parse(ReadOnlySpan{byte})"/>.</summary>
    public enum NntpParseStatus : byte
    {
        /// <summary>Command is syntactically valid and may be dispatched to a handler.</summary>
        Ok = 0,

        /// <summary>Empty or whitespace-only line.</summary>
        Empty,

        /// <summary>Verb is not a supported command.</summary>
        UnknownVerb,

        /// <summary>Verb is known but the qualifier/variant is missing or unsupported.</summary>
        UnknownQualifier,

        /// <summary>A required argument is missing.</summary>
        MissingArgument,

        /// <summary>An argument is present but syntactically invalid.</summary>
        InvalidArgument,

        /// <summary>The command has more tokens than its grammar allows.</summary>
        ExtraArgument,

        /// <summary>
        /// The CRLF-terminated line is longer than <see cref="NntpCommandLineReader.MaxCommandLineBytes"/>.
        /// The prefix is not parsed.
        /// </summary>
        CommandLineTooLong,
    }
}

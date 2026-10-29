namespace VectorNNTPD.TransitService.Email.Smtp
{
    /// <summary>A complete SMTP reply (RFC 5321), including continuation lines.</summary>
    public sealed class SmtpResponse
    {
        /// <summary>Initializes a parsed SMTP reply.</summary>
        public SmtpResponse(int code, IReadOnlyList<string> lines, string? enhancedStatus, string text)
        {
            Code = code;
            Lines = lines;
            EnhancedStatus = enhancedStatus;
            Text = text;
        }

        /// <summary>Gets the three-digit status code.</summary>
        public int Code { get; }

        /// <summary>Gets the optional enhanced status code (RFC 2034) from the last line.</summary>
        public string? EnhancedStatus { get; }

        /// <summary>Gets concatenated reply text without status prefixes.</summary>
        public string Text { get; }

        /// <summary>Gets raw text portions of each reply line.</summary>
        public IReadOnlyList<string> Lines { get; }

        /// <summary>Gets whether the code is 2xx.</summary>
        public bool IsPositiveCompletion => Code is >= 200 and <= 299;

        /// <summary>Gets whether the code is 3xx.</summary>
        public bool IsPositiveIntermediate => Code is >= 300 and <= 399;

        /// <summary>Gets whether the code is 4xx.</summary>
        public bool IsTransientNegative => Code is >= 400 and <= 499;

        /// <summary>Gets whether the code is 5xx.</summary>
        public bool IsPermanentNegative => Code is >= 500 and <= 599;

        /// <inheritdoc />
        public override string ToString() => Code + " " + Text;
    }
}

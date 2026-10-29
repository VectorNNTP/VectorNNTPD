namespace VectorNNTPD.ReaderService.Email.Smtp
{
    /// <summary>SMTP transport failure with a classified <see cref="Kind"/>.</summary>
    public sealed class SmtpException : Exception
    {
        /// <summary>Initializes a new SMTP failure.</summary>
        public SmtpException(SmtpFailureKind kind, string message, Exception? inner = null)
            : base(message, inner)
        {
            Kind = kind;
        }

        /// <summary>Gets the failure classification.</summary>
        public SmtpFailureKind Kind { get; }

        /// <summary>Gets the last SMTP status code when one was received; otherwise 0.</summary>
        public int StatusCode { get; init; }

        /// <summary>Gets whether retry is appropriate.</summary>
        public bool IsTransient => Kind is SmtpFailureKind.Transient or SmtpFailureKind.Network;
    }
}

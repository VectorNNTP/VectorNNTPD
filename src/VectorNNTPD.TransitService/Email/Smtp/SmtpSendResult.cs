namespace VectorNNTPD.TransitService.Email.Smtp
{
    /// <summary>Outcome of one SMTP transaction (MAIL/RCPT/DATA).</summary>
    public sealed class SmtpSendResult
    {
        /// <summary>Gets whether every recipient was accepted and DATA completed with 2xx.</summary>
        public required bool Succeeded { get; init; }

        /// <summary>Gets per-recipient RCPT results.</summary>
        public required IReadOnlyList<SmtpRecipientResult> Recipients { get; init; }

        /// <summary>Gets the DATA status code when DATA was issued; otherwise 0.</summary>
        public int DataStatusCode { get; init; }
    }
}

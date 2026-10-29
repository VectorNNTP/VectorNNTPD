namespace VectorNNTPD.ReaderService.Email.Smtp
{
    /// <summary>Per-recipient SMTP RCPT outcome.</summary>
    /// <param name="Address">Envelope recipient.</param>
    /// <param name="Accepted">Whether the server returned a 2xx RCPT reply.</param>
    /// <param name="StatusCode">SMTP status code.</param>
    /// <param name="Text">Reply text (never a credential).</param>
    public readonly record struct SmtpRecipientResult(
        EmailAddress Address,
        bool Accepted,
        int StatusCode,
        string Text);
}

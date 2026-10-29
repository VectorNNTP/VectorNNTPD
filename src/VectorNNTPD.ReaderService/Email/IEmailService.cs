namespace VectorNNTPD.ReaderService.Email
{
    /// <summary>
    /// Application-facing outbound email API. Producers spool work; they do not
    /// perform SMTP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SendAsync"/> means the complete RFC 5322/MIME message (plus SMTP
    /// envelope) has been durably accepted into the local filesystem spool
    /// (<c>spool/smtp</c> by default). It does not mean a remote SMTP server accepted
    /// the message.
    /// </para>
    /// <para>
    /// SMTP delivery is asynchronous. Successful remote acceptance deletes the spool
    /// file. Undelivered files survive process restart. SMTP acceptance followed by
    /// filesystem deletion is at-least-once and cannot be exactly-once.
    /// </para>
    /// <para>
    /// Callers must not depend on <c>TcpClient</c>, <c>SslStream</c>, SMTP commands,
    /// STARTTLS, authentication, retries, spool paths, or connection reuse.
    /// </para>
    /// </remarks>
    public interface IEmailService
    {
        /// <summary>
        /// Validates, encodes, and durably writes <paramref name="message"/> to the outbound spool.
        /// </summary>
        /// <param name="message">Protocol-independent message.</param>
        /// <param name="cancellationToken">Cancels the spool write.</param>
        /// <returns>Durable local-spool acceptance, not remote SMTP delivery.</returns>
        ValueTask<EmailEnqueueResult> SendAsync(
            EmailMessage message,
            CancellationToken cancellationToken = default);
    }
}

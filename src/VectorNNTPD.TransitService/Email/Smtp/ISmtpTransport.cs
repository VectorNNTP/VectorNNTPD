namespace VectorNNTPD.TransitService.Email.Smtp
{
    /// <summary>SMTP delivery transport. Implementations own TCP/TLS/AUTH.</summary>
    public interface ISmtpTransport
    {
        /// <summary>Delivers one encoded message on a new or reset SMTP session.</summary>
        Task<SmtpSendResult> SendAsync(EmailWorkItem item, CancellationToken cancellationToken);
    }
}

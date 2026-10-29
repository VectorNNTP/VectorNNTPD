namespace VectorNNTPD.ReaderService.Email
{
    /// <summary>Encodes an <see cref="EmailMessage"/> to RFC 5322 / MIME wire bytes.</summary>
    /// <remarks>
    /// Separate from SMTP transport. Output uses CRLF. SMTP DATA dot-stuffing is
    /// applied later by the SMTP client, not here.
    /// </remarks>
    public interface IEmailMessageEncoder
    {
        /// <summary>Validates and encodes <paramref name="message"/>.</summary>
        ReadOnlyMemory<byte> Encode(EmailMessage message);
    }
}

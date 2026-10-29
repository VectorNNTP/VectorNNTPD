namespace VectorNNTPD.TransitService.Email
{
    /// <summary>
    /// Protocol-independent outbound email. SMTP envelope fields are distinct from headers.
    /// </summary>
    /// <remarks>
    /// Producers construct this model. The email subsystem owns MIME encoding and SMTP.
    /// Credentials must never be placed in headers or the body by this type.
    /// </remarks>
    public sealed class EmailMessage
    {
        /// <summary>Gets the RFC 5322 From mailbox.</summary>
        public required EmailAddress From { get; init; }

        /// <summary>Gets the visible To recipients.</summary>
        public required IReadOnlyList<EmailAddress> To { get; init; }

        /// <summary>Gets the Cc recipients.</summary>
        public IReadOnlyList<EmailAddress> Cc { get; init; } = [];

        /// <summary>Gets the Bcc recipients (envelope only; omitted from headers).</summary>
        public IReadOnlyList<EmailAddress> Bcc { get; init; } = [];

        /// <summary>Gets the Reply-To mailboxes.</summary>
        public IReadOnlyList<EmailAddress> ReplyTo { get; init; } = [];

        /// <summary>
        /// Gets the SMTP envelope sender. When <see langword="null"/>, configuration
        /// <c>Email:EnvelopeSender</c> / <c>Email:DefaultFrom</c> is used.
        /// </summary>
        public EmailAddress? EnvelopeSender { get; init; }

        /// <summary>Gets the subject (no CR/LF; encoded by the message encoder when needed).</summary>
        public required string Subject { get; init; }

        /// <summary>Gets the body octets (interpreted using <see cref="Charset"/>).</summary>
        public required ReadOnlyMemory<byte> Body { get; init; }

        /// <summary>Gets the body media type without parameters. Default <c>text/plain</c>.</summary>
        public string ContentType { get; init; } = "text/plain";

        /// <summary>Gets the body charset. Default <c>utf-8</c>.</summary>
        public string Charset { get; init; } = "utf-8";

        /// <summary>Gets extra header fields (not From/To/Cc/Subject/MIME).</summary>
        public IReadOnlyList<EmailHeader> Headers { get; init; } = [];

        /// <summary>Gets attachments. Non-empty messages become <c>multipart/mixed</c>.</summary>
        public IReadOnlyList<EmailAttachment> Attachments { get; init; } = [];

        /// <summary>Gets an optional correlation id for structured logs (not a secret).</summary>
        public string? CorrelationId { get; init; }

        /// <summary>Enumerates distinct envelope recipients (To, Cc, Bcc).</summary>
        public IReadOnlyList<EmailAddress> EnvelopeRecipients()
        {
            var list = new List<EmailAddress>(To.Count + Cc.Count + Bcc.Count);
            AddDistinct(list, To);
            AddDistinct(list, Cc);
            AddDistinct(list, Bcc);
            return list;
        }

        private static void AddDistinct(List<EmailAddress> target, IReadOnlyList<EmailAddress> source)
        {
            foreach (var address in source)
            {
                var found = false;
                for (var i = 0; i < target.Count; i++)
                {
                    if (string.Equals(target[i].Address, address.Address, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    target.Add(address);
                }
            }
        }
    }
}

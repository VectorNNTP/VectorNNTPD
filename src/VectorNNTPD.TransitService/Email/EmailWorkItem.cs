namespace VectorNNTPD.TransitService.Email
{
    /// <summary>One outbound message after MIME encoding (in memory or loaded from spool).</summary>
    public sealed class EmailWorkItem
    {
        /// <summary>Gets the original message when still in the producer process; otherwise <see langword="null"/>.</summary>
        public EmailMessage? Message { get; init; }

        /// <summary>Gets the RFC 5322/MIME wire representation (CRLF, not SMTP-dot-stuffed).</summary>
        public required ReadOnlyMemory<byte> EncodedMessage { get; init; }

        /// <summary>Gets the SMTP envelope sender.</summary>
        public required EmailAddress EnvelopeSender { get; init; }

        /// <summary>Gets the SMTP envelope recipients.</summary>
        public required IReadOnlyList<EmailAddress> Recipients { get; init; }

        /// <summary>Gets an optional correlation id for logs (not a secret).</summary>
        public string? CorrelationId { get; init; }

        /// <summary>Gets the 1-based in-process delivery attempt count. Not persisted on disk.</summary>
        public int Attempt { get; set; } = 1;
    }
}

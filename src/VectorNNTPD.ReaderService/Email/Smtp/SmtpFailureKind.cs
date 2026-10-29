namespace VectorNNTPD.ReaderService.Email.Smtp
{
    /// <summary>Classifies an SMTP delivery failure for retry policy.</summary>
    public enum SmtpFailureKind
    {
        /// <summary>Transient SMTP 4xx or temporary network/DNS/TLS issue. May retry.</summary>
        Transient = 0,

        /// <summary>Permanent SMTP 5xx recipient or mailbox rejection. Do not retry.</summary>
        Permanent = 1,

        /// <summary>SMTP protocol violation or malformed response. Do not retry indefinitely.</summary>
        Protocol = 2,

        /// <summary>TCP/DNS connect or disconnect failure. May retry when transient.</summary>
        Network = 3,

        /// <summary>TLS handshake or certificate validation failure.</summary>
        Tls = 4,

        /// <summary>SMTP AUTH failed or is unavailable. Do not retry.</summary>
        Authentication = 5,

        /// <summary>The operation was cancelled.</summary>
        Cancelled = 6,

        /// <summary>Message is malformed or exceeds the server SIZE limit. Do not retry.</summary>
        Message = 7,

        /// <summary>Some recipients were accepted and others rejected. Do not retry (would duplicate).</summary>
        Partial = 8,
    }
}

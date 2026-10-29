namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>
    /// SMTP transport security mode. Explicit configuration wins over port defaults.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These modes are mutually exclusive. The client never infers a mode from port
    /// number alone and never silently downgrades from a required TLS mode.
    /// </para>
    /// <list type="bullet">
    /// <item><see cref="None"/> — plaintext TCP. Legitimate for a controlled local relay.</item>
    /// <item><see cref="StartTls"/> — RFC 3207 STARTTLS after the SMTP greeting and first EHLO.</item>
    /// <item><see cref="ImplicitTls"/> — TLS handshake immediately after TCP connect (typically port 465).</item>
    /// </list>
    /// Port defaults (25 / 587 / 465) are documentation only; <see cref="SmtpOptions.Security"/> is authoritative.
    /// </remarks>
    public enum SmtpSecurityMode
    {
        /// <summary>Plaintext SMTP (no TLS).</summary>
        None = 0,

        /// <summary>STARTTLS after EHLO (RFC 3207). Typical message-submission port 587.</summary>
        StartTls = 1,

        /// <summary>Implicit TLS immediately after TCP connect (RFC 8314). Typical port 465.</summary>
        ImplicitTls = 2,
    }
}

namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>SMTP transport settings nested under <see cref="EmailOptions"/>.</summary>
    /// <remarks>
    /// Distinguishes SMTP relay (often port 25, possibly plaintext on a private network)
    /// from message submission (RFC 6409, typically 587 STARTTLS or 465 implicit TLS).
    /// This client speaks SMTP; it does not assume MSA-only rules when the destination
    /// is a local relay.
    /// </remarks>
    public sealed class SmtpOptions
    {
        /// <summary>Default submission port used when configuration omits <see cref="Port"/>.</summary>
        public const int DefaultPort = 587;

        /// <summary>Gets or sets the SMTP server hostname or IP. Required when email is enabled.</summary>
        public string Host { get; set; } = string.Empty;

        /// <summary>Gets or sets the SMTP TCP port (<c>1–65535</c>). Default <see cref="DefaultPort"/>.</summary>
        public int Port { get; set; } = DefaultPort;

        /// <summary>Gets or sets the explicit TLS mode. Never inferred from <see cref="Port"/>.</summary>
        public SmtpSecurityMode Security { get; set; } = SmtpSecurityMode.StartTls;

        /// <summary>
        /// Gets or sets the SMTP AUTH username. Empty means no authentication.
        /// Override with <c>Email__Smtp__Username</c>; never commit real values.
        /// </summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the SMTP AUTH password. Override with <c>Email__Smtp__Password</c>
        /// or a secret store. Never logged.
        /// </summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether AUTH is refused unless the session is already TLS-protected.
        /// Default <see langword="true"/>. Plaintext AUTH requires an explicit <see langword="false"/>.
        /// </summary>
        public bool RequireTlsForAuthentication { get; set; } = true;

        /// <summary>Gets or sets the TCP connect timeout. Default 15 seconds.</summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>Gets or sets the SMTP command/read/write timeout. Default 30 seconds.</summary>
        public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Gets or sets the maximum delivery attempts including the first. Default 3.</summary>
        public int MaxAttempts { get; set; } = 3;

        /// <summary>Gets or sets the first retry delay. Default 2 seconds.</summary>
        public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>Gets or sets the retry delay ceiling. Default 60 seconds.</summary>
        public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromSeconds(60);
    }
}

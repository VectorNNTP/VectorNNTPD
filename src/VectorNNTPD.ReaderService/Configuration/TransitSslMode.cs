namespace VectorNNTPD.ReaderService.Configuration
{
    /// <summary>
    /// Canonical TLS mode for a configured Transit peer.
    /// </summary>
    /// <remarks>
    /// Configuration accepts these values case-insensitively. Invalid values fail validation;
    /// they are never silently treated as plaintext.
    /// </remarks>
    public enum TransitSslMode
    {
        /// <summary>No TLS (blank configuration value).</summary>
        None = 0,

        /// <summary>Native TLS on connect (<c>TLS</c>).</summary>
        Tls = 1,

        /// <summary>Cleartext TCP upgraded with STARTTLS (<c>STARTTLS</c>).</summary>
        StartTls = 2,
    }
}

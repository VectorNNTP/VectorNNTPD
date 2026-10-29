namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>Parses Transit <c>Ssl</c> configuration values into <see cref="TransitSslMode"/>.</summary>
    public static class TransitSslParser
    {
        /// <summary>
        /// Parses a configuration value case-insensitively and returns the canonical mode.
        /// </summary>
        public static bool TryParse(string? value, out TransitSslMode mode, out string? error)
        {
            mode = TransitSslMode.None;
            error = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            var trimmed = value.Trim();
            if (trimmed.Equals("TLS", StringComparison.OrdinalIgnoreCase))
            {
                mode = TransitSslMode.Tls;
                return true;
            }

            if (trimmed.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase))
            {
                mode = TransitSslMode.StartTls;
                return true;
            }

            error = "Ssl must be blank, 'TLS', or 'STARTTLS'.";
            return false;
        }
    }
}

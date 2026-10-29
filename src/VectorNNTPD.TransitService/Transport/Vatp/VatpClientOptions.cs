namespace VectorNNTPD.TransitService.Transport.Vatp
{
    /// <summary>Internal VATP client resource and timeout defaults.</summary>
    internal sealed class VatpClientOptions
    {
        /// <summary>Maximum TLS connections per cache endpoint.</summary>
        public int MaxConnectionsPerEndpoint { get; init; } = 4;

        /// <summary>TCP connect timeout.</summary>
        public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

        /// <summary>TLS handshake timeout.</summary>
        public TimeSpan TlsHandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Per-read I/O timeout while a connection is active.
        /// It is not armed while a STORE on the connection is still waiting for its first WINDOW.
        /// </summary>
        public TimeSpan IoTimeout { get; init; } = TimeSpan.FromMinutes(5);
    }
}

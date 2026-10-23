namespace VectorNNTPD.StorageService.Tests.Support.Placement
{
    /// <summary>Internal NNTP feed client resource and timeout defaults.</summary>
    internal sealed class StorageFeedClientOptions
    {
        /// <summary>Maximum TLS connections per cache endpoint.</summary>
        public int MaxConnectionsPerEndpoint { get; init; } = 4;

        /// <summary>TCP connect timeout.</summary>
        public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

        /// <summary>TLS handshake timeout.</summary>
        public TimeSpan TlsHandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

        /// <summary>Per-read I/O timeout while a connection is active.</summary>
        public TimeSpan IoTimeout { get; init; } = TimeSpan.FromMinutes(5);
    }
}

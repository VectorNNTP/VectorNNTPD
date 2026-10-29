namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>
    /// Top-level Redis connection options. Hosts are StackExchange.Redis endpoints/seeds
    /// for one shared topology, not independently round-robined servers.
    /// </summary>
    public sealed class RedisOptions
    {
        /// <summary>Configuration section name.</summary>
        public const string SectionName = "Redis";

        /// <summary>Default Redis TCP port.</summary>
        public const int DefaultPort = 6379;

        /// <summary>Gets or sets Redis hostnames or IP addresses used as multiplexer endpoints.</summary>
        public string[] Host { get; set; } = [];

        /// <summary>Gets or sets the Redis TCP port applied to every configured host.</summary>
        public int Port { get; set; } = DefaultPort;
    }
}

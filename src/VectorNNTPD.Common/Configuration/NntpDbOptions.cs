namespace VectorNNTPD.Common.Configuration
{
    /// <summary>
    /// Shared NntpDB options. Physical connection pooling is owned by MySqlConnector.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The application-options section name is <see cref="SectionName"/> (<c>NntpDb</c>).
    /// The connection string is <c>ConnectionStrings:NntpDB</c> (<see cref="ConnectionStringName"/>).
    /// Credentials are supplied through the Generic Host environment mapping
    /// <see cref="ConnectionStringEnvironmentVariable"/>, not production appsettings.
    /// Provider pooling, pool size, and idle reaping belong in that connection string.
    /// </para>
    /// <para>
    /// NNTPD, BackFiller, and StorageServer bind this type through the same Common registration and
    /// therefore resolve the same configuration key and the same database.
    /// </para>
    /// <para>
    /// Do not log <see cref="ConnectionString"/>.
    /// </para>
    /// </remarks>
    public sealed class NntpDbOptions
    {
        /// <summary>Top-level NntpDB application-options section name.</summary>
        internal const string SectionName = "NntpDb";

        /// <summary>Connection-string name under <c>ConnectionStrings</c>.</summary>
        internal const string ConnectionStringName = "NntpDB";

        /// <summary>
        /// Existing Generic Host environment variable that supplies
        /// <c>ConnectionStrings:NntpDB</c>.
        /// </summary>
        internal const string ConnectionStringEnvironmentVariable = "ConnectionStrings__NntpDB";

        /// <summary>Default wall-clock budget for the NNTPD startup connectivity check.</summary>
        private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Gets or sets the MySQL connection string copied from
        /// <c>ConnectionStrings:NntpDB</c> when this property is empty.
        /// </summary>
        /// <remarks>Secret. Never log this value.</remarks>
        public string ConnectionString { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the wall-clock budget for startup connect and <c>SELECT 1</c>.
        /// </summary>
        /// <remarks>
        /// <see cref="VectorNNTPD.Common.NntpDb.NntpDbService"/> uses this budget when the host starts it.
        /// Connectivity failures may retry until this budget elapses. A malformed
        /// connection string, authentication failures, and a failed <c>SELECT 1</c>
        /// result fail immediately.
        /// </remarks>
        public TimeSpan StartupTimeout { get; set; } = DefaultStartupTimeout;
    }
}

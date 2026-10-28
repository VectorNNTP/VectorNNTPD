namespace VectorNNTPD.BackFillerService.Configuration
{
    /// <summary>
    /// Lab-only fail-closed upstream dial allowlist under <c>BackFiller:LabUpstreamIsolation</c>.
    /// </summary>
    /// <remarks>
    /// Default <see cref="Enabled"/> is <see langword="false"/> so ordinary deployments are unchanged.
    /// When enabled, every <see cref="Nntp.INntpTransportFactory.ConnectAsync"/> must target
    /// <see cref="AllowedHost"/>:<see cref="AllowedPort"/> before a socket is opened.
    /// When <see cref="RewritePublishedDestinations"/> is also true, each refresh rewrites published
    /// provider dial fields to that allowlist endpoint and clears credentials (no MySQL row updates).
    /// </remarks>
    internal sealed class LabUpstreamIsolationOptions
    {
        /// <summary>Relative section name under <see cref="BackFillerOptions.SectionName"/>.</summary>
        internal const string SectionRelativeName = "LabUpstreamIsolation";

        /// <summary>Only accepted host when isolation is enabled (literal IPv4; no DNS).</summary>
        internal const string RequiredLiteralHost = "127.0.0.1";

        /// <summary>
        /// Gets or sets whether the lab upstream allowlist is active.
        /// Default <see langword="false"/> — production stays unguarded.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets the only permitted upstream host when <see cref="Enabled"/> is true.
        /// Must be exactly <see cref="RequiredLiteralHost"/>.
        /// </summary>
        public string AllowedHost { get; set; } = RequiredLiteralHost;

        /// <summary>
        /// Gets or sets the only permitted upstream TCP port when <see cref="Enabled"/> is true.
        /// Required (1–65535) when enabled; ignored when disabled.
        /// </summary>
        public int? AllowedPort { get; set; }

        /// <summary>
        /// Gets or sets whether providers with <c>UseTls=true</c> are rejected when enabled.
        /// Default <see langword="true"/> — the local ARTICLE peer is cleartext.
        /// </summary>
        public bool RejectTls { get; set; } = true;

        /// <summary>
        /// Gets or sets whether each account refresh rewrites published provider destinations to
        /// <see cref="AllowedHost"/>:<see cref="AllowedPort"/> with cleartext and no credentials.
        /// Default <see langword="false"/>. Requires <see cref="Enabled"/> true. Does not mutate MySQL.
        /// </summary>
        public bool RewritePublishedDestinations { get; set; }
    }
}

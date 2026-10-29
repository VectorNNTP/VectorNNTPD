namespace VectorNNTPD.ReaderService.Configuration
{
    /// <summary>
    /// Bindable configuration for one named Transit peer (dictionary value under top-level <c>Transit</c>).
    /// </summary>
    /// <remarks>
    /// The dictionary key is the protocol-safe <c>Identifier</c>, not the display name.
    /// <see cref="PeerName"/> is the required human-readable administrative name.
    /// Do not log <see cref="Password"/>.
    /// </remarks>
    public sealed class TransitPeerOptions
    {
        /// <summary>
        /// Gets or sets the human-readable administrative display name for this peer.
        /// </summary>
        /// <remarks>
        /// Required. Preserved exactly as configured (no trimming, case-folding, or hyphenation).
        /// Not a protocol argument and not derived from the dictionary-key identifier.
        /// May contain spaces and punctuation. Must be non-empty, not whitespace-only,
        /// at most <see cref="TransitPeersOptionsValidator.MaxPeerNameLength"/> characters,
        /// and must not contain control characters.
        /// </remarks>
        public string PeerName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the maximum simultaneous inbound connections associated with this peer.
        /// </summary>
        /// <remarks>
        /// Required. Valid range is <c>0–4096</c>. This is the cluster-wide inbound
        /// cap for the peer <c>Identifier</c>. <c>0</c> means closed: admit no new
        /// inbound connections. It is not unlimited. Existing connections are not
        /// dropped when the limit is lowered.
        /// </remarks>
        public int? MaxIncomingConnections { get; set; }

        /// <summary>
        /// Gets or sets the maximum simultaneous outbound connections that may later be
        /// established to this peer.
        /// </summary>
        /// <remarks>
        /// Required. Valid range is <c>0–4096</c>. Stored and validated only; this host does
        /// not open outbound peer sockets from <see cref="ConnectTo"/>.
        /// </remarks>
        public int? MaxOutgoingConnections { get; set; }

        /// <summary>
        /// Gets or sets inbound source authorization entries (DNS hostnames, IPv4/IPv6 addresses, CIDR prefixes).
        /// </summary>
        /// <remarks>
        /// Empty means this peer cannot match inbound sources (outbound-only policy).
        /// </remarks>
        public string[] AllowFrom { get; set; } = [];

        /// <summary>
        /// Gets or sets outbound endpoint strings (<c>host:port</c> or <c>[IPv6]:port</c>).
        /// </summary>
        /// <remarks>
        /// Optional. Parsed and validated only; no outbound connection is established.
        /// An explicit port is required.
        /// </remarks>
        public string[] ConnectTo { get; set; } = [];

        /// <summary>
        /// Gets or sets the optional peer-specific AUTHINFO username.
        /// </summary>
        /// <remarks>
        /// Configuration requires <see cref="Username"/> and <see cref="Password"/>
        /// both set or both blank. Peer AUTHINFO succeeds only when both are
        /// non-empty and both supplied values match.
        /// </remarks>
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the optional peer-specific AUTHINFO password.
        /// </summary>
        /// <remarks>
        /// Never log this value. Authentication requires a matching non-empty
        /// <see cref="Username"/> as well.
        /// </remarks>
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the TLS mode string: blank (none), <c>TLS</c>, or <c>STARTTLS</c>.
        /// </summary>
        public string Ssl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the newsfeeds(5)-style subscription expression.
        /// </summary>
        /// <remarks>Default is exactly <c>*</c>.</remarks>
        public string Patterns { get; set; } = "*";

        /// <summary>
        /// Gets or sets whether duplicate/in-flight article offers should later be deferred
        /// (<c>431</c>/<c>436</c>) rather than rejected (<c>438</c>/<c>435</c>).
        /// </summary>
        /// <remarks>
        /// Default is <see langword="true"/>. CHECK and IHAVE apply it when History
        /// already contains the Message-ID. TAKETHIS does not.
        /// </remarks>
        public bool DeferOnDuplicate { get; set; } = true;

        /// <summary>
        /// Gets or sets the Path-header token reserved for outbound feed loop prevention.
        /// </summary>
        /// <remarks>
        /// Preserved exactly (no case-folding, not a DNS name or IP). Empty is allowed
        /// because article Path-header matching is not implemented yet. Not consumed by
        /// routing code in this change.
        /// </remarks>
        public string PathToken { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the maximum allowed incoming article size for this peer, in bytes.
        /// </summary>
        /// <remarks>
        /// When this options object is compiled into a snapshot, this is the Receive ceiling.
        /// Inbound acceptance uses the smaller of this value and the published global <c>maxartsize</c>.
        /// Default is <see cref="DefaultMaxSize"/>. Valid range is <c>1–2147483647</c>.
        /// </remarks>
        public long MaxSize { get; set; } = DefaultMaxSize;

        /// <summary>Default <see cref="MaxSize"/> (10 MiB).</summary>
        public const long DefaultMaxSize = 10_485_760;

        /// <summary>Maximum accepted <see cref="MaxSize"/> (2 GiB − 1 byte).</summary>
        public const long MaxMaxSize = int.MaxValue;

        /// <summary>
        /// Gets or sets Diablo message-type names accepted for this peer.
        /// </summary>
        /// <remarks>
        /// Default is <c>["default"]</c>. Not regex, MIME types, or newsgroup Patterns.
        /// Article classification is not implemented.
        /// </remarks>
        public string[] MessageTypes { get; set; } = [];
    }
}

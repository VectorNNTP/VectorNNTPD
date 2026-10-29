using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.Session
{
    /// <summary>
    /// Immutable authorization snapshot for an NNTP session.
    /// </summary>
    /// <remarks>
    /// Authentication and authorization are distinct: a session may be authenticated without
    /// reader, transit, posting, streaming, or control-cancel privileges. Production default for
    /// new sessions is unauthenticated with <see cref="StreamingPermitted"/> false. A Transit
    /// AllowFrom match retains <see cref="TransitPeerName"/> (the Transit identifier) and
    /// <see cref="TransitPeerPolicy"/>.
    /// </remarks>
    public sealed class NntpAuthorization
    {
        /// <summary>Default privileges before authentication (no reader/transit/posting/streaming/control-cancel).</summary>
        public static NntpAuthorization Unauthenticated { get; } = new(
            isAuthenticated: false,
            authorizedReader: false,
            authorizedTransit: false,
            postingPermitted: false,
            streamingPermitted: false);

        /// <summary>
        /// Unauthenticated transit/streaming peer privileges (ACL match).
        /// Does not grant reader access, posting, or <see cref="IsAuthenticated"/>.
        /// Nameless; production identification uses <see cref="ForTransitPeer"/>.
        /// </summary>
        public static NntpAuthorization TrustedTransitPeer { get; } = new(
            isAuthenticated: false,
            authorizedReader: false,
            authorizedTransit: true,
            postingPermitted: false,
            streamingPermitted: true);

        /// <summary>Initializes a new instance of the <see cref="NntpAuthorization"/> class.</summary>
        public NntpAuthorization(
            bool isAuthenticated,
            bool authorizedReader,
            bool authorizedTransit,
            bool postingPermitted,
            bool streamingPermitted,
            string? transitPeerName = null,
            TransitPeerPolicy? transitPeerPolicy = null,
            bool controlCancelPermitted = false)
        {
            IsAuthenticated = isAuthenticated;
            AuthorizedReader = authorizedReader;
            AuthorizedTransit = authorizedTransit;
            PostingPermitted = postingPermitted;
            StreamingPermitted = streamingPermitted;
            TransitPeerName = transitPeerName;
            TransitPeerPolicy = transitPeerPolicy;
            ControlCancelPermitted = controlCancelPermitted;
        }

        /// <summary>
        /// Creates streaming privileges bound to a named peer policy.
        /// </summary>
        /// <remarks>
        /// <see cref="StreamingPermitted"/> is always set so <c>MODE STREAM</c> can select Transit AUTHINFO.
        /// <see cref="AuthorizedTransit"/> is set only when receive authentication is disabled
        /// (<see cref="TransitPeerPolicy.HasPeerCredentials"/> is false: username or password empty).
        /// When both are non-empty, feed commands stay unauthorized until AUTHINFO matches that pair.
        /// The peer name and policy are retained either way, including for inbound connection limits.
        /// </remarks>
        public static NntpAuthorization ForTransitPeer(TransitPeerPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(policy);
            return new(
                isAuthenticated: false,
                authorizedReader: false,
                authorizedTransit: !policy.HasPeerCredentials,
                postingPermitted: false,
                streamingPermitted: true,
                transitPeerName: policy.Identifier,
                transitPeerPolicy: policy);
        }

        /// <summary>Gets a value indicating whether the peer has completed authentication.</summary>
        public bool IsAuthenticated { get; }

        /// <summary>Gets a value indicating whether reader commands are authorized.</summary>
        public bool AuthorizedReader { get; }

        /// <summary>Gets a value indicating whether transit/streaming feed commands are authorized.</summary>
        public bool AuthorizedTransit { get; }

        /// <summary>Gets a value indicating whether <c>POST</c> is permitted (affects greeting / MODE READER).</summary>
        public bool PostingPermitted { get; }

        /// <summary>Gets a value indicating whether streaming feed mode may be entered.</summary>
        public bool StreamingPermitted { get; }

        /// <summary>
        /// Gets a value indicating whether this session may POST a well-formed
        /// <c>Control: cancel &lt;message-id&gt;</c> article.
        /// </summary>
        /// <remarks>
        /// Ordinary posting remains forbidden from supplying <c>Control</c>. This flag is granted
        /// only by an authentication provider (the configured newsmaster account). It is not a
        /// generic control-header bypass.
        /// </remarks>
        public bool ControlCancelPermitted { get; }

        /// <summary>
        /// Gets the Transit identifier when this session was identified as a named peer.
        /// </summary>
        /// <remarks>This is the dictionary-key identifier, not <see cref="TransitPeerPolicy.PeerName"/>.</remarks>
        public string? TransitPeerName { get; }

        /// <summary>Gets the identified Transit peer policy, or <see langword="null"/> when not a named peer.</summary>
        public TransitPeerPolicy? TransitPeerPolicy { get; }

        /// <summary>Returns a copy with updated flags, preserving Transit peer identity.</summary>
        public NntpAuthorization With(
            bool? isAuthenticated = null,
            bool? authorizedReader = null,
            bool? authorizedTransit = null,
            bool? postingPermitted = null,
            bool? streamingPermitted = null,
            bool? controlCancelPermitted = null) =>
            new(
                isAuthenticated ?? IsAuthenticated,
                authorizedReader ?? AuthorizedReader,
                authorizedTransit ?? AuthorizedTransit,
                postingPermitted ?? PostingPermitted,
                streamingPermitted ?? StreamingPermitted,
                TransitPeerName,
                TransitPeerPolicy,
                controlCancelPermitted ?? ControlCancelPermitted);
    }
}

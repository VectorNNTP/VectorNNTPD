namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>
    /// Top-level <c>Transit</c> configuration: peer identifiers as dictionary keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Binds the exact JSON shape <c>Transit:{identifier}:{fields}</c>. There is no nested
    /// <c>Transit:Peers</c> layer. Distinct from <see cref="TransitOptions"/> under
    /// <c>Nntpd:Transit</c> (STREAM TX depth only).
    /// </para>
    /// <para>
    /// The dictionary key is the stable protocol/machine identifier (single NNTP token,
    /// exact ordinal match, not normalized). <see cref="TransitPeerOptions.PeerName"/> is
    /// the human-readable display name. An empty dictionary is valid (no configured peers).
    /// </para>
    /// </remarks>
    public sealed class TransitPeersOptions : Dictionary<string, TransitPeerOptions>
    {
        /// <summary>Top-level configuration section name.</summary>
        public const string SectionName = "Transit";

        /// <summary>Initializes an empty peer dictionary.</summary>
        public TransitPeersOptions()
            : base(StringComparer.Ordinal)
        {
        }
    }
}

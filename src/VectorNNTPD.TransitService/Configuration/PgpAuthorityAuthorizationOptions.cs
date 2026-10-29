namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>
    /// One explicit PGP control-rule association from ISC/INN <c>control.ctl</c>.
    /// </summary>
    /// <remarks>
    /// Corresponds to a <c>&lt;message&gt;:&lt;from&gt;:&lt;newsgroups&gt;:verify-&lt;identity&gt;</c>
    /// line. Stored for a later authorization implementation; not evaluated now.
    /// </remarks>
    public sealed class PgpAuthorityAuthorizationOptions
    {
        /// <summary>
        /// Gets or sets the control-message type (<c>newgroup</c>, <c>rmgroup</c>,
        /// <c>checkgroups</c>, or <c>all</c>).
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the source From-address pattern.
        /// </summary>
        public string From { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the source newsgroup/hierarchy pattern.
        /// </summary>
        public string Newsgroups { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the PGP user-id from the source <c>verify-</c> action.
        /// </summary>
        public string VerificationIdentity { get; set; } = string.Empty;
    }
}

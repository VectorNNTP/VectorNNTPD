namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>
    /// Authoritative Usenet PGP control-authority catalogue derived from ISC/INN
    /// <c>control.ctl</c>.
    /// </summary>
    /// <remarks>
    /// This is trusted reference data for a later control-message implementation.
    /// It is not an authorization engine and does not verify signatures.
    /// </remarks>
    public sealed class PgpAuthoritiesOptions
    {
        /// <summary>
        /// Gets or sets provenance for the <c>control.ctl</c> snapshot used to populate
        /// <see cref="Authorities"/>.
        /// </summary>
        public PgpAuthoritySourceOptions Source { get; set; } = new();

        /// <summary>
        /// Gets or sets the catalogue entries. An empty array is valid.
        /// </summary>
        public PgpAuthorityOptions[] Authorities { get; set; } = [];
    }
}

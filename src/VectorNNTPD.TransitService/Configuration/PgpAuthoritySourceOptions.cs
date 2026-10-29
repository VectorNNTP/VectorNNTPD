namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>
    /// Provenance for the ISC/INN <c>control.ctl</c> snapshot that populated
    /// <see cref="PgpAuthoritiesOptions.Authorities"/>.
    /// </summary>
    public sealed class PgpAuthoritySourceOptions
    {
        /// <summary>
        /// Gets or sets the ISC canonical <c>control.ctl</c> URL.
        /// </summary>
        public string? Url { get; set; }

        /// <summary>
        /// Gets or sets the INN GitHub <c>samples/control.ctl</c> URL.
        /// </summary>
        public string? InnUrl { get; set; }

        /// <summary>
        /// Gets or sets the source <c>Last modified</c> date from <c>control.ctl</c>
        /// (<c>yyyy-MM-dd</c>).
        /// </summary>
        public string? LastModified { get; set; }

        /// <summary>
        /// Gets or sets which configured URL was actually retrieved.
        /// </summary>
        /// <remarks>
        /// <c>InnUrl</c> when the ISC URL was unavailable and the INN copy was used.
        /// </remarks>
        public string? RetrievedFrom { get; set; }
    }
}

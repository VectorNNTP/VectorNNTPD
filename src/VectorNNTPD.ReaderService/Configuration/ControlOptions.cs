namespace VectorNNTPD.ReaderService.Configuration
{
    /// <summary>
    /// Top-level control-message infrastructure options.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The configuration section name is <see cref="SectionName"/> (<c>Control</c>;
    /// case-insensitive). This section is a passive catalogue. It does not enable
    /// control-message processing, dispatch, authorization, or PGP verification.
    /// </para>
    /// <para>
    /// An omitted or empty section is valid. NNTPD startup does not require a
    /// populated authority catalogue.
    /// </para>
    /// </remarks>
    public sealed class ControlOptions
    {
        /// <summary>Top-level configuration section name.</summary>
        public const string SectionName = "Control";

        /// <summary>
        /// Gets or sets the authoritative Usenet PGP control-authority catalogue.
        /// </summary>
        public PgpAuthoritiesOptions PgpAuthorities { get; set; } = new();
    }
}

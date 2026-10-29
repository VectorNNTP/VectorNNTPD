namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>
    /// One Usenet PGP control authority from ISC/INN <c>control.ctl</c>.
    /// </summary>
    /// <remarks>
    /// Optional metadata properties are omitted from JSON when the source does not
    /// provide them. This type stores public catalogue metadata only: no private
    /// keys, passphrases, or armored key blocks.
    /// </remarks>
    public sealed class PgpAuthorityOptions
    {
        /// <summary>
        /// Gets or sets the hierarchy heading name from <c>control.ctl</c>.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the source <c>Contact</c> value, when present.
        /// </summary>
        public string? Contact { get; set; }

        /// <summary>
        /// Gets or sets the source <c>Admin group</c> value, when present.
        /// </summary>
        public string? AdminGroup { get; set; }

        /// <summary>
        /// Gets or sets the source <c>URL</c> value, when present.
        /// </summary>
        public string? Url { get; set; }

        /// <summary>
        /// Gets or sets the source <c>Key URL</c> value, when present.
        /// </summary>
        public string? KeyUrl { get; set; }

        /// <summary>
        /// Gets or sets the source PGP key fingerprint, when present.
        /// </summary>
        /// <remarks>
        /// Stored as uppercase hexadecimal without spaces. Source grouping whitespace
        /// is discarded only; the hex digits are unchanged.
        /// </remarks>
        public string? KeyFingerprint { get; set; }

        /// <summary>
        /// Gets or sets the source <c>Key mail</c> value, when present.
        /// </summary>
        public string? KeyMail { get; set; }

        /// <summary>
        /// Gets or sets the source <c>Syncable server</c> value, when present.
        /// </summary>
        public string? SyncableServer { get; set; }

        /// <summary>
        /// Gets or sets explicit PGP <c>verify-*</c> control rules from the source.
        /// </summary>
        /// <remarks>
        /// Populated only when <c>control.ctl</c> states a control-message type,
        /// From pattern, newsgroup pattern, and verification identity. Rules are
        /// not collapsed. This array is not evaluated at runtime.
        /// </remarks>
        public PgpAuthorityAuthorizationOptions[] Authorizations { get; set; } = [];
    }
}

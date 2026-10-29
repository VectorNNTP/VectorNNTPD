namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>
    /// Top-level ordinary-newsgroup moderation options.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The configuration section name is <see cref="SectionName"/> (<c>Moderation</c>;
    /// case-insensitive). This section is not <c>Control:PgpAuthorities</c>. PGP control
    /// authorities do not authorize moderated-newsgroup approval.
    /// </para>
    /// <para>
    /// An omitted or empty section is valid. Runtime authorization is loaded from
    /// <c>nntpmoderators</c>. A leftover <see cref="Moderators"/> list fails startup.
    /// Missing database routes reject moderated POST; they do not bypass moderation.
    /// </para>
    /// </remarks>
    public sealed class ModerationOptions
    {
        /// <summary>Top-level configuration section name.</summary>
        public const string SectionName = "Moderation";

        /// <summary>
        /// Gets or sets provenance for the imported INN <c>samples/moderators</c> snapshot.
        /// </summary>
        public ModerationSourceOptions Source { get; set; } = new();

        /// <summary>
        /// Gets or sets a leftover static mapping list. Must be empty.
        /// Runtime authorization is loaded from <c>nntpmoderators</c>.
        /// </summary>
        public ModeratorMappingOptions[] Moderators { get; set; } = [];
    }
}

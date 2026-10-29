namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>
    /// Leftover static moderator-route shape retained only so a non-empty
    /// <c>Moderation:Moderators</c> list can fail startup validation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Address"/> is the unapproved-article submission destination.
    /// A static mailbox is used as-is. An INN template containing <c>%s</c> is
    /// expanded to the matched newsgroup name with dots changed to dashes.
    /// The expanded mailbox does not authenticate an NNTP client.
    /// </para>
    /// <para>
    /// <see cref="Username"/> is optional. It is required only for local
    /// authenticated reinjection. Routing-only INN destinations omit it.
    /// Credentials are not stored here.
    /// </para>
    /// </remarks>
    public sealed class ModeratorMappingOptions
    {
        /// <summary>
        /// Gets or sets the RFC 3977 wildmat matched against the newsgroup name.
        /// </summary>
        public string Pattern { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the routing mailbox or INN <c>%s</c> template for groups
        /// matching <see cref="Pattern"/>.
        /// </summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the AUTHINFO username authorized to approve groups matching
        /// <see cref="Pattern"/>, or empty when this entry is routing-only.
        /// </summary>
        public string Username { get; set; } = string.Empty;
    }
}

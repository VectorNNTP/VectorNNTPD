namespace VectorNNTPD.TransitService.Configuration
{
    /// <summary>
    /// Provenance for the historical INN <c>samples/moderators</c> import.
    /// Runtime authorization is <c>nntpmoderators</c>, not this metadata.
    /// </summary>
    public sealed class ModerationSourceOptions
    {
        /// <summary>
        /// Gets or sets the human-facing INN <c>samples/moderators</c> URL.
        /// </summary>
        public string? Url { get; set; }

        /// <summary>
        /// Gets or sets the INN GitHub raw <c>samples/moderators</c> URL.
        /// </summary>
        public string? InnUrl { get; set; }

        /// <summary>
        /// Gets or sets which configured URL was actually retrieved.
        /// </summary>
        public string? RetrievedFrom { get; set; }
    }
}

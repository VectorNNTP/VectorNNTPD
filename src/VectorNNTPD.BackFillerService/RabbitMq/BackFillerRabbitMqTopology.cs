namespace VectorNNTPD.BackFillerService.RabbitMq
{
    /// <summary>
    /// Canonical <c>backfiller.*</c> topology names consumed by Article Work.
    /// </summary>
    /// <remarks>
    /// BackFiller declares durable fanout exchanges and Hosts-derived classic or quorum queues for each
    /// backbone that becomes usable. This type does not declare, bind, or delete broker entities.
    /// <c>cache.requests</c> is the NNTPD→StorageServer fleet article-presence fanout exchange
    /// (not a BackFiller consume target and not a shared work queue).
    /// Legacy <c>grabbers.*</c> names are out of scope.
    /// </remarks>
    internal static class BackFillerRabbitMqTopology
    {
        /// <summary>Topology namespace prefix.</summary>
        private const string Prefix = "backfiller";

        /// <summary>
        /// Canonical provider backbone labels. <see cref="ComposeProviderEntity"/> turns one label
        /// into a <c>backfiller.{backbone}</c> entity name.
        /// </summary>
        /// <remarks>
        /// Account mapping matches these spellings case-insensitively. This type does not declare
        /// the corresponding broker entities and does not enumerate this list at startup.
        /// </remarks>
        internal static readonly IReadOnlyList<string> ProviderBackbones =
        [
            "Abavia",
            "Altopia",
            "BaseIP",
            "Eweka",
            "Elbracht",
            "Giganews",
            "GTT",
            "Highwinds",
            "ItsHosted",
            "Novia",
            "UExpress",
            "UsenetNode1",
        ];

        /// <summary>
        /// Normalizes a topology entity name: trim, then invariant lower-case.
        /// </summary>
        /// <param name="entityName">Exchange, queue, routing-key, or backbone label.</param>
        /// <returns>The trimmed invariant-lowercase name.</returns>
        /// <exception cref="ArgumentException"><paramref name="entityName"/> is null or whitespace.</exception>
        private static string Normalize(string entityName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
            return entityName.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Builds <c>backfiller.{backbone}</c> after normalizing both parts.
        /// </summary>
        /// <param name="backbone">Unqualified provider backbone label. Not restricted to <see cref="ProviderBackbones"/>.</param>
        /// <returns>The composed entity name, <c>backfiller.</c> plus the normalized backbone.</returns>
        /// <exception cref="ArgumentException"><paramref name="backbone"/> is null or whitespace.</exception>
        internal static string ComposeProviderEntity(string backbone)
        {
            var normalized = Normalize(backbone);
            return $"{Prefix}.{normalized}";
        }
    }
}

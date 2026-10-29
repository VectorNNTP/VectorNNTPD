namespace VectorNNTPD.TransitService.RabbitMq
{
    /// <summary>
    /// Deterministic, culture-independent names for RabbitMQ topology entities.
    /// </summary>
    /// <remarks>
    /// Every exchange name, queue name, and routing key is trimmed and converted with
    /// <see cref="string.ToLowerInvariant"/>. Callers must not assume their input is already
    /// normalized.
    /// </remarks>
    internal static class RabbitMqTopologyNames
    {
        /// <summary>
        /// Normalizes a topology entity name: trim surrounding whitespace, then invariant lower-case.
        /// </summary>
        /// <param name="entityName">Exchange, queue, routing-key, or other topology entity name.</param>
        /// <returns>The trimmed invariant-lowercase name.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="entityName"/> is null or whitespace.</exception>
        internal static string Normalize(string entityName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
            return entityName.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Builds a two-part entity name <c>{prefix}.{name}</c> after normalizing both parts.
        /// </summary>
        /// <param name="prefix">Namespace such as <c>backfiller</c>.</param>
        /// <param name="name">Unqualified identifier such as a provider name.</param>
        /// <returns>The composed normalized entity name.</returns>
        internal static string Compose(string prefix, string name) =>
            Normalize($"{Normalize(prefix)}.{Normalize(name)}");
    }
}

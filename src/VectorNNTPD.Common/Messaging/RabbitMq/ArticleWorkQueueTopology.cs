namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>
    /// Article Work provider queue kind derived from the configured <see cref="RabbitMqOptions.Hosts"/> count.
    /// </summary>
    internal enum ArticleWorkQueueKind
    {
        /// <summary>Single configured host: durable classic queue without quorum arguments.</summary>
        Classic = 0,

        /// <summary>Two or more configured hosts: durable quorum queue with initial group size equal to host count.</summary>
        Quorum = 1,
    }

    /// <summary>
    /// Derived Article Work queue topology from the configured RabbitMQ host list.
    /// </summary>
    /// <remarks>
    /// Uses the configured unique host count only. Reachable hosts, live connections, and live cluster
    /// membership never change this projection. All configured hosts must belong to the same RabbitMQ
    /// cluster; the client host list does not establish cluster membership.
    /// Changing <see cref="RabbitMqOptions.Hosts"/> from one entry to multiple (or the reverse) changes
    /// the desired queue type and may require an explicit operator queue migration; it is not a safe
    /// in-place conversion.
    /// </remarks>
    /// <param name="Kind">Classic or quorum selection.</param>
    /// <param name="ConfiguredHostCount">Unique trimmed host count from configuration.</param>
    /// <param name="QuorumInitialGroupSize">
    /// Declared <c>x-quorum-initial-group-size</c> and readiness membership floor when <see cref="Kind"/> is
    /// <see cref="ArticleWorkQueueKind.Quorum"/>; otherwise <see langword="null"/>.
    /// </param>
    internal readonly record struct ArticleWorkQueueTopology(
        ArticleWorkQueueKind Kind,
        int ConfiguredHostCount,
        int? QuorumInitialGroupSize)
    {
        /// <summary>
        /// Maximum unique <see cref="RabbitMqOptions.Hosts"/> entries accepted by validation.
        /// Also the RabbitMQ quorum initial group size upper bound used by this product.
        /// </summary>
        internal const int MaxConfiguredHosts = 7;

        /// <summary>
        /// Whether Management-plane quorum membership must be verified before consumer readiness.
        /// </summary>
        internal bool RequiresQuorumHealth => Kind == ArticleWorkQueueKind.Quorum;

        /// <summary>
        /// Counts unique configured hosts (case-insensitive, trimmed) and derives classic vs quorum topology.
        /// </summary>
        /// <param name="hosts">Configured host list. Must contain at least one unique non-empty entry.</param>
        /// <returns>Derived topology.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no unique host remains after trimming.</exception>
        internal static ArticleWorkQueueTopology FromConfiguredHosts(IEnumerable<string?>? hosts)
        {
            var unique = CountUniqueHosts(hosts);
            if (unique < 1)
            {
                throw new InvalidOperationException(
                    "RabbitMQ:Hosts must contain at least one unique entry to derive Article Work queue topology.");
            }

            if (unique == 1)
            {
                return new ArticleWorkQueueTopology(
                    ArticleWorkQueueKind.Classic,
                    ConfiguredHostCount: 1,
                    QuorumInitialGroupSize: null);
            }

            return new ArticleWorkQueueTopology(
                ArticleWorkQueueKind.Quorum,
                ConfiguredHostCount: unique,
                QuorumInitialGroupSize: unique);
        }

        /// <summary>
        /// Counts unique non-empty trimmed hosts using ordinal case-insensitive comparison.
        /// </summary>
        /// <param name="hosts">Configured host list. Null is treated as empty.</param>
        /// <returns>Unique host count.</returns>
        /// <remarks>
        /// Filters blank entries before unique counting. Validation remains the authoritative rejection
        /// of empty and whitespace-only <see cref="RabbitMqOptions.Hosts"/> lists; this helper only
        /// projects topology from already-trimmed non-empty values.
        /// </remarks>
        internal static int CountUniqueHosts(IEnumerable<string?>? hosts)
        {
            if (hosts is null)
            {
                return 0;
            }

            return hosts
                .Where(static host => !string.IsNullOrWhiteSpace(host))
                .Select(static host => host!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }
    }
}

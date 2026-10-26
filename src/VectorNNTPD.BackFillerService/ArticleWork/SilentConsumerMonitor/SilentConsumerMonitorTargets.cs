using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.RabbitMq;

namespace VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor
{
    /// <summary>
    /// One Article Work queue this process may observe via Management, derived from the live provider catalog.
    /// </summary>
    /// <param name="Backbone">Provider backbone label.</param>
    /// <param name="QueueName"><c>backfiller.{backbone}</c> entity name.</param>
    /// <param name="ExpectedActive">
    /// True when this instance currently desires consume slots for the backbone
    /// (<see cref="BackFillerProviderDefinition.MaxSessions"/> &gt; 0 and usable NNTP capacity).
    /// Idle or capacity-empty queues must not raise shared consumer-loss.
    /// </param>
    internal readonly record struct SilentConsumerMonitorQueueTarget(
        string Backbone,
        string QueueName,
        bool ExpectedActive);

    /// <summary>Resolves monitor queue targets from database-backed catalog + capacity.</summary>
    internal static class SilentConsumerMonitorTargets
    {
        /// <summary>
        /// Builds the set of Article Work queues this instance is responsible for observing.
        /// Providers with <c>MaxSessions &lt;= 0</c> are omitted (not BackFiller consume targets).
        /// </summary>
        /// <param name="catalog">Live provider catalog (backbone + MaxSessions).</param>
        /// <param name="capacity">Usable capacity used to set <see cref="SilentConsumerMonitorQueueTarget.ExpectedActive"/>.</param>
        /// <returns>Deduplicated queue targets for this cycle.</returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="catalog"/> or <paramref name="capacity"/> is null.
        /// </exception>
        public static IReadOnlyList<SilentConsumerMonitorQueueTarget> Resolve(
            IBackFillerProviderCatalog catalog,
            IBackboneUsableCapacityProvider capacity)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            ArgumentNullException.ThrowIfNull(capacity);

            var targets = new List<SilentConsumerMonitorQueueTarget>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var provider in catalog.Providers)
            {
                if (provider.MaxSessions <= 0 || string.IsNullOrWhiteSpace(provider.Backbone))
                {
                    continue;
                }

                var queue = BackFillerRabbitMqTopology.ComposeProviderEntity(provider.Backbone);
                if (!seen.Add(queue))
                {
                    continue;
                }

                var expected = capacity.HasUsableCapacityForBackbone(provider.Backbone);
                targets.Add(new SilentConsumerMonitorQueueTarget(provider.Backbone, queue, expected));
            }

            return targets;
        }
    }
}

namespace VectorNNTPD.TransitService.RabbitMq.ArticleWork
{
    /// <summary>One eligible BackFiller backbone with a positive active consumer count.</summary>
    /// <param name="Backbone">Canonical provider identifier (JSON <c>backbone</c> casing).</param>
    /// <param name="Exchange">Normalized exchange name.</param>
    /// <param name="RoutingKey">Normalized routing key.</param>
    /// <param name="QueueName">Normalized queue name used for passive consumer inspection.</param>
    /// <param name="ConsumerCount">Broker-reported consumer count (must be &gt; 0).</param>
    internal readonly record struct BackfillEligibleBackbone(
        string Backbone,
        string Exchange,
        string RoutingKey,
        string QueueName,
        int ConsumerCount);

    /// <summary>
    /// Snapshot of BackFiller ArticleWork consumer availability for scheduler selection.
    /// </summary>
    internal interface IBackfillConsumerAvailability
    {
        /// <summary>
        /// Returns provider backbones with a positive active consumer count.
        /// Zero-consumer and unknown queues are omitted.
        /// </summary>
        IReadOnlyList<BackfillEligibleBackbone> GetEligibleBackbones();
    }

    /// <summary>
    /// Selects one eligible backbone weighted by active consumer count.
    /// </summary>
    internal interface IBackboneSelector
    {
        /// <summary>
        /// Chooses one candidate. Returns <see langword="null"/> when
        /// <paramref name="candidates"/> is empty.
        /// </summary>
        BackfillEligibleBackbone? Select(IReadOnlyList<BackfillEligibleBackbone> candidates);
    }

    /// <summary>
    /// Weighted random backbone selection. Isolates randomness for deterministic tests.
    /// </summary>
    internal sealed class WeightedBackboneSelector : IBackboneSelector
    {
        private readonly Func<int, int> _nextInt;

        /// <summary>Creates a selector using <see cref="Random.Shared"/>.</summary>
        public WeightedBackboneSelector()
            : this(static exclusiveMax => Random.Shared.Next(exclusiveMax))
        {
        }

        /// <summary>
        /// Creates a selector with an injectable RNG. <paramref name="nextInt"/> receives
        /// an exclusive upper bound and must return a value in <c>[0, exclusiveMax)</c>.
        /// </summary>
        internal WeightedBackboneSelector(Func<int, int> nextInt)
        {
            ArgumentNullException.ThrowIfNull(nextInt);
            _nextInt = nextInt;
        }

        /// <inheritdoc />
        public BackfillEligibleBackbone? Select(IReadOnlyList<BackfillEligibleBackbone> candidates)
        {
            ArgumentNullException.ThrowIfNull(candidates);
            if (candidates.Count == 0)
            {
                return null;
            }

            long total = 0;
            for (var i = 0; i < candidates.Count; i++)
            {
                var weight = candidates[i].ConsumerCount;
                if (weight < 1)
                {
                    continue;
                }

                total += weight;
            }

            if (total < 1)
            {
                return null;
            }

            if (total > int.MaxValue)
            {
                total = int.MaxValue;
            }

            var roll = _nextInt((int)total);
            if (roll < 0 || roll >= total)
            {
                throw new InvalidOperationException("Backbone selector RNG returned a value outside [0, total).");
            }

            long cursor = 0;
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (candidate.ConsumerCount < 1)
                {
                    continue;
                }

                cursor += candidate.ConsumerCount;
                if (roll < cursor)
                {
                    return candidate;
                }
            }

            return candidates[^1];
        }
    }
}

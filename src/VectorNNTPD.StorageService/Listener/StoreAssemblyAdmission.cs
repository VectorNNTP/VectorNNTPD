using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.StorageService.Listener
{

    /// <summary>
    /// Process-wide cap on in-flight TAKETHIS article assemblies, acquired before the article
    /// payload is buffered and admitted to the accepted-article queue.
    /// </summary>
    public sealed class StoreAssemblyAdmission
    {
        private readonly int _maximum;
        private int _inFlight;

        /// <summary>Initializes a cap of at least one assembly.</summary>
        public StoreAssemblyAdmission(int maximum)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
            _maximum = maximum;
        }

        /// <summary>Gets the configured maximum.</summary>
        public int Maximum => _maximum;

        /// <summary>Gets the current in-flight count.</summary>
        public int InFlight => Volatile.Read(ref _inFlight);

        /// <summary>
        /// Caps concurrent assemblies so worst-case retained payload
        /// (<c>count × MaxArticleBytes</c>) cannot exceed the accepted-queue byte budget
        /// and cannot exceed <paramref name="maxActiveConnections"/>.
        /// </summary>
        /// <param name="maxActiveConnections">Listener connection ceiling.</param>
        /// <param name="maxQueuedBytes">Accepted-article queue budget.</param>
        /// <returns>Positive assembly concurrency limit.</returns>
        public static int LimitFor(int maxActiveConnections, long maxQueuedBytes)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxActiveConnections, 1);
            if (maxQueuedBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxQueuedBytes));
            }

            var byBudget = (int)Math.Min(
                int.MaxValue,
                Math.Max(1L, maxQueuedBytes / ArticleResourceLimits.MaxArticleBytes));
            return Math.Min(maxActiveConnections, byBudget);
        }

        /// <summary>Reserves one assembly slot.</summary>
        public bool TryEnter()
        {
            while (true)
            {
                var current = Volatile.Read(ref _inFlight);
                if (current >= _maximum)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _inFlight, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        /// <summary>Releases one assembly slot.</summary>
        public void Exit()
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

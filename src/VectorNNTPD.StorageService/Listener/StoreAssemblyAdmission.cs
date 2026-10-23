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

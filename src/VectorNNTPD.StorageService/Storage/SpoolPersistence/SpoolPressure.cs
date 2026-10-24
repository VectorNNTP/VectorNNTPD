namespace VectorNNTPD.StorageService.Storage.SpoolPersistence
{

    /// <summary>
    /// Last admission outcome seen by the persistence workers.
    /// Blocked means the last selection found no spool that could accept another publish:
    /// every spool was closed for a filesystem failure, at its in-flight cap, or known
    /// not to fit the article. Device load does not set this.
    /// </summary>
    internal sealed class SpoolPressure
    {
        private readonly SpoolCapacityWaitSignal? _waitSignal;
        private int _blocked;

        /// <summary>Initializes pressure without a wait signal.</summary>
        public SpoolPressure()
        {
        }

        /// <summary>Initializes pressure that wakes capacity-blocked workers when saturation clears.</summary>
        public SpoolPressure(SpoolCapacityWaitSignal waitSignal)
        {
            ArgumentNullException.ThrowIfNull(waitSignal);
            _waitSignal = waitSignal;
        }

        /// <summary>Whether the control loop must not add workers.</summary>
        internal bool IsSaturated => Volatile.Read(ref _blocked) != 0;

        /// <summary>Records whether another publish can be placed.</summary>
        /// <param name="blocked"><see langword="true"/> when no spool could accept the article.</param>
        internal void SetSaturated(bool blocked)
        {
            var next = blocked ? 1 : 0;
            var previous = Interlocked.Exchange(ref _blocked, next);
            if (previous != 0 && next == 0)
            {
                _waitSignal?.Notify();
            }
        }
    }
}

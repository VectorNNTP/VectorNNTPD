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
        private int _blocked;

        /// <summary>Whether the control loop must not add workers.</summary>
        internal bool IsSaturated => Volatile.Read(ref _blocked) != 0;

        /// <summary>Records whether another publish can be placed.</summary>
        /// <param name="blocked"><see langword="true"/> when no spool could accept the article.</param>
        internal void SetSaturated(bool blocked) => Volatile.Write(ref _blocked, blocked ? 1 : 0);
    }
}

namespace VectorNNTPD.TransitService.PostFilter
{
    /// <summary>Process counters for PostFilter reservation outcomes.</summary>
    public sealed class PostFilterMetrics
    {
        private long _commitNoop;
        private long _commitUnavailable;

        /// <summary>Gets COMMIT NOOP after a successful TryAdmit.</summary>
        public long CommitNoop => Volatile.Read(ref _commitNoop);

        /// <summary>Gets COMMIT Unavailable after a successful TryAdmit.</summary>
        public long CommitUnavailable => Volatile.Read(ref _commitUnavailable);

        /// <summary>Records a COMMIT NOOP. The article remains queued.</summary>
        public void RecordCommitNoop() => Interlocked.Increment(ref _commitNoop);

        /// <summary>Records COMMIT Unavailable after admit. Does not RELEASE.</summary>
        public void RecordCommitUnavailable() => Interlocked.Increment(ref _commitUnavailable);
    }
}

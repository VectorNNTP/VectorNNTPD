namespace VectorNNTPD.StorageService.Storage.SpoolPersistence
{

    /// <summary>
    /// Remembers spools whose publishes failed for a filesystem or device reason.
    /// Disk busy time is not recorded here. A disk-full failure closes the spool
    /// until a later free-space probe reports available bytes. Other I/O failures
    /// close it after <see cref="SpoolSelectionPolicy.OperationalFailureCloseCount"/>
    /// and allow one trial write after <see cref="SpoolSelectionPolicy.FailureRetry"/>.
    /// </summary>
    internal sealed class SpoolHealth
    {
        private readonly Dictionary<string, FailureState> _failures = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private readonly TimeProvider _time;

        /// <summary>Initializes the tracker with the process clock.</summary>
        /// <param name="time">Clock used for the I/O retry delay.</param>
        internal SpoolHealth(TimeProvider time)
        {
            ArgumentNullException.ThrowIfNull(time);
            _time = time;
        }

        /// <summary>Whether <paramref name="spoolRoot"/> is not closed for an operational failure.</summary>
        /// <param name="spoolRoot">Canonical spool path.</param>
        /// <returns><see langword="false"/> while a disk-full close or an I/O cooldown is in effect.</returns>
        internal bool AcceptsWrites(string spoolRoot)
        {
            lock (_gate)
            {
                if (!_failures.TryGetValue(spoolRoot, out var state))
                {
                    return true;
                }

                if (state.DiskFull)
                {
                    return false;
                }

                if (state.Count < SpoolSelectionPolicy.OperationalFailureCloseCount)
                {
                    return true;
                }

                return _time.GetUtcNow() >= state.RetryAfter;
            }
        }

        /// <summary>
        /// Clears a disk-full close when a newer free-space sample reports available bytes.
        /// An access or I/O close is left in place. A capacity sample does not clear that close.
        /// </summary>
        /// <param name="spoolRoot">Canonical spool path.</param>
        internal void RecoverDiskFull(string spoolRoot)
        {
            lock (_gate)
            {
                if (_failures.TryGetValue(spoolRoot, out var state) && state.DiskFull)
                {
                    _failures.Remove(spoolRoot);
                }
            }
        }

        /// <summary>Clears failures after a successful publish.</summary>
        /// <param name="spoolRoot">Canonical spool path.</param>
        internal void Success(string spoolRoot)
        {
            lock (_gate)
            {
                _failures.Remove(spoolRoot);
            }
        }

        /// <summary>Records one filesystem failure. Load is not a failure.</summary>
        /// <param name="spoolRoot">Canonical spool path.</param>
        /// <param name="close">When true, the spool is closed for disk-full until a later probe has space.</param>
        internal void Failure(string spoolRoot, bool close)
        {
            lock (_gate)
            {
                var count = _failures.TryGetValue(spoolRoot, out var current) ? current.Count : 0;
                if (close)
                {
                    _failures[spoolRoot] = new FailureState(
                        SpoolSelectionPolicy.OperationalFailureCloseCount,
                        DiskFull: true,
                        DateTimeOffset.MaxValue);
                    return;
                }

                var next = count + 1;
                var retryAfter = next >= SpoolSelectionPolicy.OperationalFailureCloseCount
                    ? _time.GetUtcNow() + SpoolSelectionPolicy.FailureRetry
                    : DateTimeOffset.MinValue;
                _failures[spoolRoot] = new FailureState(next, DiskFull: false, retryAfter);
            }
        }

        /// <summary>Failure count and whether a free-space recovery may clear it.</summary>
        /// <param name="Count">Consecutive failures. The close threshold stops new writes until retry or recovery.</param>
        /// <param name="DiskFull">Whether <see cref="RecoverDiskFull"/> may clear this close.</param>
        /// <param name="RetryAfter">When an I/O close may be tried again. Disk-full uses <see cref="DateTimeOffset.MaxValue"/>.</param>
        private readonly record struct FailureState(int Count, bool DiskFull, DateTimeOffset RetryAfter);
    }
}

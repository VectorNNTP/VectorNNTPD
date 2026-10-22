namespace VectorNNTPD.StorageService.Storage.SpoolPersistence
{

    /// <summary>Counts in-flight publishes per spool. The count is the selector's in-flight input.</summary>
    internal sealed class SpoolWriteGate
    {
        private readonly Dictionary<string, int> _inFlight = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        /// <summary>
        /// Selects the best eligible spool using the in-flight counts under this lock, then reserves it.
        /// Concurrent callers cannot all reserve the same spool from one stale count.
        /// <paramref name="observed"/> is updated in place with the locked in-flight counts.
        /// </summary>
        /// <param name="observed">Candidates from the latest monitor snapshot. Their in-flight fields are replaced.</param>
        /// <param name="requiredBytes">Rewritten article length passed to the selector.</param>
        /// <param name="selection">The reserved spool when the method returns <see langword="true"/>.</param>
        /// <returns><see langword="false"/> when every spool is blocked, at the cap, or known not to fit.</returns>
        internal bool TryReserve(Span<SpoolCandidate> observed, long requiredBytes, out SpoolSelection selection)
        {
            lock (_gate)
            {
                if (observed.Length == 0)
                {
                    selection = default;
                    return false;
                }

                for (var i = 0; i < observed.Length; i++)
                {
                    var candidate = observed[i];
                    var inFlight = _inFlight.TryGetValue(candidate.SpoolRoot, out var current) ? current : 0;
                    observed[i] = candidate with { InFlightWrites = inFlight };
                }

                var chosen = SpoolSelector.Select(observed, requiredBytes);
                if (chosen is not { } selected)
                {
                    selection = default;
                    return false;
                }

                _inFlight[selected.SpoolRoot] = (_inFlight.TryGetValue(selected.SpoolRoot, out var count) ? count : 0) + 1;
                selection = selected;
                return true;
            }
        }

        /// <summary>Reserves one publish slot when the spool is under <paramref name="limit"/>.</summary>
        /// <param name="spoolRoot">Canonical spool path.</param>
        /// <param name="limit">Per-spool cap. Must be at least 1.</param>
        /// <returns><see langword="false"/> when the spool is already at the cap.</returns>
        internal bool TryEnter(string spoolRoot, int limit)
        {
            lock (_gate)
            {
                _inFlight.TryGetValue(spoolRoot, out var current);
                if (current >= limit)
                {
                    return false;
                }

                _inFlight[spoolRoot] = current + 1;
                return true;
            }
        }

        /// <summary>Releases one publish slot. Extra releases are ignored.</summary>
        /// <param name="spoolRoot">Canonical spool path.</param>
        internal void Exit(string spoolRoot)
        {
            lock (_gate)
            {
                if (!_inFlight.TryGetValue(spoolRoot, out var current) || current <= 0)
                {
                    return;
                }

                if (current == 1)
                {
                    _inFlight.Remove(spoolRoot);
                    return;
                }

                _inFlight[spoolRoot] = current - 1;
            }
        }

        /// <summary>Current reservations for <paramref name="spoolRoot"/>.</summary>
        /// <param name="spoolRoot">Canonical spool path.</param>
        /// <returns>The in-flight count. Zero when the spool has none.</returns>
        internal int InFlight(string spoolRoot)
        {
            lock (_gate)
            {
                return _inFlight.TryGetValue(spoolRoot, out var current) ? current : 0;
            }
        }
    }
}

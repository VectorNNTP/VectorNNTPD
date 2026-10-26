namespace VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor
{
    /// <summary>One sample history entry (success snapshot or classified failure).</summary>
    /// <param name="AtUtc">Sample timestamp (snapshot time on success; caller-supplied on failure).</param>
    /// <param name="IsValid">True when a usable snapshot with required gauges is present.</param>
    /// <param name="Snapshot">Parsed snapshot on success attempts; null on explicit failures.</param>
    /// <param name="FailureKind">Failure kind when invalid; null on valid success.</param>
    /// <param name="FailureReason">Non-secret failure or incomplete-gauges reason when invalid.</param>
    internal readonly record struct QueueSampleEntry(
        DateTimeOffset AtUtc,
        bool IsValid,
        ManagementQueueSnapshot? Snapshot,
        ManagementPollKind? FailureKind,
        string? FailureReason);

    /// <summary>Bounded ring of Management samples for one queue.</summary>
    internal sealed class QueueSampleHistory
    {
        /// <summary>Fixed-capacity ring buffer storage.</summary>
        private readonly QueueSampleEntry[] _entries;

        /// <summary>Number of retained entries (≤ capacity).</summary>
        private int _count;

        /// <summary>Next write index in the ring.</summary>
        private int _next;

        /// <summary>Creates history with fixed capacity (minimum 2).</summary>
        /// <param name="capacity">Ring capacity; must be ≥ 2.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> is less than 2.</exception>
        public QueueSampleHistory(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
            _entries = new QueueSampleEntry[capacity];
        }

        /// <summary>Number of retained samples in the ring.</summary>
        public int Count => _count;

        /// <summary>Appends a success sample; incomplete gauges are stored as invalid with reason.</summary>
        /// <param name="snapshot">Parsed Management snapshot (required).</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="snapshot"/> is null.</exception>
        public void AddSuccess(ManagementQueueSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            Push(new QueueSampleEntry(
                snapshot.SampledAtUtc,
                snapshot.HasRequiredGauges,
                snapshot,
                null,
                snapshot.HasRequiredGauges ? null : "incomplete gauges"));
        }

        /// <summary>Appends an explicit failure (no synthetic zeros).</summary>
        /// <param name="atUtc">Failure timestamp.</param>
        /// <param name="kind">Management poll failure kind.</param>
        /// <param name="reason">Non-secret reason text.</param>
        public void AddFailure(DateTimeOffset atUtc, ManagementPollKind kind, string reason) =>
            Push(new QueueSampleEntry(atUtc, false, null, kind, reason));

        /// <summary>Returns an oldest-to-newest copy of retained samples.</summary>
        /// <returns>Empty array when empty; otherwise a new array of length <see cref="Count"/>.</returns>
        public IReadOnlyList<QueueSampleEntry> Snapshot()
        {
            if (_count == 0)
            {
                return Array.Empty<QueueSampleEntry>();
            }

            var result = new QueueSampleEntry[_count];
            var start = (_next - _count + _entries.Length) % _entries.Length;
            for (var i = 0; i < _count; i++)
            {
                result[i] = _entries[(start + i) % _entries.Length];
            }

            return result;
        }

        /// <summary>
        /// Derives a per-second counter rate between two valid samples; null on reset/gap/missing.
        /// </summary>
        /// <param name="older">Earlier sample.</param>
        /// <param name="newer">Later sample.</param>
        /// <param name="select">Selects the cumulative counter from each snapshot.</param>
        /// <returns>
        /// <c>(newer - older) / elapsedSeconds</c> when both samples are valid, counters present,
        /// elapsed &gt; 0, and the counter did not decrease; otherwise <see langword="null"/>.
        /// </returns>
        public static double? TryComputeCounterRate(
            QueueSampleEntry older,
            QueueSampleEntry newer,
            Func<ManagementQueueSnapshot, long?> select)
        {
            if (!older.IsValid || !newer.IsValid || older.Snapshot is null || newer.Snapshot is null)
            {
                return null;
            }

            var a = select(older.Snapshot);
            var b = select(newer.Snapshot);
            if (a is null || b is null)
            {
                return null;
            }

            var elapsed = (newer.AtUtc - older.AtUtc).TotalSeconds;
            if (elapsed <= 0 || b.Value < a.Value)
            {
                return null;
            }

            return (b.Value - a.Value) / elapsed;
        }

        /// <summary>Writes <paramref name="entry"/> into the ring, overwriting the oldest when full.</summary>
        /// <param name="entry">Sample to retain.</param>
        private void Push(QueueSampleEntry entry)
        {
            _entries[_next] = entry;
            _next = (_next + 1) % _entries.Length;
            if (_count < _entries.Length)
            {
                _count++;
            }
        }
    }
}

namespace VectorNNTPD.ReaderService.PostFilter
{
    /// <summary>No-op evidence queue used when the writer is not registered.</summary>
    public sealed class DisabledPostFilterRejectionEvidenceQueue : IPostFilterRejectionEvidenceQueue
    {
        /// <summary>Shared disabled instance.</summary>
        public static DisabledPostFilterRejectionEvidenceQueue Instance { get; } = new();

        /// <inheritdoc />
        public int Capacity => 0;

        /// <inheritdoc />
        public int Count => 0;

        /// <inheritdoc />
        public long Dropped => 0;

        /// <inheritdoc />
        public long Written => 0;

        /// <inheritdoc />
        public long WriteFailures => 0;

        /// <inheritdoc />
        public bool TryEnqueue(PostFilterRejectionEvidence evidence)
        {
            ArgumentNullException.ThrowIfNull(evidence);
            return true;
        }

        /// <inheritdoc />
        public void Complete()
        {
        }

        /// <inheritdoc />
        public ValueTask<PostFilterRejectionEvidence?> DequeueAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<PostFilterRejectionEvidence?>(null);
    }
}

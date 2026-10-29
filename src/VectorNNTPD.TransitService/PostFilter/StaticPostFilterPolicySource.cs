namespace VectorNNTPD.TransitService.PostFilter
{
    /// <summary>Fixed snapshot for tests and the disabled-gate default.</summary>
    internal sealed class StaticPostFilterPolicySource : IPostFilterPolicySource
    {
        /// <summary>Initializes a source that always returns <paramref name="snapshot"/>.</summary>
        public StaticPostFilterPolicySource(PostFilterPolicySnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            Current = snapshot;
        }

        /// <inheritdoc />
        public PostFilterPolicySnapshot Current { get; }
    }
}

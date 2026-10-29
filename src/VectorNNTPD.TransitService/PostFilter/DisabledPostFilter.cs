namespace VectorNNTPD.TransitService.PostFilter
{
    /// <summary>Accepts every article without quota or SpamAssassin. Used when no filter is wired.</summary>
    internal sealed class DisabledPostFilter : IPostFilter
    {
        /// <summary>Shared instance.</summary>
        public static DisabledPostFilter Instance { get; } = new();

        /// <inheritdoc />
        public ValueTask<PostFilterResult> EvaluateAsync(
            PostFilterRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(PostFilterResult.AcceptedDisabled());
        }
    }
}

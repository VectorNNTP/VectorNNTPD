using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.TransitService.PostFilter
{
    /// <summary>SpamAssassin seam used when CHECK is not configured.</summary>
    internal sealed class NullPostFilterSpamAssassin : IPostFilterSpamAssassin
    {
        /// <summary>Shared instance.</summary>
        public static NullPostFilterSpamAssassin Instance { get; } = new();

        /// <inheritdoc />
        public ValueTask<PostFilterSpamAssassinResult> CheckAsync(
            ArticleRecord article,
            string? accountName,
            PostFilterSpamAssassinTarget target,
            SpamdScanContext scanContext,
            CancellationToken cancellationToken = default)
        {
            _ = article;
            _ = accountName;
            _ = target;
            _ = scanContext;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(PostFilterSpamAssassinResult.Failed("spamassassin not configured"));
        }
    }
}

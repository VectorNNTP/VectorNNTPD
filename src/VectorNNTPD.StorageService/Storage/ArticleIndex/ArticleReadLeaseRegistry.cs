using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.StorageService.Storage.ArticleIndex
{

    /// <summary>Tracks active ARTICLE read leases so eviction does not delete an article being served.</summary>
    internal sealed class ArticleReadLeaseRegistry
    {
        private readonly Dictionary<ArticleId, int> _counts = new();

        /// <summary>Gets a lease for <paramref name="articleId"/>. Dispose to release.</summary>
        /// <param name="articleId">Article being read for NNTP delivery.</param>
        /// <returns>A disposable lease handle.</returns>
        internal IDisposable Acquire(ArticleId articleId)
        {
            lock (_counts)
            {
                if (_counts.TryGetValue(articleId, out var count))
                {
                    _counts[articleId] = count + 1;
                }
                else
                {
                    _counts.Add(articleId, 1);
                }
            }

            return new Release(this, articleId);
        }

        /// <summary>Returns whether any caller holds a read lease for <paramref name="articleId"/>.</summary>
        /// <param name="articleId">Canonical article identity.</param>
        /// <returns><see langword="true"/> when an ARTICLE response is in progress.</returns>
        internal bool IsLeased(ArticleId articleId)
        {
            lock (_counts)
            {
                return _counts.TryGetValue(articleId, out var count) && count > 0;
            }
        }

        private void ReleaseLease(ArticleId articleId)
        {
            lock (_counts)
            {
                if (!_counts.TryGetValue(articleId, out var count))
                {
                    return;
                }

                if (count <= 1)
                {
                    _counts.Remove(articleId);
                    return;
                }

                _counts[articleId] = count - 1;
            }
        }

        private sealed class Release(ArticleReadLeaseRegistry owner, ArticleId articleId) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                owner.ReleaseLease(articleId);
            }
        }
    }
}

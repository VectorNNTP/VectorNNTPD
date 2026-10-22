using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.StorageService.Storage.SpoolPersistence
{

    /// <summary>
    /// Serializes publication of one <see cref="ArticleId"/> across every spool.
    /// Unrelated identities do not wait on each other. The slot is removed when nobody is waiting.
    /// An uncontended acquire does not create a wait handle.
    /// </summary>
    internal sealed class SpoolArticleIdentityGate
    {
        private readonly Dictionary<ArticleId, Slot> _slots = new();
        private readonly object _gate = new();

        /// <summary>How many callers are inside <see cref="Acquire"/> for <paramref name="articleId"/>, including the holder.</summary>
        /// <param name="articleId">Canonical article identity.</param>
        /// <returns>The reservation count. Zero when the identity is idle.</returns>
        internal int Reservations(ArticleId articleId)
        {
            lock (_gate)
            {
                return _slots.TryGetValue(articleId, out var slot) ? slot.Reservations : 0;
            }
        }

        /// <summary>
        /// Waits until this caller owns publication of <paramref name="articleId"/>.
        /// Dispose the lease to let the next waiter proceed.
        /// The first caller for an idle identity returns without allocating a wait handle.
        /// </summary>
        /// <param name="articleId">Canonical article identity.</param>
        /// <param name="cancellationToken">Cancels the wait. A cancelled wait does not own the identity.</param>
        /// <returns>A lease that must be disposed once, including when publication fails.</returns>
        internal Lease Acquire(ArticleId articleId, CancellationToken cancellationToken)
        {
            SemaphoreSlim? semaphore = null;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_slots.TryGetValue(articleId, out var slot))
                {
                    _slots[articleId] = new Slot { Reservations = 1 };
                    return new Lease(this, articleId);
                }

                slot.Reservations++;
                slot.Wait ??= new SemaphoreSlim(0, 1);
                _slots[articleId] = slot;
                semaphore = slot.Wait;
            }

            try
            {
                semaphore.Wait(cancellationToken);
            }
            catch
            {
                ReleaseReservation(articleId);
                throw;
            }

            return new Lease(this, articleId);
        }

        /// <summary>Releases the caller's ownership of <paramref name="articleId"/> and wakes one waiter when one remains.</summary>
        /// <param name="articleId">Identity passed to <see cref="Acquire"/>.</param>
        private void Release(ArticleId articleId)
        {
            SemaphoreSlim? wake = null;
            lock (_gate)
            {
                if (!_slots.TryGetValue(articleId, out var slot) || slot.Reservations <= 0)
                {
                    return;
                }

                slot.Reservations--;
                if (slot.Reservations == 0)
                {
                    _slots.Remove(articleId);
                    return;
                }

                wake = slot.Wait;
                _slots[articleId] = slot;
            }

            wake?.Release();
        }

        /// <summary>Drops one reservation that never became the owner. Does not grant a wait permit.</summary>
        /// <param name="articleId">Identity whose waiter count decreases.</param>
        private void ReleaseReservation(ArticleId articleId)
        {
            lock (_gate)
            {
                if (!_slots.TryGetValue(articleId, out var slot) || slot.Reservations <= 0)
                {
                    return;
                }

                slot.Reservations--;
                if (slot.Reservations == 0)
                {
                    _slots.Remove(articleId);
                    return;
                }

                _slots[articleId] = slot;
            }
        }

        /// <summary>Ownership of one article identity. Disposing twice is a no-op.</summary>
        internal sealed class Lease : IDisposable
        {
            private readonly SpoolArticleIdentityGate _owner;
            private readonly ArticleId _articleId;
            private int _disposed;

            /// <summary>Creates a lease that releases <paramref name="articleId"/> on <see cref="Dispose"/>.</summary>
            /// <param name="owner">Gate that issued the lease.</param>
            /// <param name="articleId">Identity this caller owns.</param>
            internal Lease(SpoolArticleIdentityGate owner, ArticleId articleId)
            {
                _owner = owner;
                _articleId = articleId;
            }

            /// <summary>Lets the next waiter for this identity proceed.</summary>
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                _owner.Release(_articleId);
            }
        }

        /// <summary>One identity's in-flight callers. Stored by value and removed when idle.</summary>
        private struct Slot
        {
            /// <summary>Callers that have entered <see cref="Acquire"/> and not yet finished releasing.</summary>
            internal int Reservations;

            /// <summary>
            /// Wake handle for callers that arrived while the identity was already held.
            /// Null when nobody is waiting. Initial count is zero and the maximum count is one,
            /// so each owner grants exactly one following waiter.
            /// </summary>
            internal SemaphoreSlim? Wait;
        }
    }
}

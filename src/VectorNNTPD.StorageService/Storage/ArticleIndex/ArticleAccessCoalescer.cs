using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.StorageService.Storage.ArticleIndex
{

    /// <summary>
    /// Collapses repeated touches for one article into the newest timestamp and a summed access increment while a token command is queued.
    /// </summary>
    /// <remarks>
    /// The map is bounded by <see cref="ArticleIndexSettings.MaxCoalescedKeys"/> keys and
    /// <see cref="ArticleIndexSettings.MaxCoalescedWaitersPerKey"/> extra waiters per key.
    /// A touch that does not fit is queued on its own. The newest timestamp is not visible to eviction
    /// until the writer commits the batch that holds the token. A crash before that commit drops the update.
    /// <c>ReceivedAt</c> is not stored here.
    /// </remarks>
    internal sealed class ArticleAccessCoalescer
    {
        private readonly object _gate = new();
        private readonly Dictionary<ArticleId, Slot> _pending = new();

        /// <summary>
        /// Records a touch. The caller enqueues a token only when this method returns <see langword="true"/>.
        /// </summary>
        /// <param name="articleId">Article being accessed.</param>
        /// <param name="unixMilliseconds">UTC Unix epoch milliseconds of this access.</param>
        /// <param name="completion">Completed when the token's batch commits, including when this call does not enqueue.</param>
        /// <returns><see langword="true"/> when the caller must enqueue <paramref name="completion"/> as the token.</returns>
        internal bool RequiresEnqueue(
            ArticleId articleId,
            long unixMilliseconds,
            TaskCompletionSource<ArticleIndexWriteResult> completion)
        {
            lock (_gate)
            {
                if (_pending.TryGetValue(articleId, out var slot))
                {
                    if (unixMilliseconds > slot.Timestamp)
                    {
                        slot.Timestamp = unixMilliseconds;
                    }

                    slot.AccessIncrements++;
                    if (slot.Count < ArticleIndexSettings.MaxCoalescedWaitersPerKey)
                    {
                        slot.Waiters[slot.Count++] = completion;
                        return false;
                    }

                    return true;
                }

                if (_pending.Count >= ArticleIndexSettings.MaxCoalescedKeys)
                {
                    return true;
                }

                _pending.Add(articleId, new Slot { Timestamp = unixMilliseconds, AccessIncrements = 1 });
                return true;
            }
        }

        /// <summary>Reads the newest pending access time without removing it.</summary>
        /// <param name="articleId">Article being considered for eviction.</param>
        /// <param name="timestamp">Newest queued timestamp when the method returns <see langword="true"/>.</param>
        /// <returns><see langword="false"/> when no touch is waiting for this article.</returns>
        internal bool TryPeek(ArticleId articleId, out long timestamp)
        {
            lock (_gate)
            {
                if (!_pending.TryGetValue(articleId, out var slot))
                {
                    timestamp = 0;
                    return false;
                }

                timestamp = slot.Timestamp;
                return true;
            }
        }

        /// <summary>Removes the pending touch for a token the writer is about to commit.</summary>
        /// <param name="articleId">Token article identity.</param>
        /// <param name="timestamp">Newest timestamp seen for that token, including joined touches.</param>
        /// <param name="accessIncrements">Summed access increments for the token, including joined touches.</param>
        /// <param name="waiters">Joined completions. Null when nobody joined.</param>
        /// <param name="count">Number of valid entries in <paramref name="waiters"/>.</param>
        /// <returns><see langword="false"/> when no pending slot exists.</returns>
        internal bool TryTake(
            ArticleId articleId,
            out long timestamp,
            out int accessIncrements,
            out TaskCompletionSource<ArticleIndexWriteResult>[]? waiters,
            out int count)
        {
            lock (_gate)
            {
                if (!_pending.Remove(articleId, out var slot))
                {
                    timestamp = 0;
                    accessIncrements = 0;
                    waiters = null;
                    count = 0;
                    return false;
                }

                timestamp = slot.Timestamp;
                accessIncrements = slot.AccessIncrements;
                waiters = slot.Count == 0 ? null : slot.Waiters;
                count = slot.Count;
                return true;
            }
        }

        /// <summary>Returns whether a touch is queued for <paramref name="articleId"/>.</summary>
        /// <param name="articleId">Article identity.</param>
        /// <returns><see langword="true"/> when a token or joined touch is pending.</returns>
        internal bool HasPending(ArticleId articleId)
        {
            lock (_gate)
            {
                return _pending.ContainsKey(articleId);
            }
        }

        /// <summary>
        /// Fails waiters joined to a token that never reached the queue.
        /// A slot the writer already took is left alone.
        /// </summary>
        /// <param name="articleId">Token that was not queued.</param>
        /// <param name="exception">Failure presented to joined waiters.</param>
        internal void FailToken(ArticleId articleId, Exception exception)
        {
            TaskCompletionSource<ArticleIndexWriteResult>[]? waiters;
            int count;
            lock (_gate)
            {
                if (!_pending.Remove(articleId, out var slot))
                {
                    return;
                }

                waiters = slot.Waiters;
                count = slot.Count;
            }

            for (var i = 0; i < count; i++)
            {
                waiters![i].TrySetException(exception);
            }
        }

        /// <summary>Fails every joined waiter still held after the writer has stopped.</summary>
        /// <param name="exception">Failure presented to those waiters.</param>
        internal void FailOutstanding(Exception exception)
        {
            List<Slot> slots;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    return;
                }

                slots = new List<Slot>(_pending.Values);
                _pending.Clear();
            }

            foreach (var slot in slots)
            {
                for (var i = 0; i < slot.Count; i++)
                {
                    slot.Waiters[i].TrySetException(exception);
                }
            }
        }

        /// <summary>One in-flight touch token.</summary>
        private sealed class Slot
        {
            /// <summary>Gets or sets the newest access time observed for the token.</summary>
            internal long Timestamp;

            /// <summary>Gets or sets the number of coalesced access increments for the token.</summary>
            internal int AccessIncrements;

            /// <summary>Gets completions that joined this token instead of taking a queue slot.</summary>
            internal readonly TaskCompletionSource<ArticleIndexWriteResult>[] Waiters =
                new TaskCompletionSource<ArticleIndexWriteResult>[ArticleIndexSettings.MaxCoalescedWaitersPerKey];

            /// <summary>Gets or sets the number of joined completions.</summary>
            internal int Count;
        }
    }
}

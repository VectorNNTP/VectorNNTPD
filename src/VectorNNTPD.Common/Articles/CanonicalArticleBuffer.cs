using System.Buffers;
using System.Threading;

namespace VectorNNTPD.Common.Articles
{
    /// <summary>
    /// Owns one canonical article array and the count of live consumers.
    /// </summary>
    /// <remarks>
    /// The creator starts with one reference. Retention adds a reference only when it stores the record.
    /// Each delivery claim adds a reference and releases it when the claim settles.
    /// The array returns to <see cref="Pool"/> only when the last reference is released.
    /// An exact allocation has a null pool, so releasing the last reference leaves the array for the garbage collector.
    /// Article bytes are cleared on return because the pool is process-wide and a later renter must not observe them.
    /// </remarks>
    internal sealed class CanonicalArticleBuffer
    {
        /// <summary>Backing store. Null after a pooled buffer has been returned.</summary>
        private byte[]? _bytes;

        /// <summary>Logical article length. Independent of <see cref="byte"/> array capacity.</summary>
        private readonly int _length;

        /// <summary>1 while the factory or handler still owns the initial reference.</summary>
        private int _creatorHeld = 1;

        /// <summary>Live owners. Starts at one for the creator.</summary>
        private int _references = 1;

        /// <summary>
        /// Stores an exact or rented array. The caller already owns the initial reference.
        /// </summary>
        /// <param name="bytes">Canonical array. A rented array may be longer than <paramref name="length"/>.</param>
        /// <param name="length">Logical article length within <paramref name="bytes"/>.</param>
        /// <param name="pool">Pool that produced <paramref name="bytes"/>, or null for an exact allocation.</param>
        private CanonicalArticleBuffer(byte[] bytes, int length, ArrayPool<byte>? pool)
        {
            _bytes = bytes;
            _length = length;
            Pool = pool;
        }

        /// <summary>Gets the pool that must receive this array, or null when the array is not rented.</summary>
        internal ArrayPool<byte>? Pool { get; }

        /// <summary>Gets the logical article length. This does not change after the array is returned.</summary>
        internal int Length => _length;

        /// <summary>
        /// Gets the logical article bytes. Empty after a pooled array has been returned.
        /// </summary>
        internal ReadOnlyMemory<byte> Memory
        {
            get
            {
                var bytes = _bytes;
                if (bytes is null)
                {
                    return default;
                }

                return _length == bytes.Length
                    ? bytes
                    : new ReadOnlyMemory<byte>(bytes, 0, _length);
            }
        }

        /// <summary>Wraps an exact-size array. A null array produces a null buffer.</summary>
        /// <param name="artData">Exact canonical array, or null.</param>
        /// <returns>A buffer with no pool, or null when <paramref name="artData"/> is null.</returns>
        internal static CanonicalArticleBuffer? Exact(byte[]? artData)
            => artData is null ? null : new CanonicalArticleBuffer(artData, artData.Length, null);

        /// <summary>
        /// Wraps a rented array. <paramref name="length"/> is the article size, not the array capacity.
        /// </summary>
        /// <param name="artData">Array rented from <paramref name="pool"/>.</param>
        /// <param name="length">Logical article length. Greater than zero and no greater than <paramref name="artData"/> capacity.</param>
        /// <param name="pool">Pool that produced <paramref name="artData"/>.</param>
        /// <returns>A buffer whose last release returns <paramref name="artData"/> to <paramref name="pool"/>.</returns>
        internal static CanonicalArticleBuffer Rented(byte[] artData, int length, ArrayPool<byte> pool)
        {
            ArgumentNullException.ThrowIfNull(artData);
            ArgumentNullException.ThrowIfNull(pool);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(length, artData.Length);
            return new CanonicalArticleBuffer(artData, length, pool);
        }

        /// <summary>
        /// Adds one consumer when the count is still above zero.
        /// A count of zero means the pooled array has been returned and must not be revived.
        /// </summary>
        /// <returns><see langword="false"/> when the buffer has no live owner or the count would overflow.</returns>
        private bool TryAddConsumer()
        {
            var observed = Volatile.Read(ref _references);
            while (observed > 0)
            {
                if (observed == int.MaxValue)
                {
                    return false;
                }

                var previous = Interlocked.CompareExchange(ref _references, observed + 1, observed);
                if (previous == observed)
                {
                    return true;
                }

                observed = previous;
            }

            return false;
        }

        /// <summary>
        /// Drops the creator reference when retention did not keep the record.
        /// A second call does nothing. A successful retain still calls this; the retention reference remains.
        /// </summary>
        internal void ReleaseCreator()
        {
            if (Interlocked.Exchange(ref _creatorHeld, 0) == 1)
            {
                ReleaseConsumer();
            }
        }

        /// <summary>
        /// Adds one consumer and returns the lease that releases it.
        /// </summary>
        /// <param name="lease">The new lease when this method returns <see langword="true"/>.</param>
        /// <returns><see langword="false"/> when this buffer has already been returned or the count would overflow.</returns>
        internal bool TryCreateLease([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CanonicalBufferLease? lease) =>
            CanonicalBufferLease.TryCreate(this, out lease);

        /// <summary>
        /// Drops one consumer. The rented array returns to <see cref="Pool"/> exactly once, on the transition to zero.
        /// A release at zero does not decrement further and does not return the array again.
        /// </summary>
        private void ReleaseConsumer()
        {
            var observed = Volatile.Read(ref _references);
            while (observed > 0)
            {
                var updated = observed - 1;
                var previous = Interlocked.CompareExchange(ref _references, updated, observed);
                if (previous == observed)
                {
                    if (updated == 0)
                    {
                        ReturnPooledArray();
                    }

                    return;
                }

                observed = previous;
            }
        }

        /// <summary>Returns the rented array once. Exact allocations are left for the garbage collector.</summary>
        private void ReturnPooledArray()
        {
            if (Pool is null)
            {
                return;
            }

            var bytes = Interlocked.Exchange(ref _bytes, null);
            if (bytes is null)
            {
                return;
            }

            ReturnScope.DeferOrReturn(Pool, bytes);
        }

        /// <summary>
        /// One acquired reference to a <see cref="CanonicalArticleBuffer"/>.
        /// Disposing it releases that reference and no other.
        /// </summary>
        /// <remarks>
        /// Retention and each delivery claim hold their own lease. A copied <see cref="ArticleRecord"/>
        /// cannot release either lease. A second <see cref="Dispose"/> does nothing.
        /// The consumer count itself is not exposed, so another component cannot release this lease.
        /// </remarks>
        internal sealed class CanonicalBufferLease
        {
            /// <summary>Buffer whose consumer reference this lease owns. Null after <see cref="Dispose"/>.</summary>
            private CanonicalArticleBuffer? _buffer;

            /// <summary>Stores the buffer after <see cref="TryAddConsumer"/> has succeeded.</summary>
            /// <param name="buffer">Live buffer.</param>
            private CanonicalBufferLease(CanonicalArticleBuffer buffer)
            {
                _buffer = buffer;
            }

            /// <summary>
            /// Adds one consumer on <paramref name="buffer"/> and returns the lease that releases it.
            /// </summary>
            /// <param name="buffer">Buffer that still has a live owner.</param>
            /// <param name="lease">The new lease when this method returns <see langword="true"/>.</param>
            /// <returns><see langword="false"/> when <paramref name="buffer"/> has already been returned or the count would overflow.</returns>
            internal static bool TryCreate(
                CanonicalArticleBuffer buffer,
                [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CanonicalBufferLease? lease)
            {
                lease = null;
                if (!buffer.TryAddConsumer())
                {
                    return false;
                }

                try
                {
                    lease = new CanonicalBufferLease(buffer);
                    return true;
                }
                catch
                {
                    buffer.ReleaseConsumer();
                    throw;
                }
            }

            /// <summary>Releases this lease's consumer once.</summary>
            internal void Dispose()
            {
                var buffer = Interlocked.Exchange(ref _buffer, null);
                buffer?.ReleaseConsumer();
            }
        }

        /// <summary>
        /// Defers pool returns that happen inside a retention lock until the scope is disposed outside that lock.
        /// </summary>
        /// <remarks>
        /// Clearing a canonical array under the retention gate would walk the article while every other
        /// admission and claim waits. Disposal runs after the lock is released.
        /// A release with no active scope returns immediately. That is the claim and handler path.
        /// <see cref="Enter"/> and <see cref="Dispose"/> stay on one call.
        /// <c>RetainCanonical</c>, <c>TryClaimForDelivery</c>, <c>SweepExpired</c>, and <c>DisposeAsync</c>
        /// do not await, schedule work, or take a callback that enters another scope while one is active.
        /// A nested scope does not return arrays. The outermost <see cref="Dispose"/> does, after that call's lock is released.
        /// One throwing <see cref="ArrayPool{T}.Return(T[], bool)"/> does not skip the rest of the batch.
        /// </remarks>
        internal readonly ref struct ReturnScope
        {
            /// <summary>Arrays waiting to return on this thread.</summary>
            [ThreadStatic]
            private static List<(ArrayPool<byte> Pool, byte[] Bytes)>? _pending;

            /// <summary>Nested scopes on this thread. The outermost disposal returns the queued arrays.</summary>
            [ThreadStatic]
            private static int _depth;

            /// <summary>Starts a scope. Dispose it after the retention lock is released.</summary>
            /// <returns>A scope whose disposal returns every array queued on this thread.</returns>
            internal static ReturnScope Enter()
            {
                _depth++;
                return default;
            }

            /// <summary>Returns queued arrays when this is the outermost scope.</summary>
            public void Dispose()
            {
                if (_depth == 0)
                {
                    return;
                }

                _depth--;
                if (_depth > 0)
                {
                    return;
                }

                var pending = _pending;
                if (pending is null || pending.Count == 0)
                {
                    return;
                }

                var taken = pending.ToArray();
                pending.Clear();
                Exception? first = null;
                foreach (var (pool, bytes) in taken)
                {
                    try
                    {
                        pool.Return(bytes, clearArray: true);
                    }
                    catch (Exception ex)
                    {
                        first ??= ex;
                    }
                }

                if (first is not null)
                {
                    throw first;
                }
            }

            /// <summary>
            /// Queues <paramref name="bytes"/> while a scope is active. Otherwise returns it now.
            /// </summary>
            /// <param name="pool">Pool that produced <paramref name="bytes"/>.</param>
            /// <param name="bytes">Array whose last consumer has released it.</param>
            internal static void DeferOrReturn(ArrayPool<byte> pool, byte[] bytes)
            {
                if (_depth > 0)
                {
                    _pending ??= [];
                    _pending.Add((pool, bytes));
                    return;
                }

                pool.Return(bytes, clearArray: true);
            }
        }
    }
}

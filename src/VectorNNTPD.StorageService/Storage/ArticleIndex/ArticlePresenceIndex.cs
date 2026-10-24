using System.Buffers.Binary;
using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.StorageService.Storage.ArticleIndex
{

    /// <summary>
    /// Sharded open-addressed set of 32-byte article hashes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each shard stores keys in one <see cref="byte"/> array and occupancy in a bitset.
    /// There is no per-article object and no hexadecimal key. Equality compares all 32 bytes.
    /// The slot is chosen from the first 8 bytes; those 8 bytes are not the identity.
    /// </para>
    /// <para>
    /// Lookups read a published table without taking the shard lock. Inserts take the shard lock.
    /// Growth replaces one shard with a table about 25 percent larger. The previous table stays
    /// reachable to a lookup that already loaded it, so a resize does not allocate a second copy
    /// of every shard.
    /// </para>
    /// <para>
    /// Eviction removes a hash only after the spool file and the durable index row are deleted together.
    /// </para>
    /// </remarks>
    internal sealed class ArticlePresenceIndex
    {
        /// <summary>Number of independent tables. A lookup locks none of them.</summary>
        internal const int ShardCount = 64;

        private readonly Shard[] _shards = new Shard[ShardCount];
        private long _count;

        /// <summary>Initializes an empty index.</summary>
        internal ArticlePresenceIndex()
        {
            for (var i = 0; i < _shards.Length; i++)
            {
                _shards[i] = new Shard();
            }
        }

        /// <summary>Gets the number of hashes published into the set.</summary>
        internal long Count => Volatile.Read(ref _count);

        /// <summary>
        /// Gets the backing-array size plus a small per-shard allowance.
        /// This is not a measured process working set and it is not only the raw 32-byte payload.
        /// </summary>
        internal long EstimatedBytes
        {
            get
            {
                long bytes = 0;
                for (var i = 0; i < _shards.Length; i++)
                {
                    bytes += _shards[i].EstimatedBytes;
                }

                return bytes;
            }
        }

        /// <summary>Gets the largest shard capacity. Tests use this to bound growth.</summary>
        internal int MaxShardCapacity
        {
            get
            {
                var max = 0;
                for (var i = 0; i < _shards.Length; i++)
                {
                    var capacity = _shards[i].Capacity;
                    if (capacity > max)
                    {
                        max = capacity;
                    }
                }

                return max;
            }
        }

        /// <summary>
        /// Allocates empty slot capacity for an expected article count.
        /// Call this before inserts. A later shard that exceeds the estimate grows on its own.
        /// </summary>
        /// <param name="articleCount">Expected hashes. Zero leaves the tables empty.</param>
        internal void Reserve(long articleCount)
        {
            if (articleCount <= 0)
            {
                return;
            }

            var perShard = (articleCount + ShardCount - 1) / ShardCount;
            perShard += perShard / 10;
            var capacity = SlotsFor(perShard);
            for (var i = 0; i < _shards.Length; i++)
            {
                _shards[i].Reserve(capacity);
            }
        }

        /// <summary>Returns whether the hash is present. Does not lock.</summary>
        /// <param name="articleId">Canonical article identity.</param>
        /// <returns><see langword="true"/> when the hash has been published.</returns>
        internal bool Contains(ArticleId articleId)
        {
            Span<byte> hash = stackalloc byte[ArticleIndexSettings.HashLength];
            articleId.CopyTo(hash);
            return _shards[ShardIndex(hash)].Contains(hash);
        }

        /// <summary>Publishes a hash. A duplicate is left in place.</summary>
        /// <param name="articleId">Canonical article identity.</param>
        /// <returns><see langword="true"/> when this call inserted the hash.</returns>
        internal bool Add(ArticleId articleId)
        {
            Span<byte> hash = stackalloc byte[ArticleIndexSettings.HashLength];
            articleId.CopyTo(hash);
            if (!_shards[ShardIndex(hash)].Add(hash))
            {
                return false;
            }

            Interlocked.Increment(ref _count);
            return true;
        }

        /// <summary>Removes a published hash after eviction deleted its row and file.</summary>
        /// <param name="articleId">Canonical article identity.</param>
        /// <returns><see langword="true"/> when the hash was present.</returns>
        internal bool Remove(ArticleId articleId)
        {
            Span<byte> hash = stackalloc byte[ArticleIndexSettings.HashLength];
            articleId.CopyTo(hash);
            if (!_shards[ShardIndex(hash)].Remove(hash))
            {
                return false;
            }

            Interlocked.Decrement(ref _count);
            return true;
        }

        /// <summary>Next capacity for a shard that has reached its load factor.</summary>
        /// <param name="capacity">Current slot count. Zero selects the first table.</param>
        /// <returns>The new slot count. Large tables grow by about one quarter, not by doubling.</returns>
        internal static int GrowCapacity(int capacity)
        {
            if (capacity <= 0)
            {
                return 16;
            }

            var step = Math.Max(16, capacity / 4);
            var grown = (long)capacity + step;
            if (grown > Array.MaxLength / ArticleIndexSettings.HashLength)
            {
                throw new InvalidOperationException("Article membership shard cannot grow without exceeding the maximum array length.");
            }

            return (int)grown;
        }

        /// <summary>Slot count that can hold <paramref name="count"/> hashes at a 0.75 load factor.</summary>
        /// <param name="count">Hashes expected in one shard.</param>
        /// <returns>Slot count, or zero when <paramref name="count"/> is zero.</returns>
        private static int SlotsFor(long count)
        {
            if (count <= 0)
            {
                return 0;
            }

            var slots = (count * 4 + 2) / 3;
            if (slots > Array.MaxLength / ArticleIndexSettings.HashLength)
            {
                throw new InvalidOperationException("Article membership reserve exceeds the maximum array length.");
            }

            return (int)Math.Max(16, slots);
        }

        /// <summary>Selects a shard from the first digest byte.</summary>
        /// <param name="hash">32-byte hash.</param>
        /// <returns>Shard index in <c>0..63</c>.</returns>
        private static int ShardIndex(ReadOnlySpan<byte> hash) => hash[0] & (ShardCount - 1);

        /// <summary>One open-addressed table. The published instance is replaced as a unit when it grows.</summary>
        private sealed class Shard
        {
            private readonly object _gate = new();
            private Table _table = Table.Empty;

            /// <summary>Gets the published slot count.</summary>
            internal int Capacity => Volatile.Read(ref _table).Capacity;

            /// <summary>Gets the published backing-store estimate.</summary>
            internal long EstimatedBytes => Volatile.Read(ref _table).EstimatedBytes;

            /// <summary>Replaces an empty table with <paramref name="capacity"/> slots.</summary>
            /// <param name="capacity">Slot count. Ignored when the shard already holds keys or slots.</param>
            internal void Reserve(int capacity)
            {
                if (capacity <= 0)
                {
                    return;
                }

                lock (_gate)
                {
                    if (_table.Capacity == 0 && _table.Count == 0)
                    {
                        Volatile.Write(ref _table, Table.Create(capacity));
                    }
                }
            }

            /// <summary>Lock-free membership test against the table reference loaded at the start.</summary>
            /// <param name="hash">32-byte hash.</param>
            /// <returns><see langword="true"/> when the hash is in that table.</returns>
            internal bool Contains(ReadOnlySpan<byte> hash)
            {
                var table = Volatile.Read(ref _table);
                return table.Contains(hash);
            }

            /// <summary>Inserts a hash, growing this shard first when the load factor would be exceeded.</summary>
            /// <param name="hash">32-byte hash. The bytes are copied.</param>
            /// <returns><see langword="false"/> when the hash was already present.</returns>
            internal bool Add(ReadOnlySpan<byte> hash)
            {
                lock (_gate)
                {
                    var table = _table;
                    if (table.Contains(hash))
                    {
                        return false;
                    }

                    if (table.NeedsGrowth)
                    {
                        var grown = Table.Create(GrowCapacity(table.Capacity));
                        grown.Absorb(table);
                        if (!grown.TryAdd(hash))
                        {
                            throw new InvalidOperationException("Article membership insert failed after the shard grew.");
                        }

                        Volatile.Write(ref _table, grown);
                        return true;
                    }

                    if (!table.TryAdd(hash))
                    {
                        throw new InvalidOperationException("Article membership insert failed in a shard that had free slots.");
                    }

                    return true;
                }
            }

            /// <summary>Removes one hash by rebuilding the shard table without it.</summary>
            /// <param name="hash">32-byte hash.</param>
            /// <returns><see langword="false"/> when the hash was absent.</returns>
            internal bool Remove(ReadOnlySpan<byte> hash)
            {
                lock (_gate)
                {
                    if (!_table.TryRemove(hash, out var rebuilt))
                    {
                        return false;
                    }

                    Volatile.Write(ref _table, rebuilt);
                    return true;
                }
            }
        }

        /// <summary>Immutable-capacity key array. Occupied slots are published with a release write on the bit word.</summary>
        private sealed class Table
        {
            /// <summary>Empty table. It has no backing arrays.</summary>
            internal static readonly Table Empty = new(0, [], []);

            private readonly byte[] _keys;
            private readonly ulong[] _occupied;

            private Table(int capacity, byte[] keys, ulong[] occupied)
            {
                Capacity = capacity;
                _keys = keys;
                _occupied = occupied;
            }

            /// <summary>Gets the slot count.</summary>
            internal int Capacity { get; }

            /// <summary>Gets the number of occupied slots. Writers update it before publishing the table.</summary>
            internal int Count { get; private set; }

            /// <summary>Gets a value indicating that another insert must grow the shard.</summary>
            internal bool NeedsGrowth =>
                Capacity == 0 || ((long)Count + 1) * 4 > (long)Capacity * 3;

            /// <summary>Gets key bytes, occupancy bits, and a fixed object allowance.</summary>
            internal long EstimatedBytes =>
                _keys.LongLength + (_occupied.LongLength * sizeof(ulong)) + 128;

            /// <summary>Allocates an empty table.</summary>
            /// <param name="capacity">Slot count. Must be positive and must fit in one byte array.</param>
            /// <returns>The empty table.</returns>
            internal static Table Create(int capacity)
            {
                if (capacity <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(capacity));
                }

                var bytes = (long)capacity * ArticleIndexSettings.HashLength;
                if (bytes > Array.MaxLength)
                {
                    throw new InvalidOperationException("Article membership table exceeds the maximum array length.");
                }

                var words = (capacity + 63) / 64;
                return new Table(capacity, new byte[bytes], new ulong[words]);
            }

            /// <summary>Copies occupied keys from <paramref name="source"/>.</summary>
            /// <param name="source">Previous shard table.</param>
            internal void Absorb(Table source)
            {
                if (source.Count == 0)
                {
                    return;
                }

                for (var index = 0; index < source.Capacity; index++)
                {
                    if (!source.IsOccupied(index))
                    {
                        continue;
                    }

                    if (!TryAdd(source._keys.AsSpan(index * ArticleIndexSettings.HashLength, ArticleIndexSettings.HashLength)))
                    {
                        throw new InvalidOperationException("Article membership growth could not place an existing hash.");
                    }
                }
            }

            /// <summary>Probes for <paramref name="hash"/> without taking a lock.</summary>
            /// <param name="hash">32-byte hash.</param>
            /// <returns><see langword="true"/> when every byte matches an occupied slot.</returns>
            internal bool Contains(ReadOnlySpan<byte> hash)
            {
                if (Capacity == 0 || hash.Length != ArticleIndexSettings.HashLength)
                {
                    return false;
                }

                var start = Slot(hash);
                for (var step = 0; step < Capacity; step++)
                {
                    var index = start + step;
                    if (index >= Capacity)
                    {
                        index -= Capacity;
                    }

                    if (!IsOccupied(index))
                    {
                        return false;
                    }

                    if (KeyEquals(index, hash))
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>Inserts into an empty slot. The caller holds the shard lock and has checked for a duplicate.</summary>
            /// <param name="hash">32-byte hash.</param>
            /// <returns><see langword="false"/> when the hash is already present or the table has no empty slot.</returns>
            internal bool TryAdd(ReadOnlySpan<byte> hash)
            {
                if (Capacity == 0 || hash.Length != ArticleIndexSettings.HashLength)
                {
                    return false;
                }

                var start = Slot(hash);
                for (var step = 0; step < Capacity; step++)
                {
                    var index = start + step;
                    if (index >= Capacity)
                    {
                        index -= Capacity;
                    }

                    if (!IsOccupied(index))
                    {
                        hash.CopyTo(_keys.AsSpan(index * ArticleIndexSettings.HashLength, ArticleIndexSettings.HashLength));
                        var word = index >> 6;
                        var updated = _occupied[word] | (1UL << (index & 63));
                        Volatile.Write(ref _occupied[word], updated);
                        Count++;
                        return true;
                    }

                    if (KeyEquals(index, hash))
                    {
                        return false;
                    }
                }

                return false;
            }

            /// <summary>Removes one hash by rebuilding a table that omits it.</summary>
            /// <param name="hash">32-byte hash.</param>
            /// <param name="rebuilt">Replacement table when the method returns <see langword="true"/>.</param>
            /// <returns><see langword="false"/> when the hash was absent.</returns>
            internal bool TryRemove(ReadOnlySpan<byte> hash, out Table rebuilt)
            {
                rebuilt = this;
                if (!Contains(hash))
                {
                    return false;
                }

                if (Count <= 1)
                {
                    rebuilt = Create(Math.Max(16, Capacity));
                    return true;
                }

                var next = Create(Capacity);
                for (var index = 0; index < Capacity; index++)
                {
                    if (!IsOccupied(index))
                    {
                        continue;
                    }

                    var candidate = _keys.AsSpan(index * ArticleIndexSettings.HashLength, ArticleIndexSettings.HashLength);
                    if (candidate.SequenceEqual(hash))
                    {
                        continue;
                    }

                    if (!next.TryAdd(candidate))
                    {
                        throw new InvalidOperationException("Article membership rebuild failed after eviction.");
                    }
                }

                rebuilt = next;
                return true;
            }

            /// <summary>Reads one occupancy bit with acquire semantics.</summary>
            /// <param name="index">Slot index.</param>
            /// <returns><see langword="true"/> when the slot holds a hash.</returns>
            internal bool IsOccupied(int index)
            {
                var word = Volatile.Read(ref _occupied[index >> 6]);
                return (word & (1UL << (index & 63))) != 0;
            }

            /// <summary>Compares all 32 stored bytes.</summary>
            /// <param name="index">Occupied slot.</param>
            /// <param name="hash">Candidate hash.</param>
            /// <returns><see langword="true"/> when the bytes match.</returns>
            private bool KeyEquals(int index, ReadOnlySpan<byte> hash) =>
                _keys.AsSpan(index * ArticleIndexSettings.HashLength, ArticleIndexSettings.HashLength).SequenceEqual(hash);

            /// <summary>First probe slot from the leading 8 digest bytes.</summary>
            /// <param name="hash">32-byte hash.</param>
            /// <returns>A slot in range.</returns>
            private int Slot(ReadOnlySpan<byte> hash)
            {
                var mixed = BinaryPrimitives.ReadUInt64LittleEndian(hash);
                return (int)(mixed % (ulong)Capacity);
            }
        }
    }
}

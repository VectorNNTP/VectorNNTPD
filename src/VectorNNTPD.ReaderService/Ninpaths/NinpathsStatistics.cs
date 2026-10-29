using System.Text;

namespace VectorNNTPD.ReaderService.Ninpaths
{
    /// <summary>
    /// Compact INN ninpaths aggregation: unique sites and pairwise Path relations.
    /// </summary>
    /// <remarks>
    /// Memory is O(unique sites + unique directed relations), not O(articles).
    /// Raw Path observations are not retained. Hash-table layout matches INN
    /// <c>hosthash</c> (FNV-style hash, chain prepend, dump numbering).
    /// </remarks>
    public sealed class NinpathsStatistics
    {
        private readonly NinpathsSite?[] _buckets = new NinpathsSite?[NinpathsConstants.HashTableSize];
        private long _sites;
        private long _total;

        /// <summary>Gets the number of Path observations that INN would count in <c>total</c>.</summary>
        public long TotalArticles => _total;

        /// <summary>Gets the number of unique interned sites.</summary>
        public long SiteCount => _sites;

        /// <summary>
        /// Applies one Path value after INN last-element chopping (may be empty).
        /// Does not increment <see cref="TotalArticles"/>; the reader does that
        /// for valid lines even when this method stops early on an overlong token.
        /// </summary>
        internal void AddChoppedPath(ReadOnlySpan<byte> chopped)
        {
            NinpathsSite? previous = null;
            var remaining = chopped;
            while (!remaining.IsEmpty)
            {
                var bang = remaining.IndexOf((byte)'!');
                ReadOnlySpan<byte> token;
                if (bang < 0)
                {
                    token = remaining;
                    remaining = default;
                }
                else
                {
                    token = remaining[..bang];
                    remaining = remaining[(bang + 1)..];
                    while (!remaining.IsEmpty && remaining[0] == (byte)'!')
                    {
                        remaining = remaining[1..];
                    }
                }

                if (token.Length > NinpathsConstants.MaxHostChars)
                {
                    return;
                }

                var site = GetOrAdd(token);
                site.SentTo++;
                if (previous is not null && !ReferenceEquals(previous, site))
                {
                    IncrementRelation(previous, site);
                }

                previous = site;
            }
        }

        /// <summary>Counts one valid Path line (INN <c>++total</c>).</summary>
        internal void AddValidArticle() => _total++;

        internal NinpathsSite?[] Buckets => _buckets;

        /// <summary>Looks up INN <c>sentto</c> for a site id, using case-sensitive <c>strcmp</c> semantics.</summary>
        public bool TryGetSentTo(ReadOnlySpan<byte> siteId, out long sentTo)
        {
            var site = Find(siteId);
            if (site is null)
            {
                sentTo = 0;
                return false;
            }

            sentTo = site.SentTo;
            return true;
        }

        /// <summary>Looks up the directed relation tally from <paramref name="from"/> to <paramref name="to"/>.</summary>
        public bool TryGetRelation(ReadOnlySpan<byte> from, ReadOnlySpan<byte> to, out long tally)
        {
            tally = 0;
            var source = Find(from);
            var target = Find(to);
            if (source is null || target is null)
            {
                return false;
            }

            for (var rel = source.Relations; rel is not null; rel = rel.Next)
            {
                if (ReferenceEquals(rel.Target, target))
                {
                    tally = rel.Tally;
                    return true;
                }
            }

            return false;
        }

        /// <summary>INN <c>hash()</c> over a NUL-free token (FNV-style).</summary>
        internal static int Hash(ReadOnlySpan<byte> str)
        {
            ulong val = 0;
            for (var i = 0; i < str.Length; i++)
            {
                val *= NinpathsConstants.HashPrime;
                val ^= str[i];
            }

            return (int)(val & (NinpathsConstants.HashTableSize - 1));
        }

        internal NinpathsSite? Find(ReadOnlySpan<byte> token)
        {
            var index = Hash(token);
            for (var site = _buckets[index]; site is not null; site = site.BucketNext)
            {
                if (token.SequenceEqual(site.Id))
                {
                    return site;
                }
            }

            return null;
        }

        /// <summary>Latin-1 helper for tests that start from managed strings.</summary>
        public bool TryGetSentTo(string siteId, out long sentTo)
        {
            ArgumentNullException.ThrowIfNull(siteId);
            return TryGetSentTo(Encoding.Latin1.GetBytes(siteId), out sentTo);
        }

        /// <summary>Latin-1 helper for tests that start from managed strings.</summary>
        public bool TryGetRelation(string from, string to, out long tally)
        {
            ArgumentNullException.ThrowIfNull(from);
            ArgumentNullException.ThrowIfNull(to);
            return TryGetRelation(Encoding.Latin1.GetBytes(from), Encoding.Latin1.GetBytes(to), out tally);
        }

        private NinpathsSite GetOrAdd(ReadOnlySpan<byte> token)
        {
            var index = Hash(token);
            for (var site = _buckets[index]; site is not null; site = site.BucketNext)
            {
                if (token.SequenceEqual(site.Id))
                {
                    return site;
                }
            }

            var created = new NinpathsSite
            {
                Id = token.ToArray(),
                BucketNext = _buckets[index],
            };
            _buckets[index] = created;
            _sites++;
            return created;
        }

        private static void IncrementRelation(NinpathsSite from, NinpathsSite to)
        {
            for (var rel = from.Relations; rel is not null; rel = rel.Next)
            {
                if (ReferenceEquals(rel.Target, to))
                {
                    rel.Tally++;
                    return;
                }
            }

            from.Relations = new NinpathsRelation
            {
                Target = to,
                Tally = 1,
                Next = from.Relations,
            };
        }
    }

    /// <summary>One interned Path hop (INN <c>nrec</c>).</summary>
    internal sealed class NinpathsSite
    {
        public NinpathsSite? BucketNext;

        public NinpathsRelation? Relations;

        public byte[] Id { get; set; } = [];

        public long Number;

        public long SentTo;
    }

    /// <summary>One directed relation (INN <c>trec</c>).</summary>
    internal sealed class NinpathsRelation
    {
        public NinpathsRelation? Next;

        public NinpathsSite Target { get; set; } = null!;

        public long Tally;
    }
}

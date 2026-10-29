using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.ReaderService.History
{
    /// <summary>32-byte BLAKE3 digest of a Message-ID's protocol bytes.</summary>
    /// <remarks>
    /// Delegates to Common <see cref="ArticleId"/> so History and ArticleRecord share
    /// the same BLAKE3(Message-ID) identity bytes.
    /// </remarks>
    public readonly struct HistoryDigest : IEquatable<HistoryDigest>
    {
        /// <summary>BLAKE3 output length in bytes.</summary>
        public const int Length = ArticleId.Length;

        private readonly ArticleId _id;

        private HistoryDigest(ArticleId id)
        {
            _id = id;
        }

        /// <summary>Computes the HistoryDB digest from Message-ID wire octets.</summary>
        public static HistoryDigest FromMessageId(ReadOnlySpan<byte> messageId)
            => new(ArticleId.FromMessageId(messageId));

        /// <summary>Creates a digest from an existing 32-byte BLAKE3 output.</summary>
        public static HistoryDigest FromSpan(ReadOnlySpan<byte> digest)
        {
            if (digest.Length != Length)
            {
                throw new ArgumentException($"History digest must be {Length} bytes.", nameof(digest));
            }

            return new HistoryDigest(ArticleId.FromSpan(digest));
        }

        /// <summary>Copies the digest bytes into <paramref name="destination"/>.</summary>
        public void CopyTo(Span<byte> destination)
        {
            if (destination.Length < Length)
            {
                throw new ArgumentException("Destination is shorter than the digest.", nameof(destination));
            }

            _id.CopyTo(destination);
        }

        /// <inheritdoc />
        public bool Equals(HistoryDigest other) => _id.Equals(other._id);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is HistoryDigest other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => _id.GetHashCode();

        /// <summary>Equality operator.</summary>
        public static bool operator ==(HistoryDigest left, HistoryDigest right) => left.Equals(right);

        /// <summary>Inequality operator.</summary>
        public static bool operator !=(HistoryDigest left, HistoryDigest right) => !left.Equals(right);
    }
}

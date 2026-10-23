using System.Text;
using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.StorageService.Tests.Support.Placement
{
    /// <summary>Observable result of one single-copy placement attempt.</summary>
    public enum ArticlePlacementKind : byte
    {
        /// <summary>Server accepted the article (<c>239</c>).</summary>
        Accepted = 1,

        /// <summary>Identical article already represented (mapped from reject when duplicate is known).</summary>
        Duplicate = 2,

        /// <summary>Conflicting identity. Unused for NNTP TAKETHIS; retained for enum stability.</summary>
        Conflict = 3,

        /// <summary>Temporary pressure / capacity rejection.</summary>
        RejectedPressure = 4,

        /// <summary>Canonical or integrity rejection (<c>439</c>).</summary>
        RejectedInvalid = 5,

        /// <summary>Capacity rejection (<c>439</c> when ArtSize exceeds queue budget).</summary>
        RejectedCapacity = 6,

        /// <summary>No eligible registry entry. No dial.</summary>
        NoActiveServer = 7,

        /// <summary>Connect, TLS, protocol, or I/O failure before the article was fully written.</summary>
        TransportFailure = 8,

        /// <summary>Caller cancelled before the server could accept.</summary>
        Cancelled = 9,

        /// <summary>Article was written and no status line was observed. The queue may already hold it.</summary>
        AcknowledgementNotObserved = 10,
    }

    /// <summary>One finished placement attempt.</summary>
    /// <param name="Kind">Classification.</param>
    /// <param name="Failure">Transport or protocol failure name. Never article bytes.</param>
    public readonly record struct ArticlePlacementResult(ArticlePlacementKind Kind, string? Failure = null)
    {
        /// <summary>Gets whether the StorageServer accepted responsibility or already held the same article.</summary>
        public bool Succeeded => Kind is ArticlePlacementKind.Accepted or ArticlePlacementKind.Duplicate;

        /// <summary>Maps an NNTP status code from a TAKETHIS response.</summary>
        public static ArticlePlacementResult FromNntpStatus(int code) => code switch
        {
            239 => new(ArticlePlacementKind.Accepted),
            439 => new(ArticlePlacementKind.RejectedInvalid),
            400 => new(ArticlePlacementKind.RejectedPressure, "temporary-failure"),
            _ => new(ArticlePlacementKind.TransportFailure, "status-" + code.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
    }

    /// <summary>Selects one placement target from an already-active registry snapshot.</summary>
    public static class StorageServerPlacementSelector
    {
        /// <summary>
        /// Selects the eligible entry with the greatest <see cref="StorageServerFleetEntry.AvailableBytes"/>,
        /// then the lowest <see cref="StorageServerFleetEntry.ServerId"/>, then ordinal FQDN.
        /// </summary>
        public static bool TrySelect(IReadOnlyList<StorageServerFleetEntry> active, out StorageServerFleetEntry selected)
        {
            ArgumentNullException.ThrowIfNull(active);
            selected = default;
            var found = false;
            foreach (var entry in active)
            {
                if (entry.VatpPort is not (>= 1 and <= 65535))
                {
                    continue;
                }

                if (!found || Prefer(entry, selected))
                {
                    selected = entry;
                    found = true;
                }
            }

            return found;
        }

        private static bool Prefer(in StorageServerFleetEntry candidate, in StorageServerFleetEntry current)
        {
            if (candidate.AvailableBytes != current.AvailableBytes)
            {
                return candidate.AvailableBytes > current.AvailableBytes;
            }

            if (candidate.ServerId != current.ServerId)
            {
                return candidate.ServerId < current.ServerId;
            }

            return string.CompareOrdinal(candidate.Fqdn, current.Fqdn) < 0;
        }
    }

    /// <summary>Extracts the Message-ID value octets from a CanonicalV1 record for TAKETHIS.</summary>
    public static class ArticlePlacementMessageId
    {
        /// <summary>Copies the Message-ID header value (including angle brackets) from <paramref name="record"/>.</summary>
        public static bool TryCopy(in ArticleRecord record, out byte[] messageId)
        {
            messageId = [];
            if (record.ParseStatus != ArticleParseStatus.CanonicalV1 || record.ArtSize <= 0)
            {
                return false;
            }

            var value = record.MessageId;
            if (value.IsEmpty)
            {
                return false;
            }

            messageId = value.ToArray();
            return true;
        }

        /// <summary>ASCII Message-ID for logging only.</summary>
        public static string ToAscii(ReadOnlySpan<byte> messageId) => Encoding.ASCII.GetString(messageId);
    }
}

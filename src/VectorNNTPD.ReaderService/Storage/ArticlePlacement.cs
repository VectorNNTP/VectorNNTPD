using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Transport.ArticleTransfer;

namespace VectorNNTPD.ReaderService.Storage
{
    /// <summary>Observable result of one single-copy placement attempt.</summary>
    public enum ArticlePlacementKind : byte
    {
        /// <summary>Journal accept completed. Matches <c>ArticleAcceptOutcome.Accepted</c> (1).</summary>
        Accepted = 1,

        /// <summary>Identical article already represented. Matches <c>ArticleAcceptOutcome.Duplicate</c> (2).</summary>
        Duplicate = 2,

        /// <summary>Conflicting ArtHash or ArtSize. Matches <c>ArticleAcceptOutcome.Conflict</c> (3).</summary>
        Conflict = 3,

        /// <summary>Journal pressure rejection. Matches <c>ArticleAcceptOutcome.RejectedPressure</c> (4).</summary>
        RejectedPressure = 4,

        /// <summary>Canonical or integrity rejection. Matches <c>ArticleAcceptOutcome.RejectedInvalid</c> (5).</summary>
        RejectedInvalid = 5,

        /// <summary>Capacity rejection. Matches <c>ArticleAcceptOutcome.RejectedCapacity</c> (6).</summary>
        RejectedCapacity = 6,

        /// <summary>No eligible registry entry. No dial.</summary>
        NoActiveServer = 7,

        /// <summary>Connect, TLS, protocol FAIL, or I/O failure before END was fully written.</summary>
        TransportFailure = 8,

        /// <summary>Caller cancelled before the server could accept.</summary>
        Cancelled = 9,

        /// <summary>END was written and RESULT was not observed. The journal may already hold the article.</summary>
        AcknowledgementNotObserved = 10,
    }

    /// <summary>One finished placement attempt.</summary>
    /// <param name="Kind">Classification.</param>
    /// <param name="Failure">Transport or protocol failure name. Never article bytes.</param>
    public readonly record struct ArticlePlacementResult(ArticlePlacementKind Kind, string? Failure = null)
    {
        /// <summary>Gets whether the StorageServer accepted responsibility or already held the same article.</summary>
        public bool Succeeded => Kind is ArticlePlacementKind.Accepted or ArticlePlacementKind.Duplicate;

        /// <summary>Maps a RESULT outcome byte. Unknown bytes are transport failures.</summary>
        public static ArticlePlacementResult FromOutcomeByte(byte outcome) => outcome switch
        {
            (byte)ArticlePlacementKind.Accepted => new(ArticlePlacementKind.Accepted),
            (byte)ArticlePlacementKind.Duplicate => new(ArticlePlacementKind.Duplicate),
            (byte)ArticlePlacementKind.Conflict => new(ArticlePlacementKind.Conflict),
            (byte)ArticlePlacementKind.RejectedPressure => new(ArticlePlacementKind.RejectedPressure),
            (byte)ArticlePlacementKind.RejectedInvalid => new(ArticlePlacementKind.RejectedInvalid),
            (byte)ArticlePlacementKind.RejectedCapacity => new(ArticlePlacementKind.RejectedCapacity),
            _ => new(ArticlePlacementKind.TransportFailure, "result-outcome"),
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

    /// <summary>Builds VATP META from a CanonicalV1 record.</summary>
    public static class ArticlePlacementMeta
    {
        private static readonly NntpArticleHeaderName[] DateHeaders =
        [
            NntpArticleHeaderName.Date,
            NntpArticleHeaderName.InjectionDate,
            NntpArticleHeaderName.NntpPostingDate,
            NntpArticleHeaderName.Posted,
            NntpArticleHeaderName.XDate,
            NntpArticleHeaderName.DeliveryDate,
        ];

        /// <summary>
        /// Recovers the winning Date-family header by locating fields that match the record,
        /// then builds META with <see cref="ArticleCanonicalTransferMeta.FromRecord"/>.
        /// </summary>
        public static bool TryFromRecord(in ArticleRecord record, out ArticleCanonicalTransferMeta meta)
        {
            meta = default;
            if (record.ParseStatus != ArticleParseStatus.CanonicalV1 || record.ArtSize <= 0)
            {
                return false;
            }

            var artData = record.ArtData.Span;
            foreach (var name in DateHeaders)
            {
                if (ArticleFieldTable.Locate(artData, name).Equals(record.Fields))
                {
                    meta = ArticleCanonicalTransferMeta.FromRecord(in record, name);
                    return true;
                }
            }

            return false;
        }
    }
}

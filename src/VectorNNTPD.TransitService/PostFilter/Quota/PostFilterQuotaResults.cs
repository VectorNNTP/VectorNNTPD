namespace VectorNNTPD.TransitService.PostFilter.Quota
{
    /// <summary>Integer codes returned by the <c>RESERVE</c> EVAL (and the matching engine).</summary>
    internal static class PostFilterQuotaReserveCodes
    {
        /// <summary>Accepted, including an idempotent live-token hit.</summary>
        public const long Accept = 0;

        /// <summary>First failing enabled dimension: messages L.</summary>
        public const long DeniedMessagesLong = 1;

        /// <summary>Bytes L.</summary>
        public const long DeniedBytesLong = 2;

        /// <summary>Identical L.</summary>
        public const long DeniedIdenticalLong = 3;

        /// <summary>Messages S.</summary>
        public const long DeniedMessagesShort = 4;

        /// <summary>Bytes S.</summary>
        public const long DeniedBytesShort = 5;

        /// <summary>Identical S.</summary>
        public const long DeniedIdenticalShort = 6;

        /// <summary>Live token exists with different units or body hash. No write.</summary>
        public const long Conflict = 7;
    }

    /// <summary>Mapped <c>RESERVE</c> outcome including store-level Redis unavailability.</summary>
    internal enum PostFilterQuotaReserveStatus
    {
        /// <summary>Reservation is live and counts toward the ceiling.</summary>
        Accepted = 0,

        /// <summary>Messages L would exceed.</summary>
        DeniedMessagesLong = 1,

        /// <summary>Bytes L would exceed.</summary>
        DeniedBytesLong = 2,

        /// <summary>Identical L would exceed.</summary>
        DeniedIdenticalLong = 3,

        /// <summary>Messages S would exceed.</summary>
        DeniedMessagesShort = 4,

        /// <summary>Bytes S would exceed.</summary>
        DeniedBytesShort = 5,

        /// <summary>Identical S would exceed.</summary>
        DeniedIdenticalShort = 6,

        /// <summary>Same token, different payload. State unchanged.</summary>
        Conflict = 7,

        /// <summary>Redis unavailable. No reservation. Caller fail-closes.</summary>
        Unavailable = 8,
    }

    /// <summary>Mapped <c>COMMIT</c> outcome.</summary>
    internal enum PostFilterQuotaCommitStatus
    {
        /// <summary>No live matching reservation. Committed counters unchanged.</summary>
        Noop = 0,

        /// <summary>Units transferred into the current buckets and the reservation deleted.</summary>
        Committed = 1,

        /// <summary>Redis unavailable. Reservation, if any, is unchanged.</summary>
        Unavailable = 2,
    }

    /// <summary>Mapped <c>RELEASE</c> outcome.</summary>
    internal enum PostFilterQuotaReleaseStatus
    {
        /// <summary>Nothing deleted.</summary>
        Noop = 0,

        /// <summary>Matching live reservation deleted.</summary>
        Released = 1,

        /// <summary>Redis unavailable. Reservation, if any, is unchanged.</summary>
        Unavailable = 2,
    }

    /// <summary>Integer codes returned by the <c>COMMIT</c> EVAL.</summary>
    internal static class PostFilterQuotaCommitCodes
    {
        /// <summary>No transfer.</summary>
        public const long Noop = 0;

        /// <summary>Transferred.</summary>
        public const long Committed = 1;
    }

    /// <summary>Integer codes returned by the <c>RELEASE</c> EVAL.</summary>
    internal static class PostFilterQuotaReleaseCodes
    {
        /// <summary>No delete.</summary>
        public const long Noop = 0;

        /// <summary>Deleted.</summary>
        public const long Released = 1;
    }
}

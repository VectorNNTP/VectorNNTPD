using System.Diagnostics.CodeAnalysis;

namespace VectorNNTPD.ReaderService.SessionState.BytesAccounting
{
    /// <summary>
    /// Identity of one MySQL-committed consume batch used for Redis APPLY idempotency.
    /// </summary>
    /// <remarks>
    /// Generated after MySQL commits and reused for every Redis retry of that batch in this
    /// process. It is not persisted. A process crash drops it and the batch is not replayed.
    /// </remarks>
    internal static class AccountByteBatchId
    {
        /// <summary>HASH field prefix for an applied batch on the remaining-quota key.</summary>
        public const string FieldPrefix = "b:";

        /// <summary>
        /// Maximum applied-batch marks retained per remaining key. Floor-only APPLY
        /// stays correct if an older mark is evicted.
        /// </summary>
        public const int MaxRetainedMarks = 256;

        /// <summary>Creates a new unique batch identity.</summary>
        public static string Create() => Guid.NewGuid().ToString("N");

        /// <summary>Returns whether <paramref name="batchId"/> is a usable identity.</summary>
        public static bool IsValid([NotNullWhen(true)] string? batchId) =>
            !string.IsNullOrWhiteSpace(batchId);

        /// <summary>HASH field name for <paramref name="batchId"/>.</summary>
        public static string FieldName(string batchId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
            return FieldPrefix + batchId;
        }
    }
}

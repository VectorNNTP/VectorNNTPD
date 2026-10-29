namespace VectorNNTPD.ReaderService.Session.CommandProcessor
{
    /// <summary>
    /// Chunk budget for the shared article TX data plane (Phase 1).
    /// </summary>
    /// <remarks>
    /// Architecture: default 64 KiB; allowed production range 64–256 KiB
    /// (<c>docs/NNTP-DATA-PLANE-IMPLEMENTATION.md</c>). Kept as an internal seam for Phase 6
    /// measurement rather than a full configuration surface.
    /// </remarks>
    public static class NntpArticleTxChunkBudget
    {
        /// <summary>Default owned TX chunk size (64 KiB).</summary>
        public const int DefaultBytes = 64 * 1024;

        /// <summary>Minimum production chunk size (64 KiB).</summary>
        public const int MinBytes = 64 * 1024;

        /// <summary>Maximum production chunk size (256 KiB).</summary>
        public const int MaxBytes = 256 * 1024;

        /// <summary>Returns <see langword="true"/> when <paramref name="chunkBytes"/> is in the production range.</summary>
        public static bool IsProductionRange(int chunkBytes) =>
            chunkBytes is >= MinBytes and <= MaxBytes;

        /// <summary>Throws if <paramref name="chunkBytes"/> is outside the production range.</summary>
        public static void ThrowIfNotProductionRange(int chunkBytes)
        {
            if (!IsProductionRange(chunkBytes))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(chunkBytes),
                    chunkBytes,
                    $"Article TX chunk budget must be between {MinBytes} and {MaxBytes} bytes.");
            }
        }
    }
}

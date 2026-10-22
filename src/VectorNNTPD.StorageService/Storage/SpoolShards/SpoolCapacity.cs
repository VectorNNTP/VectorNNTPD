namespace VectorNNTPD.StorageService.Storage.SpoolShards
{

    /// <summary>
    /// Integer capacity figures for one spool. Article counts use floor division:
    /// a remainder shorter than one average article does not count as an article.
    /// These figures are per spool. They are not divided across configured directories.
    /// </summary>
    internal static class SpoolCapacity
    {
        /// <summary>750 KiB, the planning average article size.</summary>
        internal const long AverageArticleBytes = 750L * 1024L;

        /// <summary>One decimal terabyte.</summary>
        internal const long DecimalTerabyteBytes = 1_000_000_000_000L;

        /// <summary>22 decimal terabytes of article bytes in one spool.</summary>
        internal const long Decimal22TerabyteBytes = 22L * DecimalTerabyteBytes;

        /// <summary>One binary tebibyte, 1024^4.</summary>
        internal const long BinaryTebibyteBytes = 1024L * 1024L * 1024L * 1024L;

        /// <summary>22 binary tebibytes of article bytes in one spool, 22 × 1024^4.</summary>
        internal const long Binary22TebibyteBytes = 22L * BinaryTebibyteBytes;

        /// <summary>Complete average-sized articles that fit in <paramref name="capacityBytes"/>.</summary>
        /// <param name="capacityBytes">Article-byte budget. Not raw disk size.</param>
        /// <returns>Floor of <paramref name="capacityBytes"/> divided by <see cref="AverageArticleBytes"/>.</returns>
        internal static long ArticleCount(long capacityBytes)
        {
            if (capacityBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacityBytes));
            }

            return capacityBytes / AverageArticleBytes;
        }

        /// <summary>Bytes left over after <see cref="ArticleCount"/>.</summary>
        /// <param name="capacityBytes">Article-byte budget.</param>
        /// <returns>Remainder. Always smaller than <see cref="AverageArticleBytes"/> for a non-negative budget.</returns>
        internal static long RemainderBytes(long capacityBytes)
        {
            if (capacityBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacityBytes));
            }

            return capacityBytes % AverageArticleBytes;
        }

        /// <summary>
        /// Floor of <paramref name="articleCount"/> divided by <paramref name="leafCount"/>.
        /// <see cref="LeafRemainder"/> is how many leaves would hold one extra article in an even integer split.
        /// BLAKE3 placement can still put more than that in one leaf.
        /// </summary>
        /// <param name="articleCount">Articles in one spool.</param>
        /// <param name="leafCount">Leaf directories in that same spool.</param>
        /// <returns>The even-split floor.</returns>
        internal static long ArticlesPerLeafFloor(long articleCount, int leafCount)
        {
            if (articleCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(articleCount));
            }

            if (leafCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(leafCount));
            }

            return articleCount / leafCount;
        }

        /// <summary>Extra articles above an even floor split across <paramref name="leafCount"/> leaves.</summary>
        /// <param name="articleCount">Articles in one spool.</param>
        /// <param name="leafCount">Leaf directories in that same spool.</param>
        /// <returns><paramref name="articleCount"/> modulo <paramref name="leafCount"/>.</returns>
        internal static long LeafRemainder(long articleCount, int leafCount)
        {
            if (articleCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(articleCount));
            }

            if (leafCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(leafCount));
            }

            return articleCount % leafCount;
        }
    }
}

using System.Diagnostics.CodeAnalysis;
using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.BackFillerService.ArticleWork
{
    /// <summary>
    /// BackFiller facade over <see cref="CanonicalArticleDeliveryFraming"/> for retained ArtData delivery.
    /// </summary>
    /// <remarks>
    /// StorageService and Common callers use <see cref="CanonicalArticleDeliveryFraming"/> directly.
    /// This type keeps existing BackFiller call sites and tests stable.
    /// </remarks>
    internal static class ArticleWorkDeliveryFraming
    {
        /// <inheritdoc cref="CanonicalArticleDeliveryFraming.IsFrameable"/>
        internal static bool IsFrameable(ReadOnlySpan<byte> artData) =>
            CanonicalArticleDeliveryFraming.IsFrameable(artData);

        /// <inheritdoc cref="CanonicalArticleDeliveryFraming.TryStuffHeaderLines"/>
        internal static bool TryStuffHeaderLines(
            ReadOnlySpan<byte> artData,
            [NotNullWhen(true)] out byte[]? stuffedHeader) =>
            CanonicalArticleDeliveryFraming.TryStuffHeaderLines(artData, out stuffedHeader);

        /// <inheritdoc cref="CanonicalArticleDeliveryFraming.TryCreateStuffedHeader"/>
        internal static bool TryCreateStuffedHeader(
            ReadOnlySpan<byte> artData,
            int bodyStart,
            [NotNullWhen(true)] out byte[]? stuffedHeader) =>
            CanonicalArticleDeliveryFraming.TryCreateStuffedHeader(artData, bodyStart, out stuffedHeader);
    }
}

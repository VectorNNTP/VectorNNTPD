using System.Text;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.Transit;

namespace VectorNNTPD.ReaderService.NntpDb
{
    /// <summary>
    /// Captures one article-size ceiling and Path tracker for a single article operation.
    /// </summary>
    internal static class NntpArticlePolicyCapture
    {
        /// <summary>
        /// Copies the published <c>maxartsize</c> when the session has a shared-configuration snapshot.
        /// Otherwise uses <paramref name="fallbackMaxArticleBytes"/>.
        /// The returned ceiling is the smaller of that value and the connection's captured Receive
        /// <c>MaxArticleBytes</c>. A peer cannot exceed the published global ceiling.
        /// A non-positive peer size, or a peer size above <see cref="int.MaxValue"/>, adds no further limit.
        /// </summary>
        /// <param name="session">Session that may hold the catalogue and the identified peer.</param>
        /// <param name="fallbackMaxArticleBytes">Limit used when no snapshot is published on the session.</param>
        /// <param name="maxArticleBytes">Article-size boundary for this operation.</param>
        /// <param name="siteNameUtf8">UTF-8 tracker component, or <see langword="null"/> when the static tracker remains in use.</param>
        internal static void Capture(
            NntpSession session,
            int fallbackMaxArticleBytes,
            out int maxArticleBytes,
            out byte[]? siteNameUtf8)
        {
            ArgumentNullException.ThrowIfNull(session);
            int globalOrFallback;
            if (session.SharedConfiguration is { } catalogue
                && catalogue.TryGetArticlePolicy(out var publishedMax, out var siteName))
            {
                globalOrFallback = publishedMax;
                siteNameUtf8 = Encoding.UTF8.GetBytes(siteName);
            }
            else
            {
                globalOrFallback = fallbackMaxArticleBytes;
                siteNameUtf8 = null;
            }

            maxArticleBytes = TransitReceivePolicy.EffectiveMaxArticleBytes(
                globalOrFallback,
                session.Authorization.TransitPeerPolicy);
        }
    }
}

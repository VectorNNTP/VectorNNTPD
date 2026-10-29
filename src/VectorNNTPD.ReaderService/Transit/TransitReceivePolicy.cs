using System.Text;
using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.ReaderService.Transit
{
    /// <summary>
    /// Applies one connection's captured Receive peer policy to an already classified article.
    /// </summary>
    /// <remarks>
    /// The peer object is the session authorization captured at identification.
    /// Send <see cref="TransitPeerPolicy.Patterns"/>, <see cref="TransitPeerPolicy.MessageTypes"/>,
    /// and <see cref="TransitPeerPolicy.SendMaxArticleBytes"/> are not read.
    /// CHECK has no article body and does not call this type.
    /// </remarks>
    internal static class TransitReceivePolicy
    {
        /// <summary>TAKETHIS/IHAVE detail when <see cref="ArticleRecord.ArtType"/> is outside the receive mask.</summary>
        internal const string ArticleTypeRejectDetail = "rejected article type";

        /// <summary>TAKETHIS/IHAVE detail when <see cref="TransitPeerPolicy.ReceivePatterns"/> does not subscribe the article.</summary>
        internal const string NewsgroupPatternRejectDetail = "rejected newsgroup pattern";

        /// <summary>
        /// Returns the receive ceiling for one article operation.
        /// </summary>
        /// <param name="globalOrFallback">
        /// Published <c>nntpsharedconfig.maxartsize</c>, or the caller fallback when no snapshot is on the session.
        /// This value is always the hard ceiling.
        /// </param>
        /// <param name="peer">Peer captured for this connection, or <see langword="null"/> when the session is not a named peer.</param>
        /// <returns>
        /// <paramref name="globalOrFallback"/> when <paramref name="peer"/> is absent or its
        /// <see cref="TransitPeerPolicy.MaxSize"/> is non-positive or above <see cref="int.MaxValue"/>
        /// (no peer ceiling). Otherwise the smaller of the two.
        /// </returns>
        internal static int EffectiveMaxArticleBytes(int globalOrFallback, TransitPeerPolicy? peer)
        {
            if (peer is null)
            {
                return globalOrFallback;
            }

            var peerMax = peer.MaxSize;
            if (peerMax <= 0 || peerMax > int.MaxValue)
            {
                return globalOrFallback;
            }

            var peerCeiling = (int)peerMax;
            return peerCeiling < globalOrFallback ? peerCeiling : globalOrFallback;
        }

        /// <summary>
        /// Returns whether the captured receive mask or newsgroup expression rejects <paramref name="record"/>.
        /// </summary>
        /// <param name="peer">Peer captured for this connection. <see langword="null"/> does not reject.</param>
        /// <param name="record">Classified CanonicalV1 record. Classification is not repeated here.</param>
        /// <param name="rejectDetail">
        /// <see cref="ArticleTypeRejectDetail"/> or <see cref="NewsgroupPatternRejectDetail"/> when this method returns <see langword="true"/>.
        /// </param>
        /// <returns><see langword="true"/> when the article must be rejected.</returns>
        /// <remarks>
        /// Article types use <see cref="ArticleTypeCapabilities.Allows"/>: <c>65535</c> allows every combination,
        /// and every other mask requires each flag on <see cref="ArticleRecord.ArtType"/> to be present.
        /// Newsgroups use <see cref="NewsfeedsPattern.EvaluateArticle"/> on the article's Newsgroups header.
        /// Only <see cref="NewsfeedsPatternDecision.Match"/> is accepted. Exclude, poison, and no-match reject.
        /// </remarks>
        internal static bool TryReject(TransitPeerPolicy? peer, in ArticleRecord record, out string rejectDetail)
        {
            rejectDetail = string.Empty;
            if (peer is null)
            {
                return false;
            }

            var allowed = (ArticleType)peer.ReceiveArticleTypes;
            if (!ArticleTypeCapabilities.Allows(allowed, record.ArtType))
            {
                rejectDetail = ArticleTypeRejectDetail;
                return true;
            }

            if (peer.ReceivePatterns.EvaluateArticle(SplitNewsgroups(record.Newsgroups)) != NewsfeedsPatternDecision.Match)
            {
                rejectDetail = NewsgroupPatternRejectDetail;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Splits a Newsgroups header into trimmed group tokens for <see cref="NewsfeedsPattern"/>.
        /// </summary>
        /// <param name="newsgroups">Header value bytes from the canonical record.</param>
        /// <returns>Group names. Empty and whitespace-only tokens are omitted.</returns>
        /// <remarks>
        /// The string conversion exists only because <see cref="NewsfeedsPattern.EvaluateArticle"/> takes strings.
        /// Comma separation and surrounding SP/HTAB trimming follow the header, not a second wildmat implementation.
        /// </remarks>
        private static List<string> SplitNewsgroups(ReadOnlySpan<byte> newsgroups)
        {
            var groups = new List<string>(4);
            var start = 0;
            for (var i = 0; i <= newsgroups.Length; i++)
            {
                if (i < newsgroups.Length && newsgroups[i] != (byte)',')
                {
                    continue;
                }

                var token = newsgroups[start..i];
                start = i + 1;
                var tokenStart = 0;
                var tokenEnd = token.Length;
                while (tokenStart < tokenEnd && IsSpace(token[tokenStart]))
                {
                    tokenStart++;
                }

                while (tokenEnd > tokenStart && IsSpace(token[tokenEnd - 1]))
                {
                    tokenEnd--;
                }

                if (tokenEnd > tokenStart)
                {
                    groups.Add(Encoding.UTF8.GetString(token[tokenStart..tokenEnd]));
                }
            }

            return groups;
        }

        /// <summary>Returns whether <paramref name="value"/> is SP or HTAB.</summary>
        private static bool IsSpace(byte value) => value is (byte)' ' or (byte)'\t';
    }
}

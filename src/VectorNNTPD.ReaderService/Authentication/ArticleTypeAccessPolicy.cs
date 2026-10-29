using VectorNNTPD.Common.Articles;
using VectorNNTPD.ReaderService.Session;

namespace VectorNNTPD.ReaderService.Authentication
{
    /// <summary>
    /// Shared account ArtType capability checks. Distinct from PostFilter policy
    /// <c>art_type</c> ENUM rows and from <see cref="ArticleRecord.ArtType"/> flags.
    /// </summary>
    public static class ArticleTypeAccessPolicy
    {
        /// <summary>
        /// Returns whether the session may inject <paramref name="articleType"/>.
        /// Unauthenticated sessions and accounts without a capability snapshot are
        /// unrestricted. Authenticated accounts use the flags loaded at AUTHINFO.
        /// </summary>
        public static bool CanPostArticleType(NntpSession session, ArticleType articleType)
        {
            ArgumentNullException.ThrowIfNull(session);
            return Allows(session.AccountPolicy, articleType);
        }

        /// <summary>
        /// Returns whether the session may read <paramref name="articleType"/>.
        /// Uses the same capability mask as posting. Retrieval cannot call this
        /// until stored articles expose <see cref="ArticleType"/>.
        /// </summary>
        public static bool CanReadArticleType(NntpSession session, ArticleType articleType)
        {
            ArgumentNullException.ThrowIfNull(session);
            return Allows(session.AccountPolicy, articleType);
        }

        /// <summary>Evaluates a captured account policy, or unrestricted when none applies.</summary>
        public static bool Allows(NntpAccountPolicy? policy, ArticleType articleType)
        {
            if (policy is null)
            {
                return true;
            }

            return ArticleTypeCapabilities.Allows(policy.AllowedArtTypes, articleType);
        }
    }
}

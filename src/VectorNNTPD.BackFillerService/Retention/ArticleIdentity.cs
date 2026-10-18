
using VectorNNTPD.Common.Articles;

namespace VectorNNTPD.BackFillerService.Retention
{
    /// <summary>
    /// Exact Message-ID string paired with the lowercase hexadecimal <see cref="ArticleId"/> used in Article Work Success
    /// metadata and retention ArticleId collision checks. NNTP delivery looks up by Message-ID; RequestIds are separate capabilities.
    /// </summary>
    /// <param name="MessageId">Exact Message-ID string. Not normalized.</param>
    /// <param name="ArticleIdHex">64-character lowercase hexadecimal <see cref="ArticleId"/>.</param>
    internal readonly record struct ArticleIdentity(string MessageId, string ArticleIdHex)
    {
        /// <summary>Length of <see cref="ArticleIdHex"/>. Equals <see cref="ArticleId.HexLength"/>.</summary>
        private const int ArticleIdHexLength = ArticleId.HexLength;

        /// <summary>
        /// Creates an identity from the exact Message-ID and an already-computed <see cref="ArticleId"/>.
        /// Does not hash the Message-ID.
        /// </summary>
        /// <param name="messageId">Exact Message-ID string. Not normalized. Must not be <see langword="null"/>.</param>
        /// <param name="artId">ArticleId already computed for <paramref name="messageId"/>.</param>
        /// <returns>
        /// An identity whose <see cref="ArticleIdHex"/> is <paramref name="artId"/> in lowercase hexadecimal.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="messageId"/> is <see langword="null"/>.</exception>
        internal static ArticleIdentity From(string messageId, ArticleId artId)
        {
            ArgumentNullException.ThrowIfNull(messageId);
            return new ArticleIdentity(messageId, artId.ToLowerHexString());
        }
    }
}

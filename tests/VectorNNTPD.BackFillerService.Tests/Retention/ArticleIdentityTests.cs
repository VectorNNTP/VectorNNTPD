using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.Common.Articles;
using System.Text;

namespace VectorNNTPD.BackFillerService.Tests.Retention
{
    /// <summary>Regression for <see cref="ArticleIdentity"/> mapping from message id and precomputed <see cref="ArticleId"/>.</summary>
    public sealed class ArticleIdentityTests
    {
        /// <summary>Uses the supplied article id hex from <see cref="ArticleId.FromMessageId"/> rather than re-deriving from the message id string alone.</summary>
        [Fact]
        public void From_uses_existing_ArticleId_without_message_id_hash()
        {
            const string messageId = "<12345@example.invalid>";
            var artId = ArticleId.FromMessageId(Encoding.ASCII.GetBytes(messageId));
            var identity = ArticleIdentity.From(messageId, artId);
            Assert.Equal(messageId, identity.MessageId);
            Assert.Equal(artId.ToLowerHexString(), identity.ArticleIdHex);
            Assert.Equal(ArticleId.HexLength, identity.ArticleIdHex.Length);
            Assert.Equal("dcab316ba0e91c6abbad8d5759bff207932dbe9168c88954c6dd9240b4a6da14", identity.ArticleIdHex);
        }

        /// <summary>Different message ids must not collapse to the same lower-hex article id.</summary>
        [Fact]
        public void Distinct_message_ids_produce_distinct_article_id_hex()
        {
            var a = ArticleIdentity.From(
                "<one@example.invalid>",
                ArticleId.FromMessageId("<one@example.invalid>"u8));
            var b = ArticleIdentity.From(
                "<two@example.invalid>",
                ArticleId.FromMessageId("<two@example.invalid>"u8));
            Assert.NotEqual(a.ArticleIdHex, b.ArticleIdHex);
        }
    }
}

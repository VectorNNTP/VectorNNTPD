using VectorNNTPD.BackFillerService.Retention;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using System.Text;

namespace VectorNNTPD.BackFillerService.Tests.Fixtures
{
    /// <summary>Builds CanonicalV1 articles for retention tests.</summary>
    internal static class RetentionTestArticles
    {
        /// <summary>Parses a destuffed article into a prepared canonical record with a new request id.</summary>
        /// <param name="messageId">Message-ID written into the article and the prepared record.</param>
        /// <param name="body">Article body after the header blank line. Defaults to <c>body</c>.</param>
        internal static PreparedCanonical Create(string messageId, string body = "body\r\n")
        {
            var parser = new NntpArticleParser("backfiller.test");
            var destuffed = BuildDestuffed(messageId, body);
            var created = ArticleRecordFactory.TryCreate(parser, destuffed);
            Assert.True(created.IsAccepted, created.ParseFailure.ToString());
            return new PreparedCanonical(
                messageId,
                Guid.NewGuid(),
                created.Record,
                created.SelectedDateHeaderName,
                created.Record.ArtData.ToArray());
        }

        /// <summary>Creates a prepared article and retains it through <paramref name="authority"/>.</summary>
        internal static PreparedCanonical RetainPrepared(
            ArticleRetentionAuthority authority,
            string messageId,
            string body = "body\r\n")
        {
            var prepared = Create(messageId, body);
            var result = authority.RetainCanonical(
                prepared.MessageId,
                prepared.RequestId,
                prepared.Record,
                prepared.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.Retained, result.Kind);
            return prepared;
        }

        /// <summary>ASCII article with Path, Date, Message-ID, Newsgroups, and the supplied body.</summary>
        private static byte[] BuildDestuffed(string messageId, string body) =>
            Encoding.ASCII.GetBytes(
                "Path: peer.example\r\n"
                + "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n"
                + "Message-ID: " + messageId + "\r\n"
                + "Newsgroups: alt.test\r\n"
                + "From: user@example.test\r\n"
                + "Subject: s\r\n"
                + "\r\n"
                + body);

        /// <summary>Canonical materialization inputs ready for retention authority tests.</summary>
        /// <param name="MessageId">Article message id octets.</param>
        /// <param name="RequestId">Correlated work request id.</param>
        /// <param name="Record">Parsed article record.</param>
        /// <param name="SelectedDateHeaderName">Header chosen for date rewrite.</param>
        /// <param name="ArtData">Canonical retained payload bytes.</param>
        internal readonly record struct PreparedCanonical(
            string MessageId,
            Guid RequestId,
            ArticleRecord Record,
            NntpArticleHeaderName SelectedDateHeaderName,
            byte[] ArtData);
    }
}

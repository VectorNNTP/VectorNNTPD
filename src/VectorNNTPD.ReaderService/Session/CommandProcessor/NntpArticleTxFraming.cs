using System.Globalization;
using System.Text;

namespace VectorNNTPD.ReaderService.Session.CommandProcessor
{
    /// <summary>
    /// Wire framing kind for shared article TX. Mode-independent: callers choose framing; the TX
    /// primitive does not branch on MODE READER vs MODE STREAM and does not perform article lookup.
    /// </summary>
    /// <remarks>
    /// <c>Customer*</c> names mean RFC 3977 ARTICLE/BODY response shapes for externally supplied
    /// bytes — not an NNTPD customer article store.
    /// </remarks>
    public enum NntpArticleTxFrameKind
    {
        /// <summary>ARTICLE wire shape: <c>220 n|0 &lt;message-id&gt;\r\n</c> + full article + terminator.</summary>
        CustomerArticle = 0,

        /// <summary>Peer TAKETHIS-style feed: <c>TAKETHIS &lt;message-id&gt;\r\n</c> + article + terminator.</summary>
        PeerTakeThis = 1,

        /// <summary>BODY wire shape: <c>222 n|0 &lt;message-id&gt;\r\n</c> + body only + terminator.</summary>
        CustomerBody = 2,

        /// <summary>HEAD wire shape: <c>221 n|0 &lt;message-id&gt;\r\n</c> + headers only + terminator.</summary>
        CustomerHead = 3,
    }

    /// <summary>Framing parameters for shared article TX via <see cref="NntpResponseWriter"/>.</summary>
    public readonly struct NntpArticleTxFraming
    {
        /// <summary>Initializes framing.</summary>
        /// <param name="kind">Framing kind.</param>
        /// <param name="messageId">Message-id (including angle brackets when applicable).</param>
        /// <param name="articleNumber">
        /// Article number for the status line, or <c>0</c> when the article was selected by message-id
        /// (RFC 3977 §6.2.1 / §6.2.3).
        /// </param>
        public NntpArticleTxFraming(NntpArticleTxFrameKind kind, string messageId, long articleNumber = 0)
        {
            ArgumentException.ThrowIfNullOrEmpty(messageId);
            ArgumentOutOfRangeException.ThrowIfNegative(articleNumber);
            Kind = kind;
            MessageId = messageId;
            ArticleNumber = articleNumber;
        }

        /// <summary>Gets the framing kind.</summary>
        public NntpArticleTxFrameKind Kind { get; }

        /// <summary>Gets the message-id (including angle brackets when applicable).</summary>
        public string MessageId { get; }

        /// <summary>Gets the article number used in the status line (<c>0</c> for message-id selection).</summary>
        public long ArticleNumber { get; }

        /// <summary>Creates ARTICLE-style framing (<c>220 n mid</c>).</summary>
        public static NntpArticleTxFraming CustomerArticle(string messageId, long articleNumber = 0) =>
            new(NntpArticleTxFrameKind.CustomerArticle, messageId, articleNumber);

        /// <summary>Creates BODY-style framing (<c>222 n mid</c>).</summary>
        public static NntpArticleTxFraming CustomerBody(string messageId, long articleNumber = 0) =>
            new(NntpArticleTxFrameKind.CustomerBody, messageId, articleNumber);

        /// <summary>Creates HEAD-style framing (<c>221 n mid</c>).</summary>
        public static NntpArticleTxFraming CustomerHead(string messageId, long articleNumber = 0) =>
            new(NntpArticleTxFrameKind.CustomerHead, messageId, articleNumber);

        /// <summary>Creates TAKETHIS-style framing (<c>TAKETHIS mid</c>).</summary>
        public static NntpArticleTxFraming PeerTakeThis(string messageId) =>
            new(NntpArticleTxFrameKind.PeerTakeThis, messageId);

        /// <summary>Builds the ASCII status/command header line including trailing CRLF.</summary>
        public byte[] BuildHeaderBytes()
        {
            var number = ArticleNumber.ToString(CultureInfo.InvariantCulture);
            return Kind switch
            {
                NntpArticleTxFrameKind.CustomerArticle =>
                    Encoding.ASCII.GetBytes("220 " + number + " " + MessageId + "\r\n"),
                NntpArticleTxFrameKind.CustomerHead =>
                    Encoding.ASCII.GetBytes("221 " + number + " " + MessageId + "\r\n"),
                NntpArticleTxFrameKind.CustomerBody =>
                    Encoding.ASCII.GetBytes("222 " + number + " " + MessageId + "\r\n"),
                NntpArticleTxFrameKind.PeerTakeThis =>
                    Encoding.ASCII.GetBytes("TAKETHIS " + MessageId + "\r\n"),
                _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown article TX framing kind."),
            };
        }
    }
}

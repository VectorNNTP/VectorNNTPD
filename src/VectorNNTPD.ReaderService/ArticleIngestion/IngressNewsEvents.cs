using System.Text;
using VectorNNTPD.ReaderService.Session;

namespace VectorNNTPD.ReaderService.ArticleIngestion
{
    /// <summary>
    /// Builds already-decided news events at ingress decision points.
    /// </summary>
    internal static class IngressNewsEvents
    {
        /// <summary>WantTrash=false reason for unknown/non-carried Newsgroups.</summary>
        internal const string NewsgroupNotCarried = IngressNewsReasons.NewsgroupNotCarried;

        /// <summary>Writes a rejection news event. Failures do not change protocol state.</summary>
        public static void TryWriteRejected(
            NntpSession session,
            string? messageId,
            int responseCode,
            string reason,
            int size = 0)
        {
            ArgumentNullException.ThrowIfNull(session);
            session.TryWriteNews(
                new NewsLogEvent(
                    NewsLogDisposition.Rejected,
                    Ascii(messageId),
                    feed: SnapshotInboundFeed(session),
                    timestamp: session.Time.GetLocalNow(),
                    responseCode: responseCode,
                    reason: Ascii(reason),
                    size: size));
        }

        /// <summary>Writes an accepted news event for a path that never enters the queue.</summary>
        public static void TryWriteAccepted(NntpSession session, string? messageId, int size = 0)
        {
            ArgumentNullException.ThrowIfNull(session);
            session.TryWriteNews(
                new NewsLogEvent(
                    NewsLogDisposition.Accepted,
                    Ascii(messageId),
                    feed: SnapshotInboundFeed(session),
                    timestamp: session.Time.GetLocalNow(),
                    size: size));
        }

        /// <summary>Writes a moderated news event for a successful POST moderation submission.</summary>
        public static void TryWriteModerated(NntpSession session, string? messageId, int size = 0)
        {
            ArgumentNullException.ThrowIfNull(session);
            session.TryWriteNews(
                new NewsLogEvent(
                    NewsLogDisposition.Moderated,
                    Ascii(messageId),
                    feed: SnapshotInboundFeed(session),
                    timestamp: session.Time.GetLocalNow(),
                    size: size));
        }

        /// <summary>
        /// Copies the session's inbound Transit identifier for a self-contained news event.
        /// </summary>
        internal static ReadOnlyMemory<byte> SnapshotInboundFeed(NntpSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            var name = session.Authorization.TransitPeerName;
            return string.IsNullOrEmpty(name) ? default : Encoding.ASCII.GetBytes(name);
        }

        private static ReadOnlyMemory<byte> Ascii(string? value) =>
            string.IsNullOrEmpty(value) ? default : Encoding.ASCII.GetBytes(value);
    }
}

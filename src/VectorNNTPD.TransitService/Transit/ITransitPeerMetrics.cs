namespace VectorNNTPD.TransitService.Transit
{
    /// <summary>Lifetime counters for one configured Transit peer.</summary>
    public readonly struct TransitPeerMetricsSnapshot
    {
        /// <summary>Initializes a peer snapshot.</summary>
        public TransitPeerMetricsSnapshot(
            string peerId,
            int active,
            int peak,
            long accepted,
            long rejected,
            long articlesReceived,
            long articleBytes,
            long checks,
            long transmitted)
        {
            PeerId = peerId;
            Active = active;
            Peak = peak;
            Accepted = accepted;
            Rejected = rejected;
            ArticlesReceived = articlesReceived;
            ArticleBytes = articleBytes;
            Checks = checks;
            Transmitted = transmitted;
        }

        /// <summary>Gets the Transit identifier (configuration dictionary key).</summary>
        public string PeerId { get; }

        /// <summary>Gets current established sessions belonging to this peer.</summary>
        public int Active { get; }

        /// <summary>Gets the lifetime high-water of simultaneous established sessions.</summary>
        public int Peak { get; }

        /// <summary>Gets lifetime accepted inbound connections.</summary>
        public long Accepted { get; }

        /// <summary>Gets lifetime rejected inbound connections.</summary>
        public long Rejected { get; }

        /// <summary>Gets lifetime framed articles received from this peer.</summary>
        public long ArticlesReceived { get; }

        /// <summary>Gets lifetime framed article payload bytes received from this peer.</summary>
        public long ArticleBytes { get; }

        /// <summary>Gets lifetime CHECK operations for this peer.</summary>
        public long Checks { get; }

        /// <summary>
        /// Gets lifetime articles successfully transmitted <em>to</em> this peer.
        /// Remains 0 until outbound delivery accounting increments it.
        /// </summary>
        public long Transmitted { get; }
    }

    /// <summary>
    /// Authoritative process-wide Transit peer counters. Independent of FeedDiagnostics.
    /// </summary>
    public interface ITransitPeerMetrics
    {
        /// <summary>Records a successful inbound admission for a named peer.</summary>
        void RecordAccepted(string peerId);

        /// <summary>
        /// Retracts a prior <see cref="RecordAccepted"/> when the session is reclassified
        /// as a non-transit reader after AUTHINFO.
        /// </summary>
        void RetractAccepted(string peerId);

        /// <summary>Records an inbound admission rejection for a named peer.</summary>
        void RecordRejected(string peerId);

        /// <summary>Records that an established session for <paramref name="peerId"/> entered <c>RunAsync</c>.</summary>
        void RecordSessionRegistered(string peerId);

        /// <summary>Records that an established session for <paramref name="peerId"/> left <c>RunAsync</c>.</summary>
        void RecordSessionUnregistered(string peerId);

        /// <summary>Records one framed article received from <paramref name="peerId"/>.</summary>
        void RecordArticleReceived(string peerId, int bytes);

        /// <summary>Records one CHECK for <paramref name="peerId"/>.</summary>
        void RecordCheck(string peerId);

        /// <summary>
        /// Records articles successfully transmitted to <paramref name="peerId"/>.
        /// Call only from the outbound delivery path after a successful send.
        /// </summary>
        void RecordTransmitted(string peerId, long articles = 1);

        /// <summary>Captures lifetime counters for <paramref name="peerId"/> (zeros when unseen).</summary>
        TransitPeerMetricsSnapshot Capture(string peerId);
    }
}

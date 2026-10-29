using VectorNNTPD.ReaderService.Diagnostics;

namespace VectorNNTPD.ReaderService.Session
{
    /// <summary>Point-in-time counts of every established NNTP session on this process.</summary>
    public readonly struct NntpSessionCensusSnapshot
    {
        /// <summary>Initializes a census snapshot.</summary>
        public NntpSessionCensusSnapshot(
            int active,
            int established,
            int idle,
            int receiving,
            int waitingHistory,
            int waitingQueue,
            int waitingWindow,
            int completing,
            int takeThisOccupied = 0,
            int takeThisOccupiedMax = 0,
            int takeThisFullSessions = 0,
            int takeThisProcessing = 0,
            int takeThisProcessingMax = 0)
        {
            Active = active;
            Established = established;
            Idle = idle;
            Receiving = receiving;
            WaitingHistory = waitingHistory;
            WaitingQueue = waitingQueue;
            WaitingWindow = waitingWindow;
            Completing = completing;
            TakeThisOccupied = takeThisOccupied;
            TakeThisOccupiedMax = takeThisOccupiedMax;
            TakeThisFullSessions = takeThisFullSessions;
            TakeThisProcessing = takeThisProcessing;
            TakeThisProcessingMax = takeThisProcessingMax;
        }

        /// <summary>Gets established session count (complete server population).</summary>
        public int Active { get; }

        /// <summary>Gets established session count (same population as <see cref="Active"/>).</summary>
        public int Established { get; }

        /// <summary>Gets sessions in <see cref="FeedSessionState.Idle"/>.</summary>
        public int Idle { get; }

        /// <summary>Gets sessions in <see cref="FeedSessionState.Receiving"/>.</summary>
        public int Receiving { get; }

        /// <summary>Gets sessions in <see cref="FeedSessionState.WaitingHistory"/>.</summary>
        public int WaitingHistory { get; }

        /// <summary>Gets sessions in <see cref="FeedSessionState.WaitingQueue"/>.</summary>
        public int WaitingQueue { get; }

        /// <summary>Gets sessions in <see cref="FeedSessionState.WaitingWindow"/>.</summary>
        public int WaitingWindow { get; }

        /// <summary>Gets sessions in <see cref="FeedSessionState.Completing"/>.</summary>
        public int Completing { get; }

        /// <summary>Gets the sum of TAKETHIS Occupied slots across sessions.</summary>
        public int TakeThisOccupied { get; }

        /// <summary>Gets the highest TAKETHIS Occupied count on any one session.</summary>
        public int TakeThisOccupiedMax { get; }

        /// <summary>Gets how many sessions currently have Occupied equal to TAKETHIS Depth (16).</summary>
        public int TakeThisFullSessions { get; }

        /// <summary>Gets the sum of concurrent ProcessArticleAsync tasks across sessions.</summary>
        public int TakeThisProcessing { get; }

        /// <summary>Gets the highest concurrent ProcessArticleAsync count on any one session.</summary>
        public int TakeThisProcessingMax { get; }
    }

    /// <summary>
    /// Authoritative registry of established NNTP sessions (readers, streamers, and transit).
    /// </summary>
    public interface INntpSessionCensus
    {
        /// <summary>Registers a session that has entered <see cref="NntpSession.RunAsync"/>.</summary>
        void Register(NntpSession session);

        /// <summary>Unregisters a session that is leaving <see cref="NntpSession.RunAsync"/>.</summary>
        void Unregister(NntpSession session);

        /// <summary>
        /// Stops attributing <paramref name="session"/> to Transit peer telemetry after
        /// it authenticates as a reader. Does not remove it from the process census.
        /// </summary>
        void ReleasePeerAttribution(NntpSession session);

        /// <summary>Captures the current complete session population.</summary>
        NntpSessionCensusSnapshot Capture();
    }
}

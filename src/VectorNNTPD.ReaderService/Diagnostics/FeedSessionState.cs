namespace VectorNNTPD.ReaderService.Diagnostics
{
    /// <summary>Coarse activity for one NNTP session in a feed-diagnostics snapshot.</summary>
    public enum FeedSessionState : byte
    {
        /// <summary>Between commands, or no in-flight article work.</summary>
        Idle = 0,

        /// <summary>Framing or reading an article from the input pipe.</summary>
        Receiving = 1,

        /// <summary>Waiting for HistoryDB Peek/Lookup to complete.</summary>
        WaitingHistory = 2,

        /// <summary>Waiting for ingestion-queue byte-budget admission.</summary>
        WaitingQueue = 3,

        /// <summary>Blocked on CHECK/TAKETHIS pipeline depth.</summary>
        WaitingWindow = 4,

        /// <summary>Emitting the transfer/CHECK response.</summary>
        Completing = 5,
    }
}

namespace VectorNNTPD.ReaderService.History
{
    /// <summary>Outcome of a HistoryDB lookup for CHECK.</summary>
    public enum HistoryLookupResult : byte
    {
        /// <summary>Message-ID has not been seen; CHECK should return 238.</summary>
        Unseen = 0,

        /// <summary>Message-ID is already in history. CHECK and IHAVE map this result to a reply.</summary>
        Seen = 1,

        /// <summary>Redis infrastructure failed; CHECK should return 431, not a false 238.</summary>
        Unavailable = 2,
    }
}

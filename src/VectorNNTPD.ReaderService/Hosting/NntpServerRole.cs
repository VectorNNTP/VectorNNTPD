namespace VectorNNTPD.ReaderService.Hosting
{
    /// <summary>Operational role of this NNTP host process (intrinsic; not a runtime switch).</summary>
    public enum NntpServerRole
    {
        /// <summary>NNTP reader-server role (MODE READER, article retrieval, POST).</summary>
        Reader = 1,

        /// <summary>NNTP transit-server role (streaming peers, CHECK/TAKETHIS/IHAVE).</summary>
        Transit = 2,
    }
}

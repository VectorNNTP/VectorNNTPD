namespace VectorNNTPD.ReaderService.Session
{
    /// <summary>Observable lifetime of an <see cref="NntpSession"/> connection.</summary>
    /// <remarks>
    /// <see cref="Running"/> → <see cref="Finalizing"/> is the one-shot transition that may
    /// release distributed admission. Later teardown signals observe <see cref="Finalizing"/>
    /// or <see cref="Finalized"/> and do nothing.
    /// </remarks>
    public enum NntpSessionLifetime
    {
        /// <summary>Constructed; <see cref="NntpSession.RunAsync"/> has not started.</summary>
        Created = 0,

        /// <summary>Greeting/command loop is running.</summary>
        Running = 1,

        /// <summary>The first teardown path has claimed cleanup.</summary>
        Finalizing = 2,

        /// <summary>Admission release and connection cleanup have finished.</summary>
        Finalized = 3,
    }
}

namespace VectorNNTPD.TransitService.ArticleIngestion
{
    /// <summary>
    /// INN <c>news</c> log disposition codes implemented for v1 ingress.
    /// </summary>
    /// <remarks>
    /// Values match innd(8) LOGGING / newslog(5) where VectorNNTP actually
    /// produces the event: <c>+</c> accepted for propagation, <c>j</c> accepted
    /// locally as junk (not propagated), <c>-</c> deliberately rejected. <c>m</c>
    /// is a VectorNNTP disposition for a POST sealed for moderation (not an INN
    /// innd code) and is not currently propagated by egress routing. Cancel
    /// processing (<c>c</c>) is not produced until a cancel path exists. INN's
    /// informational <c>?</c> (isolated CR/LF) is not emitted. Only
    /// <see cref="Accepted"/> includes the outbound-site field.
    /// </remarks>
    public enum NewsLogDisposition : byte
    {
        /// <summary>Accepted for local store and propagation (<c>+</c>); includes the outbound-site field.</summary>
        Accepted = (byte)'+',

        /// <summary>Accepted locally as junk (<c>j</c>); not propagated, so no outbound-site field.</summary>
        Junk = (byte)'j',

        /// <summary>Deliberately rejected article (<c>-</c>); no outbound-site field.</summary>
        Rejected = (byte)'-',

        /// <summary>POST sealed for moderation (<c>m</c>); not propagated, so no outbound-site field.</summary>
        Moderated = (byte)'m',
    }
}

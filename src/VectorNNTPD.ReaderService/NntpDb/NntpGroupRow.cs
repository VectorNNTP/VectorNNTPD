namespace VectorNNTPD.ReaderService.NntpDb
{
    /// <summary>
    /// One raw <c>nntpgroups</c> row as read from MySQL. Validation and wire encoding
    /// happen when a newsgroup snapshot is constructed.
    /// </summary>
    /// <param name="GroupName">Original <c>group_name</c> text.</param>
    /// <param name="GroupDescription"><c>group_description</c>, empty when the column is null.</param>
    /// <param name="CountHigh">Unsigned <c>count_high</c> water mark.</param>
    /// <param name="CountLow">Unsigned <c>count_low</c> water mark.</param>
    /// <param name="PostingStatus">Single status octet as stored (<c>y</c>/<c>n</c>/<c>m</c>/<c>x</c>/<c>j</c>). The RFC 6048 <c>=&lt;newsgroup&gt;</c> form is not represented.</param>
    public readonly record struct NntpGroupRow(
        string GroupName,
        string GroupDescription,
        ulong CountHigh,
        ulong CountLow,
        byte PostingStatus);
}

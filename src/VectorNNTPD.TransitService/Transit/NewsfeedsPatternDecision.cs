namespace VectorNNTPD.TransitService.Transit
{
    /// <summary>
    /// Result of evaluating a newsfeeds(5) / uwildmat_poison subscription expression.
    /// </summary>
    public enum NewsfeedsPatternDecision
    {
        /// <summary>No pattern matched the newsgroup.</summary>
        NoMatch = 0,

        /// <summary>The rightmost matching pattern is an inclusion (subscribed).</summary>
        Match = 1,

        /// <summary>The rightmost matching pattern is an exclusion (<c>!</c>).</summary>
        Exclude = 2,

        /// <summary>The rightmost matching pattern is poison (<c>@</c>).</summary>
        Poison = 3,
    }
}

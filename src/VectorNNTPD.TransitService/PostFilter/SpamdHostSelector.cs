using VectorNNTPD.TransitService.Configuration;

namespace VectorNNTPD.TransitService.PostFilter
{
    /// <summary>Deterministic SPAMD host index selection. Does not allocate.</summary>
    internal static class SpamdHostSelector
    {
        /// <summary>Returns the first host index to try for this CHECK.</summary>
        public static int NextStart(
            PostFilterSpamAssassinHostSelection selection,
            int hostCount,
            ref int roundRobin)
        {
            if (hostCount <= 0)
            {
                return 0;
            }

            if (selection == PostFilterSpamAssassinHostSelection.Failover)
            {
                return 0;
            }

            var next = Interlocked.Increment(ref roundRobin) - 1;
            if (next < 0)
            {
                next = 0;
            }

            return next % hostCount;
        }
    }
}

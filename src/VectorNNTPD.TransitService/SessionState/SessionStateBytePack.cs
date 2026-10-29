namespace VectorNNTPD.TransitService.SessionState
{
    /// <summary>
    /// Encodes combined RENEW+APPLY into one Redis integer. Remaining is always
    /// non-negative; a lost renewal is <c>-remaining - 1</c>.
    /// </summary>
    internal static class SessionStateBytePack
    {
        /// <summary>Packs renew status and remaining for a combined EVAL return.</summary>
        public static long Encode(bool renewed, long remaining)
        {
            if (remaining < 0)
            {
                remaining = 0;
            }

            return renewed ? remaining : -remaining - 1;
        }

        /// <summary>Unpacks a combined EVAL integer into renew status and remaining.</summary>
        public static (SessionStateRenewStatus Status, long Remaining) Decode(long packed)
        {
            if (packed >= 0)
            {
                return (SessionStateRenewStatus.Renewed, packed);
            }

            var remaining = -packed - 1;
            return (SessionStateRenewStatus.Lost, remaining < 0 ? 0 : remaining);
        }
    }
}

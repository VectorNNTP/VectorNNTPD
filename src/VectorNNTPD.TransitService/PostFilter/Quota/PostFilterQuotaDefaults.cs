namespace VectorNNTPD.TransitService.PostFilter.Quota
{
    /// <summary>Fixed crash-recovery timings for PostFilter accept-quota reservations.</summary>
    /// <remarks>
    /// Reservation TTL is not a quota window and is not a v1 operator knob.
    /// The floor (<see cref="ReservationTtl"/>) is the crash-recovery hold when
    /// SpamAssassin CHECK is not in the POST path. When CHECK is enabled, the
    /// effective hold is derived so a live reservation outlives the configured
    /// SPAMD <c>OperationTimeout</c> plus <see cref="ReservationHoldSkew"/>.
    /// Idle key TTL is garbage collection only; bucket fields define window expiry.
    /// </remarks>
    internal static class PostFilterQuotaDefaults
    {
        /// <summary>Live-reservation crash-recovery floor written by <c>RESERVE</c> when CHECK is off.</summary>
        public static readonly TimeSpan ReservationTtl = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Extra hold after the configured SpamAssassin operation budget so
        /// CreateQueued / TryAdmit / COMMIT still see a live reservation.
        /// </summary>
        public static readonly TimeSpan ReservationHoldSkew = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Extra milliseconds added to <c>max(window) + reservation hold</c> when refreshing
        /// idle Redis key expiry. Does not change bucket accounting.
        /// </summary>
        public const long IdleTtlSkewMs = 1_000;

        /// <summary>
        /// Effective reservation hold in milliseconds for one POST.
        /// Floor is <see cref="ReservationTtl"/>. When SpamAssassin is enabled,
        /// hold is at least <paramref name="operationTimeout"/> plus
        /// <see cref="ReservationHoldSkew"/>.
        /// </summary>
        public static long HoldMilliseconds(bool spamAssassinEnabled, TimeSpan operationTimeout)
        {
            var floor = (long)ReservationTtl.TotalMilliseconds;
            if (!spamAssassinEnabled)
            {
                return floor;
            }

            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(operationTimeout, TimeSpan.Zero);
            var needed = (long)operationTimeout.TotalMilliseconds
                + (long)ReservationHoldSkew.TotalMilliseconds;
            return Math.Max(floor, needed);
        }
    }
}

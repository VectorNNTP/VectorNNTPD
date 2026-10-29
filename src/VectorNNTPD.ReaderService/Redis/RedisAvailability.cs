namespace VectorNNTPD.ReaderService.Redis
{
    /// <summary>
    /// Short-lived Redis unavailable / recovery-probe state shared by EXISTS, SET, and EVAL.
    /// </summary>
    /// <remarks>
    /// Healthy operations proceed without a lock. After a failure, callers see
    /// <see cref="TryBegin"/> return <see langword="false"/> until the cooldown expires.
    /// Exactly one caller may become the recovery probe. Only that probe may return
    /// the circuit to healthy. Caller cancellation abandons the grant and does not
    /// open or close the cooldown. CHECK never takes a global lock.
    /// </remarks>
    internal sealed class RedisAvailability
    {
        internal static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(1);

        private readonly TimeProvider _timeProvider;
        private readonly long _cooldownTicks;
        private long _unavailableUntilTicks;
        private int _probeInFlight;
        private int _openLogged;

        /// <summary>Initializes a new instance of the <see cref="RedisAvailability"/> class.</summary>
        public RedisAvailability(TimeProvider timeProvider, TimeSpan cooldown)
        {
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cooldown, TimeSpan.Zero);
            _timeProvider = timeProvider;
            _cooldownTicks = cooldown.Ticks;
        }

        /// <summary>Gets whether a cooldown is active (a recovery probe is not counted as available).</summary>
        public bool IsUnavailable
        {
            get
            {
                var until = Volatile.Read(ref _unavailableUntilTicks);
                return until != 0 && _timeProvider.GetUtcNow().UtcTicks < until;
            }
        }

        /// <summary>
        /// Attempts to start a Redis call. <paramref name="isRecoveryProbe"/> is set only for the
        /// single caller allowed to probe after cooldown.
        /// </summary>
        public bool TryBegin(out bool isRecoveryProbe)
        {
            isRecoveryProbe = false;
            var until = Volatile.Read(ref _unavailableUntilTicks);
            if (until == 0)
            {
                return true;
            }

            if (_timeProvider.GetUtcNow().UtcTicks < until)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _probeInFlight, 1, 0) != 0)
            {
                return false;
            }

            isRecoveryProbe = true;
            return true;
        }

        /// <summary>
        /// Records a finished Redis call. Only a recovery probe may clear cooldown on success.
        /// </summary>
        public void Complete(
            bool isRecoveryProbe,
            bool succeeded,
            out bool becameUnavailable,
            out bool recovered)
        {
            becameUnavailable = false;
            recovered = false;
            if (succeeded)
            {
                if (isRecoveryProbe)
                {
                    var previous = Interlocked.Exchange(ref _unavailableUntilTicks, 0);
                    Interlocked.Exchange(ref _openLogged, 0);
                    Interlocked.Exchange(ref _probeInFlight, 0);
                    recovered = previous != 0;
                }

                return;
            }

            var until = _timeProvider.GetUtcNow().UtcTicks + _cooldownTicks;
            Interlocked.Exchange(ref _unavailableUntilTicks, until);
            becameUnavailable = Interlocked.Exchange(ref _openLogged, 1) == 0;
            if (isRecoveryProbe)
            {
                Interlocked.Exchange(ref _probeInFlight, 0);
            }
        }

        /// <summary>Releases a recovery-probe grant without changing cooldown state.</summary>
        public void Abandon(bool isRecoveryProbe)
        {
            if (isRecoveryProbe)
            {
                Interlocked.Exchange(ref _probeInFlight, 0);
            }
        }
    }
}

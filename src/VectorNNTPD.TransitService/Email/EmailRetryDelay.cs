using VectorNNTPD.TransitService.Configuration;

namespace VectorNNTPD.TransitService.Email
{
    /// <summary>Exponential backoff with full jitter for SMTP retries.</summary>
    internal static class EmailRetryDelay
    {
        /// <summary>Computes the delay before the next attempt (<paramref name="attempt"/> is 1-based completed attempt).</summary>
        public static TimeSpan Compute(SmtpOptions smtp, int attempt, Random random)
        {
            ArgumentNullException.ThrowIfNull(smtp);
            ArgumentNullException.ThrowIfNull(random);
            if (attempt < 1)
            {
                attempt = 1;
            }

            var initial = smtp.InitialRetryDelay;
            var maximum = smtp.MaximumRetryDelay < initial ? initial : smtp.MaximumRetryDelay;
            var multiplier = 1L << Math.Min(attempt - 1, 16);
            var rawTicks = initial.Ticks * multiplier;
            var capped = rawTicks > maximum.Ticks ? maximum : TimeSpan.FromTicks(rawTicks);
            if (capped <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            var jitter = random.NextDouble();
            return TimeSpan.FromTicks((long)(capped.Ticks * jitter));
        }
    }
}

using System.Threading.RateLimiting;

namespace VectorNNTPD.Common.Messaging.RabbitMq
{
    /// <summary>
    /// Forwards rate-limit operations to an inner <see cref="RateLimiter"/> without taking ownership.
    /// </summary>
    /// <remarks>
    /// RabbitMQ.Client 7.2.2 <c>Channel</c> stores and disposes
    /// <c>CreateChannelOptions.OutstandingPublisherConfirmationsRateLimiter</c>. When production code
    /// must retain sole disposal ownership of a <c>ThrottlingRateLimiter</c>, pass this facade into
    /// <c>CreateChannelOptions</c> so the client dispose path only disposes the facade.
    /// </remarks>
    internal sealed class RabbitMqBorrowedRateLimiter : RateLimiter
    {
        /// <summary>Limiter that performs acquires; not disposed by this facade.</summary>
        private readonly RateLimiter _inner;

        /// <summary>Wraps <paramref name="inner"/> without assuming disposal ownership.</summary>
        /// <param name="inner">Limiter used for acquire/statistics; owned by the caller.</param>
        internal RabbitMqBorrowedRateLimiter(RateLimiter inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            _inner = inner;
        }

        /// <inheritdoc />
        public override TimeSpan? IdleDuration => _inner.IdleDuration;

        /// <inheritdoc />
        public override RateLimiterStatistics? GetStatistics() => _inner.GetStatistics();

        /// <inheritdoc />
        protected override RateLimitLease AttemptAcquireCore(int permitCount)
            => _inner.AttemptAcquire(permitCount);

        /// <inheritdoc />
        protected override ValueTask<RateLimitLease> AcquireAsyncCore(
            int permitCount,
            CancellationToken cancellationToken)
            => _inner.AcquireAsync(permitCount, cancellationToken);

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            // Intentionally empty: the inner limiter is owned by the caller.
            base.Dispose(disposing);
        }

        /// <inheritdoc />
        protected override ValueTask DisposeAsyncCore()
        {
            // Intentionally empty: the inner limiter is owned by the caller.
            return default;
        }
    }
}

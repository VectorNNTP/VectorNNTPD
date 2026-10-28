using System.Threading.RateLimiting;
using RabbitMQ.Client;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.Common.Tests.Messaging.RabbitMq
{
    /// <summary>
    /// Verifies the borrowed outstanding-confirm limiter facade does not assume disposal ownership.
    /// </summary>
    public sealed class RabbitMqBorrowedRateLimiterTests
    {
        /// <summary>
        /// Disposing the facade must leave the inner <see cref="ThrottlingRateLimiter"/> usable so
        /// <see cref="RabbitMqClientPublishChannel"/> remains the sole disposal owner.
        /// </summary>
        [Fact]
        public async Task Dispose_of_borrowed_facade_does_not_dispose_inner_limiter()
        {
            using var inner = new ThrottlingRateLimiter(4);
            var borrowed = new RabbitMqBorrowedRateLimiter(inner);

            await borrowed.DisposeAsync();
            borrowed.Dispose();

            using RateLimitLease lease = await inner.AcquireAsync(1);
            Assert.True(lease.IsAcquired);
        }

        /// <summary>
        /// After the owner disposes the inner limiter, acquires must fail closed.
        /// </summary>
        [Fact]
        public async Task Dispose_of_inner_limiter_rejects_further_acquires()
        {
            var inner = new ThrottlingRateLimiter(4);
            var borrowed = new RabbitMqBorrowedRateLimiter(inner);
            inner.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            {
                _ = await borrowed.AcquireAsync(1);
            });
        }
    }
}

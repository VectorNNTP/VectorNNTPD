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
        /// Sync <see cref="IDisposable.Dispose"/> on the facade must leave the inner limiter usable.
        /// </summary>
        [Fact]
        public void Dispose_of_borrowed_facade_does_not_dispose_inner_limiter()
        {
            using var inner = new ThrottlingRateLimiter(4);
            using (var borrowed = new RabbitMqBorrowedRateLimiter(inner))
            {
            }

            using RateLimitLease lease = inner.AttemptAcquire(1);
            Assert.True(lease.IsAcquired);
        }

        /// <summary>
        /// <see cref="IAsyncDisposable.DisposeAsync"/> on the facade must leave the inner limiter usable.
        /// </summary>
        [Fact]
        public async Task DisposeAsync_of_borrowed_facade_does_not_dispose_inner_limiter()
        {
            using var inner = new ThrottlingRateLimiter(4);
            await using (var borrowed = new RabbitMqBorrowedRateLimiter(inner))
            {
            }

            using RateLimitLease lease = await inner.AcquireAsync(1);
            Assert.True(lease.IsAcquired);
        }

        /// <summary>
        /// After the owner disposes the inner limiter, acquires through the facade must fail closed.
        /// </summary>
        [Fact]
        public async Task Dispose_of_inner_limiter_rejects_further_acquires()
        {
            var inner = new ThrottlingRateLimiter(4);
            await using (var borrowed = new RabbitMqBorrowedRateLimiter(inner))
            {
                inner.Dispose();

                await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                {
                    _ = await borrowed.AcquireAsync(1);
                });
            }
        }
    }
}

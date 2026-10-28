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
        /// Sync facade dispose must not dispose the inner limiter, and acquires must deplete inner permits.
        /// </summary>
        [Fact]
        public void Dispose_of_borrowed_facade_does_not_dispose_inner_limiter()
        {
            using var inner = new ThrottlingRateLimiter(1);
            RateLimitLease heldThroughFacade;
            using (var borrowed = new RabbitMqBorrowedRateLimiter(inner))
            {
                heldThroughFacade = borrowed.AttemptAcquire(1);
                Assert.True(heldThroughFacade.IsAcquired);

                // Lease is held on the inner limiter: no further permit available.
                using RateLimitLease blocked = inner.AttemptAcquire(1);
                Assert.False(blocked.IsAcquired);
            }

            // Facade disposed; the outstanding lease still belongs to the inner limiter.
            using (heldThroughFacade)
            {
                using RateLimitLease stillBlocked = inner.AttemptAcquire(1);
                Assert.False(stillBlocked.IsAcquired);
            }

            using RateLimitLease afterRelease = inner.AttemptAcquire(1);
            Assert.True(afterRelease.IsAcquired);
        }

        /// <summary>
        /// Async facade dispose must not dispose the inner limiter, and async acquires must deplete inner permits.
        /// </summary>
        [Fact]
        public async Task DisposeAsync_of_borrowed_facade_does_not_dispose_inner_limiter()
        {
            using var inner = new ThrottlingRateLimiter(1);
            RateLimitLease heldThroughFacade;
            await using (var borrowed = new RabbitMqBorrowedRateLimiter(inner))
            {
                heldThroughFacade = await borrowed.AcquireAsync(1);
                Assert.True(heldThroughFacade.IsAcquired);

                // AttemptAcquire (not AcquireAsync): exhausted permits queue async waits.
                using RateLimitLease blocked = inner.AttemptAcquire(1);
                Assert.False(blocked.IsAcquired);
            }

            using (heldThroughFacade)
            {
                using RateLimitLease stillBlocked = inner.AttemptAcquire(1);
                Assert.False(stillBlocked.IsAcquired);
            }

            using RateLimitLease afterRelease = await inner.AcquireAsync(1);
            Assert.True(afterRelease.IsAcquired);
        }

        /// <summary>
        /// After the owner disposes the inner limiter, acquires through the facade must fail closed.
        /// </summary>
        [Fact]
        public async Task Dispose_of_inner_limiter_rejects_further_acquires()
        {
            var inner = new ThrottlingRateLimiter(4);
            try
            {
                await using (var borrowed = new RabbitMqBorrowedRateLimiter(inner))
                {
                    inner.Dispose();

                    await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                    {
                        _ = await borrowed.AcquireAsync(1);
                    });
                }
            }
            finally
            {
                // Idempotent if already disposed above; covers assertion-failure paths before Dispose.
                inner.Dispose();
            }
        }
    }
}

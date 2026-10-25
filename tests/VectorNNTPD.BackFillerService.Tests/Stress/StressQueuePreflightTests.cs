namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>Deterministic cleanliness thresholds for stress preflight (no broker I/O).</summary>
    public sealed class StressQueuePreflightTests
    {
        /// <summary>Default limits accept zero ready and unacked up to two.</summary>
        [Fact]
        public void IsClean_true_when_ready_and_unacked_within_limits()
        {
            Assert.True(StressQueuePreflight.IsClean(ready: 0, unacked: 0));
            Assert.True(StressQueuePreflight.IsClean(ready: 0, unacked: 2));
        }

        /// <summary>Non-zero ready or excessive unacked fails cleanliness.</summary>
        [Fact]
        public void IsClean_false_when_ready_or_unacked_exceed_limits()
        {
            Assert.False(StressQueuePreflight.IsClean(ready: 1, unacked: 0));
            Assert.False(StressQueuePreflight.IsClean(ready: 0, unacked: 3));
            Assert.False(StressQueuePreflight.IsClean(ready: 44, unacked: 99));
            Assert.False(StressQueuePreflight.IsClean(ready: 0, unacked: 25));
        }

        /// <summary>Custom maxReady and maxUnacked bounds are inclusive.</summary>
        [Fact]
        public void IsClean_respects_custom_limits()
        {
            Assert.True(StressQueuePreflight.IsClean(ready: 5, unacked: 10, maxReady: 5, maxUnacked: 10));
            Assert.False(StressQueuePreflight.IsClean(ready: 6, unacked: 10, maxReady: 5, maxUnacked: 10));
        }
    }
}

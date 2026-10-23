using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Tests.Fixtures;

namespace VectorNNTPD.StorageService.Tests.Configuration
{

    public sealed class SpoolPersistenceOptionsTests
    {
        [Fact]
        public void Defaults_bound_the_queue_and_the_worker_pool()
        {
            var options = new SpoolPersistenceOptions();
            Assert.Equal(1L << 30, options.MaxQueuedBytes);
            Assert.Equal(1, options.MinWorkers);
            Assert.Equal(8, options.MaxWorkers);
            Assert.Equal(2, options.MaxInFlightPerSpool);
            Assert.Equal(32, options.ScaleUpQueueDepth);
            Assert.Equal(2, options.ScaleDownQueueDepth);
            Assert.Equal(2000, options.ScaleDwellMilliseconds);
        }

        [Fact]
        public void Max_workers_below_the_minimum_fails_validation()
        {
            var options = StorageServerTestOptions.CreateValid();
            options.SpoolPersistence.MinWorkers = 4;
            options.SpoolPersistence.MaxWorkers = 2;
            var result = new StorageServerOptionsValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(
                result.Failures!,
                failure => failure.Contains("MaxWorkers", StringComparison.Ordinal));
        }
    }
}

using VectorNNTPD.Common.Core;

namespace VectorNNTPD.Common.Tests.Core
{
    /// <summary>Regresses ownership-transfer helpers used to avoid dispose-in-finally patterns.</summary>
    public sealed class DisposableOwnershipTests
    {
        /// <summary>Transfer clears ownership so dispose does not dispose the resource.</summary>
        [Fact]
        public void Transfer_prevents_subsequent_dispose_from_disposing_the_resource()
        {
            var resource = new TrackingDisposable();
            TrackingDisposable transferred;
            using (var ownership = new DisposableOwnership<TrackingDisposable>(resource))
            {
                transferred = ownership.Transfer();
            }

            Assert.Same(resource, transferred);
            Assert.Equal(0, resource.DisposeCount);
            transferred.Dispose();
            Assert.Equal(1, resource.DisposeCount);
        }

        /// <summary>Without transfer, using disposes the held resource.</summary>
        [Fact]
        public void Dispose_without_transfer_disposes_the_resource()
        {
            var resource = new TrackingDisposable();
            using (var ownership = new DisposableOwnership<TrackingDisposable>(resource))
            {
                Assert.Same(resource, ownership.Resource);
            }

            Assert.Equal(1, resource.DisposeCount);
        }

        /// <summary>A second transfer after the first fails closed.</summary>
        [Fact]
        public void Second_transfer_throws_and_does_not_dispose()
        {
            var resource = new TrackingDisposable();
            using var ownership = new DisposableOwnership<TrackingDisposable>(resource);
            Assert.Same(resource, ownership.Transfer());
            Assert.Throws<ObjectDisposedException>(() => ownership.Transfer());
            Assert.Equal(0, resource.DisposeCount);
            resource.Dispose();
        }

        /// <summary>Resource access after transfer fails closed.</summary>
        [Fact]
        public void Resource_after_transfer_throws()
        {
            var resource = new TrackingDisposable();
            using var ownership = new DisposableOwnership<TrackingDisposable>(resource);
            _ = ownership.Transfer();
            Assert.Throws<ObjectDisposedException>(() => _ = ownership.Resource);
            resource.Dispose();
        }

        /// <summary>Repeated dispose is a no-op after the first release.</summary>
        [Fact]
        public void Repeated_dispose_is_safe()
        {
            var resource = new TrackingDisposable();
            var ownership = new DisposableOwnership<TrackingDisposable>(resource);
            ownership.Dispose();
            ownership.Dispose();
            Assert.Equal(1, resource.DisposeCount);
        }

        /// <summary>Null construction is rejected before any ownership is taken.</summary>
        [Fact]
        public void Null_resource_is_rejected()
        {
            Assert.Throws<ArgumentNullException>(() => new DisposableOwnership<TrackingDisposable>(null!));
        }

        /// <summary>Async transfer clears ownership so dispose does not dispose the resource.</summary>
        /// <returns>A task that completes after async ownership is released.</returns>
        [Fact]
        public async Task Async_transfer_prevents_subsequent_dispose_from_disposing_the_resource()
        {
            var resource = new TrackingAsyncDisposable();
            TrackingAsyncDisposable transferred;
            await using (var ownership = new AsyncDisposableOwnership<TrackingAsyncDisposable>(resource))
            {
                transferred = ownership.Transfer();
            }

            Assert.Same(resource, transferred);
            Assert.Equal(0, resource.DisposeCount);
            await transferred.DisposeAsync();
            Assert.Equal(1, resource.DisposeCount);
        }

        /// <summary>Async dispose without transfer awaits resource disposal once.</summary>
        /// <returns>A task that completes after disposal.</returns>
        [Fact]
        public async Task Async_dispose_without_transfer_disposes_once()
        {
            var resource = new TrackingAsyncDisposable();
            var ownership = new AsyncDisposableOwnership<TrackingAsyncDisposable>(resource);
            await ownership.DisposeAsync();
            await ownership.DisposeAsync();
            Assert.Equal(1, resource.DisposeCount);
        }

        /// <summary>A second async transfer after the first fails closed.</summary>
        /// <returns>A task that completes after the failed second transfer.</returns>
        [Fact]
        public async Task Async_second_transfer_throws()
        {
            var resource = new TrackingAsyncDisposable();
            await using var ownership = new AsyncDisposableOwnership<TrackingAsyncDisposable>(resource);
            Assert.Same(resource, ownership.Transfer());
            Assert.Throws<ObjectDisposedException>(() => ownership.Transfer());
            Assert.Equal(0, resource.DisposeCount);
            await resource.DisposeAsync();
        }

        private sealed class TrackingDisposable : IDisposable
        {
            public int DisposeCount { get; private set; }

            public void Dispose() => DisposeCount++;
        }

        private sealed class TrackingAsyncDisposable : IAsyncDisposable
        {
            public int DisposeCount { get; private set; }

            public ValueTask DisposeAsync()
            {
                DisposeCount++;
                return ValueTask.CompletedTask;
            }
        }
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.Accounts;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;

namespace VectorNNTPD.BackFillerService.Tests.Nntp
{
    /// <summary>
    /// Global cap on concurrent NNTP session establishment across pools and registry apply.
    /// </summary>

    public sealed class ProviderSessionEstablishmentGateTests
    {
        /// <summary>
        /// Shared gate limits concurrent connects across three pools during warm-up.
        /// </summary>

        [Fact]

        public async Task Global_bound_limits_concurrent_establishment_across_providers()
        {
            var factory = new ScriptedNntpTransportFactory();
            var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.BlockConnect = block;
            NntpSessionPoolTests.EnqueueReadyServers(factory, count: 15);
            using var gate = new ProviderSessionEstablishmentGate(2);
            await using var poolA = NntpSessionPoolTests.CreatePool(factory, max: 5, establishment: gate, backbone: "A");
            await using var poolB = NntpSessionPoolTests.CreatePool(factory, max: 5, establishment: gate, backbone: "B");
            await using var poolC = NntpSessionPoolTests.CreatePool(factory, max: 5, establishment: gate, backbone: "C");

            var warming = Task.WhenAll(
                poolA.EnsureDesiredSessionsAsync(CancellationToken.None),
                poolB.EnsureDesiredSessionsAsync(CancellationToken.None),
                poolC.EnsureDesiredSessionsAsync(CancellationToken.None));

            await WaitForAsync(() => factory.CurrentConcurrentConnects == 2, TimeSpan.FromSeconds(5));
            Assert.Equal(2, factory.CurrentConcurrentConnects);
            Assert.True(factory.MaxObservedConcurrentConnects <= 2);

            block.TrySetResult();
            await warming.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(factory.MaxObservedConcurrentConnects <= 2);
            Assert.Equal(5, poolA.ActiveSessionCount);
            Assert.Equal(5, poolB.ActiveSessionCount);
            Assert.Equal(5, poolC.ActiveSessionCount);
            Assert.Equal(15, factory.ConnectAttempts.Count);
        }

        /// <summary>
        /// Gate does not raise steady-state sessions above per-provider MaxSessions.
        /// </summary>

        [Fact]

        public async Task Per_provider_MaxSessions_still_limits_steady_state_capacity()
        {
            var factory = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(factory, count: 3);
            using var gate = new ProviderSessionEstablishmentGate(8);
            await using var pool = NntpSessionPoolTests.CreatePool(factory, max: 2, establishment: gate);

            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
            Assert.Equal(2, pool.ActiveSessionCount);

            await using var first = await pool.AcquireAsync(CancellationToken.None);
            await using var second = await pool.AcquireAsync(CancellationToken.None);
            Assert.Equal(2, pool.ActiveLeaseCount);
            Assert.Equal(2, factory.ConnectAttempts.Count);
        }

        /// <summary>
        /// After warm-up, total sessions can exceed gate limit sequentially.
        /// </summary>

        [Fact]

        public async Task Steady_state_is_not_globally_capped_after_establishment()
        {
            var factory = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(factory, count: 6);
            using var gate = new ProviderSessionEstablishmentGate(1);
            await using var poolA = NntpSessionPoolTests.CreatePool(factory, max: 3, establishment: gate, backbone: "A");
            await using var poolB = NntpSessionPoolTests.CreatePool(factory, max: 3, establishment: gate, backbone: "B");

            await Task.WhenAll(
                poolA.EnsureDesiredSessionsAsync(CancellationToken.None),
                poolB.EnsureDesiredSessionsAsync(CancellationToken.None));

            Assert.Equal(3, poolA.ActiveSessionCount);
            Assert.Equal(3, poolB.ActiveSessionCount);
            Assert.Equal(1, factory.MaxObservedConcurrentConnects);
        }

        /// <summary>
        /// Warm-up respects gate concurrency below desired MaxSessions.
        /// </summary>

        [Fact]

        public async Task Startup_does_not_establish_everything_simultaneously()
        {
            var factory = new ScriptedNntpTransportFactory();
            var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.BlockConnect = block;
            NntpSessionPoolTests.EnqueueReadyServers(factory, count: 8);
            using var gate = new ProviderSessionEstablishmentGate(2);
            await using var pool = NntpSessionPoolTests.CreatePool(factory, max: 8, establishment: gate);

            var warming = pool.EnsureDesiredSessionsAsync(CancellationToken.None);
            await WaitForAsync(() => factory.CurrentConcurrentConnects == 2, TimeSpan.FromSeconds(5));
            Assert.Equal(2, factory.CurrentConcurrentConnects);
            Assert.True(factory.ConnectAttempts.Count >= 2);

            block.TrySetResult();
            await warming.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(factory.MaxObservedConcurrentConnects <= 2);
            Assert.Equal(8, pool.ActiveSessionCount);
        }

        /// <summary>
        /// On-demand acquire replenishment respects global establishment gate.
        /// </summary>

        [Fact]

        public async Task Replacement_acquire_path_is_globally_bounded()
        {
            var factory = new ScriptedNntpTransportFactory();
            var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.BlockConnect = block;
            NntpSessionPoolTests.EnqueueReadyServers(factory, count: 4);
            using var gate = new ProviderSessionEstablishmentGate(1);
            await using var poolA = NntpSessionPoolTests.CreatePool(factory, max: 2, establishment: gate, backbone: "A");
            await using var poolB = NntpSessionPoolTests.CreatePool(factory, max: 2, establishment: gate, backbone: "B");

            var acquires = Task.WhenAll(
                poolA.AcquireAsync(CancellationToken.None),
                poolB.AcquireAsync(CancellationToken.None),
                poolB.AcquireAsync(CancellationToken.None));

            await WaitForAsync(() => factory.CurrentConcurrentConnects == 1, TimeSpan.FromSeconds(5));
            Assert.Equal(1, factory.CurrentConcurrentConnects);

            block.TrySetResult();
            var leases = await acquires.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(factory.MaxObservedConcurrentConnects <= 1);
            Assert.Equal(3, factory.ConnectAttempts.Count);
            foreach (var lease in leases)
            {
                await lease.DisposeAsync();
            }
        }

        /// <summary>
        /// Canceled warm-up on a waiting pool does not open sessions.
        /// </summary>

        [Fact]

        public async Task Cancellation_while_waiting_for_gate_does_not_establish()
        {
            var factory = new ScriptedNntpTransportFactory();
            var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.BlockConnect = block;
            NntpSessionPoolTests.EnqueueReadyServers(factory, count: 2);
            using var gate = new ProviderSessionEstablishmentGate(1);
            await using var holder = NntpSessionPoolTests.CreatePool(factory, max: 1, establishment: gate, backbone: "Holder");
            await using var waiter = NntpSessionPoolTests.CreatePool(factory, max: 1, establishment: gate, backbone: "Waiter");

            var holding = holder.EnsureDesiredSessionsAsync(CancellationToken.None);
            await WaitForAsync(() => factory.CurrentConcurrentConnects == 1, TimeSpan.FromSeconds(5));

            using var cts = new CancellationTokenSource();
            var waiting = waiter.EnsureDesiredSessionsAsync(cts.Token);
            await Task.Yield();
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(0, waiter.ActiveSessionCount);
            Assert.Single(factory.ConnectAttempts);

            block.TrySetResult();
            await holding.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, holder.ActiveSessionCount);
        }

        /// <summary>
        /// Cancel during in-flight connect releases gate permit for a later warm-up.
        /// </summary>

        [Fact]

        public async Task Cancellation_while_establishing_releases_the_gate()
        {
            var factory = new ScriptedNntpTransportFactory();
            var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.BlockConnect = block;
            NntpSessionPoolTests.EnqueueReadyServers(factory, count: 1);
            using var gate = new ProviderSessionEstablishmentGate(1);
            await using var pool = NntpSessionPoolTests.CreatePool(factory, max: 1, establishment: gate);

            using var cts = new CancellationTokenSource();
            var establishing = pool.EnsureDesiredSessionsAsync(cts.Token);
            await WaitForAsync(() => factory.CurrentConcurrentConnects == 1, TimeSpan.FromSeconds(5));
            await cts.CancelAsync();

            // The in-flight connect observes the token inside the transport wait. Connect classifies
            // that as Cancelled, so warm-up can complete without OperationCanceledException.
            // A leaked permit makes the follow-up warm-up hit the safety timeout.
            await establishing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, pool.ActiveSessionCount);
            Assert.Equal(0, factory.CurrentConcurrentConnects);
            Assert.Single(factory.ConnectAttempts);

            factory.BlockConnect = null;
            NntpSessionPoolTests.EnqueueReadyServers(factory, count: 1);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, pool.ActiveSessionCount);
            Assert.Equal(2, factory.ConnectAttempts.Count);
        }

        /// <summary>
        /// Connect exception releases gate so a later acquire can succeed.
        /// </summary>

        [Fact]

        public async Task Exception_while_establishing_releases_the_gate()
        {
            var factory = new ScriptedNntpTransportFactory
            {
                ConnectException = new IOException("boom"),
            };
            using var gate = new ProviderSessionEstablishmentGate(1);
            await using var pool = NntpSessionPoolTests.CreatePool(factory, max: 1, establishment: gate);

            await Assert.ThrowsAsync<NntpProviderConnectException>(
                () => pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

            factory.ConnectException = null;
            NntpSessionPoolTests.EnqueueReadyServers(factory, count: 1);
            await using var lease = await pool.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, pool.ActiveSessionCount);
        }

        /// <summary>
        /// NntpProviderRegistry ApplySnapshot warm-up shares one gate across pools.
        /// </summary>

        [Fact]

        public async Task Registry_pools_share_one_application_wide_gate()
        {
            var catalog = new ProviderConfigurationCatalog();
            var factory = new ScriptedNntpTransportFactory();
            var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.BlockConnect = block;
            NntpSessionPoolTests.EnqueueReadyServers(factory, count: 6);
            using var gate = new ProviderSessionEstablishmentGate(2);
            await using var registry = new NntpProviderRegistry(
                catalog,
                factory,
                NntpSessionOptions.Default with
                {
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    CommandTimeout = TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                },
                TimeSpan.FromSeconds(2),
                NullLogger<NntpProviderRegistry>.Instance,
                establishment: gate);

            var applying = registry.ApplySnapshotAsync(
                [
                    new BackFillerProviderDefinition("A", "127.0.0.1", 119, false, null, null, 0, 3),
                    new BackFillerProviderDefinition("B", "127.0.0.1", 119, false, null, null, 0, 3),
                ],
                CancellationToken.None);

            await WaitForAsync(() => factory.CurrentConcurrentConnects == 2, TimeSpan.FromSeconds(5));
            Assert.Equal(2, factory.CurrentConcurrentConnects);
            block.TrySetResult();
            await applying.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(factory.MaxObservedConcurrentConnects <= 2);
            Assert.True(registry.TryGetPool("A", out var poolA));
            Assert.True(registry.TryGetPool("B", out var poolB));
            Assert.Equal(3, poolA.ActiveSessionCount);
            Assert.Equal(3, poolB.ActiveSessionCount);
        }


        /// <summary>Polls until <paramref name="condition"/> is true, or throws <see cref="TimeoutException"/>.</summary>
        /// <param name="condition">Condition the test is waiting to observe.</param>
        /// <param name="timeout">How long to wait before failing the test.</param>
        private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = TimeProvider.System.GetUtcNow() + timeout;
            while (!condition())
            {
                if (TimeProvider.System.GetUtcNow() > deadline)
                {
                    throw new TimeoutException("Condition was not met before the safety timeout.");
                }

                await Task.Yield();
            }
        }
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;


namespace VectorNNTPD.BackFillerService.Tests.RabbitMq
{
    /// <summary>
    /// Application RabbitMQ connection service: connect, generation replacement, recovery races, shutdown, and channels.
    /// </summary>

    public sealed class RabbitMqServiceTests
    {
        /// <summary>
        /// RabbitMqConnectionHandle does not implement IDisposable; connection lifetime stays with RabbitMqService.
        /// </summary>

        [Fact]

        public void ConnectionHandle_does_not_own_the_connection()
        {
            Assert.DoesNotContain(typeof(IDisposable), typeof(RabbitMqConnectionHandle).GetInterfaces());
            Assert.DoesNotContain(typeof(IAsyncDisposable), typeof(RabbitMqConnectionHandle).GetInterfaces());
        }

        /// <summary>
        /// First StartAsync connects once, publishes generation 1, and exposes a current handle.
        /// </summary>

        [Fact]

        public async Task StartAsync_connects_once_and_is_ready()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var replaced = new List<RabbitMqConnectionReplacedEventArgs>();
            var service = CreateService(factory);
            service.ConnectionReplaced += (_, args) => replaced.Add(args);

            await service.StartAsync(CancellationToken.None);

            Assert.Equal(1, factory.ConnectCount);
            Assert.Equal(1, service.ConnectionCount);
            Assert.Equal(1, service.ConnectionGeneration);
            Assert.True(service.IsReady);
            Assert.NotNull(service.Execution);
            Assert.True(service.TryGetCurrent(out var handle));
            Assert.True(handle.IsCurrent);
            Assert.Equal(1, handle.Generation);
            Assert.Same(factory.LastConnection, handle.Connection);
            var generation = Assert.Single(replaced);
            Assert.Equal(1, generation.ConnectionGeneration);
            Assert.False(generation.IsReplacement);
            Assert.Equal("VectorNNTPD.BackFillerService:backfiller01.usenet.ninja", factory.LastConnection!.ClientProvidedName);

            await service.DisposeAsync();
        }

        /// <summary>
        /// A ConnectionReplaced subscriber that throws during initial install does not fail StartAsync or retire generation 1.
        /// </summary>
        [Fact]
        public async Task StartAsync_succeeds_when_a_ConnectionReplaced_subscriber_throws()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var logger = new CollectingLogger<RabbitMqService>();
            var service = CreateService(factory, logger);
            service.ConnectionReplaced += static (_, _) =>
                throw new InvalidOperationException("subscriber boom on first connect");

            await service.StartAsync(CancellationToken.None);

            Assert.True(service.IsReady);
            Assert.Equal(1, service.ConnectionGeneration);
            Assert.True(service.TryGetCurrent(out var handle));
            Assert.Same(factory.LastConnection, handle.Connection);
            Assert.Equal(0, factory.LastConnection!.DisposeCount);
            Assert.Contains(
                logger.Entries,
                static entry => entry.EventId.Id == 2820
                    && entry.Message.Contains("generation 1", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message == "subscriber boom on first connect");

            await service.DisposeAsync();
        }

        /// <summary>
        /// One throwing ConnectionReplaced subscriber does not prevent later subscribers from receiving the event.
        /// </summary>
        [Fact]
        public async Task ConnectionReplaced_continues_notifying_subscribers_after_one_throws()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = CreateService(factory);
            var healthy = new List<RabbitMqConnectionReplacedEventArgs>();
            service.ConnectionReplaced += static (_, _) =>
                throw new InvalidOperationException("first subscriber failed");
            service.ConnectionReplaced += (_, args) => healthy.Add(args);

            await service.StartAsync(CancellationToken.None);

            var observed = Assert.Single(healthy);
            Assert.Equal(1, observed.ConnectionGeneration);
            Assert.False(observed.IsReplacement);
            Assert.True(service.IsReady);

            await service.DisposeAsync();
        }

        /// <summary>
        /// A throwing ConnectionReplaced subscriber during reconnect does not fail recovery or retire the new generation.
        /// </summary>
        [Fact]
        public async Task ConnectionLost_keeps_replacement_when_a_ConnectionReplaced_subscriber_throws()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var logger = new CollectingLogger<RabbitMqService>();
            var service = CreateService(factory, logger);
            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;

            service.ConnectionReplaced += (_, args) =>
            {
                if (args.IsReplacement)
                {
                    throw new InvalidOperationException("subscriber boom on replacement");
                }
            };

            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;
            first.SimulateLost();

            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await secondConnected.Task.WaitAsync(safety.Token);

            Assert.Equal(2, service.ConnectionGeneration);
            Assert.True(service.IsReady);
            Assert.Equal(1, first.DisposeCount);
            Assert.True(service.TryGetCurrent(out var current));
            Assert.Same(factory.LastConnection, current.Connection);
            Assert.Equal(0, factory.LastConnection!.DisposeCount);
            Assert.Contains(
                logger.Entries,
                static entry => entry.EventId.Id == 2820
                    && entry.Message.Contains("generation 2", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, static message => message == "subscriber boom on replacement");
            Assert.DoesNotContain(
                logger.Entries,
                static entry => entry.EventId.Id == 2810);

            await service.DisposeAsync();
        }

        /// <summary>
        /// Connect exception fails startup without marking service ready.
        /// </summary>

        [Fact]

        public async Task StartAsync_fails_when_connect_throws()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory
            {
                ConnectException = new InvalidOperationException("broker down"),
            };
            var service = CreateService(factory);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
            Assert.Equal("broker down", ex.Message);
            Assert.Equal(0, service.ConnectionCount);
            Assert.Equal(0, service.ConnectionGeneration);
            Assert.False(service.IsReady);
            Assert.Null(service.Execution);
            Assert.False(service.TryGetCurrent(out _));
        }

        /// <summary>
        /// Unusable connection from factory is disposed and startup fails.
        /// </summary>

        [Fact]

        public async Task StartAsync_fails_when_connection_is_not_usable()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory { ReturnUnusableConnection = true };
            var service = CreateService(factory);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
            Assert.Equal(0, service.ConnectionCount);
            Assert.False(service.IsReady);
            Assert.Equal(1, factory.LastConnection!.DisposeCount);
        }

        /// <summary>
        /// Canceled StartAsync leaves zero connections and not ready.
        /// </summary>

        [Fact]

        public async Task StartAsync_cancelled_does_not_leave_a_connection()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory
            {
                BlockConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var service = CreateService(factory);
            using var cts = new CancellationTokenSource();

            var start = service.StartAsync(cts.Token);
            cts.Cancel();
            factory.BlockConnect.TrySetCanceled(cts.Token);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            Assert.Equal(0, service.ConnectionCount);
            Assert.False(service.IsReady);
            Assert.Null(service.Execution);
        }

        /// <summary>
        /// After failed StartAsync, a retry can connect successfully.
        /// </summary>

        [Fact]

        public async Task StartAsync_can_retry_after_failed_connect()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory
            {
                ConnectException = new InvalidOperationException("broker down"),
            };
            var service = CreateService(factory);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
            factory.ConnectException = null;

            await service.StartAsync(CancellationToken.None);
            Assert.True(service.IsReady);
            Assert.Equal(1, service.ConnectionGeneration);

            await service.DisposeAsync();
        }

        /// <summary>
        /// Second StartAsync does not create another connection.
        /// </summary>

        [Fact]

        public async Task StartAsync_is_idempotent()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = CreateService(factory);
            await service.StartAsync(CancellationToken.None);

            Assert.Equal(1, factory.ConnectCount);
            await service.DisposeAsync();
        }

        /// <summary>
        /// StopAsync disposes the active connection and clears readiness.
        /// </summary>

        [Fact]

        public async Task Shutdown_after_successful_startup_disposes_the_current_generation()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = CreateService(factory);
            await service.StartAsync(CancellationToken.None);
            var connection = factory.LastConnection!;

            await service.StopAsync(CancellationToken.None);

            Assert.False(service.IsReady);
            Assert.False(service.TryGetCurrent(out _));
            Assert.Equal(1, connection.DisposeCount);
        }

        /// <summary>
        /// Simulated connection loss installs generation 2 and disposes the prior connection.
        /// </summary>

        [Fact]

        public async Task ConnectionLost_replaces_connection_and_increments_generation()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var replaced = new List<RabbitMqConnectionReplacedEventArgs>();
            var service = CreateService(factory);
            service.ConnectionReplaced += (_, args) => replaced.Add(args);

            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;
            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;

            first.SimulateLost();

            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var count = await secondConnected.Task.WaitAsync(safety.Token);

            Assert.Equal(2, count);
            Assert.Equal(2, service.ConnectionCount);
            Assert.Equal(2, service.ConnectionGeneration);
            Assert.True(service.IsReady);
            Assert.Equal(1, first.DisposeCount);
            Assert.Equal(2, replaced.Count);
            Assert.True(replaced[1].IsReplacement);
            Assert.Equal(2, replaced[1].ConnectionGeneration);
            Assert.True(service.TryGetCurrent(out var current));
            Assert.Same(factory.LastConnection, current.Connection);

            await service.DisposeAsync();
        }

        /// <summary>
        /// Multiple consecutive connection losses keep recovering with increasing generation.
        /// </summary>

        [Fact]

        public async Task Repeated_connection_loss_recovers_indefinitely()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = CreateService(factory);
            await service.StartAsync(CancellationToken.None);

            for (var expected = 2; expected <= 4; expected++)
            {
                var previous = factory.LastConnection!;
                var connected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var published = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                factory.Connected = connected;
                void OnReplaced(object? sender, RabbitMqConnectionReplacedEventArgs args)
                {
                    if (args.ConnectionGeneration == expected)
                    {
                        published.TrySetResult(args.ConnectionGeneration);
                    }
                }

                service.ConnectionReplaced += OnReplaced;
                try
                {
                    previous.SimulateLost();
                    using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await connected.Task.WaitAsync(safety.Token);
                    await published.Task.WaitAsync(safety.Token);
                    Assert.Equal(expected, service.ConnectionGeneration);
                    Assert.True(service.IsReady);
                    Assert.Equal(1, previous.DisposeCount);
                }
                finally
                {
                    service.ConnectionReplaced -= OnReplaced;
                }
            }

            await service.DisposeAsync();
        }

        /// <summary>
        /// Stale connection loss handler cannot replace a newer generation.
        /// </summary>

        [Fact]

        public async Task Stale_generation_cannot_replace_newer_generation()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = CreateService(factory);
            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;
            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;
            first.SimulateLost();

            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await secondConnected.Task.WaitAsync(safety.Token);
            var current = factory.LastConnection!;

            var extra = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = extra;
            first.SimulateLost();

            var extraConnect = extra.Task.WaitAsync(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<TimeoutException>(() => extraConnect);

            Assert.Equal(2, factory.ConnectCount);
            Assert.Equal(2, service.ConnectionGeneration);
            Assert.True(service.TryGetCurrent(out var handle));
            Assert.Same(current, handle.Connection);
            Assert.Equal(1, first.DisposeCount);
            Assert.Equal(0, current.DisposeCount);

            await service.DisposeAsync();
            Assert.Equal(1, current.DisposeCount);
        }

        /// <summary>
        /// Dispose on stale handle does not dispose the current connection.
        /// </summary>

        [Fact]

        public async Task Stale_generation_cannot_dispose_newer_generation()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = CreateService(factory);
            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;
            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;
            first.SimulateLost();

            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await secondConnected.Task.WaitAsync(safety.Token);
            var current = factory.LastConnection!;

            await first.DisposeAsync();

            Assert.Equal(2, first.DisposeCount);
            Assert.Equal(0, current.DisposeCount);
            Assert.True(service.IsReady);
            Assert.True(service.TryGetCurrent(out var handle));
            Assert.Same(current, handle.Connection);

            await service.DisposeAsync();
            Assert.Equal(1, current.DisposeCount);
        }

        /// <summary>
        /// DisposeAsync invalidates handle, disposes connection, and does not fault execution.
        /// </summary>

        [Fact]

        public async Task Current_generation_can_be_shut_down_cleanly()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = CreateService(factory);
            await service.StartAsync(CancellationToken.None);
            Assert.True(service.TryGetCurrent(out var handle));
            var connection = factory.LastConnection!;

            await service.DisposeAsync();

            Assert.False(handle.IsCurrent);
            Assert.False(service.IsReady);
            Assert.Equal(1, connection.DisposeCount);
            Assert.False(service.Execution!.IsFaulted);
        }

        /// <summary>
        /// Connection dispose exception is logged; service still shuts down.
        /// </summary>

        [Fact]

        public async Task Unexpected_connection_disposal_failure_does_not_stop_shutdown()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var logger = new CollectingLogger<RabbitMqService>();
            var service = CreateService(factory, logger);
            await service.StartAsync(CancellationToken.None);
            var connection = factory.LastConnection!;
            connection.DisposeException = new NotSupportedException("dispose boom");

            await service.DisposeAsync();

            Assert.False(service.IsReady);
            Assert.False(service.TryGetCurrent(out _));
            Assert.Equal(1, connection.DisposeCount);
            Assert.False(service.Execution!.IsFaulted);
            Assert.Contains(
                logger.Entries,
                entry => entry.EventId.Id == 2815 && entry.Message.Contains("dispose boom", StringComparison.Ordinal));
            Assert.Contains(logger.Entries, static entry => entry.EventId.Id == 2816);
        }

        /// <summary>
        /// Stop during blocked recovery leaves no replacement connection installed.
        /// </summary>

        [Fact]

        public async Task Cancellation_during_recovery_does_not_install_a_replacement()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = CreateService(factory);
            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;
            var connectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.ConnectStarted = connectStarted;
            factory.BlockConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            first.SimulateLost();
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await connectStarted.Task.WaitAsync(safety.Token);

            var stop = service.StopAsync(CancellationToken.None);
            factory.BlockConnect.TrySetCanceled();
            await stop;

            Assert.Equal(1, service.ConnectionCount);
            Assert.Equal(1, service.ConnectionGeneration);
            Assert.False(service.IsReady);
            Assert.False(service.TryGetCurrent(out _));
            foreach (var connection in factory.Connections)
            {
                Assert.Equal(1, connection.DisposeCount);
            }
        }

        /// <summary>
        /// Dispose while recovery connect is in flight cancels install without faulting execution.
        /// </summary>

        [Fact]

        public async Task Shutdown_before_recovery_install_does_not_fault_the_watcher()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var logger = new CollectingLogger<RabbitMqService>();
            var service = CreateService(factory, logger);
            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;
            var disposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.DisposeStarted = disposeEntered;
            first.BlockDispose = releaseDispose;

            first.SimulateLost();
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await disposeEntered.Task.WaitAsync(safety.Token);
            Assert.False(service.IsStopping);
            Assert.Equal(0, first.DisposeCount);
            Assert.Equal(1, factory.AttemptCount);

            var dispose = Task.Run(() => service.DisposeAsync().AsTask());
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => service.IsStopping, TimeSpan.FromSeconds(2));
            releaseDispose.TrySetResult();
            await dispose.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(service.Execution!.IsFaulted);
            Assert.True(service.Execution.IsCompletedSuccessfully);
            Assert.Equal(1, factory.ConnectCount);
            Assert.Equal(1, factory.AttemptCount);
            Assert.Equal(1, service.ConnectionCount);
            Assert.Equal(1, service.ConnectionGeneration);
            Assert.Equal(1, first.DisposeCount);
            Assert.False(service.IsReady);
            Assert.False(service.TryGetCurrent(out _));
            AssertInstallCanceled(logger);
        }

        /// <summary>
        /// Dispose after recovery connect returns but before install avoids publishing stale generation.
        /// </summary>

        [Fact]

        public async Task Shutdown_after_recovery_connect_does_not_publish_or_fault()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new HoldSecondConnectFactory(entered, release);
            var logger = new CollectingLogger<RabbitMqService>();
            var service = CreateService(factory, logger);
            await service.StartAsync(CancellationToken.None);
            var first = factory.Inner.LastConnection!;

            first.SimulateLost();
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await entered.Task.WaitAsync(safety.Token);
            Assert.False(service.IsStopping);
            Assert.Equal(1, first.DisposeCount);

            var dispose = Task.Run(() => service.DisposeAsync().AsTask());
            await ArticleWorkTestDeliveries.WaitUntilAsync(() => service.IsStopping, TimeSpan.FromSeconds(2));
            release.TrySetResult();
            await dispose.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(service.Execution!.IsFaulted);
            Assert.True(service.Execution.IsCompletedSuccessfully);
            Assert.Equal(1, service.ConnectionCount);
            Assert.Equal(1, service.ConnectionGeneration);
            Assert.Equal(2, factory.Inner.ConnectCount);
            var second = factory.Inner.Connections[1];
            Assert.NotSame(first, second);
            Assert.Equal(1, first.DisposeCount);
            Assert.Equal(1, second.DisposeCount);
            Assert.False(service.IsReady);
            Assert.False(service.TryGetCurrent(out _));
            AssertInstallCanceled(logger);
            Assert.DoesNotContain(logger.Entries, static entry => entry.EventId.Id == 2809);
        }

        /// <summary>
        /// A recovery connect exception is logged and retried. The watcher stays running until disposal.
        /// </summary>
        [Fact]
        public async Task Unexpected_recovery_failure_is_retried_and_disposal_completes()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var logger = new CollectingLogger<RabbitMqService>();
            var service = CreateService(factory, logger);
            await service.StartAsync(CancellationToken.None);
            var first = factory.LastConnection!;
            factory.ConnectException = new InvalidOperationException("broker exploded");

            first.SimulateLost();
            await ArticleWorkTestDeliveries.WaitUntilAsync(
                () => logger.Entries.Any(static entry =>
                    entry.EventId.Id == 2810
                    && entry.Message.Contains("broker exploded", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(2));

            Assert.False(service.Execution!.IsFaulted);
            await service.DisposeAsync();

            Assert.True(service.Execution.IsCompletedSuccessfully);
            Assert.True(factory.AttemptCount >= 2);
            Assert.Equal(1, service.ConnectionCount);
            Assert.Equal(1, first.DisposeCount);
            Assert.Contains(logger.Entries, static entry => entry.EventId.Id == 2816);
            Assert.DoesNotContain(logger.Entries, static entry => entry.EventId.Id == 2809);
        }

        /// <summary>
        /// CreateManualAckChannelAsync on current handle opens channel without disposing connection.
        /// </summary>

        [Fact]

        public async Task CreateChannel_uses_the_current_generation_and_does_not_dispose_the_connection()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = CreateService(factory);
            await service.StartAsync(CancellationToken.None);
            Assert.True(service.TryGetCurrent(out var handle));

            var channel = await handle.Connection.CreateManualAckChannelAsync(handle.Generation, CancellationToken.None);
            Assert.Equal(1, channel.Generation);
            Assert.True(channel.IsOpen);
            Assert.Single(factory.LastConnection!.Channels);

            await channel.DisposeAsync();
            Assert.Equal(1, factory.LastConnection.Channels[0].DisposeCount);
            Assert.Equal(0, factory.LastConnection.DisposeCount);
            Assert.True(service.IsReady);

            await service.DisposeAsync();
        }

        /// <summary>
        /// Stale handle cannot create channels on the replaced connection.
        /// </summary>

        [Fact]

        public async Task CreateChannel_on_a_stale_handle_fails()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var service = CreateService(factory);
            await service.StartAsync(CancellationToken.None);
            Assert.True(service.TryGetCurrent(out var stale));
            var first = factory.LastConnection!;
            var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = secondConnected;
            first.SimulateLost();

            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await secondConnected.Task.WaitAsync(safety.Token);

            await Assert.ThrowsAsync<InvalidOperationException>(() => stale.Connection.CreateManualAckChannelAsync(stale.Generation, CancellationToken.None));
            Assert.Empty(factory.LastConnection!.Channels);

            await service.DisposeAsync();
        }

        /// <summary>
        /// Failed connect logs must not contain configured RabbitMQ password.
        /// </summary>

        [Fact]

        public async Task Logs_do_not_include_configured_password()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory
            {
                ConnectException = new InvalidOperationException("broker down"),
            };
            var logger = new CollectingLogger<RabbitMqService>();
            var service = CreateService(factory, logger);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));

            Assert.All(
                logger.Messages,
                static message => Assert.DoesNotContain(BackFillerTestOptions.SecretPassword, message, StringComparison.Ordinal));
        }

        /// <summary>
        /// Production RabbitMq sources avoid blocking sync waits on async paths.
        /// </summary>

        [Fact]

        public void Production_rabbitmq_code_does_not_block_synchronously()
        {
            var directory = FindRabbitMqSourceDirectory();
            Assert.True(Directory.Exists(directory), directory);
            foreach (var text in Directory.GetFiles(directory, "*.cs").Select(static file => File.ReadAllText(file)))
            {
                Assert.DoesNotContain("Thread.Sleep", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".GetAwaiter().GetResult()", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".Wait();", text, StringComparison.Ordinal);
                Assert.DoesNotContain(".Result;", text, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Legacy BackFillerRabbitMqService type is absent from the integrated assembly.
        /// </summary>

        [Fact]

        public void BackFiller_assembly_does_not_contain_BackFillerRabbitMqService()
        {
            var assembly = typeof(VectorNNTPD.BackFillerService.Hosting.BackFillerServiceCollectionExtensions).Assembly;
            Assert.Null(assembly.GetType("VectorNNTPD.BackFillerService.RabbitMq.BackFillerRabbitMqService"));
            Assert.DoesNotContain(
                assembly.GetTypes(),
                static type => type.Name == "BackFillerRabbitMqService");
        }

        /// <summary>
        /// Assembly retains Article Work, excludes cache-fleet topology, and excludes legacy backfiller.storage resources.
        /// </summary>
        [Fact]
        public void Integrated_standalone_assembly_keeps_article_work_without_cache_fleet_or_legacy_storage()
        {
            Assert.Equal("VectorNNTPD.Common", typeof(RabbitMqService).Assembly.GetName().Name);
            var assembly = typeof(VectorNNTPD.BackFillerService.Hosting.BackFillerServiceCollectionExtensions).Assembly;
            Assert.NotNull(assembly.GetType("VectorNNTPD.BackFillerService.ArticleWork.ArticleWorkConsumerService"));
            Assert.Null(assembly.GetType("VectorNNTPD.BackFillerService.Messaging.Cache.CacheFleetTopology"));
            Assert.DoesNotContain(
                assembly.GetTypes(),
                static type => type.Namespace == "VectorNNTPD.BackFillerService.Messaging.Cache");
            Assert.DoesNotContain(
                assembly.GetManifestResourceNames(),
                static name => name.Contains("backfiller.storage", StringComparison.OrdinalIgnoreCase));

            var topologyNames = assembly.GetTypes()
                .Where(static type => type.Namespace?.StartsWith(
                    "Coverlet.Core.Instrumentation",
                    StringComparison.Ordinal) != true)
                .SelectMany(static type => type.GetFields(
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Instance))
                .Where(static field => field.FieldType == typeof(string) && field.IsLiteral)
                .Select(static field => field.GetRawConstantValue() as string)
                .Where(static value => !string.IsNullOrEmpty(value))
                .ToArray();
            Assert.DoesNotContain(topologyNames, static value => value == "cache.requests");
            Assert.DoesNotContain(topologyNames, static value => value == "cache.broadcast");
            Assert.DoesNotContain(topologyNames, static value => value == "backfiller.storage");
        }

        /// <summary>
        /// Constructs a <see cref="RabbitMqService"/> with minimal reconnect delays for fast tests.
        /// Does not start the service or open connections.
        /// </summary>
        /// <param name="factory">Connection factory injected into the service.</param>
        /// <param name="logger">Optional logger; defaults to null logger.</param>

        internal static RabbitMqService CreateService(
            IRabbitMqConnectionFactory factory,
            ILogger<RabbitMqService>? logger = null)
        {
            var options = BackFillerTestOptions.CreateValidRabbitMq();
            options.PoolReconnectBaseDelayMs = 1;
            options.PoolReconnectMaxDelayMs = 1;
            return new RabbitMqService(
                factory,
                Microsoft.Extensions.Options.Options.Create(options),
                new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTPD.BackFillerService:backfiller01.usenet.ninja"),
                logger ?? NullLogger<RabbitMqService>.Instance);
        }

        /// <summary>Runtime options with RabbitMQ pool reconnect delays minimized for unit tests (single-host classic).</summary>
        internal static BackFillerRuntimeOptions CreateFastRuntime() =>
            CreateFastRuntime(hostCount: 1);

        /// <summary>
        /// Runtime options with minimized reconnect delays and the given unique host count
        /// (classic when 1, quorum when ≥ 2).
        /// </summary>
        /// <param name="hostCount">Unique configured host count in <c>1–7</c>.</param>
        internal static BackFillerRuntimeOptions CreateFastRuntime(int hostCount)
        {
            var rabbit = BackFillerTestOptions.CreateValidRabbitMqWithHostCount(hostCount);
            rabbit.PoolReconnectBaseDelayMs = 1;
            rabbit.PoolReconnectMaxDelayMs = 1;
            return BackFillerRuntimeOptionsFactory.Create(
                BackFillerTestOptions.CreateValid(),
                BackFillerTestOptions.CreateValidNntpDb(),
                BackFillerTestOptions.CreateValidAcme(),
                rabbit);
        }

        /// <summary>Walks upward from the test base directory to locate src/VectorNNTPD.BackFillerService/RabbitMq on disk.</summary>

        private static string FindRabbitMqSourceDirectory()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = RelativePath.Under(directory.FullName, "src", "VectorNNTPD.BackFillerService", "RabbitMq");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate src/VectorNNTPD.BackFillerService/RabbitMq.");
        }

        /// <summary>
        /// Asserts shutdown during recovery is logged as a failed reconnect and no generation is published.
        /// </summary>
        /// <param name="logger">Collecting logger from the service under test.</param>
        private static void AssertInstallCanceled(CollectingLogger<RabbitMqService> logger)
        {
            Assert.Contains(
                logger.Entries,
                static entry => entry.EventId.Id == 2810
                    && entry.Message.Contains("RabbitMQ service is stopping", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Entries, static entry => entry.EventId.Id == 2809);
        }

        /// <summary>
        /// Recovery connect waits until the test releases it and does not observe shutdown cancellation,
        /// so publication can lose the race after the broker connect returns.
        /// </summary>
        private sealed class HoldSecondConnectFactory : IRabbitMqConnectionFactory
        {
            /// <summary>Underlying fake factory performing actual connect work after gates release.</summary>

            private readonly FakeBackFillerRabbitMqConnectionFactory _inner = new();
            /// <summary>Signaled when the second connect attempt begins.</summary>
            private readonly TaskCompletionSource _secondConnectEntered;
            /// <summary>Released to unblock the held second connect.</summary>
            private readonly TaskCompletionSource _releaseSecondConnect;
            /// <summary>Connect attempts observed by this wrapper.</summary>
            private int _attempts;

            /// <summary>Blocks the second connect until released for recovery race tests.</summary>
            /// <param name="secondConnectEntered">Signaled when the second attempt starts.</param>
            /// <param name="releaseSecondConnect">Released to finish the blocked connect.</param>
            internal HoldSecondConnectFactory(
                TaskCompletionSource secondConnectEntered,
                TaskCompletionSource releaseSecondConnect)
            {
                _secondConnectEntered = secondConnectEntered;
                _releaseSecondConnect = releaseSecondConnect;
            }

            /// <summary>Fake factory used for connections that are not held on the second attempt.</summary>

            internal FakeBackFillerRabbitMqConnectionFactory Inner => _inner;

            /// <summary>
            /// Delegates the first connect to the inner fake; second and later attempts block until released.
            /// </summary>
            /// <param name="options">RabbitMQ options forwarded to the inner factory.</param>
            /// <param name="connectionName">Client connection name forwarded to the inner factory.</param>
            /// <param name="cancellationToken">Honored on the first attempt only.</param>

            public Task<IRabbitMqConnection> ConnectAsync(
                RabbitMqOptions options,
                string connectionName,
                CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref _attempts) == 1)
                {
                    return _inner.ConnectAsync(options, connectionName, cancellationToken);
                }

                return ConnectSecondAsync(options, connectionName);
            }

            /// <summary>
            /// Signals second-connect entry, waits for test release, then connects with no cancellation token.
            /// </summary>
            /// <param name="options">RabbitMQ options forwarded to the inner factory.</param>
            /// <param name="connectionName">Client connection name forwarded to the inner factory.</param>

            private async Task<IRabbitMqConnection> ConnectSecondAsync(
                RabbitMqOptions options,
                string connectionName)
            {
                _secondConnectEntered.TrySetResult();
                await _releaseSecondConnect.Task.ConfigureAwait(false);
                return await _inner.ConnectAsync(options, connectionName, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}

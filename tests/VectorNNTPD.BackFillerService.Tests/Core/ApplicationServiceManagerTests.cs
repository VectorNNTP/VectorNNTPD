using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Tests.Fixtures;
using VectorNNTPD.Common.Core;
using BackFillerApplicationServiceManager = VectorNNTPD.BackFillerService.Core.ApplicationServiceManager;

namespace VectorNNTPD.BackFillerService.Tests.Core
{
    /// <summary>
    /// ApplicationServiceManager start/stop ordering, rollback, and unexpected termination events.
    /// </summary>
    public sealed class ApplicationServiceManagerTests
    {
        /// <summary>
        /// Started services are tracked in registration order; stop reverses order and clears the started list.
        /// </summary>
        [Fact]
        public async Task Starts_in_registration_order_and_stops_in_reverse()
        {
            var order = new List<string>();
            var first = new TrackingService("acme", order);
            var second = new TrackingService("listener", order);
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                BackFillerTestOptions.CreateValid(),
                BackFillerTestOptions.CreateValidNntpDb());
            var manager = new BackFillerApplicationServiceManager(
                [first, second],
                runtime,
                NullLogger<BackFillerApplicationServiceManager>.Instance);

            await manager.StartAsync(CancellationToken.None);
            Assert.Equal(["start:acme", "start:listener"], order);
            Assert.Equal([first, second], manager.StartedServices);

            await manager.StopAsync(CancellationToken.None);
            Assert.Equal(["start:acme", "start:listener", "stop:listener", "stop:acme"], order);
            Assert.Empty(manager.StartedServices);
        }

        /// <summary>
        /// When a later service fails to start, already-started services are stopped in reverse and none remain started.
        /// </summary>
        [Fact]
        public async Task Startup_failure_rolls_back_already_started_services()
        {
            var order = new List<string>();
            var first = new TrackingService("acme", order);
            var second = new TrackingService("listener", order, startException: new InvalidOperationException("listener blocked"));
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                BackFillerTestOptions.CreateValid(),
                BackFillerTestOptions.CreateValidNntpDb());
            var manager = new BackFillerApplicationServiceManager(
                [first, second],
                runtime,
                NullLogger<BackFillerApplicationServiceManager>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(CancellationToken.None));
            Assert.Equal(["start:acme", "start:listener", "stop:acme"], order);
            Assert.Empty(manager.StartedServices);
        }

        /// <summary>
        /// UnexpectedServiceTermination carries a null exception when the service execution task completes successfully.
        /// </summary>
        [Fact]
        public async Task Unexpected_termination_event_for_completed_execution_has_null_exception()
        {
            var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new MonitoredService("worker", execution.Task);
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                BackFillerTestOptions.CreateValid(),
                BackFillerTestOptions.CreateValidNntpDb());
            var manager = new BackFillerApplicationServiceManager(
                [service],
                runtime,
                NullLogger<BackFillerApplicationServiceManager>.Instance);

            Task<(string ServiceName, Exception? Exception)> termination = AwaitUnexpectedTerminationAsync(manager);
            await manager.StartAsync(CancellationToken.None);
            execution.SetResult();

            var observed = await termination;
            Assert.Equal("worker", observed.ServiceName);
            Assert.Null(observed.Exception);

            await manager.StopAsync(CancellationToken.None);
        }

        /// <summary>
        /// The manager is not disposable. Stop releases the execution watch; the host does not dispose the singleton.
        /// </summary>
        [Fact]
        public void Manager_is_not_disposable()
        {
            Assert.False(typeof(BackFillerApplicationServiceManager).IsAssignableTo(typeof(IDisposable)));
        }

        /// <summary>
        /// A canceled execution raises unexpected termination with a null exception, the same as normal completion.
        /// </summary>
        [Fact]
        public async Task Unexpected_termination_event_for_canceled_execution_has_null_exception()
        {
            var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new MonitoredService("worker", execution.Task);
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                BackFillerTestOptions.CreateValid(),
                BackFillerTestOptions.CreateValidNntpDb());
            var manager = new BackFillerApplicationServiceManager(
                [service],
                runtime,
                NullLogger<BackFillerApplicationServiceManager>.Instance);

            Task<(string ServiceName, Exception? Exception)> termination = AwaitUnexpectedTerminationAsync(manager);
            await manager.StartAsync(CancellationToken.None);
            execution.SetCanceled();

            var observed = await termination;
            Assert.Equal("worker", observed.ServiceName);
            Assert.Null(observed.Exception);

            await manager.StopAsync(CancellationToken.None);
        }

        /// <summary>
        /// UnexpectedServiceTermination surfaces the fault exception when the service execution task faults.
        /// </summary>
        [Fact]
        public async Task Unexpected_termination_event_for_faulted_execution_has_non_null_exception()
        {
            var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new MonitoredService("worker", execution.Task);
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                BackFillerTestOptions.CreateValid(),
                BackFillerTestOptions.CreateValidNntpDb());
            var manager = new BackFillerApplicationServiceManager(
                [service],
                runtime,
                NullLogger<BackFillerApplicationServiceManager>.Instance);

            Task<(string ServiceName, Exception? Exception)> termination = AwaitUnexpectedTerminationAsync(manager);
            await manager.StartAsync(CancellationToken.None);
            execution.SetException(new InvalidOperationException("boom"));

            var observed = await termination;
            Assert.Equal("worker", observed.ServiceName);
            Assert.NotNull(observed.Exception);
            Assert.IsType<InvalidOperationException>(observed.Exception);

            await manager.StopAsync(CancellationToken.None);
        }

        /// <summary>
        /// A service that waits past the shared shutdown budget causes <see cref="TimeoutException"/> identifying that service and budget.
        /// </summary>
        [Fact]
        public async Task StopAsync_reports_timeout_when_shutdown_budget_expires()
        {
            var budget = TimeSpan.FromMilliseconds(40);
            var service = new CancellationAwareService("slow-stop");
            var manager = CreateManager([service], budget);

            await manager.StartAsync(CancellationToken.None);

            var timeout = await Assert.ThrowsAsync<TimeoutException>(
                () => manager.StopAsync(CancellationToken.None));

            Assert.Contains("slow-stop", timeout.Message, StringComparison.Ordinal);
            Assert.Contains(budget.ToString(), timeout.Message, StringComparison.Ordinal);
            Assert.Empty(manager.StartedServices);
        }

        /// <summary>
        /// Caller cancellation during stop is rethrown as <see cref="OperationCanceledException"/> and is not a budget timeout.
        /// </summary>
        [Fact]
        public async Task StopAsync_caller_cancellation_is_not_classified_as_budget_timeout()
        {
            var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new CancellationAwareService("cooperative", stopEntered);
            var manager = CreateManager([service], TimeSpan.FromSeconds(30));
            await manager.StartAsync(CancellationToken.None);

            using var caller = new CancellationTokenSource();
            var stopTask = manager.StopAsync(caller.Token);
            await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            caller.Cancel();

            var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopTask);
            Assert.IsNotType<TimeoutException>(canceled);
            Assert.Empty(manager.StartedServices);
        }

        /// <summary>
        /// Unrelated stop failures remain collected in <see cref="AggregateException"/>.
        /// </summary>
        [Fact]
        public async Task StopAsync_collects_unrelated_service_exceptions()
        {
            var service = new FaultingStopService("broken", new InvalidOperationException("stop boom"));
            var manager = CreateManager([service], TimeSpan.FromSeconds(30));
            await manager.StartAsync(CancellationToken.None);

            var aggregate = await Assert.ThrowsAsync<AggregateException>(() => manager.StopAsync(CancellationToken.None));
            Assert.Contains(
                aggregate.InnerExceptions,
                static ex => ex is InvalidOperationException { Message: "stop boom" });
            Assert.Empty(manager.StartedServices);
        }

        /// <summary>
        /// After the budget expires on the first stopped service, later services are still stopped and only one timeout is reported.
        /// </summary>
        [Fact]
        public async Task StopAsync_aggregates_budget_timeout_across_remaining_services_once()
        {
            var budget = TimeSpan.FromMilliseconds(40);
            var first = new CancellationAwareService("first");
            var second = new CancellationAwareService("second");
            var manager = CreateManager([first, second], budget);
            await manager.StartAsync(CancellationToken.None);

            // Reverse stop order: second is offered the budget first.
            var timeout = await Assert.ThrowsAsync<TimeoutException>(() => manager.StopAsync(CancellationToken.None));
            Assert.Contains("second", timeout.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("first", timeout.Message, StringComparison.Ordinal);
            Assert.Empty(manager.StartedServices);
            Assert.True(first.StopObserved);
            Assert.True(second.StopObserved);
        }

        /// <summary>
        /// Normal stop within the budget succeeds and clears started services.
        /// </summary>
        [Fact]
        public async Task StopAsync_succeeds_when_services_stop_within_budget()
        {
            var order = new List<string>();
            var service = new TrackingService("quick", order);
            var manager = CreateManager([service], TimeSpan.FromSeconds(30));
            await manager.StartAsync(CancellationToken.None);
            await manager.StopAsync(CancellationToken.None);
            Assert.Equal(["start:quick", "stop:quick"], order);
            Assert.Empty(manager.StartedServices);
        }

        /// <summary>
        /// Builds a manager with a custom shutdown budget for cancellation/timeout tests.
        /// </summary>
        private static BackFillerApplicationServiceManager CreateManager(
            IEnumerable<IApplicationService> services,
            TimeSpan shutdownBudget)
        {
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                BackFillerTestOptions.CreateValid(),
                BackFillerTestOptions.CreateValidNntpDb());
            runtime = runtime with
            {
                Shutdown = runtime.Shutdown with { GracePeriod = shutdownBudget },
            };
            return new BackFillerApplicationServiceManager(
                services,
                runtime,
                NullLogger<BackFillerApplicationServiceManager>.Instance);
        }

        /// <summary>Waits for the next UnexpectedServiceTermination event or fails if none arrives within two seconds.</summary>
        /// <param name="manager">Manager whose termination event is subscribed.</param>
        private static async Task<(string ServiceName, Exception? Exception)> AwaitUnexpectedTerminationAsync(
            BackFillerApplicationServiceManager manager)
        {
            var completion = new TaskCompletionSource<(string ServiceName, Exception? Exception)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            manager.UnexpectedServiceTermination += (_, args) => completion.TrySetResult((args.ServiceName, args.Exception));

            Task completed = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(completion.Task, completed);
            return await completion.Task;
        }

        /// <summary>Minimal service exposing a monitored execution task for termination signaling tests.</summary>
        private sealed class MonitoredService : IApplicationService
        {
            /// <summary>Creates a service with the given logical name and background execution task.</summary>
            /// <param name="name">Reported service name.</param>
            /// <param name="execution">Task whose completion or fault drives termination events.</param>
            internal MonitoredService(string name, Task execution)
            {
                Name = name;
                Execution = execution;
            }

            /// <inheritdoc />
            public string Name { get; }

            /// <inheritdoc />
            public Task? Execution { get; }

            /// <inheritdoc />
            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            /// <inheritdoc />
            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        /// <summary>Records start/stop calls in a shared list; can throw on start to simulate startup failure.</summary>
        private sealed class TrackingService : IApplicationService
        {
            /// <summary>Shared list mutated with start/stop markers.</summary>
            private readonly List<string> _order;
            /// <summary>Optional exception thrown from <see cref="StartAsync"/> after recording start.</summary>
            private readonly Exception? _startException;

            /// <summary>Creates a tracker that appends start/stop markers to <paramref name="order"/>.</summary>
            /// <param name="name">Logical service name used in recorded markers.</param>
            /// <param name="order">Shared list mutated by start/stop.</param>
            /// <param name="startException">When set, thrown from <see cref="StartAsync"/> after recording start.</param>
            internal TrackingService(string name, List<string> order, Exception? startException = null)
            {
                Name = name;
                _order = order;
                _startException = startException;
            }

            /// <inheritdoc />
            public string Name { get; }

            /// <inheritdoc />
            public Task? Execution => null;

            /// <inheritdoc />
            public Task StartAsync(CancellationToken cancellationToken)
            {
                _order.Add("start:" + Name);
                if (_startException is not null)
                {
                    throw _startException;
                }

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task StopAsync(CancellationToken cancellationToken)
            {
                _order.Add("stop:" + Name);
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Stop waits until the supplied token is canceled, then throws <see cref="OperationCanceledException"/>.
        /// </summary>
        private sealed class CancellationAwareService : IApplicationService
        {
            /// <summary>Optional signal completed when <see cref="StopAsync"/> begins waiting.</summary>
            private readonly TaskCompletionSource? _stopEntered;

            /// <summary>Creates a service that blocks in <see cref="StopAsync"/> until canceled.</summary>
            /// <param name="name">Logical service name.</param>
            /// <param name="stopEntered">When set, completed as soon as stop begins waiting on the token.</param>
            internal CancellationAwareService(string name, TaskCompletionSource? stopEntered = null)
            {
                Name = name;
                _stopEntered = stopEntered;
            }

            /// <inheritdoc />
            public string Name { get; }

            /// <inheritdoc />
            public Task? Execution => null;

            /// <summary>True after <see cref="StopAsync"/> has been entered.</summary>
            internal bool StopObserved { get; private set; }

            /// <inheritdoc />
            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            /// <inheritdoc />
            public async Task StopAsync(CancellationToken cancellationToken)
            {
                StopObserved = true;
                _stopEntered?.TrySetResult();
                if (cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = cancellationToken.Register(
                    static state =>
                    {
                        var (source, token) = ((TaskCompletionSource, CancellationToken))state!;
                        source.TrySetCanceled(token);
                    },
                    (completion, cancellationToken));
                await completion.Task.ConfigureAwait(false);
            }
        }

        /// <summary>Throws a configured exception from <see cref="StopAsync"/>.</summary>
        private sealed class FaultingStopService : IApplicationService
        {
            /// <summary>Exception thrown from <see cref="StopAsync"/>.</summary>
            private readonly Exception _stopException;

            /// <summary>Creates a service whose stop fails with <paramref name="stopException"/>.</summary>
            /// <param name="name">Logical service name.</param>
            /// <param name="stopException">Failure raised from stop.</param>
            internal FaultingStopService(string name, Exception stopException)
            {
                Name = name;
                _stopException = stopException;
            }

            /// <inheritdoc />
            public string Name { get; }

            /// <inheritdoc />
            public Task? Execution => null;

            /// <inheritdoc />
            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            /// <inheritdoc />
            public Task StopAsync(CancellationToken cancellationToken) => Task.FromException(_stopException);
        }
    }
}

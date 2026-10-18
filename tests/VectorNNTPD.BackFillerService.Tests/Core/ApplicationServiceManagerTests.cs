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
    }
}

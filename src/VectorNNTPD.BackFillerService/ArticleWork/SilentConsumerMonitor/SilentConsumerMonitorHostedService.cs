using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Configuration;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.Common.Core;
using VectorNNTPD.Common.Messaging.RabbitMq;

namespace VectorNNTPD.BackFillerService.ArticleWork.SilentConsumerMonitor
{
    /// <summary>
    /// Background silent-consumer monitor hosted inside BackFillerService.
    /// Failures are logged and never propagate into article processing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Observational only: never calls <c>RetireForRecoveryAsync</c> or changes ACK/NACK.
    /// Event 5328 recovery remains owned by Article Work reconcile.
    /// </para>
    /// <para>
    /// Each cycle projects live sessions, runs <see cref="SilentConsumerMonitorEngine.ObserveInstance"/>,
    /// prunes shared targets removed from the catalog, then (when Management is ready) polls queues.
    /// Instance evaluation always runs before the Management gate so missing BaseUrl/credentials or
    /// HTTP failures do not disable local monitoring.
    /// </para>
    /// <para>
    /// Virtual host, Management base URL, and credentials come from live <see cref="RabbitMqOptions"/>
    /// (credentials refreshed from <c>nntpsharedconfig</c>). Queue targets come from the MySQL-backed
    /// provider catalog each cycle.
    /// </para>
    /// </remarks>
    internal sealed class SilentConsumerMonitorHostedService : BackgroundService
    {
        /// <summary>In-process observation engine (instance + shared evaluation and diagnostics).</summary>
        private readonly SilentConsumerMonitorEngine _engine;

        /// <summary>HTTP Management client used for shared-queue polls.</summary>
        private readonly ManagementQueueClient _management;

        /// <summary>Live Article Work consumer sessions projected each cycle.</summary>
        private readonly ArticleWorkConsumerService _consumers;

        /// <summary>Usable NNTP capacity used for expected-active targets and desired session counts.</summary>
        private readonly IBackboneUsableCapacityProvider _capacity;

        /// <summary>MySQL-backed provider catalog used to resolve queue targets each cycle.</summary>
        private readonly IBackFillerProviderCatalog _catalog;

        /// <summary>Public enable switch under <c>BackFiller:SilentConsumerMonitor</c>.</summary>
        private readonly IOptions<SilentConsumerMonitorOptions> _options;

        /// <summary>Live RabbitMQ options (vhost, Management BaseUrl, credentials).</summary>
        private readonly IOptions<RabbitMqOptions> _rabbitMq;

        /// <summary>Runtime options; <see cref="BackFillerRuntimeOptions.Fqdn"/> stamps diagnostic instance id.</summary>
        private readonly BackFillerRuntimeOptions _runtime;

        /// <summary>Hosted-service logger for Events 9100+.</summary>
        private readonly ILogger<SilentConsumerMonitorHostedService> _logger;

        /// <summary>Clock used for poll-interval <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>.</summary>
        private readonly TimeProvider _time;

        /// <summary>True after Event 9123 until Management becomes ready again (emit once per outage).</summary>
        private bool _loggedManagementUnavailable;

        /// <summary>
        /// Creates the background silent-consumer monitor hosted inside BackFillerService.
        /// </summary>
        /// <param name="engine">Observation engine that owns in-memory state machines and sample history.</param>
        /// <param name="management">Management HTTP client for shared-queue polls.</param>
        /// <param name="consumers">Article Work consumer service whose sessions are projected each cycle.</param>
        /// <param name="capacity">Usable backbone capacity for expected-active and desired-session counting.</param>
        /// <param name="catalog">Provider catalog supplying backbone/MaxSessions for queue targets.</param>
        /// <param name="options">Enable switch; when disabled the loop logs start and returns.</param>
        /// <param name="rabbitMq">Live RabbitMQ options for vhost/Management readiness and credentials.</param>
        /// <param name="runtime">Runtime options providing the FQDN stamped on diagnostics.</param>
        /// <param name="logger">Structured logger.</param>
        /// <param name="time">Time provider for cycle delay.</param>
        /// <exception cref="ArgumentNullException">Thrown when any constructor argument is null.</exception>
        public SilentConsumerMonitorHostedService(
            SilentConsumerMonitorEngine engine,
            ManagementQueueClient management,
            ArticleWorkConsumerService consumers,
            IBackboneUsableCapacityProvider capacity,
            IBackFillerProviderCatalog catalog,
            IOptions<SilentConsumerMonitorOptions> options,
            IOptions<RabbitMqOptions> rabbitMq,
            BackFillerRuntimeOptions runtime,
            ILogger<SilentConsumerMonitorHostedService> logger,
            TimeProvider time)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(management);
            ArgumentNullException.ThrowIfNull(consumers);
            ArgumentNullException.ThrowIfNull(capacity);
            ArgumentNullException.ThrowIfNull(catalog);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(rabbitMq);
            ArgumentNullException.ThrowIfNull(runtime);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(time);
            _engine = engine;
            _management = management;
            _consumers = consumers;
            _capacity = capacity;
            _catalog = catalog;
            _options = options;
            _rabbitMq = rabbitMq;
            _runtime = runtime;
            _logger = logger;
            _time = time;
        }

        /// <summary>
        /// Runs the monitor loop until cancelled or disabled: project sessions, observe instance,
        /// prune catalog targets, optionally poll Management, then delay one poll interval.
        /// </summary>
        /// <param name="stoppingToken">Host shutdown token; cancels delay and in-flight polls.</param>
        /// <returns>A task that completes when the loop exits (disabled, cancelled, or host stop).</returns>
        /// <remarks>
        /// Non-fatal cycle exceptions are logged as Management-degraded and do not stop the host.
        /// Fatal exceptions are not caught here.
        /// </remarks>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Value.Enabled)
            {
                SilentConsumerMonitorLogMessages.Starting(_logger, _runtime.Fqdn, enabled: false, queueCount: 0);
                return;
            }

            var startedLogged = false;
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var targets = SilentConsumerMonitorTargets.Resolve(_catalog, _capacity);
                        if (!startedLogged)
                        {
                            SilentConsumerMonitorLogMessages.Starting(
                                _logger,
                                _runtime.Fqdn,
                                enabled: true,
                                queueCount: targets.Count);
                            startedLogged = true;
                        }

                        var observations = ProjectSessions();
                        var desired = CountDesiredSessions();
                        _ = _engine.ObserveInstance(observations, desired);
                        _engine.PruneQueues(targets.Select(static t => t.QueueName).ToArray());

                        if (!TryGetManagementReady(out var virtualHost, out var unavailableReason))
                        {
                            if (!_loggedManagementUnavailable)
                            {
                                SilentConsumerMonitorLogMessages.ManagementUnavailable(
                                    _logger,
                                    _runtime.Fqdn,
                                    unavailableReason);
                                _loggedManagementUnavailable = true;
                            }
                        }
                        else
                        {
                            if (_loggedManagementUnavailable)
                            {
                                _loggedManagementUnavailable = false;
                                SilentConsumerMonitorLogMessages.ManagementRecovered(_logger, _runtime.Fqdn);
                            }

                            foreach (var target in targets)
                            {
                                stoppingToken.ThrowIfCancellationRequested();
                                try
                                {
                                    var poll = await _management
                                        .GetQueueAsync(virtualHost, target.QueueName, stoppingToken)
                                        .ConfigureAwait(false);
                                    _ = _engine.ObserveSharedQueue(
                                        target.QueueName,
                                        poll,
                                        target.ExpectedActive);
                                }
                                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                                {
                                    break;
                                }
                                catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                                {
                                    SilentConsumerMonitorLogMessages.ManagementPollFailed(
                                        _logger,
                                        _runtime.Fqdn,
                                        target.QueueName,
                                        "Exception",
                                        ex.GetType().Name);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                    {
                        SilentConsumerMonitorLogMessages.ManagementDegraded(
                            _logger,
                            _runtime.Fqdn,
                            consecutiveFailures: 1,
                            reason: "cycle:" + ex.GetType().Name);
                    }

                    try
                    {
                        await Task
                            .Delay(
                                TimeSpan.FromSeconds(SilentConsumerMonitorDefaults.PollIntervalSeconds),
                                _time,
                                stoppingToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
            finally
            {
                SilentConsumerMonitorLogMessages.Stopped(_logger, _runtime.Fqdn);
            }
        }

        /// <summary>
        /// Returns whether live <see cref="RabbitMqOptions"/> can support a Management poll this cycle.
        /// Does not perform HTTP; a false result skips shared evaluation (instance monitoring already ran).
        /// </summary>
        /// <param name="virtualHost">
        /// Virtual host to poll (trimmed; defaults to <c>/</c> when options VirtualHost is blank).
        /// </param>
        /// <param name="reason">Non-secret unavailability reason when returning false; empty on success.</param>
        /// <returns>
        /// <see langword="true"/> when Management BaseUrl is configured and Username/Password are present
        /// (Password may be empty string once projected; null Password is treated as not ready).
        /// </returns>
        private bool TryGetManagementReady(out string virtualHost, out string reason)
        {
            var rabbit = _rabbitMq.Value;
            virtualHost = string.IsNullOrWhiteSpace(rabbit.VirtualHost) ? "/" : rabbit.VirtualHost.Trim();
            var baseUrl = rabbit.Management?.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                reason = "RabbitMQ:Management:BaseUrl is not configured.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(rabbit.Username) || rabbit.Password is null)
            {
                reason = "RabbitMQ credentials are not available yet (awaiting nntpsharedconfig projection).";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// Projects tracked consumer sessions with the same stall threshold reconcile uses (Event 5328).
        /// </summary>
        /// <returns>Immutable session projections for the current cycle.</returns>
        private List<SilentConsumerSessionObservation> ProjectSessions()
        {
            var sessions = _consumers.Sessions;
            var projected = new List<SilentConsumerSessionObservation>(sessions.Count);
            foreach (var session in sessions)
            {
                projected.Add(
                    SilentConsumerSessionObservation.FromSession(
                        session,
                        ArticleWorkConsumerService.ProcessingStallThreshold));
            }

            return projected;
        }

        /// <summary>
        /// Mirrors <c>ArticleWorkConsumerService.BuildDesiredSessions</c> slot counting:
        /// MaxSessions for each provider with usable NNTP capacity.
        /// </summary>
        /// <returns>Sum of MaxSessions across providers that currently have usable backbone capacity.</returns>
        private int CountDesiredSessions()
        {
            var total = 0;
            foreach (var provider in _catalog.Providers)
            {
                if (provider.MaxSessions <= 0)
                {
                    continue;
                }

                if (!_capacity.HasUsableCapacityForBackbone(provider.Backbone))
                {
                    continue;
                }

                total += provider.MaxSessions;
            }

            return total;
        }
    }
}

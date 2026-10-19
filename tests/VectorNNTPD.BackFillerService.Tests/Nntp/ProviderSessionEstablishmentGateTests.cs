using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTPD.BackFillerService.Accounts;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.BackFillerService.Tests.TestDoubles;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace VectorNNTPD.BackFillerService.Tests.Nntp
{
    /// <summary>
    /// Global cap on concurrent NNTP session establishment across pools and registry apply.
    /// </summary>

    public sealed class ProviderSessionEstablishmentGateTests
    {
        /// <summary>Receives the establishment trace when a steady-state count assertion fails.</summary>
        private readonly ITestOutputHelper _output;

        /// <summary>Captures <paramref name="output"/> for the steady-state failure trace.</summary>
        /// <param name="output">xUnit output written only when that assertion fails.</param>
        public ProviderSessionEstablishmentGateTests(ITestOutputHelper output)
        {
            _output = output;
        }

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
        /// <remarks>
        /// A short active count still fails <see cref="Assert.Equal{T}(T, T)"/>.
        /// The original assertion is rethrown. Its xUnit output then lists each attempt's connection,
        /// terminal result, failure reason, and elapsed time, plus the final pool counts.
        /// </remarks>

        [Fact]

        public async Task Steady_state_is_not_globally_capped_after_establishment()
        {
            var factory = new ScriptedNntpTransportFactory();
            var logA = new EstablishmentAttemptLog();
            var logB = new EstablishmentAttemptLog();
            NntpSessionPoolTests.EnqueueReadyServers(factory, count: 6);
            using var gate = new ProviderSessionEstablishmentGate(1);
            await using var poolA = NntpSessionPoolTests.CreatePool(factory, max: 3, establishment: gate, backbone: "A", logger: logA);
            await using var poolB = NntpSessionPoolTests.CreatePool(factory, max: 3, establishment: gate, backbone: "B", logger: logB);

            await Task.WhenAll(
                poolA.EnsureDesiredSessionsAsync(CancellationToken.None),
                poolB.EnsureDesiredSessionsAsync(CancellationToken.None));

            try
            {
                Assert.Equal(3, poolA.ActiveSessionCount);
                Assert.Equal(3, poolB.ActiveSessionCount);
                Assert.Equal(1, factory.MaxObservedConcurrentConnects);
            }
            catch (XunitException)
            {
                _output.WriteLine(DescribeFailure(factory, poolA, logA, poolB, logB));
                throw;
            }
        }

        /// <summary>
        /// Failure text names the attempt and its reason, and does not keep an AUTHINFO argument.
        /// </summary>
        [Fact]
        public void Establishment_diagnostic_names_the_failed_attempt_and_redacts_credentials()
        {
            var log = new EstablishmentAttemptLog();
            NntpLogMessages.SessionEstablishmentWaiting(log, "A", 1);
            NntpLogMessages.SessionEstablishmentStarted(log, "A", 1);
            NntpLogMessages.SessionEstablishmentCompleted(log, "A", 1);
            NntpLogMessages.SessionEstablishmentWaiting(log, "A", 2);
            NntpLogMessages.SessionEstablishmentStarted(log, "A", 2);
            NntpLogMessages.SessionEstablishmentFailed(log, "A", 2, "NNTP connect timed out.");
            NntpLogMessages.SessionEstablishmentFailed(log, "A", 3, "AUTHINFO PASS secret-value");

            var text = log.Describe();

            Assert.Contains("connection=1 result=ready", text, StringComparison.Ordinal);
            Assert.Contains("connection=2 result=failed reason=NNTP connect timed out.", text, StringComparison.Ordinal);
            Assert.Contains("connection=3 result=failed reason=AUTHINFO PASS ***", text, StringComparison.Ordinal);
            Assert.Contains("elapsedMs=", text, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-value", text, StringComparison.Ordinal);
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

        /// <summary>Formats pool counts and per-attempt establishment results after a failed assertion.</summary>
        /// <param name="factory">Shared scripted transport. Connects are counted per backbone.</param>
        /// <param name="poolA">First warm-up pool.</param>
        /// <param name="logA">Establishment log for <paramref name="poolA"/>.</param>
        /// <param name="poolB">Second warm-up pool.</param>
        /// <param name="logB">Establishment log for <paramref name="poolB"/>.</param>
        /// <returns>Failure output. AUTHINFO arguments are redacted.</returns>
        private static string DescribeFailure(
            ScriptedNntpTransportFactory factory,
            NntpSessionPool poolA,
            EstablishmentAttemptLog logA,
            NntpSessionPool poolB,
            EstablishmentAttemptLog logB)
        {
            var builder = new StringBuilder();
            AppendPool(builder, factory, poolA, logA);
            AppendPool(builder, factory, poolB, logB);
            builder.Append("maxConcurrentConnects=");
            builder.AppendLine(factory.MaxObservedConcurrentConnects.ToString(CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        /// <summary>Appends one pool's final counts and its attempt lines.</summary>
        /// <param name="builder">Failure text being built.</param>
        /// <param name="factory">Transport whose connects are counted for this backbone.</param>
        /// <param name="pool">Pool observed after warm-up.</param>
        /// <param name="log">Structured establishment log for <paramref name="pool"/>.</param>
        private static void AppendPool(
            StringBuilder builder,
            ScriptedNntpTransportFactory factory,
            NntpSessionPool pool,
            EstablishmentAttemptLog log)
        {
            var connects = factory.ConnectAttempts.Count(attempt =>
                string.Equals(attempt.Backbone, pool.Backbone, StringComparison.Ordinal));

            builder.Append("pool=").Append(pool.Backbone);
            builder.Append(" desired=").Append(pool.Provider.MaxSessions.ToString(CultureInfo.InvariantCulture));
            builder.Append(" active=").Append(pool.ActiveSessionCount.ToString(CultureInfo.InvariantCulture));
            builder.Append(" live=").Append(pool.LiveSessionCount.ToString(CultureInfo.InvariantCulture));
            builder.Append(" idle=").Append(pool.IdleSessionCount.ToString(CultureInfo.InvariantCulture));
            builder.Append(" leases=").Append(pool.ActiveLeaseCount.ToString(CultureInfo.InvariantCulture));
            builder.Append(" transportConnects=").AppendLine(connects.ToString(CultureInfo.InvariantCulture));
            builder.Append(log.Describe());
        }

        /// <summary>
        /// Records structured establishment events for one pool.
        /// Wire traffic is not stored. <see cref="IsEnabled"/> still returns true for Debug because
        /// <see cref="NntpLogMessages.SessionEstablishmentFailed"/> is a Debug event, and that same check
        /// also lets the session copy status lines before it logs them.
        /// </summary>
        private sealed class EstablishmentAttemptLog : ILogger
        {
            private const int WaitingEventId = 5412;
            private const int StartedEventId = 5413;
            private const int CompletedEventId = 5414;
            private const int CancelledEventId = 5415;
            private const int FailedEventId = 5416;

            private readonly object _gate = new();
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly List<Observation> _events = [];

            /// <inheritdoc />
            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull => NoScope.Instance;

            /// <inheritdoc />
            public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Debug;

            /// <inheritdoc />
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id is not (WaitingEventId or StartedEventId or CompletedEventId or CancelledEventId or FailedEventId))
                {
                    return;
                }

                if (!TryRead(state, "ConnectionNumber", out var connectionValue) || connectionValue is not int connection)
                {
                    return;
                }

                string? reason = null;
                if (eventId.Id == FailedEventId
                    && TryRead(state, "Reason", out var reasonValue)
                    && reasonValue is string reasonText)
                {
                    reason = Scrub(reasonText);
                }

                var observation = new Observation(_clock.Elapsed, eventId.Id, connection, reason);
                lock (_gate)
                {
                    _events.Add(observation);
                }
            }

            /// <summary>Formats one line per connection from the structured establishment events.</summary>
            /// <returns>Attempt identity, terminal result, reason, and elapsed milliseconds.</returns>
            internal string Describe()
            {
                Observation[] events;
                var now = _clock.Elapsed;
                lock (_gate)
                {
                    events = [.. _events];
                }

                var attempts = new SortedDictionary<int, Attempt>();
                foreach (var observation in events)
                {
                    if (!attempts.TryGetValue(observation.Connection, out var attempt))
                    {
                        attempt = new Attempt(observation.Connection);
                        attempts.Add(observation.Connection, attempt);
                    }

                    attempt.Apply(observation);
                }

                var builder = new StringBuilder();
                foreach (var attempt in attempts.Values)
                {
                    builder.AppendLine(attempt.Summarize(now));
                }

                return builder.ToString();
            }

            private static bool TryRead(object? state, string name, out object? value)
            {
                if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    foreach (var pair in pairs)
                    {
                        if (string.Equals(pair.Key, name, StringComparison.Ordinal))
                        {
                            value = pair.Value;
                            return true;
                        }
                    }
                }

                value = null;
                return false;
            }

            private static string Scrub(string reason)
            {
                reason = ScrubCommand(reason, "AUTHINFO USER ");
                return ScrubCommand(reason, "AUTHINFO PASS ");
            }

            private static string ScrubCommand(string reason, string marker)
            {
                var index = reason.IndexOf(marker, StringComparison.Ordinal);
                if (index < 0)
                {
                    return reason;
                }

                var valueStart = index + marker.Length;
                if (reason.AsSpan(valueStart).StartsWith("***", StringComparison.Ordinal))
                {
                    return reason;
                }

                var valueEnd = valueStart;
                while (valueEnd < reason.Length && !char.IsWhiteSpace(reason[valueEnd]))
                {
                    valueEnd++;
                }

                return string.Concat(reason.AsSpan(0, valueStart), "***", reason.AsSpan(valueEnd));
            }

            private readonly record struct Observation(TimeSpan Elapsed, int EventId, int Connection, string? Reason);

            private sealed class Attempt(int connection)
            {
                private TimeSpan? _origin;
                private TimeSpan? _terminal;
                private string _result = "incomplete";
                private string? _reason;

                internal string Summarize(TimeSpan now)
                {
                    var end = _terminal ?? now;
                    var elapsed = _origin is { } origin ? end - origin : TimeSpan.Zero;
                    var text = new StringBuilder();
                    text.Append("attempt connection=").Append(connection.ToString(CultureInfo.InvariantCulture));
                    text.Append(" result=").Append(_result);
                    if (!string.IsNullOrEmpty(_reason))
                    {
                        text.Append(" reason=").Append(_reason);
                    }

                    text.Append(" elapsedMs=").Append(FormatMilliseconds(elapsed));
                    return text.ToString();
                }

                internal void Apply(Observation observation)
                {
                    _origin ??= observation.Elapsed;
                    switch (observation.EventId)
                    {
                        case CompletedEventId:
                            SetTerminal("ready", observation.Elapsed, reason: null);
                            break;
                        case CancelledEventId:
                            SetTerminal("cancelled", observation.Elapsed, reason: null);
                            break;
                        case FailedEventId:
                            SetTerminal("failed", observation.Elapsed, observation.Reason);
                            break;
                    }
                }

                private void SetTerminal(string result, TimeSpan elapsed, string? reason)
                {
                    if (_terminal is not null)
                    {
                        return;
                    }

                    _terminal = elapsed;
                    _result = result;
                    _reason = reason;
                }
            }

            private static string FormatMilliseconds(TimeSpan elapsed)
            {
                var milliseconds = elapsed.TotalMilliseconds;
                if (milliseconds < 0)
                {
                    milliseconds = 0;
                }

                return Math.Round(milliseconds).ToString("0", CultureInfo.InvariantCulture);
            }

            private sealed class NoScope : IDisposable
            {
                internal static NoScope Instance { get; } = new();

                public void Dispose()
                {
                }
            }
        }
    }
}

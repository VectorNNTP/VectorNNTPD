using VectorNNTPD.Common.Core;
using VectorNNTPD.ReaderService.Session;

namespace VectorNNTPD.ReaderService.Transit
{
    /// <summary>
    /// Refreshes Transit AllowFrom DNS names according to each hostname's TTL (minimum 60 seconds).
    /// </summary>
    /// <remarks>
    /// Does not poll <c>VectorNNTPD.ReaderService.json</c>. Configuration reloads arrive through
    /// <see cref="TransitConfigurationStore"/> (IOptionsMonitor). The wait interval is the
    /// next hostname due time, not a fixed poll.
    /// </remarks>
    public sealed class TransitDnsRefreshService : IApplicationService, IAsyncDisposable
    {
        private readonly TransitConfigurationStore _store;
        private readonly ITransitDnsAddressCache _cache;
        private readonly ILogger<TransitDnsRefreshService> _logger;
        private readonly CancellationTokenSource _runCts = new();
        private IDisposable? _subscription;
        private TaskCompletionSource _wakeup = NewWakeup();
        private Task? _execution;
        private int _disposed;

        /// <summary>Initializes a new instance of the <see cref="TransitDnsRefreshService"/> class.</summary>
        public TransitDnsRefreshService(
            TransitConfigurationStore store,
            ITransitDnsAddressCache cache,
            ITransitPeerAuthorization authorization,
            ILogger<TransitDnsRefreshService> logger)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(cache);
            ArgumentNullException.ThrowIfNull(authorization);
            ArgumentNullException.ThrowIfNull(logger);
            _store = store;
            _cache = cache;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "TransitDnsRefresh";

        /// <inheritdoc />
        public Task? Execution => _execution;

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _cache.RefreshAllAsync(_store.Current, cancellationToken).ConfigureAwait(false);
            _subscription = _store.Subscribe(_ => _wakeup.TrySetResult());
            _execution = RunAsync(_runCts.Token);
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _subscription?.Dispose();
            _subscription = null;
            await _runCts.CancelAsync().ConfigureAwait(false);
            _wakeup.TrySetResult();
            if (_execution is not null)
            {
                try
                {
                    await _execution.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    _ = ex; // observed; intentionally ignored at this boundary
                    // Expected.
                }
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _subscription?.Dispose();
            await _runCts.CancelAsync().ConfigureAwait(false);
            _runCts.Dispose();
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var delay = _cache.GetDelayUntilNextRefresh(_store.Current);
                    var wakeup = _wakeup;
                    try
                    {
                        if (delay == Timeout.InfiniteTimeSpan)
                        {
                            await wakeup.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                        }
                        else if (delay > TimeSpan.Zero)
                        {
                            var delayTask = Task.Delay(delay, cancellationToken);
                            await Task.WhenAny(delayTask, wakeup.Task).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    Interlocked.Exchange(ref _wakeup, NewWakeup());
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await _cache.RefreshDueAsync(_store.Current, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
                // Shutdown.
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                TransitLogMessages.RefreshLoopTerminated(_logger, ex);
                throw;
            }
        }

        private static TaskCompletionSource NewWakeup() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

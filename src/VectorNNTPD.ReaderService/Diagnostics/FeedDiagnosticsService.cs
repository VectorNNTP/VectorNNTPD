using Microsoft.Extensions.Options;
using VectorNNTPD.ReaderService.ArticleIngestion;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Core;
using VectorNNTPD.ReaderService.Transit;

namespace VectorNNTPD.ReaderService.Diagnostics
{
    /// <summary>
    /// Periodic feed-diagnostics reporter. Does not run when <see cref="IFeedDiagnostics.IsEnabled"/> is false.
    /// </summary>
    public sealed class FeedDiagnosticsService : IApplicationService, IAsyncDisposable
    {
        private readonly IFeedDiagnostics _feed;
        private readonly IArticleIngestionQueue _queue;
        private readonly TransitConfigurationStore _store;
        private readonly IOptions<NntpdOptions> _options;
        private readonly ILogger<FeedDiagnosticsService> _logger;
        private readonly CancellationTokenSource _runCts = new();
        private Task? _execution;
        private int _disposed;

        /// <summary>Initializes a new instance of the <see cref="FeedDiagnosticsService"/> class.</summary>
        public FeedDiagnosticsService(
            IFeedDiagnostics feed,
            IArticleIngestionQueue queue,
            TransitConfigurationStore store,
            IOptions<NntpdOptions> options,
            ILogger<FeedDiagnosticsService> logger)
        {
            ArgumentNullException.ThrowIfNull(feed);
            ArgumentNullException.ThrowIfNull(queue);
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);
            _feed = feed;
            _queue = queue;
            _store = store;
            _options = options;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "FeedDiagnostics";

        /// <inheritdoc />
        public Task? Execution => _execution;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_feed.IsEnabled)
            {
                return Task.CompletedTask;
            }

            var interval = _options.Value.FeedDiagnostics?.IntervalSeconds
                ?? FeedDiagnosticsOptions.DefaultIntervalSeconds;
            var includeSessions = _options.Value.FeedDiagnostics?.IncludeSessions ?? true;
            FeedDiagnosticsLogMessages.Enabled(_logger, interval, includeSessions);
            _execution = RunAsync(TimeSpan.FromSeconds(interval), includeSessions, _runCts.Token);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _runCts.CancelAsync().ConfigureAwait(false);
            if (_execution is null)
            {
                return;
            }

            try
            {
                await _execution.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }

            if (_feed.IsEnabled)
            {
                FeedDiagnosticsLogMessages.Stopped(_logger);
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ignored) when (!ExceptionHelpers.IsFatal(ignored))
            {
                _ = ignored; // observed; intentionally ignored at this best-effort boundary
            }

            _runCts.Dispose();
        }

        private async Task RunAsync(TimeSpan interval, bool includeSessions, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                    var snapshot = _feed.CaptureSnapshot(_queue, _store);
                    FeedDiagnosticsLogMessages.Snapshot(_logger, FeedDiagnosticsFormatter.Format(snapshot, includeSessions));
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                _ = ex; // observed; intentionally ignored at this boundary
            }
        }
    }
}

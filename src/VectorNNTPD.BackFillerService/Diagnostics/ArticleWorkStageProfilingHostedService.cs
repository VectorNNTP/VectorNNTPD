using Microsoft.Extensions.Options;
using VectorNNTPD.BackFillerService.Configuration;

namespace VectorNNTPD.BackFillerService.Diagnostics
{
    /// <summary>
    /// Installs the lab stage profiler when enabled and optionally exports JSON snapshots.
    /// </summary>
    internal sealed class ArticleWorkStageProfilingHostedService : IHostedService, IAsyncDisposable
    {
        private readonly LabStageProfilingOptions _options;
        private readonly ArticleWorkStageProfiler _profiler;
        private readonly ILogger<ArticleWorkStageProfilingHostedService> _logger;
        private CancellationTokenSource? _exportCts;
        private Task? _exportLoop;

        /// <summary>Creates the hosted service bound to validated options.</summary>
        public ArticleWorkStageProfilingHostedService(
            IOptions<LabStageProfilingOptions> options,
            ILogger<ArticleWorkStageProfilingHostedService> logger)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);
            _options = options.Value;
            _logger = logger;
            _profiler = new ArticleWorkStageProfiler(_options.Enabled);
        }

        /// <summary>The profiler instance owned by this hosted service.</summary>
        internal ArticleWorkStageProfiler Profiler => _profiler;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (!_options.Enabled)
            {
                ArticleWorkStageProfiler.Install(null);
                _logger.LogInformation("Lab Article Work stage profiling is disabled.");
                return Task.CompletedTask;
            }

            ArticleWorkStageProfiler.Install(_profiler);
            _logger.LogInformation(
                "Lab Article Work stage profiling enabled. ExportDirectory={ExportDirectory} IntervalSeconds={IntervalSeconds}",
                string.IsNullOrWhiteSpace(_options.ExportDirectory) ? "(none)" : _options.ExportDirectory,
                _options.ExportIntervalSeconds);

            if (!string.IsNullOrWhiteSpace(_options.ExportDirectory))
            {
                Directory.CreateDirectory(_options.ExportDirectory);
                _exportCts = new CancellationTokenSource();
                _exportLoop = ExportLoopAsync(_exportCts.Token);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_exportCts is not null)
            {
                await _exportCts.CancelAsync().ConfigureAwait(false);
            }

            if (_exportLoop is not null)
            {
                try
                {
                    await _exportLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogTrace("Stage profiling export loop wait cancelled during stop.");
                }
                catch (Exception ex) when (IsExportIoFailure(ex))
                {
                    _logger.LogDebug(ex, "Stage profiling export loop ended with I/O error.");
                }
            }

            if (_options.Enabled && !string.IsNullOrWhiteSpace(_options.ExportDirectory))
            {
                try
                {
                    await WriteSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsExportIoFailure(ex))
                {
                    _logger.LogWarning(ex, "Failed to write final stage profiling snapshot.");
                }
            }

            ArticleWorkStageProfiler.Install(null);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_exportCts is not null)
            {
                await _exportCts.CancelAsync().ConfigureAwait(false);
                _exportCts.Dispose();
            }

            ArticleWorkStageProfiler.Install(null);
        }

        private async Task ExportLoopAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.ExportIntervalSeconds));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    await WriteSnapshotAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogTrace("Stage profiling export loop cancelled.");
            }
        }

        private Task WriteSnapshotAsync(CancellationToken cancellationToken)
        {
            var path = Path.Join(_options.ExportDirectory!, "article-work-stage-profile.json");
            return File.WriteAllTextAsync(path, _profiler.ToJson(), cancellationToken);
        }

        /// <summary>
        /// True for expected snapshot export I/O failures. Programming errors and unexpected faults are not matched.
        /// <see cref="DirectoryNotFoundException"/> / <see cref="PathTooLongException"/> are covered by <see cref="IOException"/>.
        /// </summary>
        private static bool IsExportIoFailure(Exception ex) =>
            ex is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException;
    }
}

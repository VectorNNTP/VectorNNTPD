using VectorNNTPD.Common.Core;
using VectorNNTPD.TransitService.Redis;

namespace VectorNNTPD.TransitService.History
{
    /// <summary>Background consumer that persists HistoryDB markers to Redis.</summary>
    public sealed class HistoryWriteService : IApplicationService
    {
        private readonly HistoryDb _history;
        private readonly IRedisService _redis;
        private readonly ILogger<HistoryWriteService> _logger;
        private readonly CancellationTokenSource _runCts = new();
        private Task? _execution;
        private int _started;

        /// <summary>Initializes a new instance of the <see cref="HistoryWriteService"/> class.</summary>
        public HistoryWriteService(
            HistoryDb history,
            IRedisService redis,
            ILogger<HistoryWriteService> logger)
        {
            ArgumentNullException.ThrowIfNull(history);
            ArgumentNullException.ThrowIfNull(redis);
            ArgumentNullException.ThrowIfNull(logger);
            _history = history;
            _redis = redis;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "HistoryWrite";

        /// <inheritdoc />
        public Task? Execution => _execution;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return Task.CompletedTask;
            }

            _execution = Task.Run(() => RunAsync(_runCts.Token), CancellationToken.None);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _history.Writes.Complete();
            await _runCts.CancelAsync().ConfigureAwait(false);

            var execution = _execution;
            if (execution is null)
            {
                return;
            }

            try
            {
                await execution.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                HistoryLogMessages.WriterStoppedWithQueued(_logger, _history.Writes.Count);
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            HistoryLogMessages.WriterStarted(_logger, _history.Writes.Capacity, _history.Retention);
            while (true)
            {
                HistoryDigest? digest;
                try
                {
                    digest = await _history.Writes.DequeueAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (digest is null)
                {
                    break;
                }

                if (!_redis.TryBeginOperation(out var isRecoveryProbe))
                {
                    continue;
                }

                try
                {
                    var key = HistoryRedisKeys.Create(digest.Value);
                    await _redis.SetAsync(key, HistoryDbMarker.Value, _history.Retention, CancellationToken.None)
                        .ConfigureAwait(false);
                    _redis.CompleteOperation(isRecoveryProbe, succeeded: true);
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex)
                {
                    _redis.CompleteOperation(isRecoveryProbe, succeeded: false, ex);
                    HistoryLogMessages.RedisWriteFailed(_logger, ex);
                }

                if (cancellationToken.IsCancellationRequested && _history.Writes.Count == 0)
                {
                    break;
                }
            }

            HistoryLogMessages.WriterStopped(_logger);
        }
    }

    /// <summary>Single-byte Redis value stored for a HistoryDB marker.</summary>
    internal static class HistoryDbMarker
    {
        /// <summary>Presence marker. Existence, not payload, is the history signal.</summary>
        internal static readonly byte[] Value = [1];
    }
}

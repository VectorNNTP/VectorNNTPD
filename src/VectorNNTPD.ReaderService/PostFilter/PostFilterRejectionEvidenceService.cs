using VectorNNTPD.Common.Core;
using VectorNNTPD.Common.NntpDb;
using VectorNNTPD.ReaderService.NntpDb;

namespace VectorNNTPD.ReaderService.PostFilter
{
    /// <summary>
    /// Background writer for PostFilter rejection evidence. Completes independently
    /// of the POST decision. Write failures are counted and logged; they never
    /// change 441/240 semantics.
    /// </summary>
    public sealed class PostFilterRejectionEvidenceService : IApplicationService
    {
        private readonly NntpDbService _nntpDb;
        private readonly PostFilterRejectionEvidenceQueue _queue;
        private readonly ILogger<PostFilterRejectionEvidenceService> _logger;
        private readonly CancellationTokenSource _runCts = new();
        private Task? _execution;
        private int _started;

        /// <summary>Initializes the writer.</summary>
        public PostFilterRejectionEvidenceService(
            NntpDbService nntpDb,
            PostFilterRejectionEvidenceQueue queue,
            ILogger<PostFilterRejectionEvidenceService> logger)
        {
            ArgumentNullException.ThrowIfNull(nntpDb);
            ArgumentNullException.ThrowIfNull(queue);
            ArgumentNullException.ThrowIfNull(logger);
            _nntpDb = nntpDb;
            _queue = queue;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "PostFilterRejectionEvidence";

        /// <inheritdoc />
        public Task? Execution => _execution;

        /// <summary>Gets the bounded queue consumed by this service.</summary>
        public PostFilterRejectionEvidenceQueue Queue => _queue;

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
            _queue.Complete();
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
                PostFilterLogMessages.EvidenceWriterStoppedWithQueued(_logger, _queue.Count);
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            PostFilterLogMessages.EvidenceWriterStarted(_logger, _queue.Capacity);
            while (true)
            {
                PostFilterRejectionEvidence? evidence;
                try
                {
                    evidence = await _queue.DequeueAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (evidence is null)
                {
                    break;
                }

                try
                {
                    await using var connection = await NntpDbConnections.OpenAsync(_nntpDb, CancellationToken.None)
                        .ConfigureAwait(false);
                    await connection.InsertPostFilterRejectionAsync(evidence, CancellationToken.None)
                        .ConfigureAwait(false);
                    _queue.RecordWritten();
                }
                // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _queue.RecordWriteFailure();
                    PostFilterLogMessages.EvidenceWriteFailed(_logger, ex, evidence.Stage, evidence.Reason);
                }

                if (cancellationToken.IsCancellationRequested && _queue.Count == 0)
                {
                    break;
                }
            }

            PostFilterLogMessages.EvidenceWriterStopped(_logger, _queue.Written, _queue.Dropped, _queue.WriteFailures);
        }
    }
}

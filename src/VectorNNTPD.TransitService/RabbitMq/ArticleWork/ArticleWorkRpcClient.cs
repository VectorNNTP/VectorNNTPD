using System.Text;
using Microsoft.Extensions.Logging;
using VectorNNTPD.Common.Articles.Validation;

namespace VectorNNTPD.TransitService.RabbitMq.ArticleWork
{
    /// <summary>
    /// Sequential Backfill Scheduler: one ArticleWork publish per attempt, weighted by
    /// active consumer count, excluding already-attempted backbones.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never fans out concurrently. Definitive <c>ArticleNotFound</c> (provider <c>430</c>)
    /// from BackFiller is ACKed there and ends the logical lookup — NNTPD does not publish
    /// to another backbone. <c>InvalidArticle</c> / <c>InvalidRequest</c> remain
    /// attempt-terminal for the selected backbone and may advance to another eligible
    /// backbone. In-flight attempts cannot be cancelled on the broker — an attempt wait
    /// that expires without a definitive wire outcome ends the logical lookup without
    /// starting another backbone.
    /// </para>
    /// </remarks>
    internal sealed class ArticleWorkRpcClient : IArticleWorkRpcClient
    {
        private readonly IArticleWorkRpcPublisher _publisher;
        private readonly ArticleWorkRpcResponseRouter _router;
        private readonly IBackfillConsumerAvailability _availability;
        private readonly IBackboneSelector _selector;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger _logger;
        private readonly Func<long> _currentGeneration;

        /// <summary>Initializes a new orchestrator.</summary>
        internal ArticleWorkRpcClient(
            IArticleWorkRpcPublisher publisher,
            ArticleWorkRpcResponseRouter router,
            IBackfillConsumerAvailability availability,
            IBackboneSelector selector,
            TimeProvider timeProvider,
            ILogger logger,
            Func<long>? currentGeneration = null)
        {
            ArgumentNullException.ThrowIfNull(publisher);
            ArgumentNullException.ThrowIfNull(router);
            ArgumentNullException.ThrowIfNull(availability);
            ArgumentNullException.ThrowIfNull(selector);
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentNullException.ThrowIfNull(logger);
            _publisher = publisher;
            _router = router;
            _availability = availability;
            _selector = selector;
            _timeProvider = timeProvider;
            _logger = logger;
            _currentGeneration = currentGeneration ?? (() => 0);
        }

        /// <inheritdoc />
        public async Task<ArticleWorkRpcResult> LookupByMessageIdAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken)
        {
            var started = _timeProvider.GetUtcNow();
            var decoded = DecodeMessageId(messageId);
            var requestId = Guid.NewGuid();
            var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deadline = started + ArticleWorkRpcTiming.LookupDeadline;

            using var deadlineCts = new CancellationTokenSource(ArticleWorkRpcTiming.LookupDeadline, _timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineCts.Token);
            var lookupToken = linked.Token;

            try
            {
                var attempt = 0;
                while (true)
                {
                    lookupToken.ThrowIfCancellationRequested();
                    var now = _timeProvider.GetUtcNow();
                    if (now >= deadline)
                    {
                        var timedOut = ArticleWorkRpcResult.NotFound(
                            requestId,
                            decoded,
                            "Article lookup deadline elapsed");
                        LogCompleted(timedOut, started);
                        return timedOut;
                    }

                    var selected = SelectNext(attempted);
                    if (selected is null)
                    {
                        var exhausted = ArticleWorkRpcResult.NotFound(
                            requestId,
                            decoded,
                            attempted.Count == 0
                                ? "No BackFiller ArticleWork consumers are active"
                                : "All eligible BackFiller backbones returned not-found");
                        LogCompleted(exhausted, started);
                        return exhausted;
                    }

                    attempted.Add(selected.Value.Backbone);
                    attempt++;
                    ArticleWorkRpcLogMessages.SchedulerAttempt(
                        _logger,
                        requestId,
                        decoded,
                        selected.Value.Backbone,
                        selected.Value.ConsumerCount,
                        attempt);

                    var operation = new ArticleWorkLookupOperation(requestId, decoded);
                    try
                    {
                        await PublishAsync(operation, selected.Value, lookupToken).ConfigureAwait(false);
                        lookupToken.ThrowIfCancellationRequested();

                        await WaitUntilAsync(operation, deadline, lookupToken).ConfigureAwait(false);

                        if (!operation.TryGetResult(out var attemptResult))
                        {
                            // No definitive wire outcome before the remaining lookup deadline.
                            // Do not start another backbone: in-flight work cannot be cancelled.
                            var uncertain = ArticleWorkRpcResult.NotFound(
                                requestId,
                                decoded,
                                "Article-work attempt timed out without a definitive response");
                            LogCompleted(uncertain, started);
                            return uncertain;
                        }

                        if (ArticleWorkAggregatePolicy.IsLookupSuccess(attemptResult.Outcome))
                        {
                            LogCompleted(attemptResult, started);
                            return attemptResult;
                        }

                        if (!ArticleWorkAggregatePolicy.ShouldTryNextBackbone(attemptResult.Outcome))
                        {
                            // Includes definitive ArticleNotFound (430): lookup ends here.
                            LogCompleted(attemptResult, started);
                            return attemptResult;
                        }

                        // InvalidArticle / InvalidRequest for this backbone — try another eligible.
                    }
                    finally
                    {
                        _router.UnregisterAll(operation);
                    }
                }
            }
            catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested
                                                     && !cancellationToken.IsCancellationRequested)
            {
                var expired = ArticleWorkRpcResult.NotFound(
                    requestId,
                    decoded,
                    "Article lookup deadline elapsed");
                LogCompleted(expired, started);
                return expired;
            }
        }

        private BackfillEligibleBackbone? SelectNext(HashSet<string> attempted)
        {
            var eligible = _availability.GetEligibleBackbones();
            if (eligible.Count == 0)
            {
                return null;
            }

            List<BackfillEligibleBackbone>? filtered = null;
            for (var i = 0; i < eligible.Count; i++)
            {
                var candidate = eligible[i];
                if (candidate.ConsumerCount < 1 || attempted.Contains(candidate.Backbone))
                {
                    continue;
                }

                filtered ??= new List<BackfillEligibleBackbone>(eligible.Count);
                filtered.Add(candidate);
            }

            if (filtered is null || filtered.Count == 0)
            {
                return null;
            }

            return _selector.Select(filtered);
        }

        private async Task PublishAsync(
            ArticleWorkLookupOperation operation,
            BackfillEligibleBackbone destination,
            CancellationToken cancellationToken)
        {
            var correlationId = Guid.NewGuid().ToString("D");
            var request = new ArticleWorkRequest(
                ArticleWorkWireProtocol.CurrentVersion,
                operation.RequestId,
                operation.MessageId,
                destination.Backbone);
            var body = ArticleWorkWireProtocol.SerializeRequestV1(request);
            var generation = _currentGeneration();
            if (!_router.TryRegister(correlationId, operation, destination.Exchange, generation))
            {
                return;
            }

            try
            {
                await _publisher
                    .PublishAsync(
                        destination.Exchange,
                        destination.RoutingKey,
                        operation.RequestId,
                        correlationId,
                        body,
                        cancellationToken)
                    .ConfigureAwait(false);

                ArticleWorkRpcLogMessages.Published(
                    _logger,
                    operation.RequestId,
                    correlationId,
                    operation.MessageId,
                    destination.Exchange,
                    generation);
            }
            catch (OperationCanceledException)
            {
                _router.Unregister(correlationId);
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                _router.Unregister(correlationId);
                ArticleWorkRpcLogMessages.PublishFailed(
                    _logger,
                    ex,
                    operation.RequestId,
                    correlationId,
                    operation.MessageId,
                    destination.Exchange,
                    generation);
                throw;
            }
        }

        private async Task WaitUntilAsync(
            ArticleWorkLookupOperation operation,
            DateTimeOffset deadline,
            CancellationToken cancellationToken)
        {
            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero || operation.IsCompleted)
            {
                return;
            }

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(remaining, _timeProvider, delayCts.Token);
            var completed = await Task.WhenAny(operation.Completion, delay).ConfigureAwait(false);
            if (completed == operation.Completion)
            {
                await delayCts.CancelAsync().ConfigureAwait(false);
                _ = await operation.Completion.ConfigureAwait(false);
            }
            else
            {
                await delay.ConfigureAwait(false);
            }
        }

        private void LogCompleted(ArticleWorkRpcResult result, DateTimeOffset started)
        {
            var elapsedMs = (long)Math.Max(0, (_timeProvider.GetUtcNow() - started).TotalMilliseconds);
            ArticleWorkRpcLogMessages.Completed(
                _logger,
                result.RequestId,
                result.MessageId,
                result.Outcome.ToString(),
                result.SourceExchange,
                elapsedMs);
        }

        /// <summary>
        /// Converts command-line Message-ID bytes to the JSON string at the RPC application boundary.
        /// NNTP Message-IDs are ASCII; this is not a protocol-data-plane default representation.
        /// </summary>
        internal static string DecodeMessageId(ReadOnlyMemory<byte> messageId)
        {
            if (messageId.IsEmpty || !NntpMessageIdValidation.IsValidMessageId(messageId.Span))
            {
                throw new ArgumentException("Article-work RPC requires a well-formed Message-ID.", nameof(messageId));
            }

            return Encoding.ASCII.GetString(messageId.Span);
        }
    }
}

using System.Diagnostics;
using VectorNNTPD.BackFillerService.Diagnostics;
using VectorNNTPD.BackFillerService.Nntp;
using VectorNNTPD.Common.Messaging.RabbitMq;
using VectorNNTPD.Common.Core;

namespace VectorNNTPD.BackFillerService.ArticleWork
{
    /// <summary>
    /// Parses, classifies, publishes a confirmed terminal response when required, and settles one delivery.
    /// </summary>
    internal sealed class ArticleWorkDeliveryPipeline
    {
        /// <summary>Handler for validated work. This pipeline does not serialize calls to it.</summary>
        private readonly IArticleWorkHandler _handler;

        /// <summary>Response-publish seam. Confirm serialization belongs to the publisher.</summary>
        private readonly IArticleWorkResponsePublisher _publisher;

        /// <summary>Maximum JSON body size passed to <see cref="ArticleWorkRequestParser.Parse"/>.</summary>
        private readonly int _maxPayloadBytes;

        /// <summary>Configured queue <c>x-delivery-limit</c>; application requeue cap uses the same value.</summary>
        private readonly int _deliveryLimit;

        /// <summary>
        /// When <see langword="true"/>, a redelivery without <c>x-delivery-count</c> is treated as exhausted (quorum).
        /// Classic queues pass <see langword="false"/>.
        /// </summary>
        private readonly bool _treatMissingRedeliveryCountAsExhausted;

        /// <summary>Maximum JSON body size passed to <see cref="ArticleWorkRequestParser.Parse"/>.</summary>
        internal int MaxPayloadBytes => _maxPayloadBytes;

        /// <summary>Finite delivery limit applied to retryable dispositions.</summary>
        internal int DeliveryLimit => _deliveryLimit;

        /// <summary>
        /// Whether missing broker delivery-count on redelivery is treated as exhausted.
        /// </summary>
        internal bool TreatMissingRedeliveryCountAsExhausted => _treatMissingRedeliveryCountAsExhausted;

        /// <summary>
        /// Error text prepared for the most recent UnexpectedFailure caused by an escaped handler exception.
        /// </summary>
        /// <remarks>
        /// Cleared at the start of each <see cref="ProcessAsync"/> call. When disposition publishes a terminal
        /// <see cref="ArticleWorkOutcome.UnexpectedFailure"/> reply, this is the <c>error</c> value passed to
        /// publication. Formatted by <see cref="NntpExceptionDiagnostics.FormatReason"/>.
        /// </remarks>
        internal string? LastEscapedHandlerFailureError { get; private set; }

        /// <summary>
        /// Initializes a new pipeline.
        /// </summary>
        /// <param name="handler">Admitted-work handler.</param>
        /// <param name="publisher">Response-publish seam.</param>
        /// <param name="maxPayloadBytes">Maximum accepted JSON body size. Must be at least 1.</param>
        /// <param name="deliveryLimit">
        /// Finite delivery limit mirrored from queue declaration. Must be at least 1.
        /// Defaults to <c>10</c> so existing tests retain the production default.
        /// </param>
        /// <param name="treatMissingRedeliveryCountAsExhausted">
        /// Quorum topology passes <see langword="true"/> (default). Classic topology passes <see langword="false"/>.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="handler"/> or <paramref name="publisher"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="maxPayloadBytes"/> or <paramref name="deliveryLimit"/> is less than 1.
        /// </exception>
        internal ArticleWorkDeliveryPipeline(
            IArticleWorkHandler handler,
            IArticleWorkResponsePublisher publisher,
            int maxPayloadBytes,
            int deliveryLimit = 10,
            bool treatMissingRedeliveryCountAsExhausted = true)
        {
            ArgumentNullException.ThrowIfNull(handler);
            ArgumentNullException.ThrowIfNull(publisher);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadBytes, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(deliveryLimit, 1);
            _handler = handler;
            _publisher = publisher;
            _maxPayloadBytes = maxPayloadBytes;
            _deliveryLimit = deliveryLimit;
            _treatMissingRedeliveryCountAsExhausted = treatMissingRedeliveryCountAsExhausted;
        }

        /// <summary>
        /// Parses one delivery, handles a valid request, publishes a confirmed terminal response when required, and attempts settlement on <paramref name="channel"/>.
        /// </summary>
        /// <param name="delivery">Consumed manual-ack delivery.</param>
        /// <param name="consumingBackbone">Queue backbone context passed to the parser.</param>
        /// <param name="channel">Original consumer channel bound into the settlement lease.</param>
        /// <param name="channelStillCurrent">
        /// Evaluated again before publication and settlement. Must not be captured only at admission: a generation
        /// can disappear while work is in flight.
        /// </param>
        /// <param name="cancellationToken">
        /// Processing cancellation. When it is already cancelled, a valid request becomes
        /// <see cref="ArticleWorkOutcome.Cancelled"/> and the handler is not called.
        /// <see cref="OperationCanceledException"/> from the handler is classified the same way when this token is cancelled.
        /// The invalid-request path still publishes when replyable; this token only cancels that publication.
        /// </param>
        /// <returns>
        /// The <see cref="ArticleWorkOutcome"/> classification for the delivery. Diagnostic parse failures are returned
        /// through the optional <c>preParsed</c> value and are not reparsed by diagnostic code. Returns
        /// <see cref="ArticleWorkOutcome.UnexpectedFailure"/> when a required publication is cancelled or fails with a
        /// transient or ambiguous error; those paths NACK-requeue (while the delivery limit allows) and do not ACK.
        /// A confirmed mandatory unroutable reply (<see cref="RabbitMqPublicationUnroutableException"/>) settles
        /// NACK without requeue so a permanently missing ReplyTo cannot re-fetch indefinitely. Successful article
        /// retrieval is not the same as successful RPC delivery in that case.
        /// A handler result of <see cref="ArticleWorkOutcome.InvalidRequest"/> is treated as
        /// <see cref="ArticleWorkOutcome.UnexpectedFailure"/>. When <see cref="IArticleWorkResponsePublisher.CompletesSuccessPublication"/>
        /// is <see langword="false"/>, Success is NACK-requeued without publication, and this method still returns
        /// <see cref="ArticleWorkOutcome.Success"/>.
        /// </returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="consumingBackbone"/> is null or whitespace.</exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="channel"/> or <paramref name="channelStillCurrent"/> is null.</exception>
        /// <param name="preParsed">Optional pre-parsed result. When supplied, the pipeline will not reparse the body and will use
        /// the supplied failure reason for diagnostics when the delivery is invalid.</param>
        /// <remarks>
        /// Settlement calls use <see cref="CancellationToken.None"/>. A stale <paramref name="channelStillCurrent"/> result, or a lease
        /// that is not the original channel, returns without ACK or NACK. After the handler returns, a cancelled token forces
        /// <see cref="ArticleWorkOutcome.Cancelled"/> settlement even when the handler outcome was terminal. A required publication that
        /// is cancelled or fails NACK-requeues and returns <see cref="ArticleWorkOutcome.UnexpectedFailure"/>, including when the planned
        /// disposition was ACK. The lease still attempts settlement once; a failed NACK is not retried on that lease. Any
        /// <see cref="ArticleWorkHandlerResult.Article"/> is disposed before settlement. Other handler exceptions become
        /// <see cref="ArticleWorkOutcome.UnexpectedFailure"/>; the prepared error text is
        /// <see cref="NntpExceptionDiagnostics.FormatReason"/> (type and message when present). Handler
        /// <see cref="ArticleWorkOutcome.UnexpectedFailure"/> (and ProviderFailure / RetentionRejected) settle ACK with
        /// a correlated terminal RPC reply under the current disposition map; that is distinct from a failed reply
        /// publish, which still NACK-requeues via <see cref="CreateRetryDisposition"/>.
        /// SECURITY: The diagnostic reason is captured from the parse failure and returned directly. This ensures the configured payload
        /// limit is never bypassed by diagnostic code that might reparse the body with an unbounded limit.
        /// </remarks>
        internal async Task<ArticleWorkOutcome> ProcessAsync(
            RabbitMqManualAckDelivery delivery,
            string consumingBackbone,
            IRabbitMqManualAckChannel channel,
            Func<bool> channelStillCurrent,
            CancellationToken cancellationToken,
            ArticleWorkParseResult? preParsed = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(consumingBackbone);
            ArgumentNullException.ThrowIfNull(channel);
            LastEscapedHandlerFailureError = null;
            ArgumentNullException.ThrowIfNull(channelStillCurrent);

            var pipelineStarted = ArticleWorkStageProfiler.IsEnabled ? Stopwatch.GetTimestamp() : 0;
            var profiler = ArticleWorkStageProfiler.Installed;
            profiler?.BeginActive();
            var pipelineFailed = false;
            try
            {
                var parsed = preParsed ?? ArticleWorkRequestParser.Parse(delivery, consumingBackbone, _maxPayloadBytes);
                var lease = new ArticleWorkSettlementLease(channel, delivery.DeliveryTag, delivery.Generation);
                var replyable = !string.IsNullOrWhiteSpace(delivery.CorrelationId)
                                && !string.IsNullOrWhiteSpace(delivery.ReplyTo);

                if (!parsed.IsValid)
                {
                    pipelineFailed = true;
                    var failure = parsed.Failure!;
                    var invalid = ArticleWorkDispositionPlanner.Create(
                        ArticleWorkOutcome.InvalidRequest,
                        replyable,
                        cancellationRequested: false,
                        delivery.DeliveryCount,
                        delivery.Redelivered,
                        _deliveryLimit,
                        _treatMissingRedeliveryCountAsExhausted);
                    if (!channelStillCurrent() || !lease.IsOriginalChannel(channel))
                    {
                        return ArticleWorkOutcome.InvalidRequest;
                    }

                    var replyStartedInvalid = ArticleWorkStageProfiler.IsEnabled ? Stopwatch.GetTimestamp() : 0;
                    var invalidPublished = await TryPublishIfRequiredAsync(
                            invalid,
                            ArticleWorkOutcome.InvalidRequest,
                            failure.Identities.RequestId,
                            failure.Identities.MessageId,
                            failure.Identities.Backbone,
                            delivery.CorrelationId,
                            delivery.ReplyTo,
                            failure.Reason,
                            fqdn: null,
                            listenerPort: null,
                            articleIdHex: null,
                            cancellationToken)
                        .ConfigureAwait(false);
                    ArticleWorkStageProfiler.Installed?.Record(ArticleWorkStageId.ReplyPublish, replyStartedInvalid);
                    var publishOutcome = await CompleteAfterPublishAttemptAsync(
                            invalidPublished,
                            invalid,
                            ArticleWorkOutcome.InvalidRequest,
                            lease,
                            channel,
                            channelStillCurrent,
                            delivery.DeliveryCount,
                            delivery.Redelivered)
                        .ConfigureAwait(false);
                    return publishOutcome;
                }

                var item = new ArticleWorkItem(
                    parsed.Request!,
                    delivery.CorrelationId!,
                    delivery.ReplyTo!,
                    lease);

                ArticleWorkOutcome outcome;
                string? error;
                string? fqdn = null;
                int? listenerPort = null;
                string? articleIdHex = null;
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        outcome = ArticleWorkOutcome.Cancelled;
                        error = null;
                    }
                    else
                    {
                        var result = await _handler.HandleAsync(item, cancellationToken).ConfigureAwait(false);
                        outcome = result.Outcome == ArticleWorkOutcome.InvalidRequest
                            ? ArticleWorkOutcome.UnexpectedFailure
                            : result.Outcome;
                        error = result.Error;
                        fqdn = result.Fqdn;
                        listenerPort = result.ListenerPort;
                        articleIdHex = result.ArticleId?.ToLowerHexString();
                        result.Article?.Dispose();
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    outcome = ArticleWorkOutcome.Cancelled;
                    error = null;
                }
                // codeql[cs/catch-of-all-exceptions] A delivery failure must be settled or logged by this pipeline boundary.
                catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
                {
                    outcome = ArticleWorkOutcome.UnexpectedFailure;
                    error = NntpExceptionDiagnostics.FormatReason(ex);
                    LastEscapedHandlerFailureError = error;
                }

                pipelineFailed = outcome is not ArticleWorkOutcome.Success and not ArticleWorkOutcome.Cancelled;

                var disposition = ArticleWorkDispositionPlanner.Create(
                    outcome,
                    replyable: true,
                    cancellationToken.IsCancellationRequested || outcome == ArticleWorkOutcome.Cancelled,
                    delivery.DeliveryCount,
                    delivery.Redelivered,
                    _deliveryLimit,
                    _treatMissingRedeliveryCountAsExhausted);

                if (!channelStillCurrent() || !lease.IsOriginalChannel(channel))
                {
                    return outcome;
                }

                if (outcome == ArticleWorkOutcome.Success && !_publisher.CompletesSuccessPublication)
                {
                    var pending = CreateRetryDisposition(delivery.DeliveryCount, delivery.Redelivered);
                    await lease.TrySettleAsync(
                            pending,
                            channelStillCurrent() && lease.IsOriginalChannel(channel),
                            CancellationToken.None)
                    .ConfigureAwait(false);
                    return outcome;
                }

                var published = ArticleWorkPublishAttempt.Confirmed;
                if (disposition.PublishResponse)
                {
                    var replyStarted = ArticleWorkStageProfiler.IsEnabled ? Stopwatch.GetTimestamp() : 0;
                    published = await TryPublishIfRequiredAsync(
                            disposition,
                            outcome,
                            item.Request.RequestId,
                            item.Request.MessageId,
                            item.Request.Backbone,
                            item.CorrelationId,
                            item.ReplyTo,
                            error,
                            fqdn,
                            listenerPort,
                            articleIdHex,
                            cancellationToken)
                        .ConfigureAwait(false);
                    ArticleWorkStageProfiler.Installed?.Record(ArticleWorkStageId.ReplyPublish, replyStarted);
                }

                ArticleWorkOutcome finalOutcome = await CompleteAfterPublishAttemptAsync(
                        published,
                        disposition,
                        outcome,
                        lease,
                        channel,
                        channelStillCurrent,
                        delivery.DeliveryCount,
                        delivery.Redelivered)
                    .ConfigureAwait(false);
                return finalOutcome;
            }
            finally
            {
                profiler?.Record(ArticleWorkStageId.PipelineTotal, pipelineStarted);
                profiler?.EndActive(pipelineFailed);
            }
        }

        /// <summary>NACK with requeue while retries remain; NACK without requeue when the delivery limit is exhausted.</summary>
        /// <param name="lease">Settlement lease for this delivery.</param>
        /// <param name="channel">Channel originally bound to <paramref name="lease"/>.</param>
        /// <param name="channelStillCurrent">Rechecked immediately before the NACK.</param>
        /// <param name="deliveryCount">Broker <c>x-delivery-count</c> for this delivery.</param>
        /// <param name="redelivered">AMQP redelivered flag for missing-count handling.</param>
        /// <returns>
        /// A task that completes when the NACK attempt finishes. The lease swallows broker failures and does not retry them.
        /// </returns>
        private async Task SettleRetryableAsync(
            ArticleWorkSettlementLease lease,
            IRabbitMqManualAckChannel channel,
            Func<bool> channelStillCurrent,
            int? deliveryCount,
            bool redelivered)
        {
            var retry = CreateRetryDisposition(deliveryCount, redelivered);
            await lease.TrySettleAsync(
                    retry,
                    channelStillCurrent() && lease.IsOriginalChannel(channel),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Builds NACK requeue/discard for publish-path retries (not handler <see cref="ArticleWorkOutcome.UnexpectedFailure"/>).
        /// </summary>
        /// <param name="deliveryCount">Broker <c>x-delivery-count</c> when present; otherwise null.</param>
        /// <param name="redelivered">AMQP redelivered flag for missing-count handling.</param>
        /// <returns>
        /// ACK false, publish false, and requeue while the effective count is below <see cref="DeliveryLimit"/>.
        /// </returns>
        /// <remarks>
        /// Must not use the planner's terminal UnexpectedFailure arm (ACK + reply). A failed or incomplete
        /// reply publish must leave the delivery eligible for requeue while retries remain.
        /// </remarks>
        private ArticleWorkDisposition CreateRetryDisposition(int? deliveryCount, bool redelivered)
        {
            var effectiveCount = ArticleWorkDispositionPlanner.ResolveEffectiveDeliveryCount(
                deliveryCount,
                redelivered,
                _deliveryLimit,
                _treatMissingRedeliveryCountAsExhausted);
            return new ArticleWorkDisposition(
                Acknowledge: false,
                Requeue: effectiveCount < _deliveryLimit,
                PublishResponse: false);
        }

        /// <summary>
        /// Settles after a publication attempt.
        /// </summary>
        /// <param name="published">Result of the publication attempt. <see cref="ArticleWorkPublishAttempt.Confirmed"/> includes attempts that were not required.</param>
        /// <param name="disposition">Plan produced before publication. Submitted only when <paramref name="published"/> is confirmed.</param>
        /// <param name="outcome">Outcome that <paramref name="disposition"/> was planned from.</param>
        /// <param name="lease">Settlement lease for this delivery. One attempt; a failed NACK does not settle again.</param>
        /// <param name="channel">Channel originally bound to <paramref name="lease"/>.</param>
        /// <param name="channelStillCurrent">Rechecked immediately before settlement. A stale or replacement channel is not settled.</param>
        /// <param name="deliveryCount">Broker <c>x-delivery-count</c> for this delivery.</param>
        /// <param name="redelivered">AMQP redelivered flag for missing-count handling.</param>
        /// <returns>
        /// <paramref name="outcome"/> when the original disposition is submitted.
        /// <see cref="ArticleWorkOutcome.UnexpectedFailure"/> when publication was cancelled, failed transiently, or was permanently unroutable.
        /// </returns>
        /// <remarks>
        /// Broker RPCs use <see cref="CancellationToken.None"/>. A lost publisher confirm is reported as failure, so the
        /// requeue can publish another reply with the same correlation id. That is not exactly-once delivery.
        /// A confirmed mandatory unroutable return is terminal (NACK without requeue): the ReplyTo queue is gone and
        /// refetching cannot repair RPC delivery. Article retrieval may already have succeeded; that is not RPC delivery.
        /// </remarks>
        private async Task<ArticleWorkOutcome> CompleteAfterPublishAttemptAsync(
            ArticleWorkPublishAttempt published,
            ArticleWorkDisposition disposition,
            ArticleWorkOutcome outcome,
            ArticleWorkSettlementLease lease,
            IRabbitMqManualAckChannel channel,
            Func<bool> channelStillCurrent,
            int? deliveryCount,
            bool redelivered)
        {
            if (published == ArticleWorkPublishAttempt.Unroutable)
            {
                var terminal = new ArticleWorkDisposition(Acknowledge: false, Requeue: false, PublishResponse: false);
                await lease.TrySettleAsync(
                        terminal,
                        channelStillCurrent() && lease.IsOriginalChannel(channel),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return ArticleWorkOutcome.UnexpectedFailure;
            }

            if (published != ArticleWorkPublishAttempt.Confirmed)
            {
                await SettleRetryableAsync(lease, channel, channelStillCurrent, deliveryCount, redelivered)
                    .ConfigureAwait(false);
                return ArticleWorkOutcome.UnexpectedFailure;
            }

            await lease.TrySettleAsync(
                    disposition,
                    channelStillCurrent() && lease.IsOriginalChannel(channel),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return outcome;
        }

        /// <summary>
        /// Publishes when <paramref name="disposition"/> requires a terminal response.
        /// </summary>
        /// <param name="disposition">Settlement plan. When it does not publish, the publisher is not called.</param>
        /// <param name="outcome">Outcome written into the response intent.</param>
        /// <param name="requestId">Recovered request id. May be null for <see cref="ArticleWorkOutcome.InvalidRequest"/>.</param>
        /// <param name="messageId">Recovered Message-ID. May be null for <see cref="ArticleWorkOutcome.InvalidRequest"/>.</param>
        /// <param name="backbone">Recovered JSON backbone. May be null for <see cref="ArticleWorkOutcome.InvalidRequest"/>.</param>
        /// <param name="correlationId">AMQP correlation id to echo. May be null when the delivery is not replyable.</param>
        /// <param name="replyTo">AMQP reply destination. May be null when the delivery is not replyable.</param>
        /// <param name="error">Failure text for a non-success outcome. Null for success.</param>
        /// <param name="fqdn">Success FQDN. Ignored unless <paramref name="outcome"/> is <see cref="ArticleWorkOutcome.Success"/>.</param>
        /// <param name="listenerPort">Success listener port. Ignored unless <paramref name="outcome"/> is <see cref="ArticleWorkOutcome.Success"/>.</param>
        /// <param name="articleIdHex">Success article id hex. Ignored unless <paramref name="outcome"/> is <see cref="ArticleWorkOutcome.Success"/>.</param>
        /// <param name="cancellationToken">Cancellation passed to <see cref="IArticleWorkResponsePublisher.PublishAsync"/>.</param>
        /// <returns>
        /// <see cref="ArticleWorkPublishAttempt.Confirmed"/> when publication is not required or <see cref="IArticleWorkResponsePublisher.PublishAsync"/> completes.
        /// <see cref="ArticleWorkPublishAttempt.Cancelled"/> when it throws <see cref="OperationCanceledException"/>.
        /// <see cref="ArticleWorkPublishAttempt.Unroutable"/> when it throws <see cref="RabbitMqPublicationUnroutableException"/>.
        /// <see cref="ArticleWorkPublishAttempt.Failed"/> when it throws any other exception. Exceptions are not propagated.
        /// </returns>
        private async Task<ArticleWorkPublishAttempt> TryPublishIfRequiredAsync(
            ArticleWorkDisposition disposition,
            ArticleWorkOutcome outcome,
            Guid? requestId,
            string? messageId,
            string? backbone,
            string? correlationId,
            string? replyTo,
            string? error,
            string? fqdn,
            int? listenerPort,
            string? articleIdHex,
            CancellationToken cancellationToken)
        {
            if (!disposition.PublishResponse)
            {
                return ArticleWorkPublishAttempt.Confirmed;
            }

            try
            {
                // Terminal failure replies require a non-empty error; Event 5321 may omit reason text.
                var wireError = outcome == ArticleWorkOutcome.Success
                    ? error
                    : (string.IsNullOrWhiteSpace(error) ? outcome.ToString() : error);
                await _publisher.PublishAsync(
                        new ArticleWorkResponseIntent(
                            outcome,
                            requestId,
                            messageId,
                            backbone,
                            correlationId,
                            replyTo,
                            wireError,
                            outcome == ArticleWorkOutcome.Success ? fqdn : null,
                            outcome == ArticleWorkOutcome.Success ? listenerPort : null,
                            outcome == ArticleWorkOutcome.Success ? articleIdHex : null),
                        cancellationToken)
                    .ConfigureAwait(false);
                return ArticleWorkPublishAttempt.Confirmed;
            }
            catch (OperationCanceledException)
            {
                return ArticleWorkPublishAttempt.Cancelled;
            }
            catch (RabbitMqPublicationUnroutableException)
            {
                return ArticleWorkPublishAttempt.Unroutable;
            }
            // codeql[cs/catch-of-all-exceptions] A delivery failure must be settled or logged by this pipeline boundary.
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                return ArticleWorkPublishAttempt.Failed;
            }
        }

        /// <summary>Result of one terminal-response publication attempt inside this pipeline.</summary>
        private enum ArticleWorkPublishAttempt
        {
            /// <summary>Publication was not required, or the publisher completed the attempt.</summary>
            Confirmed,

            /// <summary>The publisher threw <see cref="OperationCanceledException"/>.</summary>
            Cancelled,

            /// <summary>
            /// The publisher threw <see cref="RabbitMqPublicationUnroutableException"/> (mandatory basic.return).
            /// </summary>
            Unroutable,

            /// <summary>
            /// The publisher threw an exception other than cancellation or confirmed unroutable
            /// (transient or ambiguous failure).
            /// </summary>
            Failed,
        }
    }
}

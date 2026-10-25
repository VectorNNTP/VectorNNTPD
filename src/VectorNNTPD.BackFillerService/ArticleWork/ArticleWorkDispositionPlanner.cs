namespace VectorNNTPD.BackFillerService.ArticleWork
{
    /// <summary>
    /// Maps an Article Work outcome to ACK/NACK and response-publication policy.
    /// </summary>
    internal static class ArticleWorkDispositionPlanner
    {
        /// <summary>
        /// Resolves the delivery-count value used for the finite retry policy.
        /// </summary>
        /// <param name="deliveryCount">Broker <c>x-delivery-count</c> when present and non-negative.</param>
        /// <param name="redelivered">AMQP redelivered flag from the delivery.</param>
        /// <param name="deliveryLimit">Configured finite limit. Used when metadata is missing on a quorum redelivery.</param>
        /// <param name="treatMissingRedeliveryCountAsExhausted">
        /// When <see langword="true"/> (quorum), a redelivery without a usable count is treated as exhausted so
        /// missing metadata cannot bypass the finite retry policy. When <see langword="false"/> (classic),
        /// missing count remains <c>0</c> because classic queues do not provide <c>x-delivery-count</c>.
        /// </param>
        /// <returns>
        /// <paramref name="deliveryCount"/> when present; <c>0</c> on a first delivery without the header;
        /// <paramref name="deliveryLimit"/> when <paramref name="treatMissingRedeliveryCountAsExhausted"/> is
        /// <see langword="true"/> and a redelivery omits a usable count; otherwise <c>0</c>.
        /// </returns>
        /// <remarks>
        /// Quorum queues include <c>x-delivery-count</c> on redelivered messages. The broker discards when
        /// the post-increment count is greater than <c>x-delivery-limit</c>. Application exhaustion uses
        /// <c>count &gt;= limit</c> on the count observed at delivery time, which aligns with that boundary:
        /// counts <c>0..(limit-1)</c> may requeue; a delivery that already shows <c>limit</c> is not requeued.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="deliveryLimit"/> is less than 1.</exception>
        internal static int ResolveEffectiveDeliveryCount(
            int? deliveryCount,
            bool redelivered,
            int deliveryLimit,
            bool treatMissingRedeliveryCountAsExhausted = true)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(deliveryLimit, 1);
            if (deliveryCount is { } count && count >= 0)
            {
                return count;
            }

            if (treatMissingRedeliveryCountAsExhausted && redelivered)
            {
                return deliveryLimit;
            }

            return 0;
        }

        /// <summary>
        /// Creates the settlement plan for one classified result.
        /// </summary>
        /// <param name="outcome">Classified processing or parse outcome.</param>
        /// <param name="replyable">
        /// Whether AMQP <c>CorrelationId</c> and <c>ReplyTo</c> are both present.
        /// Only <see cref="ArticleWorkOutcome.InvalidRequest"/> uses this to decide publication.
        /// </param>
        /// <param name="cancellationRequested">
        /// When <see langword="true"/>, forces <see cref="ArticleWorkOutcome.Cancelled"/> settlement
        /// (NACK requeue when retries remain, no terminal response) and ignores <paramref name="outcome"/>.
        /// </param>
        /// <param name="deliveryCount">
        /// Broker <c>x-delivery-count</c> when present. Pass the raw header value; this method applies
        /// <see cref="ResolveEffectiveDeliveryCount"/> with <paramref name="redelivered"/>.
        /// </param>
        /// <param name="redelivered">AMQP redelivered flag for conservative missing-count handling.</param>
        /// <param name="deliveryLimit">
        /// Configured finite delivery limit. When the effective count is greater than or equal
        /// to this value, an otherwise-requeue disposition becomes NACK without requeue so poison work cannot
        /// retry indefinitely. Must be at least 1.
        /// </param>
        /// <param name="treatMissingRedeliveryCountAsExhausted">
        /// Quorum queues pass <see langword="true"/>; classic queues pass <see langword="false"/>.
        /// </param>
        /// <returns>
        /// The disposition to apply. An unrecognized <paramref name="outcome"/> is NACK requeue without publication
        /// unless the delivery limit is exhausted.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="deliveryLimit"/> is less than 1.</exception>
        internal static ArticleWorkDisposition Create(
            ArticleWorkOutcome outcome,
            bool replyable,
            bool cancellationRequested,
            int? deliveryCount,
            bool redelivered,
            int deliveryLimit,
            bool treatMissingRedeliveryCountAsExhausted = true)
        {
            var effectiveCount = ResolveEffectiveDeliveryCount(
                deliveryCount,
                redelivered,
                deliveryLimit,
                treatMissingRedeliveryCountAsExhausted);
            var retriesExhausted = effectiveCount >= deliveryLimit;

            if (cancellationRequested)
            {
                return CreateRetryable(retriesExhausted);
            }

            return outcome switch
            {
                ArticleWorkOutcome.Success => new ArticleWorkDisposition(Acknowledge: true, Requeue: false, PublishResponse: true),
                // Definitive provider miss (430 or ARTICLE 451): ACK; do not requeue.
                // Callers must treat NotFound as lookup-terminal (no cross-backbone republish).
                ArticleWorkOutcome.ArticleNotFound => new ArticleWorkDisposition(Acknowledge: true, Requeue: false, PublishResponse: true),
                // Unusable article on this backbone: ACK + publish. Callers may try another backbone.
                ArticleWorkOutcome.InvalidArticle => new ArticleWorkDisposition(Acknowledge: true, Requeue: false, PublishResponse: true),
                ArticleWorkOutcome.InvalidRequest => new ArticleWorkDisposition(Acknowledge: true, Requeue: false, PublishResponse: replyable),
                // Provider/operation failures are terminal for this delivery: ACK + correlated failure reply.
                // NACK-requeue without a reply caused unbounded classic-queue redelivery and client timeouts
                // that published replacement work for an already-finished attempt.
                ArticleWorkOutcome.ProviderFailure
                    or ArticleWorkOutcome.UnexpectedFailure
                    or ArticleWorkOutcome.RetentionRejected =>
                    new ArticleWorkDisposition(Acknowledge: true, Requeue: false, PublishResponse: true),
                // Cooperative cancel / shutdown: requeue while delivery-limit retries remain so work can resume.
                ArticleWorkOutcome.Cancelled => CreateRetryable(retriesExhausted),
                _ => CreateRetryable(retriesExhausted),
            };
        }

        /// <summary>
        /// Builds a non-publishing NACK disposition for cooperative cancel or unrecognized outcomes.
        /// </summary>
        /// <param name="retriesExhausted">
        /// When <see langword="true"/>, NACK without requeue; otherwise NACK with requeue.
        /// </param>
        /// <returns>ACK false, <see cref="ArticleWorkDisposition.PublishResponse"/> false, and requeue inverted from exhaustion.</returns>
        private static ArticleWorkDisposition CreateRetryable(bool retriesExhausted) =>
            new(Acknowledge: false, Requeue: !retriesExhausted, PublishResponse: false);
    }
}

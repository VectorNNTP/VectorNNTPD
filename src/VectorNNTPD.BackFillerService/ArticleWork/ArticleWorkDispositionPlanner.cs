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
        /// <param name="deliveryLimit">Configured finite limit. Used when metadata is missing on a redelivery.</param>
        /// <returns>
        /// <paramref name="deliveryCount"/> when present; <c>0</c> on a first delivery without the header;
        /// <paramref name="deliveryLimit"/> when a redelivery omits or cannot supply a usable count so the
        /// finite-retry guarantee cannot be bypassed by missing metadata.
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
            int deliveryLimit)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(deliveryLimit, 1);
            if (deliveryCount is { } count && count >= 0)
            {
                return count;
            }

            return redelivered ? deliveryLimit : 0;
        }

        /// <summary>
        /// Creates the settlement plan for one classified result.
        /// </summary>
        /// <param name="outcome">Processing or parse the outcome.</param>
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
            int deliveryLimit)
        {
            var effectiveCount = ResolveEffectiveDeliveryCount(deliveryCount, redelivered, deliveryLimit);
            var retriesExhausted = effectiveCount >= deliveryLimit;

            if (cancellationRequested)
            {
                return CreateRetryable(retriesExhausted);
            }

            return outcome switch
            {
                ArticleWorkOutcome.Success => new ArticleWorkDisposition(Acknowledge: true, Requeue: false, PublishResponse: true),
                // Definitive absence / unusable article: processing completed successfully.
                // ACK removes the message; NNTPD may publish a NEW request to another backbone.
                ArticleWorkOutcome.ArticleNotFound => new ArticleWorkDisposition(Acknowledge: true, Requeue: false, PublishResponse: true),
                ArticleWorkOutcome.InvalidArticle => new ArticleWorkDisposition(Acknowledge: true, Requeue: false, PublishResponse: true),
                ArticleWorkOutcome.InvalidRequest => new ArticleWorkDisposition(Acknowledge: true, Requeue: false, PublishResponse: replyable),
                // Known retryable outcomes. The same settlement as an unrecognized value, and listed as a
                // later change to the discard arm cannot retarget them.
                ArticleWorkOutcome.ProviderFailure
                    or ArticleWorkOutcome.Cancelled
                    or ArticleWorkOutcome.UnexpectedFailure
                    or ArticleWorkOutcome.RetentionRejected =>
                    CreateRetryable(retriesExhausted),
                _ => CreateRetryable(retriesExhausted),
            };
        }

        /// <summary>NACK requeue while retries remain; NACK without requeue when the delivery limit is exhausted.</summary>
        private static ArticleWorkDisposition CreateRetryable(bool retriesExhausted) =>
            new(Acknowledge: false, Requeue: !retriesExhausted, PublishResponse: false);
    }
}

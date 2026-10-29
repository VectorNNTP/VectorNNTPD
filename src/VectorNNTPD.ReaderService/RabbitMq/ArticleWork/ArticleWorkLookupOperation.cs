namespace VectorNNTPD.ReaderService.RabbitMq.ArticleWork
{
    /// <summary>In-flight article-work lookup shared by every publication of one requestId.</summary>
    internal sealed class ArticleWorkLookupOperation
    {
        private readonly TaskCompletionSource<ArticleWorkRpcResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Initializes a new lookup operation.</summary>
        internal ArticleWorkLookupOperation(Guid requestId, string messageId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
            RequestId = requestId;
            MessageId = messageId;
        }

        /// <summary>Gets the application request identity shared by every publication.</summary>
        internal Guid RequestId { get; }

        /// <summary>Gets the Message-ID being retrieved.</summary>
        internal string MessageId { get; }

        /// <summary>Gets the task that completes when the lookup reaches a terminal classification.</summary>
        internal Task<ArticleWorkRpcResult> Completion => _completion.Task;

        /// <summary>Returns whether the operation has already completed.</summary>
        internal bool IsCompleted => _completion.Task.IsCompleted;

        /// <summary>Returns whether a response belongs to this operation's application identity.</summary>
        internal bool Matches(ArticleWorkResponse response)
        {
            ArgumentNullException.ThrowIfNull(response);
            if (response.RequestId.HasValue && response.RequestId.Value != RequestId)
            {
                return false;
            }

            if (response.MessageId is not null
                && !string.Equals(response.MessageId, MessageId, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Applies a validated response immediately. Attempt-terminal outcomes
        /// (<see cref="ArticleWorkOutcome.Success"/>, NotFound, InvalidArticle, InvalidRequest)
        /// complete this attempt's wait so the scheduler can decide whether to continue.
        /// </summary>
        internal void OnResponse(string exchange, ArticleWorkResponse response)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
            ArgumentNullException.ThrowIfNull(response);

            if (!ArticleWorkAggregatePolicy.IsAttemptTerminal(response.Outcome))
            {
                return;
            }

            TryComplete(new ArticleWorkRpcResult(
                response.Outcome,
                RequestId,
                MessageId,
                response.Backbone,
                response.Fqdn,
                response.VatpPort,
                response.ArticleId,
                response.Error,
                exchange));
        }

        /// <summary>Completes the operation if it is still pending.</summary>
        internal bool TryComplete(ArticleWorkRpcResult result) => _completion.TrySetResult(result);

        /// <summary>Cancels the operation during shutdown.</summary>
        internal bool TryCancel() => _completion.TrySetCanceled();

        /// <summary>Returns the completed result when available.</summary>
        internal bool TryGetResult(out ArticleWorkRpcResult result)
        {
            if (_completion.Task is { IsCompletedSuccessfully: true } task)
            {
                result = task.Result;
                return true;
            }

            result = default!;
            return false;
        }
    }
}

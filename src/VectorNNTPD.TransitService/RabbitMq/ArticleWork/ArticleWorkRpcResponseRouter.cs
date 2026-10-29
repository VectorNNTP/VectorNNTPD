using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace VectorNNTPD.TransitService.RabbitMq.ArticleWork
{
    /// <summary>
    /// Routes consumed RPC responses to outstanding publications by AMQP
    /// <c>CorrelationId</c> and logical <c>RequestId</c>.
    /// </summary>
    /// <remarks>
    /// This type owns NNTPD in-process correlation state only. It does not delete,
    /// purge, or cancel RabbitMQ messages. Unknown and stale correlations are a
    /// normal cheap ignore path after the logical lookup has completed.
    /// </remarks>
    internal sealed class ArticleWorkRpcResponseRouter
    {
        private readonly ConcurrentDictionary<string, PendingPublication> _pending = new(StringComparer.Ordinal);
        private readonly ILogger _logger;

        /// <summary>Initializes a new router.</summary>
        internal ArticleWorkRpcResponseRouter(ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(logger);
            _logger = logger;
        }

        /// <summary>Gets the number of outstanding correlation registrations.</summary>
        internal int OutstandingCount => _pending.Count;

        /// <summary>
        /// Registers a publication so a later response can be correlated.
        /// Completed lookups are not registered.
        /// </summary>
        internal bool TryRegister(
            string correlationId,
            ArticleWorkLookupOperation operation,
            string exchange,
            long generation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentException.ThrowIfNullOrWhiteSpace(exchange);

            if (operation.IsCompleted)
            {
                return false;
            }

            if (!_pending.TryAdd(correlationId, new PendingPublication(operation, exchange, generation)))
            {
                throw new InvalidOperationException($"Duplicate article-work correlation '{correlationId}'.");
            }

            if (operation.IsCompleted)
            {
                _pending.TryRemove(correlationId, out _);
                return false;
            }

            return true;
        }

        /// <summary>Registers a publication so a later response can be correlated.</summary>
        internal void Register(
            string correlationId,
            ArticleWorkLookupOperation operation,
            string exchange,
            long generation)
        {
            _ = TryRegister(correlationId, operation, exchange, generation);
        }

        /// <summary>Removes one publication registration. Safe when already removed.</summary>
        internal void Unregister(string correlationId)
        {
            if (string.IsNullOrWhiteSpace(correlationId))
            {
                return;
            }

            _pending.TryRemove(correlationId, out _);
        }

        /// <summary>Removes every registration owned by <paramref name="operation"/>.</summary>
        internal void UnregisterAll(ArticleWorkLookupOperation operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            // codeql[cs/linq/missed-where] ConcurrentDictionary iteration with TryRemove side effects.
            foreach (var pair in _pending)
            {
                if (ReferenceEquals(pair.Value.Operation, operation))
                {
                    _pending.TryRemove(pair.Key, out _);
                }
            }
        }

        /// <summary>Cancels every outstanding operation during shutdown.</summary>
        internal void CancelAll()
        {
            foreach (var pair in _pending)
            {
                pair.Value.Operation.TryCancel();
                _pending.TryRemove(pair.Key, out _);
            }
        }

        /// <summary>
        /// Dispatches one consumed body. Unknown, stale, malformed, generation-mismatched,
        /// already-completed, or identity-mismatched responses are ignored.
        /// </summary>
        internal void Dispatch(
            string? correlationId,
            ReadOnlyMemory<byte> body,
            string? amqpRequestId,
            long deliveryGeneration)
        {
            if (string.IsNullOrWhiteSpace(correlationId))
            {
                ArticleWorkRpcLogMessages.ResponseIgnored(_logger, "(missing)", "missing-correlation");
                return;
            }

            if (!_pending.TryGetValue(correlationId, out var pending))
            {
                ArticleWorkRpcLogMessages.ResponseIgnored(_logger, correlationId, "unknown-correlation");
                return;
            }

            if (pending.Operation.IsCompleted)
            {
                UnregisterAll(pending.Operation);
                ArticleWorkRpcLogMessages.ResponseIgnored(_logger, correlationId, "lookup-already-complete");
                return;
            }

            if (pending.Generation != deliveryGeneration)
            {
                ArticleWorkRpcLogMessages.ResponseIgnored(_logger, correlationId, "generation-mismatch");
                return;
            }

            if (string.IsNullOrWhiteSpace(amqpRequestId)
                || !Guid.TryParse(amqpRequestId, out var headerRequestId)
                || headerRequestId != pending.Operation.RequestId)
            {
                ArticleWorkRpcLogMessages.ResponseIgnored(_logger, correlationId, "request-id-mismatch");
                return;
            }

            if (!ArticleWorkWireProtocol.TryParseResponseV1(body.Span, out var response, out var reason)
                || response is null)
            {
                ArticleWorkRpcLogMessages.ResponseIgnored(_logger, correlationId, reason);
                return;
            }

            if (!pending.Operation.Matches(response))
            {
                ArticleWorkRpcLogMessages.ResponseIgnored(_logger, correlationId, "identity-mismatch");
                return;
            }

            pending.Operation.OnResponse(pending.Exchange, response);
            if (pending.Operation.IsCompleted)
            {
                UnregisterAll(pending.Operation);
            }
        }

        private readonly record struct PendingPublication(
            ArticleWorkLookupOperation Operation,
            string Exchange,
            long Generation);
    }
}

using System.Collections.Concurrent;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Messaging.Cache;
using VectorNNTPD.ReaderService.Configuration;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.ReaderService.Storage
{
    /// <summary>One validated StorageServer presence response retained for a single lookup.</summary>
    /// <param name="ServerId">Responding StorageServer id.</param>
    /// <param name="Fqdn">Responding StorageServer FQDN.</param>
    /// <param name="VatpPort">TLS VATP listen port for retrieval.</param>
    internal readonly record struct StorageArticleCandidate(int ServerId, string Fqdn, int VatpPort);

    /// <summary>
    /// Later positives for one in-flight lookup. Completes when an alternate arrives or the
    /// original lookup window closes. Does not start another lookup.
    /// </summary>
    internal interface IStorageArticleAlternateSource
    {
        /// <summary>Completes when the lookup window closes.</summary>
        Task WhenClosed { get; }

        /// <summary>
        /// Returns the second retained candidate, or <see langword="null"/> when the original
        /// lookup window closes without one.
        /// </summary>
        ValueTask<StorageArticleCandidate?> WaitForAlternateAsync(CancellationToken cancellationToken);
    }

    /// <summary>Outcome of a StorageServer fleet article-presence lookup.</summary>
    public enum StorageArticleLookupOutcome
    {
        /// <summary>At least one StorageServer responded positively.</summary>
        Found = 0,

        /// <summary>No positive response arrived before the lookup timeout.</summary>
        NotFound = 1,
    }

    /// <summary>Result of a StorageServer fleet article-presence lookup.</summary>
    /// <param name="Outcome">Classified outcome.</param>
    /// <param name="RequestId">Logical lookup identity.</param>
    /// <param name="ArticleId">Queried article identity.</param>
    /// <param name="ServerId">Winning StorageServer id when found.</param>
    /// <param name="Fqdn">Winning StorageServer FQDN when found.</param>
    /// <param name="VatpPort">TLS VATP listen port when found.</param>
    /// <param name="Error">Detail when not found.</param>
    public sealed record StorageArticleLookupResult(
        StorageArticleLookupOutcome Outcome,
        Guid RequestId,
        ArticleId ArticleId,
        int? ServerId,
        string? Fqdn,
        int? VatpPort,
        string? Error)
    {
        /// <summary>
        /// Later candidates for this lookup, present only while the original lookup window can
        /// still accept them. Null when the lookup did not find a first candidate.
        /// </summary>
        internal IStorageArticleAlternateSource? Alternates { get; init; }

        /// <summary>Builds a not-found result.</summary>
        public static StorageArticleLookupResult NotFound(Guid requestId, ArticleId articleId, string error) =>
            new(StorageArticleLookupOutcome.NotFound, requestId, articleId, null, null, null, error);
    }

    /// <summary>NNTPD client for StorageServer fleet article-presence lookups.</summary>
    public interface IStorageArticleLookupClient
    {
        /// <summary>
        /// Asks all StorageServers "who has this article?" once. The task completes when the
        /// first valid positive arrives or the lookup timeout elapses. The registration stays
        /// until that same timeout so one later positive can still be retained.
        /// </summary>
        Task<StorageArticleLookupResult> LookupAsync(ArticleId articleId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Correlates StorageServer lookup responses by AMQP <c>CorrelationId</c>.
    /// Matching positives are retained until the lookup window closes. Duplicates and
    /// unknowns are ignored. The registration is not removed on the first positive.
    /// </summary>
    internal sealed class StorageArticleLookupResponseRouter
    {
        private readonly ConcurrentDictionary<string, PendingLookup> _pending = new(StringComparer.Ordinal);

        /// <summary>Gets outstanding correlation count (tests).</summary>
        internal int OutstandingCount => _pending.Count;

        /// <summary>Registers a lookup awaiting responses.</summary>
        internal bool TryRegister(string correlationId, StorageArticleLookupOperation operation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
            ArgumentNullException.ThrowIfNull(operation);
            if (operation.IsCompleted)
            {
                return false;
            }

            if (!_pending.TryAdd(correlationId, new PendingLookup(operation)))
            {
                throw new InvalidOperationException($"Duplicate storage lookup correlation '{correlationId}'.");
            }

            if (operation.IsCompleted)
            {
                _pending.TryRemove(correlationId, out _);
                return false;
            }

            return true;
        }

        /// <summary>Removes a registration.</summary>
        internal void Unregister(string correlationId)
        {
            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                _pending.TryRemove(correlationId, out _);
            }
        }

        /// <summary>Cancels all outstanding lookups.</summary>
        internal void CancelAll()
        {
            // codeql[cs/linq/missed-where] ConcurrentDictionary iteration with TryRemove side effects.
            foreach (var pair in _pending)
            {
                if (_pending.TryRemove(pair.Key, out var pending))
                {
                    pending.Operation.TrySetCanceled();
                }
            }
        }

        /// <summary>
        /// Dispatches a parsed positive response. Returns <see langword="true"/> when the
        /// response was retained as a candidate. Does not remove the registration.
        /// </summary>
        internal bool TryComplete(string? correlationId, StorageArticleLookupResponse response)
        {
            ArgumentNullException.ThrowIfNull(response);
            if (string.IsNullOrWhiteSpace(correlationId))
            {
                return false;
            }

            if (!_pending.TryGetValue(correlationId, out var pending))
            {
                return false;
            }

            if (pending.Operation.RequestId != response.RequestId)
            {
                return false;
            }

            if (pending.Operation.ArticleId != response.ArticleId)
            {
                return false;
            }

            return pending.Operation.TryAccept(response);
        }

        private sealed record PendingLookup(StorageArticleLookupOperation Operation);
    }

    /// <summary>
    /// One in-flight StorageServer fleet lookup. Retains at most two distinct candidates in
    /// arrival order and wakes waiters on the first without closing the lookup window.
    /// </summary>
    internal sealed class StorageArticleLookupOperation : IStorageArticleAlternateSource
    {
        private const int MaxRetainedCandidates = 2;
        private readonly object _gate = new();
        private readonly List<StorageArticleCandidate> _candidates = new(MaxRetainedCandidates);
        private readonly HashSet<int> _serverIds = [];
        private readonly HashSet<string> _endpoints = new(StringComparer.OrdinalIgnoreCase);
        private readonly TaskCompletionSource<bool> _firstReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _alternateOrClosed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _windowClosed;

        /// <summary>Initializes a lookup operation.</summary>
        internal StorageArticleLookupOperation(Guid requestId, ArticleId articleId)
        {
            RequestId = requestId;
            ArticleId = articleId;
        }

        /// <summary>Logical request identity.</summary>
        internal Guid RequestId { get; }

        /// <summary>Queried article identity.</summary>
        internal ArticleId ArticleId { get; }

        /// <summary>Completes when the first candidate is retained or the window closes without one.</summary>
        internal Task FirstReady => _firstReady.Task;

        /// <inheritdoc />
        public Task WhenClosed => _closed.Task;

        /// <summary>Whether the window has been closed.</summary>
        internal bool IsCompleted
        {
            get
            {
                lock (_gate)
                {
                    return _windowClosed;
                }
            }
        }

        /// <summary>Reads the first retained candidate.</summary>
        internal bool TryGetFirst(out StorageArticleCandidate candidate)
        {
            lock (_gate)
            {
                if (_candidates.Count > 0)
                {
                    candidate = _candidates[0];
                    return true;
                }
            }

            candidate = default;
            return false;
        }

        /// <summary>
        /// Retains <paramref name="response"/> when it is a new valid candidate.
        /// Invalid, duplicate, and over-cap responses are ignored.
        /// </summary>
        internal bool TryAccept(StorageArticleLookupResponse response)
        {
            if (!TryValidate(response, out var candidate, out var endpointKey))
            {
                return false;
            }

            lock (_gate)
            {
                if (_windowClosed || _candidates.Count >= MaxRetainedCandidates)
                {
                    return false;
                }

                if (_serverIds.Contains(candidate.ServerId) || _endpoints.Contains(endpointKey))
                {
                    return false;
                }

                _serverIds.Add(candidate.ServerId);
                _endpoints.Add(endpointKey);
                _candidates.Add(candidate);
                if (_candidates.Count == 1)
                {
                    _firstReady.TrySetResult(true);
                }
                else
                {
                    _alternateOrClosed.TrySetResult(true);
                }

                return true;
            }
        }

        /// <summary>Closes the window so no further candidate can be retained.</summary>
        internal void CloseWindow()
        {
            lock (_gate)
            {
                if (_windowClosed)
                {
                    return;
                }

                _windowClosed = true;
            }

            _firstReady.TrySetResult(false);
            _alternateOrClosed.TrySetResult(false);
            _closed.TrySetResult();
        }

        /// <summary>Cancels waiters. Used when the lookup service is stopping.</summary>
        internal bool TrySetCanceled()
        {
            CloseWindow();
            return true;
        }

        /// <inheritdoc />
        public async ValueTask<StorageArticleCandidate?> WaitForAlternateAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_candidates.Count >= 2)
                {
                    return _candidates[1];
                }

                if (_windowClosed)
                {
                    return null;
                }
            }

            await _alternateOrClosed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                return _candidates.Count >= 2 ? _candidates[1] : null;
            }
        }

        private bool TryValidate(
            StorageArticleLookupResponse response,
            out StorageArticleCandidate candidate,
            out string endpointKey)
        {
            candidate = default;
            endpointKey = string.Empty;
            if (!ServerIdRules.IsInRange(response.ServerId))
            {
                return false;
            }

            if (response.ArticleId != ArticleId
                || !VatpEndpointFields.IsCanonicalFqdn(response.Fqdn)
                || !VatpEndpointFields.IsCanonicalPort(response.VatpPort))
            {
                return false;
            }

            candidate = new StorageArticleCandidate(response.ServerId, response.Fqdn, response.VatpPort);
            endpointKey = response.Fqdn + ":" + response.VatpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
    }
}

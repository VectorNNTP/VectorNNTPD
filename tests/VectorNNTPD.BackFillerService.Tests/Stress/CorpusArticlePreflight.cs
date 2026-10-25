namespace VectorNNTPD.BackFillerService.Tests.Stress
{
    /// <summary>
    /// Bounded preflight checks over selected corpus Message-IDs before throughput measurement.
    /// </summary>
    /// <remarks>
    /// Does not invent replacement IDs when retrieval fails. Live RPC preflight is opt-in via
    /// <see cref="RunLiveRetrievalPreflightAsync"/> and records explicit per-ID outcomes.
    /// </remarks>
    public static class CorpusArticlePreflight
    {
        /// <summary>
        /// Confirms every selected ID is present in the discovered corpus set and is canonically valid.
        /// </summary>
        public static IReadOnlyList<CorpusIdPreflightResult> ValidateSelectedAgainstCorpus(
            IReadOnlyList<string> selectedIds,
            IReadOnlySet<string> corpusUniqueIds)
        {
            ArgumentNullException.ThrowIfNull(selectedIds);
            ArgumentNullException.ThrowIfNull(corpusUniqueIds);
            var results = new List<CorpusIdPreflightResult>(selectedIds.Count);
            foreach (var id in selectedIds)
            {
                if (!NewsLogMessageIdDiscovery.IsCanonicalMessageId(id))
                {
                    results.Add(new CorpusIdPreflightResult(id, CorpusIdPreflightKind.InvalidMessageId, "canonical validation failed"));
                    continue;
                }

                if (!corpusUniqueIds.Contains(id))
                {
                    results.Add(new CorpusIdPreflightResult(id, CorpusIdPreflightKind.NotInCorpus, "not present in discovery set"));
                    continue;
                }

                results.Add(new CorpusIdPreflightResult(id, CorpusIdPreflightKind.InCorpus, null));
            }

            return results;
        }

        /// <summary>
        /// Publishes a bounded number of Article Work RPCs for the selected IDs and records outcomes.
        /// Failed retrievals are recorded; IDs are not replaced.
        /// </summary>
        public static async Task<IReadOnlyList<CorpusIdPreflightResult>> RunLiveRetrievalPreflightAsync(
            ArticleWorkStressClient client,
            string backbone,
            IReadOnlyList<string> selectedIds,
            int maxIds,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentException.ThrowIfNullOrWhiteSpace(backbone);
            ArgumentNullException.ThrowIfNull(selectedIds);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxIds, 1);

            var limit = Math.Min(maxIds, selectedIds.Count);
            var results = new List<CorpusIdPreflightResult>(limit);
            for (var i = 0; i < limit; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = selectedIds[i];
                try
                {
                    var reply = await client.RequestAsync(backbone, id, cancellationToken).ConfigureAwait(false);
                    var kind = reply.Outcome switch
                    {
                        ArticleWorkStressOutcome.Success => CorpusIdPreflightKind.Retrieved,
                        ArticleWorkStressOutcome.ArticleNotFound => CorpusIdPreflightKind.NotFound,
                        ArticleWorkStressOutcome.NoResponse => CorpusIdPreflightKind.Timeout,
                        _ => CorpusIdPreflightKind.RetrievalFailed,
                    };
                    results.Add(new CorpusIdPreflightResult(id, kind, reply.Error ?? reply.Outcome.ToString()));
                }
                catch (RabbitMQ.Client.Exceptions.AlreadyClosedException ex)
                {
                    results.Add(new CorpusIdPreflightResult(id, CorpusIdPreflightKind.RetrievalFailed, ex.GetType().Name));
                }
                catch (RabbitMQ.Client.Exceptions.OperationInterruptedException ex)
                {
                    results.Add(new CorpusIdPreflightResult(id, CorpusIdPreflightKind.RetrievalFailed, ex.GetType().Name));
                }
                catch (IOException ex)
                {
                    // Includes BrokerUnreachableException.
                    results.Add(new CorpusIdPreflightResult(id, CorpusIdPreflightKind.RetrievalFailed, ex.GetType().Name));
                }
                catch (ObjectDisposedException ex)
                {
                    results.Add(new CorpusIdPreflightResult(id, CorpusIdPreflightKind.RetrievalFailed, ex.GetType().Name));
                }
                catch (InvalidOperationException ex)
                {
                    results.Add(new CorpusIdPreflightResult(id, CorpusIdPreflightKind.RetrievalFailed, ex.GetType().Name));
                }
                catch (TimeoutException ex)
                {
                    results.Add(new CorpusIdPreflightResult(id, CorpusIdPreflightKind.RetrievalFailed, ex.GetType().Name));
                }
            }

            return results;
        }

        /// <summary>
        /// Throws when preflight results must block throughput publishing.
        /// </summary>
        /// <remarks>
        /// Blocks on invalid / non-corpus IDs, and when every probed ID fails retrieval
        /// (no successful retrieve). Does not invent replacement Message-IDs.
        /// Partial NotFound among some Retrieved successes is recorded by the caller and does not throw.
        /// </remarks>
        public static void ThrowIfPreflightBlocksPublish(IReadOnlyList<CorpusIdPreflightResult> results)
        {
            ArgumentNullException.ThrowIfNull(results);
            if (results.Count == 0)
            {
                throw new InvalidOperationException(
                    "Article preflight produced no results. Stress will not publish without a completed preflight.");
            }

            foreach (var result in results)
            {
                if (result.Kind is CorpusIdPreflightKind.InvalidMessageId or CorpusIdPreflightKind.NotInCorpus)
                {
                    throw new InvalidOperationException(
                        "Article preflight blocked publish: " + result.Kind + " for " + result.MessageId
                        + (result.Detail is null ? string.Empty : (" (" + result.Detail + ")"))
                        + ". No replacement Message-IDs will be generated.");
                }
            }

            var retrieved = results.Count(static result => result.Kind == CorpusIdPreflightKind.Retrieved);

            if (retrieved == 0)
            {
                throw new InvalidOperationException(
                    "Article preflight blocked publish: none of the "
                    + results.Count
                    + " probed corpus Message-IDs retrieved successfully. "
                    + "Failures are explicit; Message-IDs were not replaced.");
            }
        }
    }

    public enum CorpusIdPreflightKind
    {
        InCorpus,
        NotInCorpus,
        InvalidMessageId,
        Retrieved,
        NotFound,
        Timeout,
        RetrievalFailed,
    }

    public sealed record CorpusIdPreflightResult(string MessageId, CorpusIdPreflightKind Kind, string? Detail);
}

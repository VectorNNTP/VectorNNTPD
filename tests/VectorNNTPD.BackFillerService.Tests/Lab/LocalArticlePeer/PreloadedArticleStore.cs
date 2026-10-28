using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace VectorNNTPD.BackFillerService.Tests.Lab.LocalArticlePeer
{
    /// <summary>
    /// Lab-only immutable store of pre-framed ARTICLE responses. Built once at peer startup;
    /// shared buffers are never mutated after construction.
    /// </summary>
    internal sealed class PreloadedArticleStore
    {
        private readonly ConcurrentDictionary<string, PreloadedArticle> _articles =
            new(StringComparer.Ordinal);

        private PreloadedArticleStore(
            IReadOnlyDictionary<string, PreloadedArticle> articles,
            PreloadInitializationMetrics metrics)
        {
            foreach (var pair in articles)
            {
                _articles[pair.Key] = pair.Value;
            }

            Metrics = metrics;
        }

        /// <summary>Startup load counters (not reset during the measured interval).</summary>
        internal PreloadInitializationMetrics Metrics { get; }

        /// <summary>Number of Message-IDs with a precomputed response.</summary>
        internal int Count => _articles.Count;

        /// <summary>Resolves a precomputed ARTICLE response for <paramref name="messageId"/>.</summary>
        internal bool TryGet(string messageId, out PreloadedArticle article) =>
            _articles.TryGetValue(messageId, out article!);

        /// <summary>
        /// Loads every indexed article, frames once, and stores status+body wire bytes.
        /// Throws when any article cannot be read or framed (fail-closed startup).
        /// </summary>
        internal static async Task<PreloadedArticleStore> LoadAsync(
            CorpusArticleIndex index,
            int maxArticleBytes,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(index);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxArticleBytes, 1);

            var sw = Stopwatch.StartNew();
            var map = new Dictionary<string, PreloadedArticle>(index.Count, StringComparer.Ordinal);
            long logicalBytes = 0;
            long wireBytes = 0;
            var failures = 0;
            Exception? firstFailure = null;

            foreach (var messageId in index.MessageIds.OrderBy(static id => id, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!index.TryResolve(messageId, out var path))
                {
                    failures++;
                    firstFailure ??= new FileNotFoundException(
                        $"Indexed Message-ID could not be resolved to an existing file: {messageId}");
                    continue;
                }

                try
                {
                    var (logical, framed) = await NntpWireFramer
                        .ReadAndFrameAsync(path, maxArticleBytes, cancellationToken)
                        .ConfigureAwait(false);
                    var status = Encoding.ASCII.GetBytes($"220 0 {messageId} article follows\r\n");
                    var wire = new byte[status.Length + framed.Length];
                    Buffer.BlockCopy(status, 0, wire, 0, status.Length);
                    Buffer.BlockCopy(framed, 0, wire, status.Length, framed.Length);
                    map[messageId] = new PreloadedArticle(wire, logical.Length);
                    logicalBytes += logical.Length;
                    wireBytes += wire.Length;
                }
                // codeql[cs/catch-of-all-exceptions] Fail-closed preload must capture every article load failure and continue counting remaining files.
                catch (Exception ex)
                {
                    failures++;
                    firstFailure ??= ex;
                }
            }

            sw.Stop();
            var metrics = new PreloadInitializationMetrics(
                ArticlesLoaded: map.Count,
                LogicalBytesLoaded: logicalBytes,
                WireBytesLoaded: wireBytes,
                LoadDuration: sw.Elapsed,
                LoadFailures: failures);

            if (failures > 0)
            {
                throw new InvalidOperationException(
                    $"PreloadedArticleStore failed to load {failures} article(s) "
                    + $"(loaded={map.Count}, duration={sw.Elapsed}).",
                    firstFailure);
            }

            if (map.Count != index.Count)
            {
                throw new InvalidOperationException(
                    $"PreloadedArticleStore count mismatch: loaded={map.Count} index={index.Count}.");
            }

            return new PreloadedArticleStore(map, metrics);
        }
    }

    /// <summary>Immutable precomputed ARTICLE wire response for one Message-ID.</summary>
    /// <param name="WireResponse">Status line + framed body including terminator; never mutated.</param>
    /// <param name="LogicalByteCount">Logical on-disk article size (destuffed payload length).</param>
    internal sealed record PreloadedArticle(byte[] WireResponse, int LogicalByteCount);

    /// <summary>One-shot preload initialization counters exposed for lab evidence.</summary>
    /// <param name="ArticlesLoaded">Articles successfully framed into the store.</param>
    /// <param name="LogicalBytesLoaded">Sum of logical article sizes.</param>
    /// <param name="WireBytesLoaded">Sum of status+framed response sizes retained in memory.</param>
    /// <param name="LoadDuration">Wall time for the preload pass.</param>
    /// <param name="LoadFailures">Articles that failed to read or frame (startup aborts when &gt; 0).</param>
    internal sealed record PreloadInitializationMetrics(
        int ArticlesLoaded,
        long LogicalBytesLoaded,
        long WireBytesLoaded,
        TimeSpan LoadDuration,
        int LoadFailures);
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Core;
using VectorNNTPD.StorageService.Configuration;
using VectorNNTPD.StorageService.Storage.SegmentPools;
using VectorNNTPD.StorageService.Storage.SpoolPersistence;
using VectorNNTPD.StorageService.Storage.SpoolShards;

namespace VectorNNTPD.StorageService.Storage.ArticleIndex
{

    /// <summary>Filesystem-threshold LFU eviction for one spool.</summary>
    /// <remarks>
    /// Eviction starts when that spool's filesystem used percent is at or above
    /// <see cref="SegmentPoolsOptions.HighPercent"/> and continues until usage is at or below
    /// <see cref="SegmentPoolsOptions.LowPercent"/>, no eligible victim remains, measurement is lost,
    /// or <see cref="ArticleIndexSettings.MaxEvictionAttempts"/> is reached. Falling below High alone
    /// does not stop an in-progress eviction run.
    /// </remarks>
    internal sealed class SpoolLfuEviction
    {
        private readonly StorageServerOptions _options;
        private readonly BlockDeviceMonitorService _monitor;
        private readonly ISpoolArticleFiles _files;
        private readonly ArticleReadLeaseRegistry _readLeases;
        private readonly ILogger _logger;

        /// <summary>Initializes eviction helpers.</summary>
        public SpoolLfuEviction(
            IOptions<StorageServerOptions> options,
            BlockDeviceMonitorService monitor,
            ISpoolArticleFiles files,
            ArticleReadLeaseRegistry readLeases,
            ILogger<ArticleIndexService> logger)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(monitor);
            ArgumentNullException.ThrowIfNull(files);
            ArgumentNullException.ThrowIfNull(readLeases);
            ArgumentNullException.ThrowIfNull(logger);
            _options = options.Value;
            _monitor = monitor;
            _files = files;
            _readLeases = readLeases;
            _logger = logger;
        }

        /// <summary>
        /// Evicts until filesystem used percent is at or below <see cref="SegmentPoolsOptions.LowPercent"/>.
        /// </summary>
        /// <param name="index">Open article index.</param>
        /// <param name="spoolDirectory">Spool whose occupancy is measured.</param>
        /// <param name="cancellationToken">Observed between attempts.</param>
        internal async ValueTask EvictUntilBelowHighAsync(
            ArticleIndexService index,
            string spoolDirectory,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(index);
            ArgumentException.ThrowIfNullOrWhiteSpace(spoolDirectory);
            var pools = _options.SegmentPools ?? new SegmentPoolsOptions();
            if (!TryGetUsedPercent(spoolDirectory, out var used))
            {
                // No sample: do not invent an empty filesystem or start eviction.
                return;
            }

            if (used < pools.HighPercent)
            {
                return;
            }

            var attempts = 0;
            while (attempts < ArticleIndexSettings.MaxEvictionAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetUsedPercent(spoolDirectory, out used))
                {
                    return;
                }

                if (used <= pools.LowPercent)
                {
                    return;
                }

                if (!await index.TryEvictOneAsync(spoolDirectory, this, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                attempts++;
            }
        }

        /// <summary>Deletes one eligible LFU victim when possible.</summary>
        /// <param name="index">Open article index.</param>
        /// <param name="spoolDirectory">Spool root.</param>
        /// <param name="candidate">LFU row chosen by the index.</param>
        /// <param name="cancellationToken">Observed before index deletion.</param>
        /// <returns><see langword="true"/> when the file and index row were removed.</returns>
        internal async ValueTask<bool> EvictCandidateAsync(
            ArticleIndexService index,
            string spoolDirectory,
            ArticleIndexRecord candidate,
            CancellationToken cancellationToken)
        {
            var path = SpoolArticlePath.CombineArticle(spoolDirectory, candidate.ArticleId);
            var fileExisted = _files.Exists(path);
            try
            {
                _files.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ArticleIndexLogMessages.EvictionFileDeleteFailed(
                    _logger,
                    candidate.ArticleId.ToLowerHexString(),
                    ex.GetType().Name);
                return false;
            }

            try
            {
                if (!await index.DeleteEvictedRowAsync(spoolDirectory, candidate.ArticleId, cancellationToken)
                        .ConfigureAwait(false))
                {
                    ArticleIndexLogMessages.EvictionIndexDeleteFailed(
                        _logger,
                        candidate.ArticleId.ToLowerHexString(),
                        spoolDirectory);
                    return false;
                }
            }
            catch (Exception ex) when (!ExceptionHelpers.IsFatal(ex))
            {
                ArticleIndexLogMessages.EvictionIndexDeleteFailed(
                    _logger,
                    candidate.ArticleId.ToLowerHexString(),
                    spoolDirectory);
                ExpectedException.Ignore(ex);
                return false;
            }

            if (fileExisted && candidate.ArtSizeBytes > 0)
            {
                _monitor.NoteFreedBytes(spoolDirectory, candidate.ArtSizeBytes);
            }

            return true;
        }

        /// <summary>Returns whether <paramref name="articleId"/> may be evicted from <paramref name="spoolDirectory"/>.</summary>
        internal bool IsEligible(ArticleIndexService index, string spoolDirectory, ArticleId articleId, long accessedAt)
        {
            if (_readLeases.IsLeased(articleId) || index.IsIngestionReserved(articleId))
            {
                return false;
            }

            return index.IsEvictionCandidate(spoolDirectory, articleId, accessedAt);
        }

        private bool TryGetUsedPercent(string spoolDirectory, out int usedPercent) =>
            SegmentPoolCapacityGate.TryGetUsedPercent(_monitor.Observations, spoolDirectory, out usedPercent);
    }
}

using System.Buffers;
using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Articles.Parsing;
using VectorNNTPD.Common.Articles.Processing;
using VectorNNTPD.StorageService.Configuration;

namespace VectorNNTPD.StorageService.Ingress
{

    /// <summary>
    /// Applies <see cref="ArticlePathMode.Traverse"/>. The production host publishes the result.
    /// This type can still stop after the rewrite when a caller does not publish.
    /// </summary>
    /// <remarks>
    /// An optional observer sees the rewritten bytes. The observer copy is the caller's.
    /// The rewrite checks that the canonical <see cref="ArticleId"/> is unchanged.
    /// </remarks>
    internal sealed class AcceptedArticlePathProcessor : IAcceptedArticleProcessor
    {
        private readonly StorageServerRuntimeOptionsSource? _runtimeSource;
        private readonly string? _pathIdentity;
        private readonly ILogger _logger;
        private readonly Action<ReadOnlyMemory<byte>>? _rewritten;
        private NntpArticleParser? _parser;

        /// <summary>
        /// Rewritten-article buffers. At most eight arrays are retained in each size bucket.
        /// A 750 KiB article uses the 1 MiB bucket. A 5 MiB article uses the 8 MiB bucket.
        /// </summary>
        private static readonly ArrayPool<byte> RewritePool = ArrayPool<byte>.Create(8 * 1024 * 1024, 8);

        /// <summary>Initializes the host processor. The FQDN is read in <see cref="EnsureReady"/>.</summary>
        /// <param name="runtimeSource">Published runtime options.</param>
        /// <param name="logger">Failure logger. Article bodies are not written.</param>
        public AcceptedArticlePathProcessor(
            StorageServerRuntimeOptionsSource runtimeSource,
            ILogger<AcceptedArticlePathProcessor> logger)
        {
            ArgumentNullException.ThrowIfNull(runtimeSource);
            ArgumentNullException.ThrowIfNull(logger);
            _runtimeSource = runtimeSource;
            _logger = logger;
        }

        /// <summary>Initializes a processor with a known Path identity.</summary>
        /// <param name="pathIdentity">Server FQDN prepended by <see cref="ArticlePathMode.Traverse"/>.</param>
        /// <param name="logger">Failure logger.</param>
        /// <param name="rewritten">
        /// Called with the rewritten article bytes before they are dropped. Production does not set this.
        /// </param>
        internal AcceptedArticlePathProcessor(
            string pathIdentity,
            ILogger logger,
            Action<ReadOnlyMemory<byte>>? rewritten = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pathIdentity);
            ArgumentNullException.ThrowIfNull(logger);
            _pathIdentity = pathIdentity.Trim();
            _logger = logger;
            _rewritten = rewritten;
            _parser = new NntpArticleParser(_pathIdentity);
        }

        /// <inheritdoc />
        public void EnsureReady()
        {
            if (_parser is not null)
            {
                return;
            }

            var identity = _pathIdentity ?? _runtimeSource!.Current.Fqdn;
            if (string.IsNullOrWhiteSpace(identity))
            {
                throw new InvalidOperationException("StorageServer Path identity has not been published.");
            }

            _parser = new NntpArticleParser(identity.Trim());
        }

        /// <inheritdoc />
        public ValueTask<AcceptedArticleProcessResult> ProcessAsync(
            AcceptedArticle article,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                TryRewrite(article, out var rewritten)
                    ? Observe(rewritten)
                    : AcceptedArticleProcessResult.Failed);
        }

        /// <summary>
        /// Rewrites Path and returns the new article bytes.
        /// </summary>
        /// <param name="article">Queued article whose bytes have not yet received this server's Path hop.</param>
        /// <param name="rewritten">Rewritten article bytes when the method returns <see langword="true"/>.</param>
        /// <returns><see langword="false"/> when parse, materialize, or the identity check fails.</returns>
        /// <remarks>
        /// Uses the same parser and canonical materializer as <see cref="ArticleRecordFactory"/>.
        /// Body-line limits stay in force. The queued <see cref="ArticleId"/> is checked against the
        /// Message-ID in the rewritten bytes. This path does not compute XXH3 of the rewritten article:
        /// publication compares those bytes directly, and the queued <c>ArtHash</c> still describes the
        /// pre-rewrite buffer.
        /// </remarks>
        internal bool TryRewrite(AcceptedArticle article, out ReadOnlyMemory<byte> rewritten)
        {
            EnsureReady();
            var parse = _parser!.Parse(article.ArtData);
            if (!parse.IsAccepted)
            {
                AcceptedArticleWorkerLogMessages.PathRejected(
                    _logger,
                    article.ArtId.ToLowerHexString(),
                    parse.FailureCode.ToString(),
                    NntpArticleCanonicalFailureCode.None.ToString());
                rewritten = default;
                return false;
            }

            var materialize = NntpArticleCanonicalMaterializer.Materialize(
                in parse,
                ArticlePathMode.Traverse,
                ArticleResourceLimits.MaxArticleBytes,
                ArticlePathCanonicalizer.OrganizationalTrackerHost,
                rentFrom: null,
                CanonicalBodyMode.PreserveCanonicalBody);
            if (!materialize.IsAccepted || materialize.ArticleBytes is null)
            {
                AcceptedArticleWorkerLogMessages.PathRejected(
                    _logger,
                    article.ArtId.ToLowerHexString(),
                    NntpArticleParseFailureCode.None.ToString(),
                    materialize.FailureCode.ToString());
                rewritten = default;
                return false;
            }

            var artData = materialize.ArticleBytes;
            var fields = ArticleFieldTable.Locate(artData, parse.SelectedDateHeaderName);
            if (ArticleId.FromMessageId(fields.MessageId.Slice(artData)) != article.ArtId)
            {
                AcceptedArticleWorkerLogMessages.PathIdentityChanged(
                    _logger,
                    article.ArtId.ToLowerHexString());
                rewritten = default;
                return false;
            }

            rewritten = artData;
            return true;
        }

        /// <summary>
        /// Rewrites Path into a rented buffer. The caller must <see cref="ReturnRewriteBuffer"/> exactly once.
        /// </summary>
        /// <param name="article">Queued article whose bytes have not yet received this server's Path hop.</param>
        /// <param name="buffer">Rented buffer when the method returns <see langword="true"/>; otherwise <see langword="null"/>.</param>
        /// <param name="length">Canonical length within <paramref name="buffer"/>.</param>
        /// <returns><see langword="false"/> when parse, materialize, or the identity check fails. No buffer is returned to the caller on failure.</returns>
        internal bool TryRewriteRented(AcceptedArticle article, out byte[]? buffer, out int length)
        {
            EnsureReady();
            buffer = null;
            length = 0;
            var parse = _parser!.Parse(article.ArtData);
            if (!parse.IsAccepted)
            {
                AcceptedArticleWorkerLogMessages.PathRejected(
                    _logger,
                    article.ArtId.ToLowerHexString(),
                    parse.FailureCode.ToString(),
                    NntpArticleCanonicalFailureCode.None.ToString());
                return false;
            }

            // ArtData is already CanonicalV1 from ingest Normalize; only Path (and Date) must change.
            var materialize = NntpArticleCanonicalMaterializer.Materialize(
                in parse,
                ArticlePathMode.Traverse,
                ArticleResourceLimits.MaxArticleBytes,
                ArticlePathCanonicalizer.OrganizationalTrackerHost,
                RewritePool,
                CanonicalBodyMode.PreserveCanonicalBody);
            if (!materialize.IsAccepted || materialize.ArticleBytes is null)
            {
                AcceptedArticleWorkerLogMessages.PathRejected(
                    _logger,
                    article.ArtId.ToLowerHexString(),
                    NntpArticleParseFailureCode.None.ToString(),
                    materialize.FailureCode.ToString());
                return false;
            }

            var rented = materialize.ArticleBytes;
            var canonical = rented.AsSpan(0, materialize.CanonicalLength);
            var fields = ArticleFieldTable.Locate(canonical, parse.SelectedDateHeaderName);
            if (ArticleId.FromMessageId(fields.MessageId.Slice(canonical)) != article.ArtId)
            {
                RewritePool.Return(rented);
                AcceptedArticleWorkerLogMessages.PathIdentityChanged(
                    _logger,
                    article.ArtId.ToLowerHexString());
                return false;
            }

            buffer = rented;
            length = materialize.CanonicalLength;
            return true;
        }

        /// <summary>Returns a buffer from <see cref="TryRewriteRented"/>. The buffer is not cleared.</summary>
        /// <param name="buffer">Buffer previously returned by <see cref="TryRewriteRented"/>.</param>
        internal static void ReturnRewriteBuffer(byte[] buffer) => RewritePool.Return(buffer);

        /// <summary>Shows the rewritten bytes to the optional observer and reports completion.</summary>
        /// <param name="rewritten">Rewritten article bytes.</param>
        /// <returns><see cref="AcceptedArticleProcessResult.Completed"/>.</returns>
        private AcceptedArticleProcessResult Observe(ReadOnlyMemory<byte> rewritten)
        {
            _rewritten?.Invoke(rewritten);
            return AcceptedArticleProcessResult.Completed;
        }
    }
}

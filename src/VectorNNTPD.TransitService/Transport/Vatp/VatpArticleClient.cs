using VectorNNTPD.Common.Articles;
using VectorNNTPD.Common.Messaging.Cache;

namespace VectorNNTPD.TransitService.Transport.Vatp
{
    /// <summary>Pool-backed VATP article fetch orchestration.</summary>
    internal sealed class VatpArticleClient : IVatpArticleClient
    {
        private readonly VatpConnectionPool _pool;
        private readonly ILogger<VatpArticleClient> _logger;

        public VatpArticleClient(VatpConnectionPool pool, ILogger<VatpArticleClient> logger)
        {
            _pool = pool;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<VatpFetchResult> FetchArticleAsync(
            string fqdn,
            int vatpPort,
            Guid requestId,
            ArticleId articleId,
            CancellationToken cancellationToken)
        {
            if (requestId == Guid.Empty)
            {
                return VatpFetchResult.ProtocolFailure("RequestId must not be empty.", requestId, articleId);
            }

            if (!VatpEndpointFields.IsCanonicalFqdn(fqdn))
            {
                return VatpFetchResult.ProtocolFailure("VATP endpoint requires a lowercase dotted DNS fqdn.", requestId, articleId);
            }

            if (!VatpEndpointFields.IsCanonicalPort(vatpPort))
            {
                return VatpFetchResult.ProtocolFailure("VATP endpoint requires a port in the range 1–65535.", requestId, articleId);
            }

            var (connection, acquireFailure) = await _pool.AcquireAsync(fqdn, vatpPort, cancellationToken)
                .ConfigureAwait(false);
            if (acquireFailure is { } failure)
            {
                return VatpFetchResult.ConnectionFailure(failure.Error, requestId, articleId);
            }

            if (connection is null)
            {
                return VatpFetchResult.ConnectionFailure("Failed to acquire VATP connection.", requestId, articleId);
            }

            try
            {
                return await connection.FetchArticleAsync(requestId, articleId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (connection.IsDead)
                {
                    _pool.RemoveDead(connection);
                }
                else
                {
                    _pool.Release(connection);
                }
            }
        }
    }
}

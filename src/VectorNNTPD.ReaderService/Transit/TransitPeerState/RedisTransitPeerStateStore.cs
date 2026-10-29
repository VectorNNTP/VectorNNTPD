using System.Globalization;
using System.Text;
using VectorNNTPD.ReaderService.Redis;

namespace VectorNNTPD.ReaderService.Transit
{
    /// <summary>
    /// Redis-backed cluster Transit inbound membership using one atomic Lua EVAL
    /// on the shared <see cref="IRedisService"/> connection.
    /// </summary>
    internal sealed class RedisTransitPeerStateStore : ITransitPeerStateStore
    {
        private readonly IRedisService _redis;

        /// <summary>Initializes a new instance of the <see cref="RedisTransitPeerStateStore"/> class.</summary>
        public RedisTransitPeerStateStore(IRedisService redis)
        {
            ArgumentNullException.ThrowIfNull(redis);
            _redis = redis;
        }

        /// <inheritdoc />
        public ValueTask<TransitPeerStateAdmitResult> TryAdmitAsync(
            string identifier,
            string ownerId,
            int maxIncoming,
            long generation,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync(
                TransitPeerStateScripts.TryAdmit,
                identifier,
                [
                    Utf8(ownerId),
                    Utf8(maxIncoming.ToString(CultureInfo.InvariantCulture)),
                    Utf8(now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
                    Utf8(((long)leaseTtl.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)),
                    Utf8(generation.ToString(CultureInfo.InvariantCulture)),
                ],
                result => result == TransitPeerStateEngine.Accepted
                    ? new TransitPeerStateAdmitResult(TransitPeerStateAdmitStatus.Accepted, generation)
                    : new TransitPeerStateAdmitResult(TransitPeerStateAdmitStatus.Rejected),
                new TransitPeerStateAdmitResult(TransitPeerStateAdmitStatus.Unavailable),
                cancellationToken);

        /// <inheritdoc />
        public async ValueTask ReleaseAsync(
            string identifier,
            string ownerId,
            long generation,
            CancellationToken cancellationToken = default)
        {
            _ = await ExecuteAsync(
                TransitPeerStateScripts.Release,
                identifier,
                [
                    Utf8(ownerId),
                    Utf8(generation.ToString(CultureInfo.InvariantCulture)),
                ],
                static _ => true,
                fallback: true,
                cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public ValueTask<TransitPeerStateRenewStatus> RenewAsync(
            string identifier,
            string ownerId,
            long generation,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync(
                TransitPeerStateScripts.Renew,
                identifier,
                [
                    Utf8(ownerId),
                    Utf8(generation.ToString(CultureInfo.InvariantCulture)),
                    Utf8(now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
                    Utf8(((long)leaseTtl.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)),
                ],
                result => result == 1
                    ? TransitPeerStateRenewStatus.Renewed
                    : TransitPeerStateRenewStatus.Lost,
                TransitPeerStateRenewStatus.Unavailable,
                cancellationToken);

        /// <inheritdoc />
        public async ValueTask ReleaseOwnerAsync(
            string identifier,
            string ownerId,
            CancellationToken cancellationToken = default)
        {
            _ = await ExecuteAsync(
                TransitPeerStateScripts.ReleaseOwner,
                identifier,
                [Utf8(ownerId)],
                static _ => true,
                fallback: true,
                cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask<T> ExecuteAsync<T>(
            string script,
            string identifier,
            ReadOnlyMemory<byte>[] values,
            Func<long, T> map,
            T fallback,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
            if (!_redis.TryBeginOperation(out var isRecoveryProbe))
            {
                return fallback;
            }

            IRedisDatabase database;
            try
            {
                database = _redis.Database;
            }
            catch (InvalidOperationException)
            {
                _redis.AbandonOperation(isRecoveryProbe);
                return fallback;
            }

            try
            {
                var result = await database.ScriptEvaluateAsync(
                    script,
                    [TransitPeerStateKeys.Create(identifier)],
                    values,
                    cancellationToken).ConfigureAwait(false);
                _redis.CompleteOperation(isRecoveryProbe, succeeded: true);
                return map(result);
            }
            catch (OperationCanceledException)
            {
                _redis.AbandonOperation(isRecoveryProbe);
                throw;
            }
            catch (RedisUnavailableException)
            {
                _redis.CompleteOperation(isRecoveryProbe, succeeded: false);
                return fallback;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                _redis.CompleteOperation(isRecoveryProbe, succeeded: false, ex);
                return fallback;
            }
        }

        private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
    }
}

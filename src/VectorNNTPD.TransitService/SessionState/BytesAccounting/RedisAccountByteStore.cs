using System.Globalization;
using System.Text;
using VectorNNTPD.TransitService.Redis;

namespace VectorNNTPD.TransitService.SessionState.BytesAccounting
{
    /// <summary>Redis remaining-quota store. One EVAL per observe/apply. No key TTL.</summary>
    internal sealed class RedisAccountByteStore : IAccountByteStore
    {
        private readonly IRedisService _redis;

        /// <summary>Initializes a new instance of the <see cref="RedisAccountByteStore"/> class.</summary>
        public RedisAccountByteStore(IRedisService redis)
        {
            ArgumentNullException.ThrowIfNull(redis);
            _redis = redis;
        }

        /// <inheritdoc />
        public ValueTask<long?> ApplyAsync(
            string accountName,
            string batchId,
            long consumed,
            long mysqlRemainingAfter,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
            return ExecuteAsync(
                AccountByteScripts.Apply,
                accountName,
                [
                    Utf8(batchId),
                    Utf8(AccountByteEngine.ClampNonNegative(consumed).ToString(CultureInfo.InvariantCulture)),
                    Utf8(AccountByteEngine.ClampNonNegative(mysqlRemainingAfter).ToString(CultureInfo.InvariantCulture)),
                ],
                static result => result < 0 ? 0L : result,
                cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask<long?> ObserveAsync(string accountName, CancellationToken cancellationToken = default) =>
            ExecuteAsync(
                AccountByteScripts.Observe,
                accountName,
                [],
                static result => result,
                cancellationToken);

        /// <inheritdoc />
        public ValueTask<long?> DeleteAsync(string accountName, CancellationToken cancellationToken = default) =>
            ExecuteAsync(
                AccountByteScripts.Delete,
                accountName,
                [],
                static result => result < 0 ? 0L : result,
                cancellationToken);

        private async ValueTask<long?> ExecuteAsync(
            string script,
            string accountName,
            ReadOnlyMemory<byte>[] values,
            Func<long, long> map,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            if (!_redis.TryBeginOperation(out var isRecoveryProbe))
            {
                return null;
            }

            IRedisDatabase database;
            try
            {
                database = _redis.Database;
            }
            catch (InvalidOperationException)
            {
                _redis.AbandonOperation(isRecoveryProbe);
                return null;
            }

            try
            {
                var result = await database.ScriptEvaluateAsync(
                    script,
                    [AccountByteKeys.Create(accountName)],
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
                return null;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                _redis.CompleteOperation(isRecoveryProbe, succeeded: false, ex);
                return null;
            }
        }

        private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
    }
}

using System.Runtime.InteropServices;
using StackExchange.Redis;

namespace VectorNNTPD.ReaderService.Redis
{
    /// <summary>Adapts <see cref="IDatabase"/> to <see cref="IRedisDatabase"/>.</summary>
    internal sealed class StackExchangeRedisDatabase : IRedisDatabase
    {
        private readonly IDatabase _database;

        /// <summary>Initializes a new instance of the <see cref="StackExchangeRedisDatabase"/> class.</summary>
        public StackExchangeRedisDatabase(IDatabase database)
        {
            ArgumentNullException.ThrowIfNull(database);
            _database = database;
        }

        /// <inheritdoc />
        public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default) =>
            await Await(_database.PingAsync(), cancellationToken).ConfigureAwait(false);

        /// <inheritdoc />
        public ValueTask<bool> KeyExistsAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
        {
            try
            {
                var pending = _database.KeyExistsAsync(ToRedisKey(key));
                return AwaitExists(pending, cancellationToken);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (IsInfrastructureFailure(ex))
            {
                return ValueTask.FromException<bool>(new RedisUnavailableException("Redis EXISTS failed.", ex));
            }
        }

        /// <inheritdoc />
        public ValueTask<byte[]?> GetAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
        {
            try
            {
                var pending = _database.StringGetAsync(ToRedisKey(key));
                return AwaitGet(pending, cancellationToken);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (IsInfrastructureFailure(ex))
            {
                return ValueTask.FromException<byte[]?>(new RedisUnavailableException("Redis GET failed.", ex));
            }
        }

        /// <inheritdoc />
        public ValueTask SetAsync(
            ReadOnlyMemory<byte> key,
            ReadOnlyMemory<byte> value,
            TimeSpan expiry,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var pending = _database.StringSetAsync(ToRedisKey(key), ToRedisValue(value), expiry);
                return AwaitSet(pending, cancellationToken);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (IsInfrastructureFailure(ex))
            {
                return ValueTask.FromException(new RedisUnavailableException("Redis SET failed.", ex));
            }
        }

        /// <inheritdoc />
        public ValueTask<long> ScriptEvaluateAsync(
            string script,
            ReadOnlyMemory<byte>[] keys,
            ReadOnlyMemory<byte>[] values,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(script);
            ArgumentNullException.ThrowIfNull(keys);
            ArgumentNullException.ThrowIfNull(values);
            try
            {
                var redisKeys = new RedisKey[keys.Length];
                for (var i = 0; i < keys.Length; i++)
                {
                    redisKeys[i] = ToRedisKey(keys[i]);
                }

                var redisValues = new RedisValue[values.Length];
                for (var i = 0; i < values.Length; i++)
                {
                    redisValues[i] = ToRedisValue(values[i]);
                }

                var pending = _database.ScriptEvaluateAsync(script, redisKeys, redisValues);
                return AwaitEval(pending, cancellationToken);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (IsInfrastructureFailure(ex))
            {
                return ValueTask.FromException<long>(new RedisUnavailableException("Redis EVAL failed.", ex));
            }
        }

        private static RedisKey ToRedisKey(ReadOnlyMemory<byte> key)
        {
            // RedisKey is byte[]-backed and has no ReadOnlyMemory implicit.
            // HistoryRedisKeys.Create returns a dedicated array; reuse it.
            if (MemoryMarshal.TryGetArray(key, out var segment)
                && segment.Array is { } array
                && segment.Offset == 0
                && segment.Count == array.Length)
            {
                return (RedisKey)array;
            }

            return (RedisKey)key.ToArray();
        }

        private static RedisValue ToRedisValue(ReadOnlyMemory<byte> value) =>
            value.IsEmpty ? RedisValue.EmptyString : (RedisValue)value;

        private static ValueTask<bool> AwaitExists(Task<bool> pending, CancellationToken cancellationToken)
        {
            if (pending.IsCompletedSuccessfully)
            {
                return new ValueTask<bool>(pending.Result);
            }

            return AwaitExistsSlow(pending, cancellationToken);
        }

        private static async ValueTask<bool> AwaitExistsSlow(Task<bool> pending, CancellationToken cancellationToken)
        {
            try
            {
                return await Await(pending, cancellationToken).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (IsInfrastructureFailure(ex))
            {
                throw new RedisUnavailableException("Redis EXISTS failed.", ex);
            }
        }

        private static ValueTask<byte[]?> AwaitGet(Task<RedisValue> pending, CancellationToken cancellationToken)
        {
            if (pending.IsCompletedSuccessfully)
            {
                return new ValueTask<byte[]?>(ToBytes(pending.Result));
            }

            return AwaitGetSlow(pending, cancellationToken);
        }

        private static async ValueTask<byte[]?> AwaitGetSlow(Task<RedisValue> pending, CancellationToken cancellationToken)
        {
            try
            {
                var value = await Await(pending, cancellationToken).ConfigureAwait(false);
                return ToBytes(value);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (IsInfrastructureFailure(ex))
            {
                throw new RedisUnavailableException("Redis GET failed.", ex);
            }
        }

        private static byte[]? ToBytes(RedisValue value) => value.IsNull ? null : (byte[]?)value;

        private static ValueTask<long> AwaitEval(Task<RedisResult> pending, CancellationToken cancellationToken)
        {
            if (pending.IsCompletedSuccessfully)
            {
                return new ValueTask<long>(ToInt64(pending.Result));
            }

            return AwaitEvalSlow(pending, cancellationToken);
        }

        private static async ValueTask<long> AwaitEvalSlow(Task<RedisResult> pending, CancellationToken cancellationToken)
        {
            try
            {
                var result = await Await(pending, cancellationToken).ConfigureAwait(false);
                return ToInt64(result);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (IsInfrastructureFailure(ex))
            {
                throw new RedisUnavailableException("Redis EVAL failed.", ex);
            }
        }

        private static long ToInt64(RedisResult result)
        {
            if (result.IsNull)
            {
                return 0;
            }

            try
            {
                return (long)result;
            }
            catch (InvalidCastException)
            {
                return 0;
            }
        }

        private static ValueTask AwaitSet(Task<bool> pending, CancellationToken cancellationToken)
        {
            if (pending.IsCompletedSuccessfully)
            {
                return ValueTask.CompletedTask;
            }

            return AwaitSetSlow(pending, cancellationToken);
        }

        private static async ValueTask AwaitSetSlow(Task<bool> pending, CancellationToken cancellationToken)
        {
            try
            {
                _ = await Await(pending, cancellationToken).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex) when (IsInfrastructureFailure(ex))
            {
                throw new RedisUnavailableException("Redis SET failed.", ex);
            }
        }

        private static Task<T> Await<T>(Task<T> pending, CancellationToken cancellationToken) =>
            cancellationToken.CanBeCanceled ? pending.WaitAsync(cancellationToken) : pending;

        private static bool IsInfrastructureFailure(Exception exception) =>
            exception is RedisException or RedisTimeoutException or TimeoutException or IOException;
    }
}

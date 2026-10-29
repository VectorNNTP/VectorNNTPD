using System.Collections.Concurrent;
using VectorNNTPD.TransitService.Redis;

namespace VectorNNTPD.TransitService.Authentication
{
    /// <summary>
    /// Short-lived Redis cache around <see cref="INntpUserRecordStore"/> MySQL SELECT.
    /// </summary>
    /// <remarks>
    /// Caches a complete account-record snapshot only. Does not cache password
    /// verification or session admission. Redis GET/SET failure falls back to the
    /// inner store and does not trip the shared Redis availability circuit.
    /// Concurrent cold lookups for the same wire account name share one inner
    /// SELECT. Missing accounts are not cached. Idle single-flight state is
    /// removed when the shared lookup completes.
    /// </remarks>
    internal sealed class CachedNntpUserRecordStore : INntpUserRecordStore
    {
        internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

        private readonly INntpUserRecordStore _inner;
        private readonly IRedisService _redis;
        private readonly ILogger<CachedNntpUserRecordStore> _logger;
        private readonly ConcurrentDictionary<string, Task<NntpUserRecord?>> _inflight =
            new(StringComparer.Ordinal);

        /// <summary>Initializes a new instance of the <see cref="CachedNntpUserRecordStore"/> class.</summary>
        public CachedNntpUserRecordStore(
            INntpUserRecordStore inner,
            IRedisService redis,
            ILogger<CachedNntpUserRecordStore> logger)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(redis);
            ArgumentNullException.ThrowIfNull(logger);
            _inner = inner;
            _redis = redis;
            _logger = logger;
        }

        /// <summary>Gets the number of in-flight account-scoped lookups (tests).</summary>
        internal int InFlightCount => _inflight.Count;

        /// <inheritdoc />
        public async ValueTask<NntpUserRecord?> TryGetUserAsync(
            string accountName,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
            cancellationToken.ThrowIfCancellationRequested();

            var cached = await TryReadCacheAsync(accountName, cancellationToken).ConfigureAwait(false);
            if (cached.Status == CacheReadStatus.Hit)
            {
                AccountCacheLogMessages.Hit(_logger, AccountCacheKeys.HashHex(accountName));
                return cached.Record;
            }

            var tcs = new TaskCompletionSource<NntpUserRecord?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var shared = _inflight.GetOrAdd(accountName, tcs.Task);
            if (!ReferenceEquals(shared, tcs.Task))
            {
                return await shared.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var record = await LoadAndPopulateAsync(accountName, cached.Status).ConfigureAwait(false);
                tcs.SetResult(record);
                return record;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception ex)
            {
                tcs.SetException(ex);
                throw;
            }
            finally
            {
                _ = _inflight.TryRemove(new KeyValuePair<string, Task<NntpUserRecord?>>(accountName, tcs.Task));
            }
        }

        private async Task<NntpUserRecord?> LoadAndPopulateAsync(string accountName, CacheReadStatus priorStatus)
        {
            var again = await TryReadCacheAsync(accountName, CancellationToken.None).ConfigureAwait(false);
            if (again.Status == CacheReadStatus.Hit)
            {
                AccountCacheLogMessages.Hit(_logger, AccountCacheKeys.HashHex(accountName));
                return again.Record;
            }

            if (priorStatus == CacheReadStatus.Miss || again.Status == CacheReadStatus.Miss)
            {
                AccountCacheLogMessages.Miss(_logger, AccountCacheKeys.HashHex(accountName));
            }

            var record = await _inner.TryGetUserAsync(accountName, CancellationToken.None).ConfigureAwait(false);
            if (record is not null)
            {
                await TryWriteCacheAsync(accountName, record).ConfigureAwait(false);
            }

            return record;
        }

        private async ValueTask<CacheRead> TryReadCacheAsync(string accountName, CancellationToken cancellationToken)
        {
            if (!_redis.TryBeginOperation(out var isRecoveryProbe))
            {
                AccountCacheLogMessages.Unavailable(_logger, AccountCacheKeys.HashHex(accountName));
                return CacheRead.Unavailable;
            }

            IRedisDatabase database;
            try
            {
                database = _redis.Database;
            }
            catch (InvalidOperationException)
            {
                _redis.AbandonOperation(isRecoveryProbe);
                AccountCacheLogMessages.Unavailable(_logger, AccountCacheKeys.HashHex(accountName));
                return CacheRead.Unavailable;
            }

            byte[]? payload;
            try
            {
                payload = await database.GetAsync(AccountCacheKeys.Create(accountName), cancellationToken)
                    .ConfigureAwait(false);
                _redis.CompleteOperation(isRecoveryProbe, succeeded: true);
            }
            catch (OperationCanceledException)
            {
                _redis.AbandonOperation(isRecoveryProbe);
                throw;
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception)
            {
                // Cache is an optimization. Do not trip the shared Redis circuit.
                _redis.AbandonOperation(isRecoveryProbe);
                AccountCacheLogMessages.Unavailable(_logger, AccountCacheKeys.HashHex(accountName));
                return CacheRead.Unavailable;
            }

            if (payload is null)
            {
                return CacheRead.Missed;
            }

            if (!NntpAccountCacheCodec.TryDecode(payload, accountName, out var record) || record is null)
            {
                AccountCacheLogMessages.PayloadRejected(_logger, AccountCacheKeys.HashHex(accountName));
                return CacheRead.Missed;
            }

            return CacheRead.Found(record);
        }

        private async ValueTask TryWriteCacheAsync(string accountName, NntpUserRecord record)
        {
            byte[] payload;
            try
            {
                payload = NntpAccountCacheCodec.Encode(record);
            }
            catch (ArgumentOutOfRangeException)
            {
                AccountCacheLogMessages.PopulateSkipped(_logger, AccountCacheKeys.HashHex(accountName));
                return;
            }

            if (!_redis.TryBeginOperation(out var isRecoveryProbe))
            {
                AccountCacheLogMessages.PopulateSkipped(_logger, AccountCacheKeys.HashHex(accountName));
                return;
            }

            IRedisDatabase database;
            try
            {
                database = _redis.Database;
            }
            catch (InvalidOperationException)
            {
                _redis.AbandonOperation(isRecoveryProbe);
                AccountCacheLogMessages.PopulateSkipped(_logger, AccountCacheKeys.HashHex(accountName));
                return;
            }

            try
            {
                await database.SetAsync(
                        AccountCacheKeys.Create(accountName),
                        payload,
                        CacheTtl,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                _redis.CompleteOperation(isRecoveryProbe, succeeded: true);
            }
            // codeql[cs/catch-of-all-exceptions] Service/process boundary must observe every non-fatal failure without aborting the host.
            catch (Exception)
            {
                _redis.AbandonOperation(isRecoveryProbe);
                AccountCacheLogMessages.PopulateSkipped(_logger, AccountCacheKeys.HashHex(accountName));
            }
        }

        private enum CacheReadStatus
        {
            Hit,
            Miss,
            Unavailable,
        }

        private readonly record struct CacheRead(CacheReadStatus Status, NntpUserRecord? Record)
        {
            public static CacheRead Found(NntpUserRecord record) => new(CacheReadStatus.Hit, record);

            public static CacheRead Missed { get; } = new(CacheReadStatus.Miss, null);

            public static CacheRead Unavailable { get; } = new(CacheReadStatus.Unavailable, null);
        }
    }
}

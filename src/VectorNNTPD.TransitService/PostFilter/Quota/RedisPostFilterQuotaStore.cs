using System.Globalization;
using System.Text;
using VectorNNTPD.TransitService.Redis;

namespace VectorNNTPD.TransitService.PostFilter.Quota
{
    /// <summary>
    /// Redis-backed accept-quota using one atomic Lua EVAL per reserve / commit / release
    /// on the shared <see cref="IRedisService"/> connection.
    /// </summary>
    internal sealed class RedisPostFilterQuotaStore : IPostFilterQuotaStore
    {
        private readonly IRedisService _redis;

        /// <summary>Initializes a Redis-backed quota store.</summary>
        public RedisPostFilterQuotaStore(IRedisService redis)
        {
            ArgumentNullException.ThrowIfNull(redis);
            _redis = redis;
        }

        /// <inheritdoc />
        public ValueTask<PostFilterQuotaReserveStatus> ReserveAsync(
            string accountName,
            PostFilterReservationId reservation,
            DateTimeOffset now,
            PostFilterQuotaWindows windows,
            PostFilterQuotaCeilings ceilings,
            long messages,
            long bytes,
            int mpUnits,
            string? bodyHex,
            CancellationToken cancellationToken = default,
            long reservationTtlMs = 0)
        {
            windows.Validate();
            ceilings.Validate();
            ArgumentOutOfRangeException.ThrowIfNegative(messages);
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            if (mpUnits is not (0 or 1))
            {
                throw new ArgumentOutOfRangeException(nameof(mpUnits), mpUnits, "mpUnits must be 0 or 1.");
            }

            var ttl = ResolveReservationTtlMs(reservationTtlMs);
            var hash = mpUnits == 1 ? bodyHex : string.Empty;
            if (mpUnits == 1)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(bodyHex);
                hash = bodyHex;
            }

            return ExecuteAsync(
                PostFilterQuotaScripts.Reserve,
                accountName,
                [
                    Utf8(now.ToUnixTimeMilliseconds()),
                    Utf8(ttl),
                    Utf8(reservation.Token),
                    Utf8(reservation.Generation),
                    Utf8(messages),
                    Utf8(bytes),
                    Utf8(mpUnits),
                    Utf8(hash ?? string.Empty),
                    Utf8(windows.LongWindowMs),
                    Utf8(windows.ShortWindowMs),
                    Utf8(ceilings.MaxMessagesLong),
                    Utf8(ceilings.MaxBytesLong),
                    Utf8(ceilings.MaxIdenticalLong),
                    Utf8(ceilings.MaxMessagesShort),
                    Utf8(ceilings.MaxBytesShort),
                    Utf8(ceilings.MaxIdenticalShort),
                    Utf8(IdleTtlMs(windows, ttl)),
                ],
                static result => result is >= 0 and <= 7
                    ? (PostFilterQuotaReserveStatus)result
                    : PostFilterQuotaReserveStatus.Unavailable,
                PostFilterQuotaReserveStatus.Unavailable,
                cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask<PostFilterQuotaCommitStatus> CommitAsync(
            string accountName,
            PostFilterReservationId reservation,
            DateTimeOffset now,
            PostFilterQuotaWindows windows,
            PostFilterQuotaCeilings ceilings,
            CancellationToken cancellationToken = default)
        {
            windows.Validate();
            ceilings.Validate();
            return ExecuteAsync(
                PostFilterQuotaScripts.Commit,
                accountName,
                [
                    Utf8(now.ToUnixTimeMilliseconds()),
                    Utf8(reservation.Token),
                    Utf8(reservation.Generation),
                    Utf8(windows.LongWindowMs),
                    Utf8(windows.ShortWindowMs),
                    Utf8(ceilings.MaxMessagesLong),
                    Utf8(ceilings.MaxBytesLong),
                    Utf8(ceilings.MaxIdenticalLong),
                    Utf8(ceilings.MaxMessagesShort),
                    Utf8(ceilings.MaxBytesShort),
                    Utf8(ceilings.MaxIdenticalShort),
                    Utf8(IdleTtlMs(windows, (long)PostFilterQuotaDefaults.ReservationTtl.TotalMilliseconds)),
                ],
                static result => result == PostFilterQuotaCommitCodes.Committed
                    ? PostFilterQuotaCommitStatus.Committed
                    : PostFilterQuotaCommitStatus.Noop,
                PostFilterQuotaCommitStatus.Unavailable,
                cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask<PostFilterQuotaReleaseStatus> ReleaseAsync(
            string accountName,
            PostFilterReservationId reservation,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync(
                PostFilterQuotaScripts.Release,
                accountName,
                [
                    Utf8(now.ToUnixTimeMilliseconds()),
                    Utf8(reservation.Token),
                    Utf8(reservation.Generation),
                ],
                static result => result == PostFilterQuotaReleaseCodes.Released
                    ? PostFilterQuotaReleaseStatus.Released
                    : PostFilterQuotaReleaseStatus.Noop,
                PostFilterQuotaReleaseStatus.Unavailable,
                cancellationToken);

        private async ValueTask<T> ExecuteAsync<T>(
            string script,
            string accountName,
            ReadOnlyMemory<byte>[] values,
            Func<long, T> map,
            T fallback,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
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

            long result;
            try
            {
                // Observe EVAL even if the caller cancelled. WaitAsync on the Redis
                // task would otherwise abandon a completed Lua write and leak a
                // reservation until TTL. After the result is known, propagate
                // cancel so EvaluateAsync RELEASE can delete the row.
                result = await database.ScriptEvaluateAsync(
                    script,
                    [PostFilterQuotaKeys.CreateQuota(accountName), PostFilterQuotaKeys.CreateMultipost(accountName)],
                    values,
                    CancellationToken.None).ConfigureAwait(false);
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

            _redis.CompleteOperation(isRecoveryProbe, succeeded: true);
            cancellationToken.ThrowIfCancellationRequested();
            return map(result);
        }

        private static long ResolveReservationTtlMs(long reservationTtlMs) =>
            reservationTtlMs > 0
                ? reservationTtlMs
                : (long)PostFilterQuotaDefaults.ReservationTtl.TotalMilliseconds;

        private static long IdleTtlMs(PostFilterQuotaWindows windows, long reservationTtlMs) =>
            Math.Max(windows.LongWindowMs, windows.ShortWindowMs)
            + reservationTtlMs
            + PostFilterQuotaDefaults.IdleTtlSkewMs;

        private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

        private static byte[] Utf8(long value) =>
            Encoding.UTF8.GetBytes(value.ToString(CultureInfo.InvariantCulture));
    }
}

using System.Globalization;
using System.Text;
using VectorNNTPD.TransitService.Redis;
using VectorNNTPD.TransitService.SessionState.BytesAccounting;

namespace VectorNNTPD.TransitService.SessionState
{
    /// <summary>
    /// Redis-backed cluster session + source-IP membership using one atomic Lua EVAL
    /// on the shared <see cref="IRedisService"/> connection.
    /// </summary>
    internal sealed class RedisSessionStateStore : ISessionStateStore
    {
        private readonly IRedisService _redis;

        /// <summary>Initializes a new instance of the <see cref="RedisSessionStateStore"/> class.</summary>
        public RedisSessionStateStore(IRedisService redis)
        {
            ArgumentNullException.ThrowIfNull(redis);
            _redis = redis;
        }

        /// <inheritdoc />
        public ValueTask<SessionStateAdmitResult> TryAdmitAsync(
            string accountName,
            string normalizedSourceIp,
            string ownerId,
            int sessionLimit,
            int srcIpLimit,
            long sessionGeneration,
            long sourceGeneration,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            CancellationToken cancellationToken = default,
            bool trackSessions = false) =>
            ExecuteAsync(
                SessionStateScripts.TryAdmit,
                accountName,
                MembershipKeys(accountName),
                [
                    Utf8(normalizedSourceIp),
                    Utf8(ownerId),
                    Utf8(sessionLimit.ToString(CultureInfo.InvariantCulture)),
                    Utf8(srcIpLimit.ToString(CultureInfo.InvariantCulture)),
                    Utf8(now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
                    Utf8(((long)leaseTtl.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)),
                    Utf8(sessionGeneration.ToString(CultureInfo.InvariantCulture)),
                    Utf8(sourceGeneration.ToString(CultureInfo.InvariantCulture)),
                    Utf8(trackSessions ? "1" : "0"),
                ],
                result =>
                {
                    var status = SessionStateEngine.UnpackAdmitStatus(result);
                    var total = SessionStateEngine.UnpackAdmitSessionTotal(result);
                    return status switch
                    {
                        SessionStateEngine.AcceptedExisting =>
                            new SessionStateAdmitResult(SessionStateAdmitStatus.AcceptedExisting, total),
                        SessionStateEngine.AcceptedNew =>
                            new SessionStateAdmitResult(SessionStateAdmitStatus.AcceptedNew, total),
                        SessionStateEngine.RejectedSessionLimit =>
                            new SessionStateAdmitResult(SessionStateAdmitStatus.RejectedSessionLimit),
                        _ => new SessionStateAdmitResult(SessionStateAdmitStatus.RejectedSourceLimit),
                    };
                },
                new SessionStateAdmitResult(SessionStateAdmitStatus.Unavailable),
                cancellationToken);

        /// <inheritdoc />
        public ValueTask<int> ReleaseAsync(
            string accountName,
            string normalizedSourceIp,
            string ownerId,
            long sessionGeneration,
            long sourceGeneration,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync(
                SessionStateScripts.Release,
                accountName,
                MembershipKeys(accountName),
                [
                    Utf8(normalizedSourceIp),
                    Utf8(ownerId),
                    Utf8(sessionGeneration.ToString(CultureInfo.InvariantCulture)),
                    Utf8(sourceGeneration.ToString(CultureInfo.InvariantCulture)),
                ],
                static result => result < 0 ? 0 : (int)result,
                fallback: 0,
                cancellationToken);

        /// <inheritdoc />
        public ValueTask<SessionStateRenewResult> RenewAsync(
            string accountName,
            string ownerId,
            long sessionGeneration,
            IReadOnlyList<(string Ip, long Generation)> sources,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(sources);
            var values = new ReadOnlyMemory<byte>[5 + (sources.Count * 2)];
            values[0] = Utf8(ownerId);
            values[1] = Utf8(sessionGeneration.ToString(CultureInfo.InvariantCulture));
            values[2] = Utf8(now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
            values[3] = Utf8(((long)leaseTtl.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
            values[4] = Utf8(sources.Count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < sources.Count; i++)
            {
                values[5 + (i * 2)] = Utf8(sources[i].Ip);
                values[6 + (i * 2)] = Utf8(sources[i].Generation.ToString(CultureInfo.InvariantCulture));
            }

            return ExecuteAsync(
                SessionStateScripts.Renew,
                accountName,
                MembershipKeys(accountName),
                values,
                result => result == 0
                    ? new SessionStateRenewResult(SessionStateRenewStatus.Lost)
                    : new SessionStateRenewResult(SessionStateRenewStatus.Renewed, (int)result),
                new SessionStateRenewResult(SessionStateRenewStatus.Unavailable),
                cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask<SessionStateRenewAndApplyResult> RenewAndApplyAsync(
            string accountName,
            string ownerId,
            long sessionGeneration,
            IReadOnlyList<(string Ip, long Generation)> sources,
            DateTimeOffset now,
            TimeSpan leaseTtl,
            string batchId,
            long consumed,
            long mysqlRemainingAfter,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(sources);
            ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
            var values = new ReadOnlyMemory<byte>[8 + (sources.Count * 2)];
            values[0] = Utf8(ownerId);
            values[1] = Utf8(sessionGeneration.ToString(CultureInfo.InvariantCulture));
            values[2] = Utf8(now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
            values[3] = Utf8(((long)leaseTtl.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
            values[4] = Utf8(sources.Count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < sources.Count; i++)
            {
                values[5 + (i * 2)] = Utf8(sources[i].Ip);
                values[6 + (i * 2)] = Utf8(sources[i].Generation.ToString(CultureInfo.InvariantCulture));
            }

            values[5 + (sources.Count * 2)] = Utf8(batchId);
            values[6 + (sources.Count * 2)] = Utf8(
                AccountByteEngine.ClampNonNegative(consumed).ToString(CultureInfo.InvariantCulture));
            values[7 + (sources.Count * 2)] = Utf8(
                AccountByteEngine.ClampNonNegative(mysqlRemainingAfter)
                    .ToString(CultureInfo.InvariantCulture));

            return ExecuteAsync(
                SessionStateScripts.RenewAndApply,
                accountName,
                CombinedKeys(accountName),
                values,
                packed =>
                {
                    var (status, remaining) = SessionStateBytePack.Decode(packed);
                    return new SessionStateRenewAndApplyResult(status, remaining);
                },
                new SessionStateRenewAndApplyResult(SessionStateRenewStatus.Unavailable, remaining: null),
                cancellationToken);
        }

        /// <inheritdoc />
        public async ValueTask ReleaseOwnerAsync(
            string accountName,
            string ownerId,
            CancellationToken cancellationToken = default)
        {
            _ = await ExecuteAsync(
                SessionStateScripts.ReleaseOwner,
                accountName,
                MembershipKeys(accountName),
                [Utf8(ownerId)],
                static _ => true,
                fallback: true,
                cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask<T> ExecuteAsync<T>(
            string script,
            string accountName,
            ReadOnlyMemory<byte>[] keys,
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

            try
            {
                var result = await database.ScriptEvaluateAsync(
                    script,
                    keys,
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

        private static ReadOnlyMemory<byte>[] MembershipKeys(string accountName) =>
            [SessionStateKeys.CreateSource(accountName), SessionStateKeys.CreateSession(accountName)];

        private static ReadOnlyMemory<byte>[] CombinedKeys(string accountName) =>
            [
                SessionStateKeys.CreateSource(accountName),
                SessionStateKeys.CreateSession(accountName),
                AccountByteKeys.Create(accountName),
            ];

        private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
    }
}

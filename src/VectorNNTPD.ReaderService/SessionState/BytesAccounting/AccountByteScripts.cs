namespace VectorNNTPD.ReaderService.SessionState.BytesAccounting
{
    /// <summary>
    /// Redis Lua for cluster remaining-quota. Semantics match <see cref="AccountByteEngine"/>.
    /// The remaining HASH has no key TTL. Batch fields are marks only; they do not expire
    /// the remaining value.
    /// </summary>
    internal static class AccountByteScripts
    {
        /// <summary>
        /// Applies one consumed batch idempotently. Remaining is floored to
        /// <c>mysqlRemainingAfter</c> and never increased. The same batch id is a no-op.
        /// </summary>
        /// <remarks>
        /// KEYS[1] remaining HASH.
        /// ARGV: batchId, consumed, mysqlRemainingAfter.
        /// <c>consumed</c> is accepted for the call contract but is not subtracted.
        /// MySQL already subtracted the batch; decrementing Redis is not retry-safe
        /// when another node has already floored to a mysql_after that includes it.
        /// Returns remaining &gt;= 0.
        /// </remarks>
        public const string Apply =
            """
            -- nntpd-bytes-apply
            -- Retain at most 256 batch marks (AccountByteBatchId.MaxRetainedMarks).
            local function trim_marks(key, batchField)
              if redis.call('HLEN', key) <= 257 then
                return
              end
              local fields = redis.call('HKEYS', key)
              for i = 1, #fields do
                local f = fields[i]
                if f ~= 'remaining' and f ~= batchField then
                  redis.call('HDEL', key, f)
                  if redis.call('HLEN', key) <= 257 then
                    return
                  end
                end
              end
            end
            local key = KEYS[1]
            local batchId = ARGV[1]
            local mysql = tonumber(ARGV[3])
            if mysql == nil or mysql < 0 then
              mysql = 0
            end
            if batchId == nil or batchId == '' then
              batchId = ''
            end
            local batchField = 'b:' .. batchId
            if redis.call('EXISTS', key) == 0 then
              if batchId ~= '' then
                redis.call('HSET', key, 'remaining', tostring(mysql), batchField, '1')
                trim_marks(key, batchField)
              else
                redis.call('HSET', key, 'remaining', tostring(mysql))
              end
              return mysql
            end
            if batchId ~= '' and redis.call('HEXISTS', key, batchField) == 1 then
              local cur = tonumber(redis.call('HGET', key, 'remaining'))
              if cur == nil or cur < 0 then
                return 0
              end
              return cur
            end
            local current = tonumber(redis.call('HGET', key, 'remaining'))
            if current == nil or current < 0 then
              current = 0
            end
            local next = current
            if next > mysql then
              next = mysql
            end
            if batchId ~= '' then
              redis.call('HSET', key, 'remaining', tostring(next), batchField, '1')
              trim_marks(key, batchField)
            else
              redis.call('HSET', key, 'remaining', tostring(next))
            end
            return next
            """;

        /// <summary>Reads remaining. Missing key returns <c>-1</c>.</summary>
        /// <remarks>KEYS[1] remaining HASH. No ARGV. Never creates or writes the key.</remarks>
        public const string Observe =
            """
            -- nntpd-bytes-observe
            if redis.call('EXISTS', KEYS[1]) == 0 then
              return -1
            end
            local n = tonumber(redis.call('HGET', KEYS[1], 'remaining'))
            if n == nil or n < 0 then
              return 0
            end
            return n
            """;

        /// <summary>
        /// Deletes the remaining-quota HASH. Used only for explicit operator top-up
        /// invalidation. Never creates or writes the key.
        /// </summary>
        /// <remarks>KEYS[1] remaining HASH. No ARGV. Returns 1 if the key existed.</remarks>
        public const string Delete =
            """
            -- nntpd-bytes-delete
            return redis.call('DEL', KEYS[1])
            """;
    }
}

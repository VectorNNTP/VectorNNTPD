namespace VectorNNTPD.ReaderService.Transit
{
    /// <summary>
    /// Redis Lua scripts for atomic cluster Transit inbound-connection admission.
    /// Semantics match <see cref="TransitPeerStateEngine"/>.
    /// </summary>
    internal static class TransitPeerStateScripts
    {
        /// <summary>
        /// Atomically evaluates the inbound limit and, on accept, increments this
        /// owner's count. Rejection writes no ownership increment.
        /// </summary>
        /// <remarks>
        /// KEYS[1] tconn hash.
        /// ARGV: owner, maxIncoming, nowMs, leaseMs, generation.
        /// Returns 1 accepted, 0 rejected (closed or at limit).
        /// Expired fields are pruned before the limit is summed.
        /// <c>max &lt;= 0</c> rejects without incrementing.
        /// </remarks>
        public const string TryAdmit =
            """
            -- nntpd-tconn-try
            local key = KEYS[1]
            local owner = ARGV[1]
            local maxIncoming = tonumber(ARGV[2])
            local now = tonumber(ARGV[3])
            local lease = tonumber(ARGV[4])
            local gen = ARGV[5]
            local expiry = now + lease

            local function parse_value(v)
              local p1 = string.find(v, '|', 1, true)
              if not p1 then
                return nil, nil, nil
              end
              local p2 = string.find(v, '|', p1 + 1, true)
              if not p2 then
                return nil, nil, nil
              end
              return tonumber(string.sub(v, 1, p1 - 1)), string.sub(v, p1 + 1, p2 - 1), tonumber(string.sub(v, p2 + 1))
            end

            local function prune(hashKey)
              local entries = redis.call('HGETALL', hashKey)
              local expired = {}
              for i = 1, #entries, 2 do
                local exp = parse_value(entries[i + 1])
                if exp == nil or exp <= now then
                  expired[#expired + 1] = entries[i]
                end
              end
              if #expired > 0 then
                redis.call('HDEL', hashKey, unpack(expired))
              end
            end

            local function increment(hashKey, field, requestedGen)
              local cur = redis.call('HGET', hashKey, field)
              if cur then
                local exp, storedGen, count = parse_value(cur)
                if storedGen and storedGen == requestedGen then
                  redis.call('HSET', hashKey, field, tostring(expiry) .. '|' .. storedGen .. '|' .. tostring((count or 0) + 1))
                  return
                end
              end
              redis.call('HSET', hashKey, field, tostring(expiry) .. '|' .. requestedGen .. '|1')
            end

            prune(key)
            if redis.call('HLEN', key) == 0 then
              redis.call('DEL', key)
            end

            if maxIncoming == nil or maxIncoming <= 0 then
              return 0
            end

            local total = 0
            local entries = redis.call('HGETALL', key)
            for i = 1, #entries, 2 do
              local _, _, count = parse_value(entries[i + 1])
              total = total + (count or 0)
            end
            if total >= maxIncoming then
              return 0
            end

            increment(key, owner, gen)
            return 1
            """;

        /// <summary>
        /// Decrements this owner's count when the generation matches.
        /// </summary>
        /// <remarks>
        /// KEYS[1] tconn hash. ARGV: owner, generation. Always returns 1.
        /// A mismatched generation is a no-op. The field is deleted at count 0;
        /// the hash is deleted when empty.
        /// </remarks>
        public const string Release =
            """
            -- nntpd-tconn-release
            local key = KEYS[1]
            local owner = ARGV[1]
            local gen = ARGV[2]

            local function parse_value(v)
              local p1 = string.find(v, '|', 1, true)
              if not p1 then
                return nil, nil, nil
              end
              local p2 = string.find(v, '|', p1 + 1, true)
              if not p2 then
                return nil, nil, nil
              end
              return tonumber(string.sub(v, 1, p1 - 1)), string.sub(v, p1 + 1, p2 - 1), tonumber(string.sub(v, p2 + 1))
            end

            local cur = redis.call('HGET', key, owner)
            if cur then
              local exp, storedGen, count = parse_value(cur)
              if storedGen == gen then
                if count == nil or count <= 1 then
                  redis.call('HDEL', key, owner)
                else
                  redis.call('HSET', key, owner, tostring(exp) .. '|' .. storedGen .. '|' .. tostring(count - 1))
                end
              end
            end
            if redis.call('HLEN', key) == 0 then
              redis.call('DEL', key)
            end
            return 1
            """;

        /// <summary>
        /// Refreshes this owner's unexpired matching lease. Missing ownership is not recreated.
        /// </summary>
        /// <remarks>
        /// KEYS[1] tconn hash. ARGV: owner, generation, nowMs, leaseMs.
        /// Returns 1 when the field was renewed, 0 when it was missing, expired, or mismatched.
        /// </remarks>
        public const string Renew =
            """
            -- nntpd-tconn-renew
            local key = KEYS[1]
            local owner = ARGV[1]
            local gen = ARGV[2]
            local now = tonumber(ARGV[3])
            local lease = tonumber(ARGV[4])

            local function parse_value(v)
              local p1 = string.find(v, '|', 1, true)
              if not p1 then
                return nil, nil, nil
              end
              local p2 = string.find(v, '|', p1 + 1, true)
              if not p2 then
                return nil, nil, nil
              end
              return tonumber(string.sub(v, 1, p1 - 1)), string.sub(v, p1 + 1, p2 - 1), tonumber(string.sub(v, p2 + 1))
            end

            local cur = redis.call('HGET', key, owner)
            if not cur then
              return 0
            end
            local exp, storedGen, count = parse_value(cur)
            if storedGen ~= gen or exp == nil or exp <= now then
              return 0
            end
            redis.call('HSET', key, owner, tostring(now + lease) .. '|' .. gen .. '|' .. tostring(count or 1))
            return 1
            """;

        /// <summary>Deletes only this owner's field on this peer hash.</summary>
        /// <remarks>KEYS[1] tconn hash. ARGV: owner. Always returns 1.</remarks>
        public const string ReleaseOwner =
            """
            -- nntpd-tconn-release-owner
            local key = KEYS[1]
            local owner = ARGV[1]
            redis.call('HDEL', key, owner)
            if redis.call('HLEN', key) == 0 then
              redis.call('DEL', key)
            end
            return 1
            """;
    }
}

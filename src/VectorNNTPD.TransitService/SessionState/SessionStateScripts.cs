namespace VectorNNTPD.TransitService.SessionState
{
    /// <summary>
    /// Redis Lua scripts for atomic cluster session + source-IP admission.
    /// Semantics match <see cref="SessionStateEngine"/>.
    /// </summary>
    internal static class SessionStateScripts
    {
        /// <summary>
        /// Atomically evaluates both limits and, on accept, increments session and
        /// source-IP ownership. Rejection writes no fields.
        /// </summary>
        /// <remarks>
        /// KEYS[1] source hash, KEYS[2] session hash.
        /// ARGV: ip, owner, sessionLimit, srcIpLimit, nowMs, leaseMs, sessionGen, sourceGen,
        /// trackSessions (1 to increment the session hash when sessionLimit is 0).
        /// Accept returns <c>status + (sessionTotal * 4)</c> (3 existing source, 2 new source).
        /// Reject remains 1 session-limit, 0 source-limit. Session limit is checked first.
        /// </remarks>
        public const string TryAdmit =
            """
            -- nntpd-admit-try
            local srcKey = KEYS[1]
            local sessKey = KEYS[2]
            local ip = ARGV[1]
            local owner = ARGV[2]
            local sessionLimit = tonumber(ARGV[3])
            local srcLimit = tonumber(ARGV[4])
            local now = tonumber(ARGV[5])
            local lease = tonumber(ARGV[6])
            local sessionGen = ARGV[7]
            local sourceGen = ARGV[8]
            local srcField = ip .. '\31' .. owner
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

            local function prune(key)
              local entries = redis.call('HGETALL', key)
              local expired = {}
              for i = 1, #entries, 2 do
                local exp = parse_value(entries[i + 1])
                if exp == nil or exp <= now then
                  expired[#expired + 1] = entries[i]
                end
              end
              if #expired > 0 then
                redis.call('HDEL', key, unpack(expired))
              end
            end

            local function increment(key, field, gen)
              local cur = redis.call('HGET', key, field)
              if cur then
                local exp, storedGen, count = parse_value(cur)
                if storedGen and storedGen == gen then
                  redis.call('HSET', key, field, tostring(expiry) .. '|' .. storedGen .. '|' .. tostring((count or 0) + 1))
                  return
                end
              end
              redis.call('HSET', key, field, tostring(expiry) .. '|' .. gen .. '|1')
            end

            prune(srcKey)
            prune(sessKey)
            if redis.call('HLEN', srcKey) == 0 then
              redis.call('DEL', srcKey)
            end
            if redis.call('HLEN', sessKey) == 0 then
              redis.call('DEL', sessKey)
            end

            local sessionTotal = 0
            local sessEntries = redis.call('HGETALL', sessKey)
            for i = 1, #sessEntries, 2 do
              local _, _, count = parse_value(sessEntries[i + 1])
              sessionTotal = sessionTotal + (count or 0)
            end
            if sessionLimit ~= nil and sessionLimit > 0 and sessionTotal >= sessionLimit then
              return 1
            end

            local active = {}
            local srcEntries = redis.call('HGETALL', srcKey)
            for i = 1, #srcEntries, 2 do
              local f = srcEntries[i]
              local sep = string.find(f, '\31', 1, true)
              local fip = sep and string.sub(f, 1, sep - 1) or f
              active[fip] = true
            end

            if srcLimit ~= nil and srcLimit > 0 and not active[ip] then
              local distinct = 0
              for _ in pairs(active) do
                distinct = distinct + 1
              end
              if distinct >= srcLimit then
                return 0
              end
            end

            local trackSessions = tonumber(ARGV[9]) == 1
            if (sessionLimit ~= nil and sessionLimit > 0) or trackSessions then
              increment(sessKey, owner, sessionGen)
            end
            if srcLimit ~= nil and srcLimit > 0 then
              increment(srcKey, srcField, sourceGen)
            end
            local sessionTotal = 0
            local counted = redis.call('HGETALL', sessKey)
            for i = 1, #counted, 2 do
              local _, _, count = parse_value(counted[i + 1])
              sessionTotal = sessionTotal + (count or 0)
            end
            if active[ip] then
              return 3 + (sessionTotal * 4)
            end
            return 2 + (sessionTotal * 4)
            """;

        /// <summary>
        /// Decrements this owner's session and source-IP counts when generations match.
        /// </summary>
        /// <remarks>
        /// KEYS[1] source hash, KEYS[2] session hash.
        /// ARGV: ip, owner, sessionGen, sourceGen. Returns the remaining cluster session total.
        /// </remarks>
        public const string Release =
            """
            -- nntpd-admit-release
            local srcKey = KEYS[1]
            local sessKey = KEYS[2]
            local ip = ARGV[1]
            local owner = ARGV[2]
            local sessionGen = ARGV[3]
            local sourceGen = ARGV[4]
            local srcField = ip .. '\31' .. owner

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

            local function decrement(key, field, gen)
              local cur = redis.call('HGET', key, field)
              if not cur then
                return
              end
              local exp, storedGen, count = parse_value(cur)
              if storedGen ~= gen then
                return
              end
              if count == nil or count <= 1 then
                redis.call('HDEL', key, field)
              else
                redis.call('HSET', key, field, tostring(exp) .. '|' .. storedGen .. '|' .. tostring(count - 1))
              end
              if redis.call('HLEN', key) == 0 then
                redis.call('DEL', key)
              end
            end

            decrement(sessKey, owner, sessionGen)
            decrement(srcKey, srcField, sourceGen)
            local sessionTotal = 0
            local counted = redis.call('HGETALL', sessKey)
            for i = 1, #counted, 2 do
              local _, _, count = parse_value(counted[i + 1])
              sessionTotal = sessionTotal + (count or 0)
            end
            return sessionTotal
            """;

        /// <summary>
        /// Refreshes this owner's unexpired matching session and source leases.
        /// </summary>
        /// <remarks>
        /// KEYS[1] source hash, KEYS[2] session hash.
        /// ARGV: owner, sessionGen, nowMs, leaseMs, ipCount, ip1, gen1, ...
        /// Returns 0 when any requested field was lost. On success returns the
        /// unexpired cluster session total (minimum 1 so a source-only renew is not Lost).
        /// A sessionGen of 0 skips the session field.
        /// </remarks>
        public const string Renew =
            """
            -- nntpd-admit-renew
            local srcKey = KEYS[1]
            local sessKey = KEYS[2]
            local owner = ARGV[1]
            local sessionGen = ARGV[2]
            local now = tonumber(ARGV[3])
            local lease = tonumber(ARGV[4])
            local ipCount = tonumber(ARGV[5]) or 0

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

            local function can_renew(key, field, gen)
              local cur = redis.call('HGET', key, field)
              if not cur then
                return false
              end
              local exp, storedGen, count = parse_value(cur)
              if storedGen ~= gen or exp == nil or exp <= now then
                return false
              end
              return true, count
            end

            local function renew_field(key, field, gen, count)
              redis.call('HSET', key, field, tostring(now + lease) .. '|' .. gen .. '|' .. tostring(count or 1))
            end

            if sessionGen ~= '0' then
              local ok = can_renew(sessKey, owner, sessionGen)
              if not ok then
                return 0
              end
            end

            local i = 0
            while i < ipCount do
              local ip = ARGV[6 + (i * 2)]
              local gen = ARGV[7 + (i * 2)]
              local ok = can_renew(srcKey, ip .. '\31' .. owner, gen)
              if not ok then
                return 0
              end
              i = i + 1
            end

            if sessionGen ~= '0' then
              local ok, count = can_renew(sessKey, owner, sessionGen)
              if ok then
                renew_field(sessKey, owner, sessionGen, count)
              end
            end

            i = 0
            while i < ipCount do
              local ip = ARGV[6 + (i * 2)]
              local gen = ARGV[7 + (i * 2)]
              local ok, count = can_renew(srcKey, ip .. '\31' .. owner, gen)
              if ok then
                renew_field(srcKey, ip .. '\31' .. owner, gen, count)
              end
              i = i + 1
            end
            local sessionTotal = 0
            local counted = redis.call('HGETALL', sessKey)
            for i = 1, #counted, 2 do
              local exp, _, count = parse_value(counted[i + 1])
              if exp ~= nil and exp > now then
                sessionTotal = sessionTotal + (count or 0)
              end
            end
            if sessionTotal < 1 then
              sessionTotal = 1
            end
            return sessionTotal
            """;

        /// <summary>
        /// Renews this owner's unexpired matching leases, then APPLYs one byte batch.
        /// APPLY always runs; a lost renewal does not skip quota reconciliation.
        /// </summary>
        /// <remarks>
        /// KEYS[1] source hash, KEYS[2] session hash, KEYS[3] remaining-quota HASH.
        /// ARGV: owner, sessionGen, nowMs, leaseMs, ipCount, ip1, gen1, ...,
        /// batchId, consumed, mysqlRemainingAfter.
        /// Returns packed <see cref="SessionStateBytePack"/>: remaining when renewed,
        /// <c>-remaining - 1</c> when lost. APPLY is floor-only and idempotent.
        /// </remarks>
        public const string RenewAndApply =
            """
            -- nntpd-admit-renew-and-apply
            local srcKey = KEYS[1]
            local sessKey = KEYS[2]
            local bytesKey = KEYS[3]
            local owner = ARGV[1]
            local sessionGen = ARGV[2]
            local now = tonumber(ARGV[3])
            local lease = tonumber(ARGV[4])
            local ipCount = tonumber(ARGV[5]) or 0

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

            local function can_renew(key, field, gen)
              local cur = redis.call('HGET', key, field)
              if not cur then
                return false
              end
              local exp, storedGen, count = parse_value(cur)
              if storedGen ~= gen or exp == nil or exp <= now then
                return false
              end
              return true, count
            end

            local function renew_field(key, field, gen, count)
              redis.call('HSET', key, field, tostring(now + lease) .. '|' .. gen .. '|' .. tostring(count or 1))
            end

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

            local function apply_bytes()
              local batchId = ARGV[6 + (ipCount * 2)]
              local mysql = tonumber(ARGV[8 + (ipCount * 2)])
              if mysql == nil or mysql < 0 then
                mysql = 0
              end
              if batchId == nil or batchId == '' then
                batchId = ''
              end
              local batchField = 'b:' .. batchId
              if redis.call('EXISTS', bytesKey) == 0 then
                if batchId ~= '' then
                  redis.call('HSET', bytesKey, 'remaining', tostring(mysql), batchField, '1')
                  trim_marks(bytesKey, batchField)
                else
                  redis.call('HSET', bytesKey, 'remaining', tostring(mysql))
                end
                return mysql
              end
              if batchId ~= '' and redis.call('HEXISTS', bytesKey, batchField) == 1 then
                local cur = tonumber(redis.call('HGET', bytesKey, 'remaining'))
                if cur == nil or cur < 0 then
                  return 0
                end
                return cur
              end
              local current = tonumber(redis.call('HGET', bytesKey, 'remaining'))
              if current == nil or current < 0 then
                current = 0
              end
              local next = current
              if next > mysql then
                next = mysql
              end
              if batchId ~= '' then
                redis.call('HSET', bytesKey, 'remaining', tostring(next), batchField, '1')
                trim_marks(bytesKey, batchField)
              else
                redis.call('HSET', bytesKey, 'remaining', tostring(next))
              end
              return next
            end

            local renewed = 1
            if sessionGen ~= '0' then
              local ok = can_renew(sessKey, owner, sessionGen)
              if not ok then
                renewed = 0
              end
            end

            if renewed == 1 then
              local i = 0
              while i < ipCount do
                local ip = ARGV[6 + (i * 2)]
                local gen = ARGV[7 + (i * 2)]
                local ok = can_renew(srcKey, ip .. '\31' .. owner, gen)
                if not ok then
                  renewed = 0
                  break
                end
                i = i + 1
              end
            end

            if renewed == 1 then
              if sessionGen ~= '0' then
                local ok, count = can_renew(sessKey, owner, sessionGen)
                if ok then
                  renew_field(sessKey, owner, sessionGen, count)
                end
              end
              local i = 0
              while i < ipCount do
                local ip = ARGV[6 + (i * 2)]
                local gen = ARGV[7 + (i * 2)]
                local ok, count = can_renew(srcKey, ip .. '\31' .. owner, gen)
                if ok then
                  renew_field(srcKey, ip .. '\31' .. owner, gen, count)
                end
                i = i + 1
              end
            end

            local remaining = apply_bytes()
            if remaining == nil or remaining < 0 then
              remaining = 0
            end
            if renewed == 1 then
              return remaining
            end
            return -remaining - 1
            """;

        /// <summary>Deletes every field owned by this process incarnation.</summary>
        /// <remarks>KEYS[1] source hash, KEYS[2] session hash. ARGV: owner. Always returns 1.</remarks>
        public const string ReleaseOwner =
            """
            -- nntpd-admit-release-owner
            local srcKey = KEYS[1]
            local sessKey = KEYS[2]
            local owner = ARGV[1]
            redis.call('HDEL', sessKey, owner)
            if redis.call('HLEN', sessKey) == 0 then
              redis.call('DEL', sessKey)
            end
            local entries = redis.call('HGETALL', srcKey)
            local owned = {}
            for i = 1, #entries, 2 do
              local f = entries[i]
              local sep = string.find(f, '\31', 1, true)
              local fieldOwner = sep and string.sub(f, sep + 1) or ''
              if fieldOwner == owner then
                owned[#owned + 1] = f
              end
            end
            if #owned > 0 then
              redis.call('HDEL', srcKey, unpack(owned))
            end
            if redis.call('HLEN', srcKey) == 0 then
              redis.call('DEL', srcKey)
            end
            return 1
            """;
    }
}

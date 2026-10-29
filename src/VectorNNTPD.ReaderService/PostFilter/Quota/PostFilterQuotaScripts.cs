namespace VectorNNTPD.ReaderService.PostFilter.Quota
{
    /// <summary>
    /// Redis Lua scripts for atomic PostFilter accept-quota reserve / commit / release.
    /// Semantics match <see cref="PostFilterQuotaEngine"/>.
    /// </summary>
    internal static class PostFilterQuotaScripts
    {
        /// <summary>
        /// Evaluates all enabled ceilings and creates one reservation, or writes nothing.
        /// </summary>
        /// <remarks>
        /// KEYS[1] quota hash, KEYS[2] multipost hash.
        /// ARGV: nowMs, reservationTtlMs, reservationToken, generation, messages, bytes,
        /// mpUnits, bodyHex, longMs, shortMs, maxML, maxBL, maxIL, maxMS, maxBS, maxIS, idleMs.
        /// Returns 0 accept, 1–6 first deny, 7 conflict.
        /// </remarks>
        public const string Reserve =
            """
            -- nntpd-pf-reserve
            local qKey = KEYS[1]
            local mKey = KEYS[2]
            local now = tonumber(ARGV[1])
            local ttl = tonumber(ARGV[2])
            local resId = ARGV[3]
            local gen = ARGV[4]
            local messages = tonumber(ARGV[5])
            local bytes = tonumber(ARGV[6])
            local mpUnits = tonumber(ARGV[7])
            local bodyHex = ARGV[8]
            local longMs = tonumber(ARGV[9])
            local shortMs = tonumber(ARGV[10])
            local maxML = tonumber(ARGV[11])
            local maxBL = tonumber(ARGV[12])
            local maxIL = tonumber(ARGV[13])
            local maxMS = tonumber(ARGV[14])
            local maxBS = tonumber(ARGV[15])
            local maxIS = tonumber(ARGV[16])
            local idleMs = tonumber(ARGV[17])
            local rField = 'r:' .. resId

            local function window_ms(w)
              if w == 'L' then return longMs end
              return shortMs
            end

            local function bucket_id(w)
              return math.floor(now / window_ms(w))
            end

            local function bucket_ended(w, b)
              return ((b + 1) * window_ms(w)) <= now
            end

            local function split_res(v)
              if not v then return nil end
              local p = {}
              local start = 1
              for i = 1, 5 do
                local sep = string.find(v, '|', start, true)
                if not sep then return nil end
                p[i] = string.sub(v, start, sep - 1)
                start = sep + 1
              end
              p[6] = string.sub(v, start)
              local exp = tonumber(p[1])
              local g = p[2]
              local m = tonumber(p[3])
              local b = tonumber(p[4])
              local mp = tonumber(p[5])
              if exp == nil or m == nil or b == nil or mp == nil then return nil end
              return { exp = exp, gen = g, messages = m, bytes = b, mp = mp, body = p[6] }
            end

            local function parse_c_field(f)
              if string.sub(f, 1, 2) ~= 'c:' then return nil end
              local kind = string.sub(f, 3, 3)
              if (kind ~= 'm' and kind ~= 'b') or string.sub(f, 4, 4) ~= ':' then return nil end
              local w = string.sub(f, 5, 5)
              if (w ~= 'L' and w ~= 'S') or string.sub(f, 6, 6) ~= ':' then return nil end
              local b = tonumber(string.sub(f, 7))
              if b == nil then return nil end
              return kind, w, b
            end

            local function parse_m_field(f)
              local last = string.len(f)
              local colon2 = nil
              for i = last, 1, -1 do
                if string.sub(f, i, i) == ':' then
                  if colon2 == nil then
                    colon2 = i
                  else
                    local w = string.sub(f, i + 1, colon2 - 1)
                    local b = tonumber(string.sub(f, colon2 + 1))
                    local body = string.sub(f, 1, i - 1)
                    if (w == 'L' or w == 'S') and b ~= nil and string.len(body) > 0 then
                      return body, w, b
                    end
                    return nil
                  end
                end
              end
              return nil
            end

            local function hdel_list(key, dead)
              if #dead == 0 then return end
              redis.call('HDEL', key, unpack(dead))
              if redis.call('HLEN', key) == 0 then
                redis.call('DEL', key)
              end
            end

            local function prune_q()
              local entries = redis.call('HGETALL', qKey)
              local dead = {}
              for i = 1, #entries, 2 do
                local f = entries[i]
                if string.sub(f, 1, 2) == 'r:' then
                  local rec = split_res(entries[i + 1])
                  if rec == nil or rec.exp <= now then
                    dead[#dead + 1] = f
                  end
                else
                  local _, w, b = parse_c_field(f)
                  if w ~= nil and bucket_ended(w, b) then
                    dead[#dead + 1] = f
                  end
                end
              end
              hdel_list(qKey, dead)
            end

            local function prune_m()
              local entries = redis.call('HGETALL', mKey)
              local dead = {}
              for i = 1, #entries, 2 do
                local body, w, b = parse_m_field(entries[i])
                if w ~= nil and bucket_ended(w, b) then
                  dead[#dead + 1] = entries[i]
                end
              end
              hdel_list(mKey, dead)
            end

            local function touch()
              if idleMs ~= nil and idleMs > 0 then
                if redis.call('EXISTS', qKey) == 1 then
                  redis.call('PEXPIRE', qKey, idleMs)
                end
                if redis.call('EXISTS', mKey) == 1 then
                  redis.call('PEXPIRE', mKey, idleMs)
                end
              end
            end

            if mpUnits == 0 then
              bodyHex = ''
            end

            prune_q()
            prune_m()

            local existing = redis.call('HGET', qKey, rField)
            if existing then
              local rec = split_res(existing)
              if rec and rec.exp > now and rec.gen == gen and rec.messages == messages and rec.bytes == bytes and rec.mp == mpUnits and rec.body == bodyHex then
                return 0
              end
              return 7
            end

            local bL = bucket_id('L')
            local bS = bucket_id('S')
            local function hnum(key, field)
              local v = redis.call('HGET', key, field)
              if not v then return 0 end
              return tonumber(v) or 0
            end

            local cml = hnum(qKey, 'c:m:L:' .. bL)
            local cbl = hnum(qKey, 'c:b:L:' .. bL)
            local cms = hnum(qKey, 'c:m:S:' .. bS)
            local cbs = hnum(qKey, 'c:b:S:' .. bS)
            local cil = 0
            local cis = 0
            if mpUnits == 1 and bodyHex ~= '' then
              cil = hnum(mKey, bodyHex .. ':L:' .. bL)
              cis = hnum(mKey, bodyHex .. ':S:' .. bS)
            end

            local resMsg, resBytes, resIdent = 0, 0, 0
            local entries = redis.call('HGETALL', qKey)
            for i = 1, #entries, 2 do
              if string.sub(entries[i], 1, 2) == 'r:' then
                local rec = split_res(entries[i + 1])
                if rec and rec.exp > now then
                  resMsg = resMsg + rec.messages
                  resBytes = resBytes + rec.bytes
                  if mpUnits == 1 and rec.mp == 1 and rec.body == bodyHex then
                    resIdent = resIdent + rec.mp
                  end
                end
              end
            end

            if maxML > 0 and (cml + resMsg + messages) > maxML then return 1 end
            if maxBL > 0 and (cbl + resBytes + bytes) > maxBL then return 2 end
            if mpUnits == 1 and maxIL > 0 and (cil + resIdent + mpUnits) > maxIL then return 3 end
            if maxMS > 0 and (cms + resMsg + messages) > maxMS then return 4 end
            if maxBS > 0 and (cbs + resBytes + bytes) > maxBS then return 5 end
            if mpUnits == 1 and maxIS > 0 and (cis + resIdent + mpUnits) > maxIS then return 6 end

            redis.call('HSET', qKey, rField, tostring(now + ttl) .. '|' .. gen .. '|' .. tostring(messages) .. '|' .. tostring(bytes) .. '|' .. tostring(mpUnits) .. '|' .. bodyHex)
            touch()
            return 0
            """;

        /// <summary>
        /// Transfers reservation units into the current buckets, or no-ops.
        /// </summary>
        /// <remarks>
        /// KEYS[1] quota, KEYS[2] multipost.
        /// ARGV: nowMs, reservationToken, generation, longMs, shortMs,
        /// maxML, maxBL, maxIL, maxMS, maxBS, maxIS, idleMs.
        /// Units are read only from the reservation. Returns 0 no-op, 1 committed.
        /// </remarks>
        public const string Commit =
            """
            -- nntpd-pf-commit
            local qKey = KEYS[1]
            local mKey = KEYS[2]
            local now = tonumber(ARGV[1])
            local resId = ARGV[2]
            local gen = ARGV[3]
            local longMs = tonumber(ARGV[4])
            local shortMs = tonumber(ARGV[5])
            local maxML = tonumber(ARGV[6])
            local maxBL = tonumber(ARGV[7])
            local maxIL = tonumber(ARGV[8])
            local maxMS = tonumber(ARGV[9])
            local maxBS = tonumber(ARGV[10])
            local maxIS = tonumber(ARGV[11])
            local idleMs = tonumber(ARGV[12])
            local rField = 'r:' .. resId

            local function window_ms(w)
              if w == 'L' then return longMs end
              return shortMs
            end

            local function bucket_id(w)
              return math.floor(now / window_ms(w))
            end

            local function bucket_ended(w, b)
              return ((b + 1) * window_ms(w)) <= now
            end

            local function split_res(v)
              if not v then return nil end
              local p = {}
              local start = 1
              for i = 1, 5 do
                local sep = string.find(v, '|', start, true)
                if not sep then return nil end
                p[i] = string.sub(v, start, sep - 1)
                start = sep + 1
              end
              p[6] = string.sub(v, start)
              local exp = tonumber(p[1])
              local g = p[2]
              local m = tonumber(p[3])
              local b = tonumber(p[4])
              local mp = tonumber(p[5])
              if exp == nil or m == nil or b == nil or mp == nil then return nil end
              return { exp = exp, gen = g, messages = m, bytes = b, mp = mp, body = p[6] }
            end

            local function parse_c_field(f)
              if string.sub(f, 1, 2) ~= 'c:' then return nil end
              local kind = string.sub(f, 3, 3)
              if (kind ~= 'm' and kind ~= 'b') or string.sub(f, 4, 4) ~= ':' then return nil end
              local w = string.sub(f, 5, 5)
              if (w ~= 'L' and w ~= 'S') or string.sub(f, 6, 6) ~= ':' then return nil end
              local b = tonumber(string.sub(f, 7))
              if b == nil then return nil end
              return kind, w, b
            end

            local function parse_m_field(f)
              local last = string.len(f)
              local colon2 = nil
              for i = last, 1, -1 do
                if string.sub(f, i, i) == ':' then
                  if colon2 == nil then
                    colon2 = i
                  else
                    local w = string.sub(f, i + 1, colon2 - 1)
                    local b = tonumber(string.sub(f, colon2 + 1))
                    local body = string.sub(f, 1, i - 1)
                    if (w == 'L' or w == 'S') and b ~= nil and string.len(body) > 0 then
                      return body, w, b
                    end
                    return nil
                  end
                end
              end
              return nil
            end

            local function hdel_list(key, dead)
              if #dead == 0 then return end
              redis.call('HDEL', key, unpack(dead))
              if redis.call('HLEN', key) == 0 then
                redis.call('DEL', key)
              end
            end

            local function prune_q()
              local entries = redis.call('HGETALL', qKey)
              local dead = {}
              for i = 1, #entries, 2 do
                local f = entries[i]
                if string.sub(f, 1, 2) == 'r:' then
                  local rec = split_res(entries[i + 1])
                  if rec == nil or rec.exp <= now then
                    dead[#dead + 1] = f
                  end
                else
                  local _, w, b = parse_c_field(f)
                  if w ~= nil and bucket_ended(w, b) then
                    dead[#dead + 1] = f
                  end
                end
              end
              hdel_list(qKey, dead)
            end

            local function prune_m()
              local entries = redis.call('HGETALL', mKey)
              local dead = {}
              for i = 1, #entries, 2 do
                local body, w, b = parse_m_field(entries[i])
                if w ~= nil and bucket_ended(w, b) then
                  dead[#dead + 1] = entries[i]
                end
              end
              hdel_list(mKey, dead)
            end

            prune_q()
            prune_m()

            local existing = redis.call('HGET', qKey, rField)
            local rec = split_res(existing)
            if rec == nil then
              if existing then
                redis.call('HDEL', qKey, rField)
              end
              if redis.call('HLEN', qKey) == 0 then redis.call('DEL', qKey) end
              return 0
            end
            if rec.gen ~= gen then
              return 0
            end
            if rec.exp <= now then
              redis.call('HDEL', qKey, rField)
              if redis.call('HLEN', qKey) == 0 then redis.call('DEL', qKey) end
              return 0
            end

            local bL = bucket_id('L')
            local bS = bucket_id('S')
            if maxML > 0 then redis.call('HINCRBY', qKey, 'c:m:L:' .. bL, rec.messages) end
            if maxBL > 0 then redis.call('HINCRBY', qKey, 'c:b:L:' .. bL, rec.bytes) end
            if maxMS > 0 then redis.call('HINCRBY', qKey, 'c:m:S:' .. bS, rec.messages) end
            if maxBS > 0 then redis.call('HINCRBY', qKey, 'c:b:S:' .. bS, rec.bytes) end
            if rec.mp == 1 and rec.body ~= '' then
              if maxIL > 0 then redis.call('HINCRBY', mKey, rec.body .. ':L:' .. bL, rec.mp) end
              if maxIS > 0 then redis.call('HINCRBY', mKey, rec.body .. ':S:' .. bS, rec.mp) end
            end
            redis.call('HDEL', qKey, rField)
            if redis.call('HLEN', qKey) == 0 then redis.call('DEL', qKey) end
            if idleMs ~= nil and idleMs > 0 then
              if redis.call('EXISTS', qKey) == 1 then redis.call('PEXPIRE', qKey, idleMs) end
              if redis.call('EXISTS', mKey) == 1 then redis.call('PEXPIRE', mKey, idleMs) end
            end
            return 1
            """;

        /// <summary>
        /// Deletes a matching live reservation. Never decrements committed usage.
        /// </summary>
        /// <remarks>
        /// KEYS[1] quota, KEYS[2] multipost (unused except for KEYS contract).
        /// ARGV: nowMs, reservationToken, generation.
        /// Returns 0 no-op, 1 released.
        /// </remarks>
        public const string Release =
            """
            -- nntpd-pf-release
            local qKey = KEYS[1]
            local now = tonumber(ARGV[1])
            local resId = ARGV[2]
            local gen = ARGV[3]
            local rField = 'r:' .. resId

            local function split_res(v)
              if not v then return nil end
              local p = {}
              local start = 1
              for i = 1, 5 do
                local sep = string.find(v, '|', start, true)
                if not sep then return nil end
                p[i] = string.sub(v, start, sep - 1)
                start = sep + 1
              end
              p[6] = string.sub(v, start)
              local exp = tonumber(p[1])
              local g = p[2]
              local m = tonumber(p[3])
              local b = tonumber(p[4])
              local mp = tonumber(p[5])
              if exp == nil or m == nil or b == nil or mp == nil then return nil end
              return { exp = exp, gen = g, messages = m, bytes = b, mp = mp, body = p[6] }
            end

            local existing = redis.call('HGET', qKey, rField)
            local rec = split_res(existing)
            if rec == nil then
              if existing then
                redis.call('HDEL', qKey, rField)
              end
              if redis.call('EXISTS', qKey) == 1 and redis.call('HLEN', qKey) == 0 then
                redis.call('DEL', qKey)
              end
              return 0
            end
            if rec.gen ~= gen then
              return 0
            end
            if rec.exp <= now then
              redis.call('HDEL', qKey, rField)
              if redis.call('HLEN', qKey) == 0 then redis.call('DEL', qKey) end
              return 0
            end
            redis.call('HDEL', qKey, rField)
            if redis.call('HLEN', qKey) == 0 then redis.call('DEL', qKey) end
            return 1
            """;
    }
}

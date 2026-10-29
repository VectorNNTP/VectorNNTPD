# PostFilter v1 — operator guide

> **Upstream reference only.** This guide describes PostFilter as implemented in
> `VectorNNTP.NNTPD` (`src/VectorNNTP.NNTPD/PostFilter/`). It is **not** part of the
> VectorNNTPD.BackFillerService or VectorNNTPD.StorageService hosts. Schema DDL under
> [`../../schema/`](../../schema/) is upstream reference only (see that README). Applying or
> operating PostFilter, Redis quota, or SPAMD remains an upstream NNTPD concern.
> **VectorNNTPD hosts do not require Redis or PostFilter to start or run.**

Authoritative operator reference for NNTPD PostFilter as implemented in `src/VectorNNTP.NNTPD/PostFilter/`.

**NntpDB is the only authoritative cluster policy.** Every NNTPD instance reads the published revision from `nntppostfiltercurrent`, loads that revision’s scalar row and collections, compiles an immutable local snapshot, and evaluates POST against that snapshot. There is no `Nntpd:PostFilter` options binding. A leftover `Nntpd:PostFilter` section fails startup. Redis holds quota **runtime state** only, never policy.

Protocol replies stay `240 Article received OK` or `441 Posting failed`; PostFilter reasons are never sent on the NNTP wire.

| Concern | Owner |
|---|---|
| Authoritative policy | MySQL `nntppostfiltercurrent` + revision-keyed tables |
| Repository | `MySqlPostFilterPolicyRepository` |
| Snapshot / refresh | `PostFilterPolicyCompiler`, `PostFilterPolicyService` (60s) |
| POST evaluation | `PostFilterEvaluator` (snapshot only; no SQL) |
| Redis quota | `PostFilter/Quota/` |
| SPAMD client | `SpamdCheckClient`, `SpamdScanArticleBuilder` |
| Settings table | Upstream VectorNNTP `docs/configuration.md` (NNTPD Redis / PostFilter keys — **not** migrated; VectorNNTPD [configuration.md](../../configuration.md) is BackFiller-only) |
| POST protocol | [commands.md](commands.md) |
| Validation matrix | `tests/VectorNNTP.NNTPD.Tests/PostFilter/PostFilterProductionPolicyMatrixTests.cs` |

NNTPD does not create or migrate these tables. Apply the canonical provisioning script [`docs/schema/postfilter.sql`](../../schema/postfilter.sql) once per NntpDB. Seed `Gate=Disabled` so the cluster stays inert until operators publish a new revision.

Account ArtType capability is a separate additive change on existing `nntpusers`. Apply [`docs/schema/nntpusers-account-art-type.sql`](../../schema/nntpusers-account-art-type.sql) to the account database **before** deploying an NNTPD that `SELECT`s `account_art_type`. A missing column is AUTHINFO `503`, not `481` and not unrestricted access.

---

## 1. What PostFilter does

PostFilter is a **POST-only** accept-path policy that runs after a destuffed article has been parsed into an immutable `ArticleRecord`. When the gate is `Active`, it can:

1. Reject a POST before queue admission (deny lists, ArtType policy, unauthenticated session, quota, SpamAssassin).
2. Reserve distributed accept-quota in Redis for the authenticated account.
3. Optionally send a **disposable** email-like scan representation to SpamAssassin SPAMD `CHECK`.
4. After `TryAdmit` succeeds, commit the reservation (or leave it until TTL if COMMIT cannot run).

PostFilter does **not** mutate `ArticleRecord`, does **not** create a second canonical article, and does **not** change History, spool, or worker processing.

---

## 2. Where it runs

PostFilter is invoked only from the ordinary POST injection path, after History Peek and `ArticleRecordIngress.TryCreateFromDestuffed`, and before `CreateQueued` / `TryAdmit`.

```text
POST
  → posting-permitted? (else 440; no article is read)
  → 340
  → StreamingPostArticleReader
  → catalogue `m` unapproved proto-article?
        yes → IModerationSubmissionService (no PostFilter, no Peek, no TryAdmit)
  → History Peek (Seen / Unavailable → 441; no PostFilter)
  → ArticleRecordIngress.TryCreateFromDestuffed
  → ArticleRecord (immutable canonical bytes)
  → account ArtType capability (AUTHINFO flags; unrestricted when no policy)
  → PostFilter.EvaluateAsync  (one captured policy snapshot)
        Gate Disabled → accept, no Redis, no SPAMD
        Gate Closed   → 441
        deny / ArtType / unauthenticated → 441 (no Redis)
        Redis RESERVE
        optional SPAMD CHECK
  → CreateQueued
  → TryAdmit
        fail → Redis RELEASE + enqueue admission evidence + 441
  → Redis COMMIT          (never RELEASE on COMMIT failure)
  → History Remember
  → 240 Article received OK
```

Every PostFilter `Reject` (and post-accept `TryAdmit` failure) enqueues one evidence row onto a bounded background queue. The 441 is written regardless of whether the enqueue or later MySQL insert succeeds. Acceptance never writes evidence.

Complete unstuffed article bytes (`ArticleRecord.ArtData`) are available at every PostFilter decision in the current POST path. The evidence payload is therefore those bytes when present, or NULL only if a future path rejects without a complete article.

One POST captures `PostFilterPolicyService.Current` once. That snapshot is used for the entire request. POST never queries NntpDB. Mid-request database edits do not affect that evaluation.

---

## 3. What it does not filter

PostFilter does **not** run for:

| Path | Why |
|------|-----|
| IHAVE | Transit ingest; no `IPostFilter` call |
| TAKETHIS | Transit ingest; no `IPostFilter` call |
| BackFiller | Separate process; no PostFilter |
| Catalogue status `m` unapproved submission | `SubmitForModerationAsync` returns before ArticleRecord / PostFilter |
| Incoming spool / queue workers | Filter already decided at POST admission |
| History duplicate / History unavailable | Peek rejects before PostFilter |
| Receive/parse failures (`Nntpd:MaxArticleSize`, malformed article) | Rejected before ArticleRecord |

Authorized moderator reinjection (`Approved:` + catalogue authorization) is ordinary POST and **does** run PostFilter.

---

## 4. Default / safe posture

| Gate | Meaning |
|------|---------|
| `Disabled` (default) | Skip remaining stages. No quota reservation. No SPAMD call. Ordinary POST continues. |
| `Active` | Evaluate deny, ArtType, quotas, and SpamAssassin. |
| `Closed` | Reject every POST that reaches PostFilter (`441`). No Redis. No SPAMD. |

`Disabled` is the recommended seed for `nntppostfilterpolicy.gate`. Existing POST behaviour continues until an operator updates NntpDB. There is no per-node Gate in appsettings.

When `Gate` is `Active`, an unauthenticated session that passes deny/ArtType is rejected at the quota stage (`reason=unauthenticated`) without RESERVE. AUTHINFO is therefore required for any POST that reaches an `Active` filter.

---

## 5. Authoritative NntpDB policy

```text
NntpDB nntppostfiltercurrent.revision
    → SELECT nntppostfilterpolicy WHERE revision = current
    → SELECT collections WHERE revision = current
    → PostFilterPolicyCompiler
    → immutable PostFilterPolicySnapshot
    → Volatile publish
    → POST reads Current only
```

NNTPD follows the same catalogue contract as `nntpgroups` / `nntpmoderators`:

- Initial load during `PostFilterPolicyService.StartAsync`. Failure prevents `RUNNING`. There is **no** appsettings fallback.
- Refresh every **60 seconds**. A new published revision compiles a snapshot and publishes it with `Volatile.Write`.
- The same published revision is re-read and recompiled for validity, but does not replace the live snapshot.
- Refresh failure (MySQL down, missing current row, compile error) keeps last-known-good. PostFilter is not disabled and does not revert to local JSON.
- Empty/malformed/duplicate collection entries fail compile of that refresh (or startup).

`nntppostfiltercurrent.revision` is the operator-visible published revision. Logs emit `revision={Revision}`. NNTPD never reads collection rows from a different revision than the current pointer.

---

## 6. NntpDB Schema and Administration

Existing NntpDB tables (`nntpgroups`, `nntpusers`, `nntpmoderators`) have no checked-in `CREATE TABLE`. NNTPD only `SELECT`s them. PostFilter is the first NntpDB surface with product DDL.

**Canonical provisioning script:** [`docs/schema/postfilter.sql`](../../schema/postfilter.sql)

Apply that script once per NntpDB. NNTPD does not create or migrate tables. If an earlier singleton (`nntppostfilterpolicy.policy_id = 1` without revision-keyed collections) was applied, drop those objects and apply this script; there is no automated migrator.

InnoDB + `utf8mb4` / `utf8mb4_unicode_ci` match typical MySQL 8 NntpDB deployments. Confirm `SHOW TABLE STATUS LIKE 'nntpmoderators'` if your server uses another collation.

### 6.1 Control-plane model

| Object | Role |
|--------|------|
| `nntppostfilterpolicy` | Append-only scalar revisions. Primary key is `revision`. |
| `nntppostfiltercurrent` | Singleton `policy_id = 1` pointing at the published revision. |
| `nntppostfilteraccounts` | Deny/allow **MD5 hex** AUTH identities (`CHAR(32)`). Real usernames are not stored. |
| `nntppostfiltercidrs` | Deny/allow CIDRs for **that revision**. |
| `nntppostfilterarttypes` | `reject` / `sa_exclude` **one** classifier name per row (`ENUM` of `ArticleType` names). |
| `nntppostfiltersahosts` | SPAMD hosts (`host_order`) for **that revision**. |
| `nntppostfilterrejections` | Append-only PostFilter rejection evidence (not SA-specific). |
| `trg_nntppostfiltercurrent_revision_forward` | Published revision must increase. |

`nntpusers.account_art_type` is **not** created by `postfilter.sql`. Apply [`nntpusers-account-art-type.sql`](../../schema/nntpusers-account-art-type.sql) on the existing account table (`INT UNSIGNED NOT NULL DEFAULT 65535` = `ArticleTypeCapabilities.All`). That is the AUTHINFO session capability, not a PostFilter policy ENUM.

Three related but different ArtType representations:

| Surface | Representation | Meaning |
|---------|----------------|---------|
| `nntppostfilterarttypes.art_type` | MySQL `ENUM` of public `ArticleType` names | One type per policy row (`reject` / `sa_exclude`) |
| `ArticleRecord.ArtType` | `[Flags]` classifier bitmask | What this article is |
| `nntpusers.account_art_type` | `INT UNSIGNED` of the same flags | Types the authenticated account may post/read |

Matching: policy lists trim then MD5 (UTF-8, lowercase hex). The evaluator hashes the session username **without** introducing a new trim. Case remains ordinal.

`nntppostfilterrejections` is append-oriented. NNTPD has no evidence retention sweeper; deletion is an operator decision.

Evidence writes are asynchronous (`PostFilterRejectionEvidenceQueue`, capacity 128, drop-newest-on-full). Queue-full or MySQL failure is logged and counted. It never turns 441 into 240 or the reverse.

The article payload column is `LONGBLOB` so it can hold `Nntpd:MaxArticleSize` up to the configured 100 MiB ceiling. Stored bytes are the unstuffed complete article, not an `ArticleRecord` and not a restuffed wire copy. `source_ip` is `VARBINARY(16)` after the same IPv4-mapped normalization as session identity (`::ffff:a.b.c.d` stores as IPv4). Display with `INET6_NTOA(source_ip)`.

Collection rows belong to a revision because `revision` is part of every primary key and a foreign key to `nntppostfilterpolicy.revision`. A loader that binds `@revision` from `nntppostfiltercurrent` cannot assemble accounts from revision 11 with scalars from revision 12 unless the database itself is deliberately broken (missing FK).

Historical revisions remain after publish. Rollback is **not** pointing current backward (the trigger rejects that). Rollback is inserting a **new higher** revision that copies the previous content, then publishing it.

### 6.2 Atomic update

Every publish is one transaction. Insert the new revision and **all** of its collections, then update `nntppostfiltercurrent` last.

```sql
START TRANSACTION;

SELECT revision FROM nntppostfiltercurrent WHERE policy_id = 1 FOR UPDATE;
-- @old = that value; @new = @old + 1

INSERT INTO nntppostfilterpolicy (revision, updated_utc, gate, ...) VALUES (@new, UTC_TIMESTAMP(3), ...);
INSERT INTO nntppostfilteraccounts (revision, list_kind, account_name)
  VALUES (@new, 'deny', LOWER(MD5('spammer')));
INSERT INTO nntppostfiltercidrs ...
INSERT INTO nntppostfilterarttypes ...
INSERT INTO nntppostfiltersahosts ...

UPDATE nntppostfiltercurrent SET revision = @new WHERE policy_id = 1 AND revision = @old;

COMMIT;
```

`FOR UPDATE` serializes two operators. A duplicate `@new` hits the policy primary key. An uncommitted transaction is invisible to NNTPD. `ROLLBACK` leaves the previous published revision intact.

Do **not** `UPDATE` an already-published revision in place. NNTPD ignores same-revision reloads for snapshot publication.

### 6.3 Atomic read

`MySqlNntpDbConnection.QueryPostFilterPolicyAsync` starts a `REPEATABLE READ` transaction, reads `nntppostfiltercurrent.revision`, then selects policy and collections with `WHERE revision = @revision`. Missing `policy_id = 1` fails startup. A current pointer with no policy row fails the load (FK should prevent that).

Maximum cluster propagation delay after `COMMIT` is one refresh interval: **60 seconds**.

### 6.4 Seed / what a new NntpDB must contain

Before NNTPD can start, these objects and rows must exist:

1. The six tables and the forward-revision trigger from [`docs/schema/postfilter.sql`](../../schema/postfilter.sql).
2. Policy revision `1` (Disabled, 1 day / 10 minutes, all ceilings `0`, SA disabled, no invented SPAMD host).
3. `nntppostfilterarttypes (1, 'sa_exclude', 'YEncoded')`.
4. `nntppostfiltercurrent (1, 1)`.

No host rows. `sa_on_failure` is NULL while disabled. Missing current row prevents startup. NNTPD will not invent a Disabled policy.

Inspect:

```sql
SELECT c.revision, p.gate, p.updated_utc
FROM nntppostfiltercurrent c
JOIN nntppostfilterpolicy p ON p.revision = c.revision
WHERE c.policy_id = 1;

SELECT list_kind, account_name FROM nntppostfilteraccounts WHERE revision = (
  SELECT revision FROM nntppostfiltercurrent WHERE policy_id = 1);
```

### 6.5 Operator change cases

| Change | Procedure |
|--------|-----------|
| A–F. Any scalar and/or collection change | Insert `@old+1` with the **complete** new policy (copy unchanged lists), publish current. |
| G. Transaction never committed | Invisible. Nodes keep last published revision. |
| H. `ROLLBACK` | Previous published revision remains. |
| I. Duplicate collection entry | Primary key / unique key rejects the `INSERT`. |
| J. Invalid CIDR syntax | Database accepts the string; compiler fails the refresh; last-good retained. |
| K. Invalid ArtType name | Same as J. |
| L. Publish a lower revision | Trigger rejects `UPDATE nntppostfiltercurrent`. |
| M. Two operators | `FOR UPDATE` or duplicate revision PK; one commit wins. |

When NntpDB is unavailable after a successful start: last-known-good snapshot remains. PostFilter stays enabled. There is no appsettings fallback.

### 6.6 Runtime property → column map

| Runtime | Table.column | SQL | Null | Seed | Validation |
|---------|--------------|-----|------|------|------------|
| Snapshot.Revision | `nntppostfiltercurrent.revision` / `nntppostfilterpolicy.revision` | `BIGINT UNSIGNED` | no | `1` | `>= 1`; published value must increase |
| Record.UpdatedUtc | `nntppostfilterpolicy.updated_utc` | `DATETIME(3)` | no | `UTC_TIMESTAMP(3)` | set on insert |
| Gate | `gate` | `VARCHAR(16)` | no | `Disabled` | `Disabled`/`Active`/`Closed` |
| Quota.LongWindow | `long_window_ms` | `BIGINT UNSIGNED` | no | `86400000` | `>= 1` |
| Quota.ShortWindow | `short_window_ms` | `BIGINT UNSIGNED` | no | `600000` | `>= 1` |
| Quota.MaxMessagesLong | `max_messages_long` | `BIGINT UNSIGNED` | no | `0` | `0` disables |
| Quota.MaxBytesLong | `max_bytes_long` | `BIGINT UNSIGNED` | no | `0` | `0` disables |
| Quota.MaxIdenticalLong | `max_identical_long` | `BIGINT UNSIGNED` | no | `0` | `0` disables |
| Quota.MaxMessagesShort | `max_messages_short` | `BIGINT UNSIGNED` | no | `0` | `0` disables |
| Quota.MaxBytesShort | `max_bytes_short` | `BIGINT UNSIGNED` | no | `0` | `0` disables |
| Quota.MaxIdenticalShort | `max_identical_short` | `BIGINT UNSIGNED` | no | `0` | `0` disables |
| DeniedAccounts | `nntppostfilteraccounts` `deny` | `VARCHAR(255)` | n/a | empty | unique per revision; compiler rejects blank/duplicate |
| AllowlistedAccounts | `nntppostfilteraccounts` `allow` | `VARCHAR(255)` | n/a | empty | same |
| DeniedCidrs | `nntppostfiltercidrs` `deny` | `VARCHAR(64)` | n/a | empty | unique; compiler parses CIDR |
| AllowlistedCidrs | `nntppostfiltercidrs` `allow` | `VARCHAR(64)` | n/a | empty | same |
| RejectArtTypes | `nntppostfilterarttypes` `reject` | `VARCHAR(32)` | n/a | empty | compiler `ArticleType` |
| SA.Enabled | `sa_enabled` | `CHAR(1)` | no | `N` | `Y`/`N` |
| SA.OnFailure | `sa_on_failure` | `VARCHAR(16)` | yes when disabled | `NULL` | required `Reject`/`Accept` when `Y` |
| SA.MaxArticleSize | `sa_max_article_size` | `INT` | no | `131072` | `>= 0`; `0` disables size gate |
| SA.ExcludeArtTypes | `nntppostfilterarttypes` `sa_exclude` | `VARCHAR(32)` | n/a | `YEncoded` | compiler `ArticleType` |
| SA.Hosts | `nntppostfiltersahosts.host` | `VARCHAR(255)` | n/a | empty | unique host/order; required when SA enabled |
| SA.Port | `sa_port` | `INT UNSIGNED` | no | `783` | `1–65535` |
| SA.ProtocolVersion | `sa_protocol_version` | `VARCHAR(16)` | no | `1.5` | required when SA enabled |
| SA.MaxConnections | `sa_max_connections` | `INT UNSIGNED` | no | `4` | `1–32` |
| SA.HostSelection | `sa_host_selection` | `VARCHAR(16)` | no | `RoundRobin` | `RoundRobin`/`Failover` |
| SA.ConnectTimeout | `sa_connect_timeout_ms` | `INT UNSIGNED` | no | `5000` | `>= 1` and `<=` operation |
| SA.OperationTimeout | `sa_operation_timeout_ms` | `INT UNSIGNED` | no | `30000` | `>= 1`; compiler `1s–2m` when SA enabled |

Reservation TTL is compiled, not stored. `ServerId` and `Redis:*` are node-local infrastructure, not PostFilter policy.

### 6.7 Former `Nntpd:PostFilter` mapping

| Former key | NntpDB |
|------------|--------|
| `Gate` | `nntppostfilterpolicy.gate` |
| `DeniedAccounts` / `AllowlistedAccounts` | `nntppostfilteraccounts` (`deny` / `allow`) |
| `DeniedCidrs` / `AllowlistedCidrs` | `nntppostfiltercidrs` (`deny` / `allow`) |
| `RejectArtTypes` | `nntppostfilterarttypes` (`reject`) |
| `Quota:LongWindow` / `ShortWindow` | `long_window_ms` / `short_window_ms` |
| `Quota:MaxMessages*` / `MaxBytes*` / `MaxIdentical*` | matching columns |
| `SpamAssassin:Enabled` | `sa_enabled` (`Y`/`N`) |
| `SpamAssassin:OnFailure` | `sa_on_failure` (`Reject`/`Accept`, NULL when disabled) |
| `SpamAssassin:MaxArticleSize` | `sa_max_article_size` (exclusive; `0` disables) |
| `SpamAssassin:ExcludeArtTypes` | `nntppostfilterarttypes` (`sa_exclude`) |
| `SpamAssassin:Hosts` | `nntppostfiltersahosts` (`host_order`, `host`) |
| `SpamAssassin:Port` / `ProtocolVersion` / `MaxConnections` / `HostSelection` | `sa_port`, `sa_protocol_version`, `sa_max_connections`, `sa_host_selection` |
| `SpamAssassin:ConnectTimeout` / `OperationTimeout` | `sa_connect_timeout_ms` / `sa_operation_timeout_ms` |

Do not automatically copy leftover appsettings values. Operators apply the seed, then edit NntpDB.

SPAMD endpoints are **cluster policy** (same Hosts/Port on every node). Reservation identity still uses node-local `ServerId` (already NNTPD identity, not PostFilter policy). Redis connection settings remain the top-level `Redis` section.

`ArticleType` names: `Default`, `Control`, `Cancel`, `Mime`, `Binary`, `UuEncode`, `Base64`, `YEncoded`, `BommaNews`, `UniData`, `Multipart`, `Html`, `PostScript`, `BinHex`, `Partial`, `PgpMessage`.

Account names compare **ordinal** after trim. Empty or duplicate collection rows fail compile. Ceiling `0` disables that quota dimension. Reservation TTL is not a database column (see Redis).

---

## 7. Policy semantics

Compiled snapshot fields include gate, deny/allow sets, reject ArtType mask, quota windows/ceilings, SpamAssassin enablement, `OnFailure`, eligibility, hosts, port, protocol, pool size, host selection, timeouts, and reservation hold.

Evaluation order when `Gate` is `Active`:

1. Deny (account or CIDR) → `441`, no Redis.
2. `RejectArtTypes` if any listed flag is set on `ArtType` → `441`, no Redis.
3. Missing AUTH username → `441` (`unauthenticated`), no Redis.
4. Redis `RESERVE` (fail-closed on Unavailable).
5. SpamAssassin CHECK only when enabled, not allowlisted, and `ShouldScan` is true.
6. Accept with a live lease.

Allowlist skips step 5 only.

---

## 8. Redis

Redis holds **distributed runtime quota state**, not policy. Policy is the local immutable snapshot. The same account on every NNTPD node shares the same Redis hashes.

| Item | Current behaviour |
|------|-------------------|
| Identity | Authenticated AUTH username (UTF-8 SHA-256, lowercase hex). Not client IP. |
| Quota HASH | `nntpd:pf:q:{sha256hex}` — committed counters + live reservations |
| Multipost HASH | `nntpd:pf:m:{sha256hex}` — committed identical-body counts |
| Reservation field | `r:{nodeId}:{incarnation}:{generation}` |
| Node id | `nntpd{ServerId:00}` host label (same pattern as session ownership) |
| Model | Two fixed-epoch windows (L + S). Bucket id = `nowMs / windowMs`. |
| Hold / crash TTL | Floor **10s**. When SpamAssassin is enabled: `max(10s, OperationTimeout + 1s)`. Not a v1 operator knob. |
| Idle key TTL | `max(L,S) + hold + 1s` — garbage collection only; does not change bucket maths. |

`RESERVE` is atomic Lua EVAL. A deny writes **nothing**. A live reservation counts toward the ceiling immediately. `COMMIT` moves reserved units into the current buckets and deletes the reservation. `RELEASE` deletes a matching live reservation and **never** decrements committed usage.

These conservative outcomes are intentional:

| Condition | Client | Queue | Quota |
|-----------|--------|-------|-------|
| `RESERVE` Unavailable | `441` | not queued | no reservation |
| `COMMIT` Unavailable after `TryAdmit` | `240` | article remains queued | reservation **not** released; committed usage may temporarily under-count |
| `RELEASE` Unavailable | reject/cancel path already decided | not queued (if reject) | reservation may remain until hold TTL expires |
| Process crash after `RESERVE`, before `COMMIT`/`RELEASE` | no `240` | not admitted | reservation expires at hold TTL |

Redis itself remains a required NNTPD dependency (startup connect/PING). PostFilter adds the keys above on the same multiplexer. Redis binding lives in upstream VectorNNTP `docs/configuration.md` (not migrated). VectorNNTPD BackFiller [configuration.md](../../configuration.md) has no Redis section.

---

## 9. Quotas

Dimensions (each independently disableable with ceiling `0`):

| Dimension | What is counted |
|-----------|-----------------|
| Messages | `1` per reserved POST |
| Bytes | `ArticleRecord.ArtSize` |
| Identical-message / multipost | `1` when a body identity is computed |

Identical-message identity is **XXH3-64 of the canonical body octets after the first `CRLF CRLF`**, lowercase hex. It is **not** `ArticleRecord.ArtHash`. Articles whose `ArtType` includes `YEncoded`, `Binary`, `UuEncode`, `Base64`, or `BinHex` do not participate (no multipost units).

Accounts are isolated: the same body on two usernames does not share a ceiling. Cross-node POSTs for one username share Redis state.

Deterministic reserve outcomes (log `reason`, never on the wire):

| Status | Meaning |
|--------|---------|
| `Accepted` | Live reservation counts toward the ceiling |
| `DeniedMessagesLong` / `DeniedMessagesShort` | Message ceiling |
| `DeniedBytesLong` / `DeniedBytesShort` | Byte ceiling |
| `DeniedIdenticalLong` / `DeniedIdenticalShort` | Identical-body ceiling |
| `Conflict` | Same token, different payload (should not occur in production) |
| `Unavailable` | Redis/EVAL unavailable — fail closed |

---

## 10. SpamAssassin

SPAMD does **not** receive `ArticleRecord` / `ArtData` directly.

```text
ArticleRecord (read-only)
  → SpamdScanArticleBuilder (disposable buffer)
  → SPAMD CHECK SPAMC/1.5
```

`ArtData` is not mutated. The scan buffer is independent and discarded after CHECK.

### 10.1 Scan representation (current builder only)

Synthetic headers prepended:

- `Received: from [<client-ip>] by <server-fqdn> with NNTP [id <Message-ID>]; <date>`
- `To: usenet@<server-fqdn>`
- `X-Usenet-Newsgroups: <Newsgroups>` when present
- `Date:` only when the original article has no Date header

Stripped (not copied into the scan): `Path`, `Xref`, `Injection-Info`, `X-Trace`, `X-Complaints-To`, `NNTP-Posting-Host`.

All other original header blocks are copied. The body after the first `CRLF CRLF` is copied verbatim. A missing/unsafe FQDN becomes `localhost`.

### 10.2 Transport

- Protocol: SPAMC/SPAMD `CHECK` with configured `ProtocolVersion` (default `1.5`).
- Persistent connection pool sized by `MaxConnections`.
- Each connection **serializes** CHECKs. No pipelining. No multiplexing (SPAMD has no request IDs).
- `RoundRobin`: rotate start host. `Failover`: always start at `Hosts[0]`.
- Connect-time failures fail over to the next host.
- After the request is written, the CHECK is **not** retried on another host.
- Stale idle socket (`closed` / `connection` on a reused connection): retry **once on the same host**.
- Bytes after the response `\r\n\r\n` are `Failed leftover`; the connection is evicted.

### 10.3 Eligibility (`ShouldScan`)

CHECK runs only when all of the following are true:

- `Gate` is `Active` (or the filter otherwise reached the SA stage)
- `SpamAssassin:Enabled` is true
- the session is not allowlisted
- `MaxArticleSize` is `0` **or** `ArtSize < MaxArticleSize` (exclusive)
- `ArtType` has no bits in `ExcludeArtTypes`

A skip does **not** contact SPAMD and is **not** an `OnFailure` event. Quota reservation already exists and the rest of POST continues.

Default eligibility matches the historical small-article boundary: `ArtSize < 131072` and not `YEncoded`. A 768 KiB article is not sent to SPAMD under that default.

### 10.4 Results

| Scanner outcome | Filter | Reservation |
|-----------------|--------|-------------|
| HAM (`Spam: False`) | Accept | keep lease |
| SPAM (`Spam: True`) | `441` | RELEASE |
| Failed + `OnFailure=Reject` | `441` | RELEASE |
| Failed + `OnFailure=Accept` | Accept | keep lease |
| Caller cancellation | throw | RELEASE (`CancellationToken.None`) |

---

## 11. Failure matrix

Client rejects after `340` are `441 Posting failed`. Success is `240 Article received OK`.

| Condition | Redis | SPAMD | Queue | Client | Result |
|-----------|-------|-------|-------|--------|--------|
| `Gate=Disabled` | none | none | admitted | `240` | Normal POST; no reserve |
| `Gate=Closed` | none | none | no | `441` | No reserve, no CHECK |
| CIDR / account deny | none | none | no | `441` | Before quota |
| ArtType deny | none | none | no | `441` | Before quota |
| Unauthenticated + `Active` | none | none | no | `441` | `reason=unauthenticated` |
| Quota denial | deny writes nothing | none | no | `441` | Live reservations of others still count |
| SPAMD spam | RELEASE | CHECK ran | no | `441` | Not remembered |
| SPAMD failure `Reject` | RELEASE | failed | no | `441` | Not an eligibility skip |
| SPAMD failure `Accept` | COMMIT on admit | failed | admitted | `240` | Reservation kept |
| Redis unavailable at `RESERVE` | fail-closed | none | no | `441` | Intentional |
| `TryAdmit` failure | RELEASE | already done | no | `441` | Article not queued |
| `COMMIT` unavailable | reservation left | — | **admitted** | `240` | Under-count until TTL; not a rollback |
| Successful POST | COMMIT | HAM or skip | admitted | `240` | Remember |
| Cancel after `RESERVE` | RELEASE (`None`) | — | no | no completion | No `240` |

Eligibility skips (size / excluded ArtType / allowlist / SA disabled) are **not** dependency failures: SPAMD is never contacted.

---

## 12. Observability

Filter reasons stay off the wire. Use logs.

| EventId | Level | When |
|---------|-------|------|
| 2800 | Information | Reject (`stage`, `reason`, `account`, `artId`) |
| 2801 | Debug | Accept (`Gate` disabled or complete; `reserved`) |
| 2802 | Warning | `COMMIT` NOOP after successful admit |
| 2803 | Warning | `COMMIT` unavailable after admit (no RELEASE) |
| 2804 | Debug | `RELEASE` status |
| 2805 | Warning | Policy refresh failed; last-good retained |
| 2806 | Warning | SpamAssassin failure (`action`, `detail`) |
| 2807 | Information | SPAMD transport stopped (connects/checks/reuses/reconnects/evictions) |
| 2808 | Information | Policy published (`revision`, gate, SA flags, hosts, pool) |
| 2809 | Error | Initial NntpDB policy load failed |
| 2810 | Information | Policy revision changed (`from` → `to`) |
| 2811 | Error | Policy repository failed |

`artId` in these logs is `ArticleRecord.ArtHash` hex, not the identical-body XXH3-64.

In-process counters (not a metrics HTTP endpoint):

- `PostFilterMetrics.CommitNoop` / `CommitUnavailable` — incremented only after successful `TryAdmit`
- `SpamdTransportMetrics` — summarized at transport stop (EventId 2807)

**Current limitations (do not add noisy logging solely to close these):**

- EventIds 2800–2808 **overlap** RabbitMQ lifecycle EventIds. Filter on message text (`PostFilter …`) or logger category, not EventId alone.
- Successful 60s refresh of an unchanged published revision is not logged and does not replace the snapshot.
- `RESERVE` Unavailable appears as EventId 2800 `stage=Quota reason=Unavailable`, not a dedicated Redis-down event.

POST also emits existing `PostLogMessages` Accepted/Rejected lines (`441` category `PolicyRejected` plus the internal reason).

---

## 13. Safe deployment / activation

1. Apply [`docs/schema/postfilter.sql`](../../schema/postfilter.sql) (tables, trigger, Disabled seed). Do not add `Nntpd:PostFilter` to appsettings.
2. Confirm `nntppostfiltercurrent.policy_id = 1` exists. Initial load is required for RUNNING.
3. Confirm Redis (`Redis:Host` / startup PING). PostFilter uses the same multiplexer for quota state.
4. If SpamAssassin will be used, confirm SPAMD from every NNTPD host, then publish a new revision with `sa_enabled='Y'` and host rows.
5. Change policy only by inserting a complete new revision and updating `nntppostfiltercurrent` in one transaction. Keep `gate='Disabled'` while rehearsing.
6. Start with **conservative** ceilings (or `0` to disable a dimension). There is no universal production number.
7. Watch EventId 2808 (`revision=`) on each node after a new publish. Failed compile/refresh keeps last-good (2805 / 2809).
8. Publish `gate='Active'` as a new revision. Within 60s both nodes should log 2810 and the new revision.
9. POST a known HAM from an authenticated account that is not denied. Expect `240` and a Redis COMMIT.
10. POST enough volume to hit a test ceiling. Expect `441` and EventId 2800 `stage=Quota`.
11. Confirm clients see only `240` / `441`. Confirm both nodes report the same revision.
12. Inspect Redis keys `nntpd:pf:q:*` / `nntpd:pf:m:*` (runtime state, not policy).

To disable quickly: insert a new revision that copies the current scalars with `gate='Disabled'` (or `Closed`), publish `nntppostfiltercurrent`, and wait up to 60s. Do not `UPDATE` the live revision in place.

---

## 14. Troubleshooting

| Symptom | Distinguish | What to check |
|---------|-------------|---------------|
| Every POST is `441` | Closed gate vs deny vs quota vs SA vs receive/parse | EventId 2800 `stage`/`reason`. `Closed` / `denied` / `arttype` do not touch Redis. `Nntpd:MaxArticleSize` and catalogue policy reject **before** PostFilter (POST rejected logs, no 2800). |
| Redis unavailable | `reason=Unavailable` at Quota | Redis process, `Redis:Host`, multiplexer health. Fail-closed is expected. |
| Quota rejects unexpectedly | Ceiling includes **live reservations** | Other in-flight POSTs, short window still open, shared cluster state, case-sensitive account names, `ArtSize` vs assumed size. |
| SPAMD unavailable | EventId 2806 `action=Reject` or `Accept` | Hosts/port/firewall. `OnFailure=Accept` still yields `240`. |
| SPAMD rejects HAM | `reason=spam` | Disposable scan headers (Received/To/X-Usenet-Newsgroups). Tune SA rules, not ArticleRecord. |
| SPAMD never contacted | Eligibility skip vs Disabled vs allowlist vs Closed | Debug 2801 `reserved=false` (Disabled). Size: `ArtSize >= MaxArticleSize`. `ExcludeArtTypes`. Allowlist. `Enabled=false`. |
| Large articles skip SA | Exclusive size gate | Default `131072`. `0` disables the size gate. |
| yEnc / listed ArtType skip SA | Default `ExcludeArtTypes=["YEncoded"]` | Empty array disables type exclusion. |
| Allowlisted account still quota-denied | Allowlist skips SA only | Deny/ArtType/quotas still apply. |
| `240` but COMMIT warning | EventId 2802 / 2803 | Article is queued. Do not RELEASE. Committed count may lag until TTL. |
| Reservation fields linger | RELEASE Unavailable or crash | Hold TTL (10s, or `OperationTimeout+1s` when SA is enabled). Expected. |

---

## 15. Current limitations

Absent from this implementation (not implied as a roadmap):

- DNSBL, URIBL, TOR, rDNS / domain identity
- Dictionary / bad-word / regex filtering
- Reject-streak logic
- Anonymous / IP quotas (quota identity is AUTH username only)
- Moderation as a PostFilter outcome (`Moderate` is not implemented; catalogue `m` submission is a separate path)
- Header/body mutation of the canonical article
- Generic filter framework / plugin stages beyond gate, deny, ArtType, quota, and SpamAssassin
- Per-POST metrics export or a dedicated Redis-down EventId
- Configurable reservation TTL
- IHAVE ArtType capability enforcement (classification is after the `235` boundary)
- ARTICLE/HEAD/BODY/STAT ArtType capability enforcement (no article repository exposes persisted `ArticleType`)

---

## 16. Test / validation evidence

POST-boundary coverage lives in `PostFilterProductionPolicyMatrixTests` (test-local snapshots; production `Gate` stays `Disabled`):

- Gate `Disabled` / `Closed`
- Account and CIDR deny; ArtType deny; allowlist (SA skip only)
- Message, byte, and identical-body quotas
- Account isolation; shared in-memory quota across two reservation identities
- SPAMD HAM / SPAM; size and ArtType skip; allowlisted skip
- `OnFailure=Reject` / `OnFailure=Accept`
- Redis unavailable at RESERVE; `TryAdmit` failure; cancel after RESERVE
- COMMIT unavailable after admit (`240`, queued, no RELEASE)
- Complete reserve → scan → admit → COMMIT → Remember
- `ArticleRecord` immutability and disposable SPAMD representation

Additional suites: `PostFilterEvaluatorTests`, `PostFilterPostCommandTests`, quota Lua/engine/live-cluster tests, SPAMD transport/eligibility/builder tests.

Ordinary CI talks to a loopback SPAMC listener (`LocalSpamdServer`) through `SpamdCheckClient`. It does not dial a SpamAssassin host. Compatibility with a real `spamd` is opt-in: set `VECTORNNTP_SPAMD_INTEGRATION=host:port` before running `PostFilterSpamdLiveTests` methods marked `SpamdIntegrationFact`. When the variable is absent those tests are skipped. When it is present they use that endpoint only and fail if it is unreachable. There is no built-in address.

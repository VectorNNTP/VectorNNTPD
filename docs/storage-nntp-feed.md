# StorageService NNTP feed protocol

Wire behaviour implemented by `VectorNNTPD.StorageService.Listener.StorageNntpFeedSession`
(and TLS accept path in `StorageNntpListenerService`). This is **VectorNNTPD StorageService**
behaviour only. It is not a full NNTP server profile and not upstream `VectorNNTP.NNTPD`.

Standards text for general NNTP framing: [standards/rfcs/](standards/rfcs/)
(RFC 3977 article/command framing; RFC 4644 streaming/`TAKETHIS` context). Where this host
diverges, the **code wins**.

Architecture and configuration: [storage.md](storage.md).  
Do not use [reference/upstream-nntpd/commands.md](reference/upstream-nntpd/commands.md) as a
capability list for this host.

## Scope

| Supported | Not supported (receive `500 Unknown command` or no implementation) |
|-----------|---------------------------------------------------------------------|
| Greeting, then `ARTICLE`, `TAKETHIS`, `QUIT` | `MODE STREAM` / `MODE READER`, `CAPABILITIES`, `AUTHINFO`, `IHAVE`, `CHECK`, `POST`, `LIST`, group navigation, compression, STARTTLS (listener is already TLS) |

No mode negotiation is required before `ARTICLE` or `TAKETHIS`. There is no cleartext listener;
TLS 1.2/1.3 is established before the session runs.

## Connection lifecycle

1. TCP accept → TLS server handshake (timeout: `Listener:TlsHandshakeTimeoutSeconds`, default 30).
2. Session writes greeting: `200 VectorNNTP StorageServer ready` + CRLF.  
   (Code uses status **200**, not `201`. Text is fixed; not an RFC capabilities advertisement.)
3. Command loop: read one command line (max **512** octets including CRLF). Overlong → `501 Syntax error`, connection stays open.
4. Keywords are ASCII case-insensitive. Arguments: Message-ID must pass Common
   `NntpMessageIdValidation` (RFC 5536-oriented grammar used by this codebase).
5. Session ends on: successful `QUIT`, peer disconnect, cancellation, or handlers that return
   “close” after writing `400 Temporary failure` (see TAKETHIS).

Per-connection I/O uses `IoProgressTimeoutSeconds` (default 60). While TAKETHIS waits for queue
capacity, read-progress timing is suspended, then armed again. Overall TAKETHIS body receive uses
`ArticleReceiveTimeoutSeconds` (default 300), which does **not** reset on drip bytes.

Path identity for article parse/normalize is the runtime FQDN (`cache{ServerId:00}.{DnsSuffix}`).

## QUIT

| Request | Response | Session |
|---------|----------|---------|
| `QUIT` with no argument | `205 Connection closing` | Closes after write |
| `QUIT` with any argument | `501 Syntax error` | Stays open |

## ARTICLE

Serves an indexed, **published** CanonicalV1 spool file for `ARTICLE <message-id>`.

| Condition | Response |
|-----------|----------|
| Missing / invalid Message-ID / extra tokens | `501 Syntax error` |
| Not in index / path resolve empty / file or directory not found | `430 No Such Article Found` |
| I/O / access / truncated read while loading file | `400 Temporary failure` |
| Empty payload, size &gt; 5 MiB (`ArticleResourceLimits.MaxArticleBytes`, enforced on file length / buffer before `220`), or not frameable | `403 Article not available` |
| Success | `220 0 <message-id> article follows` + CRLF, article bytes (header lines beginning with `.` may be stuffed for the socket), then `.\r\n` |

Important implementation details:

- A read lease is taken before path resolve so eviction cannot delete between lookup and open.
- The full article is read and framing analyzed **before** any `220` is written, so a read failure
  never injects a second status into a partial body.
- Write failure after `220` propagates and ends the session without another NNTP status.
- Temporary `.writing-*` files are never opened for retrieval.
- Best-effort last-access touch after a successful write does not change the already-sent `220`.

`ARTICLE` with no Message-ID (or an article-number-only form) is not implemented as group-current
article semantics; invalid/empty arguments yield `501`.

## TAKETHIS

Streaming-style transfer: command line carries the Message-ID; body follows as a multiline
article ending in `.\r\n` (dot-stuffing undone while reading).

On many reject paths the body is **drained** (discarded) before the status is written so the next
command is not contaminated.

### Success

1. Destuff and assemble body (overall receive timeout; size capped by
   `ArticleResourceLimits.MaxArticleBytes` = **5 MiB** during receive/drain and again at
   `ArticleRecordFactory` / parse).
2. Reject if already present in the article index (`439`).
3. `ArticleRecordFactory.TryCreate` with path normalize; reject if not accepted or ArtId ≠ request Message-ID (`439`).
4. Reserve/commit into `IAcceptedArticleQueue` (may wait; I/O progress suspended during wait).
5. On successful commit, write **`239 <message-id>`** + CRLF.

`239` means the article entered the **accepted queue**, not that spool write, SQLite index update,
NVMe/SATA flush, or any replication finished. See [storage.md](storage.md) durability note.

### Rejection and temporary failure

| Condition | Typical response | Connection |
|-----------|------------------|------------|
| Invalid Message-ID on command (body drained when applicable) | `501 Syntax error` | Open |
| Capacity gate denies admission (critical/unknown) | `439 <message-id>` | Open |
| No assembly admission permit | `439 <message-id>` | Open |
| Duplicate ArtId already indexed | `439 <message-id>` | Open |
| Parse/materialize not accepted | `439 <message-id>` | Open |
| Message-ID ≠ canonical ArtId | `439 <message-id>` | Open |
| Article larger than queue `MaxQueuedBytes` | `439 <message-id>` | Open |
| Queue reserve fails / completed queue | `439` or `400 Temporary failure` | `400` paths close |
| Commit fails; receive timeout; oversized/unrecoverable framing (`InvalidOperationException`) | `400 Temporary failure` | **Closes** (`HandleTakeThisAsync` returns false) |

`439` status lines include the request Message-ID (same scratch format as `239`). This host uses
`439` broadly for “not accepted into the queue” conditions above; it does not implement a separate
RFC 4644 `CHECK`/`TAKETHIS` negotiation dialogue beyond this command.

Unknown verbs → `500 Unknown command` (connection stays open).

## Differences from “full” NNTP expectations

- Greeting `200` with Storage-specific text; no capability advertisement.
- No authentication; TLS is the only access control at this layer.
- No `MODE STREAM` requirement despite offering `TAKETHIS`.
- Cache-style `ARTICLE` by Message-ID from local spool/index, not newsgroup article numbers.
- `239` is queue admission, not proven durable store.
- Upstream NNTPD command inventories and PostFilter/Redis behaviour do not apply here.

## Evidence pointers

- Session commands and status bytes: `StorageNntpFeedSession.cs`
- TLS listener / FQDN path identity: `StorageNntpListenerService.cs`
- Size ceiling: `VectorNNTPD.Common.Articles.ArticleResourceLimits.MaxArticleBytes`
- Queue / persistence semantics: `SpoolPersistenceOptions` remarks; `AcceptedArticleQueue`

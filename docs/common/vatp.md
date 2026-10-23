# Vector Article Transfer Protocol (VATP)

Status: Phase 1 Common foundation, Phase 2 BackFiller VATP **server**,
Phase 3A NNTPD VATP **client** + RabbitMQ Success `articleId`, and
Phase 3B message-id **ARTICLE/HEAD/BODY/STAT** wiring are implemented.
Numeric article lookup / OverviewDB retrieval remain deferred.

Owner: `VectorNNTPD.Common` (`VectorNNTPD.Common.Transport.ArticleTransfer`).
BackFiller adapter: `VectorNNTPD.BackFillerService.Listener.VatpListenerSession` on the
existing TLS `CacheListenerService` (`BackFiller:BindPortTls`).
NNTPD adapter: `VectorNNTP.NNTPD.Transport.Vatp` (`IVatpArticleClient`,
connection pool). Common does not reference application hosts.

## Purpose

VATP is a byte-oriented, multiplexed TCP framing protocol for transferring one
CanonicalV1 `ArticleRecord` per stream. RabbitMQ remains the control plane.
Article bytes do not travel over RabbitMQ, JSON, XML, or protobuf.

### Control plane vs data plane

| Plane | Transport | Carries |
|-------|-----------|---------|
| Control | RabbitMQ ArticleWork RPC | RequestId, Message-ID, backbone, outcome, `fqdn`, `vatpPort`, `articleId` |
| Data | VATP over TLS TCP | META + ArtData (`ArticleRecord`) |

Success JSON carries the BackFiller FQDN, the TLS VATP listen port, and the
64-character lowercase hexadecimal `ArticleId`. Those fields are routing
metadata. VATP OPEN uses **RequestId + ArticleId** together.

## Byte order and header

All multi-byte integers are **big-endian**. Fixed header is **16 bytes**:

| Offset | Size | Field |
|-------:|-----:|-------|
| 0 | 1 | Version (`0x01`) |
| 1 | 1 | Type (`VatpFrameType`) |
| 2 | 2 | HeaderLength (must be 16) |
| 4 | 4 | StreamId (`0` = connection-level) |
| 8 | 4 | PayloadLength (this frame only) |
| 12 | 4 | Flags (bit 0 = FIN on DATA; bits 1–31 MBZ) |

## Frame types

| Value | Name | StreamId | Payload |
|------:|------|----------|---------|
| `0x00` | HELLO | 0 | magic `VNATP01\0` + `u32` maxFramePayload |
| `0x01` | OPEN | ≠0 | 16-byte RequestId GUID + 32-byte `ArticleId` |
| `0x02` | META | ≠0 | 76-byte META |
| `0x03` | DATA | ≠0 | ArtData chunk; Flags.FIN on last |
| `0x04` | END | ≠0 | empty |
| `0x05` | FAIL | 0 or ≠0 | `u16` error + optional ASCII reason ≤ 256 |
| `0x06` | CANCEL | ≠0 | empty |
| `0x07` | WINDOW | ≠0 | `u32` add-credit |

Unknown types, reserved flags, wrong StreamId for the type, and payloads larger
than the HELLO-negotiated max are rejected.

## HELLO

After TLS, peers exchange HELLO on StreamId 0. There is no version negotiation
framework beyond Version = 1 and exact magic match.

**Direction (BackFiller server):** the client sends HELLO first; the server
validates magic / maxFramePayload / StreamId 0, then replies with HELLO using
`min(clientMax, serverDefault)`. OPEN is rejected until the client HELLO is
accepted. Duplicate or malformed HELLO terminates the connection.

Default advertised `maxFramePayload` is **64 KiB** (`VatpProtocol.DefaultMaxFramePayload`).
That default is also the sensible DATA payload policy default; hosts may advertise a
different value within protocol rules.

## META (76 bytes)

| Offset | Size | Field |
|-------:|-----:|-------|
| 0 | 8 | ArtHash (XXH3-64 of ArtData) |
| 8 | 4 | ArtLines |
| 12 | 4 | ArtSize |
| 16 | 1 | SelectedDateHdr (`NntpArticleHeaderName` Date-family) |
| 17 | 3 | Pad (MBZ) |
| 20 | 56 | FieldTable — 7 × (`i32` offset, `i32` length) |

Not on the wire: ArtId, ArtType, CanonicalUtc, ParseStatus, header/body split.

## ArticleId vs ArtHash

- `ArticleId` = BLAKE3(Message-ID value bytes). Carried on OPEN; recomputed from ArtData.
- `ArtHash` = XxHash3-64(ArtData). Carried on META; recomputed from ArtData.
- Success `articleId` = lowercase hexadecimal `ArticleId`. Success `fqdn` and `vatpPort`
  are the TLS dial target. None of those JSON fields is a VATP lookup key.

## Canonical transfer factory

```text
ArticleRecordFactory.TryCreateFromCanonicalTransfer(artData, meta, expectedArtId)
```

Validates size, ranges, RFC 5536 Message-ID syntax, ArtId binding, ArtHash, Locate equality, Date → CanonicalUtc,
and ArtLines sanity; classifies ArtType cheaply; assigns `ParseStatus = CanonicalV1`.
Does **not** destuff, parse, rematerialize Path, or recount body lines.

There is no trusted `ArticleRecord.FromCanonicalTransfer` constructor.

## Transfer state machine (receiver)

```text
OPEN accepted
  → AwaitingMeta
  → META → ReceivingData
  → DATA* (exact ArtSize) with FIN on the final DATA frame
  → AwaitingEnd
  → END
  → TryCreateFromCanonicalTransfer
  → Completed (consumable) | Failed
```

**Completion contract:**

- `FIN` marks the final DATA frame (exact `ArtSize` must already be assembled).
- `END` confirms transfer completion.
- An `ArticleRecord` becomes consumable only after ArtSize bytes + FIN + END and
  successful canonical validation.
- Neither FIN nor END alone exposes an article.

Invalid sequences (DATA before META, duplicate META, DATA after FIN, END before
ArtSize+FIN, duplicate END, etc.) are deterministic protocol errors.

## Flow control

`ArticleTransferWindow` tracks per-stream credit. Initial credit defaults to **1 MiB**
(`ArticleTransferLimits.Default`) — policy, not a wire constant. WINDOW adds credit (saturating).
Insufficient credit is a flow-control violation. Zero credit pauses only that stream.
HELLO does not carry the initial window, so NNTPD, BackFiller, and StorageServer all use this
same default. A sender that assumes a larger window than the receiver granted fails flow control.
The 64 MiB maximum credit is unchanged. 1 MiB is the production default because a 768 KiB article
fits in it, and measured 2 MiB and 4 MiB windows did not improve on 1 MiB.

The initial window is **outstanding-byte credit**, not an article-size ceiling. Receivers
consume credit on accepted DATA and replenish the same amount locally when advertising
outbound WINDOW so transfers larger than the initial window can complete under pacing.

StorageServer STORE is the exception to the assumed 1 MiB send credit. A placement
client starts that stream at zero credit and does not send DATA until the server
sends WINDOW. After META, the server reserves `ArtSize` bytes in the 1 GiB accept
queue. It sends the initial WINDOW only after that reservation exists, and it does
not allocate the article buffer before then. A full queue leaves the client waiting
on WINDOW; it is not `FAIL/IncompleteTransfer` after the body. That silent wait is queue
admission, not a stalled read. StorageServer suspends its I/O no-progress timeout until
the wait ends, then arms it again so a peer that receives WINDOW and sends nothing still
times out. The placement client does the same with its per-read I/O timeout until the
first WINDOW for that STORE. An article larger
than the whole queue budget is `RESULT` outcome `RejectedCapacity` (`6`) and no
body is authorized. Retrieval OPEN is unchanged and still assumes the 1 MiB initial
window. A placement client and StorageServer that disagree about this STORE credit
deadlock, so those two ship together.

Queue admission is strict FIFO. A waiting STORE whose `ArtSize` does not fit
blocks every later STORE, including a smaller one that would fit in the bytes
free right now. The head waits until a dequeue frees enough for it, or until
that waiter is cancelled. Cancellation admits the next waiter that fits and
does not leave its reservation behind. A live head is not skipped, so later
small articles cannot pass a larger one that arrived first. That wait is the
time required to free the head's bytes. It is not an indefinite skip of the
head, and a new STORE cannot reserve while any waiter is queued.

WINDOW on an unknown or terminal stream is `UnknownStream`.

## Fairness (scheduling model)

One write pump per connection (adapter-owned; not implemented in Phase 1).
`ArticleTransferReadyRing` documents and provides the ready-ring primitive:

- round-robin among write-ready streams
- at most one DATA frame per turn
- size = min(remaining, credit, maxFramePayload)
- zero-credit streams skipped

## Protocol invariants vs resource policy

**Invariants (Common):** header size, byte order, frame types, StreamId 0 rules,
HELLO magic, META layout, article ceiling `ArticleResourceLimits.MaxArticleBytes`,
malformed-frame rejection.

**Policy (`ArticleTransferLimits`):** max streams, initial window, max credit,
default maxFramePayload. Not frozen as immutable protocol constants.

## Multiplexing primitives

`ArticleTransferStreamTable` bounds active StreamIds, rejects duplicates and StreamId 0,
and requires removal after terminalization before reuse.

NNTPD retrieval already opens many OPEN streams on one pooled TLS connection, up to
`MaxStreamsPerConnection` (64). BackFiller schedules their DATA frames one at a time on
a ready ring. StorageService article *ingestion* is NNTP RFC 4644 `TAKETHIS` (not VATP STORE);
the Common advertisement field `vatpPort` still names the TLS feed listen port for placement.
Retrieval OPEN multiplexing is unchanged. VATP STORE multiplexing language that formerly
described StorageServer ingestion no longer applies to this host.

## Not implemented yet (later phases)

- Numeric ARTICLE/HEAD/BODY/STAT (article-number / current-article forms)
- OverviewDB article-number resolution
- Outbound VATP server-to-server transfers

## NNTP message-id retrieval (Phase 3B)

```text
ARTICLE|HEAD|BODY|STAT <message-id>
        ↓
ArticleWork RPC (control plane)
        ↓ Success → URI + RequestId + ArticleId
IVatpArticleClient.FetchArticleAsync
        ↓ ArtSize + FIN + END + CanonicalV1
ArticleRecord
        ├── NNTP 220/221/222/223 response (writer + restuff)
        └── TryAdmit(InboundArticleProducer.BackFiller)  [best-effort]
```

Serving the requesting client does **not** depend on local ingest admission.
Queue full / unavailable still returns the successful NNTP response.
History `Remember` runs only when `TryAdmit` accepts, using the existing
ingestion Remember path (HistoryWriteService remains the Redis drain).

ArticleWork not-found / invalid → `430`. VATP connection/remote/incomplete
failures → `400 Service temporarily unavailable` (connection left open).
Fetched Message-ID / ArticleId mismatch → `430` (never served).

## BackFiller Phase 2 server

`CacheListenerService` is the single TLS listener on `BindPortTls`. After the
handshake it runs `VatpListenerSession` only — VATP is the sole article data-plane
protocol. A non-VATP first frame is handled with VATP FAIL / connection-close
semantics; there is no legacy MD5 cache-transfer fallback.

Canonical articles enter retention via `IArticleRetentionAuthority.RetainCanonical`
(RequestId + `ArticleRecord`). The sole retained byte representation is
`ArticleRecord.ArtData`. On AlreadyPresent, the existing record wins (first-wins);
the new RequestId is added as an additional openable VATP capability and does **not**
revoke prior RequestIds. Each successful ArticleWork RequestId remains independently
valid for one VATP OPEN until that RequestId is consumed, cancelled, the retained
entry reaches `RetentionTtlSeconds`, or the BackFiller process disposes retention.
FIFO capacity reclaim under `MaximumRetainedPayloadGigabytes` **skips** entries that
still have openable RequestIds; pressure may reject **new** ArticleWork admissions
(`CapacityUnavailable`) rather than invalidate already-published Success capabilities.
TTL remains the maximum lifetime and may expire an entry (clearing remaining RequestIds)
even while capabilities were still openable. A configured per-article bound
(`BackFiller:ArticleRetention:MaxOpenableRequestIdsPerArticle`, default 16) rejects
further AlreadyPresent attachments without removing existing RequestIds or ArtData.
VATP OPEN resolves RequestId, verifies ArticleId, reserves outbound bytes, consumes
only that RequestId, acquires a transfer lease, then streams META / DATA / END under
per-stream WINDOW credit and round-robin DATA scheduling.

## NNTPD Phase 3 client

`IVatpArticleClient.FetchArticleAsync(fqdn, vatpPort, requestId, articleId, ct)`:

1. Dials the supplied FQDN and VATP port (`TargetHost` / SNI = FQDN).
2. Acquires a pooled multiplexed VATP connection (keyed by host+port; bounded).
3. Client HELLO → server HELLO.
4. OPEN(RequestId, ArticleId) on a client-assigned StreamId ≠ 0.
5. Receives META / DATA* / DATA(FIN) / END via Common `ArticleTransferReceiveStream`.
6. Returns a consumable CanonicalV1 `ArticleRecord` only after ArtSize + FIN + END
   and successful `TryCreateFromCanonicalTransfer`.

TLS is mandatory (1.2/1.3). There is no plaintext fallback and no certificate
bypass. Clients send WINDOW updates to replenish server send credit. CANCEL is
sent for caller cancellation when the connection remains usable.

ArticleWork Success must include `fqdn`, `vatpPort`, and `articleId` (validated on parse).
A response that includes `uri` is rejected. Failure responses must not include
`fqdn`, `vatpPort`, or `articleId`. Message-id ARTICLE/HEAD/BODY/STAT
handlers consume `IVatpArticleClient` after ArticleWork Success.

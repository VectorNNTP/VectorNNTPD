# VectorNNTPD.BackFillerService architecture

This document describes the current standalone BackFiller process. BackFiller was extracted from VectorNNTP.NNTPD. Shared configuration that still mentions NNTPD is shared-domain context. It is not a second solution in this repository.

The worker consumes RabbitMQ Article Work, retrieves articles from upstream NNTP providers, parses them, retains accepted payloads in memory, and serves them to cache clients over a TLS listener. After the handshake that listener speaks a partial RFC 3977 session: `ARTICLE` by Message-ID, `DATE`, and `QUIT`. It does not listen for cleartext NNTP.

## Host lifecycle

`BackFillerApplicationHostedService` is the Generic Host adapter. It starts and stops `ApplicationServiceManager`.

`ApplicationServiceManager` starts registered `IApplicationService` instances in registration order. On startup failure it stops the services that already started (rollback) and leaves the manager idle so start can be tried again. Stop runs in reverse order. One `BackFillerShutdownOptions.GracePeriodSeconds` budget covers the whole stop or rollback loop. It is not granted separately to each service. When that budget expires while a service is stopping and the caller has not cancelled, `StopAsync` fails with `TimeoutException` (a non-zero process exit). Caller cancellation of shutdown is not classified as a budget timeout. Overlapping start and stop throw `InvalidOperationException` before waiting on the manager gate.

After start, non-null `IApplicationService.Execution` tasks are watched. A fault, a successful completion, or cancellation of that execution raises `UnexpectedServiceTermination`. `BackFillerApplicationHostedService` marks `BackFillerApplicationHealth` unhealthy and calls `IHostApplicationLifetime.StopApplication`.

`SystemdLifecycleNotifier` sends `READY=1` at most once after `IHostApplicationLifetime.ApplicationStarted`. Startup failure does not report ready. If watchdog health is not yet true at that moment, or if a handled transient notify failure occurs, readiness is retried until it succeeds, shutdown begins, or the notifier is disposed. Once a successful `READY=1` is sent, or once shutdown begins, further readiness notifications are suppressed. `SystemdWatchdogService` sends keep-alives only while `IApplicationHealth.IsHealthyForWatchdog` is true. That flag is also false while the published TLS certificate is missing, unreadable, or expired. Being inside the renewal window alone does not block keep-alives while the current material remains usable and unexpired; a recoverable renewal failure keeps serving that material. Each health read compares the recorded certificate not-after to the process clock so expiry during a prolonged ACME outage fails closed without waiting for a new observation. A later successful publication of a usable certificate clears the block. Unexpected supervised-service termination stays sticky. Readiness is not inferred from DI registration order.

Article Work shutdown uses `BackFillerShutdownOptions`:

- `DrainQueuedWork` defaults to true. When false, admitted work that has not entered processing is cancelled and NACK-requeued. Broker-prefetched deliveries that were never admitted are not drained by this flag.
- `FinishActiveArticles` defaults to true. When false, active work is cooperatively cancelled. Cancellation does not ACK the delivery.
- `ArticleWorkConsumerSession.RetireAsync()` without a token waits for admitted work and does not arm the grace timer. The host shutdown path passes a grace token from `GracePeriodSeconds`.

## Article Work reconcile

The consumer membership loop waits 15 seconds after a pass that started every desired consumer, including a pass with nothing to start. A pass that cannot declare topology because RabbitMQ is not ready, because channel creation or declaration failed for a desired backbone, or because Management-plane quorum membership is deficient or unverifiable, does not reset that delay. The next wait doubles, up to 2 minutes. An unexpected exception uses the same delay and does not end the loop. Shutdown cancellation ends the loop and is not a failure. The host stays healthy while RabbitMQ is temporarily unavailable. Capacity snapshots and connection replacement reconcile once on the shared gate and do not change the delay. If replacement cannot restore a session, the same periodic loop retries it. A later successful pass resets the delay to 15 seconds.

Active-processing stalls (`IsActiveProcessingStalled` / Event 5328) are remediated only by that reconcile path (`RetireForRecoveryAsync`). Silent-consumer monitoring may observe the same predicate but does not introduce a second recovery action.

## Silent-consumer monitoring (in-process)

Monitoring runs inside each `BackFillerService` process as `SilentConsumerMonitorHostedService`. There is no separate monitor executable. Multiple concurrent BackFiller instances consuming the same provider queues are the intended operating model. The monitor is observational only: it does not call `RetireForRecoveryAsync` or otherwise compete with Article Work reconcile recovery (Event 5328).

### RabbitMQ configuration sources (traced)

| Setting | Authoritative source | Runtime update |
|---|---|---|
| AMQP `Hosts`, `Port`, `VirtualHost`, TLS | Bound `RabbitMQ` section (`VectorNNTPD.RabbitMQ.json` / env). **Not** columns in `nntpsharedconfig`. | Captured into `RabbitMqService`'s start-time `RabbitMqRuntimeOptions` for logging, reconnect backoff, and related fields. Each `ConnectAndInstallAsync` dials via `ConnectAsync(_options.Value, …)` so the factory builds from the **live** `RabbitMqOptions` at that attempt. Hosts/vhost/TLS are not database-managed; changing the bound JSON/env still requires a process restart to pick up new endpoints in practice (they are not projected from MySQL). |
| Management `BaseUrl`, `RequestTimeoutSeconds` | `RabbitMQ:Management` in the same section. Distinct from AMQP port 5672. | Read live from `IOptions<RabbitMqOptions>` on each Management HTTP call (monitor and quorum probe). |
| Username / password | MySQL `nntpsharedconfig.rabbitmqusername` / `rabbitmqpassword` via `ProviderAccountConfigurationService.Project` → `NntpSharedConfiguration.CopyRabbitMqCredentialsTo`. Bound JSON/env credentials are discarded at options PostConfigure. | Written onto the live `RabbitMqOptions` instance each account refresh. Management clients see new credentials on the next HTTP request. AMQP uses those live credentials on the **next** connection attempt (`ConnectAndInstallAsync` → factory `ToRuntimeOptions()` from `_options.Value`); an already-open connection is not re-authenticated in place. |
| Article Work queues | MySQL provider catalog (`nntpbackfilleraccounts` → `ProviderConfigurationCatalog`). Queue name = `BackFillerRabbitMqTopology.ComposeProviderEntity(backbone)` → `backfiller.{normalized}`. | Catalog `Publish` replaces the list; monitor resolves targets every cycle. |
| Expected capacity for shared ConsumerLoss | `MaxSessions > 0` and `IBackboneUsableCapacityProvider.HasUsableCapacityForBackbone` (same gate as `BuildDesiredSessions`). | Capacity snapshots publish from `NntpProviderRegistry`; monitor re-reads each cycle. |

There is no database column for Management URL or AMQP hosts in this repository. The monitor does not invent Management from the AMQP host/port. Missing `RabbitMQ:Management:BaseUrl` or credentials yields a clear unavailable diagnostic; instance-local monitoring continues.

Optional public switch only: `BackFiller:SilentConsumerMonitor:Enabled` (default true). Detection windows live in `SilentConsumerMonitorDefaults` (`ProcessingStall` / `DispatchStall` / `ConsumerLoss`).

### Observation pipeline

Each monitor cycle: project live `ArticleWorkConsumerSession` counters via `SilentConsumerSessionObservation.FromSession` → `SilentConsumerMonitorEngine.ObserveInstance` → (when Management is configured) `ManagementQueueClient.GetQueueAsync` → `ObserveSharedQueue`. Instance evaluation always runs before the Management gate, so missing BaseUrl, missing credentials, or Management HTTP failures do not disable local Detection. Opened / Continued / Resolved transitions emit structured events 9110–9112 with `scope`, `condition`, `target` (session key or queue name), and `instanceId`.

| Condition | Observation source | Open window / resolve | Alert identity | Opens when | Resolves when |
|---|---|---|---|---|---|
| Instance ProcessingStall | Session `IsActiveProcessingStalled` (progress clock; same predicate as Event 5328) | 90s / 20s | `Instance` + session key | Running and actively stalled | Predicate clears for resolve window, or session removed |
| Instance DispatchStall | Session `QueuedCount` / `ActiveCount` from admit → `_dispatch` → `_active++` | 90s / 20s | `Instance` + session key | Running, queued &gt; 0, active == 0 | Queued clears / active advances for resolve window, or session removed |
| Instance ConsumerLoss | Desired slot count vs Running+open-channel sessions on this process | 45s / 20s | `Instance` + `local` | Desired &gt; 0 and zero usable local consumers | A local consumer returns, or desired becomes 0 |
| Shared ProcessingStall | Management unacked + near-zero ack rate with consumers ≥ 1 | 90s / 20s | `SharedQueue` + queue name | Aggregate looks stalled | Aggregate progress returns, or target pruned |
| Shared DispatchStall | Management ready backlog + near-zero deliver/ack | 90s / 20s | `SharedQueue` + queue name | Aggregate looks stalled | Aggregate progress returns, or target pruned |
| Shared ConsumerLoss | Management `consumers == 0` + ready floor while expected-active | 45s / 20s | `SharedQueue` + queue name | Fleet-wide empty consumers with backlog | A consumer reappears, capacity not expected, or target pruned |

### Dispatch-acquisition failure mode

Admission increments `_inFlight` before the dispatch lock. `QueuedCount` is `_inFlight - _active`. The session waits on `_dispatch`, optionally on the test seam `DispatchAcquireHold`, then increments `_active` and enters the pipeline. A hold (or any block) after the lock and before `_active++` leaves the consumer registered, the channel open, local work queued, and `IsActiveProcessingStalled` false because active processing never started. Instance DispatchStall covers that gap; active-processing recovery (Event 5328) remains authoritative once `_active &gt; 0`.

Legitimate idleness is `QueuedCount == 0` (and typically `ActiveCount == 0`): no DispatchStall. Broker ready count is not used for instance DispatchStall.

### Per-instance health

Local session projections only (no Management call). Events include `instanceId` = runtime FQDN. ProcessingStall and DispatchStall are keyed by the canonical session identity `ArticleWorkConsumerSession.ComposeSessionKey` (`backbone/connectionNumber`) so multiple `MaxSessions` slots on the same backbone — and distinct backbones — keep independent observation state. Shared-queue observations remain keyed by queue name only. Removing one session applies Inactive `"session removed"` only to that session key.

| Condition | Investigation label | Predicate | Idle vs fault |
|---|---|---|---|
| ProcessingStall | D | Running session with `IsActiveProcessingStalled` (same as reconcile) | Idle sessions never stall (`HasActiveProcessing` false). |
| DispatchStall | D2 | Running, `QueuedCount > 0`, `ActiveCount == 0` (dispatch hold before `_active++`, including `DispatchAcquireHold`) | `QueuedCount == 0` is legitimate idle, not a stall. |
| ConsumerLoss | E | Desired slots (usable capacity × MaxSessions) &gt; 0 and zero Running sessions with an open channel on **this** process | Desired=0 → inactive. |

A healthy sibling session on the same process, or a sibling instance advancing aggregate ACK/deliver rates, cannot clear these local observations.

### Shared queue health

Management polling of catalog-derived queues when BaseUrl and projected credentials exist. Aggregate gauges/rates describe the queue, not a consumer identity. A healthy sibling keeps shared ProcessingStall/DispatchStall/ConsumerLoss inactive while still leaving local stalls visible on the broken instance. Shared ConsumerLoss requires `consumers == 0`, ready backlog, and expected-active capacity on this instance. Idle or capacity-empty queues are not faults.

Every polling instance may emit the same shared observation independently (**no cross-process deduplication**). Removing a provider from the catalog emits an explicit Resolved event for any open shared observation on that queue (target gone — not broker recovery), then drops history; reintroducing the provider recreates fresh history and state machines (next Open is a new Opened, not Continued). In-memory alert state is per process and is lost on restart.

### Management outages and `Unknown`

Two distinct unavailability paths:

1. **Management not ready to poll** (missing `BaseUrl` or credentials): HostedService skips `ObserveSharedQueue` and emits Event 9123 once per transition into unavailable. No `Unknown` signal is applied; any already-open shared alerts simply remain as last evaluated.
2. **Management polled but sample unusable** (HTTP auth/timeout/transport/incomplete gauges): `ObserveSharedQueue` records a failure sample; shared evaluators return `ConditionSignal.Unknown`. The state machine neither opens nor resolves on `Unknown`. Event 9113 may log (rate-limited per observation key = scope + condition + queue/session target, interval = ProcessingStall open window) that an already-open alert received Unavailable/`Unknown` and was retained — not that Management was never configured.

Consequences:

- An already-`Open` shared alert **remains Open** for the duration of a Management outage or while polls return non-success. That freeze is intentional: lack of observation must not be treated as recovery or as proof the underlying queue condition cleared.
- An `Open` shared alert during an outage does **not** establish that the broker condition is still present; it only means the last successful evaluation left the alert open and no conclusive Inactive signal has been observed since.
- `Unknown` is not healthy and is not recovery.
- Instance-local monitoring continues on every cycle before the Management gate (`ObserveInstance` always runs). Missing BaseUrl, missing credentials, or HTTP failures do not disable local ProcessingStall / DispatchStall / ConsumerLoss.
- When valid Management samples resume, shared conditions are reevaluated: Active continues an open alert, Inactive starts the resolve window, and catalog prune still emits Resolved with the target-removed reason (monitoring-scope change, not broker recovery).
- Event 9113 / Continued rate-limit state is keyed per observation identity (not global across queues or conditions). Shared prune removes that rate-limit entry with the machine.

### Known limitations

- Shared metrics cannot identify which consumer is unhealthy; they only describe the queue aggregate.
- Duplicate shared Opens across BackFiller instances are expected.
- Shared alerts may remain `Open` while Management observations are unavailable (`Unknown` or no poll); operators must not read that freeze as confirmed ongoing fault or as recovery.
- Instance DispatchStall sees only this process’s admitted queue, not another instance’s waiting work on the same broker queue.
- `MaxSessions &gt; 0` with usable capacity means desired consume slots exist; it does not prove a broker consumer tag is present until the session reaches Running with an open channel.
- Monitor observations do not recover sessions; reconcile remains the only remediation for Event 5328.

### Isolation

Monitor HTTP never enters Article Work dispatch, settlement, NNTP, or ACK/NACK paths. Failed/missing metrics are `Unknown` (never invented as zero). The monitor does not change reconcile, topology, or logging retention.

Provider queue type is derived from the configured unique `RabbitMQ:Hosts` count (never from reachable hosts or live cluster members). Exactly one host declares a durable classic queue (`x-queue-type=classic`) without quorum arguments or Management quorum health. Two or more hosts declare a durable quorum queue with `x-quorum-initial-group-size` and readiness membership floor equal to that host count, plus configured `x-delivery-limit`. All configured hosts must belong to the same RabbitMQ cluster; the client host list does not establish membership. Successful AMQP quorum declaration alone does not mark a backbone ready: readiness also requires Management HTTP verification of members, online members, and leader. Pre-existing queues with inequivalent type or immutable arguments are not deleted or recreated; operators must migrate or grow membership explicitly. Changing `Hosts` from one entry to multiple (or the reverse) changes the desired queue type and is not a safe in-place conversion. Management outages and timeouts leave multi-host backbones unready and follow the same reconcile backoff.

## Article Work settlement

`ArticleWorkDispositionPlanner` maps one outcome to ACK or NACK, the NACK requeue flag, and whether a terminal response is published. `ArticleWorkSettlementTests` locks this map for replyable deliveries when cancellation is not already requested. Broker `x-delivery-count` is compared to `ArticleWorkDeliveryLimit`: when the effective count is greater than or equal to the limit, otherwise-requeue outcomes (including cancellation override) settle without requeue so poison work cannot retry indefinitely. On quorum topology, a redelivery missing `x-delivery-count` is treated as exhausted; classic topology does not apply that conservative missing-count rule because classic queues do not provide the header. Negative settlement uses AMQP `basic.reject` via `IRabbitMqManualAckChannel.BasicRejectAsync` so quorum-queue delivery counts increment under RabbitMQ 4.3+ rules; `BasicNackAsync` remains true `basic.nack` for other callers and would not increment the count on 4.3+. The broker itself discards when the post-increment count exceeds `x-delivery-limit` on quorum queues. No dead-letter exchange is declared; exhausted messages are dropped. On multi-host (quorum) topology, a Management outage leaves new or recovering backbones unready even when AMQP is healthy (fail closed). Single-host classic readiness does not call the quorum Management probe.

| Outcome | Broker action | Publish terminal response |
|---|---|---|
| Success | Basic.Ack | Yes |
| ArticleNotFound | Basic.Ack | Yes. Definitive provider `430` or ARTICLE `451`; callers must not republish the same logical lookup to another backbone because of this outcome. |
| InvalidArticle | Basic.Ack | Yes. Includes an article `ArticleWorkDeliveryFraming` cannot send. That rejection happens before retention, so the article is not stored. |
| InvalidRequest | Basic.Ack | Yes when both `CorrelationId` and `ReplyTo` are present. Otherwise ACK without publication |
| ProviderFailure | Basic.Ack | Yes. Correlated failure reply (`outcome=ProviderFailure`). Must not NACK-requeue: classic queues lack enforceable delivery-count exhaustion and requeue without a reply caused unbounded redelivery plus client timeout republish. |
| Cancelled | Basic.Nack, requeue true while delivery count &lt; limit; otherwise NACK without requeue | No. Cooperative cancel / shutdown may resume the delivery later. |
| UnexpectedFailure | Basic.Ack | Yes. Correlated failure reply (`outcome=UnexpectedFailure`). |
| RetentionRejected | Basic.Ack | Yes. Correlated failure reply (`outcome=RetentionRejected`). |

If cancellation is already requested, the planner returns NACK (requeue while retries remain) and does not publish, including when the handler outcome was terminal.

`ArticleWorkSettlementLease` is one settlement attempt for one delivery tag on the original consumer channel and connection generation. It sets the settled flag before the broker RPC. If that RPC throws, the flag stays set and the lease does not retry. If the channel is stale, closed, or not the original channel, `TrySettleAsync` returns false without ACK or NACK, and the broker may redeliver. Pipeline settlement calls use `CancellationToken.None`.

When the success publisher reports that it does not complete success publication, the pipeline NACK-requeues and still returns `ArticleWorkOutcome.Success`. A required terminal response that is cancelled or that fails for a **transient or ambiguous** reason NACK-requeues and returns `ArticleWorkOutcome.UnexpectedFailure`, including when the planned disposition was ACK. A **confirmed mandatory unroutable** reply (`RabbitMqPublicationUnroutableException` / broker basic.return on the ReplyTo queue) is terminal: NACK **without** requeue. Successful upstream article retrieval is not the same as successful RPC delivery; a permanently missing exclusive ReplyTo cannot be repaired by refetching. Because no dead-letter exchange is declared on Article Work queues, that terminal NACK **discards** the original work message at the broker. This is the same discard class as delivery-limit exhaustion. It is **not** a dead-letter. Operator visibility and recoverability for that path are the structured Warning `ReplyUnroutableTerminal` (request id, Message-ID, backbone, correlation id, ReplyTo, outcome) plus any article already retained from a successful upstream fetch. Introducing a durable failed-work queue or DLX is an explicit topology/ops decision and is not implied by the current settlement. The lease still settles at most once. A failed NACK is not retried on that lease, and a stale or replacement channel is not settled. A confirm lost after the broker has accepted the publish is indistinguishable from a failed publish, so the requeue can publish a second reply with the same correlation id. That is at-least-once for the reply when the confirm is ambiguous, not exactly-once.

Publisher confirms (`BasicAcksAsync` / `BasicNacksAsync` on an async-confirm publish channel) are the broker's response to an outbound publish. They are not the consumer ACK of the inbound delivery.

## NNTP client

`NntpProviderSession` is an upstream client. Connect requires a 200 or 201 greeting, always sends `CAPABILITIES`, sends `STARTTLS` only when that capability is advertised and the provider is not already on implicit TLS, then sends `AUTHINFO USER` and, when required, `AUTHINFO PASS`. `ARTICLE` takes a Message-ID. Idle pooled sessions may send RFC 3977 `DATE` when the provider keepalive is enabled. `ARTICLE` and `DATE` share one busy lock. The upstream socket disables Nagle and sets independent send and receive buffer limits before connect and again on the connected socket, before TLS or the first NNTP byte. Those limits are not the 64 KiB managed reader buffer. `MaxSessions` and `MaxConcurrentSessionEstablishments` are unchanged.

`NntpStartTlsTests` assert the client does not send `COMPRESS`. The listener below does not implement `IHAVE`, `TAKETHIS`, `MODE STREAM`, `LIST`, `CAPABILITIES`, `GROUP`, `HEAD`, `BODY`, `STAT`, posting, or authentication.

`VectorNNTPD.Common.Articles.Parsing.NntpArticleParser` parses transport-normalized article bytes after acquisition has removed the multiline terminator and undone dot-stuffing once. Message-ID checks, yEnc validation, Date resolution, Path classification, and `ArticleTypeClassifier` are that same Common implementation. `VectorNNTPD.Common.Articles.ArticleRecordFactory` is the only canonical materializer. It rewrites Date and Path, then `ArticleBodyWireStuffer` dot-stuffs body lines into the same output buffer. Headers are not stuffed, and the NNTP terminator is not stored. `ArticleRecord.ArtData` is that stuffed image, and `ArtHash` is XXH3-64 of those logical bytes. `ArticleId` remains BLAKE3 of the Message-ID value. BackFiller retention stores that record and does not materialize a second image. Article Work rents the canonical array from `ArrayPool<byte>.Shared`. `ArtSize` is the logical length; the rented array may be longer, and hashing, framing, and retention accounting ignore the unused capacity. Callers that do not pass a pool still receive an exact-size array. A rented array returns to that same pool only when the last consumer releases it. A new retention insert either publishes every index, the retained-byte count, and one buffer lease together, or rolls those changes back before the retention lock is released. A stored entry holds that lease until expiry, FIFO reclaim, capacity eviction, or a committed delivery. Each delivery claim holds a separate lease until that claim settles, so a committed or expired entry does not return the array while a claim can still read it. The claim owner finishes reading before it commits or disposes; the reference count does not wait for a different thread. The rented array is cleared once on return, outside the retention lock, because the pool is process-wide. `VectorNNTPD.BackFillerService.ArticleWork.ArticleWorkDeliveryFraming` copies the retained body unchanged and adds a stuffing dot only to header lines that begin with `.`, and only in the socket image. The listener then writes one `.\r\n`. `ArticleTypeClassifier` sets control and cancel bits from `Control: cancel`, and a PGP-message bit from ASCII armor. That classification does not implement cancel propagation or PGPVERIFY.

Per-provider steady-state session count comes from MySQL `maxconnections` (`BackFillerProviderDefinition.MaxSessions`). `BackFillerNntpOptions.MaxConcurrentSessionEstablishments` bounds how many sessions may be connecting at once. The pool throws if it would exceed `MaxSessions`.

Pool `DisposeAsync` and registry `StopAsync` / `DisposeAsync` each capture one monotonic deadline from that call's configured grace. The same deadline bounds the keepalive wait, the lease-drain wait, and every graceful `QUIT`. `QUIT` uses the shorter of its two-second allowance and the time still left. The deadline arms one cancellation token when the budget is captured. A `QUIT` that this deadline bounds waits on that token, so the next session observes the same cancellation and does not start another `QUIT`. A session that cannot start `QUIT` before the deadline aborts the transport instead. Sessions in one pool, and pools in one registry shutdown, are retired with a fixed degree of parallelism and every retirement is still awaited. A configuration replacement drain waits for outstanding leases on the caller token, then captures its own grace as one retirement deadline for every remaining session. That deadline is not the registry shutdown deadline, and it is not reset per session. A direct session dispose keeps the two-second `QUIT` allowance. It does not consume another shutdown's deadline.

Relevant local RFC text is under `docs/standards/rfcs/`. For this client, start with RFC 3977 (`ARTICLE`, `DATE`, `CAPABILITIES`, multiline and dot-stuffing), RFC 4643 (`AUTHINFO`), and RFC 4642 / RFC 8143 (TLS). RFC 5536 is the article-format reference used by the parser and by the listener's Message-ID check. The other files in that directory are retained standards text. They are not evidence that this process implements those extensions.

## NNTP listener

`CacheListenerService` binds the configured TLS endpoints and admits connections up to `MaxActiveConnections`. Each admitted socket disables Nagle and receives its own send and receive buffer limits immediately after accept and before TLS. Listening-socket options are not inherited. A socket-option failure disposes that socket and releases the admission slot. The configured sizes are kernel buffer limits; the operating system may round them, and they are not a measured congestion window. Each admitted socket then gets one TLS 1.2 or 1.3 handshake with no client certificate and revocation checks off. The certificate lease is taken per connection and disposed with that connection. After the handshake succeeds, `NntpCacheListenerSession` uses `StreamCacheListenerTransport`. The session does not own the socket or the certificate. `IoProgressTimeout` is one no-progress deadline for each read and each non-empty write on that connection. Completing the call is progress, and the next call gets a new deadline. The session passes each logical buffer — the status line, the article image, and the terminating `.\r\n` — in its own write. It does not slice a buffer into 32 KiB pieces. `Stream.WriteAsync` accepts the supplied buffer or throws, so that deadline covers the whole buffer and is not reset mid-call. A transport that returns a shorter positive count has made progress: the session continues at the first unaccepted byte, and the next write gets a new deadline. Shutdown cancellation, handshake failure, timeout, I/O, and other connection failures use the same listener logs as before.

The greeting is `201 NNTP Service Ready, posting prohibited` because posting is not implemented. It is sent before any command response. Command keywords are ASCII case-insensitive. One or more spaces or tabs separate the keyword from its argument. Lines end in CRLF. A command longer than 512 octets, including CRLF, is answered with `501 Syntax error` and is not split into a later command. Bytes after one command stay in the buffer. The command being handled stays at the front of that buffer until its response writes finish, so the session reads the line and the Message-ID from those bytes. The `220` and `111` lines are formatted into buffers owned by that session and are not overwritten until the write of that line has returned. `QUIT` with no argument sends `205 Connection closing` and returns; the service then closes the connection. `QUIT` with an argument is `501` and the connection stays open. `DATE` with no argument sends `111` and fourteen UTC digits `yyyymmddhhmmss` from the same `TimeProvider` used for article admission, then waits for another command. `DATE` with an argument is `501` and the connection stays open. Any other command is `500 Unknown command`. A peer close ends the session without another response.

`ARTICLE` with no argument, or with an all-digit article number, is `412 No newsgroup selected`. There is no selected group. Extra arguments, or a Message-ID that fails `NntpMessageIdValidation`, are `501 Syntax error`. That validator is the RFC 5536 grammar already used by article admission, so it is stricter than an opaque RFC 3977 message-id. A valid Message-ID that is not retained is `430 No Such Article Found`.

A successful claim writes `220 0`, the Message-ID octets from the command, ` article follows`, CRLF, the multiline article image, and one `.\r\n`. The claim is granted only when every retained line ends in CRLF and the buffer ends in CRLF. `ArticleRecord` records that framing from its logical bytes at construction, and admission uses the stored result. Delivery still analyzes the live claimed bytes after the claim is reserved and after the retention lock is released, so the lock does not cover a walk of `ArtData` and a write into the array after admission is still visible. The claimed buffer stays reachable for that scan. If the scan fails, the claim is released before `TryClaimForDelivery` returns, and the session responds `403` before `220` or any article byte. The Message-ID dictionary key is built before the lock. The scan's body offset is what the listener uses. When a header line begins with `.`, the listener writes a stuffed header buffer and then the retained body slice. It does not allocate another copy of the body, and it does not walk the body again to decide stuffing. A bare CR, a bare LF, or NUL anywhere in the article is `NotFrameable`: the session responds `403 Article not available` before `220` or any article byte, and the stored buffer and byte accounting stay unchanged. A header line that begins with `.` is dot-stuffed only in the bytes written to the socket, so a client that removes one leading dot restores that header. The body is written from the retained slice, so a stuffing dot already stored on a body line is not added again. A dot-leading line with no `CRLF CRLF` separator is also `NotFrameable`, because the header region and the already-stuffed body cannot be distinguished. The same `403` is used when another delivery already holds the entry, and the connection stays open so the client can retry. `436` is not used.

The retained entry is removed only after the status line, the article bytes, and the terminator have all been accepted by the local transport. A failure, cancellation, or session fault before that rolls the claim back when the entry is still indexed and unexpired. TTL is not extended because a delivery is active. An active claim keeps the `ArtData` bytes reachable for the write even if expiry or disposal drops the authority's own reference, and it holds the rented buffer out of the pool until the claim settles. It also pins the entry against FIFO physical reclamation, including when its last RequestId is cancelled during the write. Expiry or disposal that already removed the entry does not reindex it on rollback. A second claim while one is active is refused and does not cancel a RequestId. One committed `ARTICLE` removes the entry and every RequestId attached to it. RequestIds remain Article Work capabilities that pin FIFO reclaim until cancel, TTL, committed delivery, or dispose. They are not a second listener protocol.

Acceptance of those writes by the local socket is not proof that the peer received or processed the article. A disconnect after some bytes have been accepted can leave the peer with a prefix; rollback then allows another delivery. A commit followed by a lost connection does not.

NNTP is the only listener protocol. There is no cleartext listener and no second framing after TLS.

## Byte-oriented protocol data plane

NNTP command bytes stay bytes through session I/O. `NntpProtocolIo` pre-encodes `DATE`, `CAPABILITIES`, `STARTTLS`, and the `ARTICLE`, `AUTHINFO USER`, and `AUTHINFO PASS` prefixes, and appends CRLF as bytes. The Message-ID argument is sent as ASCII bytes. A non-ASCII Message-ID is not placed on the wire.

Incoming status lines and article payloads are read as bytes. Dot-stuffing is removed in the stream reader before the parser sees the article. Header and body slices alias the caller buffer. The retained article image is that canonical article with body lines dot-stuffed once in the materializer's output buffer. The listener appends one `.\r\n`. It writes the retained body slice unchanged and adds a stuffing dot only to header lines that begin with `.`, and only in the socket image.

Strings are used at explicit boundaries: configuration, logging, AMQP metadata, and article-identity keys such as the Message-ID string stored by retention. The listener's command parser and `220` line stay on the command octets. Do not add a bytes-to-string-to-bytes conversion on the session I/O path because an API currently accepts a string.

The listener command path is byte-oriented, as is the upstream NNTP client. They are different roles: the client retrieves articles, and the listener serves retained bytes.

## Source-generated structured logging

Application logs use `LoggerMessageAttribute` on partial methods, usually in a `*LogMessages.cs` companion. Serilog is the host logging stack. The rolling gzip file sink, compressor, active-file writer, and periodic flush wrapper live in `VectorNNTPD.Common.Logging`. BackFiller selects targets, levels, retention, syslog, and RabbitMQ publishing. Message templates keep structured property names. Do not preformat those properties into the message string.

Do not log Cloudflare API keys, ACME passwords, RabbitMQ passwords, `AUTHINFO` secrets, or article payloads. NNTP wire debug logs redact `AUTHINFO USER` and `AUTHINFO PASS`. Guard expensive trace or debug arguments with `IsEnabled` when building them has a cost.

`[LoggerMessage]` message strings are ASCII in the current contributing guidance. XML documentation may use Unicode.

## Not implemented

This process does not implement:

- a complete RFC 3977 server, a cleartext NNTP listener, listener `CAPABILITIES`, `GROUP`, `HEAD`, `BODY`, `STAT`, posting, or authentication
- NNTP compression, streaming feeds, or `LIST` extensions
- PGPVERIFY or SPAMD (`docs/standards/pgpverify/` and `docs/standards/spamd/` are not in this repository)
- a BenchmarkDotNet project or a `TransitPublisherTests` baseline

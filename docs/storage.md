# VectorNNTPD.StorageService — architecture and configuration

Operator and developer contract for the StorageService host in `VectorNNTPD.slnx`.
Claims below are verified against `src/VectorNNTPD.StorageService/` and related Common types
on the current tree. This is **not** BackFiller documentation and **not** upstream NNTPD.

Related docs (do not duplicate): [logging.md](logging.md), [systemd.md](systemd.md),
[common/configuration.md](common/configuration.md), [common/vatp.md](common/vatp.md),
[common/ArticleRecord.md](common/ArticleRecord.md), feed wire behaviour
[storage-nntp-feed.md](storage-nntp-feed.md), root [../README.md](../README.md).

## Responsibilities

StorageService is a **TLS-only NNTP feed / cache host**:

- Accepts inbound TLS NNTP sessions and speaks a small command set (`ARTICLE`, `TAKETHIS`, `QUIT`).
- Parses accepted TAKETHIS bodies into CanonicalV1 `ArticleRecord` values, queues them, and
  persists them into configured segment-pool spool directories.
- Indexes articles in a **local SQLite** database under `StorageServer:DbDir`.
- Publishes capacity advertisements to RabbitMQ exchange `cache.broadcast` (connectivity only;
  this host does not register Article Work consumers).
- Loads ACME / Cloudflare / RabbitMQ credentials from MySQL `nntpsharedconfig` (selected columns).

It does **not** implement NNTPD Reader/Transit command sets, PostFilter, Redis quota, or
BackFiller Article Work settlement. Upstream SQL under [schema/](schema/) is not applied by this host.

## Architectural boundaries

| Concern | Owner |
|---------|--------|
| Options bind / validate | `StorageServerOptions`, `StorageServerOptionsValidator` |
| Host entry / JSON | `Program.cs`, `ApplicationJsonConfiguration` |
| Application service order | `StorageServerServiceCollectionExtensions.AddStorageServerHosting` |
| Lifecycle / READY | Common `ApplicationLifecycle` + Storage `SystemdLifecycleNotifier` |
| NNTP TLS listener | `StorageNntpListenerService` |
| Per-connection session | `StorageNntpFeedSession` |
| In-memory admit queue | `AcceptedArticleQueue` / `AcceptedArticleWorker` |
| Spool write / LFU | `SpoolArticlePersistenceProcessor`, segment-pool / eviction services |
| Article index | `ArticleIndexService` / `ArticleIndexDatabase` (SQLite) |
| Shared MySQL row | `StorageServerSharedConfigurationService` |
| Capacity ads | `StorageServerAdvertisementPublisherService` → `cache.broadcast` |

### Startup order (application services)

Documented in code remarks and registration order:

1. NntpDB  
2. `nntpsharedconfig`  
3. Cloudflare DNS reconciliation  
4. RabbitMQ (connectivity)  
5. ACME certificates  
6. Segment-pool validation  
7. Spool shard initialization  
8. Article index  
9. Block-device monitor  
10. Spool eviction monitors  
11. Accepted-article worker  
12. NNTP feed listener  
13. Advertisement publisher  

Shutdown is the reverse: advertisement stops first; the listener drains before the worker stops.
NntpDB does not migrate schema. Segment-pool validation and shard initialization throw before the
worker, listener, or advertisement publisher start.

## Configuration sources and precedence

Content root is `AppContext.BaseDirectory` (executable directory), not the process working directory.

| Source | Role |
|--------|------|
| `VectorNNTPD.StorageService.json` | Primary host JSON (`{entry assembly name}.json`) |
| Optional `VectorNNTPD.StorageService.{Environment}.json` | Environment overlay when present |
| `VectorNNTPD.RabbitMQ.json` | Shared RabbitMQ connectivity (inserted after entry-assembly JSON) |
| Environment variables | Generic Host default environment source (for example `StorageServer__ServerId`, `ConnectionStrings__NntpDB`) plus `AddVectorEnvironmentVariables()` for the shared `VECTOR__` prefix (RabbitMQ/ACME/Cloudflare names documented in [common/configuration.md](common/configuration.md)). Later sources override earlier JSON for the same keys. Bound RabbitMQ user/password and shared ACME/Cloudflare secrets from env are still discarded/projected as described above. |
| Command-line args | Generic Host args after env |
| `/etc/vectornntp/storageservice.env` | systemd `EnvironmentFile` (typically `ConnectionStrings__NntpDB`) |
| MySQL `nntpsharedconfig` | ACME directory URL, renewal days, zone id, DNS suffix, ACME account/password, Cloudflare API key, RabbitMQ username/password |

Bound RabbitMQ username/password from JSON/env are cleared at PostConfigure; live credentials come from
`nntpsharedconfig` after that snapshot is published. Do not log complete options objects.

Section name: **`StorageServer`**. RabbitMQ uses top-level section **`RabbitMQ`** (not nested).

## Required versus optional

### Required for a normal production start

| Item | Evidence |
|------|----------|
| `StorageServer:ServerId` | Integer 1–255; missing ≠ `0` |
| `StorageServer:BindPortTls` | Integer 1–65535; TLS-only — no cleartext fallback |
| `ConnectionStrings:NntpDB` / `ConnectionStrings__NntpDB` | Startup validation / NntpDB |
| Reachable singleton `nntpsharedconfig` with columns Storage selects | `StorageServerSharedConfigurationReader` |
| Writable `DbDir`, `LogDir`, `AcmeStateDir` / certs, and each `SegmentPools:Directories` entry | Validation + start-time services |
| Usable ACME certificate before listener bind | Listener refuses to start without TLS material |

### Optional / deferred from JSON

| Item | Notes |
|------|-------|
| `BindAddress` | Empty/omitted → all interfaces (`*` after PostConfigure). Explicit addresses must be local. |
| `BindPort` | Default `0`; never listened on |
| `DnsSuffix`, `CloudFlareZoneId`, `AcmeDirectoryUrl`, `AcmeRenewalThresholdDays` | Published from `nntpsharedconfig`, not trusted from this JSON section after projection |
| `SpoolPersistence` | Omitted uses type defaults (still validated when present as a section) |
| `StartupTimeout` | Optional lifecycle bound |
| systemd `WatchdogSec` | Unit ships commented out; app `EnableWatchdog` default true |

Storage’s `nntpsharedconfig` reader does **not** select `maxartsize`, `sitename`, or `prometheusurl`.
Article size for this host uses Common `ArticleResourceLimits.MaxArticleBytes` (5 MiB) at parse/receive,
not a MySQL maxartsize column.

## Configuration keys (bindable)

Defaults below are code defaults on `StorageServerOptions` / nested types. The shipped
`VectorNNTPD.StorageService.json` may override some (for example `LogDir: logs`, explicit
`BindAddress`, `BindPortTls: 1191`).

| Key | Default (code) | Notes |
|-----|----------------|-------|
| `ServerId` | none | Required 1–255 |
| `BindAddress` | `*` after PostConfigure | Wildcards `*`, `0.0.0.0`, `::` |
| `BindPort` | `0` | Unused cleartext |
| `BindPortTls` | none | Required 1–65535 |
| `AcmeStateDir` | `certs` | Relative → `ApplicationLocalPath` |
| `CertificateDirectory` | `certs` | |
| `LogDir` | `/logs` (constant); shipped JSON uses `logs` | File sink directory |
| `DbDir` | `db` | SQLite index; must not coincide with a spool root |
| `GracefulShutdownTimeout` | `00:00:30` | Also drives host `ShutdownTimeout` |
| `StopHostOnUnexpectedServiceTermination` | `true` | |
| `ApplicationName` | entry assembly name | Lifecycle display; log file names use entry assembly name |
| `Listener:ParserAccumulationMaxBytes` | `262144` | |
| `Listener:TlsHandshakeTimeoutSeconds` | `30` | |
| `Listener:IoProgressTimeoutSeconds` | `60` | Suspended while waiting for queue capacity |
| `Listener:ArticleReceiveTimeoutSeconds` | `300` | Overall TAKETHIS receive deadline |
| `Listener:MaxActiveConnections` | `1024` | |
| `SegmentPools:Directories` | `["spool/cache"]` | Each dir dedicated mount before traffic |
| `SegmentPools:LowPercent` / `HighPercent` / `CriticalPercent` | `75` / `79` / `85` | Per-spool LFU / admission; Critical excludes placement |
| `SpoolPersistence:MaxQueuedBytes` | 1 GiB | Range 1 MiB–8 GiB |
| `SpoolPersistence:MinWorkers` / `MaxWorkers` | `1` / `8` | |
| `SpoolPersistence:MaxInFlightPerSpool` | `2` | |
| `SpoolPersistence` scale/control intervals | see `SpoolPersistenceOptions` | Bound later disk work; **do not** make TAKETHIS `239` durable |
| `Systemd:*` | ready/stop/status/watchdog flags; fraction `0.5` | See [systemd.md](systemd.md) |

FQDN is generated: `cache{ServerId:00}.{DnsSuffix}` after `DnsSuffix` is published. It is not a bindable key.

## Persistence and paths

| Path | Role |
|------|------|
| `DbDir` | SQLite `ArticleIndex` / `ArticleIndexMeta` (+ WAL). Created at startup. Schema created/migrated in-process — **not** the MySQL scripts under `docs/schema/`. |
| `SegmentPools:Directories` | Canonical article spool roots; placement and LFU eviction |
| `LogDir` | Serilog rolling gzip files |
| `AcmeStateDir` / `CertificateDirectory` | ACME/TLS material |

Under systemd, unit `ReadWritePaths` include `logs/`, `certs/`, `db/`, and `spool/` under
`/opt/vectornntp/storageservice`. See [systemd.md](systemd.md) and root README.

**Durability note (code contract):** TAKETHIS `239` is written after the article is committed into
`AcceptedArticleQueue`. SpoolPersistence settings bound later disk work. They do **not** make that
`239` mean the article is already durable on disk.

## Ingestion flow (summary)

1. Peer completes TLS; session greets and accepts commands ([storage-nntp-feed.md](storage-nntp-feed.md)).
2. Successful TAKETHIS → parse/normalize → queue commit → `239`.
3. `AcceptedArticleWorker` persists to an eligible spool and updates the article index.
4. `ARTICLE <message-id>` serves indexed published CanonicalV1 files (not `.writing-*` temps).
5. Advertisement publisher emits capacity to `cache.broadcast` about once per second while running.

## Messaging

- Shared `RabbitMqService` connection (Common). Username/password from `nntpsharedconfig`.
- Host registers **connectivity** and the advertisement publisher. Topology/queue/consumer registration
  for Article Work is **not** done in `AddStorageServerHosting` (code remark).
- This host does **not** register a `cache.requests` consumer in the current hosting setup (that
  fanout name exists in Common `CacheFleetTopology` for NNTPD↔Storage fleet lookup contracts).

### `cache.broadcast` advertisements

Publisher: `StorageServerAdvertisementPublisherService`. Declares durable **fanout** exchange
`cache.broadcast` so Storage can start before an NNTPD consumer exists.

| Behaviour | Verified value |
|-----------|----------------|
| Cadence | `CacheFleetTopology.AdvertisementInterval` = **1 s** |
| AMQP expiration | `AdvertisementExpirationMilliseconds` = **`"3000"`** (3 s message TTL) |
| Persistent | `false` |
| Routing key | Exchange name (fanout ignores key; empty keys are rejected by the publisher helper) |
| Content-Type | `application/json` |
| AppId | Runtime FQDN |
| Starts after | Advertisement service start (after NNTP listener is in the start order) |

**Capacity advertisement v1** JSON (`StorageServerAdvertisementWireProtocol`): `version`, `serverId`,
`fqdn`, `totalBytes`, `usedBytes`, `availableBytes`, `timestamp` (UTC ISO-8601), optional
`vatpPort` (1–65535). Storage fills `vatpPort` from `BindPortTls` (wire name remains `vatpPort`).

**Current capacity source:** DI registers `NoSpoolCapacityReader`, which always returns
`(TotalBytes, UsedBytes, AvailableBytes) = (0, 0, 0)`. Segment-pool Critical/High/Low percents do
**not** rewrite those advertisement fields. Treat published byte counts as **placeholder zeros**
until a real `IStorageCapacityReader` is registered. Identity/`vatpPort`/timestamp still publish.

**Draining announcement** (separate JSON, still on `cache.broadcast`): on stop, after the publish
loop ends, one lifecycle body with `state: "Draining"` plus `version`, `serverId`, `fqdn`,
`timestamp`, `vatpPort` (`StorageServerLifecycleWireProtocol`). Best-effort within a 2 s timeout;
failure does not block shutdown. Periodic advertisements remain the liveness/readiness signal for
peers (NNTPD registry semantics live outside this host).

## Logging and troubleshooting

Serilog-only. Shipped JSON enables Console + Async File; `StorageServer:LogDir` rewrites the file path.
See [logging.md](logging.md). Under systemd, journald collects console output.

Common failures:

| Symptom | Check |
|---------|--------|
| Exit 1 / validation | `ServerId`, `BindPortTls`, paths, `ConnectionStrings__NntpDB` |
| Never READY | Startup order stuck (MySQL, ACME, segment pools); `TimeoutStartSec` |
| Listener will not start | ACME certificate not ready (TLS-only) |
| TAKETHIS always `439` | Capacity critical / assembly admission / duplicate / parse reject / queue budget — see feed doc |
| `239` but article missing later | Persistence backlog, spool eligibility, index — `239` is not durability |

Do not paste connection strings or `nntpsharedconfig` secrets into tickets.

## systemd, readiness, shutdown

- Ready: `ApplicationLifecycle` → `Running` (single-shot). Notify failures **rethrow**.
- Stopping: lifecycle STOPPING / host shutdown; `GracefulShutdownTimeout` vs unit `TimeoutStopSec`.
- Details and install paths: [systemd.md](systemd.md), root README Storage install notes.

## Build and deploy

- Solution: `VectorNNTPD.slnx`; project `src/VectorNNTPD.StorageService/`.
- Publish: `tools/Publish-Release.ps1 -Platform linux-64` (flat layout with BackFiller).
- Startup smoke: `VECTORNNTP_STORAGESERVICE_STARTUP_SMOKE=1` and
  `VECTORNNTP_STORAGESERVICE_STARTUP_SMOKE_CONFIG` (path to isolated JSON) — builds host without
  listener/DB; see root README.

## Active databases (not `docs/schema/`)

| Store | Applied by |
|-------|------------|
| MySQL `nntpsharedconfig` | External / shared ops; Storage only SELECTs listed columns |
| SQLite under `DbDir` | Storage `ArticleIndexDatabase` at runtime |

Upstream Transit/PostFilter SQL remains under [schema/](schema/) as **reference only**.

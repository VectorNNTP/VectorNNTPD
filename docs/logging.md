# VectorNNTPD — Logging (Serilog)

Serilog is the **only** logging implementation for both production hosts in this solution
(`VectorNNTPD.BackFillerService` and `VectorNNTPD.StorageService`). Application code uses
`Microsoft.Extensions.Logging.ILogger<T>`; those resolve to Serilog sinks.

This document describes **current** BackFiller and StorageService behaviour. It does **not**
document INN `news` / Path-survey (`inpaths`) streams; those belong to upstream
`VectorNNTP.NNTPD` and are not hosts in `VectorNNTPD.slnx`.

Shared rolling-file primitives live in `VectorNNTPD.Common.Logging`
(`GzipRollingFileSink`, compressor, active-file writer, periodic flush). Prefer
`LoggerMessageAttribute` + partial methods for application messages; keep structured
property names and do not preformat them into the message string. See
[architecture.md](architecture.md#source-generated-structured-logging).

## Abstractions vs implementation

| Layer | Role |
|-------|------|
| `ILogger<T>` / `ILoggerFactory` | Application and framework logging API |
| `SerilogLoggerFactory` | Exclusive MEL factory registered by `AddSerilog` |
| Serilog sinks | Destinations (file, optional console / syslog / RabbitMQ depending on host) |

Do not re-add `AddConsole`, `AddDebug`, or `AddEventLog` as MEL providers. Both hosts clear
MEL providers and remove the default `ILoggerFactory` before registering Serilog.

`AddSystemd()` remains for systemd lifetime and notify support only. Any Microsoft console
formatter registration it may add is stripped afterward; Serilog owns console formatting when
a console sink is enabled.

## BackFillerService

### Configuration surface

BackFiller does **not** read a `Serilog` JSON section. The only bindable surface is
`BackFiller:Logging` in `VectorNNTPD.BackFillerService.json` (see
[configuration.md](configuration.md)).

| Setting | Default | Meaning |
|---------|---------|---------|
| `LogLevel` | `Information` | Serilog minimum applied to enabled targets (`Verbose`…`Fatal`) |
| `LogRetentionDays` | `14` | Daily file retention when the File target is enabled (range 1–3650) |
| `Json` | `false` | When true, enabled text-capable sinks use JSON formatting |
| `File:Enabled` | `true` | Rolling gzip file sink |
| `File:LogDir` | `logs` | Directory; required when File is enabled. Relative paths use Common `ApplicationLocalPath` against the application binary directory |
| `Syslog:Enabled` | `false` | Optional UDP/TCP syslog |
| `RabbitMQ:Enabled` | `false` | Optional publish via existing `IRabbitMqService` (`Exchange` / `RoutingKey`; broker credentials stay in `nntpsharedconfig` / RabbitMQ connectivity JSON) |

Sink graph construction is code-owned (`BackFillerFileLogging`) for Native AOT / single-file safety.
It does not call `ReadFrom.Configuration`.

### Console

The console sink is **off** unless the process is started with `--console`. Under the shipped
systemd unit, journald still receives stdout/stderr from the process, but Serilog does not
write application events to the console sink by default. See the unit comments in
`deploy/systemd/vectornntp-backfiller.service`.

### File sink behaviour

- Rolling path: `{LogDir}/{entry assembly name}-yyyyMMdd.log` (Serilog day token `-.log`).
- Daily roll; `rollOnFileSizeLimit` is false (no size-based roll).
- Completed days are gzip-compressed (`CompressionLevel.Fastest`) via Common
  `GzipRollingFileSink`; the active file is never compressed or deleted.
- Async wrapper: buffer size `50000`, **`blockWhenFull: false`** so a stalled file sink drops
  excess events instead of blocking article-path loggers. Critical diagnostics may be lost under
  sustained backpressure; prefer ops alerts on log-volume disk pressure.
- File sink is buffered with about a one-second flush interval.

### Bootstrap

`Program` assigns a bootstrap logger before the host is built so configuration and construction
failures are recorded, then flushes on shutdown (`Log.CloseAndFlushAsync`). After `host.Build()`,
an Information “logging initialized” event proves the Serilog factory is live (no secrets).

## StorageService

Host architecture and non-logging options: [storage.md](storage.md).

### Configuration surface

StorageService uses a `Serilog` section in `VectorNNTPD.StorageService.json` for minimum levels,
enrichment, and `WriteTo` arguments, plus `StorageServer:LogDir` for the directory.

| Setting | Source of truth |
|---------|-----------------|
| Console / File / Async arguments | `Serilog:WriteTo` |
| Log directory | `StorageServer:LogDir` (shipped `logs`; code default constant is `/logs` if the key is blank) |
| Path rewrite | `StorageServerFileLogging.BindResolvedFilePath` creates the directory and overwrites the File `path` so the JSON placeholder is never the runtime path |

Native AOT cannot rely on reflection discovery of sink extension methods for every `WriteTo`
entry. Storage applies MinimumLevel / enrichment via `ReadFrom.Configuration` while registering
Console / Async / File sinks explicitly (`StorageServerConfiguredSinks`).

### Console and journald

The shipped JSON enables the Console sink (Debug+ restricted level on that sink; global default
Information with `VectorNNTPD.StorageService` overridden to Debug). Under systemd, Information+
lines reach stdout and journald. See `deploy/systemd/vectornntp-storageservice.service`.

### File sink behaviour

- Same daily `{entry assembly name}-yyyyMMdd.log` naming and Common gzip rotation as BackFiller.
- Shipped Async args: `bufferSize` 50000, **`blockWhenFull: true`** (logging calls block when the
  async queue is full — deliberate difference from BackFiller).
- `retainedFileCountLimit` 14 in the shipped JSON.
- `buffered` true; `flushToDiskInterval` `00:00:01`.

### Bootstrap

Same pattern as BackFiller: bootstrap logger, `ConfigureStorageServerLogging`, then
`WriteLoggingInitialized` after the host is built.

## Host comparison

| Topic | BackFiller | StorageService |
|-------|------------|----------------|
| Config keys | `BackFiller:Logging` only | `Serilog` + `StorageServer:LogDir` |
| Console by default | Off (`--console` to enable) | On (shipped JSON) |
| Async `blockWhenFull` | `false` (drop under backpressure) | `true` (block) |
| Optional syslog / RabbitMQ log targets | Yes | No (not on this host’s logging options) |
| Shared gzip file implementation | Common `GzipRollingFileSink` | Same |

## Under systemd

See [systemd.md](systemd.md). File logs remain under each install tree’s `logs/` (unit
`ReadWritePaths`). Journal queries:

```bash
sudo journalctl -u vectornntp-backfiller.service -f
sudo journalctl -u vectornntp-storageservice.service -f
```

## What this repository does not implement

- INN-compatible `news` disposition log and Path-survey `inpaths` files (upstream NNTPD).
- Operator-configurable INN field order via `outputTemplate` for those streams.
- Microsoft MEL console / debug / event-log providers alongside Serilog.

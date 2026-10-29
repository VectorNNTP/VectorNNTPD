# VectorNNTPD — systemd deployment guide

Linux systemd installation and operations for the two production hosts in
`VectorNNTPD.slnx`: **BackFillerService** and **StorageService**.

This document covers Type=notify readiness, STOPPING, optional watchdog, journald,
and unit hardening. It does **not** replace the full install ceremony in
[../README.md](../README.md) (account creation, `install` modes, environment-file
secrets handling). It does **not** cover NNTP listeners, RabbitMQ topology, Article Work,
or VATP details — see [architecture.md](architecture.md) and [common/vatp.md](common/vatp.md).

Both hosts share the same *operational* systemd contract (Type=notify, READY/STOPPING,
optional WatchdogSec, journald) but keep **distinct** readiness and notify-failure
semantics. Do not assume identical internal state machines. A design-only comparison of
extracting a shared kit is in
[common/systemd-host-abstraction.md](common/systemd-host-abstraction.md) (**proposal only**).

Upstream `VectorNNTP.NNTPD` units and install paths are **not** documented here.

---

## Shared facts

| Item | Value |
|------|-------|
| Publish | `pwsh -File tools/Publish-Release.ps1 -Platform linux-64` → `release/linux-64/` (flat Native AOT layout for both hosts + shared `VectorNNTPD.RabbitMQ.json` + unit/env examples) |
| Service account | `vectornntp` / group `vectornntp` (may be shared by both trees under `/opt/vectornntp`) |
| Unit type | `Type=notify`, `NotifyAccess=main` |
| Watchdog in shipped units | `WatchdogSec` **commented out** until readiness is validated on the host |
| Secrets | `ConnectionStrings__NntpDB` in `/etc/vectornntp/*.env`; RabbitMQ/ACME/Cloudflare from `nntpsharedconfig` (not invented in the unit) |

Detection limitations (both hosts):

- Non-Linux: systemd notify/watchdog inactive; Windows Service registration still applies on Windows.
- Interactive Linux without `NOTIFY_SOCKET`: ordinary `dotnet run` is unaffected.
- `NOTIFY_SOCKET` is consumed by `Microsoft.Extensions.Hosting.Systemd` when the notifier is constructed; application code uses the host notify bridge, not a later re-read of the environment variable.
- Application configuration never overrides the systemd-supplied watchdog deadline (`WATCHDOG_USEC`).

---

## BackFillerService

### Paths and unit

| Item | Value |
|------|-------|
| Install root | `/opt/vectornntp/backfiller` |
| Executable | `VectorNNTPD.BackFillerService` |
| Primary JSON | `VectorNNTPD.BackFillerService.json` |
| Unit | `deploy/systemd/vectornntp-backfiller.service` → `/etc/systemd/system/` |
| Environment file | `EnvironmentFile=-/etc/vectornntp/backfiller.env` |
| `ReadWritePaths` | `logs/`, `certs/` |
| `TimeoutStartSec` / `TimeoutStopSec` | 180 / 60 (shipped) |

Follow [../README.md](../README.md#linux-service) for the authoritative `install` sequence,
environment-file permissions (`root:root` `0600`), and secret-handling rules. Do not
`chown -R` the install tree to `vectornntp`.

### Application options (`BackFiller:Systemd`)

| Option | Default | Meaning |
|--------|---------|---------|
| `EnableWatchdog` | `true` | Allow keep-alives when systemd set `WatchdogSec=` / `WATCHDOG_USEC` |
| `ReportLifecycleStatus` | `true` | Send `STATUS=` on Generic Host lifetime transitions |
| `NotifyReadyOnApplicationRunning` | `true` | Send `READY=1` after hosted services have started (see below) |
| `NotifyStoppingOnApplicationShutdown` | `true` | Send `STOPPING=1` when graceful shutdown begins |
| `WatchdogIntervalFraction` | `0.5` | Heartbeat interval as a fraction of the systemd deadline (validated ~0.05–0.9) |

Align `TimeoutStopSec` with `BackFiller:Shutdown:GracePeriodSeconds` (shipped grace 30; maximum 600).

### Readiness and notify failure

- Ready boundary: Generic Host `ApplicationStarted` (all `IHostedService.StartAsync` succeeded, including work owned through `ApplicationServiceManager`). Implementation: `SystemdLifecycleNotifier` under `Hosting/Systemd/`.
- READY does **not** wait for every external provider NNTP session to be connected.
- Startup failure never reports ready; the process exits non-zero.
- Ready retries on unhealthy or handled notify failure until success, shutdown, or dispose.
- Notify failures are **logged and swallowed** so a broken notify socket does not crash the process. Missing `READY=1` / keep-alives can still trip systemd timeouts when configured.
- Watchdog keep-alives run only while `IApplicationHealth` reports healthy for watchdog (host started, shutdown not begun, no unexpected supervised `IApplicationService` termination / related sticky unhealthy conditions).

### Logging under systemd

Shipped file destination: `BackFiller:Logging:File:LogDir=logs`. Console Serilog sink is off unless `--console`. See [logging.md](logging.md).

```bash
sudo journalctl -u vectornntp-backfiller.service -f
sudo systemctl status vectornntp-backfiller.service --no-pager
```

---

## StorageService

Architecture, configuration keys, and persistence: [storage.md](storage.md).  
NNTP feed commands: [storage-nntp-feed.md](storage-nntp-feed.md).

### Paths and unit

| Item | Value |
|------|-------|
| Install root | `/opt/vectornntp/storageservice` |
| Executable | `VectorNNTPD.StorageService` |
| Primary JSON | `VectorNNTPD.StorageService.json` |
| Unit | `deploy/systemd/vectornntp-storageservice.service` → `/etc/systemd/system/` |
| Environment file | `EnvironmentFile=-/etc/vectornntp/storageservice.env` |
| `ReadWritePaths` | `logs/`, `certs/`, `db/`, `spool/` |
| `TimeoutStartSec` / `TimeoutStopSec` | 180 / 60 (shipped) |

Mirror the BackFiller install pattern: root-owned binary and JSON; `vectornntp`-owned writable directories; `storageservice.env.example` → `/etc/vectornntp/storageservice.env` mode `0600`. Create `db/` and `spool/` before enable.

Example skeleton (adjust if README Storage steps are extended later):

```bash
sudo install -d -o root -g root -m 0755 /opt/vectornntp /opt/vectornntp/storageservice
sudo install -o root -g root -m 0755 VectorNNTPD.StorageService /opt/vectornntp/storageservice/VectorNNTPD.StorageService
sudo install -o root -g root -m 0644 VectorNNTPD.StorageService.json VectorNNTPD.RabbitMQ.json README.md /opt/vectornntp/storageservice/
sudo install -d -o vectornntp -g vectornntp -m 0750 \
  /opt/vectornntp/storageservice/logs \
  /opt/vectornntp/storageservice/certs \
  /opt/vectornntp/storageservice/db \
  /opt/vectornntp/storageservice/spool
sudo install -o root -g root -m 0600 storageservice.env.example /etc/vectornntp/storageservice.env
sudo install -o root -g root -m 0644 vectornntp-storageservice.service /etc/systemd/system/vectornntp-storageservice.service
sudo systemctl daemon-reload
sudoedit /etc/vectornntp/storageservice.env   # replace REPLACE_WITH_ placeholders
sudo systemd-analyze verify vectornntp-storageservice.service
sudo systemctl enable --now vectornntp-storageservice.service
```

### Application options (`StorageServer:Systemd`)

| Option | Default | Meaning |
|--------|---------|---------|
| `EnableWatchdog` | `true` | Allow keep-alives when systemd configured a watchdog |
| `NotifyReadyOnApplicationRunning` | `true` | Send `READY=1` when lifecycle reaches `Running` |
| `WatchdogIntervalFraction` | `0.5` | Heartbeat fraction of systemd deadline (same validation band as BackFiller) |

Align `TimeoutStopSec` with `StorageServer:GracefulShutdownTimeout` (shipped `00:00:30`).

### Readiness and notify failure

- Ready boundary: Common `ApplicationLifecycle` transition to `ApplicationState.Running` after successful application-service initialization. Implementation: Storage `SystemdLifecycleNotifier`.
- Single-shot ready (no BackFiller-style retry loop).
- Startup failure never reports ready.
- Notify failures are **logged and rethrown** (broken notify fails closed).
- Watchdog keep-alives require lifecycle `Running`, no unexpected termination, shutdown not requested.

### Logging under systemd

Shipped `StorageServer:LogDir=logs`. Console sink enabled in shipped JSON → journald. See [logging.md](logging.md).

```bash
sudo journalctl -u vectornntp-storageservice.service -f
```

---

## Deliberate differences

| Topic | BackFiller | StorageService |
|-------|------------|----------------|
| Ready trigger | `IHostApplicationLifetime.ApplicationStarted` | `ApplicationLifecycle` → `Running` |
| Ready retry | Yes (until healthy + notify succeeds) | No (one attempt) |
| Notify failure | Log + swallow | Log + rethrow |
| Lifecycle owner | `ApplicationServiceManager` + hosted services | `ApplicationLifecycle` + application services |
| Writable paths | `logs`, `certs` | `logs`, `certs`, `db`, `spool` |
| Install path | `/opt/vectornntp/backfiller` | `/opt/vectornntp/storageservice` |

---

## Troubleshooting

### Failure to start

```bash
sudo systemctl status vectornntp-backfiller.service --full
sudo journalctl -u vectornntp-backfiller.service -b --no-pager
# same pattern for vectornntp-storageservice.service
```

Check publish layout, execute bit, options validation, missing `ConnectionStrings__NntpDB`, and writable directory ownership under `ProtectSystem=strict`.

### Readiness notification timeouts

Symptoms: `TimeoutStartSec` exceeded, unit never becomes active.

- Confirm `Type=notify` and that the process is the main PID.
- Confirm initialization reaches the host-specific ready boundary (BackFiller: hosted services started; Storage: lifecycle `Running`).
- ACME / RabbitMQ / listener bind can legitimately need a higher `TimeoutStartSec` (shipped 180).

### Watchdog-triggered restarts

Leave `WatchdogSec` unset until ready has been proven. Then confirm `EnableWatchdog=true` and that health remains true. Unexpected supervised-service termination stops keep-alives by design.

### Shutdown timeouts

Raise `TimeoutStopSec` when raising BackFiller `GracePeriodSeconds` or Storage `GracefulShutdownTimeout`.

### Permission problems

Confirm `User=`/`Group=`, that `ReadWritePaths` directories exist and are writable by `vectornntp`, and that binaries/JSON remain root-owned and not world-writable.

### Running outside systemd

Interactive `dotnet run` must not send watchdog keep-alives. If `NOTIFY_SOCKET` is accidentally set in a developer shell, remove it.

## Security notes

- Do not run as root.
- Prefer environment files with restrictive permissions over embedding secrets in the unit.
- Do not paste connection strings or `nntpsharedconfig` secrets into tickets or logs.
- Do not run `systemctl show -p Environment` when diagnosing production hosts.

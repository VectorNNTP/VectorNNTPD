# NNTP Reader and Transit hosts

This document describes the migrated NNTP server split in VectorNNTPD. It does not replace BackFiller or StorageService architecture docs.

## Projects

| Project | Role |
|---------|------|
| `VectorNNTPD.Common` | Shared connectivity and domain primitives (ACME, Cloudflare, NntpDB, articles, VATP, RabbitMQ connection lifecycle). Not an NNTP server library. |
| `VectorNNTPD.ReaderService` | Reader executable **and** Reader NNTP implementation (session/commands, listeners, auth, PostFilter, moderation, email, Redis session state, ingestion, RabbitMQ topology) |
| `VectorNNTPD.TransitService` | Transit executable **and** Transit NNTP implementation (peer DNS/auth, CHECK/TAKETHIS/IHAVE, transit peer state, shared plumbing duplicated for independence) |
| `tests/VectorNNTPD.ReaderService.Tests` | Reader protocol, hosting, PostFilter, session-state, and related tests |
| `tests/VectorNNTPD.TransitService.Tests` | Transit protocol, peer, ingestion, and related tests |

There is **no** `VectorNNTPD.NntpServer` assembly. ReaderService → Common and TransitService → Common only. Shared *server* plumbing required by both hosts is **duplicated** into each executable project so neither depends on the other and Common does not become a relocated monolith.

Configuration section name remains `Nntpd`. Primary JSON files are `{entry assembly}.json` plus shared `VectorNNTPD.RabbitMQ.json`, loaded from `AppContext.BaseDirectory` (the executable directory).

## Role isolation

- Each host registers only its role via `AddNntpReaderHosting` / `AddNntpTransitHosting`. Opposite-role registration APIs throw `NotSupportedException`.
- FQDN host-label prefixes are `reader` and `transit`.
- Combined / dual-role hosting is not supported in production or tests.

## Protocol gating

Command catalog flags and **CAPABILITIES** advertising follow the host role. MODE READER / MODE STREAM behaviour is role-gated.

### HELP vs CAPABILITIES

**HELP** returns one static multiline syntax inventory for every session (see `Help` remarks and `NntpResponses.HelpComplete`). It lists both reader and transit command forms. That matches the source monolith HELP contract and RFC 3977’s view of HELP as human-readable syntax reference, not an authorization advertisement.

**CAPABILITIES** is the authoritative, role-filtered advertisement. A Reader host omits STREAMING/IHAVE/SPEEDTEST; a Transit host omits READER/MODE-READER/POST. Invoking a command that HELP mentions but CAPABILITIES does not advertise still yields the normal protocol rejection for that role.

## Canonical article bytes

Ingress destuffs transport wire once, then `ArticleRecordFactory` / `NntpArticleCanonicalMaterializer` materializes CanonicalV1 `ArtData` with Path/Date rewrite and **one storage body stuffing pass** (dest Common contract). Source monolith Common copied destuffed bodies without that pass; VectorNNTPD Common does not. Migrated tests assert the dest CanonicalV1 stuffed-body contract.

## Schema DDL

Operator SQL for PostFilter and `nntpusers` account-art-type lives under `docs/schema/` (`postfilter.sql`, `nntpusers-account-art-type.sql`). Apply these on the site MySQL database before enabling features that depend on those tables/columns. Do not invent production DDL beyond the checked-in scripts.

## Release package layout

`tools/Publish-Release.ps1` and `.github/workflows/build.yml` publish BackFiller, StorageService, ReaderService, and TransitService as self-contained Native AOT for `win-x64` and `linux-x64`. Each service is an **independent package directory**. Published outputs must not contain a `VectorNNTPD.NntpServer` assembly.

Local release script output:

```text
release/win-64/
  backfiller/   VectorNNTPD.BackFillerService.exe + BackFiller JSON + RabbitMQ JSON
  storage/      VectorNNTPD.StorageService.exe + Storage JSON + RabbitMQ JSON
  reader/       VectorNNTPD.ReaderService.exe + Reader JSON + RabbitMQ JSON
  transit/      VectorNNTPD.TransitService.exe + Transit JSON + RabbitMQ JSON

release/linux-64/
  backfiller/   executable + JSON + RabbitMQ JSON + vectornntp-backfiller.service + backfiller.env.example + README.md
  storage/      executable + JSON + RabbitMQ JSON + vectornntp-storageservice.service + storageservice.env.example + README.md
  reader/       executable + JSON + RabbitMQ JSON + vectornntp-reader.service + reader.env.example + README.md
  transit/      executable + JSON + RabbitMQ JSON + vectornntp-transit.service + transit.env.example + README.md
```

CI artifact roots use the same package names under `.artifacts/win-x64/` and `.artifacts/linux-x64/`.

Role isolation in the package:

- Reader package contains only Reader executable and `VectorNNTPD.ReaderService.json` (not Transit).
- Transit package contains only Transit executable and `VectorNNTPD.TransitService.json` (not Reader).
- Each package carries its own copy of `VectorNNTPD.RabbitMQ.json`.
- Linux packages include only that role’s systemd unit and environment example.
- Secrets and site credentials are never packaged; use `/etc/vectornntp/*.env` and site-edited JSON / `nntpsharedconfig`.

Startup smoke (configuration bind only; no listeners or brokers):

| Host | Env | Config path env | Checkpoint FQDN (ServerId=1) |
|------|-----|-----------------|------------------------------|
| Reader | `VECTORNNTP_READER_STARTUP_SMOKE=1` | `VECTORNNTP_READER_STARTUP_SMOKE_CONFIG` | `reader01.usenet.ninja` |
| Transit | `VECTORNNTP_TRANSIT_STARTUP_SMOKE=1` | `VECTORNNTP_TRANSIT_STARTUP_SMOKE_CONFIG` | `transit01.usenet.ninja` |

Smoke fixtures: `tests/VectorNNTPD.ReaderService.Tests/Fixtures/startup-smoke-reader.json` and `tests/VectorNNTPD.TransitService.Tests/Fixtures/startup-smoke-transit.json`.

## Deployment handoff

Install each role from its package directory alone. Do not merge Reader and Transit into one install tree.

### 1. Install roots

| Role | Package directory | Install root | Executable |
|------|-------------------|--------------|------------|
| Reader | `reader/` | `/opt/vectornntp/reader` | `VectorNNTPD.ReaderService` |
| Transit | `transit/` | `/opt/vectornntp/transit` | `VectorNNTPD.TransitService` |

BackFiller and StorageService remain `/opt/vectornntp/backfiller` and `/opt/vectornntp/storageservice` respectively (packages `backfiller/` and `storage/`).

### 2. Files that must accompany each executable

In the install root (same directory as the executable):

| File | Reader | Transit |
|------|--------|---------|
| Host executable | required | required |
| `VectorNNTPD.ReaderService.json` | required | absent |
| `VectorNNTPD.TransitService.json` | absent | required |
| `VectorNNTPD.RabbitMQ.json` | required | required |
| `README.md` (linux package) | optional documentation | optional documentation |

Configuration is loaded from `AppContext.BaseDirectory` (the executable directory), not from a shared sibling role.

### 3. Environment files and permissions

| Role | Example in package | Installed path | Owner / mode |
|------|--------------------|----------------|--------------|
| Reader | `reader.env.example` | `/etc/vectornntp/reader.env` | `root:root` `0600` |
| Transit | `transit.env.example` | `/etc/vectornntp/transit.env` | `root:root` `0600` |

Unit files install to `/etc/systemd/system/vectornntp-reader.service` and `/etc/systemd/system/vectornntp-transit.service`. Units use `EnvironmentFile=-/etc/vectornntp/<role>.env` (leading `-` means a missing or unreadable file is ignored by systemd; missing keys still fail host validation).

Create writable runtime directories owned by the service account before first start:

```bash
sudo install -d -o vectornntp -g vectornntp -m 0750 /opt/vectornntp/reader/logs /opt/vectornntp/reader/certs
sudo install -d -o vectornntp -g vectornntp -m 0750 /opt/vectornntp/transit/logs /opt/vectornntp/transit/certs
```

Do not `chown -R` the install tree to `vectornntp`. Keep executables and JSON root-owned and not writable by the service account.

### 4. Site-specific binds, ports, FQDNs, and Transit peers

Mandatory site editing (do not invent production values here):

- `Nntpd:ServerId`, DNS suffix / FQDN host label conventions (`reader` / `transit` prefixes).
- Listener bind addresses and ports in the host JSON (and optional `Nntpd__*` environment overrides).
- Transit peer catalogue and related peer DNS/auth settings for Transit hosts only.
- Cloudflare zone identifiers and related DNS publication settings when ACME/DNS features are enabled for the site.

Edit JSON as root after install, or supply overrides in the role environment file. Never commit real site addresses, peer lists, or zone ids.

### 5. Redis, RabbitMQ, and MySQL

| Dependency | Where configured | Notes |
|------------|------------------|-------|
| MySQL (`ConnectionStrings:NntpDB`) | **Mandatory** in `/etc/vectornntp/<role>.env` as `ConnectionStrings__NntpDB` | Placeholder in the example fails at NntpDB startup check; a missing key fails validation earlier. |
| `NNTPD__XTRACEKEY` | **Mandatory** in the role environment file | 32-byte key as 64 hex or Base64. Never commit the real value. |
| Redis (`Redis` section) | Host JSON / site overrides | Session state, transit peer state, PostFilter quotas/history as enabled for the role. |
| RabbitMQ | `VectorNNTPD.RabbitMQ.json` in the install root; credentials typically from `nntpsharedconfig` | Topology and Article Work. Do not put broker passwords in Git. |
| Cloudflare / ACME | Site policy via shared config / JSON | Certificate issuance and DNS publication when enabled. |

### 6. Schema preparation

Before enabling PostFilter or account art-type features that depend on the checked-in DDL:

1. Apply `docs/schema/postfilter.sql` on the site database when PostFilter tables are required.
2. Apply `docs/schema/nntpusers-account-art-type.sql` when that column/feature is required.
3. Confirm `nntpsharedconfig` and other existing operational tables match the site’s current VectorNNTPD expectations.

### 7. Service startup and health verification

Example Reader install (from the `reader/` package contents; adjust paths to the extracted artifact):

```bash
sudo groupadd --system vectornntp || true
sudo useradd --system --gid vectornntp --home-dir /opt/vectornntp --shell /usr/sbin/nologin vectornntp || true
sudo install -d -o root -g root -m 0755 /opt/vectornntp /opt/vectornntp/reader
sudo install -o root -g root -m 0755 VectorNNTPD.ReaderService /opt/vectornntp/reader/VectorNNTPD.ReaderService
sudo install -o root -g root -m 0644 VectorNNTPD.ReaderService.json VectorNNTPD.RabbitMQ.json README.md /opt/vectornntp/reader/
sudo install -d -o vectornntp -g vectornntp -m 0750 /opt/vectornntp/reader/logs /opt/vectornntp/reader/certs
sudo install -d -o root -g root -m 0755 /etc/vectornntp
sudo install -o root -g root -m 0600 reader.env.example /etc/vectornntp/reader.env
sudo install -o root -g root -m 0644 vectornntp-reader.service /etc/systemd/system/vectornntp-reader.service
sudoedit /etc/vectornntp/reader.env
sudo systemctl daemon-reload
sudo systemd-analyze verify vectornntp-reader.service
sudo stat -c '%U:%G %a' /etc/vectornntp/reader.env   # expect root:root 600
sudo systemctl enable --now vectornntp-reader.service
sudo systemctl status vectornntp-reader.service --no-pager
sudo journalctl -u vectornntp-reader.service -e --no-pager
```

Repeat the same pattern for Transit using the `transit/` package, `/opt/vectornntp/transit`, `transit.env.example` → `/etc/vectornntp/transit.env`, and `vectornntp-transit.service`.

Health checks that do **not** print secrets:

- `systemctl is-active vectornntp-reader.service` / `vectornntp-transit.service`
- journal lines showing host starting and ready/notify as configured
- Listener reachability from an allowed client using site-known ports

Do not run `systemctl show -p Environment` or paste environment files into tickets.

Optional integrations (not required for a minimal start):

- **Spamd**: configure only when PostFilter SpamAssassin path is intentionally enabled for the site.
- **systemd watchdog**: leave `WatchdogSec` commented in the unit until readiness notify behaviour is verified on the host; then enable deliberately.

### 8. Safe rollback

1. Stop the unit: `sudo systemctl stop vectornntp-reader.service` (or transit).
2. Keep the previous release tree (for example rename `/opt/vectornntp/reader` to `/opt/vectornntp/reader.prev` before installing the new package).
3. Restore the previous executable + JSON + RabbitMQ JSON into the install root (or move the previous tree back).
4. Keep `/etc/vectornntp/<role>.env` unless the rollback also requires restoring a prior environment file from a secure backup (never from Git).
5. `sudo systemctl daemon-reload` if the unit file changed, then `sudo systemctl start vectornntp-<role>.service`.
6. Verify status/journal as above.

Rollback does not require reinstalling the opposite role. Reader and Transit packages are independent.

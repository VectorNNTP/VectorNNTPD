# VectorNNTPD

Standalone VectorNNTPD hosts extracted from VectorNNTP.NNTPD. This repository ships BackFiller, StorageService, NNTP ReaderService, NNTP TransitService, and shared `VectorNNTPD.Common`. Reader and Transit each embed their own NNTP server implementation (no shared NntpServer library).

## Structure

- `src/VectorNNTPD.BackFillerService/` - BackFiller worker app (net10.0)
- `src/VectorNNTPD.StorageService/` - StorageService / NNTP TAKETHIS feed host (net10.0)
- `src/VectorNNTPD.ReaderService/` - NNTP reader-server host (`AddNntpReaderHosting`)
- `src/VectorNNTPD.TransitService/` - NNTP transit-server host (`AddNntpTransitHosting`)
- `src/VectorNNTPD.Common/` - shared library
- `tests/VectorNNTPD.BackFillerService.Tests/` / `tests/VectorNNTPD.StorageService.Tests/` / `tests/VectorNNTPD.ReaderService.Tests/` / `tests/VectorNNTPD.TransitService.Tests/` / `tests/VectorNNTPD.Common.Tests/` - xUnit suites
- `docs/nntpd-reader-transit.md` - Reader/Transit architecture and deployment notes
- `VectorNNTPD.slnx` - solution including apps + tests
- `tools/Publish-Release.ps1` - Native AOT release publish for BackFiller, StorageService, ReaderService, and TransitService
- `tools/Invoke-CodeQlAnalysis.ps1` - local traced C# CodeQL analysis

## Documentation

Permanent docs live under [`docs/`](docs/). Start with [`docs/README.md`](docs/README.md).

| Document | Contents |
|---|---|
| [docs/architecture.md](docs/architecture.md) | BackFiller host lifecycle, Article Work, byte-oriented data plane |
| [docs/nntpd-reader-transit.md](docs/nntpd-reader-transit.md) | Reader/Transit roles, packaging, CAPABILITIES vs HELP, deployment |
| [docs/configuration.md](docs/configuration.md) | BackFiller options, RabbitMQ, NntpDB, secrets (BackFiller-focused) |
| [docs/storage.md](docs/storage.md) | StorageService architecture and configuration |
| [docs/storage-nntp-feed.md](docs/storage-nntp-feed.md) | Storage NNTP feed (ARTICLE / TAKETHIS / QUIT) |
| [docs/logging.md](docs/logging.md) | Serilog for BackFiller and StorageService |
| [docs/systemd.md](docs/systemd.md) | systemd notify/watchdog for BackFiller/Storage |
| [docs/common/](docs/common/) | Shared library contracts (ArticleRecord, VATP, Common config) |
| [docs/standards/](docs/standards/) | Local RFC and protocol standards library |
| [docs/schema/](docs/schema/) | Transit / PostFilter / account-art-type SQL (operator-applied; no migrator) |
| [docs/reference/upstream-nntpd/](docs/reference/upstream-nntpd/) | Historical monolith NNTPD notes |

## Build

```powershell
dotnet build .\VectorNNTPD.slnx -c Release -p:Platform=x64
```

## Test

```powershell
dotnet test .\tests\VectorNNTPD.BackFillerService.Tests\VectorNNTPD.BackFillerService.Tests.csproj -c Release -p:Platform=x64
```

## Publish

Preferred release path (one independent package directory per service):

```powershell
.\tools\Publish-Release.ps1 -Platform win-64
.\tools\Publish-Release.ps1 -Platform linux-64
```

Output layout:

```text
release/<platform>/backfiller/
release/<platform>/storage/
release/<platform>/reader/
release/<platform>/transit/
```

Each package contains that host’s Native AOT executable, its primary JSON, and `VectorNNTPD.RabbitMQ.json`. Linux packages also include that role’s systemd unit, environment example, and `README.md`. Reader and Transit never share a package directory. CI writes the same package names under `.artifacts/win-x64/` and `.artifacts/linux-x64/`.

Per-project Native AOT publish still works. Example for StorageService:

```powershell
dotnet publish .\src\VectorNNTPD.StorageService\VectorNNTPD.StorageService.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:PublishAot=true -p:SelfContained=true
```

Production hosts reference the `StuDev.AotAnywhere` 1.0.4 MSBuild SDK. On Windows that SDK links `linux-x64` with the Zig toolchain restored by NuGet. A globalization-enabled Linux host still needs `libicu`.

Native AOT publish keeps trim and AOT analysis enabled. CI runs each published executable against an isolated JSON fixture:

- BackFiller: `VECTORNNTP_BACKFILLER_STARTUP_SMOKE=1` / `VECTORNNTP_BACKFILLER_STARTUP_SMOKE_CONFIG` → checkpoint `BackFiller startup smoke: configuration serverId=1 fqdn=backfiller01.usenet.ninja`
- StorageService: `VECTORNNTP_STORAGESERVICE_STARTUP_SMOKE=1` / `VECTORNNTP_STORAGESERVICE_STARTUP_SMOKE_CONFIG` → checkpoint `StorageService startup smoke: configuration serverId=1 fqdn=cache01.usenet.ninja`
- ReaderService: `VECTORNNTP_READER_STARTUP_SMOKE=1` / `VECTORNNTP_READER_STARTUP_SMOKE_CONFIG` → checkpoint `ReaderService startup smoke: configuration serverId=1 fqdn=reader01.usenet.ninja`
- TransitService: `VECTORNNTP_TRANSIT_STARTUP_SMOKE=1` / `VECTORNNTP_TRANSIT_STARTUP_SMOKE_CONFIG` → checkpoint `TransitService startup smoke: configuration serverId=1 fqdn=transit01.usenet.ninja`

Those checkpoints prove configuration binding and host construction (and StorageService logging initialization). They do not open a listener or contact a database, broker, DNS provider, or certificate authority. StorageService AOT publish still scopes Serilog.Settings.Configuration IL suppressions (`IL2104`/`IL3050`/`IL3000`/`IL3002`) because MinimumLevel is applied via `ReadFrom.Configuration` while sinks are registered explicitly.

BackFiller references `VectorNNTPD.Common` for `IApplicationService`, NntpDB, ACME certificate management, the Cloudflare DNS API client, authoritative DNS queries used for DNS-01, RabbitMQ connectivity, and article parsing and validation. It keeps its own `ApplicationServiceManager` and does not use Common's `ApplicationLifecycle`. BackFiller still owns certificate watchdog health, the decision to publish listener-bound A/AAAA records only after the cache listener binds, Article Work topology, the RabbitMQ hosted adapter, and the retained article image that dot-stuffs body lines after Common's parser accepts the article.

## Host requirements

**Minimum supported physical RAM: 8 GiB.** Intended production hosts are **32–64 GiB**. Startup validation fails when total physical memory cannot be determined or is below the minimum. Article retention capacity is further limited to 80% of detected physical memory (truncated to whole gibibytes; at 8 GiB that ceiling is 6 GiB). See `docs/configuration.md`.

## Linux service

`tools/Publish-Release.ps1 -Platform linux-64` writes per-service packages under `release/linux-64/{backfiller,storage,reader,transit}/`. The Linux CI job writes the same package names under `.artifacts/linux-x64/` and uploads that tree. Each package is independently installable (executable, host JSON, RabbitMQ JSON, that role’s systemd unit and environment example, and `README.md`).

Install BackFiller from `backfiller/` at `/opt/vectornntp/backfiller`. `deploy/systemd/vectornntp-backfiller.service` uses that path for `ExecStart`, `WorkingDirectory`, and `Documentation=`. `ReadWritePaths` lists only `logs/` and `certs/`. Under `ProtectSystem=strict` that re-opens those two directories for writing. It does not change Unix ownership or mode. The shipped JSON writes file logs under `logs/` and ACME state under `certs/`, and startup creates files inside directories that must already exist.

Install StorageService from `storage/` at `/opt/vectornntp/storageservice` using `VectorNNTPD.StorageService`, `VectorNNTPD.StorageService.json`, `VectorNNTPD.RabbitMQ.json`, `vectornntp-storageservice.service`, and `storageservice.env.example`. That unit’s `ReadWritePaths` also include `db/` and `spool/` for the article index and segment-pool roots.

Install ReaderService from `reader/` at `/opt/vectornntp/reader` and TransitService from `transit/` at `/opt/vectornntp/transit`. See `docs/nntpd-reader-transit.md` for the Reader/Transit deployment handoff (environment files, schema, peers, rollback).

Create the account and install the release as root-owned files. Run the following from the `backfiller/` package directory. This step copies the placeholder environment file and reloads systemd. It does not start the service.

```bash
sudo groupadd --system vectornntp || true
sudo useradd --system --gid vectornntp --home-dir /opt/vectornntp --shell /usr/sbin/nologin vectornntp || true
sudo install -d -o root -g root -m 0755 /opt/vectornntp /opt/vectornntp/backfiller
sudo install -o root -g root -m 0755 VectorNNTPD.BackFillerService /opt/vectornntp/backfiller/VectorNNTPD.BackFillerService
sudo install -o root -g root -m 0644 VectorNNTPD.BackFillerService.json VectorNNTPD.RabbitMQ.json README.md /opt/vectornntp/backfiller/
sudo install -d -o vectornntp -g vectornntp -m 0750 /opt/vectornntp/backfiller/logs /opt/vectornntp/backfiller/certs
sudo install -d -o root -g root -m 0755 /etc/vectornntp
sudo install -o root -g root -m 0600 backfiller.env.example /etc/vectornntp/backfiller.env
sudo install -o root -g root -m 0644 vectornntp-backfiller.service /etc/systemd/system/vectornntp-backfiller.service
sudo systemctl daemon-reload
```

Replace every `REPLACE_WITH_` placeholder before the first start. Open the file with a root editor. Do not pass the connection string as a shell argument, do not commit the installed file, and do not leave it mode `0644`. systemd reads the file as root, so `vectornntp` does not need permission to read it. Owner `root:root` and mode `0600` are the installed permissions.

```bash
sudoedit /etc/vectornntp/backfiller.env
```

The placeholder connection string passes MySqlConnector's parser, so startup validation does not reject it. It does not name a database this service can use. The NntpDB startup check then fails and the process exits 1.

The unit directive is `EnvironmentFile=-/etc/vectornntp/backfiller.env`. The leading `-` suppresses errors reading the environment file. Current systemd ignores a missing, unreadable, or invalid file. Older systemd ignored only a missing file. Suppressing that error does not insert a connection string. If `ConnectionStrings__NntpDB` is absent, startup validation fails with `ConnectionStrings:NntpDB must be configured.` and the process exits 1. When that message appears, check that `/etc/vectornntp/backfiller.env` exists, is owned by `root:root`, is mode `0600`, and that its syntax is valid. Do not print the file.

The file is one `KEY=VALUE` per line. A line whose first non-space character is `#` is a comment. systemd parses the file itself. It does not expand variables, command substitutions, or backticks, and `source` does not reproduce that parse. In an unquoted value, a backslash escapes the next character, so `\\` is one backslash, and a backslash at the end of a line joins the next line. Interior spaces remain. Leading and trailing spaces are removed. On systemd 259, single quotes around the whole value keep the enclosed characters verbatim and do not treat a backslash as an escape; a single quote cannot appear inside that pair. Double quotes use a different escape set. Those quote rules are not the same on every systemd version. After editing a value that contains a backslash, a quote, or leading or trailing whitespace, confirm the loaded value on that host without writing it to a log.

The shipped JSON enables TLS (`BindPortTls` 1190). The only environment setting startup validation requires for that configuration is:

```text
ConnectionStrings__NntpDB=Server=REPLACE_WITH_MYSQL_HOST;User ID=REPLACE_WITH_MYSQL_USER;Password=REPLACE_WITH_MYSQL_PASSWORD;Database=REPLACE_WITH_DATABASE_NAME
```

That name has no `VECTOR__` prefix. Generic Host maps it to `ConnectionStrings:NntpDB`.

These names are not required in `/etc/vectornntp/backfiller.env`:

```text
VECTOR__RABBITMQ__USERNAME
VECTOR__RABBITMQ__PASSWORD
VECTOR__CLOUDFLAREAPIKEY
VECTOR__CLOUDFLAREZONEID
VECTOR__ACMEACCOUNT
VECTOR__ACMECERTIFICATEPASSWORD
```

BackFiller discards a bound RabbitMQ username and password, and it clears the ACME account, ACME certificate password, and Cloudflare API key before validation. The root `VECTOR__CLOUDFLAREZONEID` value is not copied onto BackFiller options. After the connection string works, `nntpsharedconfig` supplies those values. Assigning the names above in the environment file does not override that row.

`DOTNET_ENVIRONMENT=Production` is set by the unit. It selects the optional `VectorNNTPD.BackFillerService.Production.json` overlay. A missing overlay does not fail startup.

After the placeholders have been replaced, validate the unit and the file metadata. `stat` prints the owner and mode and does not print the connection string. `daemon-reload` is required after a unit-file change. An environment-file edit is read on the next service start; `daemon-reload` does not reload the environment of a process that is already running.

```bash
sudo systemctl daemon-reload
sudo systemd-analyze verify vectornntp-backfiller.service
sudo stat -c '%U:%G %a' /etc/vectornntp/backfiller.env
```

The expected metadata is `root:root 600`. Start the service only after that check:

```bash
sudo systemctl enable --now vectornntp-backfiller.service
```

Inspect startup without printing secrets:

```bash
sudo systemctl status vectornntp-backfiller.service --no-pager
sudo journalctl -u vectornntp-backfiller.service -e --no-pager
```

Do not run `systemctl show -p Environment`, and do not copy `/etc/vectornntp/backfiller.env` into a ticket or log. A missing file, a missing key, or a blank value produces `ConnectionStrings:NntpDB must be configured.` The same message appears when the leading `-` caused systemd to ignore an unreadable or invalid file. A string MySqlConnector's parser rejects fails in startup validation. The placeholder is accepted by that parser and fails later, at the NntpDB startup check. Correct the file and restart the service. Do not paste the connection string.

Rotate the connection string by editing `/etc/vectornntp/backfiller.env` with a root editor, then `sudo systemctl restart vectornntp-backfiller.service`. Rotate the ACME account, ACME certificate password, Cloudflare API key, Cloudflare zone id, and RabbitMQ username and password in `nntpsharedconfig`, then restart the service so the next snapshot is loaded. Do not put those secrets on a command line, in Git, or in a world-readable file.

`groupadd` and `useradd` may report that the account already exists; that is safe to ignore. Do not `chown -R` the install tree to `vectornntp` and do not use mode `0777`. The executable and JSON stay owned by root and are not writable by the service account. `TimeoutStopSec=60` covers the shipped 30-second shutdown grace; raise it when `BackFiller:Shutdown:GracePeriodSeconds` is raised. `WatchdogSec` stays unset until readiness has been checked on the host.

## CodeQL

Local analysis uses the installed CodeQL CLI. It traces the Release x64 restore and build, then runs the queries selected by `.github/codeql/codeql-config.yml`. That file selects `.github/codeql/csharp-with-alert-suppression.qls`, which imports the `security-extended` and `security-and-quality` suites and adds `AlertSuppression.ql` from the same query pack. The suites themselves do not include that query, and CodeQL CLI 2.27.2 does not record in-source suppressions unless it is analyzed with them. The database, SARIF, and logs are written under `.artifacts/codeql/`, which Git ignores.

A suppression is a `// codeql[rule-id]` comment on the line immediately before the alert, with no other code on that line. A same-line `// codeql[...]` comment is not recognized. A recognized suppression stays in the SARIF with `suppressions.kind` set to `inSource`. Each `cs/catch-of-all-exceptions` suppression is on a boundary that must contain every exception, including types the method cannot enumerate, and the comment states that contract. Generated System.Text.Json and regex sources are not edited and are not suppressed.

`codeql database interpret-results` on CLI 2.27.2 writes `suppressions.kind` as `inSource` and logs `Skipping non-rule, non-metric, non-diagnostic analysis cs/alert-suppression`. The local SARIF from that run still contains those `inSource` entries. The GitHub CodeQL log for the pull-request analysis of merge commit `8fd7de1bed9df4e8d81f3face517c0d2534f1eba` shows the same skip line and `Successfully uploaded results`. That workflow run has no SARIF artifact, and the code-scanning SARIF API returns processing status without the file, so the uploaded SARIF was not compared with the local file. GitHub recorded the analysis with `results_count` 2. The open alerts on `refs/pull/14/merge` are `cs/catch-of-all-exceptions` at `ArticleRetentionAuthority.cs` line 976 and `ArticleWorkSettlementLease.cs` line 115. Both catches on that commit already have this comment on the preceding line. That check did not inventory every default-branch alert. Open `Program.cs` alerts remain from an earlier default-branch analysis and were not results of this upload.

```powershell
.\tools\Invoke-CodeQlAnalysis.ps1
.\tools\Invoke-CodeQlAnalysis.ps1 -CodeQl C:\Tools\codeql\codeql.exe
```

A local SARIF file does not update or clear GitHub code scanning alerts. For a traced C# build, `paths-ignore` in the repository config does not exclude compiled files; the CLI records that limitation while creating the database.

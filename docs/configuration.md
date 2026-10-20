# VectorNNTPD.BackFillerService configuration

This document is the configuration contract for the standalone BackFiller host. Property names below match the current options types. Defaults and ranges below are the values those types and their validators enforce today.

Startup registers options with `ValidateOnStart` in `BackFillerServiceCollectionExtensions`. Invalid mandatory configuration fails startup. Do not log a complete options object: ACME, Cloudflare, RabbitMQ, and NntpDB values include secrets.

`VectorEnvironment` loads environment variables with the prefix `VECTOR__`. After the prefix is removed, `VECTOR__RABBITMQ__USERNAME` binds to `RabbitMQ:Username`. The prefixes `nntpd__` and `backfiller__` are not registered and are not aliases.

## Configuration files

The host content root is the application binary directory. Common `ApplicationJsonConfiguration` removes Generic Host `appsettings.json`, `appsettings.{environment}.json`, and `*.settings.json` sources and loads these files instead:

| File | Contents |
|---|---|
| `VectorNNTPD.BackFillerService.json` | Primary application configuration. The name is `{entry assembly name}.json`. |
| `VectorNNTPD.BackFillerService.{environment}.json` | Optional environment overlay. |
| `VectorNNTPD.RabbitMQ.json` | RabbitMQ connectivity. It is a separate file. Its section name remains `RabbitMQ`. |

The replacement keeps the position of the earliest removed `appsettings` source. When the host registered none, the entry-assembly files are inserted at the start of the source list. Optional and reload-on-change are copied from the matching removed source. When that source was absent, the added file is optional and reloads on change. A second call does not add another copy of the same path. The helper does not change the builder base path and does not create the files.

`VectorNNTPD.RabbitMQ.json` is the only RabbitMQ JSON file BackFiller registers. It is inserted immediately after the entry-assembly JSON sources. Its optional and reload-on-change flags are copied from the last of those sources. A second call with the same filename does nothing. A present `RabbitMq.json` is not read. A missing `VectorNNTPD.RabbitMQ.json` follows the copied optional flag and does not select another filename. Environment variables and command-line arguments remain after the JSON files and override the same keys. A missing optional file does not fail configuration loading. A required primary file that is missing fails with `FileNotFoundException` when the configuration is built. Invalid JSON in a present file fails when that source is read (`InvalidDataException`). Mandatory option validation still runs at startup.

## BackFiller section

Section name: `BackFiller` (`BackFillerOptions.SectionName`).

| Key | Required | Notes |
|---|---|---|
| `ServerId` | Yes | `int?`, range 1–255. Missing and `0` both fail. There is no default. Old key `BackFiller:Id` is not the current key. |
| `DnsSuffix` | No | Binder default `usenet.ninja`. A successful `nntpsharedconfig` projection replaces it. Whitespace fails when the runtime snapshot canonicalizes the suffix. |
| `CloudFlareZoneId` | Shared config | The property remarks say it binds from `BackFiller:CloudFlareZoneId` only. `BackFillerOptionsValidator` does not itself reject an empty value. `NntpSharedConfigurationReader` rejects a blank `cloudflarezoneid` column, and `AcmeCloudflareOptionsValidator` rejects a blank zone id when shared database fields are validated. `ProviderAccountConfigurationService` copies the shared zone id onto `BackFillerOptions`. |
| `BindAddress` | No | Omitted or empty means all interfaces. Explicit addresses must be locally assigned. Wildcard tokens include `*`, `0.0.0.0`, and `::`. |
| `BindPortTls` | Yes | `int?`, range 1–65535. BackFiller is TLS-only. There is no `BackFiller:BindPort`. |
| `AcmeDirectoryUrl` | No | Binder default is the Let's Encrypt staging directory. ACME validation requires an absolute HTTPS URL. A successful `nntpsharedconfig` projection replaces the bound value. |
| `AcmeRenewalThresholdDays` | No | Binder default 30. ACME validation accepts 1–90. A successful `nntpsharedconfig` projection replaces the bound value before renewal uses it. |
| `AcmeStateDir` | No | Default `certs/`. `ApplicationLocalPath` in Common resolves a relative path against the application binary directory and rejects it when that canonical path leaves the directory. Rooted paths stay rooted. |
| `CertificateDirectory` | No | Default `certs`. Old key `DirCerts`. |
| `BackFillerAccountRefreshIntervalSeconds` | No | Default 60. Range 5–3600. This is the NntpDB account poll. It is not the per-account NNTP `DATE` keepalive. |

Generated `Fqdn` is `backfiller{ServerId:00}.{DnsSuffix}`. It is not a configuration key. It is empty when `ServerId` is missing or out of range, or when `DnsSuffix` is empty.

`BackFillerRuntimeOptions` is built from the bindable options at DI construction time, so its FQDN starts as that config-file provisional name. When `ProviderAccountConfigurationService` publishes `nntpsharedconfig`, `Project` sets `BackFillerOptions.DnsSuffix`, `AcmeCloudflareOptions.Fqdn` / `DnsSuffix`, and calls `BackFillerRuntimeOptions.ApplySharedDnsIdentity` so the singleton runtime FQDN, certificate domain names, retention fallback, DI article-parser accessor, and RabbitMQ connection name all use the database-derived identity. Startup smoke that only builds the host (without starting the account service) still reports the provisional FQDN.

`BackFillerAcmeCloudflareOptionsAdapter` copies BackFiller bind and ACME fields onto `AcmeCloudflareOptions` and sets `IncludeNewsHostnameInCertificate` to false. The shared `AcmeCloudflareOptions` type still defaults that flag to true and documents `BindPort` 119 as an NNTPD cleartext port. BackFiller validation does not use `BindPort`.

Nested groups:

- `BackFiller:Logging`
- `BackFiller:ArticleRetention` — `MaximumRetainedPayloadGigabytes` default 4 (must be > 0 and within the physical-memory ceiling), `RetentionTtlSeconds` default 60 and range 1–60, `MaxOpenableRequestIdsPerArticle` default 16 and range 1–256, `SweepIntervalSeconds` default 1 and range 1–60
- `BackFiller:Shutdown` — `GracePeriodSeconds` default 30, range 5–600; `DrainQueuedWork` default true; `FinishActiveArticles` default true
- `BackFiller:Listener` — `TlsHandshakeTimeoutSeconds` default 30, range 1–300, copied into the runtime snapshot and used for the handshake. `IoProgressTimeoutSeconds` default 60, range 1–600, copied and used as the no-progress deadline for one listener read or one non-empty listener write. A completed call starts a new deadline for the next call. The deadline is not restarted for each 32 KiB of one buffer. `MaxActiveConnections` default 1024, minimum 1, and the listener rejects further accepts at that count. `SendBufferBytes` default 2097152 (2 MiB) and `ReceiveBufferBytes` default 1048576 (1 MiB) are copied and applied to each accepted socket before TLS. They are independent. The range for each is 16384 through 16777216. Nagle is disabled on those sockets and is not a setting. The listening socket does not carry these options. At the default connection cap the default buffers are a 3 GiB kernel-buffer ceiling (1024 × 3 MiB), not memory reserved at bind, and not a measured TCP window. The operating system may round, clamp, or double the stored limit. `MaxQueuedFoundPayloadBytes` default 67108864, minimum 1, is copied into the snapshot; the cache listener does not read it. `ParserAccumulationMaxBytes` default 262144, minimum 32768, and `AwaitingReceiptAckTimeoutSeconds` default 30, range 1–300, are validated and are not copied into `BackFillerListenerRuntimeOptions`.
- `BackFiller:Nntp` — `MaxConcurrentSessionEstablishments` default 8, range 1–1024. Zero is invalid. This does not replace per-provider `MaxSessions` from MySQL. `SendBufferBytes` default 1048576 (1 MiB) and `ReceiveBufferBytes` default 2097152 (2 MiB) are the upstream TCP buffer limits. Each is validated from 16384 through 16777216. They are independent of each other and of the 64 KiB managed reader buffer. Nagle is disabled in code. One upstream session at the defaults requests 3 MiB of kernel buffering. Steady-state session count remains the sum of per-provider `MaxSessions`. These buffers do not raise the establishment gate. The effective window still depends on the operating system, autotuning, TLS, and the path.
- `BackFiller:Systemd` — notify and watchdog enablement

## RabbitMQ section

Section name: `RabbitMQ` (`RabbitMqOptions.SectionName`). It is not nested under `BackFiller`.

Username and password are published from `nntpsharedconfig` (`rabbitmqusername`, `rabbitmqpassword`). Bound username and password values are discarded. Environment variables:

```text
VECTOR__RABBITMQ__USERNAME
VECTOR__RABBITMQ__PASSWORD
```

Never commit or log those values. BackFiller discards values bound from those variables before validation. They are not required in the Linux environment file. Other connectivity settings, including `Hosts`, bind from the `RabbitMQ` section. See `RabbitMqOptions` for the current defaults (`WorkRequestMaxPayloadBytes` 1024, `RpcTimeoutSeconds` 30, `ConnectionBlockedTimeoutSeconds` 30, `ChannelLeaseTimeoutSeconds` 60).

## Shared ACME and Cloudflare secrets

These are not BackFiller-section keys. NNTPD, BackFiller, and StorageServer publish them from `nntpsharedconfig`. Environment variables:

```text
VECTOR__CLOUDFLAREAPIKEY
VECTOR__CLOUDFLAREZONEID
VECTOR__ACMEACCOUNT
VECTOR__ACMECERTIFICATEPASSWORD
```

`CloudFlareApiKey` is a secret. BackFiller clears the API key, ACME account, and certificate password bound at startup, and it does not copy the root `VECTOR__CLOUDFLAREZONEID` value onto BackFiller options. Those names are not required in the Linux environment file. Ordinary options validation must not call Cloudflare or mutate DNS. Tests inject NIC data through `ILocalIpAddressAssignee` instead of reading the live host.

`BackFiller:CloudFlareZoneId` is the application key. `VECTOR__CLOUDFLAREZONEID` is the shared environment variable documented on `AcmeCloudflareOptions`. Do not invent an `nntpd__` name for either.

## NntpDB

`NntpDbOptions.SectionName` is `NntpDb`. The connection string name is `ConnectionStrings:NntpDB`. The environment variable is:

```text
ConnectionStrings__NntpDB
```

That name has no `VECTOR__` prefix. Do not log the connection string. The same key is what NNTPD uses. That is shared-domain configuration, not an instruction to build the NNTPD solution.

For the shipped TLS configuration, this is the only environment setting startup validation requires. `TlsOnlyAcmeCloudflareOptionsValidator` requires `BindPortTls` in the range 1–65535, and the shipped JSON already sets that port. ACME and Cloudflare credential validation is not enabled at startup (`validateSharedCredentials: false`).

## Linux environment file

`deploy/systemd/vectornntp-backfiller.service` contains:

```text
EnvironmentFile=-/etc/vectornntp/backfiller.env
```

systemd reads that file as root before it executes the service. The leading `-` suppresses errors reading the environment file. Current systemd ignores a missing, unreadable, or invalid file. Older systemd ignored only a missing file. Suppressing that error does not insert a connection string. If `ConnectionStrings__NntpDB` is absent, `NntpDbOptionsValidator` fails with `ConnectionStrings:NntpDB must be configured.` and the process exits 1. When that message appears, check that the file exists, is owned by `root:root`, is mode `0600`, and that its syntax is valid. Do not print the file.

The file is systemd `KEY=VALUE` syntax. A line whose first non-space character is `#` is a comment. systemd parses the file itself. It does not expand variables, command substitutions, or backticks, and `source` does not reproduce that parse. In an unquoted value, a backslash escapes the next character, so `\\` is one backslash, and a backslash at the end of a line joins the next line. Interior spaces remain. Leading and trailing spaces are removed. On systemd 259, single quotes around the whole value keep the enclosed characters verbatim and do not treat a backslash as an escape; a single quote cannot appear inside that pair. Double quotes use a different escape set. Those quote rules are not the same on every systemd version. After editing a value that contains a backslash, a quote, or leading or trailing whitespace, confirm the loaded value on that host without writing it to a log.

Install it as owner `root:root` and mode `0600`. The service account does not need to read the file. Do not commit the installed file, do not pass the connection string on a command line, and do not make the file world-readable. `deploy/systemd/backfiller.env.example` is the placeholder template. Replace `REPLACE_WITH_MYSQL_HOST`, `REPLACE_WITH_MYSQL_USER`, `REPLACE_WITH_MYSQL_PASSWORD`, and `REPLACE_WITH_DATABASE_NAME` with a root editor before the first start.

These names are not required in `/etc/vectornntp/backfiller.env`:

```text
VECTOR__RABBITMQ__USERNAME
VECTOR__RABBITMQ__PASSWORD
VECTOR__CLOUDFLAREAPIKEY
VECTOR__CLOUDFLAREZONEID
VECTOR__ACMEACCOUNT
VECTOR__ACMECERTIFICATEPASSWORD
```

After the connection string works, `ProviderAccountConfigurationService` copies the ACME account, ACME certificate password, Cloudflare API key, Cloudflare zone id, DNS suffix, and RabbitMQ username and password from `nntpsharedconfig`. Assigning the names above in the environment file does not override that row.

`Environment=DOTNET_ENVIRONMENT=Production` in the unit selects the optional `VectorNNTPD.BackFillerService.Production.json` overlay. A missing overlay does not fail startup. `daemon-reload` is required after a unit-file change. An edit to the environment file is read on the next service start; `daemon-reload` does not reload the environment of a running process. `WatchdogSec` stays commented out.

## Validation behavior that is easy to misread

- Missing `ServerId` is not the same as `0`. Both fail. The property is nullable so those cases stay distinct.
- `BindPortTls` missing, `0`, or outside 1–65535 fails. TLS disabled is not a valid BackFiller mode.
- `nntpd__cloudflareapikey` and `nntpd__CloudFlareZoneId` are not current environment variables.
- Do not log `BackFillerOptions`, `AcmeCloudflareOptions`, or `RabbitMqOptions` as a whole object.
- `AcmeStateDir` and `BackFiller:Logging:File:LogDir` use Common `ApplicationLocalPath`. A relative value, including one that contains `..`, is rejected when its canonical path leaves the application binary directory. A rooted value is an explicit location and is not forced back under that directory. The check normalizes `.` and `..` and does not follow symlinks or junctions.

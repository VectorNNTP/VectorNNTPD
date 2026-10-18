# VectorNNTPD.Common configuration

Applications bind these options. The library validates them and does not read a host-specific JSON section by itself.

## Environment names

`VectorEnvironment` loads variables with the prefix `VECTOR__`. After that prefix is removed, `VECTOR__RABBITMQ__USERNAME` binds to `RabbitMQ:Username`.

NNTPD, BackFiller, and StorageServer publish the ACME account email, ACME certificate password, Cloudflare API key, and RabbitMQ username and password from the single `nntpsharedconfig` row. Those values are not read from `VECTOR__ACMEACCOUNT`, `VECTOR__ACMECERTIFICATEPASSWORD`, `VECTOR__CLOUDFLAREAPIKEY`, or `VECTOR__RABBITMQ__USERNAME`. NNTPD-specific settings stay under the `Nntpd` section and use `NNTPD__SERVERID` and `NNTPD__XTRACEKEY`. The NNTPD Cloudflare zone id is `Nntpd:CloudFlareZoneId`. BackFiller owns `BackFiller:CloudFlareZoneId` and `BackFiller:ServerId` under the `BackFiller` section. There is no `nntpd__` or `backfiller__` alias for those shared secrets.

## JSON files

`ApplicationJsonConfiguration` removes Generic Host `appsettings.json`, `appsettings.{environment}.json`, and `*.settings.json` sources and registers `{entry assembly}.json` then `{entry assembly}.{environment}.json` at the earliest removed `appsettings` position. When the host registered no JSON sources, those files are inserted at the start, optional, and reload on change. Optional and reload-on-change are otherwise copied from the replaced source. A second call does not duplicate a path. The helper does not set the base path.

`AddSharedRabbitMqJsonFile` inserts the caller-supplied filename after those entry-assembly sources and before later sources. The filename is required. The canonical name is `VectorNNTPD.RabbitMQ.json`. The helper does not default to `RabbitMq.json` and does not load that name when the supplied file is missing. Calling it again with the same name does nothing. There is no environment-specific RabbitMQ file. The section name inside the file remains `RabbitMQ`. A missing optional file does not fail the build. Invalid JSON in a present file fails when that source is read.

## RabbitMQ

Top-level `RabbitMQ` section. The Common sample is `src/VectorNNTPD.Common/VectorNNTPD.RabbitMQ.json`. `ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile` loads only the filename the caller passes. BackFiller passes `VectorNNTPD.RabbitMQ.json`. The file is inserted after the entry-assembly JSON files and before environment variables and command-line arguments. Options types and connection validation live in `VectorNNTPD.Common.Messaging.RabbitMq`. Applications bind the section and call `AddRabbitMqInfrastructure()`. Nested `Management` is validated only when `BaseUrl` is set.

NNTPD and StorageServer are not projects in this repository, so their build output is not part of the contract here. BackFiller ships connectivity in `src/VectorNNTPD.BackFillerService/VectorNNTPD.RabbitMQ.json`. Its primary file, `VectorNNTPD.BackFillerService.json`, still must not repeat a top-level `RabbitMQ` section. `BackFiller:Logging:RabbitMQ` is the log sink, not broker connectivity.

`RabbitMqService` owns one process-wide connection, verifies that it is open, and replaces it on connectivity loss while incrementing a monotonic generation. Client automatic recovery is disabled. The AMQP client name comes from `IRabbitMqConnectionNameProvider`. Callers obtain the current connection with `TryGetCurrent` and do not dispose it.

There is no application-level RabbitMQ connection pool. `MinConnections` and `MaxConnections` are validated and are not enforced as a pool.

| Key | Type | Default | Required? | Description |
|-----|------|---------|-----------|-------------|
| `Hosts` | string array | _(none)_ | **yes** | Broker hostnames or IP addresses (no URI scheme, credentials, path, or query) |
| `Port` | int | `5672` | **yes** | AMQP TCP port (`1–65535`) |
| `Username` | string | _(none)_ | **yes** | Broker username. Maximum 45 characters. Never log |
| `Password` | string | _(none)_ | **yes** | Broker password. Maximum 45 characters. Never commit or log |
| `VirtualHost` | string | `/` | **yes** | RabbitMQ virtual host |
| `EnableSsl` | bool | `true` | **yes** | Whether the connection uses TLS |
| `RequestedHeartbeatSeconds` | int | `60` | **yes** | AMQP heartbeat (`0–3600`; `0` disables) |
| `SocketTimeoutSeconds` | int | `30` | **yes** | Socket read/write timeout (`5–600`) |
| `RequestedChannelMax` | int | `2047` | **yes** | Requested channel limit per connection (`1–65535`) |
| `RpcTimeoutSeconds` | int | `30` | **yes** | Client continuation/handshake timeout (`1–3600`) |
| `ConnectionBlockedTimeoutSeconds` | int | `30` | **yes** | Client connection timeout (`5–3600`; must be ≥ `RpcTimeoutSeconds`) |
| `NetworkRecoveryIntervalSeconds` | int | `5` | **yes** | Client recovery-interval setting (`1–3600`; unused while automatic recovery is disabled) |
| `PoolReconnectBaseDelayMs` | int | `250` | **yes** | Application reconnect base delay (`50–60000`) |
| `PoolReconnectMaxDelayMs` | int | `30000` | **yes** | Application reconnect max delay (`50–300000`; ≥ base) |
| `ChannelLeaseTimeoutSeconds` | int | `60` | **yes** | Validated; reserved for later channel work (`1–3600`; ≥ `RpcTimeoutSeconds`) |
| `WorkRequestMaxPayloadBytes` | int | `1024` | **yes** | Maximum admitted Article Work request payload bytes (`1–4096`) |
| `ChannelPoolSize` | int | `512` | **yes** | Validated; reserved for later consumer buffering (`1–8192`) |
| `MinConnections` | int | `4` | **yes** | Validated; reserved (`1–512`; ≤ `MaxConnections`) |
| `MaxConnections` | int | `16` | **yes** | Validated; reserved (`1–512`) |
| `MaxPendingLeaseWaiters` | int | `1024` | **yes** | Validated; reserved (`0–65536`) |
| `ConnectionScaleDownIdleSeconds` | int | `300` | **yes** | Validated; reserved (`30–86400`) |
| `ScaleDownCooldownSeconds` | int | `30` | **yes** | Validated; reserved (`0–3600`) |
| `MinimumConnectionLifetimeSeconds` | int | `300` | **yes** | Validated; reserved (`30–86400`) |
| `PublishConfirmTimeoutSeconds` | int | `10` | **yes** | Publisher-confirm wait (`1–3600`) |
| `MaximumShutdownDrainTimeoutSeconds` | int | `30` | **yes** | Validated; shutdown is cancellation-driven (`1–3600`) |
| `DegradedThreshold` | double | `0.75` | **yes** | Validated; reserved (`>0` and `≤1`) |
| `UnhealthyThreshold` | int | `5` | **yes** | Validated; reserved (`1–120`) |
| `ConsumerPrefetchCount` | ushort | _(none)_ | no | Optional Basic.Qos prefetch (`1–65535`) |
| `DiagnosticPayloadCorrelationId` | string | _(none)_ | no | Optional diagnostic gate |
| `Management:BaseUrl` | string | _(none)_ | when Management is set | Absolute Management HTTP API root (`http`/`https` host and port only) |
| `Management:RequestTimeoutSeconds` | int | `5` | when Management is set | Per-request Management API timeout (`1–60`) |

Hosts that use `nntpsharedconfig` replace `Username` and `Password` from that row and do not read `VECTOR__RABBITMQ__USERNAME` or `VECTOR__RABBITMQ__PASSWORD`. That replacement is application code. The sample file must not contain committed credentials.

## NntpDB

`ConnectionStrings:NntpDB` is the MySQL connection string bound by Common `NntpDbOptions`. `NntpDbService` performs a `SELECT 1` check during startup. There is no in-memory production fallback. MySqlConnector owns physical pooling. Callers open a logical connection, use it, and dispose it.

| Key | Type | Default | Required? | Description |
|-----|------|---------|-----------|-------------|
| `ConnectionStrings:NntpDB` | string | _(none)_ | **yes** | MySQL connection string. Do not log it |
| `NntpDb:StartupTimeout` | `TimeSpan` | `00:00:15` | no | Budget for the startup connect and retry (`> 0`) |

A connection string rejected by `MySqlConnectionStringBuilder`, an authentication failure, and a failed `SELECT 1` fail immediately. Transient connectivity errors may retry until `StartupTimeout` elapses.

## Bind addresses

`IBindAddressResolver` expands a bind-address list into the eligible IP set used for Cloudflare A/AAAA reconciliation. `ILocalIpAddressAssignee` is the NIC probe, so tests stay deterministic. Validation does not open listen sockets.

| Entry | Expansion |
|-------|-----------|
| `*` / `+` | Eligible unicast addresses on local NICs (IPv4 and IPv6) |
| `0.0.0.0` | Eligible local IPv4 addresses only |
| `::` | Eligible local IPv6 addresses only |
| Explicit IP | That address, if it is assigned and eligible |

Eligible addresses may be public or private. They must not be multicast, unspecified, loopback, or link-local. An empty resolved set fails startup. Explicit addresses that are syntactically valid but not assigned to a local NIC are rejected.

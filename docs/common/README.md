# VectorNNTPD.Common

Shared library imported from `VectorNNTPD.Common` into `src/VectorNNTPD.Common`. The assembly and root namespace remain `VectorNNTPD.Common`. Tests live in `tests/VectorNNTPD.Common.Tests` and reference this project.

The library owns reusable implementations. It does not own application topology, host startup order, or a second copy of BackFiller.

## What this library contains

- ACME account, DNS-01, issuance, journal, and certificate publication (`VectorNNTPD.Common.Acme`)
- Cloudflare DNS API client and A/AAAA reconciliation (`VectorNNTPD.Common.Cloudflare`)
- Authoritative DNS queries for DNS-01: NS discovery and UDP/TCP TXT lookup (`VectorNNTPD.Common.Dns`)
- Bind-address expansion and eligibility (`VectorNNTPD.Common.Networking`)
- Options types those hosts adapt into (`VectorNNTPD.Common.Configuration`), including `ApplicationFqdn`, `ServerIdRules`, `VectorEnvironment`, `ApplicationLocalPath`, and `ApplicationJsonConfiguration`. Relative paths must stay inside the application base after `Path.GetFullPath` normalization. Rooted paths stay rooted. The check does not follow symlinks or junctions. The JSON helper removes Generic Host `appsettings` sources and registers `{entry assembly}.json`. The RabbitMQ filename is an explicit argument. BackFiller passes `VectorNNTPD.RabbitMQ.json`.
- NntpDB connection options and MySQL session (`VectorNNTPD.Common.NntpDb`)
- Article content primitives: parser, `ArticleRecord`, `ArticleId`, classification, yEnc validation, and OverviewDB protobuf codec (`VectorNNTPD.Common.Articles`)
- Vector Article Transfer Protocol framing (`VectorNNTPD.Common.Transport.ArticleTransfer`)
- RabbitMQ connectivity: options, connection lifecycle, and channel adapters (`VectorNNTPD.Common.Messaging.RabbitMq`)
- Rolling gzip file logging: `GzipRollingFileSink`, `GzipLogFileCompressor`, `ActiveLogFileWriter`, and `PeriodicFileFlushSink` (`VectorNNTPD.Common.Logging`). BackFiller keeps target selection, level, retention binding, syslog, and RabbitMQ log publishing.
- StorageServer cache-fleet advertisement and lookup wire contracts (`VectorNNTPD.Common.Messaging.Cache`). BackFiller does not declare or consume those exchanges. Article Work checks listener FQDN and port with `VatpEndpointFields`.

RabbitMQ exchange, queue, and binding declaration stays with the application. Article Work and Management HTTP stay with the application. Common does not declare them.

## Documents migrated with the library

| Destination | Source in VectorNNTP |
|---|---|
| [ArticleRecord.md](ArticleRecord.md) | `docs/ArticleRecord.md` |
| [vatp.md](vatp.md) | `docs/vatp.md` |
| [architecture.md](architecture.md) | Common-owned passages in `docs/architecture.md` |
| [configuration.md](configuration.md) | Common-owned passages in `docs/configuration.md` |

Paths inside the copied articles point at `src/VectorNNTPD.Common` and `tests/VectorNNTPD.Common.Tests`. References to NNTPD or StorageServer projects name those repositories; they are not projects in BackFiller.

The yEnc fixture notes shipped with the tests remain at `tests/VectorNNTPD.Common.Tests/Fixtures/SabctoolsYEnc/README.md`.

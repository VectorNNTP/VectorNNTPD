# VectorNNTPD documentation

Permanent documentation for the **VectorNNTPD** solution in `VectorNNTPD.slnx`:

- `BackFillerService`
- `StorageService`
- `ReaderService`
- `TransitService`
- `Common` (+ corresponding test projects)

## Start here

| Document | Audience | Contents |
|---|---|---|
| [../README.md](../README.md) | Operators / developers | Build, test, publish, Linux install overview |
| [nntpd-reader-transit.md](nntpd-reader-transit.md) | Operators / developers | Reader/Transit roles, packaging, CAPABILITIES vs HELP, deployment |
| [architecture.md](architecture.md) | Developers | BackFiller host lifecycle, Article Work, byte-oriented data plane |
| [configuration.md](configuration.md) | Operators / developers | **BackFiller** options, RabbitMQ, NntpDB, secrets |
| [storage.md](storage.md) | Operators / developers | **StorageService** architecture, configuration, persistence, messaging |
| [storage-nntp-feed.md](storage-nntp-feed.md) | Developers / integrators | Storage NNTP feed: ARTICLE / TAKETHIS / QUIT |
| [logging.md](logging.md) | Operators / developers | Serilog for BackFiller and StorageService (Reader/Transit use host Serilog sections) |
| [systemd.md](systemd.md) | Operators | systemd notify/watchdog (BackFiller/Storage; Reader/Transit units in deploy + nntpd-reader-transit) |

## Shared library

| Document | Contents |
|---|---|
| [common/README.md](common/README.md) | What Common owns vs application hosts |
| [common/architecture.md](common/architecture.md) | Common boundaries |
| [common/configuration.md](common/configuration.md) | Shared configuration helpers |
| [common/ArticleRecord.md](common/ArticleRecord.md) | Canonical article envelope |
| [common/vatp.md](common/vatp.md) | Vector Article Transfer Protocol |
| [common/systemd-host-abstraction.md](common/systemd-host-abstraction.md) | **Proposal only** — Storage vs BackFiller systemd stacks |

## Common ownership vs Reader/Transit duplication

`VectorNNTPD.Common` holds genuinely shared infrastructure (ACME, Cloudflare, NntpDB sessions, article parse/`ArticleRecord`, RabbitMQ connection lifecycle, gzip logging sinks, cache-fleet wire contracts). It is **not** an NNTP server library.

ReaderService and TransitService each embed a full NNTP server implementation (session, commands, listeners, PostFilter, Redis, ingestion, …). ~527 corresponding source files differ only by namespace (`VectorNNTPD.ReaderService` vs `VectorNNTPD.TransitService`); a small set differs by role (hosting registration, CAPABILITIES, MODE gating). That duplication is intentional so neither host depends on the other and Common does not become a relocated monolith. There is **no** `VectorNNTPD.NntpServer` project.

## Standards and schema

| Path | Contents |
|---|---|
| [standards/rfcs/](standards/rfcs/) | Local NNTP / Usenet RFC text library |
| [standards/spamd/](standards/spamd/) | SPAMC/SPAMD protocol text (Reader PostFilter) |
| [standards/haproxy/](standards/haproxy/) | PROXY protocol reference |
| [standards/pgpverify/](standards/pgpverify/) | INN pgpverify format notes |
| [schema/](schema/) | NntpDB SQL for Transit / PostFilter / `account_art_type` — see [schema/README.md](schema/README.md) |

## Historical / upstream NNTPD reference

| Document | Topic |
|---|---|
| [reference/upstream-nntpd/commands.md](reference/upstream-nntpd/commands.md) | Monolith NNTPD command inventory (provenance) |
| [reference/upstream-nntpd/postfilter.md](reference/upstream-nntpd/postfilter.md) | PostFilter operator guide (applies to Reader PostFilter behaviour) |

For live VectorNNTPD wire behaviour prefer [nntpd-reader-transit.md](nntpd-reader-transit.md), [storage-nntp-feed.md](storage-nntp-feed.md), and [architecture.md](architecture.md).

## Agent and contribution docs (repository root)

| Document | Contents |
|---|---|
| [../AGENTS.md](../AGENTS.md) | Cursor agent entry point |
| [../CONTRIBUTING.md](../CONTRIBUTING.md) | Contribution guidance |
| [../SECURITY.md](../SECURITY.md) | Security reporting |
| [../CODE_OF_CONDUCT.md](../CODE_OF_CONDUCT.md) | Conduct |

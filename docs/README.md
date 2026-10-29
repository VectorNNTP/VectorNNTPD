# VectorNNTPD documentation

This directory is the permanent documentation for the **VectorNNTPD** solution currently in `VectorNNTPD.slnx`: BackFillerService, StorageService, and Common (plus their tests).

ReaderService and TransitService may appear on a development machine as untracked working-tree directories; they are **not** part of the solution on `master` and are not documented here as shipping hosts.

## Start here

| Document | Audience | Contents |
|---|---|---|
| [../README.md](../README.md) | Operators / developers | Build, test, publish, Linux install overview |
| [architecture.md](architecture.md) | Developers | BackFiller host lifecycle, Article Work, NNTP client/listener roles, silent-consumer monitoring, byte-oriented data plane |
| [configuration.md](configuration.md) | Operators / developers | **BackFiller only** — options, JSON locations, RabbitMQ, NntpDB, secrets |
| [storage.md](storage.md) | Operators / developers | **StorageService** architecture, configuration, persistence, messaging, ops |
| [storage-nntp-feed.md](storage-nntp-feed.md) | Developers / integrators | Storage NNTP feed: ARTICLE / TAKETHIS / QUIT only |
| [logging.md](logging.md) | Operators / developers | Serilog-only logging for BackFiller and StorageService |
| [systemd.md](systemd.md) | Operators | systemd notify/watchdog install and operations for both hosts |

Redis / PostFilter / Transit DDL under [schema/](schema/) are **not** Storage or BackFiller startup requirements.

## Shared library

| Document | Contents |
|---|---|
| [common/README.md](common/README.md) | What Common owns vs application hosts |
| [common/architecture.md](common/architecture.md) | Common boundaries |
| [common/configuration.md](common/configuration.md) | Shared configuration helpers |
| [common/ArticleRecord.md](common/ArticleRecord.md) | Canonical article envelope |
| [common/vatp.md](common/vatp.md) | Vector Article Transfer Protocol |
| [common/systemd-host-abstraction.md](common/systemd-host-abstraction.md) | **Proposal only** — comparing Storage vs BackFiller systemd stacks |

## Standards and schema

| Path | Contents |
|---|---|
| [standards/rfcs/](standards/rfcs/) | Local NNTP / Usenet RFC text library (authoritative for protocol work in this repo) |
| [standards/spamd/](standards/spamd/) | SPAMC/SPAMD protocol text — **upstream PostFilter interoperability**; not a VectorNNTPD host feature |
| [standards/haproxy/](standards/haproxy/) | PROXY protocol text — standards library only; not an implementation claim for BackFiller/Storage |
| [standards/pgpverify/](standards/pgpverify/) | INN pgpverify format notes — interoperability reference; BackFiller does not implement PGPVERIFY |
| [schema/](schema/) | **Upstream-only** Transit / PostFilter / `account_art_type` SQL — see [schema/README.md](schema/README.md) |

## Upstream NNTPD reference (not hosts in this solution)

Material from `VectorNNTP.NNTPD` for engineers who need command or PostFilter context when
working beside an NNTPD deployment. Paths such as `src/VectorNNTP.NNTPD/...` refer to the
**upstream** repository. They are **not** an implementation guarantee for
`VectorNNTPD.BackFillerService` or `VectorNNTPD.StorageService`.

For what this solution actually speaks on the wire, use [architecture.md](architecture.md)
(BackFiller upstream client + cache listener) and [storage-nntp-feed.md](storage-nntp-feed.md)
(Storage ARTICLE / TAKETHIS / QUIT). Do not treat the upstream command checklist as Storage or
BackFiller CAPABILITIES.

Upstream Redis and PostFilter configuration are **not** required to operate VectorNNTPD hosts.

| Document | Topic |
|---|---|
| [reference/upstream-nntpd/commands.md](reference/upstream-nntpd/commands.md) | Upstream NNTPD command inventory |
| [reference/upstream-nntpd/postfilter.md](reference/upstream-nntpd/postfilter.md) | Upstream PostFilter operator guide |

## Agent and contribution docs (repository root)

| Document | Contents |
|---|---|
| [../AGENTS.md](../AGENTS.md) | Cursor agent entry point |
| [../CONTRIBUTING.md](../CONTRIBUTING.md) | Contribution guidance |
| [../SECURITY.md](../SECURITY.md) | Security reporting |
| [../CODE_OF_CONDUCT.md](../CODE_OF_CONDUCT.md) | Conduct |

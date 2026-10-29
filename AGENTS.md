# VectorNNTPD.BackFillerService agent instructions

Concise routing for Cursor agents (including JetBrains Rider ACP). This file is an entry point; it does not prove that Rider ACP automatically loads it.

This repository is **VectorNNTPD**: BackFillerService, StorageService, NNTP ReaderService, NNTP TransitService, and `VectorNNTPD.Common` (Reader/Transit embed duplicated NNTP server code; there is no `VectorNNTPD.NntpServer` library). It was extracted from VectorNNTP.NNTPD. NNTPD names that remain in code comments or shared configuration are provenance or shared-domain context. Reader/Transit hosting is documented in [`docs/nntpd-reader-transit.md`](docs/nntpd-reader-transit.md).

## Before substantive work

1. Read the engineering skill index: [`.cursor/skills/README.md`](.cursor/skills/README.md).
2. Identify the task domain and **explicitly read** the relevant [`.cursor/skills/<skill-name>/SKILL.md`](.cursor/skills/) file(s) before implementing, reviewing, testing, or documenting.
3. Combine skills when appropriate (do not default to a single skill when several apply).
4. Follow the core engineering rules in [`.cursor/rules/vectornntp-core.mdc`](.cursor/rules/vectornntp-core.mdc). Protocol data remains byte-oriented throughout the data plane ([`docs/architecture.md`](docs/architecture.md#byte-oriented-protocol-data-plane)). Application logging prefers source-generated structured methods ([`docs/architecture.md`](docs/architecture.md#source-generated-structured-logging)). Host lifecycle, Article Work settlement, and the NNTP client subset are in [`docs/architecture.md`](docs/architecture.md).
5. Follow the placement policy in [`.cursor/rules/repository-artifacts.mdc`](.cursor/rules/repository-artifacts.mdc): production in `src/VectorNNTPD.BackFillerService/` and `src/VectorNNTPD.Common/`; tests in `tests/VectorNNTPD.BackFillerService.Tests/` and `tests/VectorNNTPD.Common.Tests/`; permanent docs only in `docs/`; local evidence, release staging, and CI publish output in `.artifacts/`. That directory is gitignored. Do not treat it as a committed index. Builds are x64 only (`-p:Platform=x64`); output is `bin/x64/<Configuration>/`.

## Discovery honesty

- Treat the skill index as a **routing guide**, not proof that Cursor automatically loaded or invoked a skill.
- Never claim a rule or skill was automatically loaded unless that was **directly verified in the current session**.
- If a required instruction or skill file is missing or unreadable, state that and continue only where safe.

## Skill map (see index for detail)

| Skill | Path |
|-------|------|
| `code-review` | `.cursor/skills/code-review/SKILL.md` |
| `testing` | `.cursor/skills/testing/SKILL.md` |
| `documentation` | `.cursor/skills/documentation/SKILL.md` |
| `performance-engineering` | `.cursor/skills/performance-engineering/SKILL.md` |
| `nntp-protocol` | `.cursor/skills/nntp-protocol/SKILL.md` |
| `lifecycle-and-concurrency` | `.cursor/skills/lifecycle-and-concurrency/SKILL.md` |
| `configuration-and-security` | `.cursor/skills/configuration-and-security/SKILL.md` |
| `implementation-and-verification` | `.cursor/skills/implementation-and-verification/SKILL.md` |

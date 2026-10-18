---
name: configuration-and-security
description: >-
  Safely implements VectorNNTP configuration binding, validation, secrets handling,
  bind-address NIC checks, and Cloudflare environment variables. Use when changing
  BackFillerOptions, validators, VectorNNTPD.BackFillerService.json, VectorNNTPD.RabbitMQ.json, env vars, secrets, or configuration docs/tests.
---

# Configuration and security

## Purpose

Keep configuration correct, fail-fast on mandatory errors, and never leak secrets.

## When to use

- Options / `{entry assembly name}.json` / env var changes
- Validation, bind addresses, ServerId/FQDN, Cloudflare settings
- Secret handling in logs, tests, or docs

## Authoritative contract

Inspect current sources before editing:

- `docs/configuration.md`
- `BackFillerOptions`, `BackFillerOptionsValidator`
- `AcmeCloudflareOptions`, `RabbitMqOptions`, `NntpDbOptions`
- `BackFillerServiceCollectionExtensions` (`ValidateOnStart`)
- `ILocalIpAddressAssignee` for deterministic NIC checks

Do not hardcode obsolete snake_case keys, `nntpd__` names, or `backfiller__` names. `VectorEnvironment` does not register those prefixes as aliases.

## Current naming (verify in repo)

PascalCase settings under section `BackFiller`, including `ServerId`, `BindAddress`, `BindPortTls`, `DnsSuffix`, and `CloudFlareZoneId`. There is no `BackFiller:BindPort`. Generated `Fqdn` is `backfiller{ServerId:00}.{DnsSuffix}` and is not independently configurable.

RabbitMQ connectivity other than username and password binds from section `RabbitMQ`. Username and password are published from `nntpsharedconfig`.

Shared secrets use the `VECTOR__` prefix. Exact names include:

```text
VECTOR__CLOUDFLAREAPIKEY
VECTOR__CLOUDFLAREZONEID
VECTOR__RABBITMQ__USERNAME
VECTOR__RABBITMQ__PASSWORD
VECTOR__ACMEACCOUNT
VECTOR__ACMECERTIFICATEPASSWORD
```

The NntpDB connection string environment variable is `ConnectionStrings__NntpDB` (no `VECTOR__` prefix).

## Mandatory workflow

1. Read existing options registration and validation path.
2. Distinguish **defaults** vs **required** settings; preserve missing-vs-zero distinctions (e.g. `ServerId` as `int?`).
3. Ensure invalid mandatory config fails **startup** clearly (`ValidateOnStart` / `IValidateOptions`).
4. Keep secrets out of `{entry assembly name}.json`, tracked samples, logs, exceptions, and test output.
5. Validate bind addresses: wildcards vs explicit IPs assigned to local NICs; no `IsGlobal` requirement.
6. Do not call Cloudflare or mutate DNS during ordinary validation.
7. Add deterministic validation tests with injected NIC data.
8. Update `docs/configuration.md` when the contract changes.

## Avoid

- Logging entire options objects that contain API keys
- Inventing restrictive API-key format validators
- Silent defaults for required settings
- Retaining undocumented obsolete aliases
- Committing `.env` files with secrets

## Related skills

- `testing` for deterministic options tests
- `documentation` for config docs accuracy

## Validation checklist

- [ ] Current contract inspected (not memorized)
- [ ] Required vs default behavior correct
- [ ] Startup validation fails clearly without secret leakage
- [ ] Env var names match documented exact strings
- [ ] Tests deterministic; no live Cloudflare
- [ ] Docs updated if behavior changed

# Systemd host abstraction (design proposal)

**Status:** proposal only — not implemented. Readiness semantics differ between hosts; do not extract a shared library until those contracts are agreed explicitly.

This note compares the systemd stacks in `VectorNNTPD.StorageService` and `VectorNNTPD.BackFillerService` after the StorageService merge. It identifies genuine duplication, blocking differences, and the smallest Common surface that could support both hosts without accidental behaviour change.

## Current locations

| Concern | StorageService | BackFiller |
|---------|----------------|------------|
| Lifecycle notifier | `Hosting/Systemd/SystemdLifecycleNotifier.cs` | `Hosting/Systemd/SystemdLifecycleNotifier.cs` |
| Notify bridge | `SystemdNotifyBridge` | `SystemdNotifyBridge` |
| Runtime / env detection | `SystemdRuntime` | `SystemdRuntime` |
| Watchdog interval math | `SystemdWatchdogInterval` | `SystemdWatchdogInterval` |
| Watchdog loop | `SystemdWatchdogService` | `SystemdWatchdogService` |
| Health gate | `ApplicationHealth` / `IApplicationHealth` | `BackFillerApplicationHealth` / `IApplicationHealth` |

## Genuinely duplicated functionality

These pieces are near-copies and are the safe candidates for a shared kit **after** contracts converge:

1. **`ISystemdNotifyBridge` + `SystemdNotifyBridge`** — wrap `ISystemdNotifier` for `READY=1`, `STOPPING=1`, `STATUS=`, `WATCHDOG=1`.
2. **`ISystemdRuntime` + `SystemdRuntime`** — Linux / systemd-service detection and `WATCHDOG_USEC` parsing.
3. **`SystemdWatchdogInterval.Calculate`** — fraction of the systemd deadline with a 1 ms floor and 1 ms margin before the deadline.
4. **Watchdog keep-alive loop shape** — `BackgroundService` + `PeriodicTimer`, skip ticks when unhealthy, link host stopping.

## Blocking readiness and failure differences

Do not merge the notifiers until these are decided:

| Topic | StorageService | BackFiller |
|-------|----------------|------------|
| Ready trigger | `ApplicationLifecycle` transition to `ApplicationState.Running` | `IHostApplicationLifetime.ApplicationStarted` |
| Ready retry | None — one attempt gated by `Interlocked` | Retries on unhealthy or handled notify failure (`ReadyRetryInterval` = 1 s) until success, shutdown, or dispose |
| Ready options | `NotifyReadyOnApplicationRunning` | Options under `BackFiller:Systemd` (ready tied to ApplicationStarted) |
| Stopping | On `Stopping` / `Stopped` lifecycle states | On host `ApplicationStopping` |
| Notify failure | **Rethrows** after logging | **Logged and swallowed** (broken notify must not crash the process) |
| Watchdog health | `IApplicationHealth.IsHealthyForWatchdog` | Same interface; richer sticky unhealthy (unexpected service termination, TLS material expiry) |
| Interval validation | Positive timeout accepted | Requires at least 2 ms (`MinimumTimeout`) |

Storage readiness is application-service completion. BackFiller readiness is Generic Host started, with retry until watchdog health and notify succeed. Unifying without an explicit product decision would change when systemd considers the unit ready and whether a notify socket fault fails the process.

## What could safely live in Common

Smallest shared abstraction that preserves host-specific policy:

```
VectorNNTPD.Common.Hosting.Systemd
  ISystemdNotifyBridge / SystemdNotifyBridge   // failure policy injected or strategy
  ISystemdRuntime / SystemdRuntime
  SystemdWatchdogInterval
  (optional) SystemdWatchdogServiceBase       // interval + loop; health + options from host
```

Hosts would keep:

- Their own `SystemdLifecycleNotifier` (or a thin adapter) wired to either `ApplicationLifecycle` or `IHostApplicationLifetime`.
- Host-specific `IApplicationHealth` implementations and options sections.
- Ready-retry policy (BackFiller) versus single-shot ready (Storage), unless both deliberately adopt one model.

## Required host changes (when approved)

1. Agree failure policy for notify (rethrow vs swallow) and document it once in Common.
2. Agree ready signal source (`ApplicationLifecycle.Running` vs `ApplicationStarted`) and whether retry is required for Storage.
3. Move bridge / runtime / interval (and optionally the watchdog loop) into Common; leave notifiers in each host or parameterise a shared notifier with a readiness strategy interface.
4. Point both hosts’ DI registration at the Common types; delete the duplicate host copies.
5. Align `SystemdWatchdogInterval` minimum-timeout rules before sharing.

## Failure modes and platform notes

- Outside Linux or without `NOTIFY_SOCKET`, bridges no-op when the notifier is disabled.
- Watchdog activates only when `WATCHDOG_USEC` is present and options enable it.
- BackFiller’s swallowed notify failures mean missing `READY=1` / keep-alives can still trip systemd timeouts without crashing the process.
- Storage’s rethrowing bridge can fail startup or watchdog ticks if notify throws; that must not be silently changed to BackFiller’s swallow policy.

## Test coverage needed before extraction

- Existing BackFiller tests under `tests/VectorNNTPD.BackFillerService.Tests/Hosting/Systemd/` (notifier, bridge, runtime, interval, watchdog, host integration).
- Equivalent StorageService lifecycle notifier / watchdog tests for `ApplicationLifecycle`-driven ready and for notify failure propagation.
- After extraction: Common unit tests for interval math and bridge behaviour; both hosts keep integration tests proving their readiness contract unchanged (byte-for-byte timing is not required; observable ready / stopping / retry / swallow-or-throw must match today’s host).

## Explicit non-goals for a first extraction

- Do not move `ApplicationLifecycle` or BackFiller’s host-lifetime readiness into each other as a side effect.
- Do not merge `BackFillerApplicationHealth` certificate / termination sticky state into Storage’s simpler health without a design pass.
- Do not change production ready timing as part of a mechanical file move.

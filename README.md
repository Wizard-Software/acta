# Acta

**Acta** is an Event Sourcing library for **.NET 10**. It provides durable,
append-only event storage, projections, and multi-pod coordination backed by
database guarantees — so multiple application instances can safely share one
event store without a separate coordinator.

> **Status:** early scaffold — **Phase 0 (Bootstrap)**. The public API and
> implementation are being built out across the roadmap. This repository
> currently contains the solution skeleton, supply-chain hardening, and the CI
> security gates.

## What it does

- **Event store** — an append-only log with two interchangeable backends: an
  in-memory backend for fast tests and a **PostgreSQL** backend for production.
- **Aggregates & repositories** — load/replay aggregates from their event
  streams, enforcing the `Owner.Target` ownership invariant.
- **Projections** — build read models both inline and via an asynchronous
  projection daemon.
- **Snapshots & correlation** — bounded replay via snapshots and end-to-end
  correlation of events.
- **Multi-pod coordination** — N application pods plus PostgreSQL, using the
  database's own guarantees for ordering and coordination; a hexagonal
  (ports & adapters) design keeps the core independent of the backend.

The feature set is layered in tiers: event model & serialization → event store
→ aggregates → projections → snapshots → upcasting, outbox, idempotency and
observability → crypto-shredding and multi-tenancy.

## Packages

| Package | Purpose |
|---|---|
| `Acta.Abstractions` | Public ports and contracts (10 port groups) |
| `Acta` | Core implementation |
| `Acta.Postgres` | PostgreSQL backend adapter |
| `Acta.Testing` | Testing helpers for consumers |

## Tech stack

- **Target framework:** `net10.0` · **Language:** C# 14 · **SDK:** 10.0.301
- **Testing:** xUnit v3, Testcontainers, Stryker.NET (≥ 80% mutation score on
  `src/Acta`), CsCheck, BenchmarkDotNet

## Build

```bash
dotnet build Acta.slnx
```

## CI & security gates

Every push and pull request runs the full gate set: build + tests, architecture
tests, mutation testing (Stryker.NET), SAST (CodeQL + Roslyn security
analyzers), dependency scanning (plus Dependabot), and secret scanning with
push protection. Supply-chain integrity is enforced from the first commit via a
pinned NuGet source and a committed `packages.lock.json` lock file
(`RestoreLockedMode` in CI).

### Mutation testing

```bash
dotnet tool restore

# What CI runs. On a PR the >=80% threshold applies to changed code only.
dotnet stryker
dotnet stryker --since:origin/main

# Local, with per-test coverage analysis: reports NoCoverage separately from
# Survived, so you can see which code no test touches at all.
dotnet stryker -f stryker-config.coverage.json
```

`stryker-config.json` sets `"coverage-analysis": "off"`, which makes Stryker run
the whole test suite against every mutant instead of trusting its per-test
coverage map. This is the stricter setting, not the looser one: a mutant that no
test kills is still reported as `Survived` and still counts against the 80%
threshold. It is required because Stryker 4.15.0's per-test coverage capture
returns a near-empty map under `--since` on the MTP runner (which xUnit v3
requires), marking mutants `NoCoverage` that the suite demonstrably kills — a
combination that pins the diff-scoped score near 8% regardless of test quality.
The suite runs in well under a second, so the cost of `off` is a few minutes.

Use `stryker-config.coverage.json` when you want the `NoCoverage` vs `Survived`
distinction back — it is the only thing `off` gives up. Run it without `--since`;
per-test coverage capture is reliable on a full run.

## License

[MIT](LICENSE) © 2026 Wizard Software (Artur Sawicki)

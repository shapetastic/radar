# Task: Postgres persistence integration tests (Testcontainers, Docker-gated)

## Overview

Adds integration tests that run the Postgres repositories (specs 26–28) against a **real** PostgreSQL
instance via Testcontainers, asserting the settled semantics hold against the database — **AD-1** (evidence
duplicate-hash rejection / insert-only; upsert-by-Id last-write-wins elsewhere) and **AD-3** (the
deterministic ordering of every collection query, plus the inclusive `GetObservedBetween` window).

**Critical gating requirement.** These tests must be **skippable when Docker/Postgres is unavailable** so
the existing `dotnet test Radar.sln` stays green on a machine without a database (e.g. CI without Docker).
They live in a **new dedicated test project** so the Testcontainers/Npgsql test dependencies do not leak
into the existing in-process `Radar.IntegrationTests` project, and each test **skips** (does not fail) when
the container cannot start.

---

## Assignment

Worktree: pending
Dependencies: 26 (schema/runner/Dapper config), 27 (evidence/company repos), 28 (signal/score/report repos)
Conflicts with: Edits `Radar.sln` (adds one project) — coordinate if another slice adds a project
concurrently; otherwise standalone.
Estimated time: ~1-2 hours

---

## Project structure changes

```text
tests/Radar.Persistence.IntegrationTests/        # NEW project (added to Radar.sln)
  Radar.Persistence.IntegrationTests.csproj
  PostgresFixture.cs                             # Testcontainers lifecycle + Docker-availability probe
  DockerRequiredCollection.cs                    # xUnit collection sharing the fixture
  EvidenceRepositoryPostgresTests.cs             # AD-1 immutability + AD-3 ordering
  CompanyRepositoryPostgresTests.cs              # upsert-by-Id + alias ordering
  SignalRepositoryPostgresTests.cs               # upsert + observed-window inclusivity + ordering
  ScoreRepositoryPostgresTests.cs                # snapshot/link upsert + ordering + provenance round-trip
  ReportRepositoryPostgresTests.cs               # report+items atomic write + rank ordering
```

### csproj

Reference `Radar.Infrastructure`, `Radar.Application`, `Radar.Domain`, `Radar.TestSupport`, the standard
xUnit stack, plus:

```xml
<PackageReference Include="Testcontainers.PostgreSql" Version="4.6.0" />
<PackageReference Include="Npgsql" Version="10.0.0" />            <!-- match Infrastructure -->
<PackageReference Include="Xunit.SkippableFact" Version="1.5.23" />
```

(xUnit in this repo is 2.9.x, which has no built-in dynamic skip; `Xunit.SkippableFact` provides
`[SkippableFact]` + `Skip.If(...)`. If the repo has already moved to xUnit v3, use its native
`Assert.Skip(...)` instead and drop the package.)

---

## Implementation details

### `PostgresFixture` (Docker-availability probe + container lifecycle)

`IAsyncLifetime` shared via an xUnit collection. On `InitializeAsync`:

1. Attempt to build and `StartAsync` a `PostgreSqlContainer` (e.g. `postgres:16-alpine`).
2. If it starts: expose `Available = true` and `ConnectionString`; build an `NpgsqlDataSource` +
   `NpgsqlConnectionFactory`; call `PostgresDapperConfiguration.EnsureConfigured()`; run
   `PostgresSchemaInitializer.InitializeAsync` once so the schema exists.
3. If the start throws (Docker not installed / daemon down): **catch**, set `Available = false`, and store
   the reason. Do **not** rethrow — an unavailable Docker must not fail the run.

`DisposeAsync` stops/disposes the container if it started.

Each test begins with `Skip.IfNot(_fixture.Available, "Docker/Postgres unavailable")` (or
`Assert.Skip` on xUnit v3) so the whole suite reports as **skipped**, not failed, without Docker.

> Isolation: either truncate the relevant tables at the start of each test, or generate fresh `Guid` ids /
> unique `content_hash` values per test so cases don't collide on the shared container. State the chosen
> approach in the fixture.

### Test cases (against the real database)

**Evidence (AD-1 immutability):**
- `AddIfNewAsync` returns `true` for a new item and the row is readable by id and by content hash.
- A second `AddIfNewAsync` with the **same `ContentHash`** (different `Id`, different `Title`) returns
  `false`, and `GetByContentHashAsync` still returns the **original** item (no overwrite).
- `GetAllAsync` returns evidence ordered by `collected_at_utc` then `id` (AD-3) — seed out-of-order, with
  a `collected_at_utc` tie to prove the `id` tiebreaker.

**Company:**
- `AddAsync` twice with the same `Id` and a changed `Name` → `GetByIdAsync` returns the **second**
  (last-write-wins, AD-1).
- `GetAllAsync` ordered by `created_at_utc, id`; `GetAliasesAsync` ordered by `created_at_utc, id`.

**Signal:**
- Upsert-by-Id last-write-wins; `GetByCompanyAsync` filters by company and orders by `observed_at_utc, id`.
- `GetObservedBetweenAsync` is **inclusive on both bounds**: a signal at exactly `start` and one at
  exactly `end` are both returned; one just outside is not (AD-3 / AD-6 boundary).
- A signal with `CompanyId == null` round-trips as `NULL`.

**Score:**
- Snapshot + link upsert-by-Id; `GetSnapshotsForCompanyAsync` ordered by `created_at_utc, id`;
  `GetLinksForSnapshotAsync` ordered by `id`.
- Provenance round-trip: a persisted `ScoreEvidenceLink` returns the same `SignalId`/`EvidenceId` it was
  written with (evidence → signal → score chain intact).

**Report:**
- `AddAsync(report, items)` writes report + items atomically; `GetByIdAsync` and `GetItemsAsync` return
  them with items ordered by `rank, id`.

Where practical, assert each repository's output equals the in-memory repository's output for the same
inputs (same ordering, same round-tripped values) to prove behavioural parity.

---

## Tests

This slice *is* tests. The bar: with Docker present, all cases run and pass; with Docker absent, every case
**skips** and `dotnet test Radar.sln -c Release` remains green. Do not modify or remove any existing
in-memory or `Radar.IntegrationTests` tests.

---

## Constraints

- Target .NET 10. The new project is a **test** project; its Testcontainers/Npgsql references stay out of
  the production projects and out of `Radar.IntegrationTests`.
- Tests must **skip, not fail**, when Docker/Postgres is unavailable — the committed default build must
  pass on a database-less machine.
- Assert the real-database behaviour matches the documented AD-1 and AD-3 semantics; do not relax them to
  make a test pass.
- Add the project to `Radar.sln`.

---

## Acceptance criteria

- [ ] `Radar.Persistence.IntegrationTests` exists, is added to `Radar.sln`, and references the Postgres
      repositories via `Radar.Infrastructure`.
- [ ] A shared fixture starts a PostgreSQL Testcontainer, applies the schema via `PostgresSchemaInitializer`,
      and exposes an `Available` flag; tests `Skip` when it is `false`.
- [ ] Evidence tests prove duplicate-`ContentHash` rejection (`false`, no overwrite) and `collected_at_utc,id`
      ordering (AD-1/AD-3).
- [ ] Company/Signal/Score/Report tests prove upsert-by-Id last-write-wins, the documented orderings, the
      inclusive `GetObservedBetween` window, and provenance round-trip.
- [ ] With Docker absent, the suite skips and `dotnet test Radar.sln -c Release --no-build` stays green;
      with Docker present, all cases pass.

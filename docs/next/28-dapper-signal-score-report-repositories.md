# Task: Dapper repositories — Signal, Score (+ links), Report (+ items)

## Overview

Implements the remaining three Postgres-backed repositories behind the **existing, unchanged** Application
interfaces — `ISignalRepository`, `IScoreRepository`, `IReportRepository` — using Dapper on the spec-26
schema and connection factory. Together with spec 27 this completes the relational mirror of the in-memory
store; the in-memory implementations stay as the default for unit tests.

Semantics must match the in-memory repositories **exactly**:

- **AD-1 (upsert-by-Id, last-write-wins)** for signals, score snapshots, score-evidence links, reports,
  and report items.
- **AD-3 (deterministic ordering).** Signals by `observed_at_utc, id`; snapshots by `created_at_utc, id`;
  links by `id`; report items by `rank, id`. `GetObservedBetweenAsync` is inclusive on both bounds
  (`observed_at_utc >= start AND observed_at_utc <= end`), matching the in-memory window.
- **AD-2 (real impls observe the token).** Every Npgsql/Dapper call receives `ct`.

---

## Assignment

Worktree: pending
Dependencies: 26-postgres-schema-and-migration-runner (`NpgsqlConnectionFactory`,
`PostgresDapperConfiguration`, the schema)
Conflicts with: None — adds only new files under `Persistence/Postgres/`. May run in parallel with
spec 27 (disjoint files); both are wired by spec 29.
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Infrastructure/Persistence/Postgres/
  PostgresSignalRepository.cs   # NEW: ISignalRepository
  PostgresScoreRepository.cs    # NEW: IScoreRepository
  PostgresReportRepository.cs   # NEW: IReportRepository
```

Namespace: `Radar.Infrastructure.Persistence.Postgres`.

---

## Implementation details

Same conventions as spec 27: inject `NpgsqlConnectionFactory`, open per call, use
`CommandDefinition(sql, param, cancellationToken: ct)`, enum-as-text + underscore mapping from
`PostgresDapperConfiguration`, normalise `DateTimeOffset` to UTC before binding.

### `PostgresSignalRepository : ISignalRepository`

- `AddAsync(Signal signal, CancellationToken ct)` — upsert by id into `signals`
  (`INSERT ... ON CONFLICT (id) DO UPDATE SET` every non-id column = `EXCLUDED.*`). Columns:
  `evidence_id, company_id, company_mention, type, direction, strength, novelty, confidence,
  supporting_excerpt, reason, review_status, observed_at_utc, created_at_utc`. (`company_id` is
  nullable — an unresolved signal stores `NULL`.)
- `GetByIdAsync` — `WHERE id = @id`, single-or-default.
- `GetByCompanyAsync(Guid companyId, …)` — `WHERE company_id = @companyId ORDER BY observed_at_utc, id`.
- `GetObservedBetweenAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, …)` —
  `WHERE observed_at_utc >= @startUtc AND observed_at_utc <= @endUtc ORDER BY observed_at_utc, id`
  (inclusive both ends — matches the in-memory repo and AD-6's shared-boundary windowing).

### `PostgresScoreRepository : IScoreRepository`

- `AddSnapshotAsync(CompanyScoreSnapshot snapshot, …)` — upsert by id into `company_score_snapshots`
  (all columns, `ON CONFLICT (id) DO UPDATE`).
- `AddEvidenceLinkAsync(ScoreEvidenceLink link, …)` — upsert by id into `score_evidence_links`
  (`score_snapshot_id, signal_id, evidence_id, contribution_reason, contribution_weight`).
- `GetSnapshotsForCompanyAsync(Guid companyId, …)` —
  `WHERE company_id = @companyId ORDER BY created_at_utc, id`.
- `GetLinksForSnapshotAsync(Guid snapshotId, …)` —
  `WHERE score_snapshot_id = @snapshotId ORDER BY id`.

### `PostgresReportRepository : IReportRepository`

- `AddAsync(RadarReport report, IReadOnlyList<RadarReportItem> items, CancellationToken ct)` — open a
  connection, begin a transaction, upsert the report by id into `radar_reports`, then upsert each item by
  id into `radar_report_items`, then commit. Pass `ct` to `BeginTransactionAsync` and every command; on
  exception the transaction rolls back. (Mirrors the in-memory "store report then all items" as one
  atomic unit.)
- `GetByIdAsync(Guid id, …)` — `WHERE id = @id`, single-or-default.
- `GetItemsAsync(Guid reportId, …)` —
  `WHERE report_id = @reportId ORDER BY rank, id`.

> As in spec 27, prefer direct constructor mapping; fall back to a private `Row` DTO + `ToDomain()` only
> if a record's mapping is fussy. Keep the column lists and `ORDER BY` contracts identical to the
> in-memory behaviour.

---

## Tests

No live-database tests here — the gated Testcontainers suite in spec 30 exercises these against a real
Postgres (round-trip, AD-3 ordering, the inclusive window boundary, report+items atomic write). This slice
must compile and keep `dotnet build`/`dotnet test Radar.sln` green; leave the in-memory tests intact.

---

## Constraints

- Target .NET 10. Npgsql/Dapper stay in `Radar.Infrastructure` (AD-5).
- Match the in-memory semantics exactly: upsert-by-Id last-write-wins (AD-1); the documented orderings
  and the inclusive `GetObservedBetweenAsync` window (AD-3 / AD-6).
- Observe `ct` on every database call, including the report transaction (AD-2).
- Preserve provenance: persist `signals.evidence_id`, `score_evidence_links.signal_id/evidence_id`, and
  `radar_report_items.score_snapshot_id` so the chain stays traceable. Store/emit UTC only.
- Keep changes scoped to the three new repository files. Do not touch DI, the Worker, or in-memory code.

---

## Acceptance criteria

- [ ] `PostgresSignalRepository`, `PostgresScoreRepository`, `PostgresReportRepository` implement their
      interfaces with the exact method signatures.
- [ ] All writes upsert by `id` (last-write-wins); the report write persists report + items in one
      transaction.
- [ ] Collection queries carry the AD-3 `ORDER BY`; `GetObservedBetweenAsync` is inclusive on both bounds.
- [ ] Every Npgsql/Dapper call (including the transaction) passes `ct`.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` are green.

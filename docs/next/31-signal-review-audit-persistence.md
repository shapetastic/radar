# Task: Persist the SignalReview audit record (ISignalReviewRepository)

## Overview

`RadarPipelineRunner` already produces a `SignalReview` audit record for every reviewed signal
(`outcome.Review`) but **discards it** — there is no repository for it (see the "Known MVP gap" comment in
`RadarPipelineRunner.cs`, ~lines 157-162: only the reviewed signal's `ReviewStatus`/`Confidence` are
persisted). This slice closes that provenance gap: it adds `ISignalReviewRepository` with both in-memory
and Postgres implementations, the `signal_reviews` table (migration `002`), and wires the runner to
persist the audit record alongside the reviewed signal.

> **Recommendation: do this as a SEPARATE, later slice (this one), not folded into the persistence swap.**
> The Postgres swap (26–30) is a pure "same interfaces, new backend" change. Persisting the review audit
> trail is *additive new behaviour* that crosses Application (new interface + runner change), Domain mapping,
> Infrastructure (two new repos + a migration), and DI (both registration methods) — keeping it isolated
> keeps every slice ~1-2h, lets the backend swap land and stabilise first, and means each Postgres repo
> slice mirrors an existing in-memory repo rather than inventing a new contract mid-swap. Sequence it after
> 29 (shared DI file) and after 26 (migration runner). It does not block 26–30.

---

## Assignment

Worktree: pending
Dependencies: 26 (migration runner + embedded-migration mechanism), 29 (`AddPostgresRadarPersistence`,
`AddInMemoryRadarPersistence` — this slice adds a registration to both)
Conflicts with: 29 (both edit `InfrastructureServiceCollectionExtensions.cs`) — sequence after 29.
Also edits `RadarPipelineRunner.cs` (constructor + the review-persist line) — sequence after any other
in-flight runner change.
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/Abstractions/Persistence/
  ISignalReviewRepository.cs                       # NEW
src/Radar.Application/Pipeline/
  RadarPipelineRunner.cs                           # MODIFIED: inject repo; persist outcome.Review

src/Radar.Infrastructure/Persistence/InMemory/
  InMemorySignalReviewRepository.cs                # NEW
src/Radar.Infrastructure/Persistence/Postgres/
  PostgresSignalReviewRepository.cs                # NEW
  Migrations/002_signal_reviews.sql                # NEW (embedded resource)
src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs     # MODIFIED: register in both Add*RadarPersistence
```

---

## Implementation details

### `ISignalReviewRepository` (Application)

Follow the established interface style, including the AD-1 upsert-by-Id `<remarks>` (a `SignalReview` is an
immutable audit record, written once; upsert-by-Id keeps the same last-write-wins contract as the other
non-evidence aggregates and is safe under replay):

```csharp
using Radar.Domain.Signals;

namespace Radar.Application.Abstractions.Persistence;

public interface ISignalReviewRepository
{
    /// <remarks>
    /// Upsert by Id (last-write-wins). The relational implementation must preserve these
    /// semantics; do not silently switch evidence to upsert or these to insert-only.
    /// </remarks>
    Task AddAsync(SignalReview review, CancellationToken ct);
    Task<IReadOnlyList<SignalReview>> GetBySignalAsync(Guid signalId, CancellationToken ct);
}
```

`GetBySignalAsync` orders by `reviewed_at_utc` then `id` (AD-3).

### `InMemorySignalReviewRepository`

Mirror the other in-memory repos exactly: a `ConcurrentDictionary<Guid, SignalReview>`, `AddAsync` does
`_byId[review.Id] = review` (does not observe `ct` — AD-2), `GetBySignalAsync` returns
`.Where(r => r.SignalId == signalId).OrderBy(r => r.ReviewedAtUtc).ThenBy(r => r.Id).ToList()`.

### `002_signal_reviews.sql` (embedded migration)

Picked up automatically by `PostgresSchemaInitializer` (spec 26 runs embedded `Migrations/*.sql` in name
order, tracked in `schema_migrations`):

```sql
CREATE TABLE IF NOT EXISTS signal_reviews (
    id              uuid PRIMARY KEY,
    signal_id       uuid NOT NULL REFERENCES signals(id),
    reviewer_name   text NOT NULL,
    decision        text NOT NULL,
    summary         text NOT NULL,
    issues_json     text NULL,
    reviewed_at_utc timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_signal_reviews_signal ON signal_reviews(signal_id);
```

(`decision` stores the `SignalReviewDecision` enum as text — add it to the enum handlers in
`PostgresDapperConfiguration` if not already present.)

### `PostgresSignalReviewRepository`

Same conventions as specs 27/28: inject `NpgsqlConnectionFactory`, `CommandDefinition(..., ct)`, enum-as-text.
- `AddAsync` — `INSERT INTO signal_reviews (...) VALUES (...) ON CONFLICT (id) DO UPDATE SET` every non-id
  column = `EXCLUDED.*` (upsert-by-Id). Normalise `ReviewedAtUtc` to UTC.
- `GetBySignalAsync` — `WHERE signal_id = @signalId ORDER BY reviewed_at_utc, id`.

### DI registration

Register in **both** persistence methods (one line each):
- `AddInMemoryRadarPersistence`: `services.AddSingleton<ISignalReviewRepository, InMemorySignalReviewRepository>();`
- `AddPostgresRadarPersistence`: `services.AddSingleton<ISignalReviewRepository, PostgresSignalReviewRepository>();`

### `RadarPipelineRunner` wiring

- Add `ISignalReviewRepository` to the constructor (`ThrowIfNull`), beside `_signalRepository`.
- At the review-persist site (currently ~lines 157-162): after
  `await _signalRepository.AddAsync(outcome.ReviewedSignal, ct)`, also
  `await _signalReviewRepository.AddAsync(outcome.Review, ct)`. Persist the signal **first**, then the
  review, so the `signal_reviews.signal_id` FK is satisfied under the Postgres backend.
- Replace the "Known MVP gap … NOT persisted" comment with a short note that the audit record is now
  persisted.
- Optionally surface a count (e.g. `reviewsRecorded`) in the existing run-summary log if one exists; do not
  change the pipeline's public result shape unless it is trivial and already carries similar counters.

---

## Tests

### In-memory (existing test project for the runner / Infrastructure tests)
- `InMemorySignalReviewRepository`: `AddAsync` then `GetBySignalAsync` returns the review; ordering is
  `reviewed_at_utc, id`; filtering by `signalId` excludes others; upsert-by-Id replaces on same `Id`.
- `RadarPipelineRunner`: after a run that produces reviewed signals, the injected
  `ISignalReviewRepository` has one `SignalReview` per persisted signal, each `SignalId` matching a stored
  signal (provenance). Use an in-memory or fake repo; assert the runner persists signal-before-review.

### Postgres (add to `Radar.Persistence.IntegrationTests`, spec 30 — Docker-gated, skippable)
- `PostgresSignalReviewRepository`: round-trip a `SignalReview`; `GetBySignalAsync` ordering; upsert-by-Id
  last-write-wins; the `signal_id` FK requires the parent signal to exist (insert a signal first).
- The fixture already runs all migrations, so `002_signal_reviews.sql` is applied — assert the table is
  usable.

Keep all existing tests green; this slice only **adds** behaviour and a table.

---

## Constraints

- Target .NET 10. Npgsql/Dapper and the migration stay in `Radar.Infrastructure` (AD-5).
- Preserve provenance: the review references its signal (`signal_id` FK), completing the audit trail the
  runner currently drops. Persist signal before review.
- Match conventions: in-memory ignores `ct` (AD-2); Postgres observes `ct`; both upsert-by-Id (AD-1);
  collection query ordered `reviewed_at_utc, id` (AD-3).
- Store/emit UTC only. Keep changes scoped — no unrelated runner or schema changes.

---

## Acceptance criteria

- [ ] `ISignalReviewRepository` exists with in-memory and Postgres implementations registered in the
      matching `Add*RadarPersistence` methods.
- [ ] `002_signal_reviews.sql` is an embedded migration applied by `PostgresSchemaInitializer` (tracked in
      `schema_migrations`), with the `signal_id` FK and `ix_signal_reviews_signal` index.
- [ ] `RadarPipelineRunner` persists `outcome.Review` (signal first, then review) and the "NOT persisted"
      gap comment is removed.
- [ ] In-memory tests cover repo round-trip/ordering/upsert and the runner persisting one review per
      signal; Postgres (gated) tests cover round-trip, ordering, upsert, and the FK.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` are green
      (with Docker absent, the new Postgres tests skip).

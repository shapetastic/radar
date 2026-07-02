# Task: Cross-run score read-back so the weekly report's deltas actually fire

## Overview

Spec 60 added week-over-week score movement to the weekly report: each surfaced company shows
`(Opportunity +N, Trajectory +M vs last run)` when a *previous* snapshot exists, and `(first snapshot)`
when it does not. Spec 61 added a "Recent runs" footer and, in doing so, established the pattern the report
builder now uses to read a persisted file store directly. **Spec 60's delta feature is dead in the real
Worker** — it renders `(first snapshot)` for every company on every run — and this slice fixes exactly that,
reusing spec 61's builder-reads-a-file-store pattern.

### The defect (confirmed by a live 2026-07-02 run + code reading — state it precisely)

- `WeeklyReportBuilder` (`src/Radar.Application/Reporting/WeeklyReportBuilder.cs`, ~lines 116–156) computes,
  per company, a `current` snapshot (latest with `CreatedAtUtc` in the report period) and a `previous`
  snapshot (latest with `CreatedAtUtc < current.CreatedAtUtc`), **both from the injected
  `IScoreRepository`**. `previous` is carried onto `CandidateEntry.Previous`, handed to the action policy via
  `ReportActionContext`, and surfaced by spec 60 as `WeeklyReportEntry.PreviousOpportunityScore` /
  `PreviousTrajectoryScore`.
- At runtime `IScoreRepository` is bound to `InMemoryScoreRepository`
  (`src/Radar.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs:38`). That
  repository starts **empty every process** and only holds **this run's** freshly-written snapshots (one per
  company). Prior runs' snapshots are persisted to disk by the **separate, write-only**
  `IScoreSnapshotFileStore` / `FileScoreSnapshotStore` (`WriteAsync` only — there is **no read-back method**)
  and are never loaded back into the in-memory repository.
- Therefore, across two separate pipeline runs, the in-memory repo for a company contains exactly one
  snapshot (this run's), so the `previous`-finding loop finds nothing and `previous` is **always null**. The
  renderer emits `(first snapshot)` for every company forever, and `WeeklyReportActionPolicy` never sees a
  prior score. Verified live: two runs minutes apart both showed `(first snapshot)` for all 7 companies.

### The fix (minimal, in-architecture — mirror spec 61's footer)

Give `IScoreSnapshotFileStore` a **read** method that returns the most recent persisted snapshot for a
company strictly before a given instant, implement it in `FileScoreSnapshotStore` exactly as spec 59/61
added `FilePipelineRunStore.ReadRecentAsync` (enumerate → deserialize → skip-bad → order → pick), and in
`WeeklyReportBuilder` obtain `previous` from that file store instead of the in-memory repo. `current` and
its evidence links stay sourced from this run's in-memory repo, so the report's score->signal->evidence
provenance trace is completely unchanged.

Scope is **one read method on the score file store + its implementation + swapping the builder's `previous`
source + tests**. It does NOT change scoring, the formula, the action policy, ranking, the zero-link skip,
the renderer, or which companies surface.

---

## Assignment

Worktree: any
Dependencies: 59 (run store read precedent), 60 (the delta feature being fixed), 61 (builder-reads-file-store
pattern) — all merged.
Conflicts with: any slice that touches `WeeklyReportBuilder` or the score file store / its tests — this edits
`WeeklyReportBuilder.cs`, `IScoreSnapshotFileStore.cs`, `FileScoreSnapshotStore.cs`, and both stores' tests.
Do NOT run in parallel with another report-builder or score-store slice; sequence it.
Estimated time: ~1.5–2 h

---

## Project structure changes

```text
src/Radar.Application/Scoring/
  IScoreSnapshotFileStore.cs           # MODIFIED: add ReadLatestBeforeAsync(companyId, beforeUtc, ct)

src/Radar.Infrastructure/FileSystem/
  FileScoreSnapshotStore.cs            # MODIFIED: implement ReadLatestBeforeAsync (enumerate/deserialize/skip/order)

src/Radar.Application/Reporting/
  WeeklyReportBuilder.cs               # MODIFIED: inject IScoreSnapshotFileStore; source `previous` from it, not the in-memory repo

tests/Radar.Infrastructure.Tests/FileSystem/
  FileScoreSnapshotStoreTests.cs       # MODIFIED/NEW: read-back returns correct latest-before, skips malformed, null when none

tests/Radar.Application.Tests/Reporting/
  WeeklyReportBuilderTests.cs          # MODIFIED: prior snapshot ON DISK (not in in-memory repo) yields a real delta, not "(first snapshot)"
```

(If a `FileScoreSnapshotStoreTests` file does not yet exist, create it; match the existing
`FilePipelineRunStore` / other file-store test conventions.)

---

## Implementation details

### 1. Add a read method to `IScoreSnapshotFileStore`

Add, alongside the existing `WriteAsync`:

```csharp
/// <summary>
/// Returns the most recently created persisted snapshot for <paramref name="companyId"/> whose
/// CreatedAtUtc is strictly before <paramref name="beforeUtc"/>, or null when the company has no
/// qualifying persisted snapshot. Enables cross-run "vs previous snapshot" comparisons that the
/// in-memory score repository cannot serve (it holds only the current process's snapshots).
/// Only the scalar snapshot fields are required by callers; the returned snapshot need not
/// rehydrate its ScoreEvidenceLinks. A read/deserialization failure of one file is skipped, never
/// thrown; cancellation propagates.
/// </summary>
Task<CompanyScoreSnapshot?> ReadLatestBeforeAsync(
    Guid companyId, DateTimeOffset beforeUtc, CancellationToken ct);
```

Update the interface `<remarks>` to note the store is now read+write (was write-only) and that the read is a
targeted scalar read, not a general rehydration (see provenance note below).

### 2. Implement `ReadLatestBeforeAsync` in `FileScoreSnapshotStore`

Model it on `FilePipelineRunStore.ReadRecentAsync` (the skip/order precedent):

- The company's snapshots live under `{RootDirectory}/{companyId}/*.json` (see `WriteAsync`'s path). Compute
  `companyDir = Path.Combine(_options.RootDirectory, companyId.ToString())`. If it does not exist, return
  `null`.
- Enumerate `*.json` in that directory. Guard the enumeration in `try/catch (ex is IOException or
  UnauthorizedAccessException)` and return `null` on failure (warn), exactly like the run store.
- For each file: `ct.ThrowIfCancellationRequested()`, read text, `JsonSerializer.Deserialize<...>` using the
  existing `RadarFileStoreJson.Options`. Wrap each file read in `try/catch (ex is IOException or
  UnauthorizedAccessException or JsonException)`, warn, and **skip** — one bad file must never fail the read.
  A `null` deserialization result is treated as malformed and skipped (same as the run store).
- The persisted shape is the private `ScoreSnapshotFile` record already defined in this file. **Reuse it**
  (deserialize into it, then reconstruct a `CompanyScoreSnapshot` from its scalar fields). You do **not** need
  to reconstruct the `Links` into `ScoreEvidenceLink`s — callers need only the scalar scores; leave the
  snapshot's links empty (the current report's links come from the in-memory repo, unchanged). Note this
  explicitly in a comment so a reviewer does not read the empty links as dropped provenance.
- Filter to snapshots with `CreatedAtUtc < beforeUtc`. Among those, return the one with the greatest
  `CreatedAtUtc`, tie-broken by `Id` (descending, i.e. newest-first) per AD-3's ordering spirit. Return
  `null` when none qualify.
- Do not overwrite/shadow the existing `WriteAsync` provenance guard behaviour; this is a pure addition.

### 3. Source `previous` from the file store in `WeeklyReportBuilder`

- Inject `IScoreSnapshotFileStore` into the constructor (add the field + `ArgumentNullException.ThrowIfNull`),
  exactly as `IPipelineRunStore _runStore` was injected for the spec-61 footer.
- Keep `current` as-is: still the latest in-period snapshot from `_scoreRepository`, with its in-memory
  `ScoreEvidenceLink`s fetched via `GetLinksForSnapshotAsync` — **provenance unchanged**.
- Replace the in-memory `previous`-finding loop (~lines 146–154) with a read from the file store:
  `await _scoreSnapshotFileStore.ReadLatestBeforeAsync(company.Id, current.CreatedAtUtc, ct)`. This returns
  the latest persisted snapshot strictly before the current one — including snapshots written by **earlier
  runs**, which is the whole point.
- Graceful degradation: if the read throws (it should not, but defensively) or returns null, `previous`
  stays null and the entry renders `(first snapshot)` — the report must never abort because a prior snapshot
  file is unreadable. Prefer having the store swallow per-file failures (as specified above) so the builder
  simply gets null; if you add a builder-level `try/catch`, exclude `OperationCanceledException` like the
  footer read does.
- `CandidateEntry.Previous`, the `ReportActionContext(c.Current, c.Previous)` call, and spec 60's
  `PreviousOpportunityScore`/`PreviousTrajectoryScore` wiring all stay as-is — they now receive a
  cross-run `previous` instead of always-null. No renderer change.
- The per-company file read is one call per surfaced+candidate company. That mirrors the existing per-company
  `GetSnapshotsForCompanyAsync` round-trip; keep it inside the existing company loop. Note in a comment that a
  future batched cross-run read could replace the per-company calls, but is out of scope here.

### Scope / provenance notes the spec makes explicit

- **Only the previous snapshot's scalar scores** (`OpportunityScore`, `TrajectoryScore`) are consumed — by the
  spec-60 delta clause **and** by the action policy via `ReportActionContext`. The read-back deliberately does
  **not** reload the previous snapshot's `ScoreEvidenceLink`s, and the current report's
  score->signal->evidence provenance trace is **completely unchanged** (the current snapshot and its links
  still come from this run's in-memory repo). This is a **low-provenance-risk, targeted read**, not a general
  repository rehydration.
- **Out of scope:** broader cross-run read-back of signals/velocity (loading prior *signals* back into the
  pipeline so scoring's previous-window velocity spans runs) remains a **separate future slice**. This slice
  only makes the report's previous-*snapshot* comparison work across runs.
- Determinism / AD-3 ordering preserved (newest-first, `Id` tiebreak). Files-first, no DB (AD-8). No AI, no
  provider SDK. Read impl stays in Infrastructure; the interface and builder stay in Application (AD-5).
  Graceful degradation: a read failure logs and yields a null `previous` -> `(first snapshot)`, never aborts
  the report.

---

## Tests

### `FileScoreSnapshotStoreTests` (Infrastructure)

- **Latest-before is returned:** write two snapshots for the same company at different `CreatedAtUtc` (e.g.
  T1 < T2) via `WriteAsync`. `ReadLatestBeforeAsync(companyId, afterT2, ct)` returns the T2 snapshot;
  `ReadLatestBeforeAsync(companyId, T2, ct)` (strictly-before) returns the T1 snapshot; assert the returned
  scalar scores match the expected snapshot.
- **None qualify -> null:** `ReadLatestBeforeAsync(companyId, atOrBeforeEarliest, ct)` returns null; unknown
  company (no directory) returns null.
- **Malformed file skipped:** write a valid snapshot, then drop a garbage `*.json` in the company's directory;
  the read still returns the valid snapshot (bad file skipped, no throw).
- **Cancellation propagates:** an already-cancelled token causes `OperationCanceledException` (matches the run
  store's `ct.ThrowIfCancellationRequested()` in the loop).

### `WeeklyReportBuilderTests` (Application)

- **Cross-run delta fires (the fix):** seed the in-memory score repo with ONLY the current run's snapshot for
  a company (+ its links so the entry surfaces), and place a **prior** snapshot for that same company ONLY on
  disk in the score file store (a real `FileScoreSnapshotStore` over a temp dir, or a fake
  `IScoreSnapshotFileStore` returning the prior snapshot). Assert the built entry's
  `PreviousOpportunityScore`/`PreviousTrajectoryScore` equal the on-disk prior snapshot's scores (i.e. NOT
  null), proving the cross-run path works and the report would render a real delta instead of
  `(first snapshot)`.
- **No prior on disk -> first snapshot:** with an empty score file store, `Previous` stays null (entry renders
  `(first snapshot)`), matching today's single-run behaviour.
- Update the builder test setup/fakes for the new constructor dependency. Existing spec-60 and spec-61 tests
  must stay green (the renderer, footer, and delta-formatting tests are unchanged).

---

## Constraints

- Target `net10.0`, C# 14. Reporting/extraction stays in `Radar.Application`; the file read implementation
  stays in `Radar.Infrastructure` (AD-5). No provider SDK, no AI, no DB (AD-8, files-first).
- Provenance is sacred and **unchanged**: the current snapshot + its links (score->signal->evidence) still
  come from the in-memory repo; the cross-run read supplies only the previous snapshot's scalar scores and
  deliberately omits its links. Do not add a general rehydration.
- Deterministic ordering (AD-3: `CreatedAtUtc` then `Id`, newest-first). Graceful degradation: a per-file read
  failure is skipped/logged and yields a null `previous`, never aborting the report; cancellation propagates.
- Renderer stays byte-identical for the same model; the six AD-9 labels and the advice-language ban are
  untouched. No new label, no recommendation.
- Scope: one read method + its impl + builder `previous`-source swap + tests. `dotnet build Radar.sln -c
  Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Acceptance criteria

- [ ] `IScoreSnapshotFileStore` gains `ReadLatestBeforeAsync(Guid companyId, DateTimeOffset beforeUtc,
      CancellationToken ct)` returning the most recent persisted snapshot strictly before `beforeUtc`, or null;
      the interface `<remarks>` notes it is now read+write and that the read is scalar-only (no link
      rehydration).
- [ ] `FileScoreSnapshotStore` implements it by enumerating the company's snapshot files, deserializing via
      the existing `ScoreSnapshotFile` / `RadarFileStoreJson.Options`, skipping malformed/unreadable files
      (warn, never throw), filtering `CreatedAtUtc < beforeUtc`, and returning the newest (tie-break `Id`) or
      null; cancellation propagates.
- [ ] `WeeklyReportBuilder` injects `IScoreSnapshotFileStore` and sources each company's `previous` snapshot
      from `ReadLatestBeforeAsync(company.Id, current.CreatedAtUtc, ct)` instead of the in-memory repo; a
      failed/empty read yields null `previous` (renders `(first snapshot)`), never aborting the report.
- [ ] `current` and its `ScoreEvidenceLink`s still come from this run's in-memory repo — the report's
      score->signal->evidence provenance trace is unchanged; the previous snapshot's links are NOT reloaded.
- [ ] Cross-run behaviour is proven by tests: a prior snapshot present ONLY on disk yields a non-null
      `PreviousOpportunityScore`/`PreviousTrajectoryScore` on the entry (a real delta), and an empty score file
      store yields null (`(first snapshot)`). Store read tests cover latest-before, none-qualify, malformed
      skip, and cancellation. Existing spec-60/61 tests stay green.
- [ ] Determinism (AD-3), files-first (AD-8), layering/AI rules (AD-5) preserved; no advice language.
      `dotnet build`/`dotnet test` on `Radar.sln -c Release` green.

# Task: Score snapshot `CreatedAtUtc` must equal the run instant so same-run snapshots surface in the report

## Overview

A live end-to-end Worker run (7 real RSS feeds → 84 evidence → 33 signals → 7 companies scored) produced an
**empty weekly report** despite all 7 companies being scored. Root cause is a timestamp-boundary bug:

- `ScoringEngine` stamps each `CompanyScoreSnapshot.CreatedAtUtc` with a **fresh** `_timeProvider.GetUtcNow()`
  at persist time (`src/Radar.Application/Scoring/ScoringEngine.cs:133`).
- The pipeline captures a single run instant `asOfUtc` *after collection* (AD-7) and passes it as both the
  scoring `windowEndUtc` **and** the report `periodEndUtc`.
- `WeeklyReportBuilder` selects each company's current snapshot with
  `snapshot.CreatedAtUtc > periodStartUtc && snapshot.CreatedAtUtc <= periodEndUtc`
  (`src/Radar.Application/Reporting/WeeklyReportBuilder.cs:130`).
- Because `CreatedAtUtc` is read a few hundred ms **after** `asOfUtc`, every freshly-created snapshot has
  `CreatedAtUtc > periodEndUtc` and is excluded. Observed in the live run: `windowEndUtc = …09.401` vs
  `createdAtUtc = …09.573`. Net effect: **a run can never report the snapshots it just created** — the report
  is empty on the first run and would only ever show a prior run's stale snapshot.

**Fix (AD-7-aligned):** stamp `CompanyScoreSnapshot.CreatedAtUtc = windowEndUtc` (the run instant `asOfUtc`)
instead of a separate clock read. AD-7 already states the single run-instant feeds the mapper `createdAtUtc`,
the scoring `windowEndUtc`, and the report `periodEndUtc`; the snapshot's creation timestamp using a *separate*
wall-clock read is the inconsistency. After the fix `createdAtUtc == periodEndUtc`, so the inclusive upper
bound includes the snapshot and the company surfaces.

This is a correctness fix only — no scoring-formula change (`radar-formula-v1`/`mvp-engine-v1` versions
unchanged), no new public surface.

---

## Assignment

Worktree: any
Dependencies: existing trunk (scoring + reporting already merged).
Conflicts with: None — touches the scoring engine + its tests, and strengthens an existing integration test.
Estimated time: ~30–45 min

---

## Project structure changes

```text
src/Radar.Application/Scoring/
  ScoringEngine.cs            # MODIFIED: CreatedAtUtc = windowEndUtc; drop the now-unused TimeProvider dep if it
                              # becomes unused (confirm GetUtcNow() at line 133 is its only use)

tests/Radar.Application.Tests/Scoring/
  ScoringEngineTests.cs       # MODIFIED: assert CreatedAtUtc == windowEndUtc; adjust the clock fixture

tests/Radar.IntegrationTests/
  PipelineEndToEndTests.cs    # MODIFIED: assert the report is NON-EMPTY — a company scored this run appears as
                              # a report entry (the regression guard for this bug)

src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs  # MODIFIED only if ScoringEngine's ctor signature changes
```

---

## Implementation details

### `ScoringEngine`
- Change the snapshot construction (`ScoringEngine.cs:120-133`) so `CreatedAtUtc: windowEndUtc` instead of
  `CreatedAtUtc: _timeProvider.GetUtcNow()`.
- `GetUtcNow()` at line 133 appears to be the engine's **only** use of `TimeProvider`. Confirm this. If so,
  remove the now-unused `TimeProvider` constructor parameter and field, and update its DI registration in
  `InfrastructureServiceCollectionExtensions` and any direct test construction. If `TimeProvider` is in fact
  used elsewhere, keep it and only change the one line. Do not leave an unused injected dependency (the
  architecture reviewer would flag it).
- Update the XML doc / inline comment to state that `CreatedAtUtc` is the run instant (`windowEndUtc`), so a
  snapshot is deterministic/reproducible and consistent with the AD-7 single-run-instant rule (no wall-clock
  skew between creation and the window/report bounds).

### Determinism / AD-3
- `CreatedAtUtc` remains the snapshot ordering key (AD-3). Across runs, each run's `asOfUtc` strictly increases,
  so ordering and the report's "previous = latest snapshot with `CreatedAtUtc < current.CreatedAtUtc`" logic
  still hold. Within a single run there is one snapshot per company, so no new ties are introduced; existing
  `ThenBy(Id)` tiebreakers stay.

### Out of scope
- No change to the scoring formula, window rule, label policy, renderer, or the report-builder selection logic
  (the builder is correct once snapshots carry the run instant).
- Do not change `EvidenceItem.CollectedAtUtc`/mapper timestamps (already AD-7-correct).

---

## Tests

### `ScoringEngineTests` (MODIFIED)
- The snapshot timestamp test must now assert `result.Snapshot.CreatedAtUtc == windowEndUtc` (the value passed
  to `ScoreCompanyAsync`), not a separate fixed "now". Adjust the fixture so the injected clock (if still
  present) is distinct from `windowEndUtc`, proving `CreatedAtUtc` tracks the window end and not the clock.

### `PipelineEndToEndTests` (MODIFIED) — the regression guard
- Strengthen the end-to-end assertion: after a run that scores at least one company in-period, the generated
  weekly report **contains that company as an entry** (e.g. the markdown is non-empty under "Highest
  opportunity" / the report model has ≥1 item). This is the assertion that would have caught the empty-report
  bug — confirm it FAILS without the `ScoringEngine` fix and passes with it.

### Existing tests
- All other scoring/report tests stay green (`radar-formula-v1` outputs unchanged).

---

## Constraints

- Target `net10.0`. Deterministic; no behavioural change to scores, only to the snapshot's `CreatedAtUtc`.
- Honour AD-7 (single run-instant used identically across mapper createdAt, scoring windowEnd, report
  periodEnd — now also snapshot CreatedAtUtc) and AD-3 (snapshot ordering by CreatedAtUtc, Id tiebreak).
- Scope strictly to the scoring engine + affected tests (+ DI only if the ctor changes). Do not touch the
  renderer, policy, or formula.
- `dotnet build`/`dotnet test` on `Radar.sln -c Release` green.

---

## Acceptance criteria

- [ ] `CompanyScoreSnapshot.CreatedAtUtc` equals the `windowEndUtc` (run instant) the engine was given; no
      separate wall-clock read for it.
- [ ] No unused `TimeProvider` dependency left on `ScoringEngine` (removed if it became unused, with DI + tests
      updated; otherwise unchanged).
- [ ] An end-to-end test asserts a company scored in-period appears in the weekly report (regression guard),
      and it fails without the fix.
- [ ] Scoring-formula outputs and versions are unchanged; `build`/`test` green.

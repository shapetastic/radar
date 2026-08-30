# Task: Score from the hydrated index, not a per-call disk scan — and measure the stages

## Overview

The 2026-08-29 baseline (`70f256e3`) spent **~59 minutes between the as-of instant (21:44:52Z) and the run
record (22:44Z)** on scoring + report, against ~15 minutes for the whole collection pass. The 08-28 run was
the same (~62 min for 740 snapshots vs 59 for 940), so the cost is not per-company work.

The cause is in `FileSignalStore.ReadApprovedInWindowAsync` (`src/Radar.Infrastructure/FileSystem/FileSignalStore.cs:143`).
It is called once per company per strategy from `ScoringEngine.ScoreCompanyAsync:411` for the
previous/velocity window, and it is a **per-call disk scan**: it enumerates every `*.json` in each month
directory the window touches and deserializes each file, then filters by company. For last night's
60-day window the previous window was 1 May → 30 Jun, i.e. the `2026/05` (4,662 files) and `2026/06`
(7,235 files) directories — **~12k file reads and deserializations per call, × 1,034 calls ≈ 12 million**.
Meanwhile `EnsureHydratedAsync` had already loaded all 64,782 signals into `_byId` (log line "Hydrated 63123
signal(s)") and `GetByCompanyAsync` serves the *current* window from that index. The class comment at
`:258` says the disk read is kept "deliberately" because it "answers a different question under semantics
pinned by its own tests" — that is an argument about the FILTER (window, known-at, approved, `LowestId`
survivor), not about the SOURCE, and the index holds byte-identical records (same `SignalFile→Signal`
mapping, per the same comment).

Two smaller costs in the same loop: `GetByCompanyAsync` scans all 64k signals and runs the cross-run dedupe
on every call (1,034 × 64k), and every strategy re-does the identical per-company read + evidence resolution
(11 strategies × the same 94 companies at the same as-of).

## Assignment

Worktree: any. Dependencies: spec 202 merged. No scoring identity input is touched.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. Measure first — stage timings in the run log and the run record

Before changing anything, add bounded Information lines with **elapsed durations** (monotonic
`TimeProvider.GetTimestamp`/`GetElapsedTime`, spec 187 §7's rule): signal hydration, evidence hydration,
each strategy's scoring loop (name, companies, elapsed), the weekly report build, and the run-record write.
Carry `ScoringElapsed` / `HydrationElapsed` as **trailing nullable** `TimeSpan?` on `PipelineRunRecord`
(`null` = pre-203 record, never zero). The log currently has NO per-line timestamps, which is why this
spec's hour is inferred from file mtimes rather than measured — that must not be true of the next run.
Run the existing suite; this part moves no behaviour.

## 2. `ReadApprovedInWindowAsync` serves from the hydration index

Reimplement it over `_byId` after `EnsureHydratedAsync`, applying EXACTLY the current predicate in the
current order: company match, `ObservedAtUtc` in `(startExclusive, endInclusive]`, `CreatedAtUtc <=
knownAsOfUtc`, `ReviewStatus == Approved`, then `SignalCrossRunDedupe.Collapse(…, LowestId)`, then
`ObservedAtUtc`/`Id` ordering. Delete the month-directory scan and `EnumerateWindowMonthDirectories` (rule:
delete dormant code). Keep the existing tests for the method — they pin the semantics and must pass
unmodified — and ADD an equivalence test: hydrate a fixture store, call the OLD disk implementation (kept
in the test project only, or reconstructed there) and the new one across a grid of windows/known-at
instants including month boundaries and files created after known-at, and assert the returned lists are
element-for-element identical. The per-file skip-don't-throw rule now lives only in hydration, which
already has it; the method's own "malformed file" warning is deleted with the scan.

**Consequence, stated:** a caller that never touched the repository surface now pays the one-time
hydration on first call instead of a per-call scan. Every production caller already hydrates
(`AddDurableRadarSignalHistory`), so a live run pays nothing new. Replay (spec 139) benefits identically —
it drives the same engine.

## 3. Per-company index, built at hydration

Maintain `Dictionary<Guid companyId, List<Signal>>` alongside `_byId`, updated by `TryAdd`/`WriteAsync`
exactly where `_byId` is, so `GetByCompanyAsync` and the new §2 read filter a company's own list instead
of 64k. Dedupe and ordering unchanged; outputs asserted identical to the pre-203 implementation on a
fixture with cross-run duplicates.

## 4. Do not re-read per strategy — one per-company read, N strategy evaluations

`ScoringPass` loops strategy → company, so each company's signal set, previous-window set and evidence
resolution are computed 11 times. Invert to company → strategy: perform the reads ONCE per company at the
as-of instant and hand the same materialised inputs to each strategy's engine. Constraints:
- **Output byte-identical**: every snapshot's components, explanation, `ComponentJson`, evidence links,
  `ScoringConfigVersion`, `CollectionProvenance` and file path unchanged; assert with the existing golden
  pins (`ScoringOutputStabilityTests`, the v9/v10/v11 composition guards) and a new pass-level test that
  runs both loop orders over a multi-strategy fixture and diffs every snapshot.
- Per-strategy `SignalTypes` filtering, the spec-113/109/194 supersede + collapse and the legacy
  neutralization stay INSIDE each engine — they are strategy-dependent through the filter. Only the raw
  reads (`GetByCompanyAsync`, `ReadApprovedInWindowAsync`, evidence `GetByIdAsync`) are shared.
- The write order of snapshot files may change (company-major instead of strategy-major). Nothing reads
  order from the file system (assert: `FileScoreSnapshotStore` reads sort by content), and
  `ScoreAssemblyDiagnostics` aggregation is order-independent (assert).
- If the inversion cannot be made byte-identical for any strategy, do §1–§3 and record §4 as deferred with
  the reason; §2 alone is the bulk of the hour.

## 5. Verify on the live store, read-only

Run the spec-139 replay at ONE as-of over the live store on `main` and on the branch (the spec-145
precedent), all configured strategies: every snapshot field-for-field identical excluding minted GUIDs, and
report both wall-clocks. Then the first post-203 baseline's §1 timings are the measured result — record
them in the PR or a follow-up note, not a projection.

## Non-goals

No scoring, weight, formula, rule-set, window, fingerprint or provenance change; no change to what is
persisted per signal/snapshot beyond the two nullable timings on the run record; no parallelism (AD-3
ordering must hold — this slice removes waste, it does not add threads); no change to hydration's
skip-don't-throw rules or to `AddIfNewAsync` idempotency.

## Acceptance criteria

- [ ] The run log and run record carry measured stage durations; `null` on old records.
- [ ] `ReadApprovedInWindowAsync` reads no file after hydration; equivalence test against the disk
      implementation passes across month boundaries and known-at edges.
- [ ] `GetByCompanyAsync` is O(company's signals); outputs identical.
- [ ] One read per company per pass; every snapshot byte-identical under both loop orders (or §4 deferred
      with a stated reason).
- [ ] Live read-only replay: field-for-field identical on both sides, both wall-clocks reported.
- [ ] All six pins unchanged; full suite and `git diff --check` clean.

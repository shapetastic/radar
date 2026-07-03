# Task: Cross-run signal read-back so SignalVelocityScore actually compares against prior runs

## Overview

Radar's `SignalVelocityScore` (AD-6: `50·(actNow+10)/(actPrev+10)` over `Strength` sums; 50 = steady) is
**dead across separate runs today** — it reads `50` (steady) for every company on every fresh process — and
this slice fixes exactly that, mirroring how **spec 65** fixed the report's cross-run *snapshot* delta.

### The defect (confirmed by code reading — state it precisely in the PR)

- `ScoringEngine.ScoreCompanyAsync` computes velocity from a **previous window** of signals that it slices
  out of `_signalRepository.GetByCompanyAsync(companyId, ct)`
  (`src/Radar.Application/Scoring/ScoringEngine.cs` ~lines 79, 118–126): the previous window is
  `ObservedAtUtc ∈ (previousWindowStartUtc, windowStartUtc]`, Approved-only, carried as
  `ScoringInput.PreviousSignals` (activity-only, no evidence — AD-6).
- At runtime `ISignalRepository` is bound to `InMemorySignalRepository`
  (`src/Radar.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs:42`). That
  repository **starts empty every process** and only holds **this run's** freshly-extracted+stored signals.
  Prior runs' signals are mirrored to disk by the **separate, write-only** `ISignalFileStore` /
  `FileSignalStore` (`FileSignalStore.cs` — `WriteAsync` only, **no read-back method**) and are never loaded
  back into the in-memory repository.
- Therefore, in a typical week-apart run, the in-memory repo for a company contains only *this* run's signals
  (whose `ObservedAtUtc` are recent), the previous-window slice `(previousWindowStartUtc, windowStartUtc]` is
  **empty**, `actPrev = 0`, and velocity collapses to `50·(actNow+10)/(0+10)` — but because `actNow` for a
  quiet week is also ~0, it renders **steady 50** for everyone, and a genuine acceleration or deceleration in
  signal activity week-over-week is never detected. `SignalVelocity` — a core scored component and a stated
  MVP scoring input ("number and strength of recent signals compared with a prior window") — is effectively
  inert. This is the **exact analogue** of the spec-65 snapshot defect, one layer earlier in the pipeline;
  spec 65 explicitly called broader cross-run signal read-back "a **separate future slice**." This is it.

### The fix (minimal, in-architecture — mirror spec 65 / spec 61)

Give `ISignalFileStore` a **read** method that returns the persisted Approved signals for a company whose
`ObservedAtUtc` falls in a given `(start, end]` window, implement it in `FileSignalStore` exactly as spec
59/61 added `FilePipelineRunStore.ReadRecentAsync` and spec 65 added
`FileScoreSnapshotStore.ReadLatestBeforeAsync` (enumerate → deserialize → skip-bad → filter → order), and in
`ScoringEngine` obtain the **previous-window** signals from that file store instead of slicing them out of the
in-memory repo. The **current** window's signals and their evidence/contributions/`ScoreEvidenceLink`s stay
sourced from this run's in-memory repo, so the score→signal→evidence provenance trace is **completely
unchanged** (AD-6: `PreviousSignals` never carries provenance anyway — it is activity-only for velocity).

Scope is **one read method on the signal file store + its implementation + swapping the engine's
previous-window source + tests**. It does NOT change the scoring **formula** (AD-6 unchanged), the current
window, contributions/links, the review filter, ranking, the report, or which companies surface. Because the
observable **output can move** (velocity can now differ from 50 across runs), this slice **bumps
`ScoringConfigVersion`** per AD-10.

---

## Assignment

Worktree: any
Dependencies: 59 (run-store read precedent), 65 (the score-snapshot cross-run read-back this mirrors and which
explicitly deferred signal read-back), 46 (`FileSignalStore` persistence) — all merged. Read spec 65 for the
enumerate/skip/order read pattern and the "scalar/targeted read, not general rehydration" framing.
Conflicts with: touches `ScoringEngine.cs` (+ `ScoringConfigVersion`), `ISignalFileStore.cs`,
`FileSignalStore.cs`, and their tests. It must **NOT** run in parallel with any other scoring/engine or
signal-store slice — sequence it. No collector/extractor/report/Domain/DI-shape change beyond injecting the
existing store into the engine.
Estimated time: ~2 h

---

## Grounding facts (verified against the current tree — do NOT re-research)

- `ScoringEngine` already fetches all of a company's signals once (`GetByCompanyAsync`, line 79) and slices
  **both** the current window `(windowStartUtc, windowEndUtc]` and the previous window
  `(previousWindowStartUtc, windowStartUtc]` from that single fetch (lines 84–86, 118–124). The previous slice
  is Approved-only, deterministically ordered (AD-3), carries **no evidence**, and never builds
  contributions/links (AD-6). This slice replaces **only** the *source* of the previous slice.
- The shared inclusive-end boundary (AD-6): a signal exactly at `windowStartUtc` belongs to the **previous**
  window. The read-back must preserve this exactly — filter `ObservedAtUtc > previousWindowStartUtc &&
  ObservedAtUtc <= windowStartUtc`.
- `FileSignalStore` persists one JSON file per signal (see `WriteAsync`); it is **write-only** today (no read
  method on `ISignalFileStore`). `FileScoreSnapshotStore.ReadLatestBeforeAsync` (spec 65) and
  `FilePipelineRunStore.ReadRecentAsync` (spec 59) are the read-pattern precedents (enumerate a directory,
  deserialize the private persisted-file record via `RadarFileStoreJson.Options`, skip malformed/unreadable
  files without throwing, filter, order deterministically).
- `ISignalRepository` = `InMemorySignalRepository` (line 42), empty each process — the root cause, identical
  in shape to spec 65's `InMemoryScoreRepository` root cause.

---

## Project structure changes

```text
src/Radar.Application/SignalExtraction/   (or wherever ISignalFileStore lives — confirm)
  ISignalFileStore.cs                 # MODIFIED: add ReadApprovedInWindowAsync(companyId, startExclusiveUtc, endInclusiveUtc, ct)

src/Radar.Infrastructure/FileSystem/
  FileSignalStore.cs                  # MODIFIED: implement ReadApprovedInWindowAsync (enumerate/deserialize/skip/filter/order)

src/Radar.Application/Scoring/
  ScoringEngine.cs                    # MODIFIED: inject ISignalFileStore; source the previous window from it
                                      #   (current window stays from the in-memory repo). Bump ScoringConfigVersion.

tests/Radar.Infrastructure.Tests/FileSystem/
  FileSignalStoreTests.cs             # MODIFIED/NEW: read-back returns Approved-in-window, skips malformed, empty when none, cancellation
tests/Radar.Application.Tests/Scoring/
  ScoringEngineTests.cs (+ velocity)  # MODIFIED: prior-run signals ON DISK (not in the in-memory repo) drive a
                                      #   non-steady velocity; update the ScoringConfigVersion assertion
```

Confirm the actual namespace/location of `ISignalFileStore` before editing (it is the interface
`FileSignalStore` implements). No DB (AD-8). No Domain change.

---

## Implementation details

### 1. Add a windowed read to `ISignalFileStore`

Alongside the existing `WriteAsync`:

```csharp
/// <summary>
/// Returns the persisted Approved signals for <paramref name="companyId"/> whose ObservedAtUtc is in
/// (<paramref name="startExclusiveUtc"/>, <paramref name="endInclusiveUtc"/>] — the exclusive-start,
/// inclusive-end window convention the scoring engine uses (AD-6). Enables the cross-run
/// SignalVelocity previous-window comparison that the in-memory signal repository cannot serve (it holds
/// only the current process's signals). These signals are consumed ACTIVITY-ONLY (Strength magnitude for
/// velocity) — callers do NOT need evidence, so the returned signals need not rehydrate any provenance
/// links; this is a targeted scalar read, not a general repository rehydration. A read/deserialization
/// failure of one file is skipped, never thrown; cancellation propagates. Results are deterministically
/// ordered (ObservedAtUtc, then Id — AD-3).
/// </summary>
Task<IReadOnlyList<Signal>> ReadApprovedInWindowAsync(
    Guid companyId, DateTimeOffset startExclusiveUtc, DateTimeOffset endInclusiveUtc, CancellationToken ct);
```

Update the interface `<remarks>` to note the store is now read+write (was write-only) and that the read is an
activity-only, provenance-free window read (see the scope note).

### 2. Implement `ReadApprovedInWindowAsync` in `FileSignalStore`

Model it on `FileScoreSnapshotStore.ReadLatestBeforeAsync` (spec 65):

- Locate the company's signal files (same path convention `WriteAsync` uses — confirm the on-disk layout;
  it is per-company or filter by the persisted company id). If the directory does not exist, return an empty
  list.
- Enumerate `*.json`, guarding enumeration in `try/catch (ex is IOException or UnauthorizedAccessException)`
  → warn + return empty on failure (like the run/snapshot stores).
- Per file: `ct.ThrowIfCancellationRequested()`, read text, `JsonSerializer.Deserialize<…>` via the existing
  `RadarFileStoreJson.Options` into the **existing private persisted-signal file record** already defined in
  `FileSignalStore` — reuse it, do not invent a new shape. Wrap each read in
  `try/catch (ex is IOException or UnauthorizedAccessException or JsonException)` → warn + **skip**; a `null`
  deserialization is malformed → skip. One bad file must never fail the read.
- Reconstruct each into a `Signal`, filtering to: `ReviewStatus == Approved` **and**
  `ObservedAtUtc > startExclusiveUtc && ObservedAtUtc <= endInclusiveUtc`. You need only the fields velocity
  consumes (`Strength`, `ObservedAtUtc`, `Id`, `ReviewStatus`, company id) — do not reload/rehydrate
  `ScoreEvidenceLink`s or evidence (note this in a comment so a reviewer does not read it as dropped
  provenance; AD-6 says the previous window carries no provenance by design).
- Order `OrderBy(ObservedAtUtc).ThenBy(Id)` (AD-3). Return the list (possibly empty).

### 3. Source the previous window from the file store in `ScoringEngine`

- Inject `ISignalFileStore` into the constructor (add the field + `ArgumentNullException.ThrowIfNull`), exactly
  as spec 65 injected `IScoreSnapshotFileStore` into `WeeklyReportBuilder`.
- Keep the **current** window as-is: still sliced from `_signalRepository.GetByCompanyAsync(...)`, with its
  evidence loaded, contributions and `ScoreEvidenceLink`s built — **provenance unchanged**.
- Replace the in-memory previous-window slice (lines ~118–124) with:
  `await _signalFileStore.ReadApprovedInWindowAsync(companyId, previousWindowStartUtc, windowStartUtc, ct)`.
  This returns prior-run signals from disk — the whole point. The result is already Approved-only and
  window-filtered; keep the deterministic ordering. It feeds `ScoringInput.PreviousSignals` unchanged (AD-6:
  activity-only, no evidence, never builds contributions/links).
- **Graceful degradation:** if the read returns empty (no prior signals on disk, or the store degraded a
  per-file failure), `PreviousSignals` is empty and velocity falls back to its no-previous behaviour (the
  current, safe behaviour) — scoring must never abort because a prior signal file is unreadable. Do NOT add a
  broad `catch` that swallows `OperationCanceledException`; cancellation propagates.
- **Boundary invariant (AD-6) preserved:** the read uses `(previousWindowStartUtc, windowStartUtc]`, so a
  signal exactly at `windowStartUtc` still belongs to the previous window and is never double-counted against
  the current window (which is `(windowStartUtc, windowEndUtc]`).

### 4. Bump the scoring generation stamp (AD-10)

Making velocity respond to prior-run activity is a **scoring-affecting change** — a company's
`SignalVelocityScore` (and thus `OpportunityScore`) can now differ from the pre-slice steady-50 across runs.
Per **AD-10**, bump `ScoringEngine.ScoringConfigVersion` from `"radar-scoring-config-v4"` →
`"radar-scoring-config-v5"` (confirm the current value in `ScoringEngine.cs` — the ledger records it as v4)
and update the accompanying comment to record that THIS slice (cross-run signal read-back for velocity) ships
this generation, so a cross-run delta across the pre/post boundary renders `(scoring updated)` instead of a
fabricated `Thesis improving`/`Thesis deteriorating`. Do **not** touch `ScoringVersion`, `EngineVersion`, or
`RadarScoreFormulaV2.Version` — the formula math is unchanged (this slice only changes the *source* of the
previous-window input). Update any test asserting the old `ScoringConfigVersion` string.

---

## Tests

### `FileSignalStoreTests` (Infrastructure)

1. **Approved-in-window returned:** write Approved signals at various `ObservedAtUtc` for a company via
   `WriteAsync`; `ReadApprovedInWindowAsync(companyId, start, end, ct)` returns exactly those with
   `ObservedAtUtc ∈ (start, end]`, deterministically ordered; a signal exactly at `start` is **excluded**
   (exclusive start) and one exactly at `end` is **included** (inclusive end) — locks the AD-6 boundary.
2. **Non-Approved excluded:** a `NeedsMoreEvidence`/`Rejected` signal in the window is not returned.
3. **None qualify → empty list:** a window with no signals, and an unknown company (no directory), each return
   empty.
4. **Malformed file skipped:** a valid signal + a garbage `*.json` in the same directory → the read returns the
   valid signal, no throw.
5. **Cancellation propagates:** an already-cancelled token → `OperationCanceledException`.

### `ScoringEngineTests` / velocity (Application)

6. **Cross-run velocity fires (the fix):** seed the in-memory signal repo with ONLY the current window's
   signals for a company, and place **prior-window** Approved signals for that same company ONLY on disk (a
   real `FileSignalStore` over a temp dir, or a fake `ISignalFileStore` returning them). Assert
   `SignalVelocityScore != 50` in the direction implied by the activity change (more current-window strength
   than previous → `> 50`; less → `< 50`), proving the previous window now comes from disk. With no prior
   signals on disk, velocity is the steady/no-previous value (today's behaviour) — regression-locked.
7. **Provenance unchanged:** the current-window contributions and `ScoreEvidenceLink`s are identical to before
   (the read-back supplies only activity-only previous signals; assert links still trace to current-window
   evidence).
8. **`ScoringConfigVersion` stamp:** update the assertion to expect `"radar-scoring-config-v5"` (confirm the
   prior value is v4); `ScoringVersion`/`EngineVersion`/formula `Version` unchanged.

Existing scoring/engine tests stay green (the current window, formula, review filter, and ranking are
unchanged).

---

## Spec-implementation checklist

1. **Code paths replaced:** the previous-window slice's *source* moves from the in-memory repo to the signal
   file store. The current window, formula, contributions/links, and review filter are unchanged. The
   `ScoringConfigVersion` constant is bumped (the only stamp change).
2. **Tests:** add the store read cases (1–5) and the cross-run velocity cases (6–8); update the
   `ScoringConfigVersion` assertion; keep existing scoring/store tests green.
3. **Delete nothing still used** (the in-memory repo still serves the current window).
4. **CLAUDE.md / architecture-decisions.md:** no new architecture rule — this realises the cross-run
   signal read-back that spec 65 explicitly deferred, within AD-6 (formula/window/previous-input rules
   unchanged; `PreviousSignals` stays activity-only), AD-8 (files-first), AD-5 (read impl in Infra; interface
   + engine in Application), AD-3 (deterministic order), and AD-10 (`ScoringConfigVersion` bump). No new AD
   entry — note in the PR. Update the CLAUDE.md scoring-checklist note only if warranted (the AD-10 bump is
   already documented there).

---

## Constraints

- Target `net10.0`, C# 14. Scoring/engine stay in `Radar.Application`; the file read implementation stays in
  `Radar.Infrastructure` (AD-5). No provider SDK, no AI, no DB (AD-8, files-first).
- **AD-6 unchanged:** the formula, the `(start, end]` window convention, the shared-boundary rule, and the
  "previous window is activity-only, no evidence, no contributions/links" rule are all preserved — this slice
  changes only the *source* of the previous-window signals.
- **Provenance is sacred and unchanged:** the current snapshot's score→signal→evidence links still come from
  this run's in-memory repo + loaded evidence; the cross-run read supplies only activity-only previous signals
  and deliberately omits provenance. No general rehydration.
- Deterministic ordering (AD-3: `ObservedAtUtc` then `Id`). Graceful degradation: a per-file read failure is
  skipped/logged and yields an empty previous window (steady/no-previous velocity), never aborting scoring;
  cancellation propagates.
- **Bump `ScoringEngine.ScoringConfigVersion`** (`radar-scoring-config-v4` → `radar-scoring-config-v5`, AD-10);
  do not touch `ScoringVersion`/`EngineVersion`/formula `Version`. Never emit advice language (no report
  change here).
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green.

---

## Acceptance criteria

- [ ] `ISignalFileStore` gains `ReadApprovedInWindowAsync(Guid companyId, DateTimeOffset startExclusiveUtc,
      DateTimeOffset endInclusiveUtc, CancellationToken ct)` returning the persisted **Approved** signals whose
      `ObservedAtUtc ∈ (start, end]`, deterministically ordered (AD-3); the interface `<remarks>` notes it is
      now read+write and an activity-only, provenance-free window read.
- [ ] `FileSignalStore` implements it by enumerating the company's signal files, deserializing via the
      existing persisted-signal record / `RadarFileStoreJson.Options`, skipping malformed/unreadable files
      (warn, never throw), filtering Approved + in-window, and ordering; cancellation propagates.
- [ ] `ScoringEngine` injects `ISignalFileStore` and sources the **previous** window from
      `ReadApprovedInWindowAsync(companyId, previousWindowStartUtc, windowStartUtc, ct)` instead of the
      in-memory repo; the **current** window (and its evidence/contributions/`ScoreEvidenceLink`s) still comes
      from the in-memory repo — provenance unchanged. A failed/empty read yields an empty previous window
      (steady/no-previous velocity), never aborting scoring.
- [ ] The AD-6 shared-boundary invariant holds: a signal exactly at `windowStartUtc` counts in the **previous**
      window (exclusive-start/inclusive-end read), never double-counted against the current window — asserted
      by a store test.
- [ ] Cross-run behaviour is proven by tests: prior-window signals present ONLY on disk drive a **non-steady**
      `SignalVelocityScore` (>50 for more current activity, <50 for less), while no prior signals on disk yields
      today's steady/no-previous velocity. Store read tests cover Approved-in-window, non-Approved exclusion,
      boundary, none-qualify, malformed skip, and cancellation.
- [ ] `ScoringEngine.ScoringConfigVersion` is bumped (`radar-scoring-config-v4` → `radar-scoring-config-v5`,
      AD-10), the comment records this slice, and any test asserting the old value is updated;
      `ScoringVersion`/`EngineVersion`/formula `Version` are unchanged.
- [ ] Determinism (AD-3), files-first (AD-8), layering/AI rules (AD-5), and AD-6 formula/window rules
      preserved; no collector/extractor/report/Domain change; no advice language. `dotnet build`/`dotnet test`
      on `Radar.sln -c Release` green.
```
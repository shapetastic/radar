# Task: Dedup cross-run duplicate signals in the SignalVelocity previous window

> **PRIORITY + SEQUENCING (read first).** This is a HIGH-PRIORITY correctness fix found during live
> investigation (2026-07-04). It is a **directed** maintainer task, not the generic planner loop — do
> **not** gate it on an architecture review. It is **prioritized to be dispatched and merged BEFORE
> spec 84** (`docs/next/84-news-attention-breadth-by-publisher.md`). Both slices touch
> `ScoringEngine.cs` and both bump `ScoringEngine.ScoringConfigVersion`, so they must **NOT** run in
> parallel — **sequence them, this one FIRST.** Because this slice lands first it bumps
> `radar-scoring-config-v5 → v6`; spec 84 (whichever merges second) then increments again to `v7`. The
> version instruction below is **order-robust**: read the current value from `ScoringEngine.cs` and bump
> to the next integer (`v5` is the current value in the tree today — confirm before editing).

## Overview

Radar's `SignalVelocityScore` (AD-6: `50·(actNow+10)/(actPrev+10)` over `Strength` sums; 50 = steady)
is **nondeterministic and distorted** because the previous-window signal set it compares against is
inflated by cross-run duplicate signal files on disk. Spec 82 correctly made the previous window
cross-run by sourcing it from `FileSignalStore.ReadApprovedInWindowAsync`, but that read returns
**every persisted signal** in the window — including the same underlying signal re-persisted with a
fresh `Guid` on every pipeline run. This slice makes the previous-window read return exactly **one
signal per stable identity**, restoring determinism (AD-3) and a like-for-like velocity comparison,
and — because it is dedup-on-read — it also neutralises the ~1705 already-accumulated legacy duplicate
files with **no migration**.

### The defect (verified live 2026-07-04 — state it precisely in the PR)

- **Signals get a fresh `Guid` every run.** `ExtractedSignalMapper.ToSignal`
  (`src/Radar.Application/SignalExtraction/ExtractedSignalMapper.cs:43`) assigns
  `Id: Guid.NewGuid()` to every mapped signal. `RadarPipelineRunner.MapResolveReviewStoreAsync`
  (`src/Radar.Application/Pipeline/RadarPipelineRunner.cs:414,448-449`) then persists that signal to
  `ISignalFileStore.WriteAsync`, whose on-disk path is keyed on `signal.Id`
  (`FileSignalStore.WriteAsync`, `{RootDirectory}/{yyyy}/{MM}/{signalId}.json`, `FileSignalStore.cs:56-60`).
  A fresh id every run means every run writes a **new file** for the *same* underlying signal.
- **Only evidence is protected from cross-run duplication, not signals.** Evidence dedupes on
  `ContentHash` via `IEvidenceRepository.AddIfNewAsync` (AD-1), and the runner only extracts *newly
  stored* evidence — so evidence never re-accumulates. But the same evidence, re-collected in a later
  run, is a duplicate and skipped... **except** the signals from prior runs already sit on disk with
  their old ids. Re-collection is not even required: any signal whose `ObservedAtUtc` still falls in a
  later run's previous window is re-read from disk. The in-memory `ISignalRepository`
  (`InMemorySignalRepository`, `InfrastructureServiceCollectionExtensions.cs:42`) starts empty every
  process and holds only the current run; **`FileSignalStore` accumulates across ALL runs.**
- **Concrete measurement (2026-07-04):** 1705 signal files on disk. For AGYS (companyId
  `f0d50897-7161-40e6-a367-4ce63fc5aa8c`), 202 Approved signals across ~19 runs — a single May-18
  earnings 8-K appears as **29 duplicate `GuidanceChange` copies**, one April-14 partnership as 6
  copies, etc. These are the SAME signal (same evidence, type, direction, strength) re-minted 29 times.
- **Impact on scoring — the asymmetry.** `ScoringEngine.ScoreCompanyAsync`
  (`src/Radar.Application/Scoring/ScoringEngine.cs`) sources the **current** window from the in-memory
  repo (line 83-90 — one clean run, no duplicates) and the **previous** window from
  `_signalFileStore.ReadApprovedInWindowAsync(...)` (line 131-133 — disk, duplicate-laden). So velocity
  compares a **deduped numerator** (`actNow`) against a **duplicate-inflated denominator** (`actPrev`),
  and `actPrev` depends on **how many times the pipeline has been run**. Result: velocity is (a)
  **nondeterministic** (violates AD-3 replayability) and (b) **distorted** by the numerator/denominator
  asymmetry. Live example: AGYS Velocity clamped at 100 with `actNow ≈ 123` (in-memory, clean) vs
  `actPrev = 34` (disk, mostly 6 duplicate copies of one April partnership); stripped to like-for-like
  it should sit around a steady ~50.

### The chosen fix — (a) dedup-on-read (smallest correct fix); (b) deferred as a possible follow-up

Two designs were evaluated:

- **(a) Dedup-on-read (CHOSEN).** `FileSignalStore.ReadApprovedInWindowAsync` collapses cross-run
  duplicates by a **stable signal-identity key** before returning, keeping exactly one signal per
  identity with a **deterministic tie-break**. This fixes the velocity input immediately AND neutralises
  the ~1705 already-accumulated legacy duplicates with **no migration / no one-time cleanup**. Because
  the previous window is **activity-only, no provenance** by design (AD-6 — it never builds
  `ScoreContribution`s or `ScoreEvidenceLink`s), dedup here **cannot** touch the current-window
  provenance trace. `ReadApprovedInWindowAsync` has exactly **one** production caller (the scoring
  engine's previous window — verified), so the blast radius is a single, provenance-free code path.
- **(b) Idempotent persistence (root cause, DEFERRED).** Derive a **deterministic** `signalId` from the
  signal identity so re-runs UPSERT the same file (works with AD-1's upsert-by-Id for signals) instead
  of accumulating, bounding disk growth. This is the cleaner root cause, but it (i) touches `signalId`
  generation in `ExtractedSignalMapper` — a load-bearing provenance field threaded through the runner,
  the review record, the in-memory repo, and the file path — with stability implications (a re-extracted
  signal whose confidence/direction changed would overwrite its prior file, which may or may not be
  desired), and (ii) does **not** retroactively clean the 1705 existing duplicate files. It is a larger,
  higher-risk change that does not improve correctness beyond what (a) already delivers.

**Decision: ship (a) now.** It is the smallest change that fully restores determinism and correctness,
handles the legacy duplicates for free, and is confined to a single provenance-free read path. Record
(b) as a **possible future follow-up** (idempotent/deduped persistence) — see Out of scope. Do not
bundle (b) into this slice.

Where else the file store is read: `ReadApprovedInWindowAsync` is the **only** read method on
`ISignalFileStore`, and the scoring engine's previous window is its **only** production caller.
Deduping inside the read method therefore covers every current consumer; no other call site needs a
separate dedup pass.

### Not a formula change

This is **scoring-affecting** (velocity output moves and becomes deterministic) but is **NOT** a
formula-math change: `IScoreFormula` / `RadarScoreFormulaV2.Compute` and all its constants are
**untouched** — only the *set of previous-window signals fed into it* is deduplicated upstream in the
store read. So `RadarScoreFormulaV2.Version` stays `radar-formula-v2` and `EngineVersion` /
`ScoringVersion` stay `mvp-engine-v1` (no AD-6 edit). Per **AD-10** the moved scoring output bumps
`ScoringEngine.ScoringConfigVersion` (see Version-bump obligation). If — contrary to this analysis — you
conclude the velocity **formula** itself must change, STOP and flag it; dedup-on-read is not a formula
change and this spec does not authorise one.

---

## Assignment

Worktree: any
Dependencies: 82 (`FileSignalStore.ReadApprovedInWindowAsync` — the read this dedups), 46
(`FileSignalStore` persistence), 65 (the cross-run read-back precedent 82 mirrors) — all merged. This is
a directed correctness slice driven by the 2026-07-04 live finding.
Conflicts with: touches `FileSignalStore.cs` (+ its tests) and `ScoringEngine.cs`
(`ScoringConfigVersion` constant + comment; the engine call site is unchanged). Optionally touches the
`ISignalFileStore` `<remarks>`. It must **NOT** run in parallel with any other scoring/engine or
signal-store slice — **especially spec 84** (both bump `ScoringConfigVersion` and edit
`ScoringEngine.cs`). **Sequence it; THIS slice merges FIRST.** No Domain, DI-shape, collector,
extractor, or report change.
Estimated time: ~1–1.5 h

---

## Grounding facts (verified against the current tree — do NOT re-research)

- **Signals get a fresh id every run.** `ExtractedSignalMapper.ToSignal` sets `Id: Guid.NewGuid()`
  (`ExtractedSignalMapper.cs:43`); the runner persists that via `WriteAsync`
  (`RadarPipelineRunner.cs:448-449`), path-keyed on `signal.Id` (`FileSignalStore.cs:56-60`). No
  deterministic/derived id exists today.
- **`ReadApprovedInWindowAsync` returns ALL persisted signals in the window** (Approved + company +
  `ObservedAt ∈ (start, end]`), one per file, ordered `OrderBy(ObservedAtUtc).ThenBy(Id)`
  (`FileSignalStore.cs:72-168`). It performs **no** identity dedup, so N cross-run copies of one signal
  count N times.
- **The engine's current window is from the in-memory repo** (`ScoringEngine.cs:83-90`, one clean run —
  already duplicate-free); the **previous** window is from the file store
  (`ScoringEngine.cs:131-133`). So the asymmetry is exactly: clean `actNow` vs duplicate-laden
  `actPrev`. The engine's previous-window call site does **not** change in this slice — dedup happens
  inside the store read.
- **The previous window is activity-only, no provenance (AD-6).** `ReadApprovedInWindowAsync` already
  deliberately does NOT rehydrate evidence or `ScoreEvidenceLink`s (`FileSignalStore.cs:125-127`
  comment); `ScoringEngine` never builds contributions/links from `PreviousSignals`
  (`ScoringEngine.cs:115-135`). Deduping the previous window therefore **cannot** alter the
  current-window provenance trace.
- **The persisted `SignalFile` record** (`FileSignalStore.cs:235-250`) carries `SignalId, EvidenceId,
  CompanyId, CompanyMention, Type, Direction, Strength, Novelty, Confidence, SupportingExcerpt, Reason,
  ReviewStatus, ObservedAt, CreatedAt`. Two cross-run copies of the same underlying signal differ ONLY
  in `SignalId` (fresh `Guid`) and `CreatedAt` (the run instant); every evidence-derived field
  (`EvidenceId`, `CompanyId`, `Type`, `Direction`, `Strength`, `ObservedAt`, ...) is identical.
- **One evidence item can legitimately produce multiple DISTINCT signals** — e.g. a filing yielding both
  a `CustomerWin` and a `GuidanceChange`, or signals of different `Direction`. So the identity key must
  include `Type` and `Direction` to avoid collapsing genuinely distinct signals.
- **`ReadApprovedInWindowAsync` has exactly ONE production caller** — the scoring engine's previous
  window (verified by search: interface/impl/engine + tests only). It is the only read method on
  `ISignalFileStore`.
- **`ScoringConfigVersion` is currently `"radar-scoring-config-v5"`** (`ScoringEngine.cs:39`);
  `ScoringVersion` = `EngineVersion` = `"mvp-engine-v1"`; formula `Version` = `"radar-formula-v2"`.

---

## Project structure changes

```text
src/Radar.Infrastructure/FileSystem/
  FileSignalStore.cs                  # MODIFIED: ReadApprovedInWindowAsync deduplicates by stable
                                      #   identity key with a deterministic tie-break before returning.
                                      #   WriteAsync + persisted-record shape UNCHANGED.

src/Radar.Application/Signals/
  ISignalFileStore.cs                 # MODIFIED (docs only): note the read collapses cross-run duplicate
                                      #   signals to one per identity (activity-only previous window).

src/Radar.Application/Scoring/
  ScoringEngine.cs                    # MODIFIED: bump ScoringConfigVersion (read current value, +1) and
                                      #   update the comment. Call site + formula UNCHANGED.

tests/Radar.Infrastructure.Tests/FileSystem/
  FileSignalStoreTests.cs             # MODIFIED/NEW: duplicate signals in a window are counted ONCE;
                                      #   distinct (type/direction/evidence) signals are NOT collapsed;
                                      #   deterministic tie-break is stable across read repetitions.
tests/Radar.Application.Tests/Scoring/
  ScoringEngineTests.cs               # MODIFIED: velocity is STABLE regardless of how many duplicate
                                      #   copies of a prior signal are on disk; update ScoringConfigVersion
                                      #   assertion to the bumped value.
```

No Domain change. No DI-shape change. No collector/extractor/report change. No formula-math change
(`RadarScoreFormulaV2.Compute` body untouched).

---

## Implementation details

### 1. Define the stable identity key and deduplicate inside `ReadApprovedInWindowAsync`

In `FileSignalStore.ReadApprovedInWindowAsync`, after collecting the in-window Approved `matches` and
BEFORE the final `OrderBy(...).ThenBy(...)`, collapse cross-run duplicates so each identity contributes
exactly once.

- **Identity key (justified against the code):** a duplicate is the SAME underlying signal re-minted
  across runs, differing only in `SignalId`/`CreatedAt`. Use:

  ```
  (CompanyId, EvidenceId, Type, Direction)
  ```

  Rationale, stated in a code comment:
  - `EvidenceId` + `Type` + `Direction` distinguishes the genuinely-distinct signals one evidence item
    can produce (e.g. a `CustomerWin` and a `GuidanceChange`, or a `Positive` vs a `Neutral`), so the
    key never collapses distinct signals into one.
  - `CompanyId` is included for safety although it is already fixed per read (the method filters to one
    `companyId`); keeping it in the key makes the key self-describing and correct even if the read is
    ever called differently.
  - `ObservedAt` is intentionally NOT part of the key: it is derived from the same evidence and is
    therefore constant across a signal's cross-run copies, so it adds nothing; including it would risk
    NOT collapsing copies if a future change ever perturbed `ObservedAt` derivation. `Strength`,
    `Confidence`, `Novelty`, `SupportingExcerpt`, `Reason` are likewise evidence/extractor-derived and
    identical across copies — do not add them to the key (a legitimately re-scored signal is out of
    scope for velocity dedup, which is activity-only). If you find a reason the key must differ,
    document it in the PR.

- **Deterministic tie-break (AD-3):** when multiple files share an identity key, keep exactly one,
  chosen deterministically so the read is reproducible run-to-run and independent of filesystem
  enumeration order. Keep the copy with the **lowest `SignalId`** (a total, stable order over `Guid`)
  as the tie-break; equivalently you may keep highest `CreatedAt` then lowest `SignalId` — but since all
  copies carry identical activity fields (`Strength`), the choice does not change the velocity result,
  so prefer the simplest total order: **lowest `SignalId`**. State the chosen rule in a comment and lock
  it with a test.

- Implement with a group/dedup that does not depend on enumeration order, e.g. group the `matches` by
  the identity key, and from each group select the single representative by the tie-break, then apply
  the existing final `OrderBy(ObservedAtUtc).ThenBy(Id)` to the survivors. Keep it allocation-modest;
  correctness and determinism over micro-optimisation.

- **Do NOT change** `WriteAsync`, the on-disk path convention, the `SignalFile` persisted record shape,
  the window/Approved/company filtering, the malformed-file skip, the month-directory scan bounding, or
  the cancellation behaviour. This is a single post-filter dedup step before the existing ordering.

### 2. Update the `ISignalFileStore` read doc-comment

In `ISignalFileStore.ReadApprovedInWindowAsync`'s `<summary>` (and/or the interface `<remarks>`), add one
sentence: the read returns **at most one signal per stable identity `(CompanyId, EvidenceId, Type,
Direction)`**, collapsing cross-run duplicate persisted copies (same signal re-minted with a fresh id
each run) so the activity-only previous window is deterministic and not inflated by how many times the
pipeline has run. Keep the existing activity-only / provenance-free / AD-6 wording.

### 3. Bump the scoring generation stamp (AD-10) — no formula change

- **Read the current `ScoringEngine.ScoringConfigVersion` value from `ScoringEngine.cs` and bump to the
  next integer.** In the tree today it is `"radar-scoring-config-v5"`, so this slice sets it to
  `"radar-scoring-config-v6"`. (Order-robust note for the coder: if spec 84 has somehow already merged,
  the current value will be `v6` and you bump to `v7` instead — always "current value + 1", never a
  hard-coded target.)
- Update the accompanying comment to record that THIS slice (cross-run signal dedup in the velocity
  previous window) ships this generation, so a cross-run delta across the pre/post boundary renders
  `(scoring updated)` instead of a fabricated `Thesis improving`/`Thesis deteriorating` (the AD-10
  comparability gate).
- Do **NOT** touch `ScoringVersion`, `EngineVersion`, or `RadarScoreFormulaV2.Version` — the velocity
  math (`50·(actNow+10)/(actPrev+10)`), the `(start, end]` window and its shared boundary, and the
  activity-only previous-window rule are all unchanged (AD-6). Only the *set of previous-window signals*
  is deduplicated, upstream in the store read. This is the exact AD-10 case: a scoring-affecting change
  that is not a formula/engine identity change.

---

## Version-bump obligation (state explicitly in the PR)

- **`RadarScoreFormulaV2.Version` = `radar-formula-v2` — UNCHANGED.** `RadarScoreFormulaV2.Compute` and
  its constants are not edited. Not a formula-math change → not `radar-formula-v3`, **no AD-6 edit**.
- **`ScoringEngine.ScoringConfigVersion` — BUMPED (AD-10)** to current-value + 1 (`v5 → v6` today, since
  this slice merges before spec 84). Velocity (and thus Opportunity/ranking/possibly action labels) moves
  for companies with accumulated duplicate prior signals, so per AD-10 the whole-generation stamp bumps
  in this same slice; the comparability gate then renders `(scoring updated)` across the boundary.
- **`EngineVersion` / `ScoringVersion` = `mvp-engine-v1` — UNCHANGED.**

---

## Tests

### `FileSignalStoreTests` (Infrastructure)

1. **Duplicate signals in a window are counted ONCE (the fix):** write **three** Approved signals for the
   same company that are cross-run duplicates — **same `EvidenceId`, `Type`, `Direction`, `Strength`,
   `ObservedAt`**, but **different `SignalId`** (and different `CreatedAt`) — all inside the window.
   `ReadApprovedInWindowAsync` returns **exactly one** signal for that identity. (This is the core
   assertion the maintainer asked for: duplicate signals in a window counted once.)
2. **Distinct signals are NOT collapsed:** for one company/one evidence in-window, write signals that
   differ by `Type` (e.g. `CustomerWin` vs `GuidanceChange`) and by `Direction` (e.g. `Positive` vs
   `Neutral`) — assert each distinct `(EvidenceId, Type, Direction)` survives (no over-collapsing). Also
   assert two signals with the same `(Type, Direction)` but **different `EvidenceId`** both survive
   (distinct evidence = distinct signals).
3. **Deterministic tie-break / stability across reads:** with several duplicate copies of one identity on
   disk, calling `ReadApprovedInWindowAsync` twice returns the **same** surviving `SignalId` both times
   (order-independent, reproducible — AD-3), and it is the tie-break winner (lowest `SignalId`, per the
   chosen rule).
4. **Existing behaviour preserved (regression locks):** the current spec-82 tests
   (`ReadApprovedInWindow_ReturnsInWindowApproved_OrderedAndBoundaryHonoured`,
   `_ExcludesNonApproved`, `_NoMatches_ReturnsEmpty`, `_SkipsMalformedFile_ReturnsValid`,
   `_AlreadyCancelledToken_Throws`) stay green — dedup does not change window/boundary/Approved/malformed/
   cancellation behaviour. Where those tests write only distinct signals, results are unchanged.

### `ScoringEngineTests` / velocity (Application)

5. **Velocity is STABLE across duplicate accumulation (the fix, at the engine level):** seed the current
   window (in-memory repo) with a fixed set of Approved signals for a company, and place prior-window
   Approved signals on disk (a real `FileSignalStore` over a temp dir, or a fake `ISignalFileStore`).
   Compute the snapshot with the previous window having **one** copy of each prior signal, then again
   with **N duplicate copies** (same identity, fresh ids) of those same prior signals on disk — assert
   `SignalVelocityScore` is **identical** in both cases (proving duplicates no longer inflate `actPrev`
   and velocity no longer depends on how many times the pipeline ran). Contrast: without the fix the
   second case would drive velocity down (larger `actPrev`).
6. **Provenance unchanged:** the current-window contributions and `ScoreEvidenceLink`s are identical to
   before (dedup touches only the activity-only previous window; assert links still trace to
   current-window evidence). Existing engine provenance tests stay green.
7. **`ScoringConfigVersion` stamp:** update any assertion of the stamp to the bumped value
   (`"radar-scoring-config-v6"` given this slice merges first); `ScoringVersion`/`EngineVersion`/formula
   `Version` unchanged.

Existing collector, formula, engine, store, and report tests stay green (the current window, formula,
review filter, ranking, and report are unchanged).

---

## Spec-implementation checklist

1. **Code paths replaced:** `FileSignalStore.ReadApprovedInWindowAsync` now collapses cross-run duplicate
   persisted signals to one per identity `(CompanyId, EvidenceId, Type, Direction)` with a deterministic
   tie-break, before the existing ordering. The engine call site, `WriteAsync`, the persisted record, and
   the formula are unchanged. `ScoringConfigVersion` bumped (the only stamp change).
2. **Tests:** add the store dedup cases (1–3), keep the spec-82 store regression locks (4), add the
   engine velocity-stability case (5) and provenance lock (6), update the `ScoringConfigVersion`
   assertion (7); keep all other tests green.
3. **Delete nothing still used** (the write path, persisted record, and in-memory current-window read are
   all retained). No legacy-file cleanup is performed — dedup-on-read makes it unnecessary.
4. **CLAUDE.md / architecture-decisions.md:** no new architecture rule and **no AD-6 change** (formula/
   window/activity-only rules unchanged). This upholds AD-3 (deterministic, replayable output) by making
   the previous-window read independent of run count, and AD-1 (evidence-immutable, signals-upsert) is
   untouched. Note in the PR that only `ScoringConfigVersion` moved (AD-10), already documented in the
   CLAUDE.md scoring checklist. Consider proposing a short AD entry if the maintainer wants
   dedup-on-read (and the deferred idempotent-persistence follow-up) recorded — optional, flag it; do not
   add it unprompted.

---

## Constraints

- Target `net10.0`, C# 14. The dedup implementation stays in `Radar.Infrastructure` (`FileSignalStore`);
  the interface doc + the scoring stamp live in `Radar.Application` (AD-5). No provider SDK, no AI, no DB
  (AD-8, files-first).
- **Provenance is sacred and unchanged (AD-6):** the current snapshot's score→signal→evidence links
  still come from this run's in-memory repo + loaded evidence; the deduped previous window supplies only
  activity-only signals and carries no provenance. Dedup happens only on the provenance-free
  previous-window read, whose sole caller is the engine's velocity input.
- **Determinism (AD-3):** the surviving copy per identity is chosen by a fixed total order (lowest
  `SignalId`), so the read is reproducible run-to-run and independent of filesystem enumeration order.
  Malformed-file skip and cancellation behaviour are unchanged; graceful degradation preserved.
- **AD-1 unchanged:** evidence stays immutable/dedup-by-ContentHash; signals stay upsert-by-Id on write.
  This slice does not change write semantics — it only collapses duplicates when READING for velocity.
- **AD-6 formula UNCHANGED** (`radar-formula-v2` stays; no `radar-formula-v3`, no AD-6 edit) — only the
  previous-window signal *set* is deduplicated, upstream in the store read.
- **Bump `ScoringEngine.ScoringConfigVersion`** to current-value + 1 (AD-10; `v5 → v6` today); do NOT
  touch `ScoringVersion`/`EngineVersion`/formula `Version`.
- AD-8 files-first, AD-9 labels/advice rules unchanged; never emit advice language (no report change).
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green.

---

## Out of scope (do NOT do here)

- **(b) Idempotent/deterministic-id signal persistence** — deriving a stable `signalId` from the signal
  identity so re-runs UPSERT one file instead of accumulating. This is the deeper root-cause fix and the
  **principled** answer to the maintainer's "should we archive old same-day runs?" question (the fix is
  idempotent/deduped persistence, **not** archiving), but it touches `signalId` generation and the file
  path with stability/provenance implications, and does not retroactively clean the existing files.
  Record it as a **possible future follow-up**; do not implement it in this slice — dedup-on-read (a)
  already restores correctness and neutralises the legacy duplicates for free.
- **One-time cleanup of the ~1705 legacy duplicate signal files** — unnecessary for correctness once
  dedup-on-read lands (the read collapses them). Do not add a migration/cleanup pass.
- **Retention / archiving / compaction of score snapshots** (~133 per run, one per company per run —
  accumulation is intended for cross-run deltas) and **per-day report overwrite** behaviour. These are a
  separate future concern, not this slice. Briefly note in the PR that the principled fix for signal
  accumulation is idempotent/deduped persistence (this spec + the deferred (b)), and that snapshot/report
  retention is unrelated and out of scope.
- **Any change to `RadarScoreFormulaV2.Compute`** or the velocity math. If a live re-measure after this
  ships shows velocity still miscalibrated *after* like-for-like dedup, that is a separate formula slice
  (`radar-formula-v3` + AD-6) — do not pre-empt it here.
- Any report-renderer, collector, extractor, Domain, or DI-shape change.

---

## Acceptance criteria

- [ ] `FileSignalStore.ReadApprovedInWindowAsync` returns **at most one signal per stable identity**
      `(CompanyId, EvidenceId, Type, Direction)`, collapsing cross-run duplicate persisted copies (same
      signal re-minted with a fresh `SignalId` each run) with a **deterministic** tie-break (lowest
      `SignalId`), applied before the existing `OrderBy(ObservedAtUtc).ThenBy(Id)`. A comment justifies the
      key (why `Type`+`Direction` prevent collapsing distinct signals, why `ObservedAt`/activity fields are
      excluded) and the tie-break.
- [ ] Genuinely distinct signals are NOT collapsed: differing `Type`, `Direction`, or `EvidenceId` each
      survive as separate signals (test-locked).
- [ ] Duplicate signals in a window are counted ONCE and `SignalVelocityScore` is **stable** regardless of
      how many duplicate copies of a prior signal sit on disk (engine-level test), removing the
      nondeterminism and the deduped-numerator/duplicate-denominator asymmetry.
- [ ] `WriteAsync`, the on-disk path convention, the persisted `SignalFile` shape, the window/Approved/
      company filtering, the malformed-file skip, and cancellation behaviour are **unchanged**; the
      spec-82 store regression tests stay green.
- [ ] The engine's previous-window call site is unchanged; the current window and its
      contributions/`ScoreEvidenceLink`s still come from the in-memory repo — provenance unchanged (AD-6).
- [ ] `RadarScoreFormulaV2.Compute` is **unchanged** and `RadarScoreFormulaV2.Version` stays
      `"radar-formula-v2"` (no `radar-formula-v3`, no AD-6 edit).
- [ ] `ScoringEngine.ScoringConfigVersion` is bumped to current-value + 1 (`"radar-scoring-config-v5"` →
      `"radar-scoring-config-v6"` today, since this slice merges before spec 84) with an updated comment
      recording this slice; `ScoringVersion`/`EngineVersion`/formula `Version` unchanged; any test asserting
      the old stamp is updated.
- [ ] No legacy-file migration/cleanup, no snapshot/report retention change, and no idempotent-persistence
      change are included (all out of scope / deferred).
- [ ] Layering (AD-5), files-first (AD-8), determinism (AD-3), AD-1 write semantics, AD-6 formula/window/
      activity-only rules, and AD-9 label/advice rules preserved; no Domain/DI-shape/collector/extractor/
      report change. `dotnet build`/`dotnet test` on `Radar.sln -c Release` green.

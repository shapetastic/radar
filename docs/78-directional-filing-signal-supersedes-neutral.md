# Task: Directional filing signal SUPERSEDES the deterministic Neutral GuidanceChange for the same filing

## Overview

This slice **resolves the deferred open question spec 75 left behind** (its "No double-count vs the
deterministic Neutral" section chose *coexist* and explicitly flagged the alternative — a single
`GuidanceChange` per filing — as "a deliberate follow-up: call it out, do not implement here"). Live AI
runs confirmed the coexistence is **noisy**: for the SAME earnings 8-K (item `2.02`), Radar emits **two**
`GuidanceChange` signals over the **same** `Filing` evidence — a deterministic **Neutral** (from
`KeywordSignalExtractor`, spec 57) *and* an AI-produced directional **Positive/Negative** (from
`DirectionalFilingSignalSource`, spec 75) — and both surface in the weekly report's "why Radar noticed"
block, so the same filing appears twice for one company.

**The refinement.** When a confidence-gated **directional** read exists for a given filing, it must
**supersede (replace)** the deterministic Neutral `GuidanceChange` for that **same filing evidence**,
rather than both being stored/scored/reported. When there is **no** directional read (AI disabled, below
`MinConfidence`, `Mixed`/`Unknown`, or any reader/analyzer failure), the deterministic Neutral **stands
unchanged** — this is a strict superset of today's behaviour, never a regression.

**Why it is precise and provenance-safe.** The two signals are distinct `Signal` rows (distinct `Id`s from
`ExtractedSignalMapper`, each `Guid.NewGuid()`), but they share the **same `EvidenceId`** (both map from the
same 8-K `EvidenceItem`) and the **same `Type == GuidanceChange`**. That pair — `(EvidenceId, GuidanceChange)`
— is the exact, unambiguous supersede key. The directional signal keeps its own reference to the same filing
evidence, so evidence→signal→score→report provenance is **preserved**: we replace one signal over the filing
with a better-informed signal over the *same* filing, we do not sever the evidence link.

**Why it changes scoring output (AD-10 trigger — load-bearing).** Under `radar-formula-v2` (AD-6) the Neutral
`GuidanceChange` contributes **0 to Trajectory** (excluded from numerator and denominator), so removing it does
**not** change `TrajectoryScore`. But the Neutral is **not** scoring-inert elsewhere:

- **SignalVelocityScore** sums signal `Strength` over the current/previous windows (AD-6); the Neutral carries
  `Strength 3`, so dropping it lowers the current-window `Strength` sum and therefore moves `SignalVelocityScore`
  (and, transitively, nothing in Opportunity which does not use velocity — but Velocity is a reported component).
- **The report's "why Radar noticed" block** emits one row per current-window signal contribution (AD-6:
  "one contribution per current-window signal in input order, including Neutral/Mixed, which naturally weigh
  0"), so removing the Neutral removes its (weight-0) contribution row — the filing stops appearing twice.
- **Run-summary counters** (`SignalsExtracted`/`SignalsValid`/`SignalsApproved`) and the persisted signal/review
  records change by one per superseded filing.

Because scoring output (Velocity component, contribution rows) can move, per **AD-10** this slice **MUST bump
`ScoringEngine.ScoringConfigVersion`** `"radar-scoring-config-v3"` → `"radar-scoring-config-v4"`. (As with spec
75: with AI **disabled** the output is byte-for-byte identical to v3 because no directional signal is ever
produced and nothing is superseded — but the stamp still bumps because the *generation* that CAN now emit
different scores has changed. Correct, conservative AD-10 behaviour.)

**Arc position.** Follows the completed directional-filing arc (72 seam → 73 reader → 74 analyzer → 75
directional signal). This is a refinement of 75's storage/dedup, not new AI capability — it adds **no** AI/HTTP
code and touches **no** provider SDK.

---

## Assignment

Worktree: any
Dependencies: **57** (the deterministic Neutral `GuidanceChange` for item `2.02` — the signal being
superseded), **75** (`IDirectionalFilingSignalSource` / `DirectionalFilingSignalSource` / the runner
enrichment step — the directional signal that supersedes), **22** (`RadarPipelineRunner` structure) — all
merged. Read spec 75's "No double-count vs the deterministic Neutral" section (the coexist decision this slice
reverses) and AD-6/AD-10 for the scoring-impact and version-bump rules.
Conflicts with: touches the pipeline **signal-production wiring** (`RadarPipelineRunner`), the signal
persistence contract (`ISignalRepository` + its in-memory impl + the on-disk `ISignalFileStore`), and
`ScoringEngine.ScoringConfigVersion`, plus their tests. It must **NOT** run in parallel with any
runner / scoring / signal-persistence / extractor / directional-filing slice — sequence it.
Estimated time: ~2–2.5 h

---

## Grounding facts (verified against the current tree — do NOT re-research)

- **The runner produces the Neutral FIRST, then the directional signal.** `RadarPipelineRunner.RunAsync`
  (`RadarPipelineRunner.cs`) runs the deterministic `extract → MapResolveReviewStoreAsync` loop over
  `newEvidence` (lines ~203–233) — which stores the Neutral `GuidanceChange` for a `2.02` filing — **then**
  the opt-in directional block (lines ~240–283) produces and stores the directional `GuidanceChange` over the
  **same** filing evidence. Both go through the shared `MapResolveReviewStoreAsync` tail (lines ~371–421),
  which stores the signal to `_signalRepository`, its review to `_signalReviewRepository`, and mirrors both to
  `_signalFileStore`.
- **Both signals share `EvidenceId` and `Type`.** `ExtractedSignalMapper.ToSignal` sets `EvidenceId =
  evidence.Id` (line 44) and assigns a fresh `Id = Guid.NewGuid()` (line 43). The Neutral (spec 57 rule
  `"results of operations"` → `GuidanceChange`/`Neutral`, `KeywordSignalExtractor.cs` line 116) and the
  directional signal (spec 75, `GuidanceChange`/`Positive|Negative`) therefore share `(EvidenceId,
  Type=GuidanceChange)` but differ in `Id` and `Direction`.
- **Signals are upsert-by-Id (AD-1); there is NO delete on `ISignalRepository` today.** `ISignalRepository`
  exposes `AddAsync` / `GetByIdAsync` / `GetByCompanyAsync` / `GetObservedBetweenAsync`
  (`ISignalRepository.cs`). The in-memory impl is a `ConcurrentDictionary<Guid, Signal>`
  (`InMemorySignalRepository.cs`). Removing the superseded Neutral requires **either** a new
  `RemoveAsync(Guid id)` on the repository (+ file store), **or** not-storing the Neutral in the first place
  (see the two options below).
- **The scoring engine loads ALL Approved in-window signals** via `GetByCompanyAsync` and filters by window +
  `ReviewStatus == Approved` (`ScoringEngine.cs` lines 75–82). A superseded Neutral that is left **stored and
  Approved** would still be counted (Velocity `Strength` sum, contribution row). So a supersede that only
  drops the directional-vs-Neutral *coexistence in the report* is insufficient — the Neutral must be gone from
  what scoring loads.
- **`ScoringConfigVersion` is currently `"radar-scoring-config-v3"`** (`ScoringEngine.cs` line 35).

---

## Design — RECOMMENDED: suppress-before-store (do NOT persist a Neutral that a directional read will replace)

I evaluated two supersede strategies against the real runner and persistence contract:

### Option A (REJECTED) — store both, then delete the Neutral

Store the Neutral in the extract loop as today, run the directional block, and for each produced directional
signal **delete** the previously-stored Neutral with the same `(EvidenceId, GuidanceChange)` from the signal
repository, its review, and its on-disk file. This requires adding `RemoveAsync` to `ISignalRepository` +
`ISignalReviewRepository` + `ISignalFileStore` (three new delete contracts + in-memory impls + on-disk file
deletion), leaves an orphaned-then-deleted audit record mid-run, and mutates already-reviewed, already-mirrored
state. **Rejected**: larger persistence-contract surface, deletes an immutable-feeling audit record, and the
on-disk file deletion is error-prone (the file stores are best-effort/swallow-errors, so a failed delete would
silently re-introduce the duplicate on disk).

### Option B (CHOSEN) — suppress the Neutral before it is stored

Because the runner **already collects** all newly-stored `Filing` evidence into `candidates` for the
directional block, and the directional source runs in the same `RunAsync`, we can decide *up front* which
filings will receive a directional read and **skip storing the deterministic Neutral `GuidanceChange`** for
exactly those filings. Nothing is ever double-stored, nothing is deleted, no new delete contract is needed, and
the on-disk twin is correct by construction.

**The precise mechanism.** Restructure `RunAsync` so the directional enrichment produces its signals **before**
the deterministic extract loop stores signals for the affected filings — then, in the extract loop, drop the
Neutral that a directional read superseded. Concretely (RECOMMENDED ordering):

1. **Compute the directional signals first** (only when `_directionalFilingSignals is not null`): call
   `ProduceAsync(filingCandidates, asOfUtc, ct)` and materialise the result. Build the **supersede set** =
   the distinct `EvidenceId`s of the produced directional `GuidanceChange` signals. (Every produced signal is
   a directional `GuidanceChange` per spec 75, but key on `(EvidenceId, Type)` to be exact and future-proof.)
2. **Run the deterministic extract loop** as today, but when about to store a signal, **skip** any extracted
   signal that is a `GuidanceChange` whose evidence `Id` is in the supersede set. That is the Neutral being
   superseded; the directional signal will carry the filing's `GuidanceChange` instead. Count the skip at
   `Debug` (do not bump the valid/approved counters for a suppressed signal — see counters below).
3. **Store the directional signals** through the same `MapResolveReviewStoreAsync` tail (unchanged from spec 75).

This keeps the supersede **exact** (same `EvidenceId` + same `Type`), **provenance-safe** (the directional
signal references the same filing evidence), and **persistence-minimal** (no delete contract, no orphan audit
record, on-disk twin correct). The deterministic keyword extractor is **untouched** — the suppression is a
runner-level *storage* decision, not an extractor rule change (deterministic-before-AI boundary preserved:
the keyword layer still *extracts* the Neutral; the runner just doesn't *persist* it when a better directional
read for that same filing exists).

> **Ordering note (important).** Today the runner captures `asOfUtc` after collection, then runs the extract
> loop, then the directional block. To suppress-before-store, the directional `ProduceAsync` must run **before**
> the extract loop stores its signals. `asOfUtc` is already captured before both (line ~198), so moving the
> `ProduceAsync` call earlier does **not** change the run instant or window semantics (AD-7 preserved). Only the
> *storage* order within the run changes; `ProduceAsync` itself has no persistence side effects (spec 75's
> source only reads/analyzes and returns `DirectionalFilingSignal`s).

### What the suppression looks like (sketch — the implementer refines)

```csharp
// Compute directional filing signals FIRST (opt-in; empty when AI disabled) so the extract loop knows
// which filings' deterministic Neutral GuidanceChange to suppress. No persistence side effects here.
IReadOnlyList<DirectionalFilingSignal> directional = [];
HashSet<Guid> supersededFilingEvidenceIds = [];
if (_directionalFilingSignals is not null)
{
    var filingCandidates = newEvidence
        .Where(e => e.Evidence.SourceType == EvidenceSourceType.Filing)
        .ToList();
    directional = await _directionalFilingSignals
        .ProduceAsync(filingCandidates.Select(e => e.Evidence).ToList(), asOfUtc, ct).ConfigureAwait(false);
    // Supersede key: a directional GuidanceChange REPLACES the deterministic GuidanceChange over the SAME
    // filing evidence. Key on EvidenceId (the directional signals are all GuidanceChange by construction).
    supersededFilingEvidenceIds = directional
        .Where(d => d.Signal.SignalType == nameof(SignalType.GuidanceChange)) // defensive; all are today
        .Select(d => d.Evidence.Id)
        .ToHashSet();
}

// Deterministic extract loop (unchanged), except: skip storing a GuidanceChange whose filing evidence a
// directional read superseded.
foreach (var entry in newEvidence)
{
    var evidence = entry.Evidence;
    var output = await _extractor.ExtractAsync(evidence, ct).ConfigureAwait(false);
    foreach (var extracted in output.Signals)
    {
        if (IsSupersededGuidanceChange(extracted, evidence, supersededFilingEvidenceIds))
        {
            _logger.LogDebug(
                "Suppressing deterministic Neutral GuidanceChange for filing evidence {EvidenceId}: " +
                "a directional filing read supersedes it.", evidence.Id);
            continue; // do NOT store; do NOT bump valid/approved counters
        }
        signalsExtracted++;
        // ... existing MapResolveReviewStoreAsync + counter switch, unchanged ...
    }
}

// Store the directional signals (unchanged from spec 75).
foreach (var d in directional) { /* signalsExtracted++; MapResolveReviewStoreAsync(...) ... */ }
```

`IsSupersededGuidanceChange` = the extracted signal's `SignalType` parses to `GuidanceChange` **and**
`supersededFilingEvidenceIds.Contains(evidence.Id)`. Factor it into a small private helper. Do **not** parse
the direction — any `GuidanceChange` over a superseded filing (which today is only ever the Neutral) is
replaced by the directional read; keying on type + evidence is sufficient and precise. (State in the PR that,
by construction, the only deterministic `GuidanceChange` an item-`2.02` filing produces today is the spec-57
Neutral, so this suppresses exactly that one signal and nothing on non-filing evidence.)

**Counters.** A suppressed Neutral is never mapped/stored, so it must **not** increment
`signalsExtracted`/`signalsValid`/`signalsApproved` — it is replaced, not dropped-as-invalid. The directional
signal increments the counters exactly as spec 75 already does. Net effect per superseded filing: one
`GuidanceChange` counted (the directional), not two.

---

## Project structure changes

```text
src/Radar.Application/Pipeline/
  RadarPipelineRunner.cs          # MODIFIED: run ProduceAsync BEFORE the extract loop stores signals; build
                                  #   the supersede set (EvidenceIds of directional GuidanceChange signals);
                                  #   skip storing a GuidanceChange whose filing evidence is superseded; store
                                  #   directional signals after. New private IsSupersededGuidanceChange helper.
                                  #   asOfUtc capture + window semantics UNCHANGED.

src/Radar.Application/Scoring/
  ScoringEngine.cs                # MODIFIED: bump ScoringConfigVersion "radar-scoring-config-v3" ->
                                  #   "radar-scoring-config-v4"; update the comment to record this slice
                                  #   (directional supersedes Neutral -> Velocity/contribution-row output moves).

tests/Radar.Application.Tests/Pipeline/
  RadarPipelineRunnerTests.cs     # MODIFIED: supersede + no-supersede cases (see Tests).

tests/Radar.Application.Tests/Scoring/
  (existing scoring tests)        # MODIFIED: update the ScoringConfigVersion assertion to v4.
```

`Radar.Domain` is unchanged. `ISignalRepository` / `ISignalReviewRepository` / `ISignalFileStore` are
**unchanged** (Option B needs no delete contract). No DB (AD-8). No new package references. No provider SDK.

---

## Implementation details

### `RadarPipelineRunner` (Application)

- Move the directional `ProduceAsync` call to **before** the deterministic extract loop stores signals (see
  the sketch). `asOfUtc` is already captured earlier (line ~198) — do **not** move its capture; only the
  storage/ordering within the run changes. `ProduceAsync` has no persistence side effects, so calling it
  earlier is safe.
- Preserve the existing `hintsByEvidenceId` mechanism (line ~251) for resolving directional signals with the
  filing's collector hints — reuse the same dictionary regardless of the reordering.
- Build `supersededFilingEvidenceIds` from the produced directional signals (distinct `EvidenceId` of the
  `GuidanceChange` directional signals). When `_directionalFilingSignals is null` (AI disabled) the set is
  empty and **no** signal is ever suppressed — the extract loop behaves exactly as today.
- Add the private `IsSupersededGuidanceChange(ExtractedSignal, EvidenceItem, HashSet<Guid>)` helper (parse the
  `SignalType` to `SignalType.GuidanceChange` using the same tolerant parse the mapper uses — or a direct
  string compare against `nameof(SignalType.GuidanceChange)`; pick one and keep it defensive against unknown
  types). Suppress only when it is a `GuidanceChange` **and** the evidence `Id` is in the set.
- Keep the shared `MapResolveReviewStoreAsync` tail and the `SignalStoreOutcome` counter switch unchanged.
- A suppressed signal increments **no** counter (it is not stored). Log it at `Debug`.

### `ScoringEngine` (Application)

- Bump `ScoringConfigVersion` `"radar-scoring-config-v3"` → `"radar-scoring-config-v4"`. Update the constant's
  comment to record: "This generation ships the directional-filing supersede (spec 78): a confidence-gated
  directional `GuidanceChange` replaces the deterministic Neutral `GuidanceChange` for the same filing, so the
  Neutral no longer contributes to `SignalVelocityScore` or emits a (weight-0) contribution row — Velocity and
  report content move. With AI disabled nothing is superseded and output is identical to v3, but the stamp
  bumps because the generation that CAN now emit different scores has changed (AD-10)." Do **not** touch
  `ScoringVersion`, `EngineVersion`, or `RadarScoreFormulaV2.Version` — the formula math is unchanged.

### No AD-6 formula change

The scoring **formula** is untouched: Neutral still contributes 0 to Trajectory and one contribution row *when
present*. This slice changes **which signals are present** (the Neutral is no longer stored when superseded),
not the math. No AD-6 update; the version bump is the AD-10 `ScoringConfigVersion` stamp only.

---

## Tests

### `RadarPipelineRunnerTests` (Application.Tests/Pipeline)

Use a fake `IDirectionalFilingSignalSource` (as spec 75's runner tests do) and the existing test builders.

1. **Directional read SUPERSEDES the Neutral (the core case).** Given an in-window earnings-`2.02` `Filing`
   evidence for which (a) the deterministic extractor yields a Neutral `GuidanceChange` and (b) the fake
   directional source returns one `Positive` (or `Negative`) `GuidanceChange` over the **same** evidence: after
   the run the signal repository holds **exactly one** `GuidanceChange` for that `EvidenceId`, and it is the
   **directional** one (`Direction == Positive`/`Negative`, not `Neutral`). The Neutral is **not** stored (no
   signal, no review, no on-disk file for it). Provenance holds: the stored directional signal's `EvidenceId`
   == the filing evidence `Id`.
2. **No directional read → Neutral STANDS (regression / superset guarantee).** Same filing, but the fake
   directional source returns **empty** (models below-`MinConfidence`/`Mixed`/`Unknown`/failure). After the
   run the Neutral `GuidanceChange` **is** stored exactly as today. (Guards that suppression only happens when
   a directional read actually exists.)
3. **AI disabled (null source) → byte-for-byte unchanged.** With `_directionalFilingSignals == null` the run
   behaves exactly as today: the Neutral is stored, no directional signal, existing assertions unchanged. (The
   supersede set is empty; nothing is suppressed.)
4. **Suppression is scoped to the superseded filing's `GuidanceChange` only.** A *second* in-window filing (or
   a press-release evidence) that the directional source did **not** cover keeps **all** its deterministic
   signals — including any `GuidanceChange` on a *different* `EvidenceId`. Only the filing whose `EvidenceId`
   is in the supersede set loses its Neutral. Non-`GuidanceChange` deterministic signals on the superseded
   filing (if any) are **not** suppressed.
5. **Counters.** With one superseded Neutral replaced by one directional signal, `SignalsExtracted` counts the
   `GuidanceChange` **once** (the directional), not twice; `SignalsApproved`/`SignalsValid` reflect the single
   directional signal (assuming it approves) — i.e. the suppressed Neutral contributes to **no** counter.
6. **Cancellation still honoured** across the reordered blocks (already-cancelled token → `RunAsync` throws
   `OperationCanceledException` before storing anything).

### Scoring (Application.Tests/Scoring)

7. **`ScoringConfigVersion` stamp.** Update the assertion(s) expecting `"radar-scoring-config-v3"` to
   `"radar-scoring-config-v4"`. (`ScoringVersion`/`EngineVersion`/formula `Version` assertions unchanged.)
8. **Velocity reflects the supersede (optional but recommended).** A window whose only filing signal is a
   directional `Positive` `GuidanceChange` (Neutral suppressed) yields a `SignalVelocityScore` consistent with
   **one** signal of `Strength 6` in the window — not the two-signal (`3 + 6`) sum that coexistence produced.
   (If a direct end-to-end assertion is awkward, assert at minimum that the scored company's contributing
   signals include the directional `GuidanceChange` and **not** a Neutral `GuidanceChange` over the same
   evidence.)

### Regression (must stay green)

- Existing `DirectionalFilingSignalSourceTests` (spec 75) are **unchanged** — the source's produce logic is
  untouched; only the runner's storage ordering changed.
- Existing `KeywordSignalExtractorTests` are **unchanged** — the extractor still *emits* the Neutral; only the
  runner's decision to *store* it changed.
- Existing report/DI/end-to-end tests stay green; the AI-disabled default path is byte-for-byte unchanged.

All emitted text stays advice-free (AD-9). No banned tokens.

---

## Spec-implementation checklist

1. **Code paths replaced:** the spec-75 "coexist" storage behaviour for the Neutral-vs-directional
   `GuidanceChange` over one filing is replaced by "directional supersedes Neutral" (Option B: suppress the
   Neutral before store). The keyword extractor and the directional source are **untouched**; only the runner's
   storage/ordering changed.
2. **Tests:** add runner supersede/no-supersede/null-source/scope/counter/cancellation cases (1–6); update the
   `ScoringConfigVersion` assertion (7) and, recommended, the velocity/contributing-signal assertion (8).
3. **Delete nothing still used.** No new delete contract is added (Option B avoids it).
4. **CLAUDE.md / architecture-decisions.md:** no new architecture rule. This realises the follow-up spec 75
   deferred; it follows AD-1 (persistence unchanged — no delete), AD-5 (no AI/provider change), AD-6 (formula
   unchanged), AD-8 (files-first), AD-10 (`ScoringConfigVersion` bump). Note in the PR that spec 75's
   "coexist" open question is now resolved (superseded). No new AD entry required; optionally add a one-line
   note under AD-6/AD-10 cross-referencing this resolution if the maintainer wants it recorded.
5. **Bump `ScoringEngine.ScoringConfigVersion`** to `"radar-scoring-config-v4"` (AD-10) — done in this slice.

---

## Constraints

- Target `net10.0`, C# 14.
- **AD-10:** bump `ScoringEngine.ScoringConfigVersion` `"radar-scoring-config-v3"` → `"radar-scoring-config-v4"`
  in this same slice (scoring output can move: Velocity + contribution rows). Do **not** touch
  `ScoringVersion`/`EngineVersion`/formula `Version`.
- **AD-1 preserved:** no signal is deleted or mutated after store; the supersede is *suppress-before-store*, so
  the signal/review repositories and the on-disk twin keep insert/upsert-by-Id semantics with no new delete
  contract.
- **Provenance preserved:** the surviving directional `GuidanceChange` references the **same** filing
  `EvidenceId` the suppressed Neutral would have — evidence→signal→score→report is intact; only the *duplicate*
  signal over that filing is removed.
- **Deterministic-before-AI preserved:** the `KeywordSignalExtractor` is untouched (it still extracts the
  Neutral); the runner suppresses *storing* it only when a confidence-gated directional read for the same
  filing exists. AI/HTTP stays behind the spec-75 Infrastructure interfaces (AD-5); this slice adds no provider
  SDK.
- **Opt-in / no default change:** with AI disabled (`_directionalFilingSignals == null`) the supersede set is
  empty, nothing is suppressed, and the default pipeline/output is byte-for-byte unchanged — asserted by a test.
- **Files-first, no DB (AD-8).** No advice language; AD-9 labels unchanged.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green.

---

## Out of scope / future (do NOT implement this round)

- **A general per-filing "one signal per `(EvidenceId, Type)`" dedup rule** across all signal types/sources.
  This slice narrowly supersedes the deterministic `GuidanceChange` with the directional `GuidanceChange` for
  the same filing; it does **not** introduce a generic cross-type dedup engine.
- **Deleting already-stored signals** (Option A) — rejected here; if a future capability genuinely needs to
  remove a persisted signal, that warrants its own spec adding a `RemoveAsync` contract deliberately.

---

## Acceptance criteria

- [ ] When the opt-in directional filing source produces a directional `GuidanceChange` (`Positive`/`Negative`)
      for an in-window earnings-`2.02` `Filing` evidence, the deterministic **Neutral** `GuidanceChange` for
      that **same filing evidence** is **not stored** — the run persists **exactly one** `GuidanceChange`
      (the directional one) for that `EvidenceId`, in the signal repository, its review, and the on-disk twin.
- [ ] When there is **no** directional read for a filing (AI disabled, below `MinConfidence`, `Mixed`/`Unknown`,
      or any reader/analyzer failure ⇒ the source returns nothing for it), the deterministic Neutral
      `GuidanceChange` **stands unchanged** (strict superset of prior behaviour, no regression).
- [ ] The supersede is **exact and scoped**: it removes only a `GuidanceChange` whose filing `EvidenceId` a
      directional read covered; `GuidanceChange` signals on other evidence, and non-`GuidanceChange` signals on
      the superseded filing, are unaffected. Provenance holds — the surviving directional signal references the
      same filing `EvidenceId`.
- [ ] The supersede uses **suppress-before-store** (Option B): no signal is deleted or mutated after storage;
      `ISignalRepository`/`ISignalReviewRepository`/`ISignalFileStore` gain **no** new delete contract; AD-1
      persistence semantics and the on-disk twin are unchanged. `asOfUtc`/window semantics (AD-7) unchanged.
- [ ] Run-summary counters count the filing's `GuidanceChange` **once** (the directional), never twice; a
      suppressed Neutral increments no counter.
- [ ] **AI disabled (null source) ⇒ byte-for-byte unchanged**: the supersede set is empty, nothing is
      suppressed, the Neutral is stored as today — asserted by a runner test.
- [ ] `ScoringEngine.ScoringConfigVersion` is bumped `"radar-scoring-config-v3"` → `"radar-scoring-config-v4"`
      (AD-10) with an updated explanatory comment; `ScoringVersion`/`EngineVersion`/formula `Version` are
      unchanged; every test asserting the old stamp is updated.
- [ ] Offline runner tests cover: supersede, no-supersede (Neutral stands), null-source no-op, scoped
      suppression, counters, and cancellation; the `ScoringConfigVersion` assertion is updated (and, recommended,
      a velocity/contributing-signal assertion reflects the single surviving signal). No advice language.
- [ ] `dotnet build` / `dotnet test` on `Radar.sln -c Release` are green. Spec 75's deferred "coexist" open
      question is resolved (directional supersedes Neutral) — note this in the PR.

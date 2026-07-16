# Task: Config-bind the AI directional-filing signal magnitudes and fold the AI signal source into the scoring fingerprint

## Overview

The AI directional-filing path (`DirectionalFilingSignalSource` → `IFilingAnalyzer`) is the **only**
signal path that can lift a company's Opportunity above the keyword-only ceiling — the reliability
investigation behind spec 105 established this: the `KeywordSignalExtractor` caps keyword signals at
strength 6 / confidence 0.65, so a keyword-only company cannot exceed **Opportunity ≈ 52**, below the
Investigate gate of 60. Only an AI-verified `GuidanceChange` (confidence 0.9–0.95) can clear it.

But the AI signal's scoring-affecting magnitudes — `Strength`, `Novelty`, `MinConfidence` — are
**hard-coded constants** in `DirectionalFilingSignalOptions`; they are **not wired through**
`AddDirectionalFilingSignals`, so they cannot be tuned from a run profile (spec 105 deferred follow-up
#1: *"making it config-bindable — it currently is not wired through AddDirectionalFilingSignals"*).

Making them tunable exposes a second, pre-existing correctness gap. **The AI directional-filing signal
source is invisible to the scoring fingerprint.** `SignalSourceDescriptor` (spec 95) folds "the run's
signal-production surface" into `ScoringConfigVersion` — but it enumerates only `IEvidenceCollector`, so
it never sees `IDirectionalFilingSignalSource`. Consequently a run **with** the AI path (which emits
directional `GuidanceChange` signals that move `TrajectoryScore`) and a run **without** it carry the
**same** fingerprint and are **falsely judged comparable** — the exact spec-69/AD-10 defect the stamp
exists to prevent, and precisely the shape spec 95 fixed for the `secform4` collector gap. If we make
`Strength`/`MinConfidence`/`Novelty` runtime-tunable **without** fingerprinting them, we widen that gap:
two runs at `Strength 6` vs `Strength 9` would then share a fingerprint and be wrongly compared.

This spec does both together, because they are one AD-10-correct unit: **make the magnitudes
config-tunable AND fold the AI signal source (its enablement + its per-signal magnitudes) into the
existing signal-source descriptor by value.** This is the enabling plumbing for the deferred,
measurement-gated `Strength` recalibration — it is **not** that recalibration.

> This spec is **not a scoring recalibration**. Every default is unchanged, so **default scoring output
> is byte-identical**. The formula, weights, `RuleSetVersion`, and `_formula.Version` do not move. Only
> the fingerprint *input* widens (exactly as specs 95/96 did): the **AI-OFF** default fingerprint stays
> byte-for-byte `radar-scoring-fp-c9e609ed53e9`, while the **AI-ON** live run re-stamps automatically
> (restoring comparability across the AI-on/off transition). The actual `Strength` value change is
> spec 105 deferred #1 and stays deferred pending a post-105 live re-measure.

---

## Assignment

Worktree: unassigned (ready to dispatch)
Dependencies: **Spec 105 is now MERGED (PR #107, `72e8696` on main) — dependency satisfied.** This spec
edits `RadarWorkerOptions.cs` / `RadarWorkerServices.cs` (the AI directional-filing registration) and
**deliberately moves the AI-ON default fingerprint**. Spec 105 (which asserted the default fingerprint
must NOT move) has landed, so the earlier conflict is resolved on trunk. Plan/implement against the
current `origin/main` tip (`72e8696`), not the `3ee408c` referenced in the grounding facts below.
Conflicts with: None outstanding (spec 105 merged; re-verify grounding facts against `72e8696`).
Estimated time: ~1-2 hours

---

## Grounding facts (verified on disk @ `3ee408c`)

**The magnitudes** — `src/Radar.Infrastructure/Filings/DirectionalFilingSignalOptions.cs`:
`MinConfidence = 0.6m` (confidence gate), `MaxFilingsPerRun = 5` (cost cap), `Strength = 6`,
`Novelty = 6`. The reader is `DirectionalFilingSignalSource` (same folder); it stamps each emitted
`ExtractedSignal` with `_options.Strength` / `_options.Novelty` and gates on `_options.MinConfidence`.

**The registration** — `AddDirectionalFilingSignals` (find it via
`grep -rn "AddDirectionalFilingSignals" src/`; it lives with the Infrastructure DI extensions and is
called from the Worker's AI-enabled gate, AD-11/AD-12). It currently news-up
`DirectionalFilingSignalOptions` with hard-coded defaults rather than binding from config.

**The signal-source descriptor** — `src/Radar.Application/Scoring/SignalSourceDescriptor.cs`
(spec 95): builds `rules={KeywordSignalExtractor.RuleSetVersion};collectors={csv};` ONCE at
construction from `IEnumerable<IEvidenceCollector>` (reads only `CollectorName`, never collects).
It is injected into `ScoringEngine` as `ISignalSourceDescriptor` and its `CanonicalDescriptor()` is
appended to the fingerprint as the `srcDesc` field (`ScoringConfigFingerprint.Compute`, after
`attnDesc`, before `insiderDesc`). `EffectiveScoringConfig.SignalSourceDescriptor` carries it verbatim
for content-addressed persistence (spec 91) — so anything folded into `srcDesc` is persisted and
self-verifying **for free**, with **no** new fingerprint field and **no** `EffectiveScoringConfig`
change.

**The AI source seam** — `src/Radar.Application/Filings/IDirectionalFilingSignalSource.cs` is the
Application interface; the Infra impl is opt-in and registered **only** when AI is enabled
(`Radar:Ai:Provider` non-blank, AD-11/AD-12). When AI is off it is not in the DI graph at all.

---

## Design

### 1. Make the four magnitudes config-bindable (spec-105 deferred #1, enabling half)

- Surface `DirectionalFilingSignalOptions` through `RadarWorkerOptions` under the existing AI /
  directional-filing section (wherever the AI reader's knobs live), bound in `AddDirectionalFilingSignals`
  from `Radar:Ai:*` (or the section already used), **defaulting to today's exact values**
  (`MinConfidence 0.6`, `MaxFilingsPerRun 5`, `Strength 6`, `Novelty 6`).
- Keep the existing registration-time validation (`AddDirectionalFilingSignals` already validates the
  options — mirror it): fail fast on `MinConfidence` outside `[0,1]`, `MaxFilingsPerRun <= 0`,
  `Strength`/`Novelty` outside the signal's valid `[1,10]` range. A blank/absent config MUST reproduce
  the current constants byte-for-byte (pinned by test).
- This half is pure plumbing: with defaults unchanged, the emitted signals are byte-identical, so
  scoring output does not move.

### 2. Fold the AI signal source into the signal-source descriptor (AD-10 correctness — spec-95 mechanism)

Reuse the **existing** `srcDesc` field rather than adding a new fingerprint field (chosen over a
spec-96-style separate `aiFilingDesc` field to minimise fingerprint surface area — the AI source is part
of "the enabled signal-source set" that spec 95 is meant to capture, so it belongs in `srcDesc`):

- Add `string ScoringDescriptor()` to `IDirectionalFilingSignalSource` (parallel to how spec 95 reads
  `CollectorName` off `IEvidenceCollector`). The Infra impl returns a canonical string built from its
  options, e.g. `directional-filing:str={Strength};nov={Novelty};minconf={MinConfidence:R}` using
  `InvariantCulture` round-trip number formatting (AD-3). `MaxFilingsPerRun` is a **cost cap / operational
  scaffolding** (like `ScoringWindowDays`, which spec 105 confirmed is deliberately NOT a fingerprint
  input); **exclude it** from the descriptor and document that exclusion in a comment. Only the per-signal
  magnitudes that set an emitted signal's Strength/Novelty/Confidence-gate are hashed.
- Extend `SignalSourceDescriptor` with an **optional** ctor parameter `IDirectionalFilingSignalSource?
  aiFilingSource = null`. When non-null, append `ai={Escape(source.ScoringDescriptor())};` to the
  descriptor **after** the `collectors=…;` segment (fixed field ordering, AD-3), reusing the existing
  `Escape` for delimiter-injectivity. When null (AI off), append nothing — so the **AI-OFF descriptor is
  byte-identical to today** and the pinned AI-OFF default fingerprint stays `radar-scoring-fp-c9e609ed53e9`.
- Wire the optional dependency in `AddRadarApplicationServices` (where `SignalSourceDescriptor` is
  `TryAddSingleton`'d): resolve `IDirectionalFilingSignalSource?` from the container
  (`GetService<IDirectionalFilingSignalSource>()`) so it is the Infra impl when AI is registered and
  `null` otherwise. Follow the spec-95 lazy-resolution note (the AI source, like collectors, may be
  registered by the Worker after `AddRadarApplicationServices`) — resolve it inside the factory lambda,
  not eagerly.

### Why this is correct and consistent

- `ScoringConfigFingerprint.Compute`'s **signature is unchanged** (srcDesc already a parameter);
  `EffectiveScoringConfig` is **unchanged** (its `SignalSourceDescriptor` field already carries `srcDesc`
  verbatim, so the AI descriptor is persisted and self-verifying automatically — spec 91 invariant holds).
- Enabling/disabling the AI path now re-stamps the fingerprint **automatically**, closing the
  comparability gap (the AI analogue of spec 95's `secform4` fix). Tuning `Strength`/`Novelty`/`MinConfidence`
  from a profile now re-stamps **by value**, so the deferred recalibration cannot silently produce
  falsely-comparable snapshots.
- No `_formula.Version`, no weight, no `RuleSetVersion`, no attention/insider-tier change. `radar-formula-v5`
  and all existing descriptors stay put.

### 3. The default-fingerprint pins

- The **AI-OFF** default-config fingerprint pin (the existing pinned test — verify it constructs the
  descriptors **without** the AI source) MUST stay `radar-scoring-fp-c9e609ed53e9`, untouched. If that
  test currently includes the AI source, that is itself the bug this spec fixes — but the *default*
  (opt-in-off) fingerprint must remain `c9e609ed53e9`.
- The **AI-ON** live-run fingerprint will re-stamp (AI now contributes to `srcDesc`). There is no pinned
  unit test for the live AI-on fingerprint; note in the PR body that `scripts/run-profiles/default.json`
  and the maintainer's baseline notes track a new AI-on default stamp, to be recomputed on the next live
  run (ops follow-up, not a code pin). Historical AI-on snapshots stamped `c9e609ed53e9` keep their
  recorded stamp and remain reproducible (AD-10 mechanism — snapshots are never rewritten).

---

## Project structure changes

- `src/Radar.Infrastructure/Filings/DirectionalFilingSignalOptions.cs` — MODIFIED: no shape change;
  it becomes the bound options object (values now come from config, defaults unchanged).
- `src/Radar.Application/Filings/IDirectionalFilingSignalSource.cs` — MODIFIED: add
  `string ScoringDescriptor()`.
- `src/Radar.Infrastructure/Filings/DirectionalFilingSignalSource.cs` — MODIFIED: implement
  `ScoringDescriptor()` from `_options` (per-signal magnitudes only; exclude `MaxFilingsPerRun`).
- `src/Radar.Application/Scoring/SignalSourceDescriptor.cs` — MODIFIED: optional
  `IDirectionalFilingSignalSource?` ctor param; append escaped `ai=…;` when present.
- The Infrastructure DI extension that owns `AddDirectionalFilingSignals` — MODIFIED: bind + validate the
  four options from config.
- `src/Radar.Application` DI registration for `SignalSourceDescriptor` (`AddRadarApplicationServices`) —
  MODIFIED: pass the optional AI source (lazy-resolved).
- `src/Radar.Worker/RadarWorkerOptions.cs` + `RadarWorkerServices.cs` — MODIFIED: surface the four
  tunables (defaults 0.6 / 5 / 6 / 6).
- Tests — see below.

---

## Tests

- **Descriptor without AI source** (`SignalSourceDescriptorTests`): a null `aiFilingSource` yields the
  exact spec-95 descriptor (`rules=…;collectors=…;`) — no `ai=` segment. (Backward-compat / AI-off parity.)
- **Descriptor with AI source**: a stub `IDirectionalFilingSignalSource` returning a known
  `ScoringDescriptor()` appends exactly `ai={escaped};` after `collectors=…;`, and a reserved delimiter in
  the descriptor is escaped (injectivity).
- **AI-OFF default fingerprint pin** stays `radar-scoring-fp-c9e609ed53e9` (unchanged).
- **AI-ON fingerprint differs** from the AI-OFF fingerprint (comparability gap closed) — and re-stamps
  when `Strength` changes (fold-by-value proof).
- **Options binding**: a `Radar:*` config sets each of the four fields; a blank config reproduces
  `0.6 / 5 / 6 / 6`; each invalid value (`MinConfidence` out of `[0,1]`, `MaxFilingsPerRun <= 0`,
  `Strength`/`Novelty` out of `[1,10]`) fails fast at registration.
- **`ScoringDescriptor()` excludes `MaxFilingsPerRun`**: changing only `MaxFilingsPerRun` does not change
  the descriptor (parity with the operational-scaffolding rule).
- **Default scoring output byte-identical**: an existing end-to-end / formula-equivalence test that runs
  with defaults still produces the same scores (no math moved).

Do not weaken existing `SignalSourceDescriptorTests`, the fingerprint tests, or the directional-filing
source tests except to add the new cases above.

---

## Constraints

- Target `.NET 10` / `net10.0`, C# 14.
- **Not a scoring recalibration.** Defaults unchanged ⇒ default scoring byte-identical. No
  `_formula.Version` / `RuleSetVersion` / weight / attention-tier / insider-tier change. `radar-formula-v5`
  stays.
- **AI-OFF default fingerprint pin stays `radar-scoring-fp-c9e609ed53e9`.** Only the AI-ON live stamp
  moves (documented; no code pin for it).
- Layering (AD-5): magnitudes/values stay in `Radar.Infrastructure` (`DirectionalFilingSignalOptions`);
  only a `string` descriptor crosses the `IDirectionalFilingSignalSource` seam (exactly as `CollectorName`
  crosses `IEvidenceCollector`). No provider SDK leakage.
- Reuse-over-copy: reuse the existing `srcDesc` field and `SignalSourceDescriptor.Escape`; do not add a
  new fingerprint field or a second escaping/serialization idiom.
- Determinism (AD-3): `InvariantCulture` round-trip number formatting in `ScoringDescriptor()`; fixed
  field ordering; descriptor built once at construction.
- Preserve the AI opt-in posture (AD-11/AD-12): AI off ⇒ AI source not registered ⇒ descriptor and graph
  byte-for-byte unchanged.

---

## Acceptance criteria

- [ ] `DirectionalFilingSignalOptions` (`MinConfidence`, `MaxFilingsPerRun`, `Strength`, `Novelty`) is
      config-bound through `RadarWorkerOptions` / `AddDirectionalFilingSignals`, defaults `0.6 / 5 / 6 / 6`,
      fail-fast validation, blank config == today's constants.
- [ ] `IDirectionalFilingSignalSource.ScoringDescriptor()` exists; the Infra impl builds it from the
      per-signal magnitudes (`Strength`/`Novelty`/`MinConfidence`), excluding `MaxFilingsPerRun`, with
      `InvariantCulture` formatting.
- [ ] `SignalSourceDescriptor` appends an escaped `ai=…;` segment iff the AI source is registered; the
      AI-off descriptor is byte-identical to today.
- [ ] Enabling the AI path re-stamps `ScoringConfigVersion` automatically; tuning `Strength`/`Novelty`/
      `MinConfidence` re-stamps by value. `ScoringConfigFingerprint.Compute` and `EffectiveScoringConfig`
      are unchanged.
- [ ] The AI-OFF default-fingerprint pin remains `radar-scoring-fp-c9e609ed53e9`.
- [ ] New tests cover: AI-off descriptor parity, AI-on descriptor + escaping, AI-off vs AI-on fingerprint
      divergence, fold-by-value on `Strength`, options binding + validation, `MaxFilingsPerRun` exclusion,
      default scoring byte-identical.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` both green.

---

## Deferred follow-ups (NOT this spec)

1. **The actual `Strength` recalibration** (spec 105 deferred #1): raising `Strength` above the keyword
   ceiling so an AI-verified guidance change lifts Trajectory on merit. Scoring-affecting ⇒ requires a
   post-105 live re-measure to pick the value; do it AFTER this plumbing lands and after a live run
   confirms spec 105 restored AI supply. This spec makes that a config edit that re-stamps automatically.
2. **Do not lower the Investigate threshold (60)** — spec 105 deferred #3 (unchanged).

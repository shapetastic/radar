# Task: Make the insider buy/sell materiality tiers config-tunable (and fingerprinted)

## Overview

Spec 93 fed `SignalType.InsiderBuying` as a directional signal: the `secform4` collector classifies each
Form 4 (open-market purchase `P` → Positive, discretionary sale `S` → Negative, 10b5-1/grant/mixed →
Neutral) and writes an `insiderNetValue` metadata key; the deterministic `KeywordSignalExtractor` then scales
the signal's **Strength** by that value through a materiality tier table
(`InsiderNetValueTiers`) plus a `+1` multi-insider cluster boost. The 2026-07-05 live re-measure validated
this works directionally (buys → Positive, sells → Negative; e.g. CYRX's ~$2.76M sale → Strength 7).

Today those magnitudes are **code constants** in `KeywordSignalExtractor.cs`
(`InsiderNetValueTiers`, the `Math.Min(10, strength + 1)` cluster boost), so tuning the **buy-vs-sell
asymmetry** requires a code change. This slice moves them into **config**, exactly the pattern spec 89 used to
move the scoring formula magnitudes into `ScoringWeights` and spec 94 used to sweep `MediaReachWeight`: the
buy tier table, the sell tier table, and the cluster boost become bindable from a run profile — so an
experiment can be run with **no code change** and routed to `data/experiments/{profile}/` (per
`scripts/run-radar.ps1`).

**Design rationale to bake into the code comments / spec (not a behaviour change here).** Insider **buying**
is the informative, on-thesis side — an insider spending their own money is a costly, deliberate bullish
signal. Insider **selling** is noisy: diversification, taxes, liquidity, and scheduled vesting dominate, and
the collector already forces 10b5-1/planned sales to Neutral. The *purpose* of making these tunable is to let
us test giving **buys more lift than sells** (an asymmetric tier table) — **NOT** to reflexively over-weight
sells. Today's single `InsiderNetValueTiers` table is symmetric across buy and sell; splitting it into
separate buy and sell tables (both defaulting to the current values) is what makes the asymmetry
experiment possible.

Following the CLAUDE.md rule: this is a **magnitude → config** move, so the formula/rule *structure* stays
versioned code and **no formula version bumps**. Because the insider tiers are now part of the **effective
scoring config**, their values must be folded into the `ScoringConfigVersion` fingerprint (this is why spec
95 lands first) — otherwise two runs with different insider tiers would be falsely judged comparable.

---

## Assignment

Worktree: any — a new config-bound Application options record + binder, injected into
`KeywordSignalExtractor`, plus folding its canonical descriptor into the scoring fingerprint. It **edits
`KeywordSignalExtractor.cs`, `ScoringConfigFingerprint.cs`, `EffectiveScoringConfig.cs`, `ScoringEngine.cs`,
`InfrastructureServiceCollectionExtensions.cs`, `RadarWorkerServices.cs`** and repins the fingerprint tests,
so it must **NOT** run in parallel with any scoring/extractor/fingerprint slice.
Dependencies: **95 (fold the signal-source set into the fingerprint — MERGE FIRST; this slice extends that
same fingerprint plumbing)**, 93 (the insider tiers this slice relocates), 89 (the `ScoringWeights`
config-magnitude pattern this mirrors).
Conflicts with: spec 95 (sequence AFTER it); any extractor/scoring/fingerprint slice.
Estimated time: ~1.5–2 h

---

## Project structure changes

```text
src/Radar.Application/SignalExtraction/
  InsiderMaterialityWeights.cs      # NEW: immutable record — BuyTiers, SellTiers, ClusterBoost; code defaults == spec 93; Validate() + a CanonicalDescriptor()
  KeywordSignalExtractor.cs         # MODIFIED: inject InsiderMaterialityWeights; read buy/sell tiers + cluster boost from it instead of the const arrays

src/Radar.Application/Scoring/
  ScoringConfigFingerprint.cs       # MODIFIED: fold the insider-materiality descriptor into Compute(...)
  EffectiveScoringConfig.cs         # MODIFIED: add the InsiderMaterialityDescriptor field (preserve self-verification)
  ScoringEngine.cs                  # MODIFIED: inject InsiderMaterialityWeights, fold its descriptor into the fingerprint + EffectiveConfig

src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs   # MODIFIED: TryAddSingleton default + AddRadarInsiderMateriality(config) binder (mirror AddRadarScoringWeights)

src/Radar.Worker/
  RadarWorkerServices.cs            # MODIFIED: call AddRadarInsiderMateriality(configuration) before AddRadarApplicationServices

docs/architecture-decisions.md      # MODIFIED: amend AD-6/AD-10 (insider magnitudes → config, hashed; rule structure stays versioned)
CLAUDE.md                           # MODIFIED: note the insider tiers are now a config edit (no RuleSetVersion bump for a magnitude change)

tests/Radar.Application.Tests/SignalExtraction/
  KeywordSignalExtractorTests.cs    # MODIFIED: tiers now come from injected weights; add asymmetric buy!=sell + custom cluster-boost cases
  InsiderMaterialityWeightsTests.cs # NEW: defaults == spec 93 values; Validate() fail-fast; CanonicalDescriptor determinism
tests/Radar.Application.Tests/Scoring/
  ScoringConfigFingerprintTests.cs  # MODIFIED: new insider descriptor arg; repin default; changed-insider-tiers-changes-fingerprint case
tests/Radar.Infrastructure.Tests/DependencyInjection/
  InfrastructureServiceCollectionExtensionsTests.cs  # MODIFIED: AddRadarInsiderMateriality profile-select + fail-fast (mirror the ScoringWeights tests)
```

---

## Implementation details

### `InsiderMaterialityWeights` (Application, `Radar.Application.SignalExtraction`)

An immutable record mirroring `ScoringWeights` in spirit (code defaults == current v5/spec-93 behaviour, a
`Validate()` called from the extractor ctor AND the DI binder, and a deterministic `CanonicalDescriptor()`
for the fingerprint):

- `IReadOnlyList<InsiderMaterialityTier> BuyTiers` and `SellTiers`, where
  `InsiderMaterialityTier` is `(decimal MinInclusive, int Strength)` (a small record; reuse a shared shape if
  you prefer, but the existing tuple arrays are private to the extractor). **Both default to the exact spec-93
  table** so a blank/absent config is byte-identical to today:
  `>= 5,000,000 → 8; >= 1,000,000 → 7; >= 250,000 → 6; >= 50,000 → 4; floor (decimal.MinValue) → 2`.
- `int ClusterBoost { get; init; } = 1;` — the additive multi-insider boost (spec 93's `+1`), still capped at
  the domain max 10 by the extractor.
- `Validate()` — fail fast on: an empty tier list; a tier list missing a `decimal.MinValue` floor (so every
  amount maps); non-descending `MinInclusive` ordering; any `Strength` outside the domain range `1..10`;
  non-monotonic (a higher amount must not map to a lower strength); a negative `ClusterBoost`. These mirror
  the invariants the current const arrays satisfy by construction, so a misconfigured profile cannot silently
  produce an out-of-range Strength that fails `SignalValidation` at runtime.
- `CanonicalDescriptor()` — deterministic, culture-invariant serialization (AD-3), e.g.
  `buy=5000000:8,1000000:7,...;sell=...;cluster=1;` using invariant `"R"`/decimal formatting and a fixed field
  order. This is what the fingerprint hashes.

Config binding note: bind from `Radar:Insider:*` (a named-profile map mirroring `Radar:Scoring`, OR a flat
section — pick the simplest that supports profile overlays via `scripts/run-radar.ps1`). Because `System.Text`
/ `IConfiguration` binds lists of records cleanly, `BuyTiers`/`SellTiers` bind from JSON arrays of
`{ MinInclusive, Strength }`.

### `KeywordSignalExtractor`

- Add an `InsiderMaterialityWeights` constructor dependency (null-guarded; call `weights.Validate()` in the
  ctor, mirroring the formula-ctor validation). Keep `ILogger<KeywordSignalExtractor>`.
- Replace the private `InsiderNetValueTiers` array and the `strength + 1` cluster boost with reads from the
  injected weights: a fired `InsiderBuying` **Positive** signal scales via `weights.BuyTiers`, a **Negative**
  signal via `weights.SellTiers`, and the cluster boost uses `weights.ClusterBoost`
  (`Math.Min(10, strength + weights.ClusterBoost)`). The `insiderCluster` flag read, the `insiderNetValue`
  parse, and the Neutral-routine / absent-value fallback to the fixed rule Strength are **unchanged**. Keep
  `StrengthForAmount(amount, tiers)` generic (it already takes a tier array — feed it `weights.BuyTiers` /
  `weights.SellTiers`).
- **Leave `GovernmentContractAmountTiers` as a code constant** (out of scope — note it in the comment; a
  parallel config move for GovernmentContract is a possible future slice). Only the InsiderBuying magnitudes
  move.
- Update the class XML doc + the inline materiality comment: the insider tiers/cluster boost now come from
  config (`InsiderMaterialityWeights`); the phrase→direction rule *structure* (the `Rules` table and
  first-match-per-type dedupe) stays versioned code identity (`RuleSetVersion`, spec 95). Because the tier
  values are now hashed into the fingerprint by *value*, a tier magnitude change no longer needs a
  `RuleSetVersion` bump — only a rule *structure* change does.

### Fingerprint plumbing (extends spec 95)

- `ScoringConfigFingerprint.Compute` gains a `string insiderMaterialityDescriptor` field, appended after the
  spec-95 `srcDesc` field (stable ordering; the default fingerprint re-stamps — expected, repin below).
- `EffectiveScoringConfig` gains `string InsiderMaterialityDescriptor`; the store self-verification invariant
  (recompute-from-stored == filename) must still hold, so persist it verbatim (no `FileScoringConfigStore`
  logic change; confirm round-trip).
- `ScoringEngine` injects `InsiderMaterialityWeights`, computes `weights.CanonicalDescriptor()`, and threads
  it into both `ScoringConfigFingerprint.Compute(...)` and the `EffectiveScoringConfig` projection so
  `EffectiveConfig.Fingerprint` still equals every snapshot's stamp. No scoring-math change.

### DI + Worker

- `AddRadarInsiderMateriality(this IServiceCollection, IConfiguration)` — resolve the effective profile
  (mirror `AddRadarScoringWeights`: `Radar:Insider:Profile` selects `Radar:Insider:Profiles:{name}`, blank ⇒
  defaults, a named-but-missing profile fails fast), `Validate()` the result, `AddSingleton(weights)`.
  In `AddRadarApplicationServices`, add `services.TryAddSingleton(new InsiderMaterialityWeights());` (default
  == spec 93) so the concrete binder-registered instance wins.
- `RadarWorkerServices.AddRadarWorker` calls `services.AddRadarInsiderMateriality(configuration);` BEFORE
  `AddRadarApplicationServices()` (same ordering rationale as `AddRadarScoringWeights`).

### Ledger + checklist

- Amend **AD-6 / AD-10** in `docs/architecture-decisions.md`: the insider buy/sell materiality tiers + cluster
  boost move from `KeywordSignalExtractor` code constants into config (`InsiderMaterialityWeights`,
  default == spec 93 so output is byte-identical); their values are folded into the `ScoringConfigVersion`
  fingerprint (building on spec 95). A magnitude change is now a config edit (no version bump); only a rule
  *structure* change bumps `KeywordSignalExtractor.RuleSetVersion`. Record the new default-fingerprint value.
- Extend the `CLAUDE.md` checklist accordingly.

---

## Tests

- `InsiderMaterialityWeightsTests`: defaults exactly reproduce the spec-93 table + `ClusterBoost == 1`;
  `Validate()` fail-fast on empty/missing-floor/non-descending/out-of-range-strength/non-monotonic/negative-boost;
  `CanonicalDescriptor()` is deterministic and culture-invariant; an asymmetric config (buy tiers ≠ sell tiers)
  round-trips.
- `KeywordSignalExtractorTests` (MODIFIED): construct the extractor with the default weights and keep every
  spec-93 case green (P→Positive tier boundaries, S→Negative, cluster +1, Neutral routine / absent value keeps
  fixed Strength). Add: an **asymmetric** weights instance where buy strengths exceed sell strengths at the
  same `insiderNetValue` yields a higher Strength for a Positive than a Negative; a custom `ClusterBoost` (e.g.
  2) applies and still caps at 10.
- `ScoringConfigFingerprintTests` (MODIFIED): thread the new `insiderMaterialityDescriptor` arg; add a
  `Compute_ChangedInsiderTiers_ChangesFingerprint` case; **repin** the default fingerprint to the recomputed
  hex (default insider descriptor + the spec-95 default signal-source descriptor; recompute, do not guess).
- `InfrastructureServiceCollectionExtensionsTests` (MODIFIED): `AddRadarInsiderMateriality` selects a named
  profile, applies its delta onto defaults, fails fast on a named-but-missing profile and on an invalid tier
  (mirror the `AddRadarScoringWeights` coverage).
- Grep-and-fix any remaining pin of `radar-scoring-fp-…` and any `EffectiveScoringConfig`-shape assertion.
  Snapshots still stamp `ScoringConfigVersion == EffectiveConfig.Fingerprint`.

---

## Constraints

- Target `net10.0`, C# 14. Deterministic, culture-invariant descriptor + validation (AD-3).
- Layering (AD-5): `InsiderMaterialityWeights` is `Radar.Application`; no Infrastructure/provider leakage.
- **Structure stays versioned code; only magnitudes move to config** — no `_formula.Version` bump, no new
  formula class; the phrase→direction rule table + first-match-per-type dedupe are unchanged (AD-6 / CLAUDE.md
  scoring-weights rule). Defaults == spec 93 ⇒ default scoring output is byte-identical (pinned by the extractor
  tests); only the fingerprint *input* widens (default value re-stamps — expected).
- Preserve the fingerprint self-verification invariant (recompute-from-stored `EffectiveScoringConfig` ==
  filename; `EffectiveConfig.Fingerprint` == snapshot stamp).
- No advice language (AD-9); all Strengths stay in the domain range 1–10 (enforced by `Validate()`).
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

## Out of scope (note explicitly, capture the deferred item)

- **Within-run insider signal multiplicity / net-flow aggregation.** A cluster of sells currently lands as N
  independent `InsiderBuying` Trajectory hits (only partly mitigated by the collector's within-filing `+1`
  cluster boost). Ideally materiality would aggregate by **net insider flow per company/window** (net buy $
  minus sell $) rather than counting each filing independently. This is a larger scoring/aggregation change —
  **deferred, not actioned here**; recorded so it is not lost.
- **Moving the `GovernmentContract` materiality tiers to config** — a parallel future slice; only insider
  magnitudes move here.
- **Curve-fitting insider weights to price/backtest outcomes** — these knobs are for deliberate, reasoned
  buy-vs-sell asymmetry experiments, not price optimization (AD-14; the `ScoringWeights` "Out of scope" note).
- **AI-assisted insider-sentiment refinement** — a separate future slice (per spec 93).

## Acceptance criteria

- [ ] The insider buy tier table, sell tier table, and cluster boost are bound from config
      (`InsiderMaterialityWeights`), injected into `KeywordSignalExtractor`, and validated fail-fast; buy and
      sell tiers can differ (asymmetry is expressible via a run profile with no code change).
- [ ] Defaults reproduce the spec-93 magnitudes exactly ⇒ default insider signal Strengths are byte-identical
      (extractor tests pinned); `GovernmentContract` tiers remain code constants (unchanged).
- [ ] The insider materiality descriptor is folded into `ScoringConfigFingerprint.Compute` and
      `EffectiveScoringConfig`; changing an insider tier or the cluster boost re-stamps
      `ScoringConfigVersion` automatically; the store self-verification invariant still holds.
- [ ] No formula/rule *structure* change: no `_formula.Version` bump, no new formula class, no
      `RuleSetVersion` bump; the phrase→direction table is unchanged. Only the fingerprint input widens.
- [ ] `AddRadarInsiderMateriality` mirrors `AddRadarScoringWeights` (named-profile select, defaults, fail-fast
      on missing profile / invalid tier); `RadarWorkerServices` wires it before `AddRadarApplicationServices`.
- [ ] Fingerprint tests repinned; AD-6/AD-10 amended and the CLAUDE.md checklist updated; the within-run
      net-flow-aggregation deferral is recorded. `dotnet build` / `dotnet test` on `Radar.sln -c Release`
      green.

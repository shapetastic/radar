# Task: Recalibrate the default `ScoringWeights.MediaReachWeight` 0.25 → 0.10 (de-saturate Attention)

> **DIRECTED + MAINTAINER-APPROVED (read first).** This is a **directed** recalibration the maintainer
> decided from live-sweep data this session — **NOT** the generic planner loop and **NOT** architecture-gated.
> It changes **one magnitude default** and its derived test pins. It is a `ScoringWeights` **magnitude** change,
> **NOT** a formula-structure change: it stays `radar-formula-v5`. **Do NOT add a v6 / a new `IScoreFormula`
> class, and do NOT hand-bump any version constant** (the fingerprint is derived — see below).
>
> **CRITICAL — the spec-89 "blank config == v4 byte-identical" property is DELIBERATELY superseded here.**
> The default scoring output is now the **recalibrated** default (`MediaReachWeight = 0.10`), no longer
> identical to `radar-formula-v4`. The v4-equivalence test at
> `RadarScoreFormulaV5Tests.DefaultConfig_IsByteIdenticalToV4_ForARepresentativeInput` and the pinned
> default fingerprint **must be updated to the new default** and framed as an **intentional recalibration,
> not a regression**. Do not "fix" them back to the v4 numbers.

## Overview

A post-spec-91 live re-measure of `AttentionScore` across the watch universe showed it was **saturated**:
every normally-covered small-cap landed at Attention ~64–75. The cause is the reach term
`reach = weightedBreadth + MediaReachWeight·mediaCount`: at the baseline `MediaReachWeight = 0.25`, the raw
**article-count** term (`0.25·mediaCount`) dominated the tier-weighted **distinct-publisher** breadth term
roughly **5:1**, so Attention tracked article **volume** — the content-mill noise every ticker gets — instead
of genuine market notice. The under-the-radar discount (`1 − Attention/250`) therefore fired ~uniformly and
stopped discriminating.

A **live `MediaReachWeight` sweep** (via `scripts/run-radar.ps1` profiles into isolated `--Radar:*Directory`
output dirs — the same mechanism as `scripts/run-profiles/low-media.json`) compared baseline **0.25** vs
**0.15 / 0.10 / 0.05**. Lowering the weight **de-saturates** Attention and lets tier-weighted breadth surface.
The quality gap between a genuinely-covered name (**ERII**) and a known all-aggregator name (**HLIO**) widened
monotonically as the weight dropped:

| MediaReachWeight | ERII − HLIO Attention gap |
|---:|---:|
| 0.25 (baseline) | 4 |
| 0.15 | 7 |
| **0.10 (chosen)** | **9** |
| 0.05 | 14 |

**`0.10` is chosen as the balanced middle.** It de-saturates cleanly (Attention spread ~49–63, SPNS ~9) and
keeps Attention a **modest, breadth-leaning discount** — without the near-zero volume contribution of 0.05 or
the saturation of 0.25. Rationale: Attention should be a **light modifier**; **Trajectory + Evidence** drive
the score. (No `MediaReachWeight` value makes Attention a *strong* discriminator for this mostly-aggregator-
covered universe — accepted. A tier-weighted-article-count formula `v6` was considered and **skipped as
marginal** — see Out of scope.)

This is a `ScoringWeights` magnitude change only. The formula **shape** is byte-for-byte unchanged, so it stays
`radar-formula-v5`; the **default fingerprint changes** automatically (MediaReachWeight is part of the
fingerprint) and is re-pinned; and the AD-6 ledger gains a dated refinement note.

---

## Assignment

Worktree: any
Dependencies: None
Conflicts with: None (specs 92 price and 93 Form 4 touch disjoint files). Do not start those here.
Estimated time: ~30–60 minutes

---

## Grounding facts (verified against the tree)

- **The one production default to change** — `src/Radar.Application/Scoring/ScoringWeights.cs:17`:
  ```csharp
  public double MediaReachWeight { get; init; } = 0.25;         // v4 value
  ```
  Change the value `0.25 → 0.10` and update the trailing comment to reflect the recalibration (it is no
  longer "the v4 value"; e.g. `// spec 94 recalibration (was 0.25, the v4 value)`).
- **Reach math (unchanged)** — `src/Radar.Application/Scoring/RadarScoreFormulaV5.cs:154`:
  `var reach = weightedBreadth + _weights.MediaReachWeight * mediaCount;` then
  `attentionScore = Score(100 * reach / (reach + _weights.AttentionHalfSaturation));` (`AttentionHalfSaturation`
  default `3.0`). No code change here — it already reads `_weights.MediaReachWeight`.
- **The fingerprint already hashes MediaReachWeight** — `ScoringConfigFingerprint.cs:42`
  (`Append(builder, nameof(weights.MediaReachWeight), weights.MediaReachWeight);`). So the default fingerprint
  changes **automatically** when the default changes. No code change to `ScoringConfigFingerprint.cs`.
- **Pinned default-fingerprint test constant** —
  `tests/Radar.Application.Tests/Scoring/ScoringConfigFingerprintTests.cs:71`:
  `Assert.Equal("radar-scoring-fp-c1e71b26adf3", fp);` — MUST be re-pinned to the newly-computed value.
- **The v4-equivalence pin (deliberately superseded)** —
  `tests/Radar.Application.Tests/Scoring/RadarScoreFormulaV5Tests.cs:788`
  `DefaultConfig_IsByteIdenticalToV4_ForARepresentativeInput`. Its representative input includes one
  `MediaAttention` signal (`mediaCount = 1`), so the pinned `AttentionScore` (currently 44), the dependent
  `OpportunityScore` (currently 43), the `ScoreComponents` record, the `ComponentJson`, and the `Explanation`
  string (lines 811–825) all change when MediaReachWeight drops. This test and its comment MUST be updated to
  the recalibrated default (see Tests).
- **Relative default assertion (no change needed)** —
  `tests/Radar.Application.Tests/Scoring/ScoringWeightsBindingTests.cs:52`
  `Assert.Equal(defaults.MediaReachWeight, weights.MediaReachWeight);` compares the binder output to
  `new ScoringWeights().MediaReachWeight` (relative), so it stays green automatically. Leave it; only its
  inline `== v4` comment wording is now imprecise — a light comment touch is optional, not required.

---

## Implementation details

Production code (one line):

- Edit `ScoringWeights.MediaReachWeight` default `0.25 → 0.10` (with an updated comment). **Nothing else in
  production changes** — no formula class, no version constant, no fingerprint code, no DI, no options, no
  collector/extractor/Domain/report code.

The coder MUST **compute** the two derived values rather than guessing:

1. **New default fingerprint** — run the existing `ScoringConfigFingerprint.Compute(...)` with the new default
   `new ScoringWeights()` (via a scratch test / temporary assertion, or simply run the fingerprint test and
   read the actual value from the xUnit failure message) to obtain the exact `radar-scoring-fp-<12hex>` token,
   then pin it.
2. **New v4-equivalence integers + strings** — the representative input's reach becomes
   `1.0 (genuine) + 1.0 (genuine) + 0.1 (mill) + 0.10·1 (mediaCount) = 2.20`, giving
   `Attention = round(100·2.20 / (2.20 + 3.0)) = round(42.307…) = 42`; recompute the dependent
   `OpportunityScore` from the formula (`Trajectory·(EC/100)·(1 − Attention/250)`) and update the pinned
   `ScoreComponents`, `ComponentJson` assertion, and `Explanation` string accordingly. Let the test drive the
   exact integers — pin whatever the recomputed formula actually emits; do not hand-fudge.

Documentation (AD-6 refinement note):

- Append a dated refinement to **AD-6** in `docs/architecture-decisions.md` (after the spec-90 note), recording
  the `MediaReachWeight 0.25 → 0.10` default recalibration, the one-line rationale (raw-article-count term
  dominated tier-weighted distinct-publisher breadth ~5:1 → Attention saturated ~64–75 and tracked volume, not
  notice), the sweep result (ERII−HLIO gap 4→7→9→14; 0.10 the de-saturating middle, Attention spread ~49–63),
  that it **stays `radar-formula-v5`** (magnitude, not structure), that the **default fingerprint re-stamps
  automatically** and the pinned default-fingerprint test + the v4-equivalence pin were **intentionally**
  updated (the byte-identical-to-v4 property is superseded on purpose), and that a tier-weighted-article-count
  `v6` was considered and skipped as marginal. Mark it **Accepted · 2026-07-04**. Also add the spec-94 note to
  the AD-6 **Status** line. **Do NOT touch AD-10 mechanics** (the fingerprint is derived; the re-stamp is the
  designed behaviour, not a manual bump).

Version / fingerprint obligations (be explicit):

- Stays `radar-formula-v5`; `EngineVersion` / `ScoringVersion` **unchanged** (structure unchanged).
- **No manual `ScoringConfigVersion` bump** — it is a derived content fingerprint (AD-10 as amended by spec 89).
  It re-stamps automatically because MediaReachWeight is in the hashed canonical string. The only human
  obligation is re-pinning the default-fingerprint **test** constant to the recomputed value.
- Cross-run deltas vs prior `0.25` runs will render **"(scoring updated)"** via the spec-69 comparability gate
  (the fingerprint differs) instead of a numeric delta / a `Thesis improving|deteriorating` label — this is
  **expected and correct** (it is exactly the AD-10 property that prevents a recalibration from fabricating a
  thesis-trajectory label). Note it; no action needed.

---

## Tests

- **Re-pin the default fingerprint** — `ScoringConfigFingerprintTests.cs:71`: replace
  `radar-scoring-fp-c1e71b26adf3` with the newly-computed token. Update the test's explanatory comment to note
  the value is the automatic AD-10 re-stamp for the **spec-94 `MediaReachWeight 0.25 → 0.10` recalibration**.
- **Update the v4-equivalence test (intentional recalibration, NOT a regression)** —
  `RadarScoreFormulaV5Tests.DefaultConfig_IsByteIdenticalToV4_ForARepresentativeInput`
  (`RadarScoreFormulaV5Tests.cs:788`): update the pinned `ScoreComponents` (new `AttentionScore = 42` and the
  recomputed `OpportunityScore`), the reach-math comment (line 811, `0.10·mediaCount(1)` → reach `2.20` →
  `Att 42`), and the `Explanation` string to the recomputed values. **Reframe the test intent**: the default is
  now the recalibrated `radar-formula-v5` default (`MediaReachWeight 0.10`), deliberately **no longer**
  byte-identical to v4 — rename the test and/or rewrite its header comment so a future reader (and the reviewer)
  understands the divergence is the sanctioned spec-94 recalibration, not drift. Suggested rename:
  `DefaultConfig_MatchesRecalibratedV5Baseline_ForARepresentativeInput`.
- **Assert the new default explicitly** — add a small pin (in `RadarScoreFormulaV5Tests` or
  `ScoringWeightsBindingTests`): `Assert.Equal(0.10, new ScoringWeights().MediaReachWeight);` so an accidental
  future revert is caught directly.
- All other `RadarScoreFormulaV5Tests` cases either use the `AllGenuine`/`Tiered` fakes with no `MediaAttention`
  signal (so `mediaCount = 0`, unaffected) or are relative — confirm the suite is green; only the two pinned
  cases above should need touching. Leave `ScoringWeightsBindingTests:52` as-is (relative comparison stays
  green).

---

## Constraints

- Target **.NET 10**; C# 14.
- **Stays `radar-formula-v5`** — no v6, no new `IScoreFormula` class, no formula-shape change.
- **No manual version bump** — `ScoringConfigVersion` is derived (AD-10 amended); it re-stamps automatically.
- **No collector / extractor / Domain / report change.** One production line changes.
- Determinism, layering, provenance, AD-3 (deterministic ordering / canonical serialization), AD-6, AD-8
  (files-first), AD-9 (label set), and AD-10 mechanics are all preserved.
- Build & test gate: `dotnet build Radar.sln -c Release` then `dotnet test Radar.sln -c Release --no-build`.

---

## Out of scope (note)

- A **tier-weighted-article-count formula `v6`** (weighting the media term by publisher tier) — considered as an
  alternative to de-saturate Attention; **skipped as marginal** for this mostly-aggregator-covered universe.
- Spec **92** (price-history reference store) and spec **93** (Form 4 insider-transaction collector) — untouched.
- Folding the **collector set / extractor rules** into the fingerprint — out of scope (AD-10 amended defines the
  fingerprint inputs; do not extend them here).
- Any curve-fitting of weights to price / backtest outcomes (explicitly disallowed by `ScoringWeights` doc).

---

## Acceptance criteria

- [ ] `ScoringWeights.MediaReachWeight` default is `0.10` (comment updated to note the spec-94 recalibration,
      was `0.25`).
- [ ] `_formula.Version` is still `radar-formula-v5`; no new formula class; no `ScoringConfigVersion` or engine
      version edited by hand.
- [ ] `ScoringConfigFingerprintTests` default-fingerprint constant is re-pinned to the newly **computed** token
      and its comment references the spec-94 recalibration.
- [ ] The v4-equivalence test is updated to the recalibrated default (new `AttentionScore = 42`, recomputed
      `OpportunityScore`, `ComponentJson`, `Explanation`), reframed/renamed as an intentional recalibration
      (not a regression), and its reach-math comment reflects `0.10·mediaCount`.
- [ ] A direct pin asserts `new ScoringWeights().MediaReachWeight == 0.10`.
- [ ] `ScoringWeightsBindingTests` still passes unchanged (relative comparison).
- [ ] AD-6 in `docs/architecture-decisions.md` has a dated **Accepted · 2026-07-04** refinement note recording
      the recalibration + rationale + sweep result + the deliberate supersession of the v4-byte-identical
      property; AD-10 mechanics untouched.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` both pass.

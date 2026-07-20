# Task: radar-formula-v7 — rank under-followed improvement above already-noticed improvement

> **ATTENTION / DISCOUNT CALIBRATION.** Diagnosed live on 2026-07-20. This is a formula **STRUCTURE** change
> (`radar-formula-v6 → v7`) → **maintainer approval of the exact shape + constants is required** (AD-6), like v6
> was. It changes only the Opportunity attention-discount term. Introduces a curated, **non-price** following/size
> input in the seed (AD-14: price/market-cap must not enter scoring).

## Overview

Radar's mission is to surface improving trajectories **before the market notices**. Opportunity already discounts
by attention: in `RadarScoreFormulaV6`,

```
Opportunity = Trajectory · (EvidenceConfidence/100) · (1 − Attention / OpportunityAttentionDivisor)   // divisor 250
```

But the 2026-07-20 baseline surfaced **JNJ (Johnson & Johnson, a mega-cap) at #2 "Thesis improving," Opp 45** on a
real but fully-priced earnings quarter — exactly a name the market has already noticed. The discount did not stop
it, and tightening the divisor won't fix it, because **the discount's input is blind to true notedness**: JNJ's
`Attention` is **21** and AEHR's is **19** — nearly identical. Radar's `Attention` measures third-party publisher
breadth *in its own feeds*, which does not capture that a $400B mega-cap is already maximally followed and priced.
So no amount of divisor tightening ranks under-followed AEHR above already-noticed JNJ — it would discount both
equally.

The missing ingredient is a **"how-followed-already" signal** that distinguishes a mega-cap from an under-covered
small-cap. Market cap is the obvious proxy but is **price-derived → forbidden as a scoring input (AD-14)**. The
clean, deterministic, non-price proxy is a **curated following/size tier in the company seed** (mega/large/mid/
small, or an explicit benchmark flag) — Radar's watch universe already treats AAPL/JNJ/CAT as large-cap
*benchmarks/controls* (per the seed's design intent). This slice folds that tier into the Opportunity discount so
a more-followed company is discounted harder, ranking genuine under-appreciated improvement above already-noticed
improvement.

> **A lean, not a hard exclusion.** A mega-cap CAN have an under-appreciated trajectory; the discount should make
> it *need more* to surface, not be impossible. Keep it a graded discount, not a filter. AEHR (small, genuinely
> improving) must stay #1 Investigate; JNJ (mega-cap, fully-priced quarter) should fall out of the actionable
> surface; a genuine small-cap inflection is unaffected.

## Design decision (maintainer-gated — AD-6)

The **proposed shape** (present the exact form + constants in the PR for sign-off):

```
followingDiscount = 1 − (Attention/OpportunityAttentionDivisor)·attnWeight − followingTier·tierWeight
Opportunity = Trajectory · (EvidenceConfidence/100) · clamp(followingDiscount, floor, 1)
```

where `followingTier ∈ {mega, large, mid, small} → {…}` maps to a discount magnitude, and `attnWeight`,
`tierWeight`, the tier magnitudes, and a `floor` all live in `ScoringWeights` (config — tunable later without a
formula class). Keep the existing measured-attention discount as one term; add the curated-tier discount as a
second term. Alternatives the maintainer should weigh, recorded for the decision:
- **(A) config-only divisor tighten** — rejected as the sole fix: JNJ 21 ≈ AEHR 19, so it cannot separate them.
- **(B) this spec — a non-price following-tier discount input (formula-v7).** Recommended: makes the *score*
  rank under-followed improvement higher, which is the mission.
- **(C) benchmark bucketing in the report** — keep benchmark/control names scored but out of the actionable
  surface (a report/labeling change, no formula bump). Cheaper and complementary, but it hides controls rather
  than making the score smarter, and does nothing for a non-benchmark over-followed name. May be worth doing
  alongside (B) as a quick win; note it, do not fold it in here.

## Assignment

Worktree: any
Dependencies: current main (post 113–115). Coordinate with 116 (both touch the earnings-read-driven surface, but
  different files — 116 is the analyzer prompt, this is the formula + seed).
Estimated time: ~1–2 hours (plus the AD-6 maintainer approval gate on the shape/constants).

## Project structure changes

Add:
- `src/Radar.Application/Scoring/RadarScoreFormulaV7.cs` — copy v6, change **only** the Opportunity discount term
  to fold in the curated following tier. Delete `RadarScoreFormulaV6.cs` (per the spec-implementation checklist —
  do not leave it dormant); port its tests.

Modify:
- The company seed model + `data/companies.json` — add a **following/size tier** field per company (curated,
  non-price). Populate all 19: mega = AAPL/JNJ/CAT; the small/mid-caps get their tiers. The scoring input must
  carry the tier to the formula (thread it through `ScoringInput` / the company record).
- `src/Radar.Application/Scoring/ScoringWeights.cs` — new magnitudes (`tierWeight`, per-tier discount, `floor`,
  `attnWeight`) with defaults + `Validate()` coverage. Only the *shape* stays in the formula.
- DI to construct `RadarScoreFormulaV7`; `ScoringConfigFingerprintTests` re-pin (moves via `_formula.Version`
  v6→v7); `scripts/run-profiles/default.json` `_comment`; `docs/architecture-decisions.md` new AD-6 `v7` entry
  with the exact shape + the JNJ/AEHR before/after.

## Implementation details

- **Change ONLY the Opportunity discount term.** Trajectory (v6 corroboration), Attention, EvidenceConfidence,
  SignalVelocity, recency, empty-window, provenance contributions, direction signs — all byte-identical to v6.
- **Non-price (AD-14).** The following tier is curated seed metadata, NOT derived from price/market-cap/volume.
  State this explicitly; a reviewer must confirm no price field feeds scoring.
- **Invariants (tests):** monotone (more following never raises Opportunity, never lowers it below the floor);
  a small-cap with a given Trajectory/Evidence/Attention scores ≥ a mega-cap with the same three (following is a
  discount, never a bonus); the discount is graded, not a hard cut (a high enough Trajectory still surfaces a
  mega-cap); `clamp` keeps Opportunity in [0,100]; deterministic (AD-3).
- **Version obligation (AD-6/AD-10):** formula STRUCTURE change → bump `_formula.Version` v6→v7; fingerprint
  re-stamps automatically; magnitudes → `ScoringWeights` (config). New AD-6 ledger entry, maintainer-approved.
- Efficacy note: this re-stamps the fingerprint, so the efficacy score line segments here — spec 108's
  continuity-aware segmentation renders that cleanly (no spurious break where component values are unchanged).

## Tests (ported v6 suite + new)

- All unchanged v6 components byte-identical for the same inputs (Trajectory/Attention/Evidence/Velocity).
- Following discount: same Trajectory/Evidence/Attention, mega-tier Opportunity < small-tier Opportunity;
  graded (mid between); monotone; floor respected; clamp holds; empty-signal → unchanged.
- `ScoringConfigFingerprintTests` re-stamps to the new v7 pin; recompute-from-`EffectiveScoringConfig` matches.
- LIVE re-measure acceptance: JNJ falls out of the actionable surface (or clearly below AEHR); AEHR stays #1
  Investigate; a genuine small-cap improvement is unaffected. **No ticker-specific logic — driven by the seed tier.**

## Constraints

- Target .NET 10 / `net10.0`, C# 14. Structure change is maintainer-gated (AD-6): exact shape + constants need
  sign-off before merge.
- Only the Opportunity discount term changes; everything else byte-identical to v6. Delete v6, port tests.
- Following tier is curated + non-price (AD-14). Magnitudes → config; only shape versioned.
- A discount/lean, not a filter — a mega-cap with an overwhelming trajectory can still surface. No advice
  language (AD-9); determinism + provenance preserved (AD-3/AD-1).

## Acceptance criteria

- [ ] `RadarScoreFormulaV7` changes only the Opportunity discount, folding a curated non-price following tier;
      all other components byte-identical to v6 (ported tests prove it).
- [ ] Same Trajectory/Evidence/Attention ⇒ a more-followed company scores lower Opportunity (graded, floored,
      monotone, clamped); the tier is seed-curated and never price-derived (AD-14 confirmed by a reviewer).
- [ ] `_formula.Version = radar-formula-v7`; fingerprint re-stamps; new AD-6 `v7` ledger entry;
      `default.json`/pin updated; v6 deleted + tests ported.
- [ ] LIVE re-measure: JNJ no longer outranks AEHR / drops off the actionable surface; AEHR stays #1 Investigate.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.

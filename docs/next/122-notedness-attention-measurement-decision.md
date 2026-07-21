# Task: Notedness / Attention measurement understatement — DESIGN + maintainer decision

> **BLOCKED — MAINTAINER DECISION REQUIRED before implementation. Do NOT implement via `run next` until the
> maintainer records a chosen option below.** This closes OPEN DECISION (b): "the Attention/notedness metric
> UNDERSTATES how noticed a company is — AEHR popped ~60% intraday (massively noticed) yet Radar Attention was
> 21." The likely fix touches the **scoring formula structure** (a new Attention dimension), which is
> **AD-6-gated** — a formula-STRUCTURE change requires the maintainer to approve the shape + constants
> in-session (as every `radar-formula-vN` refinement has been). This spec exists to frame the problem, present
> vetted options, and record the decision; it is **not** a build-now task. When the maintainer picks an option,
> convert the chosen option into a concrete implementation spec (or amend this one) and lift the block.

## Overview / problem

`AttentionScore` (currently `radar-formula-v7`, Attention component unchanged since `radar-formula-v4`/spec 88 +
the spec-90/94 recalibrations) measures **tier-weighted DISTINCT third-party publisher breadth** in Radar's own
collected feeds:

```
reach   = Σ over distinct third-party publishers of tierWeight(publisher) + MediaReachWeight · mediaSignals
Attention = 100 · reach / (reach + AttentionHalfSaturation)
```

Spec 109 additionally **collapses near-duplicate articles about the SAME event** into one attention unit. The
combined effect is that Attention measures **event/publisher BREADTH**, deliberately **not LOUDNESS or VELOCITY**.
So a name that is loudly noticed around a **single** event (AEHR: one earnings surprise → ~60% intraday move,
dozens of articles about that one event) collapses to a **low** breadth and scores Attention ~21 — indistinguishable
from a genuinely under-followed name.

This matters because `radar-formula-v7`'s Opportunity discount leans on Attention (alongside the curated
`FollowingTier`) to separate "improving and under-noticed" (surface) from "improving but already-priced"
(discount). If Attention understates true notedness, an already-noticed name can still surface with a small
discount — the exact failure spec 117 was fighting, only from the measured side rather than the curated side.

**AD-14 constraint (hard):** price / market-cap / intraday-move is **forbidden** as a scoring input. "Popped 60%"
can never be a signal or an Attention term. Any fix must derive notedness from Radar's own **non-price** evidence
(publisher breadth, article volume/velocity, source tiering) or from **curated non-price** seed metadata.

---

## The core design tension (this IS the decision)

Is a single, very loud event **highly noticed** (→ Attention should be high, discount it) or is it **one event**
(→ breadth is correctly low, do not over-credit duplicate coverage of one story)? Radar deliberately chose
breadth over loudness (spec 109) to resist hype loops (philosophy: "avoid hype loops", "signals before
stories"). The maintainer's AEHR observation suggests the pendulum is too far toward breadth. The decision is
**how much loudness/velocity to re-admit without re-opening the hype-loop trap.**

---

## Options (present all; recommend B; maintainer chooses)

**Option A — Non-structural: audit & improve news-collector breadth capture first (NO formula change).**
Before changing the formula, verify the input isn't the problem. Investigate why an intensely-covered name
surfaced **few distinct genuine publishers**: (i) is the `newssearch` (Google News) query under-returning, or the
title-relevance substring filter over-dropping? (ii) is spec-109 same-event collapse or the spec-88/90 publisher
tiering **over-collapsing / mis-tiering** real outlets down to the `0.1` mill weight or the `0.25` unknown
default? A collector/tiering fix is **Infrastructure-only**, changes no formula shape, and does **not** move the
fingerprint (the enabled-collector *set* and rules are unchanged; collection breadth is not hashed). **Do this
diagnosis regardless** — it may partly resolve the gap and it de-risks any formula change. Cheapest; may be
insufficient alone.

**Option B (RECOMMENDED) — Structural: add a bounded VELOCITY/volume term to Attention (`radar-formula-v8`).**
Re-admit loudness as a **saturating, bounded** dimension so a burst of genuine coverage lifts Attention without
letting raw article count dominate (the spec-94 lesson). E.g. `reach = weightedBreadth + MediaReachWeight ·
mediaSignals + AttentionVelocityWeight · saturate(distinctPublisherVelocity)`, where velocity = new distinct
genuine publishers **this window vs the prior window** (Radar already reads a previous window for velocity, and
already has cross-run signal readback — spec 82/85). Keep it **distinct-genuine-publisher** velocity (not raw
article volume) so it resists mill duplication. All magnitudes live in `ScoringWeights` config (spec-89 pattern),
default tuned on a live sweep; `AttentionVelocityWeight = 0` reproduces v7 byte-for-byte (pin it). **AD-6-gated:
maintainer must approve the shape + the default weight.** Fingerprint re-stamps automatically (formula version +
new weight). Best-targeted at the actual complaint; preserves the anti-hype posture via saturation + genuine-only.

**Option C — Report-only notedness caveat (NO scoring change).** Leave the metric; surface the tension in the
report so Dean can judge (e.g. annotate high-single-event-volume names). Complementary to spec 123 (which already
surfaces the Attention + FollowingTier inputs). Lowest-risk; does not fix ranking.

Recommendation: **do Option A's diagnosis first (cheap, no-risk), then Option B** if the input is sound and the
gap is genuinely a metric-shape issue. Option C ships anyway via spec 123.

---

## Maintainer decision block (fill in, then convert to an implementation spec)

- [ ] **Chosen option:** A / B / C / (A then B) / other: ______________________
- [ ] If B: approve the Attention shape (velocity term saturation form) and the **default `AttentionVelocityWeight`**
      (or defer to a live sweep like spec 94's `MediaReachWeight`): ______________________
- [ ] Confirm the anti-hype guardrail (genuine-publisher-only, saturating, distinct-not-volume) is acceptable.
- [ ] Confirm AD-14 is respected (no price / market-cap / intraday-move term).

---

## Diagnosis findings (spec 124)

*2026-07-21.* The Option-A diagnosis (deterministic characterization test
`tests/Radar.Application.Tests/Scoring/AttentionBreadthCharacterizationTests.cs`, through the real
engine + spec-109 collapse + `RadarScoreFormulaV7` + `ConfiguredAttentionSourceWeights` over the default
tier map) attributes the understatement **primarily to the spec-109 same-event collapse discarding
distinct-publisher breadth**, secondarily amplified by tiering. Fifteen distinct third-party publishers (7
on the default "Genuine" tier at 1.0, 8 absent from the map at `UnknownWeight` 0.25) covering **one event**
score `Attention` **10** — because `MediaAttentionCollapse` keeps a single representative, so the formula's
post-collapse breadth count sees ONE `SourceName` and `mediaSignalCount` 1 (reach 0.25 + 0.10·1 = 0.35). The
**same 15 publishers spread across distinct events** score `Attention` **78** (reach 9.0 + 0.10·15 = 10.5),
and the single-event set handed **directly to the formula, bypassing only the collapse**, also scores **78** —
which pins the entire 10→78 gap on the collapse, not the tier map (the map only sets the 78 ceiling vs. the 85
that 15 fully-genuine outlets would reach; the surviving representative resolving to `UnknownWeight` 0.25 vs.
1.0 shifts the burst number by less than a point). **No relevance-filter or tier-map bug was demonstrable from
this repo** — there is no live evidence corpus here, so no grounded basis for a `NewsAttentionCollector`
`IsRelevant` correction or a specific outlet re-tiering, and none was applied (steps 1–2 only; production code,
tier map, collapse, formula, and both fingerprints unchanged). **Recommended spec-122 direction:** this is the
*structural* question, not a measurement bug — counting **pre-collapse** distinct publishers for the breadth
term, and/or a bounded recency-gated **velocity** term (Option B) — which is **AD-6-gated** and is the
maintainer's spec-122 decision; it is deliberately **not** fixed in spec 124. Changing the three pinned numbers
above is exactly that decision.

---

## Constraints (whichever option is chosen)

- **AD-14 absolute:** no price / market-cap / trading-volume / intraday-move term, ever.
- **AD-6:** any formula-STRUCTURE change (Option B) bumps `_formula.Version` (`radar-formula-v7 → v8`), needs
  in-session maintainer approval, deletes the prior formula class (not dormant), ports its tests, and re-stamps
  the fingerprint automatically (re-pin by RUNNING the test, not by hand). Magnitudes go to `ScoringWeights` config.
- **Anti-hype posture preserved:** saturating, distinct-genuine-publisher-based; never raw article volume
  dominance (spec 94). Philosophy: signals before stories, avoid hype loops.
- **AD-9:** any report annotation (Option C) is a research statistic, never advice.

---

## Acceptance criteria

- [ ] A maintainer decision is recorded in the block above.
- [ ] The chosen option is turned into a concrete, buildable implementation spec (or this spec is amended to be
      one) with tests, and the BLOCKED banner is lifted.
- [ ] (If B) `radar-formula-v8` shape + default weight approved in-session (AD-6); fingerprint re-pinned by run.

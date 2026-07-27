# Task: Make the formula tunable inside the strategy — inline weight overrides, and give v9 the notedness discount it never got

> **Found by running it.** The first live 3-strategy run (2026-07-27) showed the two `radar-formula-v9`
> strategies almost *inverting* the v8 primary at the extremes: CAT ranked 43rd of 43 under `default` and
> **1st** under `filings-led`; CVX 42nd vs **2nd**; while GTY — v8's top pick, small and under-followed —
> fell to 18th and 33rd.
>
> Cause, confirmed by grep: `RadarScoreFormulaV8` references the notedness/following-tier discount in 13
> places. **`RadarScoreFormulaV9` references it zero times.** Spec 146 required breadth to become
> direction-correct in v9 but said nothing about carrying the discount over, so it was dropped. As shipped,
> a v9 strategy ranks on raw channel activity — largely a size proxy — which is close to the inverse of
> Radar's stated purpose (surface companies before the market notices). This is a gap in spec 146, not a
> defect in its implementation.

## Two changes, one goal: make the formula's behaviour a property of the strategy

### 1. v9 applies the notedness discount, reading the SAME `ScoringWeights` knobs

`ScoringWeights` already carries `OpportunityAttentionDivisor`, `OpportunityAttentionDiscountWeight`,
`FollowingTierDiscountWeight` and `OpportunityDiscountFloor` (plus the per-tier magnitudes). v8 consumes
them; v9 must too, using the **same** expression shape so the two formulas differ in composition, not in
what notedness means.

- **Reuse, do not re-derive** — extract v8's discount into the shared `ScoreSignalMath` alongside the eight
  primitives spec 146 already extracted, and route both formulas through it. This is the same
  reuse-over-copy rule the audit raised as M3 (v9 having copied v8's EvidenceConfidence and SignalVelocity
  blocks); fixing those two in passing is in scope if it falls out naturally, but do not let it grow the
  slice.
- **Setting `OpportunityAttentionDiscountWeight` and `FollowingTierDiscountWeight` to 0 must reproduce
  today's v9 exactly.** That is how a strategy opts out, and it is the compatibility proof.
- The discount applies to the **composed** channel score, not per channel — notedness is a property of the
  company, not of a source.

### 2. A strategy may declare weight overrides INLINE

Today tuning a magnitude means defining a whole named profile under `Radar:Scoring:Profiles:{name}` and
pointing `ScoringProfile` at it. That is fine for a handful of profiles and clumsy when the point is to run
several near-identical strategies that differ in one number.

```jsonc
{ "Name": "attention-light", "Formula": "radar-formula-v9",
  "ScoringProfile": "default",
  "Weights": { "FollowingTierDiscountWeight": 0.0, "OpportunityAttentionDiscountWeight": 0.25 },
  "Channels": [ ... ] }
```

- **Merge order, documented and tested:** code defaults → named `ScoringProfile` (if any) → inline
  `Weights`. Last wins, deterministic.
- **Unknown weight names must fail fast at startup**, naming the strategy and the key. A typo that silently
  leaves the ambient value is the exact failure this whole arc has been closing — and spec 138 already
  shipped one fail-open of this shape.
- Omitted `Weights` ⇒ byte-identical to today. Omitted `Formula` ⇒ v8, as now.
- **Layering:** parsing stays in the composition root; `Radar.Application` receives resolved
  `ScoringWeights` (no `IConfiguration` crossing the boundary).

## Identity

Resolved weights are already hashed into `ScoringConfigVersion` by value, so an inline override folds in
automatically — **verify this rather than assuming it**, because it is what stops two differently-tuned
strategies sharing a series. Two strategies differing only in one inline weight MUST get different
fingerprints; assert it.

Existing strategies must not re-stamp: `default`'s live fingerprint (`radar-scoring-fp-4da4b5ff6ec9` at the
60-day baseline window) must hold, and the test pins must not move.

## Why this shape

It makes "several attention strategies that differ only in how much notedness matters" a three-line config
change rather than a code change or a profile sprawl — which is the experiment the maintainer actually wants
to run, and the one the spec-140 leaderboard exists to judge.

## Files (verify against the tree before planning)

`RadarScoreFormulaV9`, `RadarScoreFormulaV8` (extraction source, behaviour unchanged), `ScoreSignalMath`,
`ScoringStrategyDefinition`, `ScoringStrategyFactory`, the strategy binding in
`InfrastructureServiceCollectionExtensions` (which today reads only `Formula`, `ScoringProfile`,
`SignalTypes` and `Channels`), and `ScoringWeights`.

## Constraints

- **v8 is byte-identical.** Prove it — spec 148 shipped `ScoringOutputStabilityTests` and spec 146 used a
  4000-case differential harness; reuse whichever fits rather than writing a third.
- **v9 with the two discount weights at 0 is byte-identical to today's v9.**
- **No pin move** for any existing strategy.
- **Fail fast** on unknown inline weight keys and on out-of-range values.
- Provenance intact; price never an input (AD-14).

## Out of scope (record, do not build)

- **Per-strategy report tables / cross-strategy comparison rendering** — worth doing, but it is a reporting
  slice and should follow this one, since a comparison across a formula that ignores notedness would be
  comparing the wrong thing.
- **Auto-tuning weights against price.** Humans declare; spec 140 judges.
- **A `radar-formula-v10`.** This changes v9's inputs, not its structure — no new formula class, though
  confirm whether adding the discount counts as a structure change under AD-6 and say so explicitly either
  way.

## Acceptance criteria

- [ ] v9 applies the notedness discount using the same `ScoringWeights` knobs and the same expression shape
      as v8, via one shared implementation rather than a copy.
- [ ] Setting both discount weights to 0 reproduces today's v9 output byte-identically — asserted.
- [ ] v8 output is byte-identical — asserted.
- [ ] A strategy may declare inline `Weights`; merge order is defaults → profile → inline, documented and
      tested.
- [ ] An unknown inline weight key fails fast at startup naming the strategy and the key.
- [ ] Two strategies differing only in one inline weight get different `ScoringConfigVersion`s — asserted.
- [ ] `default`'s fingerprint and the test pins are unmoved.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.

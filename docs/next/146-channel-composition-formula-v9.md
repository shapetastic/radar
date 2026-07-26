# Task: `radar-formula-v9` — a strategy is a weighted array of channels, and the formula is part of the strategy

> **Additive, not a migration.** v8 stays exactly as it is and remains the default. This slice makes the
> **formula a per-strategy choice**, so a v8 strategy and a v9 channel strategy can run over the **same
> collection pass** and be compared directly against price (spec 140). That is the whole point: an
> experiment you can run alongside the existing series, not a replacement that invalidates it.
>
> **This supersedes spec 143** (per-strategy collector selection). Declaring collectors *with weights and a
> sub-formula* is a superset of declaring which collectors you consume; 143 is moved to
> `docs/next/deferred/`.

## Why v8 cannot express this

Verified in `RadarScoreFormulaV8.Compute`: every component is computed over `input.Signals` — the **received**
set. There is no concept of expected or declared inputs, so the formula cannot know a strategy expected
patents signals and got none. Three consequences:

1. **A missing source is invisible.** Absence does not cost the strategy anything, because the denominator is
   whatever arrived.
2. **When it *is* visible, it is incoherent.** `SignalVelocity` compares current against previous window, so
   fewer signals drags it down — but `AttentionScore` enters Opportunity as an **inverse discount** (spec
   145), so a vanishing source *raises* the score. One component correctly falls while another perversely
   rises.
3. **Contributions are incommensurable.** There is no way to say "patents is half of my thesis". Without
   budget shares a strategy cannot allocate its score across sources, so sources with high traffic dominate
   ones with high value.

## Design

### 1. The formula becomes part of the strategy definition

Add `Formula` to the `Radar:Strategies[i]` entry, defaulting to the current v8. Resolve it through
`IScoreFormulaFactory` (spec 137 already made the formula per-strategy — it captures its weights in its
ctor), so this is composition, not a new dispatch mechanism.

**A strategy that does not name a formula must be byte-identical to today.** No pin move for v8 strategies.

### 2. A v9 strategy is an array of channels

```jsonc
{
  "Name": "patents-led", "Formula": "radar-formula-v9",
  "Channels": [
    { "Name": "patents",  "Collectors": ["patents"],              "Weight": 0.50, "Saturation": 3 },
    { "Name": "insider",  "Collectors": ["sec-form4"],            "Weight": 0.30, "Saturation": 2 },
    { "Name": "attention","Kind": "breadth",                       "Weight": 0.20, "Saturation": 3 }
  ]
}
```

- `score = Σ (weight_c × channelScore_c)`, every `channelScore_c ∈ [0,1]`, composite in `[0,1]`.
  **Verify what v8's components and Opportunity actually range over and reconcile explicitly** — do not
  assume `[0,1]` because this spec says so.
- **Weights must sum to 1.0** (with a documented float tolerance) and each lie in `[0,1]`. **Fail fast at
  startup** naming the strategy and the actual sum. A typo here silently rescales every score.
- Channel `Collectors` selects on the **recorded provenance** of each signal's evidence, so it behaves
  identically live and under replay (spec 139) where no collector object exists. Reuse `SignalTypeFilter`'s
  canonicalisation/validation shape rather than pasting a second copy; validate names fail-fast against the
  real registered collectors. Note the many-to-many caveat: `SignalType` and collector are different axes.
- 138's strategy-level `SignalTypes` remains a gate applied first; channels partition what survives it.

### 3. The rule that makes absence cost something

**A channel that produces no signals contributes 0, and the denominator does not shrink.** Do **not**
renormalise the surviving weights — renormalising would erase exactly the penalty this design exists to
create. A strategy declaring three channels can only approach 1.0 when all three fire; if its patents
channel is dark today, it is down by up to its 0.50 share, while a strategy that never declared patents is
unaffected. **Assert this with a test**, because renormalisation is the obvious-looking "fix" a future
reader will add.

A channel scores 0 whether its source was **down** or genuinely **quiet** — Radar scores evidence, and
absence of evidence is not evidence. But **provenance must record which it was** (declared collectors that
ran and returned nothing vs. did not run at all), so a 0 is explainable after the fact rather than
ambiguous.

### 4. Per-channel saturation is mandatory, not optional

Each channel needs its own "how much traffic counts as 1.0". RSS emits constantly and Form 4 rarely; with a
shared saturation the chatty channel pins at 1.0, the rare one never leaves the floor, and the weights become
decorative. Reuse the existing half-saturation shape (`AttentionHalfSaturation` precedent) **per channel**.

Within a channel, reuse v8's existing directional/confidence/recency machinery over that channel's signals —
extract the shared core rather than copying it (CLAUDE.md: reuse over copy). Direction, confidence, strength
and recency semantics must not change; only what set they are computed over.

### 5. Breadth is a channel, and it stops being inverted

`AttentionScore` measures breadth across **distinct publishers** and is inherently cross-source — it cannot
be a per-collector sub-score without losing its meaning. Model it as a **strategy-level channel with its own
weight** (`Kind: "breadth"` above), computed across all signals surviving the strategy's gate.

**In v9 it must be direction-correct**: more genuine breadth contributes *more*, not less. v8's inverse
discount stays in v8 — do not retrofit it, and do not carry the inversion into v9.

### 6. Versioning

This is a formula **structure** change ⇒ a new `RadarScoreFormulaV9` class with `Version =
"radar-formula-v9"` (AD-6). That is the case the versioning discipline exists for. Channel **weights and
saturations are magnitudes** and live in the strategy config, per the spec-89 structure/magnitude split —
tuning them must never require a code change.

The channel set, weights, saturations and formula version **are** part of that strategy's identity and fold
into its `ScoringConfigVersion` as spec 141 establishes. Adding a v9 strategy must not move any existing
strategy's identity.

## Files (verify against the tree before planning)

`RadarScoreFormulaV8` (untouched; extract shared primitives from it), new `RadarScoreFormulaV9`,
`IScoreFormulaFactory`, `ScoringStrategyDefinition`, `ScoringStrategyFactory`, `SignalTypeFilter` (shared
canonicalisation), `ScoringWeights`/config binding, `InfrastructureServiceCollectionExtensions`, and the
report/efficacy read side if it assumes v8's five-component shape.

## Constraints

- **v8 is untouched and stays the default.** Every existing strategy scores byte-identically; no pin move
  for them.
- **No renormalisation of weights when a channel is empty.** The core invariant.
- **Weights validated fail-fast** at startup; a bad sum never scores.
- **Price is never an input** (AD-14) — comparison against price is spec 140.
- **Provenance intact**: evidence → signal → channel → score must be traceable, and each channel's
  contribution attributable to the signals that produced it.
- **Layering:** no `IConfiguration` in `Radar.Application`; channels arrive resolved.
- **Output language** rules unchanged.

## Out of scope (record, do not build)

- **Replacing or deleting v8**, or migrating existing strategies onto v9.
- **Auto-tuning weights / fitting them to price.** That is how you overfit 44 companies. Weights are declared
  by a human; spec 140 judges the result.
- **Per-channel collector *scheduling*.** Collection remains one pass for everyone (137).
- **Strategy-vs-price comparison** — spec 140.

## Acceptance criteria

- [ ] `Formula` is per-strategy; omitting it is byte-identical to today, with no pin move for v8 strategies.
- [ ] A v9 strategy composes `Σ(weight × channelScore)` with channel scores in `[0,1]`; the composite range
      is documented and reconciled against v8's actual ranges.
- [ ] Weights that do not sum to 1.0, or fall outside `[0,1]`, fail fast at startup naming the strategy and
      the actual sum.
- [ ] **An empty channel contributes 0 and weights are not renormalised** — asserted, including the case
      where two of three channels are dark.
- [ ] A strategy that does not declare a dark channel is unaffected by it — asserted side by side with the
      one that does.
- [ ] Per-channel saturation works: a high-traffic and a low-traffic channel both map sensibly into `[0,1]`.
- [ ] Breadth is a weighted channel and contributes **positively** with more genuine breadth.
- [ ] Provenance distinguishes "declared collector ran and found nothing" from "declared collector did not
      run".
- [ ] Adding a v9 strategy moves no existing strategy's `ScoringConfigVersion`.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.

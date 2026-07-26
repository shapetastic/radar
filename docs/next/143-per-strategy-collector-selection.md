# Task: A strategy selects its collectors — `{ formula + weights + collectors }` as the unit of experiment

> Spec 138 let a strategy declare the `SignalType`s it consumes. That is **not** the same axis as choosing
> collectors: `SignalType` is a domain enum (`InsiderBuying`, `PatentActivity`, `MediaAttention`, …) and the
> mapping to collectors is many-to-many — `MediaAttention` can arrive from both the RSS and news-search
> collectors, and one collector emits several types. So today a strategy **cannot** say "score me from SEC
> Form 4 and USAspending only". This slice adds that axis, completing the intended unit of experiment:
> **a strategy is a formula + its weights + the set of collectors it trusts.**

## Why

The goal is to run many cheap strategies over one collection pass and find which combination actually
predicts something. Two of the three dimensions are already config: the formula's magnitudes
(`Radar:Scoring` profiles → `ScoringWeights`, spec 89) and the consumed signal types (spec 138). The
**source combination** — the dimension most likely to matter, since collector quality varies enormously and
is currently unmeasured — is not expressible at all.

## Design

### 1. `Collectors` on the strategy definition

Add to `Radar:Strategies[i]` alongside `SignalTypes`:

```jsonc
{ "Name": "insider-only", "ScoringProfile": "default", "Collectors": ["sec-form4"] }
```

- Reaches `ScoringStrategyDefinition` as already-parsed values (no `IConfiguration` in `Radar.Application`).
- **Omitted / empty / exhaustive all canonicalise onto one "all collectors" instance**, exactly as
  `SignalTypeFilter` does — so the default is unmoved. **Reuse `SignalTypeFilter`'s canonicalisation shape;
  do not paste a second copy** of the same logic (CLAUDE.md: reuse over copy — extract the shared core if the
  two filters genuinely share one).
- **Validate fail-fast at startup** against the real registered collector names. A typo'd collector name must
  not silently mean "all" or "none" — 138 shipped exactly that fail-open bug (`"SignalTypes": "X"` as a
  scalar) and it had to be fixed in a follow-up pass. Cover the scalar-vs-array and empty-array cases in
  tests at the **binding** seam, not just the application seam.

### 2. Filter on recorded provenance, not on collector wiring

The gate must apply to what each signal's **evidence** records as its source — the durable provenance chain —
so it works identically in a live run and in replay (spec 139), where no collector object exists at all.
**Check what identifier the evidence actually carries** (`SourceName`, collector name, or both) and gate on
that, reconciling with `SignalSourceDescriptor`'s `CollectorName` vocabulary. If the two vocabularies differ,
say so and map them explicitly rather than assuming they match.

Apply the gate in the same place 138's type filter is applied: after the 136 point-in-time predicate and the
85/113 dedupe, to **both** the current and previous (velocity) windows, as a pure membership gate — nothing
deleted, evidence chains intact for consumed signals.

### 3. Identity

The selected collector set **is** part of that strategy's identity and must be folded into its
`ScoringConfigVersion`, exactly as 138 folded the type set.

- **If spec 141 has landed**, fold it into the post-141 strategy-identity hash (the one that no longer
  contains the global collector CSV) and key the series by `StrategyName` as 141 established.
- **If 141 has not landed**, fold it the way 138 did and accept that the global CSV is still in there.
  Do not block on 141; do not silently re-open it.

### 4. Zero-source strategies

A strategy whose collector set yields no signals gets the same neutral, zero-evidence-link snapshot a
zero-signal company already gets (138's precedent) — so a filtered strategy's series stays continuous and
comparable for spec 140. Do not suppress the snapshot.

## Files (verify against the tree before planning)

`ScoringStrategyDefinition`, `ScoringStrategyFactory`, `SignalTypeFilter` (shared canonicalisation),
`ScoringEngine` (the gate + identity fold), `InfrastructureServiceCollectionExtensions` (bind + validate),
and the corresponding tests.

## Constraints

- **No formula, weight or collector-set change** — collection still runs all enabled collectors exactly once
  (137's invariant). This slice narrows what a strategy *reads*, never what is *collected*.
- **The default must be byte-identical**: no `Collectors` key ⇒ nothing changes, no pin move.
- **Provenance intact** per strategy; the gate reads provenance, it does not rewrite it.
- **Layering:** no `IConfiguration` in `Radar.Application`.

## Out of scope (record, do not build)

- **Per-strategy collector *scheduling*** — a strategy cannot cause a collector to run or not run. Collection
  is one pass for everyone.
- **Weighting collectors by trust.** Set membership only; per-source magnitudes are a weights question and
  belong in a profile. Do not blur the two (138's precedent).
- **Strategy-vs-price comparison** — spec 140.

## Acceptance criteria

- [ ] A strategy may declare `Collectors`; omitted/empty/exhaustive canonicalise to "all", byte-identical to
      today with no pin edit.
- [ ] The declared set is validated fail-fast at startup against real registered collector names; scalar and
      empty-array forms are covered by tests at the binding seam.
- [ ] Scoring consumes only signals whose recorded provenance is in the set, applied after the 136 predicate
      and 85/113 dedupe, to both windows; evidence chains intact.
- [ ] The set is folded into that strategy's `ScoringConfigVersion`, canonicalised and order-independent.
- [ ] The gate behaves identically under live scoring and replay (spec 139).
- [ ] A zero-source strategy still emits its neutral snapshot.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.

# Task: Per-strategy signal-type filter — a strategy declares which SignalTypes it consumes

> **The genuinely new capability of the strategy-decoupling arc.** Specs 136 (point-in-time honesty) and
> 137 (multi-strategy scoring) make strategies *plural* but they still all consume the **same** signals.
> This slice lets a strategy declare a **subset of `SignalType`s** it scores over — so you can run
> `insider-only`, `filings-only`, `everything`, etc. — and folds that declared set into **that strategy's
> fingerprint** so its efficacy series is honestly scoped to what it actually consumed.
>
> **Depends on spec 137 being merged.** This slice extends 137's strategy descriptor, factory, and the
> nested stage-6 loop. Do not start until 137 has landed — the exact type names and storage layout below are
> **provisional against 137's design** and must be reconciled with what 137 actually shipped.

## Why this is the slice that unblocks measurement

Until now, "which signals does this strategy believe in?" was not expressible — every strategy scored the
full signal set and differed only by weights. A signal-type filter makes the *hypothesis* explicit
("insider clusters alone predict better than the blended score") and, crucially, makes it **measurable in
isolation** once spec 140 lands. It is deliberately sequenced **after** the 137 fingerprint work so that the
declared signal set is a first-class part of what a strategy hashes.

## The key structural fact to verify against `main` (post-137)

`SignalType` is the domain enum on extracted signals. Both scoring reads (current-window and
previous-window, touched by spec 136) return the full approved signal set for a company. This slice adds a
**filter predicate** between the read and the formula: a strategy scores only signals whose `SignalType` is
in its declared set. **Verify the actual read seam and `SignalType` members before planning** — do not
assume the enum's contents.

## Design

### 1. Strategy configuration — add a signal-type set

Extend 137's `Radar:Strategies` entries:

```jsonc
"Radar": {
  "Strategies": [
    { "Name": "baseline",     "ScoringProfile": "default" },
    { "Name": "insider-only", "ScoringProfile": "default", "SignalTypes": [ "InsiderTransaction" ] }
  ],
  "PrimaryStrategy": "baseline"
}
```

- **Omitted or empty `SignalTypes` ⇒ "all signal types"** — this is what preserves 137's byte-identical
  default (the synthesised single strategy consumes everything, exactly as today).
- Each entry is validated against the real `SignalType` enum at startup (fail-fast, consistent with 137's DI
  validation): an unknown member names the offending config key and value.
- Use the actual enum member names from the domain — the `InsiderTransaction` above is **illustrative**;
  confirm the real names.

### 2. Fold the declared set into the fingerprint — carefully

The declared `SignalTypes` set is **strategy identity**, so it must be hashed into that strategy's
`ScoringConfigVersion`. But this is the load-bearing subtlety:

- **The "all types" case must hash identically to a strategy with no filter at all**, or 137's byte-identical
  default breaks and the pins move. Canonicalise: an empty/omitted set and a set containing every enum member
  both normalise to the **same** sentinel (e.g. hash nothing / a fixed "ALL" token) so the default
  fingerprint does **not** change. **`ScoringConfigFingerprintTests` must stay green with no pin edit** —
  AI-OFF `radar-scoring-fp-6b2f468041b9` / AI-ON `radar-scoring-fp-57356123e09b`.
- A **proper subset** hashes a **canonical, order-independent** encoding of the set (sort by enum value, not
  by name, so a rename never silently moves the hash). Document where this is added
  (`SignalSourceDescriptor` is the likely home — reconcile with 137).

### 3. Apply the filter at the read→score seam

- The filter runs **after** the point-in-time `CreatedAtUtc` read predicate (spec 136) and **after** the
  spec-85 cross-run dedupe — it is a pure "does this strategy consume this SignalType" gate, not a
  provenance change. A filtered-out signal is simply not fed to the formula; it is **not** deleted and its
  evidence chain is untouched.
- A strategy whose filter excludes **every** signal a company has must produce a **well-defined empty/neutral
  score**, not a crash and not a phantom snapshot that looks like real coverage. Decide and test the
  semantics explicitly (recommended: no snapshot written when the strategy consumed zero signals, mirroring
  how a company with no signals is handled today — verify that existing behaviour first).

### 4. Provenance stays intact

Every snapshot a filtered strategy writes still carries its full `ScoreEvidenceLink` chain **for the signals
it did consume**. The filter narrows the input set; it never weakens the trace for what remains.

## Assignment

Worktree: any. Files: `Radar.Application/Scoring/` (strategy descriptor + the read→score filter),
`SignalSourceDescriptor` (fingerprint fold), `InfrastructureServiceCollectionExtensions` (bind + validate
`SignalTypes`), `appsettings.json`, and tests.
Dependencies: **spec 137 merged** (functional dependency — extends its descriptor/factory/loop). Reconcile
all type and path names against 137's actual implementation.
Estimated time: ~3 h.

## Tests

- **Byte-identical default holds:** the synthesised single "all types" strategy produces the same snapshots,
  same `ScoringConfigVersion`, same paths. `ScoringConfigFingerprintTests` green **unmodified, no pin edit**.
- A strategy with `SignalTypes: [A]` scores **only** type-A signals; a sibling with `[A, B]` scores A and B;
  their `ScoringConfigVersion`s **differ from each other and from the "all" strategy**.
- **Canonicalisation:** `SignalTypes` listing every enum member hashes **identically** to the omitted/empty
  case (proves the default cannot move). Order of the list does not change the hash.
- A strategy whose filter excludes all of a company's signals yields the defined empty/neutral outcome (no
  crash, no phantom snapshot) — asserted.
- Fail-fast: an unknown `SignalTypes` value throws at startup naming the key and the bad value.
- Filtered signals retain their evidence chain in the snapshots that *are* written.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

## Constraints

- **No formula, rule, weight, tier or collector-set change.** The only new hashed input is the declared
  `SignalTypes` set, and it must be a **no-op for the default** (fingerprint pins hold).
- **One collection pass** (137's invariant) is unchanged — this slice only narrows what each strategy reads,
  it does not re-collect or re-read per type beyond what 137 already does per strategy.
- **Layering:** no `IConfiguration` in `Radar.Application`; the resolved signal-type set reaches the factory
  as already-parsed domain enum values.
- Provenance intact per strategy.

## Out of scope (record, do not build)

- **Replay across historical as-of dates** — spec 139.
- **Strategy-vs-price comparison** — spec 140.
- **Weight tuning per signal type** — weights already live in the profile; this slice is set membership, not
  magnitudes. Do not blur the two.
- **Reading signals once and scoring N times.** 137 left the reads per-strategy; a signal-type filter makes
  a shared-read-then-partition optimisation *possible* but it is not required at 43 companies — measure
  first, optimise only if it shows up.

## Acceptance criteria

- [ ] A strategy may declare `SignalTypes`; omitted/empty ⇒ all types (byte-identical to 137's default).
- [ ] The declared set is validated fail-fast against the real `SignalType` enum at startup.
- [ ] The set is folded into that strategy's `ScoringConfigVersion`, canonicalised so "all" == default and
      order-independent.
- [ ] Scoring consumes only in-set signals, applied after the 136 read predicate and 85 dedupe; evidence
      chains intact for consumed signals.
- [ ] A strategy that consumes zero signals for a company has defined, tested behaviour (no crash / no
      phantom snapshot).
- [ ] **Default fingerprints byte-identical; `ScoringConfigFingerprintTests` green with no pin edit.**
- [ ] `dotnet build` / `dotnet test` green.

# Task: Key the score series by strategy name; split collection provenance out of strategy identity

> **This is the slice the 136–140 arc exists for, and it is the one every prior slice was forbidden to do.**
> 137, 138 and 140 each carry the acceptance criterion *"default fingerprints byte-identical, no pin edit"* —
> 137 goes further: *"If a fingerprint pin needs editing, the change has leaked scope."* But the arc's stated
> goal is that **an unrelated collector must leave a strategy's identity untouched**, and achieving that
> **necessarily moves the default fingerprint**. The constraint that kept each slice safe is the same
> constraint that made the goal unreachable inside any of them. This slice takes the move deliberately.

## The measurement that justifies doing it now

Counted on `origin/main` @ `ba63d56` over the live baseline store (`data/scores`, 851 snapshots):

- **17 distinct `ScoringConfigVersion` values already exist** — 11 `radar-scoring-fp-*` fingerprints plus 6
  legacy `radar-scoring-config-vN` stamps.
- The largest single cohort is **133 snapshots ≈ 3 runs** at 43 companies.
- **The currently pinned AI-ON fingerprint `57356123e09b` has exactly 43 snapshots — one single run.**

So the "no pin edit" criterion has been protecting **one run's worth of history**, while the fingerprint has
in fact moved 17 times. There is no continuous efficacy series to preserve; spec 140's comparison has nothing
comparable to run against today regardless. **The migration cost is near zero right now and grows with every
slice built on the wrong key.**

## The actual defect

`SignalSourceDescriptor` builds, once at construction, a descriptor of the form:

```
rules=<KeywordSignalExtractor.RuleSetVersion>;collectors=<EVERY enabled collector name, CSV>;[ai=…;]
```

and 138's `SignalTypeFilter.Describe` **appends** to it — `_types is null ? sourceDescriptor : sourceDescriptor + _segment`
(`SignalTypeFilter.cs:143`). It narrows *what is scored* but never narrows *what is hashed*. So a strategy
declaring `SignalTypes: ["InsiderBuying"]` hashes the full seven-collector CSV. Enable an eighth collector
emitting only `RegulatoryApproval` and that strategy's scores are **bit-for-bit identical** while its
`ScoringConfigVersion` changes — a new series, for no behavioural reason.

The conflation: **collection provenance** ("what was collected on this run") and **strategy identity**
("what hypothesis produced this score") are different facts with different lifetimes, welded into one hash.

## Design

### 1. The series key becomes `StrategyName`, not `ScoringConfigVersion`

A strategy is **immutable by convention**: to change one, add a new named strategy (`momentum` →
`momentum-v2`). The name is then a stable, human-meaningful series key that a collector toggle cannot move,
and the "did the config change under me?" question is answered by convention rather than by a content hash
used as a primary key.

- Every read that groups/filters a series by `ScoringConfigVersion` today keys on `StrategyName` instead.
  **Find them — do not assume the list**: the spec-101/108 efficacy read side and the weekly report are the
  known consumers; grep for `ScoringConfigVersion` and reconcile every call site.
- `StrategyName` is already on `CompanyScoreSnapshot` (137, trailing + nullable, `null` ⇒ primary/legacy).
  Legacy `null` must keep reading as the primary series — do not orphan the existing 851 snapshots.

### 2. Split the descriptor into two recorded facts

Stamp **both** on the snapshot; they are separate fields, never concatenated:

- **`ScoringConfigVersion`** (strategy identity) — engine version, formula version, weights, attention
  descriptor, insider-materiality descriptor, media-collapse descriptor, extractor `RuleSetVersion`, and the
  strategy's declared `SignalTypes`. **The global collector CSV is removed from this hash.**
- **`CollectionProvenance`** (new, nullable/trailing) — the enabled-collector descriptor that
  `SignalSourceDescriptor` produces today, recorded verbatim as *what was collected*, hashed into nothing.

Provenance is not weakened: per-signal and per-evidence source attribution already carries which collector
produced each item (AD-3 / the provenance invariant). This slice **records the run-level collector set
alongside the score instead of inside its identity** — nothing becomes unknowable.

### 3. The fingerprint is demoted to a tripwire

Keep computing and stamping it — it is good provenance and a genuine drift detector. Stop treating it as an
invariant:

- On startup, compare each configured strategy's computed fingerprint against the one recorded for that name
  (reuse the existing `data/scoring-configs/` store — check its current shape before adding a new one). If a
  **name's** fingerprint has moved, that means a strategy was **edited in place**, which the immutability
  convention forbids: **fail fast with a message naming the strategy and telling the operator to add a new
  strategy name instead.** A collector toggle must NOT trip this, which is exactly what §2 buys.
- Rewrite `ScoringConfigFingerprintTests` pins as **deliberate change-detectors**: a comment stating that a
  pin move is a normal, intended act requiring a conscious update — not "scope leakage". Update the pins to
  the new post-split values in this slice.

### 4. Amend AD-10 in `docs/architecture-decisions.md`

Record the amendment: AD-10 conflated *stamp the config correctly* (keep) with *the stamp must never change*
(drop). State the new position — series keyed by strategy name, strategies immutable by convention,
fingerprint as tripwire, collection provenance recorded separately — and include the 17-versions/851-snapshots
measurement as the evidence. Correct the CLAUDE.md paragraph that currently records this coupling as "known,
not yet fixed".

### 5. Regenerating the fragmented history

139 (replay) and 142 (durable read path) have both merged, so replay can now actually see accrued signals.
Replay the stored history under the new identity to produce one continuous series out of the current 17
fragments. **Do not rewrite existing snapshots in place** — they are append-only history (AD-8); the
regenerated series is a new, labelled series alongside them.

**Scope it honestly against what 142 measured.** The real knowledge history is `createdAt`
2026-06-30 → 2026-07-26 — **27 days over 23 runs**, not the 2006–2026 `observedAt` span. A 30-day window
scores **2,628 signals across 44 companies**. If regeneration proves awkward, it is acceptable to take the
discontinuity, say so, and leave regeneration to a follow-up — 27 days of fragments is a small thing to lose
compared to shipping the identity split.

## Files (verify against the tree before planning)

`SignalSourceDescriptor` / `ISignalSourceDescriptor`, `ScoringEngine` (fingerprint composition in the ctor),
`ScoringConfigFingerprint`, `EffectiveScoringConfig`, `CompanyScoreSnapshot` (new trailing field), the
efficacy/report read side, `ScoringConfigFingerprintTests`, `docs/architecture-decisions.md`, `CLAUDE.md`.

## Constraints

- **The fingerprint pins MOVE in this slice. That is the deliverable, not a leak.** Update them; do not
  contort the design to hold them.
- **Scores must not change.** This is an identity/record-keeping change only — every component, weight and gate stays
  byte-identical. Prove it: same inputs ⇒ same numeric scores, only the stamps differ.
- **Provenance intact.** Evidence → signal → score chains unchanged; the collector set stays *recorded*.
- ⛔ **DO NOT touch evidence identity, and do not "improve" evidence id stability as a side effect.** Spec 142
  measured the live store: the 49,454 accrued signals collapse under the spec-85 dedupe key to 49,454 (a
  **1.00× no-op — that key has been near-vacuous all along**) but collapse by *content* to 5,368, i.e.
  **~9.2× real duplication**. That duplication lives in evidence identity, which the key includes, so the key
  structurally cannot see it. Radar is currently protected from ~9× score inflation only by the accident that
  duplicate evidence ids were never persisted, so `ScoringEngine` drops their signals. **Making evidence ids
  stable without first fixing content-level dedupe converts today's clean 1.03× scored set into a ~9×
  inflated one — silently, and in the direction that flatters every score.** That is spec 145; this slice
  must leave evidence identity exactly as it finds it, and a reviewer should reject any change that moves it.
- **Layering:** no `IConfiguration` in `Radar.Application`.
- No new collector, no formula change, no weight change, no price read (AD-14).

## Out of scope (record, do not build)

- **Per-strategy collector selection** — spec 143. This slice removes the collector set from strategy
  *identity*; letting a strategy *choose* collectors is the next slice.
- **Evidence identity and content-level dedupe** — spec 145, a hard prerequisite for anything that stabilises
  evidence ids (see the constraint above).
- **Splitting collection from scoring into separate runs** — spec 144.
- **Strategy-vs-price comparison** — spec 140, which should run *after* this slice so it keys on a stable
  series.

## Acceptance criteria

- [ ] Enabling or disabling a collector leaves a strategy's `ScoringConfigVersion` **unchanged** — asserted
      by a test that toggles the collector set and compares.
- [ ] The score series is keyed by `StrategyName`; legacy `null`-named snapshots still read as the primary
      series.
- [ ] `CollectionProvenance` is stamped on every snapshot and hashed into nothing.
- [ ] Editing a named strategy in place fails fast at startup naming the strategy; a collector toggle does
      not trip it.
- [ ] Numeric scores are byte-identical to pre-slice for identical inputs; only stamps differ.
- [ ] Pins updated deliberately, with the change-detector comment; AD-10 amended; CLAUDE.md corrected.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.

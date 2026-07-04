# Task: Config-driven scoring weights + a content-fingerprint scoring stamp (`radar-formula-v5`)

> **VERSION + APPROVAL (read first).** This slice moves the scoring formula's ~20 hardcoded magnitude
> constants out of `const`s and into a bound `ScoringWeights` config object injected into the formula, and
> makes `ScoringEngine.ScoringConfigVersion` a **deterministic content fingerprint of the effective resolved
> scoring config** instead of a hand-bumped code string. Because it changes the formula's *shape* (a new
> injected dependency + a config-derived stamp), it ships a new `IScoreFormula` identity
> **`radar-formula-v5`** — the same v-class precedent as `radar-formula-v1 → v2 → v3 → v4` (specs 58, 87, 88).
> Code defaults MUST equal the current (v4) magnitudes so a blank/absent config yields **byte-identical**
> output to v4 (pinned by a regression test), and the **default-weights fingerprint** is recorded so default
> runs stay comparable to each other.
>
> This is a **directed** slice (the maintainer asked for it), **NOT** the generic planner loop, and **NOT**
> architecture-gated. It **amends AD-6** (magnitudes become config, not `const`s) and **amends AD-10** (the
> stamp becomes content-derived, not hand-bumped) — both amendments are **Proposed pending maintainer
> sign-off** because they change settled conventions. Ship the ledger edits marked **Proposed**; the
> maintainer confirms the profile ergonomic, the canonicalization/hash choice, and the AD-6/AD-10 amendments.

> **STRICT SEQUENCING — implement AFTER spec 88 (`radar-formula-v4`) merges. Do NOT run in parallel.** This
> slice builds directly ON `radar-formula-v4`: it ports v4's constants (including the `IAttentionSourceWeights`
> seam and the re-tuned `AttentionHalfSaturation = 3.0`) into `ScoringWeights`, supersedes/deletes
> `RadarScoreFormulaV4`, and re-purposes the `ScoringConfigVersion` semantics that spec 88 bumps to `v10`. It
> touches the formula, its DI registration, `ScoringEngine`, the snapshot stamp, the comparability gate, the
> scoring tests, and the AD-6 + AD-10 ledger — **the same files spec 88 touches**. It MUST NOT run in parallel
> with spec 88 or any scoring / formula / engine / extractor / attention slice. Sequence it strictly after 88
> is merged; **re-read the merged `RadarScoreFormulaV4.cs` before starting** so the port is from the real v4,
> not from this spec's description of it.

## Overview

We have shipped `radar-formula-v2 → v3 → v4` inside about a week purely to change **numbers** — the attention
half-saturation, the media weight, the discount divisor, source-tier weights. Each change spawned a new
`IScoreFormula` class (delete-old, port-tests) and a manual `ScoringEngine.ScoringConfigVersion` bump, because
the ~20 magnitude constants live as `const`s in the formula. That treadmill is the cost of encoding *tunable
numbers* as *code identity*.

This slice ends it by separating **structure** (which stays versioned code) from **magnitudes** (which move to
config):

1. **Magnitudes → `ScoringWeights` config.** A single `ScoringWeights` record (bound from `Radar:Scoring:*`,
   injected into the formula) carries every magnitude the formula currently hardcodes. The formula reads
   weights from it instead of `const`s. **Code defaults equal the current v4 values**, so a blank config is
   byte-identical to v4. This lets us run Radar with different **weight profiles** in parallel (e.g. a
   conservative vs. an aggressive attention profile) writing to different `--Radar:*Directory` output dirs, to
   compare weightings on the same evidence — without a new code build per experiment.

2. **Stamp → content fingerprint.** Because runtime-configurable weights mean the old hand-typed
   `ScoringConfigVersion` string no longer *uniquely determines* the score (two runs with the same string but
   different weights would be wrongly judged comparable, silently re-creating the spec-69 defect), the snapshot
   MUST stamp a **deterministic content hash of the effective resolved scoring config** (structure identity +
   every weight value, canonically serialized). Spec-69's comparability gate then works unchanged: two
   snapshots are comparable **iff** their fingerprints are equal. The AD-10 property "any output-affecting
   change changes the stamp" is preserved — and now **automatic** (a weight tweak changes the fingerprint; it
   can no longer be forgotten). That automation is the upgrade over the hand-bumped constant.

This makes weight experimentation cheap and safe. It is **for deliberate, reasoned experiments, not
curve-fitting** — see Out of scope.

---

## Assignment

Worktree: any (but see sequencing)
Dependencies: **spec 88 (`radar-formula-v4`) MUST be merged first** — this supersedes it and re-purposes the
`ScoringConfigVersion` (`v10`) it introduces. Also: specs 69/70 + AD-10 (the `ScoringConfigVersion` stamp +
comparability gate), spec 84 (`IAttentionSourceWeights` publisher breadth), AD-3 (determinism), AD-6 (formula
identity), AD-5 (layering). Expected tree state at start: `radar-formula-v4` / `radar-scoring-config-v10`.
Conflicts with: touches the formula (`RadarScoreFormulaV4.cs` → `RadarScoreFormulaV5.cs`), its DI registration
(`InfrastructureServiceCollectionExtensions.cs`), `ScoringEngine.cs` (the stamp becomes a fingerprint), the
snapshot flow, `WeeklyReportBuilder`'s comparability gate (reads only, but re-verify), the scoring tests, and
the AD-6 + AD-10 ledger — **the same files spec 88 touches**. Adds a new Application config record
(`ScoringWeights`) + a canonical fingerprint helper. Must **NOT** run in parallel with spec 88 or any scoring /
formula / engine / extractor / attention slice — **sequence it strictly after 88 merges**.
Estimated time: ~2–2.5 h (a versioned, maintainer-co-designed formula plus a config seam and a provenance-stamp
semantics change — the highest-care class of slice). **See "Scope & split" — this is at the top of the slice
budget; a split is pre-authorized if it exceeds ~2.5 h, but the fingerprint MUST land with or before the
config-weights.**

---

## Grounding facts (verified against the current tree — do NOT re-research)

> These describe the tree **as it will be after spec 88 merges**. Re-read the merged `RadarScoreFormulaV4.cs`
> and `ScoringEngine.cs` before starting; port from the real v4.

- **The formula holds ~20 magnitude `const`s.** In the current `RadarScoreFormulaV3.cs` (v4 is that file with
  three changes — see spec 88): `DirPositive +1` / `DirNegative -1`, `RecencyFloor 0.5`, `TrajectoryNeutral
  50.0` / `TrajectoryScale 5.0`, `AttentionHalfSaturation` (v4 = **3.0**) / `MediaReachWeight` (v4 = **0.25**),
  the five quality weights (`QualPrimarySource 1.00` / `QualHigh 0.85` / `QualMedium 0.60` / `QualLow 0.35` /
  `QualUnknown 0.40`), `EcQualityBase 0.60` / `EcQualitySpan 0.40`, `EcDiversityBase 0.70` / `EcDiversitySpan
  0.30`, `DiversityTarget 3.0`, `VelocitySmoothing 10.0` / `VelocitySteady 50.0`, `OpportunityAttentionDivisor
  250.0`. These are exactly the magnitudes that move to `ScoringWeights`. (`DirPositive/Negative` are direction
  *signs*, not really tunable magnitudes — include them for completeness or leave them as structural `const`s;
  see Design.)
- **`ScoringOptions` deliberately holds ONLY `Window`.** `ScoringOptions.cs`:
  `public TimeSpan Window { get; init; } = TimeSpan.FromDays(30);` — documented "Operational scoring parameters
  (**NOT** the scoring formula)". `ScoringWeights` sits **alongside** it as the formula-weight config; do NOT
  fold weights into `ScoringOptions` (it would violate that documented "operational, not formula" boundary).
- **The formula is a pure function; v4 already takes one injected dependency.** `IScoreFormula.Compute(input)`
  is pure (no clock/IO/randomness — `IScoreFormula.cs`). After spec 88, `RadarScoreFormulaV4` already takes
  `IAttentionSourceWeights` via its constructor (breaking the parameterless form). This slice adds `ScoringWeights`
  as a second constructor dependency — the formula stays a **pure function of `(input, weights, sourceWeights)`**
  (all immutable). Registered as `TryAddSingleton<IScoreFormula, RadarScoreFormulaV4>()`
  (`InfrastructureServiceCollectionExtensions.cs`) and news-up'd in tests.
- **`ScoringConfigVersion` is a code constant stamped on every snapshot.** After spec 88,
  `ScoringEngine.cs` reads `private const string ScoringConfigVersion = "radar-scoring-config-v10";` and stamps
  it onto `CompanyScoreSnapshot.ScoringConfigVersion` at snapshot construction. `ScoringVersion` is composed
  separately as `$"{EngineVersion}+{_formula.Version}"` (`EngineVersion = "mvp-engine-v1"`). This slice replaces
  the constant with a **computed fingerprint** (see Design) — `ScoringVersion` (structure identity) is untouched.
- **`ScoringConfigVersion` is consumed ONLY as an opaque equality token — verified by grep.** The **only**
  consumer that reads its value is `WeeklyReportBuilder`'s comparability gate
  (`WeeklyReportBuilder.cs:201–205`): `previous is not null && !string.IsNullOrEmpty(current.ScoringConfigVersion)
  && string.Equals(current.ScoringConfigVersion, previous.ScoringConfigVersion, StringComparison.Ordinal)`.
  Nothing parses it, splits it, or renders its literal text to the report — it is a pure equality/`IsNullOrEmpty`
  token. `FileScoreSnapshotStore` serializes/round-trips it verbatim (`FileScoreSnapshotStore.cs:176,205,239`)
  and old files missing the property deserialize to `null` → "never comparable" (preserved). Tests assert its
  presence/value: `ScoringEngineTests` (`radar-scoring-config-v9`→`v10`; and a value-independent
  `IsNullOrEmpty` guard at line 414), `WeeklyReportBuilderTests` (uses arbitrary strings
  `"radar-scoring-config-v0"` / `null` to exercise the gate), `FileScoreSnapshotStoreTests` (round-trips an
  arbitrary string + null). **Conclusion: swapping the value from a human string to a hex fingerprint is safe —
  no consumer depends on it being human-readable.**
- **Canonical SHA256 hashing already exists in the Application layer (AD-3 precedent).**
  `EvidenceNormalizer.ComputeHash` (`Radar.Application/Evidence/EvidenceNormalizer.cs:151–156`) does
  `SHA256.HashData(Encoding.UTF8.GetBytes(canonical))` → `Convert.ToHexStringLower(hash)`. Reuse this exact
  idiom for the config fingerprint — a deterministic, culture-invariant, lowercase-hex hash of a canonical
  string. (Application referencing `System.Security.Cryptography` / `System.Text` is already established here.)
- **Options binding pattern.** Infrastructure options records (`NewsCollectorOptions`, `ScoringOptions`,
  `AttentionSourceTierOptions` from spec 88) are plain records bound from `Radar:*` config and registered as
  singletons; registration fails fast on invalid values. `ScoringWeights` follows this pattern.
- **Provenance/determinism unaffected by the mechanism.** The formula stays pure; contributions and
  `ScoreEvidenceLink` construction in `ScoringEngine` are untouched; existing on-disk snapshots keep their
  recorded `ScoringVersion` **and** their recorded `ScoringConfigVersion` string (a `v9`/`v10` snapshot's
  fingerprint field is whatever was written — it just won't equal a new default-fingerprint snapshot, which is
  correct: they were produced by different generations).

---

## Design

### 1. `ScoringWeights` — the magnitude config (Application)

A single immutable record in `Radar.Application/Scoring/`, every field defaulted to its **current v4 value**:

```csharp
namespace Radar.Application.Scoring;

/// <summary>
/// The tunable MAGNITUDES of the scoring formula (distinct from ScoringOptions, which holds only the
/// operational Window). Bound from Radar:Scoring:*; injected into the formula, which reads weights from
/// here instead of const fields. Every default EQUALS the radar-formula-v4 constant, so a blank/absent
/// config yields byte-identical v4 output. Immutable → the formula stays a pure function. These are for
/// DELIBERATE, reasoned experiments (run different profiles to compare weightings), NOT for curve-fitting
/// weights to price/backtest outcomes — see the spec's Out of scope.
/// </summary>
public sealed record ScoringWeights
{
    public double RecencyFloor { get; init; } = 0.5;
    public double TrajectoryNeutral { get; init; } = 50.0;
    public double TrajectoryScale { get; init; } = 5.0;
    public double AttentionHalfSaturation { get; init; } = 3.0;   // v4 value (post spec 88)
    public double MediaReachWeight { get; init; } = 0.25;         // v4 value
    public double QualityPrimarySource { get; init; } = 1.00;
    public double QualityHigh { get; init; } = 0.85;
    public double QualityMedium { get; init; } = 0.60;
    public double QualityLow { get; init; } = 0.35;
    public double QualityUnknown { get; init; } = 0.40;
    public double EcQualityBase { get; init; } = 0.60;
    public double EcQualitySpan { get; init; } = 0.40;
    public double EcDiversityBase { get; init; } = 0.70;
    public double EcDiversitySpan { get; init; } = 0.30;
    public double DiversityTarget { get; init; } = 3.0;
    public double VelocitySmoothing { get; init; } = 10.0;
    public double VelocitySteady { get; init; } = 50.0;
    public double OpportunityAttentionDivisor { get; init; } = 250.0;
}
```

- **Direction signs (`DirPositive +1` / `DirNegative -1`) stay structural `const`s in the formula** — they are
  not magnitudes to tune (flipping a sign is a structural change, not a weight experiment). Do **not** add them
  to `ScoringWeights`.
- **Validation.** The formula (or the DI registration) validates on construction and **fails fast** on
  nonsensical values that would break the math or the [0,100] clamp contract: `DiversityTarget` and
  `OpportunityAttentionDivisor` MUST be `> 0` (both are denominators); `AttentionHalfSaturation` MUST be
  `> 0` (denominator `reach + K`); the five quality weights and the four EC base/span values SHOULD be
  in a sane range (e.g. `>= 0`). A misconfiguration must throw at startup, never silently distort scoring.
  Keep the validation list tight and documented; do not over-constrain (weights are meant to be experimented
  with).

### 2. Profile ergonomic (recommended: a named-profile map over 20 loose knobs)

Twenty individual `Radar:Scoring:AttentionHalfSaturation`-style CLI knobs are unergonomic for the stated goal
(*run different weightings in parallel to compare them*). Recommend a **named-profile** shape, with individual
overrides still possible:

```jsonc
"Radar": {
  "Scoring": {
    "Profile": "default",           // selects which named profile's weights to bind; blank/absent => code defaults (== v4)
    "Profiles": {
      "default":     { },                                   // empty => all code defaults (byte-identical v4)
      "aggressive-attention": { "AttentionHalfSaturation": 1.5, "OpportunityAttentionDivisor": 300.0 },
      "conservative": { "AttentionHalfSaturation": 6.0 }
    }
  }
}
```

- Binding: Infrastructure DI resolves `Radar:Scoring:Profile` (default `"default"`), looks up
  `Radar:Scoring:Profiles:{name}`, binds it onto a `new ScoringWeights()` (so **unspecified fields keep the
  code default**), and registers the resulting `ScoringWeights` as a singleton. A blank/absent `Profile` or a
  `Profiles` section that lacks the named profile binds nothing → **all code defaults → byte-identical v4**.
  Fail fast only if a **named** profile is requested but not present (a silent fallthrough to defaults would
  mask a typo'd profile name in an experiment).
- Running two profiles in parallel: launch two Worker processes, each with `--Radar:Scoring:Profile=X` and a
  distinct `--Radar:*Directory` set (reports/scores/signals dirs), and compare the two report dirs. The
  fingerprint stamp guarantees the two runs' snapshots are correctly judged **not** comparable to each other
  (different weights → different fingerprint), so no cross-profile delta is ever fabricated.
- **The individual-override path stays available** for one-off tweaks: binding `Radar:Scoring:Profiles:default`
  (or a flat `Radar:Scoring` fallback if the coder prefers to also bind the section root over the selected
  profile) lets an operator override a single field without authoring a whole profile. Keep whichever of
  {profile-only, profile-then-root-override} the coder finds cleanest to bind deterministically — document the
  precedence in the DI method's `<summary>`.

> The exact binding precedence (profile-only vs. profile + root override) and the profile-key naming are a
> **maintainer-confirm item** — recommend the named-profile map above; if the maintainer prefers flat
> `Radar:Scoring:*` knobs, drop the `Profiles` layer and bind the section root directly onto `new
> ScoringWeights()`. Either way the code-default == v4 and the fingerprint invariants below MUST hold.

### 3. The fingerprint stamp (the crux — do NOT ship configurable weights without this)

The snapshot must stamp a **deterministic content hash of the effective resolved scoring config** so the
spec-69 comparability gate keeps working when weights are runtime-configurable.

- **What goes into the fingerprint:** the **structure identity** (`_formula.Version`, e.g.
  `"radar-formula-v5"`) **plus every `ScoringWeights` value** **plus the effective `IAttentionSourceWeights`
  identity/content** (the spec-88 publisher-tier map also affects Attention output, so it MUST be part of the
  generation identity — otherwise two runs with different tier maps would be wrongly comparable). Also include
  `EngineVersion`. Do **not** include operational `ScoringOptions.Window` — window length changes which signals
  feed a snapshot but is already reflected in `WindowStart/EndUtc` and is an *operational* knob, not a scoring
  *weight*; keep the fingerprint to the scoring-math generation. (If the coder judges Window should be in the
  generation identity, that is a maintainer-confirm item — default is **exclude** it, matching the
  "operational, not formula" boundary.)
- **Canonicalization (AD-3 — deterministic/culture-invariant):** build a canonical string with **stable field
  ordering** (a fixed, explicit field order — NOT reflection order, which is unstable across runtimes) and
  **culture-invariant number formatting** with a fixed round-trip format (e.g. each double via
  `value.ToString("R", CultureInfo.InvariantCulture)`, or `"G17"`). Concatenate as `key=value` pairs with a
  stable separator. Example canonical form:
  `engine=mvp-engine-v1;formula=radar-formula-v5;RecencyFloor=0.5;TrajectoryNeutral=50;...;OpportunityAttentionDivisor=250;attnTiers=<canonical tier map>`.
  Hash it with the **existing idiom**: `Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))`.
  Optionally prefix a short human tag for log/debug legibility, e.g.
  `$"radar-scoring-{fingerprintHex[..12]}"` — but the **full hash** (or a stable-length prefix) is what gates
  comparability; keep it a single opaque string. Recommend prefixing so on-disk stamps remain glanceable:
  `"radar-scoring-fp-<12hexchars>"`.
- **Where it's computed:** the fingerprint is a pure function of `(EngineVersion, _formula.Version,
  ScoringWeights, IAttentionSourceWeights identity)`, all injected into `ScoringEngine`. Compute it **once**
  (e.g. a `Lazy<string>` field or in the constructor) and stamp it onto every snapshot's `ScoringConfigVersion`
  in place of the old constant. Put the canonicalization+hash in a small dedicated, testable helper (e.g.
  `ScoringConfigFingerprint` static class in `Radar.Application/Scoring/`) so it can be unit-tested for
  determinism independently of the engine.
  - **`IAttentionSourceWeights` identity in the fingerprint:** extend that Application abstraction (added by
    spec 88) with a way to expose a **canonical content descriptor** of its effective map for fingerprinting —
    e.g. add `string CanonicalDescriptor()` (an ordered, culture-invariant serialization of the
    publisher→weight entries + the unknown default) to `IAttentionSourceWeights`, implemented by
    `ConfiguredAttentionSourceWeights`. Fold that descriptor into the fingerprint canonical string. (This is a
    minimal, additive interface change — a read-only descriptor for provenance, not new behaviour.) If the
    coder prefers not to widen the interface, an acceptable alternative is to inject the bound
    `AttentionSourceTierOptions` identity into the engine for fingerprinting — but the interface-descriptor
    keeps the Application engine off the Infrastructure options type (AD-5), so **prefer the descriptor**.
- **Default-weights fingerprint recorded.** Because the default `ScoringWeights` + default tier map produce a
  **fixed** canonical string, they produce a **fixed** fingerprint. Pin that exact fingerprint value in a test
  (a constant expected hex) so: (a) default runs are always comparable to each other (same fingerprint), and
  (b) any accidental change to a default weight is caught (the pinned fingerprint changes → test fails → forces
  a deliberate acknowledgement). This is the automatic replacement for the manual AD-10 bump.

### 4. How structure identity and the fingerprint coexist on the snapshot

- **`ScoringVersion` stays the structure identity** — `$"{EngineVersion}+{_formula.Version}"` =
  `"mvp-engine-v1+radar-formula-v5"`. Unchanged mechanism; it identifies the *code* that computed the score
  (for reproducibility/audit) and advances via `_formula.Version` exactly as today.
- **`ScoringConfigVersion` becomes the fingerprint** — the content hash of the *effective resolved* scoring
  config. It identifies the *generation* (structure + all magnitudes + tier map) and gates comparability. This
  is the AD-10 amendment: same obligation ("any output-affecting change changes the stamp"), now discharged
  **automatically** by derivation instead of by a hand edit.
- The snapshot record `CompanyScoreSnapshot` is **unchanged** (both fields already exist; only the *source* of
  `ScoringConfigVersion`'s value changes). No Domain change.

---

## The v5 formula (precise)

Implement `RadarScoreFormulaV5 : IScoreFormula`, `Version => "radar-formula-v5"`. Port `RadarScoreFormulaV4`
**verbatim** and change **only** the source of the magnitudes: every `private const double X = …` becomes a
read of `_weights.X`. The computation, clamps, contributions, explanation shape, empty-window behaviour, the
`IAttentionSourceWeights`-weighted reach (from v4), the `PreviousSignals`/window/provenance rules — **all
byte-for-byte identical to v4 when weights are default**. Because defaults == v4 values, the numeric output is
identical; only the *identity* changes (`v4` → `v5`) to mark the structural change (config-driven + fingerprint).

- Constructor: `RadarScoreFormulaV5(ScoringWeights weights, IAttentionSourceWeights sourceWeights)` with
  `ArgumentNullException.ThrowIfNull` on both, and the fail-fast weight validation from Design §1.
- `Version => "radar-formula-v5"`; explanation prefix + empty-window string become `radar-formula-v5:`.
- Direction signs remain structural `const`s; everything else reads `_weights`.
- Per the spec-impl checklist, **delete `RadarScoreFormulaV4`** (do not leave it dormant) and **port its
  tests** to `RadarScoreFormulaV5Tests`.

---

## Version, DI, and ledger

- New `RadarScoreFormulaV5 : IScoreFormula`, `Version => "radar-formula-v5"`. Delete `RadarScoreFormulaV4`;
  port tests. On-disk snapshots keep their recorded `ScoringVersion` **and** `ScoringConfigVersion` strings —
  provenance intact, no old formula code needed.
- New Application `ScoringWeights` record (Design §1) + a `ScoringConfigFingerprint` helper (Design §3) +
  a `CanonicalDescriptor()` addition to `IAttentionSourceWeights` (or the injected-options alternative).
- `ScoringEngine`: **remove the `private const string ScoringConfigVersion` constant**; inject `ScoringWeights`
  and `IAttentionSourceWeights` (or its descriptor source), compute the fingerprint once, and stamp it onto
  every snapshot's `ScoringConfigVersion`. `EngineVersion` and the `ScoringVersion` composition are unchanged.
  Update the class `<remarks>`/comment to document that the generation stamp is now a content fingerprint
  (AD-10 amended), and that this engine still holds no formula math.
- DI (`InfrastructureServiceCollectionExtensions`): repoint `IScoreFormula` → `RadarScoreFormulaV5`; register
  `ScoringWeights` bound from `Radar:Scoring:Profile`/`Profiles` (Design §2) as a singleton with the code
  default as fallback (blank/absent profile ⇒ default ⇒ byte-identical v4); fail fast on an invalid weight or a
  named-but-missing profile. `RadarScoreFormulaV5` resolves `ScoringWeights` + `IAttentionSourceWeights` from
  DI. (`IAttentionSourceWeights`/`ScoringOptions` registrations from spec 88 are unchanged.)
- `ScoringVersion` updates automatically to `mvp-engine-v1+radar-formula-v5` via `_formula.Version` — **no**
  `EngineVersion` edit.
- **AD-6 amendment (Proposed).** Add a subsection **`Refinement — radar-formula-v5 (spec 89): magnitudes become
  config; structure stays versioned`**, mirroring the v2/v3/v4 subsections: state that the ~20 magnitude
  `const`s move to a bound `ScoringWeights` object injected into the formula; **code defaults equal v4 so a
  blank config is byte-identical**; only *structure* (component shape, the fixed field ordering, direction
  signs) stays versioned code; a magnitude change is now a config edit, **not** a new formula-version class.
  Mark it **Proposed until maintainer sign-off** on the profile ergonomic and the amendment; update the AD-6
  **Status** line with `Refined · 2026-07-04 (spec 89, radar-formula-v5 — Proposed until maintainer sign-off:
  magnitudes → config)`, and mark the v1–v4 constant blocks as *superseded by radar-formula-v5* (the magnitudes
  now live in `ScoringWeights`; the recorded default values are the v4 values).
- **AD-10 amendment (Proposed).** Amend AD-10 to record that `ScoringConfigVersion` is **no longer a hand-bumped
  code constant** but a **deterministic content fingerprint of the effective resolved scoring config** (structure
  identity + all `ScoringWeights` values + the attention tier-map descriptor), computed via a canonical,
  culture-invariant SHA256 (AD-3). The AD-10 correctness property is **preserved and strengthened**: any
  output-affecting change (formula shape, any weight, the tier map) changes the fingerprint **automatically**, so
  it can no longer be silently forgotten. The spec-69 comparability gate is unchanged (still `Ordinal` string
  equality of `ScoringConfigVersion`), now comparing fingerprints. Note the one behavioural nuance for the
  ledger: because the stamp is derived, the "bump" obligation is discharged by derivation — the remaining human
  obligation is only to bump `_formula.Version` (structure) when the *shape* changes (AD-6). Mark **Proposed
  pending maintainer sign-off**; update AD-10's Status line accordingly. Cross-reference AD-6, AD-3, spec 69.

---

## Project structure changes

```text
src/Radar.Application/Scoring/
  ScoringWeights.cs                # ADD: immutable magnitude config record; every default == v4 value.
  ScoringConfigFingerprint.cs      # ADD: static helper — canonical string of (EngineVersion, formulaVersion,
                                   #   ScoringWeights, attention-tier descriptor) → lowercase-hex SHA256 (AD-3).
  IAttentionSourceWeights.cs       # MODIFIED: add string CanonicalDescriptor() for the fingerprint (additive,
                                   #   read-only provenance). [Or: fold the tier-map identity in via injected
                                   #   AttentionSourceTierOptions — descriptor preferred, keeps AD-5.]
  RadarScoreFormulaV5.cs           # ADD: port of V4; every magnitude const → _weights.X; takes ScoringWeights
                                   #   + IAttentionSourceWeights via ctor; Version => "radar-formula-v5";
                                   #   explanation prefix "radar-formula-v5"; fail-fast weight validation.
  RadarScoreFormulaV4.cs           # DELETE (spec-impl checklist: no dormant deprecated code).
  ScoringEngine.cs                 # MODIFIED: remove the ScoringConfigVersion const; inject ScoringWeights +
                                   #   IAttentionSourceWeights, compute the fingerprint once, stamp it as
                                   #   ScoringConfigVersion. EngineVersion + ScoringVersion composition unchanged.
                                   #   Update the class remarks (generation stamp is now a content fingerprint).
  ScoringOptions.cs                # UNCHANGED (still holds only Window — weights live in ScoringWeights).

src/Radar.Infrastructure/(News or Attention)/
  ConfiguredAttentionSourceWeights.cs  # MODIFIED (if descriptor path): implement CanonicalDescriptor().

src/Radar.Infrastructure/DependencyInjection/
  InfrastructureServiceCollectionExtensions.cs  # MODIFIED: IScoreFormula -> RadarScoreFormulaV5; bind + register
                                   #   ScoringWeights from Radar:Scoring:Profile/Profiles (code default == v4);
                                   #   fail fast on invalid weight / named-but-missing profile.

tests/Radar.Application.Tests/Scoring/
  RadarScoreFormulaV5Tests.cs      # ADD: port of V4 tests; default-weights output byte-identical to v4;
                                   #   a changed weight moves the score; determinism; version string.
  RadarScoreFormulaV4Tests.cs      # DELETE (ported).
  ScoringConfigFingerprintTests.cs # ADD: determinism/canonicalization; default fingerprint pinned; a changed
                                   #   weight or tier map changes the fingerprint; culture-invariance.
  ScoringEngineTests.cs            # MODIFIED: the stamp is now a fingerprint (assert it equals the pinned
                                   #   default fingerprint under default weights, and DIFFERS under a changed
                                   #   weight); repoint new RadarScoreFormulaV4(...) usages to V5.
  ScoringWeightsBindingTests.cs    # ADD (or fold into an Infra DI test): profile binding — blank profile =>
                                   #   defaults; a named profile overrides only its fields; missing named
                                   #   profile fails fast; invalid weight fails fast.

tests/Radar.Application.Tests/Reporting/
  WeeklyReportBuilderTests.cs      # VERIFY (likely unchanged): the comparability gate still segments by the
                                   #   ScoringConfigVersion string — now a fingerprint. Add/adjust a case if
                                   #   needed to prove same-fingerprint => comparable, different => "(scoring updated)".

docs/architecture-decisions.md     # MODIFIED: AD-6 v5 refinement subsection (Proposed) + AD-10 amendment
                                   #   (Proposed) + both Status lines.
```

No `Radar.Domain` change (the snapshot record is unchanged; only the *source* of `ScoringConfigVersion` moves).
No collector, no report-renderer, no new package, no provider SDK, no DB (AD-8, files-first).

---

## Tests

Port `RadarScoreFormulaV4Tests` → `RadarScoreFormulaV5Tests` (delete the V4 file), and add the config +
fingerprint pins. Match the existing test style (`BuildSignal`/`InputFrom` helpers, `[Fact]`/`Assert`). News up
the formula with `new RadarScoreFormulaV5(new ScoringWeights(), <fake or ConfiguredAttentionSourceWeights>)`.

Ported/kept green (numbers unchanged because default weights == v4):
- **Version:** `Version == "radar-formula-v5"` and it appears in the explanation.
- Trajectory / EvidenceConfidence / Velocity / Attention (tier-weighted reach from v4) / Contributions /
  Neutral-zero-weight / empty-window / determinism / `Opportunity_FallsAsAttentionRises_NeverZeroes` — all hold
  with **identical expected numbers** to v4 (this is the regression guarantee below).

New pins locking config-driven weights + the fingerprint:
1. **DEFAULT CONFIG == v4 (byte-identical regression — the headline guarantee).** For a representative input,
   `RadarScoreFormulaV5(new ScoringWeights(), defaultTiers).Compute(input)` yields the **exact** same
   `ScoreComponents`, `ComponentJson`, contributions, and explanation body (modulo the `v5` prefix) as the v4
   expectations. Pin the exact component integers. (This is the "blank config ⇒ byte-identical v4" proof.)
2. **A changed weight changes the score.** Construct V5 with a `ScoringWeights` that changes one magnitude
   (e.g. `AttentionHalfSaturation = 12.0`, the old v3 value) and assert the resulting Attention (and
   Opportunity) **differ** from the default-weights result for the same input — proving the formula reads the
   config, not `const`s.
3. **Fingerprint determinism & canonicalization (`ScoringConfigFingerprintTests`).** The same inputs
   `(engine, formulaVersion, weights, tierDescriptor)` always produce the **same** hex string; the string is
   lowercase hex, culture-invariant (compute under `CultureInfo.CurrentCulture` set to a comma-decimal locale
   like `de-DE` and assert it is unchanged), and stable across field-value round-trips (`0.5` formats
   identically regardless of ambient culture).
4. **Default fingerprint is pinned & recorded.** The fingerprint of `(mvp-engine-v1, radar-formula-v5,
   new ScoringWeights(), default tier map)` equals a **pinned expected hex constant** in the test (compute it,
   paste it, assert equality). This guarantees default runs stay comparable to each other and catches any
   accidental default-weight drift (the automatic AD-10 replacement).
5. **A changed weight OR a changed tier map changes the fingerprint.** Changing any single `ScoringWeights`
   field, or the attention tier descriptor, yields a **different** fingerprint than the default — proving
   output-affecting changes automatically re-stamp (AD-10 property, now automatic).
6. **The comparability gate still segments by fingerprint (`ScoringEngineTests` + `WeeklyReportBuilderTests`).**
   - `ScoringEngineTests`: the engine under **default** weights stamps `ScoringConfigVersion` == the pinned
     default fingerprint; under a **changed** weight it stamps a **different** value. Update the
     `Versioning_StampsScoringConfigVersion` assertion accordingly (no longer `radar-scoring-config-v10`); the
     `IsNullOrEmpty` presence guard stays green.
   - `WeeklyReportBuilder`: two snapshots with the **same** fingerprint are comparable (numeric delta rendered),
     two with **different** fingerprints render `(scoring updated)` and the policy falls back to no-previous —
     verify the existing gate tests still express this with fingerprint-shaped strings (adjust the arbitrary
     test strings if any assertion assumed the `radar-scoring-config-vN` shape).
7. **Profile binding (`ScoringWeightsBindingTests` / Infra DI test).** Blank/absent `Radar:Scoring:Profile`
   binds all code defaults (⇒ default fingerprint); a named profile overrides **only** its specified fields
   (unspecified stay default); a requested-but-missing named profile **fails fast**; an out-of-range weight
   (e.g. `OpportunityAttentionDivisor = 0`) **fails fast** at registration.
8. **Repoint `new RadarScoreFormulaV4(...)`** usages to `RadarScoreFormulaV5` (pass `ScoringWeights` +
   weights); those assertions stay green.

Keep all other scoring / pipeline / report / extractor tests green. Search the tree for any remaining
`RadarScoreFormulaV4` / `radar-formula-v4` / `radar-scoring-config-v10` reference and update or remove it.

---

## Scope & split (pre-authorized)

This is at the top of the ~2–2.5 h budget: port consts→config, the profile binding, the fingerprint helper +
its interface descriptor, migrate the `ScoringConfigVersion` semantics + verify the gate, port all formula
tests, and two ledger amendments. **If it exceeds ~2.5 h, split — but the split MUST NOT leave configurable
weights shipping under a stale manual stamp.** The only safe split is:

- **89a — fingerprint stamp first (no config change).** Replace the hand-bumped `ScoringConfigVersion` constant
  with a fingerprint of the **still-hardcoded** v4 constants (structure identity + the v4 magnitudes read from
  the formula's own `const`s, exposed for fingerprinting) + the tier descriptor. Ships the canonical helper,
  the interface descriptor, the AD-10 amendment, and the gate verification. Output is byte-identical to v4 and
  the fingerprint is pinned. This lands the crux (automatic stamp) with zero behaviour change.
- **89b — magnitudes → `ScoringWeights` + profiles.** Then move the constants into `ScoringWeights`, feed the
  same values into the fingerprint (which is unchanged for default weights — pin it identical to 89a's), ship
  `radar-formula-v5` + profiles + the AD-6 amendment.

If NOT splitting, ship as one `radar-formula-v5` slice. **Do NOT do the reverse split** (config-weights without
the fingerprint) — that is the exact unsafe state (runtime-tunable weights under a stale manual stamp) this
spec exists to prevent. State in the PR whether you split and along which line.

---

## Constraints

- Target `net10.0`, C# 14. `ScoringWeights`, the fingerprint helper, and the formula live in
  `Radar.Application`; config binding + the tier-map descriptor implementation live in `Radar.Infrastructure`
  / the host (AD-5: Application depends on abstractions/config records, Infrastructure binds config). No
  provider SDK, no AI, no HTTP, no DB (AD-8, files-first).
- **AD-6 change via the sanctioned mechanism:** bump the formula `Version` (`radar-formula-v5`), amend the
  ledger (**Proposed** until maintainer sign-off), delete v4, port tests, preserve v1–v4 snapshot provenance
  (recorded `ScoringVersion` **and** `ScoringConfigVersion` strings unchanged on disk).
- **AD-10 change via amendment:** `ScoringConfigVersion` becomes a derived content fingerprint; the "any
  output-affecting change re-stamps" property is preserved and made automatic; amend the ledger (**Proposed**).
- **Determinism (AD-3) is load-bearing here:** the fingerprint MUST be canonical — fixed field ordering,
  culture-invariant number formatting, lowercase-hex SHA256 (reuse the `EvidenceNormalizer.ComputeHash` idiom).
  The formula stays a pure function of `(input, immutable weights, immutable source-weights)`; no
  clock/IO/randomness; contributions and ordering unchanged.
- **Byte-identical default:** a blank/absent `Radar:Scoring:*` config MUST yield output byte-identical to v4
  (pinned by test 1) and the pinned default fingerprint (test 4).
- **Provenance is sacred and preserved:** one contribution per current-window signal, in input order, from
  current-window signals only; `ScoreEvidenceLink` construction in `ScoringEngine` untouched; the snapshot
  record unchanged.
- **AD-9:** no advice language; all component scores stay clamped in `[0,100]`. Weight experiments only move
  ranking/labels via legitimate score changes — no banned tokens.
- **The spec-69 comparability gate is unchanged in shape** (still `Ordinal` string equality of
  `ScoringConfigVersion`); only the value's *source* changes. Re-verify no consumer relied on the human-string
  form (grep confirmed only `WeeklyReportBuilder` reads it, as an opaque token).
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` stay green.

---

## Out of scope (note, do NOT implement this round)

- **AUTO-OPTIMIZING / curve-fitting weights against price or a backtest.** Config-driven weights exist for
  **deliberate, reasoned experiments** (compare hand-chosen profiles on the same evidence), **NOT** for fitting
  weights to price/return outcomes. On Radar's tiny universe that would overfit and would violate the ethos
  ("signals before stories; a research assistant, not a trading bot"). A price-efficacy backtest is a separate
  backlog idea and is explicitly out of scope here; do not add any price/outcome feedback into weight
  selection.
- **The price-efficacy visual / any price data ingestion.**
- **News-volume within-source signal-multiplicity dedup** (the `MediaReachWeight·mediaCount` term) — still
  deferred (spec 88 Out of scope).
- **Going-concern / skeptic-reviewer wiring.**
- **Putting `ScoringOptions.Window` into the fingerprint** (default: excluded — operational, not a weight;
  maintainer-confirm if desired).
- **Any change to a component's shape/math** (Trajectory, EvidenceConfidence, Velocity, Attention, Opportunity)
  — v5 changes only WHERE the magnitudes come from, not the formulas; default output is byte-identical to v4.
- **A UI/report surface for the profile name or the fingerprint** — the fingerprint stays an internal
  comparability token; do not render it into the report body this round.

---

## Acceptance criteria

- [ ] **Implemented AFTER spec 88 merges** (this supersedes `RadarScoreFormulaV4` and re-purposes the
      `ScoringConfigVersion` `v10` it introduced); not run in parallel with any scoring/formula/engine/
      extractor/attention slice.
- [ ] `ScoringWeights` (Application) holds every scoring magnitude with **defaults equal to the v4 values**;
      it sits alongside `ScoringOptions` (which still holds only `Window`), not inside it. Direction signs stay
      structural `const`s. Invalid weights (zero/negative denominators, etc.) **fail fast**.
- [ ] `RadarScoreFormulaV5` (`Version = "radar-formula-v5"`) reads every magnitude from injected
      `ScoringWeights` instead of `const`s; with **default** weights its output is **byte-identical to v4**
      (pinned). `RadarScoreFormulaV4` is **deleted** (no dormant code); DI points `IScoreFormula` at V5;
      `ScoringVersion` records `mvp-engine-v1+radar-formula-v5` via `_formula.Version`; `EngineVersion`
      unchanged; no Domain change.
- [ ] `ScoringConfigVersion` is now a **deterministic content fingerprint** of the effective resolved scoring
      config (structure identity + all `ScoringWeights` values + the attention tier-map descriptor), computed
      via a canonical, culture-invariant, lowercase-hex SHA256 (AD-3); the `ScoringEngine` hand-bumped constant
      is **removed**; the fingerprint is computed once and stamped on every snapshot.
- [ ] The **default-weights fingerprint is pinned** in a test (so default runs stay comparable and default-drift
      is caught); a **changed weight or tier map changes both the score and the fingerprint** (tested); the
      fingerprint is deterministic/canonical/culture-invariant (tested).
- [ ] The **spec-69 comparability gate still works**: same fingerprint ⇒ comparable (numeric delta), different
      fingerprint ⇒ `(scoring updated)` + policy no-previous fallback (verified in `WeeklyReportBuilderTests`).
      Grep confirms no other consumer relied on `ScoringConfigVersion` being a human string.
- [ ] A **named-profile** config ergonomic (`Radar:Scoring:Profile`/`Profiles`) is implemented with individual
      overrides still possible; blank/absent profile ⇒ code defaults (⇒ byte-identical v4, default fingerprint);
      a named-but-missing profile fails fast; two profiles can run in parallel to distinct `--Radar:*Directory`
      output dirs and are correctly judged non-comparable (tested where practical).
- [ ] `docs/architecture-decisions.md`: **AD-6 amended** (v5 refinement subsection — magnitudes → config,
      structure stays versioned, defaults == v4; **Proposed**) and **AD-10 amended** (stamp becomes a derived
      fingerprint; property preserved and automatic; **Proposed**); both Status lines updated.
- [ ] `RadarScoreFormulaV4Tests` ported to `RadarScoreFormulaV5Tests` (V4 file deleted) with the byte-identical
      regression + config-driven pins; `ScoringConfigFingerprintTests` added; all scoring / pipeline / report /
      extractor tests green.
- [ ] If split, split along the pre-authorized **89a (fingerprint first) → 89b (config-weights)** line only —
      never config-weights before the fingerprint; the PR states whether it split.
- [ ] Layering (AD-5), determinism (AD-3), files-first (AD-8), provenance, and AD-9 label/advice rules
      preserved; no Domain / collector / report-renderer / component-math change.
      `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` are green.

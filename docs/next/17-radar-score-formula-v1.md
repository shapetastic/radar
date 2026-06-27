# Task: Implement the Real Scoring Formula (`radar-formula-v1`)

## Overview

Replace the provisional `PlaceholderScoreFormula` with the **first real, maintainer-owned** scoring
formula, `RadarScoreFormulaV1`. The five component computations below were **designed and approved by
the maintainer** — they are the product decision the `IScoreFormula` seam was built to hold. This
spec specifies every constant and every step; the coder **transcribes it faithfully and invents
nothing**. Do not "improve", retune, or simplify the math — if something seems off, stop and flag it
rather than changing a constant.

The formula stays pure, deterministic, BCL-only, and every component clamps to `[0,100]`. All tunable
numbers are named `private const` fields at the top of the class — "simple, visible, versioned" per
the spec.

> **Why this is in a spec and not invented by the coder:** the human-owned boundary means the coder
> never *decides* weights. Here the weights are already decided (by the maintainer) and written down
> exactly; the coder's job is a precise implementation of them.

---

## Assignment

Worktree: pending
Dependencies: 14-scoring-contracts-and-formula-seam, 15-scoring-engine-windowing-and-persistence,
16-scoring-previous-window-input (this formula consumes `ScoringInput.PreviousSignals`)
Conflicts with: none
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/
  Scoring/
    RadarScoreFormulaV1.cs        # NEW: the real formula
    PlaceholderScoreFormula.cs    # DELETE (superseded)
  DependencyInjection/ (or wherever AddRadarApplicationServices lives)
                                  # CHANGED: register RadarScoreFormulaV1 as IScoreFormula

tests/Radar.Application.Tests/
  Scoring/
    RadarScoreFormulaV1Tests.cs       # NEW
    PlaceholderScoreFormulaTests.cs   # DELETE (the type it tests is gone)
```

Find the DI registration that currently binds `IScoreFormula` to `PlaceholderScoreFormula` (a
`TryAddSingleton`) and change it to `RadarScoreFormulaV1`. Update the existing scoring-engine DI test
if it asserts the concrete formula type.

---

## The formula — `RadarScoreFormulaV1 : IScoreFormula`

`Version => "radar-formula-v1"`. `Compute(ScoringInput input)` is pure/deterministic: no clock, no
randomness, no I/O. It reads `input.Signals` (current window, each a `ScoringSignal` = signal +
evidence) and `input.PreviousSignals` (previous window, signals only), plus `WindowStartUtc` /
`WindowEndUtc` for recency.

### Named constants (put these at the top, exactly)

```csharp
// Direction → sign used in trajectory.
private const int DirPositive = +1;
private const int DirNegative = -1;
// Neutral and Mixed contribute 0 to direction (see DirectionSign()).

// Recency weighting within the current window: newest signal counts 1.0, oldest counts RecencyFloor.
private const double RecencyFloor = 0.5;

// Trajectory mapping: neutral midpoint and scale (T_raw ∈ [-10,10] → 0..100).
private const double TrajectoryNeutral = 50.0;
private const double TrajectoryScale   = 5.0;

// Attention saturation: reach / (reach + K) → 0..1. MediaAttention signals add half a unit of reach.
private const double AttentionHalfSaturation = 5.0;
private const double MediaReachWeight        = 0.5;

// EvidenceConfidence quality weights (by EvidenceQuality).
private const double QualPrimarySource = 1.00;
private const double QualHigh          = 0.85;
private const double QualMedium        = 0.60;
private const double QualLow           = 0.35;
private const double QualUnknown       = 0.40;

// EvidenceConfidence blend: each adjustment has a floor + span so it discounts but never zeroes.
private const double EcQualityBase  = 0.60;
private const double EcQualitySpan  = 0.40;   // base+span = 1.0 at best quality
private const double EcDiversityBase = 0.70;
private const double EcDiversitySpan = 0.30;  // base+span = 1.0 at full diversity
private const double DiversityTarget = 3.0;   // distinct source types at/above which diversity is maxed

// Velocity: 50 * (now+λ)/(prev+λ). λ smooths low-activity ratios.
private const double VelocitySmoothing = 10.0;
private const double VelocitySteady    = 50.0;

// Opportunity: attention at 100 halves the score (divisor 200), never zeroes it.
private const double OpportunityAttentionDivisor = 200.0;
```

### Per-signal helpers

```csharp
private static int DirectionSign(SignalDirection d) => d switch
{
    SignalDirection.Positive => DirPositive,
    SignalDirection.Negative => DirNegative,
    _ => 0,                       // Neutral and Mixed are direction-neutral
};

private static double QualityWeight(EvidenceQuality q) => q switch
{
    EvidenceQuality.PrimarySource => QualPrimarySource,
    EvidenceQuality.High          => QualHigh,
    EvidenceQuality.Medium        => QualMedium,
    EvidenceQuality.Low           => QualLow,
    _ => QualUnknown,             // Unknown (and any unmapped) → QualUnknown
};

// Clamp+round any double component to an int in [0,100], deterministic midpoint handling.
private static int Score(double v) =>
    Math.Clamp((int)Math.Round(v, MidpointRounding.AwayFromZero), 0, 100);
```

### Recency factor

`windowLength = WindowEndUtc - WindowStartUtc`. For each current signal:

```
age      = (WindowEndUtc - signal.ObservedAtUtc) / windowLength      // 0 = newest, 1 = at window start
age      = clamp(age, 0, 1)
recency  = 1 - RecencyFloor * age                                    // newest 1.0, oldest 0.5
```

Guard `windowLength <= TimeSpan.Zero` → treat `age = 0` (recency = 1.0) for all, to avoid divide-by-zero.
Compute the ratio with `TotalSeconds` (double).

### 1. TrajectoryScore (50 = neutral, >50 improving)

Per current signal `i`: `w_i = (double)Confidence_i * recency_i`,
`term_i = DirectionSign_i * Strength_i * w_i`.

```
sumW = Σ w_i
T_raw = (sumW <= 0) ? 0 : (Σ term_i) / sumW            // ∈ [-10, 10]
TrajectoryScore = Score(TrajectoryNeutral + TrajectoryScale * T_raw)
```

### 2. AttentionScore (saturating on breadth)

```
distinctSources = count of DISTINCT Evidence.SourceName across current signals
                  (ordinal, case-insensitive; ignore null/whitespace names)
mediaCount      = count of current signals with Type == SignalType.MediaAttention
reach           = distinctSources + MediaReachWeight * mediaCount
AttentionScore  = Score(100 * reach / (reach + AttentionHalfSaturation))   // reach 0 → 0
```

### 3. EvidenceConfidenceScore

```
avgConf    = mean over current signals of (double)Confidence            // 0..1
qualFactor = mean over current signals of QualityWeight(Evidence.Quality)
distinctTypes = count of DISTINCT Evidence.SourceType across current signals
divFactor  = min(1, distinctTypes / DiversityTarget)
EvidenceConfidenceScore = Score(
    100 * avgConf
        * (EcQualityBase  + EcQualitySpan  * qualFactor)
        * (EcDiversityBase + EcDiversitySpan * divFactor))
```

### 4. SignalVelocityScore (50 = steady activity)

```
actNow  = Σ Strength over current signals (input.Signals)
actPrev = Σ Strength over input.PreviousSignals
ratio   = (actNow + VelocitySmoothing) / (actPrev + VelocitySmoothing)
SignalVelocityScore = Score(VelocitySteady * ratio)       // 2x → 100, steady → 50, half → 25
```

### 5. OpportunityScore (the headline: strong, trusted, under-the-radar improvement)

Multiplicative — a strong trajectory only scores high when we're confident *and* few have noticed:

```
OpportunityScore = Score(
    TrajectoryScore
    * (EvidenceConfidenceScore / 100.0)
    * (1 - AttentionScore / OpportunityAttentionDivisor))   // attention 100 halves, never zeroes
```

`TrajectoryScore`, `EvidenceConfidenceScore`, `AttentionScore` here are the already-clamped int
component values computed above.

### Contributions (provenance — current window only)

Emit **exactly one `ScoreContribution` per current-window signal**, in input order:

- `SignalId = signal.Id`, `EvidenceId = s.Evidence.Id`.
- `ContributionReason = $"{Type} ({Direction}), strength {Strength}, confidence {Confidence:0.00}"`.
- `ContributionWeight =` the signal's signed contribution to trajectory, rounded:
  `(int)Math.Round(DirectionSign_i * Strength_i * w_i, MidpointRounding.AwayFromZero)`
  (may be negative for negative-direction signals; do **not** clamp this — it is a signed weight, not
  a 0..100 score).

Never emit contributions for `PreviousSignals`.

### Explanation and ComponentJson

```
Explanation = $"radar-formula-v1: {input.Signals.Count} signal(s) over {windowDays}d → " +
              $"Trajectory {T}, Opportunity {O} (Attention {A}, Confidence {E}, Velocity {V})."
```

where `windowDays = (int)Math.Round(windowLength.TotalDays, MidpointRounding.AwayFromZero)` and
`T/O/A/E/V` are the five component scores. Deterministic.

`ComponentJson = JsonSerializer.Serialize(components)` (`System.Text.Json`, in-framework — no package).

### Empty current window

If `input.Signals.Count == 0`: return `ScoreComponents(0,0,0,0,0)`, empty contributions, a valid
`ComponentJson`, and `Explanation = "radar-formula-v1: no signals in window."` — regardless of
`PreviousSignals` (no current activity ⇒ nothing improving to report).

---

## Tests

`RadarScoreFormulaV1Tests.cs` (xUnit). Because this is the real, owned formula, tests **may** assert
specific documented behaviours and boundary values (unlike the placeholder tests). Use the shared
`Radar.TestSupport` builders; set each signal's `EvidenceId` to its evidence `Id`. Construct
`ScoringInput` with explicit current/previous lists and a real window (e.g. 30 days).

- **Version.** `Version == "radar-formula-v1"`; `Explanation` contains the version string.
- **Neutral baseline.** A single `Neutral`-direction signal → `TrajectoryScore == 50`.
- **All-positive improves; all-negative declines.** Positive-direction signals → `Trajectory > 50`;
  negative-direction → `Trajectory < 50`.
- **All components in range.** Mixed input → every component in `[0,100]`.
- **Clamp holds at extremes.** Several max-strength, max-confidence positive signals → `Trajectory`
  does not exceed 100 (and `>= 0`); likewise all-negative does not go below 0.
- **Attention saturates and is monotonic.** More distinct `SourceName`s ⇒ `AttentionScore` strictly
  higher but never `> 100`; e.g. assert 5 distinct sources yields a higher score than 1 and is `< 100`.
- **EvidenceConfidence rewards quality + diversity.** Same confidences but `PrimarySource`/diverse
  source types ⇒ higher `EvidenceConfidenceScore` than `Low`/single-type.
- **Velocity acceleration.** `actNow > actPrev` ⇒ `Velocity > 50`; `actNow < actPrev` ⇒ `< 50`;
  equal ⇒ `== 50`; empty previous with current activity ⇒ `> 50`.
- **Opportunity falls as attention rises.** Holding trajectory/confidence fixed, a higher-attention
  input yields a lower (or equal) `OpportunityScore`; high attention never zeroes a strong opportunity.
- **One contribution per current signal, in order**, with matching `SignalId`/`EvidenceId`; a
  negative-direction signal yields a negative `ContributionWeight`. **No** contribution references a
  `PreviousSignals` entry.
- **Empty current window.** Zero current signals (even with non-empty previous) → all components 0,
  empty contributions, `ComponentJson` deserializes to a `ScoreComponents`, non-empty explanation.
- **Determinism.** Two `Compute` calls on the same input produce equal components, `ComponentJson`,
  explanation, and contribution tuples.
- **Recency.** Two inputs identical except one positive signal's `ObservedAtUtc` is newer ⇒
  `Trajectory` is `>=` the older-dated case (newer positive signal weighs at least as much). Keep the
  assertion to the documented direction of effect, not an exact number.

---

## Constraints

- Target .NET 10. Application-only, pure/deterministic, BCL only (`System.Text.Json` in-framework).
- **Transcribe the specified math exactly** — every constant as named above. No extra tuning, no
  alternative mappings. If a step is ambiguous or appears wrong, stop and flag rather than guessing.
- Component scores clamp to `[0,100]` via `Score(...)`. `ContributionWeight` is a signed weight and is
  **not** clamped.
- Provenance: one contribution per current-window signal, each carrying `SignalId` + `EvidenceId`;
  never from `PreviousSignals`.
- Delete `PlaceholderScoreFormula` and its test; switch DI to `RadarScoreFormulaV1`.
- Reuse domain `Signal`/`EvidenceItem`/enums; add no domain types.

---

## Acceptance criteria

- [ ] `RadarScoreFormulaV1 : IScoreFormula` exists with `Version == "radar-formula-v1"` and all
      constants as named `private const` fields.
- [ ] The five components are computed exactly as specified, each clamped to `[0,100]`.
- [ ] Velocity uses `input.PreviousSignals`; Opportunity is the multiplicative form; Trajectory is
      confidence-and-recency weighted with 50 = neutral.
- [ ] One provenance-carrying contribution per current-window signal (signed `ContributionWeight`),
      none from `PreviousSignals`; empty current window → all-zero components.
- [ ] `PlaceholderScoreFormula` and its tests are deleted; DI binds `IScoreFormula` to
      `RadarScoreFormulaV1`; any DI test asserting the concrete type is updated.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release` are green.

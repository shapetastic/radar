# Task: Scoring Contracts and Provisional Formula Seam

## Overview

Begin Stage 6 (Scoring) per the full-pipeline spec. This slice lands the **pure contracts** for
scoring and, critically, isolates the actual scoring formula behind a small, clearly-marked seam so
the maintainer can own it.

It introduces:

- the typed input/output records the scoring engine and formula exchange,
- an `IScoreFormula` strategy interface — **the human-owned seam**,
- a `PlaceholderScoreFormula` provisional implementation so the solution builds and the engine
  (task 15) can be wired and tested end to end.

> **HUMAN-OWNED BOUNDARY — read before coding.** The real scoring formula (the *weights* and the
> *exact computation* of `TrajectoryScore`, `OpportunityScore`, `AttentionScore`,
> `EvidenceConfidenceScore`, and `SignalVelocityScore`) is a **product decision the maintainer will
> own**. This task does **not** define the real formula. `PlaceholderScoreFormula` is an explicitly
> provisional, throwaway stand-in whose only jobs are: be deterministic, stay in range [0,100], and
> emit one contribution per contributing signal so traceability can be exercised. **Do not tune it,
> do not treat its numbers as endorsed, and do not write tests that assert its specific weights.**
> The maintainer replaces `PlaceholderScoreFormula` with the real `IScoreFormula` later; everything
> else in Stage 6 is infrastructure that must not change when they do.

This slice is Application-only: **no DI changes and no persistence** (those land in task 15). That
keeps the seam reviewable in isolation before the engine is wired up.

---

## Assignment

Worktree: pending
Dependencies: 02-domain-models, 09-signal-extraction-contract-and-validation (uses `Signal`/`EvidenceItem`)
Conflicts with: 15-scoring-engine-windowing-and-persistence (task 15 references these types and adds
files in the same `Scoring/` folders) — sequence task 15 after this one
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/
  Scoring/
    ScoringSignal.cs          # NEW: signal paired with its source evidence
    ScoringInput.cs           # NEW: company + window + windowed signals (formula input)
    ScoreComponents.cs        # NEW: the five 0-100 component scores
    ScoreContribution.cs      # NEW: one signal->evidence contribution (basis for ScoreEvidenceLink)
    ScoreComputation.cs       # NEW: formula output (components + explanation + JSON + contributions)
    IScoreFormula.cs          # NEW: the human-owned formula seam
    PlaceholderScoreFormula.cs# NEW: provisional, replaceable stand-in implementation

tests/Radar.Application.Tests/
  Scoring/
    PlaceholderScoreFormulaTests.cs   # NEW
```

Namespace: `Radar.Application.Scoring`. Pure/deterministic, so it lives in **Application**. Per AD-5,
Application may reference `Microsoft.Extensions.*` abstractions, but this slice needs none — BCL only
(`System.Text.Json` is in-framework). Reuse the existing domain records `Signal` and `EvidenceItem`;
**add no domain types** (the engine in task 15 maps `ScoreComputation` onto the existing domain
`CompanyScoreSnapshot`/`ScoreEvidenceLink`).

---

## Implementation details

### Input records

```csharp
namespace Radar.Application.Scoring;

using Radar.Domain.Evidence;
using Radar.Domain.Signals;

/// <summary>A reviewed signal paired with the source evidence it was extracted from.</summary>
public sealed record ScoringSignal(Signal Signal, EvidenceItem Evidence);

/// <summary>
/// The complete, pre-windowed input to a single company score computation. The engine (task 15) is
/// responsible for selecting the window and the signals; the formula is a pure function of this input.
/// </summary>
public sealed record ScoringInput(
    Guid CompanyId,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    IReadOnlyList<ScoringSignal> Signals);
```

The formula receives evidence alongside each signal so a real implementation can reason about source
diversity / primary-source weighting for `EvidenceConfidenceScore`. Each `ScoringSignal.Evidence.Id`
must equal its `ScoringSignal.Signal.EvidenceId` — the engine guarantees this; the formula may assume
it.

### Output records

```csharp
namespace Radar.Application.Scoring;

/// <summary>The five MVP component scores, each constrained to the inclusive range 0..100.</summary>
public sealed record ScoreComponents(
    int TrajectoryScore,
    int OpportunityScore,
    int AttentionScore,
    int EvidenceConfidenceScore,
    int SignalVelocityScore);

/// <summary>
/// One signal's contribution to a score, the basis for a domain <c>ScoreEvidenceLink</c>. Preserves
/// provenance: every contribution names the signal and the evidence behind it.
/// </summary>
public sealed record ScoreContribution(
    Guid SignalId,
    Guid EvidenceId,
    string ContributionReason,
    int ContributionWeight);

/// <summary>
/// The pure output of an <see cref="IScoreFormula"/>: the component scores, a human-readable
/// explanation, a machine-readable component breakdown (JSON), and the per-signal contributions used
/// to build <c>ScoreEvidenceLink</c> rows. Contains no Ids/timestamps — the engine assigns those.
/// </summary>
public sealed record ScoreComputation(
    ScoreComponents Components,
    string Explanation,
    string ComponentJson,
    IReadOnlyList<ScoreContribution> Contributions);
```

### The formula seam (`IScoreFormula`) — HUMAN-OWNED

```csharp
namespace Radar.Application.Scoring;

/// <summary>
/// The scoring formula seam. The implementation defines HOW raw signals become the five component
/// scores — this is the product-owned decision (weights, thresholds, exact computation). The scoring
/// engine (task 15) depends only on this interface and never on a concrete formula.
///
/// Contract for any implementation:
///  - Pure and deterministic: same <see cref="ScoringInput"/> -> equal <see cref="ScoreComputation"/>.
///    No I/O, no clock, no randomness.
///  - Every component score MUST be within the inclusive range 0..100.
///  - <see cref="Version"/> is a stable, explicit formula identity (e.g. "mvp-v1"); change it
///    whenever the computation changes, so snapshots remain reproducible and auditable.
///  - Empty input (no signals) MUST still return a valid computation (in-range components, valid
///    <c>ComponentJson</c>, a non-empty explanation, and an empty contributions list).
/// </summary>
public interface IScoreFormula
{
    /// <summary>Stable formula version recorded on every score snapshot.</summary>
    string Version { get; }

    ScoreComputation Compute(ScoringInput input);
}
```

### Provisional implementation (`PlaceholderScoreFormula : IScoreFormula`) — REPLACEABLE STAND-IN

A loud, file-level doc comment must state this is provisional and not a tuned/endorsed formula, and
that the maintainer owns the replacement. Requirements:

- `Version => "placeholder-v0"`.
- `Compute` is pure and deterministic (no clock, no randomness, no I/O).
- **Every component clamped to [0,100]** via a single private `Clamp(int)` helper using
  `Math.Clamp(value, 0, 100)`. Clamping is the load-bearing guarantee here, not the chosen mapping.
- Produce **exactly one `ScoreContribution` per input `ScoringSignal`**, in input order, with
  `SignalId = s.Signal.Id`, `EvidenceId = s.Evidence.Id`, a short deterministic
  `ContributionReason` (e.g. `$"{s.Signal.Type} ({s.Signal.Direction})"`), and an in-range
  `ContributionWeight` (use the signal's `Strength` — it is already validated 1..10).
- `ComponentJson` = `JsonSerializer.Serialize(Components)` (or the five values as an object).
  `System.Text.Json` is in-framework — do not add a package.
- `Explanation` = a short deterministic sentence including the signal count and the `Version`, e.g.
  `$"placeholder-v0: scored from {input.Signals.Count} signal(s)."`.
- Empty input: return all-zero `ScoreComponents`, empty contributions, valid `ComponentJson`, and a
  non-empty explanation.

The placeholder's component math should be the **simplest defensible deterministic mapping** of the
inputs (it exists only to make the pipeline runnable). Keep it trivial and mark each line
`// PROVISIONAL placeholder — maintainer to replace with the real formula`. Do not introduce tuned
constants or anything that reads as a considered weighting. Tests assert only ranges/structure/
determinism, never the specific numbers — keep it that way.

---

## Tests

`Radar.Application.Tests/Scoring/PlaceholderScoreFormulaTests.cs` (xUnit). Use the shared
`Radar.TestSupport` `SignalBuilder`/`EvidenceBuilder` to construct `ScoringSignal`s (set each
signal's `EvidenceId` to its evidence's `Id`). **Assert structure, ranges, traceability, and
determinism only — never specific weight/score values.** Cases:

- **All components in range.** For an input with a few signals, every one of the five component
  scores is `>= 0` and `<= 100`.
- **One contribution per signal, in order.** `Contributions.Count == input.Signals.Count`, and each
  contribution's `SignalId`/`EvidenceId` equal the corresponding input signal/evidence Ids.
- **Contribution weights in range.** Every `ContributionWeight` is within a sane bound (e.g.
  `>= 0`); do not assert an exact number.
- **Empty input.** Zero signals -> valid computation: components all in range, `Contributions` empty,
  `ComponentJson` non-empty and deserializes back to a `ScoreComponents`, `Explanation` non-empty.
- **Clamping holds at extremes.** Build several max-strength signals; assert no component exceeds 100
  (and none is negative). Do not assert it equals 100 — just that the clamp bound holds.
- **Determinism.** Calling `Compute` twice on the same input yields an equal `ScoreComputation`
  (records compare structurally; compare `Components`, `ComponentJson`, and the contribution tuples).
- **Version.** `Version == "placeholder-v0"` and the `Explanation` contains the version string.

---

## Constraints

- Target .NET 10.
- Application-only, pure/deterministic; BCL only (`System.Text.Json` in-framework). No DI changes,
  no persistence, no repository calls in this slice.
- **Do not define the real scoring formula.** The formula stays behind `IScoreFormula`;
  `PlaceholderScoreFormula` is provisional and replaceable, and tests must not lock its weights.
- Preserve provenance: every `ScoreContribution` carries both `SignalId` and `EvidenceId`.
- Reuse existing domain `Signal`/`EvidenceItem`; add no domain types.
- Versioned formula identity (`placeholder-v0`) per the schema-spec versioning rule.

---

## Acceptance criteria

- [ ] `ScoringSignal`, `ScoringInput`, `ScoreComponents`, `ScoreContribution`, `ScoreComputation`,
      `IScoreFormula`, and `PlaceholderScoreFormula` exist under `Radar.Application.Scoring`.
- [ ] `IScoreFormula` documents the pure/deterministic, 0..100, versioned, empty-input contract and
      is clearly marked as the human-owned seam.
- [ ] `PlaceholderScoreFormula.Version == "placeholder-v0"`, clamps every component to [0,100],
      emits one provenance-carrying contribution per input signal, and handles empty input.
- [ ] No DI registration, no persistence, and no repository dependencies were added in this slice.
- [ ] Tests assert ranges, structure, traceability, empty-input, clamping, determinism, and version —
      and assert **no** specific formula weights/scores.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release` are green.

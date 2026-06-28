# Task: Carry the Previous Signal Window into the Scoring Input

## Overview

`SignalVelocityScore` is defined (full-pipeline spec, Stage 6) as *"count and strength acceleration
over 30 days vs the previous 30 days."* The real formula (task 17) therefore needs the **previous**
window's signal activity, but today's `ScoringInput` only carries the **current** window. This slice
adds that missing input — pure engine/contract plumbing, **no formula and no formula change**.

Key simplification: `ScoringEngine` already fetches *all* of a company's signals via
`ISignalRepository.GetByCompanyAsync`, so the previous window can be sliced from that same list with
**no extra repository call**. Velocity compares *activity magnitude* (a sum of `Strength`), so the
previous window is carried as signals only — **no evidence is loaded for it** (evidence/provenance is
only needed for the current-window signals that produce `ScoreEvidenceLink`s).

> **HUMAN-OWNED BOUNDARY.** This slice does **not** define or touch any scoring math. It only widens
> the data the formula receives. `PlaceholderScoreFormula` stays exactly as-is (it ignores the new
> field). All five component computations remain behind `IScoreFormula`.

---

## Assignment

Worktree: pending
Dependencies: 14-scoring-contracts-and-formula-seam, 15-scoring-engine-windowing-and-persistence
Conflicts with: 17-radar-score-formula-v1 (task 17 consumes `PreviousSignals`) — sequence task 17 after this one
Estimated time: ~1 hour

---

## Project structure changes

```text
src/Radar.Application/
  Scoring/
    ScoringInput.cs     # CHANGED: add PreviousSignals
    ScoringEngine.cs    # CHANGED: slice + pass the previous window

tests/Radar.Application.Tests/
  Scoring/
    ScoringEngineTests.cs           # CHANGED: assert previous window is sliced + passed
    PlaceholderScoreFormulaTests.cs # CHANGED: construct ScoringInput with the new arg (empty list)
```

No DI changes, no new files, no domain changes.

---

## Implementation details

### 1. `ScoringInput` — add `PreviousSignals`

```csharp
using Radar.Domain.Signals;

/// <summary>
/// The complete, pre-windowed input to a single company score computation. The engine selects the
/// window and the signals; the formula is a pure function of this input.
///
/// <para><see cref="Signals"/> is the CURRENT window (start, end] — each paired with its source
/// evidence for provenance. <see cref="PreviousSignals"/> is the immediately-preceding window of the
/// same length (start - window, start], carried as signals ONLY (no evidence): it exists so the
/// formula can measure signal-activity acceleration (velocity). It must NOT be used to build
/// contributions / ScoreEvidenceLinks — only the current-window signals carry provenance.</para>
/// </summary>
public sealed record ScoringInput(
    Guid CompanyId,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    IReadOnlyList<ScoringSignal> Signals,
    IReadOnlyList<Signal> PreviousSignals);
```

`PreviousSignals` is the domain `Signal` type (not `ScoringSignal`) — no evidence is attached.

### 2. `ScoringEngine` — slice and pass the previous window

In `ScoreCompanyAsync`, reuse the already-fetched `allSignals`. The previous window is the same
length as the current window, immediately before it, using the **same window/review rules** as the
current window (exclusive start, inclusive end; `Approved` only):

```csharp
var previousWindowStartUtc = windowStartUtc - _options.Window;

var previousSignals = allSignals
    .Where(s => s.ObservedAtUtc > previousWindowStartUtc && s.ObservedAtUtc <= windowStartUtc)
    .Where(s => s.ReviewStatus == SignalReviewStatus.Approved)
    .OrderBy(s => s.ObservedAtUtc).ThenBy(s => s.Id)   // deterministic (AD-3)
    .ToList();
```

- Note the boundary is shared with the current window: current is `(windowStartUtc, windowEndUtc]`,
  previous is `(previousWindowStartUtc, windowStartUtc]`. A signal observed exactly at
  `windowStartUtc` belongs to the **previous** window (inclusive end), not the current — this is
  consistent and avoids double-counting.
- **Do not** load evidence for previous signals and **do not** drop them for missing evidence — they
  are activity-only inputs.
- Pass the list into the new `ScoringInput` argument:

```csharp
var input = new ScoringInput(companyId, windowStartUtc, windowEndUtc, pairs, previousSignals);
```

Everything else in the engine (current-window pairing, evidence loading, ordering, version stamping,
snapshot/link building, persistence, logging) is unchanged.

---

## Tests

### `ScoringEngineTests` — previous window is sliced and passed

Add a small in-test capturing `IScoreFormula` fake (records the last `ScoringInput` it received and
returns a valid all-zero `ScoreComputation`) so the test can assert what the engine handed the
formula. (If an equivalent capture mechanism already exists in the test file, reuse it.)

- **Previous window populated.** Seed `Approved` signals across three windows: previous
  `(start-W, start]`, current `(start, end]`, and older than previous. Score with `windowEnd = end`.
  Assert the captured input's `Signals` contains exactly the current-window signals and
  `PreviousSignals` contains exactly the previous-window signals (by Id), and nothing from the older
  region.
- **Boundary at `windowStart` goes to previous.** A signal observed exactly at `windowStartUtc`
  appears in `PreviousSignals`, not in `Signals`.
- **Review filter applies to previous too.** A non-`Approved` signal inside the previous window is
  excluded from `PreviousSignals`.
- **No evidence required for previous.** A previous-window signal whose evidence is absent from the
  evidence repository still appears in `PreviousSignals` (it is not dropped), while a current-window
  signal with missing evidence is still dropped from `Signals` (existing behaviour preserved).
- **Empty previous window.** With no signals before `windowStart`, `PreviousSignals` is empty (not
  null) and scoring still succeeds.

### `PlaceholderScoreFormulaTests` — constructor update only

Update every `new ScoringInput(...)` to pass the new argument (e.g. `Array.Empty<Signal>()` or a
small list). No behavioural assertions change — the placeholder ignores `PreviousSignals`.

---

## Constraints

- Target .NET 10.
- Application-only plumbing. **No formula logic, no formula version change, no DI changes, no domain
  changes.** `PlaceholderScoreFormula` is untouched except that callers now pass the new field.
- Preserve AD-1/AD-2/AD-3 (immutable evidence, in-memory ct non-observance, deterministic ordering).
- Preserve provenance: only current-window `Signals` build contributions/links; `PreviousSignals`
  never does.

---

## Acceptance criteria

- [ ] `ScoringInput` has a fifth `IReadOnlyList<Signal> PreviousSignals` member, documented as
      activity-only / no-provenance.
- [ ] `ScoringEngine` slices the previous window `(start-Window, start]` from the already-fetched
      signals (no extra repository call), applies the `Approved` + deterministic-order rules, loads no
      evidence for it, and passes it into `ScoringInput`.
- [ ] A signal exactly at `windowStartUtc` is classified into the previous window.
- [ ] Tests prove correct current/previous slicing, the boundary rule, the review filter on the
      previous window, no-evidence-required for previous signals, and an empty previous window.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release` are green.

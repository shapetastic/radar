# Task: Scoring Engine — Windowing, Snapshot Construction, Traceability, Persistence

## Overview

Complete the Stage 6 (Scoring) scaffolding by adding the `IScoringEngine` that turns reviewed signals
into a persisted, fully-traceable `CompanyScoreSnapshot`. The engine is **deterministic
infrastructure**: it selects a recent-signal window via the repositories, loads the evidence behind
each signal, delegates the actual scoring to the `IScoreFormula` seam from task 14, maps the result
onto the domain `CompanyScoreSnapshot`, builds a `ScoreEvidenceLink` for every contributing signal,
and persists everything via `IScoreRepository`.

> **HUMAN-OWNED BOUNDARY.** This task contains **no scoring formula**. The engine calls
> `IScoreFormula.Compute` and never inspects or hard-codes weights. The provisional
> `PlaceholderScoreFormula` (task 14) is what gets wired up for now; the maintainer swaps in the real
> formula later behind the same interface, with **zero changes** to this engine. Engine tests assert
> windowing, traceability, versioning, range, and reproducibility — **never** specific score values.
> Operational knobs introduced here (the window length, the "only Approved signals" rule) are
> pipeline scaffolding, not formula weights, and are documented as tunable.

This is the last piece needed to satisfy "Companies can be scored from signals" and "Scores can be
traced back to contributing signals and evidence" in the MVP acceptance criteria.

---

## Assignment

Worktree: pending
Dependencies: 14-scoring-contracts-and-formula-seam (uses `IScoreFormula`/`ScoringInput`/
`ScoreComputation`), 03-repository-abstractions-and-inmemory (uses `ISignalRepository`,
`IEvidenceRepository`, `IScoreRepository`)
Conflicts with: any concurrent task editing
`InfrastructureServiceCollectionExtensions.AddRadarApplicationServices` — sequence, do not parallelize
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/
  Scoring/
    ScoringOptions.cs     # NEW: operational options (window length) — NOT formula weights
    IScoringEngine.cs     # NEW
    CompanyScoreResult.cs # NEW: returned snapshot + its evidence links
    ScoringEngine.cs      # NEW: orchestration (windowing, mapping, persistence)

src/Radar.Infrastructure/
  DependencyInjection/
    InfrastructureServiceCollectionExtensions.cs   # MODIFIED: register formula + engine + options

tests/Radar.Application.Tests/
  Scoring/
    ScoringEngineTests.cs   # NEW
```

Namespace: `Radar.Application.Scoring`. The engine depends on `ISignalRepository`,
`IEvidenceRepository`, `IScoreRepository`, `IScoreFormula`, `TimeProvider`, and (per AD-5)
`ILogger<ScoringEngine>`. No concrete provider SDKs — abstractions only.

---

## Implementation details

### Options (operational, not formula)

```csharp
namespace Radar.Application.Scoring;

/// <summary>
/// Operational scoring parameters (NOT the scoring formula). The window length controls which recent
/// signals feed a snapshot; it is a tunable pipeline knob, not a weight.
/// </summary>
public sealed class ScoringOptions
{
    /// <summary>Length of the recent-signal window. Default 30 days per the pipeline spec.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromDays(30);
}
```

### Result record

```csharp
namespace Radar.Application.Scoring;

using Radar.Domain.Scoring;

/// <summary>The persisted snapshot together with the evidence links that trace it to signals/evidence.</summary>
public sealed record CompanyScoreResult(
    CompanyScoreSnapshot Snapshot,
    IReadOnlyList<ScoreEvidenceLink> Links);
```

### Interface

```csharp
namespace Radar.Application.Scoring;

/// <summary>
/// Stage 6 engine: computes and persists a <c>CompanyScoreSnapshot</c> for one company over the
/// recent-signal window ending at <paramref name="windowEndUtc"/>, plus the <c>ScoreEvidenceLink</c>
/// rows tracing it back to the contributing signals and evidence.
/// </summary>
public interface IScoringEngine
{
    Task<CompanyScoreResult> ScoreCompanyAsync(
        Guid companyId, DateTimeOffset windowEndUtc, CancellationToken ct);
}
```

### Engine (`ScoringEngine : IScoringEngine`)

Constructor takes `ISignalRepository`, `IEvidenceRepository`, `IScoreRepository`, `IScoreFormula`,
`ScoringOptions`, `TimeProvider`, and `ILogger<ScoringEngine>`; `ArgumentNullException.ThrowIfNull`
each and store them. Define a versioned engine identity:

```csharp
private const string EngineVersion = "mvp-engine-v1";
```

`ScoreCompanyAsync` logic (deterministic given fixed clock and repository state):

1. `ct.ThrowIfCancellationRequested()`.
2. Compute the window: `windowStartUtc = windowEndUtc - _options.Window`.
3. Load the company's signals via `ISignalRepository.GetByCompanyAsync(companyId, ct)`.
4. **Window + review filter** (document both as scaffolding rules, tunable later, not formula):
   - keep signals whose `ObservedAtUtc` is in `(windowStartUtc, windowEndUtc]` (exclusive start,
     inclusive end — pick this and document it);
   - keep only signals with `ReviewStatus == SignalReviewStatus.Approved` (scoring consumes reviewed
     signals only, per task 11's note).
5. For each kept signal, load its evidence via `IEvidenceRepository.GetByIdAsync(signal.EvidenceId, ct)`.
   If evidence is `null`, **exclude that signal** (provenance cannot be established) and
   `_logger.LogWarning` once per dropped signal. Pair the rest into `ScoringSignal(signal, evidence)`.
   Order the pairs deterministically (e.g. by `Signal.ObservedAtUtc` then `Signal.Id`) so the formula
   input and resulting links are stable.
6. Build `ScoringInput(companyId, windowStartUtc, windowEndUtc, pairs)` and call
   `_formula.Compute(input)` -> `ScoreComputation computation`.
7. Compose the snapshot version recording **both** identities:
   `var scoringVersion = $"{EngineVersion}+{_formula.Version}";` (e.g. `"mvp-engine-v1+placeholder-v0"`).
8. Build the `CompanyScoreSnapshot`:
   - `Id = Guid.NewGuid()`, `CompanyId = companyId`, `ScoringVersion = scoringVersion`,
   - the five component scores copied from `computation.Components`,
   - `Explanation = computation.Explanation`, `ComponentJson = computation.ComponentJson`,
   - `WindowStartUtc = windowStartUtc`, `WindowEndUtc = windowEndUtc`,
   - `CreatedAtUtc = _timeProvider.GetUtcNow()`.
9. Build one `ScoreEvidenceLink` per `computation.Contributions` item:
   - `Id = Guid.NewGuid()`, `ScoreSnapshotId = snapshot.Id`,
   - `SignalId`, `EvidenceId`, `ContributionReason`, `ContributionWeight` copied from the contribution.
10. Persist: `await _scoreRepository.AddSnapshotAsync(snapshot, ct);` then `AddEvidenceLinkAsync` for
    each link (in order).
11. `_logger.LogInformation` a one-line summary (company, signal count, version) and return
    `new CompanyScoreResult(snapshot, links)`.

Notes:
- The engine **never** inspects or computes weights — all scoring math is `_formula`'s job.
- Empty result (no signals survive the filters) still produces a valid snapshot: `_formula.Compute`
  must handle empty input (guaranteed by task 14), so the engine persists an in-range, zero-
  contribution snapshot with no links. Do not special-case it beyond letting the formula run.
- Per AD-2, the in-memory repos ignore `ct`; the engine still threads `ct` through for the real
  (Dapper) repositories later.

### DI

Extend `InfrastructureServiceCollectionExtensions.AddRadarApplicationServices` to also register:

```csharp
services.TryAddSingleton<IScoreFormula, PlaceholderScoreFormula>();
services.TryAddSingleton(new ScoringOptions());
services.AddSingleton<IScoringEngine, ScoringEngine>();
```

Use `TryAddSingleton` for `IScoreFormula` and `ScoringOptions` so the maintainer (or a host) can
pre-register the real formula or custom options before calling this method and have them win. The
`TimeProvider.System` registration already present in that method is reused. Keep
`AddInMemoryRadarPersistence` and `AddLocalFileCollector` untouched. (`PlaceholderScoreFormula` lives
in `Radar.Application.Scoring`, already referenced.)

---

## Tests

`Radar.Application.Tests/Scoring/ScoringEngineTests.cs` (xUnit). Per AD-4 the Application test project
may reference `Radar.Infrastructure`, so seed real in-memory repositories (`InMemorySignalRepository`,
`InMemoryEvidenceRepository`, `InMemoryScoreRepository`). Use the shared `SignalBuilder`/
`EvidenceBuilder`, a fixed `TimeProvider` (constant `GetUtcNow`), and `NullLogger<ScoringEngine>`.

To keep orchestration tests independent of any formula's internals, define a small in-test
`StubScoreFormula : IScoreFormula` that returns a fixed, in-range `ScoreComputation` and echoes one
contribution per input signal (so traceability assertions are exact and decoupled from
`PlaceholderScoreFormula`). **Assert windowing, traceability, versioning, range, and reproducibility —
never specific score numbers.** Cases:

- **Window filter.** Seed signals inside and outside `(windowEnd - Window, windowEnd]`; only in-window
  signals appear among the snapshot's links (via the stub's one-link-per-signal echo).
- **Review filter.** A `Pending`/`NeedsHumanReview`/`Rejected` in-window signal is excluded; only
  `Approved` signals contribute.
- **Missing evidence excluded.** An `Approved`, in-window signal whose `EvidenceId` is not in the
  evidence repo is dropped (no link for it) and the engine still succeeds.
- **Traceability.** For approved, in-window signals with stored evidence, there is exactly one
  `ScoreEvidenceLink` per contribution; each link's `ScoreSnapshotId == snapshot.Id`, and its
  `SignalId`/`EvidenceId` match a seeded signal/evidence pair.
- **Component range.** All five snapshot component scores are within `[0,100]` (true via stub and
  enforced by the formula contract).
- **Versioning.** `snapshot.ScoringVersion` contains both `"mvp-engine-v1"` and the formula's
  `Version` (assert `Contains`, not an exact weight-bearing string).
- **Window + timestamps.** `WindowStartUtc == windowEnd - Window`, `WindowEndUtc == windowEnd`,
  `CreatedAtUtc` equals the fixed clock.
- **Persistence.** After the call, `IScoreRepository.GetSnapshotsForCompanyAsync` returns the snapshot
  and `GetLinksForSnapshotAsync(snapshot.Id)` returns the links.
- **Empty window.** A company with no qualifying signals yields a valid persisted snapshot with
  in-range scores and zero links (engine does not throw).
- **Reproducibility.** Running the engine twice over the same repository state with the same fixed
  clock yields snapshots with equal component scores, equal `ComponentJson`, equal `ScoringVersion`,
  and an equal set of contribution tuples (`SignalId`/`EvidenceId`/`ContributionWeight`/
  `ContributionReason`) — ignoring the freshly-generated snapshot/link `Id`s.

Optionally, one wiring test: build a `ServiceCollection`, call `AddInMemoryRadarPersistence()` +
`AddRadarApplicationServices()`, resolve `IScoringEngine`, and assert it scores a seeded company to an
in-range snapshot using the real `PlaceholderScoreFormula` (still no weight assertions).

---

## Constraints

- Target .NET 10.
- Application engine depends only on abstractions (repository interfaces, `IScoreFormula`,
  `TimeProvider`, `ILogger<T>` per AD-5) — **no provider SDKs**.
- **No scoring formula here.** All scoring math stays behind `IScoreFormula`; the engine only
  orchestrates, maps, traces, and persists. Tests assert no specific score values.
- Preserve provenance and replayability: every snapshot carries its window + version; every
  contribution becomes a `ScoreEvidenceLink` naming both `SignalId` and `EvidenceId`; the engine
  produces new records and never mutates signals or evidence.
- Respect AD-1 (snapshots/links upsert-by-Id) and AD-2 (engine threads `ct`; in-memory repos ignore
  it).
- Reuse existing domain `CompanyScoreSnapshot`/`ScoreEvidenceLink`; add no domain types.
- `AddInMemoryRadarPersistence` and `AddLocalFileCollector` unchanged.

---

## Acceptance criteria

- [ ] `ScoringOptions`, `IScoringEngine`, `CompanyScoreResult`, and `ScoringEngine` exist under
      `Radar.Application.Scoring`, and the engine + `PlaceholderScoreFormula` + `ScoringOptions` are
      registered via `AddRadarApplicationServices` (formula/options via `TryAddSingleton`).
- [ ] The engine windows signals by `ObservedAtUtc`, includes only `Approved` signals, loads each
      signal's evidence, and excludes signals with missing evidence (logged).
- [ ] It delegates all scoring to `IScoreFormula`, maps the result onto a `CompanyScoreSnapshot` with
      `ScoringVersion` recording both engine and formula versions, and builds one `ScoreEvidenceLink`
      per contribution with `ScoreSnapshotId`/`SignalId`/`EvidenceId`.
- [ ] Snapshot and links are persisted via `IScoreRepository` and are retrievable afterward.
- [ ] Empty/no-signal companies still yield a valid in-range snapshot with zero links.
- [ ] Tests cover window filter, review filter, missing-evidence exclusion, traceability, component
      range, version string, window/timestamps, persistence, empty window, and reproducibility — with
      **no** assertions on specific formula weights/scores.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release` are green.

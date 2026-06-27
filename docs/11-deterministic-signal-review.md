# Task: Deterministic Signal Review

## Overview

Add Stage 5 (Signal Review) as deterministic, rules-based checks — the master spec is explicit:
"For MVP, implement deterministic checks first and leave AI reviewer as interface/stub if
necessary." This slice introduces an `ISignalReviewer` and a deterministic implementation that
inspects a `Signal` together with its source `EvidenceItem`, applies a small set of guardrail
checks, and produces a versioned `SignalReview` plus an updated `Signal.ReviewStatus` (and, where
appropriate, a reduced confidence — never an increased one).

This sits between extraction (tasks 09/10) and scoring, so that scoring later consumes only
reviewed signals. It is deterministic and offline. The AI-assisted reviewer described in the schema
(`ReviewSignalsOutput`/`ReviewedSignal`) is explicitly **deferred** — only the interface and the
deterministic rules land here.

Note on product input: the review **thresholds** below are conservative default guardrails, not a
tuned model and not the scoring formula. They are safe for the coder to implement as specified;
they can be tuned later without redesign. This slice deliberately does **not** touch the scoring
weights/formula (Stage 6) or any AI prompt — those remain reserved for human-owned slices.

---

## Assignment

Worktree: pending
Dependencies: 02-domain-models, 09-signal-extraction-contract-and-validation
Conflicts with: 10-deterministic-keyword-signal-extractor (both edit
`InfrastructureServiceCollectionExtensions.cs` -> `AddRadarApplicationServices`) — sequence after
task 10
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/
  SignalReview/
    ISignalReviewer.cs
    SignalReviewOutcome.cs
    DeterministicSignalReviewer.cs

src/Radar.Infrastructure/
  DependencyInjection/
    InfrastructureServiceCollectionExtensions.cs   # MODIFIED: register ISignalReviewer

tests/Radar.Application.Tests/
  SignalReview/
    DeterministicSignalReviewerTests.cs
```

Namespace: `Radar.Application.SignalReview`. Pure/deterministic, so it lives in **Application**.
`Radar.Application` keeps **zero package references**. Reuse the existing domain records
`Radar.Domain.Signals.SignalReview`, `SignalReviewDecision`, and `SignalReviewStatus` — do not add
new domain types.

---

## Implementation details

### Outcome record

```csharp
namespace Radar.Application.SignalReview;

using Radar.Domain.Signals;

public sealed record SignalReviewOutcome(
    Signal ReviewedSignal,
    SignalReview Review);
```

`ReviewedSignal` is the input signal with `ReviewStatus` (and possibly `Confidence`) updated via a
`with` expression. `Review` is the audit record of the decision.

### Interface

```csharp
namespace Radar.Application.SignalReview;

using Radar.Domain.Evidence;
using Radar.Domain.Signals;

public interface ISignalReviewer
{
    Task<SignalReviewOutcome> ReviewAsync(Signal signal, EvidenceItem evidence, CancellationToken ct);
}
```

The reviewer needs the evidence (for source-quality checks), so it takes both. The caller is
responsible for passing the `EvidenceItem` whose `Id == signal.EvidenceId`; the reviewer may add an
issue if they do not match rather than throwing.

### Deterministic reviewer (`DeterministicSignalReviewer : ISignalReviewer`)

Constructor takes `TimeProvider` (for `ReviewedAtUtc`, deterministic in tests) and
`ILogger<DeterministicSignalReviewer>`. Define a versioned reviewer identity:

```csharp
private const string ReviewerName = "deterministic-rules-v1";
```

Conservative default thresholds as documented constants:

- `MinMaterialStrength = 3` — Strength below this is immaterial.
- `MinNovelty = 3` — Novelty below this suggests repeated/recycled PR.
- `MinConfidence = 0.40m` — below this the signal reads as hype, not evidence.
- "Weak source" = `evidence.Quality` is `EvidenceQuality.Unknown` or `EvidenceQuality.Low`.
- `ConfidenceReductionFactor = 0.5m` — multiplier applied when a `ReduceConfidence` decision fires.

`ReviewAsync` logic (deterministic; collect an ordered `List<string> issues` as you go):

1. `ArgumentNullException.ThrowIfNull` on `signal` and `evidence`.
2. If `evidence.Id != signal.EvidenceId`, add issue `"Evidence does not match signal.EvidenceId"`.
3. **Company match reliability:** if `signal.CompanyId is null`, add issue
   `"Unresolved company mention"`.
4. **Materiality:** if `signal.Strength < MinMaterialStrength`, add issue `"Strength below
   materiality threshold"`.
5. **Repeated PR / novelty:** if `signal.Novelty < MinNovelty`, add issue `"Low novelty (possible
   repeated PR)"`.
6. **Hype vs evidence (confidence):** if `signal.Confidence < MinConfidence`, add issue `"Confidence
   below evidence threshold"`.
7. **Weak source:** if the source is weak, add issue `"Weak or unknown source quality"`.

Decide a single `SignalReviewDecision` by precedence (first match wins):

- Unresolved company (step 3) OR evidence mismatch (step 2) -> `EscalateToHuman`.
- Else immaterial strength (step 4) -> `NeedsMoreEvidence`.
- Else any of low novelty / low confidence / weak source -> `ReduceConfidence`.
- Else (no issues) -> `Approve`.

(`Reject` is reserved for hard contract violations; the task-09 mapper already rejects malformed
signals before they reach review, so the deterministic reviewer does not emit `Reject` for the MVP
rule set. Document this.)

Map decision -> `SignalReviewStatus` and compute the reviewed signal:

| Decision          | New ReviewStatus              | Confidence change                              |
|-------------------|-------------------------------|------------------------------------------------|
| Approve           | `Approved`                    | unchanged                                       |
| ReduceConfidence  | `Approved`                    | `Confidence * ConfidenceReductionFactor`, clamped to [0,1], never increased |
| NeedsMoreEvidence | `NeedsHumanReview`            | unchanged                                       |
| EscalateToHuman   | `NeedsHumanReview`            | unchanged                                       |

Build `ReviewedSignal = signal with { ReviewStatus = newStatus, Confidence = adjustedConfidence }`.

Build the `SignalReview` audit record:

- `Id = Guid.NewGuid()`
- `SignalId = signal.Id`
- `ReviewerName = ReviewerName`
- `Decision =` chosen decision
- `Summary =` short deterministic sentence, e.g. `$"{decision}: {issues.Count} issue(s)."` (or
  `"All checks passed."` when none).
- `IssuesJson =` `JsonSerializer.Serialize(issues)` (issues list; `"[]"` when empty). `System.Text.Json`
  is in-framework — do not add a package.
- `ReviewedAtUtc = _timeProvider.GetUtcNow()`.

Return `new SignalReviewOutcome(reviewedSignal, review)`. Honour `ct` with
`ct.ThrowIfCancellationRequested()`; the method is synchronous work returned as a completed task.

### DI

Extend `InfrastructureServiceCollectionExtensions.AddRadarApplicationServices` to also register:

```csharp
services.TryAddSingleton(TimeProvider.System);
services.AddSingleton<ISignalReviewer, DeterministicSignalReviewer>();
```

Use `TryAddSingleton` for `TimeProvider` so it does not double-register if another method already
added it. Keep `AddInMemoryRadarPersistence` and `AddLocalFileCollector` untouched.

---

## Tests

`Radar.Application.Tests/SignalReview/DeterministicSignalReviewerTests.cs` (xUnit). Use a fixed
`TimeProvider` (constant `GetUtcNow`) and `NullLogger`. Construct `Signal` + matching `EvidenceItem`
inline. Cases:

- Clean signal (resolved `CompanyId`, Strength 6, Novelty 6, Confidence 0.8, High-quality evidence)
  -> `Approve`, `ReviewStatus == Approved`, confidence unchanged, `IssuesJson == "[]"`.
- `CompanyId == null` -> `EscalateToHuman`, `ReviewStatus == NeedsHumanReview`.
- Strength below `MinMaterialStrength` (resolved company) -> `NeedsMoreEvidence`,
  `ReviewStatus == NeedsHumanReview`.
- Low confidence (e.g. 0.2) only -> `ReduceConfidence`, `ReviewStatus == Approved`, and reviewed
  confidence is strictly less than input (and within [0,1]).
- Weak source (`EvidenceQuality.Unknown`) only -> `ReduceConfidence`.
- Precedence: an unresolved-company signal that is also low-confidence resolves to `EscalateToHuman`
  (company check wins over confidence).
- `ReviewedAtUtc` equals the fixed clock; `SignalId == signal.Id`; `ReviewerName` is the versioned
  constant; `IssuesJson` round-trips to the expected issue count via `JsonSerializer.Deserialize`.
- Determinism: same inputs produce equal `Decision`, `ReviewStatus`, and adjusted `Confidence`.

---

## Constraints

- Target .NET 10.
- Deterministic, pure-rules reviewer in **Application**; zero package references; BCL only
  (`System.Text.Json`, `TimeProvider` are in-framework).
- No AI reviewer here — only the interface + deterministic rules; the AI-assisted reviewer is
  deferred to a later, human-owned slice.
- Do **not** touch the scoring formula/weights (Stage 6) or any AI prompt.
- Preserve provenance and immutability: the reviewer produces a new `Signal` via `with` and a
  separate `SignalReview` audit record; it never mutates evidence and never raises confidence.
- Versioned identity (`deterministic-rules-v1`) recorded on every review (schema-spec versioning).
- Reuse existing domain `SignalReview`/`SignalReviewDecision`/`SignalReviewStatus`; add no domain
  types.

---

## Acceptance criteria

- [ ] `ISignalReviewer`, `SignalReviewOutcome`, and `DeterministicSignalReviewer` exist under
      `Radar.Application.SignalReview` and the reviewer is registered via `AddRadarApplicationServices`.
- [ ] Deterministic checks cover company-resolution, materiality, novelty/repeated-PR, confidence,
      and source quality, combined by the documented precedence into one `SignalReviewDecision`.
- [ ] Decisions map to the specified `SignalReviewStatus`; `ReduceConfidence` lowers (never raises)
      confidence, clamped to [0,1]; other decisions leave confidence unchanged.
- [ ] Each review yields a versioned `SignalReview` with `SignalId`, `Decision`, `Summary`,
      `IssuesJson`, and `ReviewedAtUtc` from an injected `TimeProvider`.
- [ ] `Radar.Application` has no package references; `AddInMemoryRadarPersistence` and
      `AddLocalFileCollector` are unchanged.
- [ ] Tests cover each decision path, the precedence rule, confidence reduction, determinism, and
      the versioned audit fields.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release` are green.

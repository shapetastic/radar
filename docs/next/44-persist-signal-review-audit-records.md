# Task: Persist signal-review audit records

## Overview

The Stage 5 reviewer (`DeterministicSignalReviewer`) already produces a full, immutable
`SignalReview` audit record for every signal it inspects — `ReviewerName`, `Decision`
(`Approve` / `ReduceConfidence` / `NeedsMoreEvidence` / `EscalateToHuman`), a `Summary`, the
serialized `IssuesJson` (e.g. "Unresolved company mention", "Weak or unknown source quality"),
and `ReviewedAtUtc`. But the pipeline runner **throws this record away**: it stores only the
reviewed signal's adjusted `ReviewStatus`/`Confidence` and discards `outcome.Review`. The runner
even documents this as a known gap:

> "there is currently no `ISignalReviewRepository`, so that audit record is NOT persisted in this
> slice — only the reviewed signal's ReviewStatus/Confidence are."

This is a real **provenance erosion**: Radar can show *that* a signal was approved or flagged but
not *why*, and the reason cannot be traced back from stored data. The schema spec lists
`SignalReview` as a first-class domain record (already present in `Radar.Domain.Signals`) and
`signal_reviews` as a persisted table. The philosophy is explicit: "Never lose the link between raw
evidence, extracted signal, score, and report."

This slice adds a repository for the existing `SignalReview` record (in-memory, AD-8 files-first /
no database), and wires the runner to persist `outcome.Review` immediately after it stores the
reviewed signal. It is a small, mechanical, provenance-closing slice with no new behaviour: the
reviewer, scoring, and report are unchanged. It unblocks slice 45, which surfaces the persisted
review rationale in the weekly report.

---

## Assignment

Worktree: pending
Dependencies: None (the `SignalReview` domain record and `DeterministicSignalReviewer` already
exist; this only persists what is already produced).
Conflicts with: Slice 45 (both touch `InfrastructureServiceCollectionExtensions.cs` DI registration
and both depend on the new `ISignalReviewRepository`). Sequence 44 → 45; do not parallelize.
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/Abstractions/Persistence/ISignalReviewRepository.cs   # NEW
src/Radar.Infrastructure/Persistence/InMemory/InMemorySignalReviewRepository.cs # NEW
src/Radar.Application/Pipeline/RadarPipelineRunner.cs                        # MODIFIED: inject repo + persist outcome.Review
src/Radar.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs # MODIFIED: register the repo

tests/Radar.Infrastructure.Tests/Persistence/InMemorySignalReviewRepositoryTests.cs # NEW
tests/Radar.Application.Tests/Pipeline/RadarPipelineRunnerTests.cs           # MODIFIED: assert reviews persisted
tests/Radar.Infrastructure.Tests/DependencyInjection/InfrastructureServiceCollectionExtensionsTests.cs # MODIFIED if it asserts the resolved graph
```

---

## Implementation details

### `ISignalReviewRepository` (new, Application/Abstractions/Persistence)

Mirror the shape and `<remarks>` of the existing repository interfaces (`ISignalRepository`,
`IReportRepository`). The stored type is the existing `Radar.Domain.Signals.SignalReview`.

```csharp
using Radar.Domain.Signals;

namespace Radar.Application.Abstractions.Persistence;

public interface ISignalReviewRepository
{
    /// <remarks>
    /// Upsert by Id (last-write-wins), per AD-1. Review records carry a fresh Guid Id per review,
    /// so in practice this behaves as append-only — the relational implementation must preserve
    /// these semantics; do not silently switch evidence to upsert or these to insert-only.
    /// </remarks>
    Task AddAsync(SignalReview review, CancellationToken ct);

    Task<SignalReview?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <remarks>Ordered by ReviewedAtUtc ascending, then Id (AD-3).</remarks>
    Task<IReadOnlyList<SignalReview>> GetBySignalAsync(Guid signalId, CancellationToken ct);
}
```

### `InMemorySignalReviewRepository` (new, Infrastructure/Persistence/InMemory)

Copy the established in-memory pattern (`InMemorySignalRepository`): a
`ConcurrentDictionary<Guid, SignalReview>` keyed by `Id`; `AddAsync` is upsert-by-Id;
`GetBySignalAsync` filters by `SignalId` and applies the AD-3 stable order
(`OrderBy(r => r.ReviewedAtUtc).ThenBy(r => r.Id)`). Do **not** observe the `CancellationToken`
(AD-2). No `OrderBy` on raw `.Values` without a tiebreaker.

### `RadarPipelineRunner`

- Add an `ISignalReviewRepository` constructor dependency (with the standard
  `ArgumentNullException.ThrowIfNull` guard and field), placed next to the existing
  `ISignalRepository`.
- In the extract→resolve→review→store loop, immediately after
  `await _signalRepository.AddAsync(outcome.ReviewedSignal, ct)`, add
  `await _signalReviewRepository.AddAsync(outcome.Review, ct).ConfigureAwait(false);`.
- Remove the "no `ISignalReviewRepository` … NOT persisted in this slice" caveat comment and
  replace it with a one-line note that the audit record is now persisted alongside the reviewed
  signal. Provenance: `outcome.Review.SignalId == outcome.ReviewedSignal.Id` already holds (the
  reviewer builds the review from `signal.Id`), so the persisted review traces back to the stored
  signal.
- No other stage, counter, or `RadarPipelineResult` change. Scoring/report behaviour is untouched.

### DI registration

In `AddInMemoryRadarPersistence`, register
`services.AddSingleton<ISignalReviewRepository, InMemorySignalReviewRepository>();` alongside the
other in-memory repositories (singleton so the store persists across runs in one process, matching
the existing repositories).

---

## Tests

### `InMemorySignalReviewRepositoryTests` (new)

- `AddAsync` then `GetByIdAsync` round-trips a stored review.
- `GetBySignalAsync` returns only reviews for that signal id, in `ReviewedAtUtc` then `Id` order
  (insert out of order; assert sorted). Empty result for an unknown signal id.
- `AddAsync` with the same `Id` upserts (last-write-wins), matching AD-1; reviews with distinct ids
  for the same signal all return from `GetBySignalAsync` (append-in-practice).

### `RadarPipelineRunnerTests`

- After a run that produces at least one signal, the new repository contains one `SignalReview`
  per stored signal, and each review's `SignalId` matches a stored signal's `Id` (provenance).
- A run with no extracted signals persists no reviews.
- Existing evidence/signal/scoring/report assertions still pass (inject the new repository into the
  runner; use the real `InMemorySignalReviewRepository` or a small fake).

Update the runner test setup to supply the new dependency. If
`InfrastructureServiceCollectionExtensionsTests` asserts the resolved service graph, add the new
registration to its expectations.

---

## Constraints

- Target .NET 10; C# 14.
- **Preserve provenance** — this slice exists to *stop* losing it. The persisted review must trace
  to its signal (`SignalReview.SignalId`), which traces to its evidence.
- No database, no AI, no provider SDK leakage (AD-8 / AD-5). In-memory persistence only, behind the
  Application interface; the future relational implementation must preserve AD-1 semantics.
- AD-2: in-memory repo does not observe the `CancellationToken`. AD-3: `GetBySignalAsync` returns a
  deterministic order.
- This is observational audit data — it carries no labels, scores, or advice language; the
  output-language hard rule is unaffected.
- Keep the change scoped: do not add a relational store, do not change the reviewer's decisions, do
  not alter scoring or the report in this slice.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Acceptance criteria

- [ ] `ISignalReviewRepository` exists in Application with `AddAsync` / `GetByIdAsync` /
      `GetBySignalAsync`, documented with the AD-1 upsert-by-Id remark.
- [ ] `InMemorySignalReviewRepository` implements it following the established in-memory pattern
      (upsert-by-Id, AD-2 no-ct, AD-3 ordered `GetBySignalAsync`) and is registered in
      `AddInMemoryRadarPersistence`.
- [ ] `RadarPipelineRunner` persists `outcome.Review` for every reviewed signal; the stale
      "not persisted in this slice" caveat is removed.
- [ ] New repository tests and updated runner tests pass; build/test green.

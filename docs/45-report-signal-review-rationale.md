# Task: Surface signal-review rationale in the weekly report

## Overview

The weekly report's **"Signals needing review"** section currently tells Dean *which* signals need
human attention but not *why*. Each line renders `NeedsReviewSignalRef.Summary`, which the
`WeeklyReportBuilder` populates from the signal's **extractor** reason (`s.Reason`, e.g. "Matched
phrase 'partnership'"). That explains how the signal was *found*, not why it was *flagged*. The
actual flag reason — "Unresolved company mention", "Weak or unknown source quality", "Strength
below materiality threshold" — lives in the `SignalReview` audit record, which slice 44 now
persists.

This slice makes the report use that persisted review: for each needs-review signal, the builder
loads its latest `SignalReview` via `ISignalReviewRepository.GetBySignalAsync` and surfaces the
review's decision + summary so the report explains the human-review ask. This directly serves the
MVP question "what should I investigate next?" and the philosophy rule "explain every score / never
lose provenance" — the review reason is now visible and traceable. Deterministic, file-based, no AI.

---

## Assignment

Worktree: pending
Dependencies: **Slice 44** (`ISignalReviewRepository` + persisted `SignalReview` records). Sequence
44 → 45.
Conflicts with: Slice 44 (shared `InfrastructureServiceCollectionExtensions.cs` is not re-touched
here, but both depend on the new repository; the builder change here assumes 44 is merged). Do not
parallelize with 44.
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/Reporting/NeedsReviewSignalRef.cs        # MODIFIED: add a review-reason field
src/Radar.Application/Reporting/WeeklyReportBuilder.cs         # MODIFIED: inject repo, populate review reason
src/Radar.Application/Reporting/MarkdownWeeklyReportRenderer.cs # MODIFIED: render the review reason
tests/Radar.Application.Tests/Reporting/WeeklyReportBuilderTests.cs       # MODIFIED
tests/Radar.Application.Tests/Reporting/MarkdownWeeklyReportRendererTests.cs # MODIFIED
```

---

## Implementation details

### `NeedsReviewSignalRef`

Add a `string ReviewReason` field (keep the existing `Summary`, which stays the extractor reason —
the two are complementary: *what was found* vs *why it was flagged*). Document that `ReviewReason`
is the persisted reviewer's decision + summary, or a stable fallback when no review is stored.

```csharp
public sealed record NeedsReviewSignalRef(
    Guid SignalId,
    Guid EvidenceId,
    string CompanyMention,
    string Summary,        // extractor reason (what was found)
    string ReviewReason);  // reviewer decision + summary (why it was flagged)
```

### `WeeklyReportBuilder`

- Add an `ISignalReviewRepository` constructor dependency (standard
  `ArgumentNullException.ThrowIfNull` guard + field), next to `_signalRepository`.
- In the needs-review projection (currently `.Select(s => new NeedsReviewSignalRef(...))`), for each
  needs-review signal load its reviews via `GetBySignalAsync(s.Id, ct)` and pick the **latest** one
  (the list is AD-3-ordered by `ReviewedAtUtc` then `Id`, so the last element is the most recent).
  Build `ReviewReason` deterministically as `$"{review.Decision}: {review.Summary}"`. If no review
  is stored (e.g. a `Pending` signal that never reached the reviewer), use a stable fallback such as
  `"Pending review"`. Because this is now an `async` per-signal lookup, replace the LINQ `.Select`
  with an explicit ordered loop **after** the existing `OrderByDescending(ObservedAtUtc).ThenBy(Id)`
  / `Take(MaxItems)` so the cap and ordering are unchanged and only the surfaced signals are
  queried.
- Do not change which signals appear, their order, the cap, or any other section. No scoring or
  evidence-ref change.

### `MarkdownWeeklyReportRenderer`

In `AppendSignalsNeedingReview`, append the review reason to each bullet so the line reads, e.g.:

```text
- Rocket Lab Investor News: Matched phrase 'partnership' — EscalateToHuman: Unresolved company mention (signal <id>)
```

Keep the existing `CompanyMention`, `Summary`, and `(signal <id>)`; insert the `ReviewReason`
between the summary and the signal id with a clear separator (e.g. `" — "`). Invariant culture,
`\n` line endings, byte-identical for a given model (this renderer is pure). No new label strings —
`ReviewReason` is descriptive reviewer text, not an action label, so the six-label allow-list is
unaffected; do not route it through the label set.

---

## Tests

### `WeeklyReportBuilderTests`

- A needs-review signal with a stored `SignalReview` (e.g. `EscalateToHuman` + "Unresolved company
  mention") yields a `NeedsReviewSignalRef` whose `ReviewReason` is
  `"EscalateToHuman: Unresolved company mention"` and whose `Summary` is still the extractor reason.
- A needs-review signal with **multiple** stored reviews surfaces the **latest** review's reason
  (AD-3 ordering).
- A needs-review signal with **no** stored review falls back to the stable default ("Pending
  review"). 
- Ordering, the `MaxItems` cap, and the set of surfaced signals are unchanged from the slice-43
  behaviour (regression assertions still pass). Inject the real `InMemorySignalReviewRepository` or
  a small fake.

### `MarkdownWeeklyReportRendererTests`

- The "Signals needing review" bullet contains the `ReviewReason` text in the expected position,
  byte-for-byte, alongside the existing summary and signal id.
- No disallowed label leaks through (existing label-enforcement tests still pass).

Update the builder test setup to supply the new `ISignalReviewRepository` dependency.

---

## Constraints

- Target .NET 10; C# 14.
- **Preserve provenance** — `ReviewReason` is read from the persisted `SignalReview` (slice 44),
  which traces to the signal and its evidence. Use a stable, honest fallback rather than inventing a
  reason when no review exists.
- Deterministic: same model → byte-identical markdown; AD-3 ordering and the existing `MaxItems` cap
  are unchanged.
- Output-language hard rule: `ReviewReason` is descriptive reviewer text, not an action label — it
  must not introduce any of the forbidden advice words and must not be added to the allow-list.
- No database, no AI, no provider SDK leakage (AD-8 / AD-5). Builder + renderer only.
- Keep scoped: do not change scoring, evidence refs, the highest-opportunity/thesis/watch sections,
  or the collection-summary footer.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Acceptance criteria

- [ ] `NeedsReviewSignalRef` carries a `ReviewReason` field populated from the latest persisted
      `SignalReview` (`Decision: Summary`), with a stable fallback when none exists.
- [ ] `WeeklyReportBuilder` loads reviews via `ISignalReviewRepository.GetBySignalAsync` for the
      surfaced needs-review signals only; ordering, cap, and surfaced set are unchanged.
- [ ] `MarkdownWeeklyReportRenderer` renders the review reason in the "Signals needing review"
      bullets, byte-deterministically, with no new label strings.
- [ ] Builder and renderer tests updated; build/test green.

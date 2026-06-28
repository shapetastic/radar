# Task: Order "Needs Review" Signals by Recency Before Capping (fix M1)

## Overview

Cleanup slice from the post–Stage 7 architecture checkpoint (finding **M1**, MEDIUM). The weekly
report's "signals needing review" list is ordered by raw `Id` (a `Guid`) and **then** truncated with
`Take(MaxItems)`. Guid order is stable but semantically arbitrary, so the cap surfaces a random subset
and can **silently hide the most recent** needs-review signals — exactly the wrong behaviour on a
human-attention surface where recency is what matters. It also diverges from the established
signal-ordering convention (`ObservedAtUtc` then `Id`, per AD-3 and `ScoringEngine`).

This is a tiny, behaviour-correcting change: order most-recent-first **before** the cap.

> No new types, no DI changes, no formula/policy changes, no domain changes. Provenance and the
> output-language rules are unaffected.

---

## Assignment

Worktree: pending
Dependencies: 20-weekly-report-builder
Conflicts with: none
Estimated time: ~30 minutes

---

## Project structure changes

```text
src/Radar.Application/
  Reporting/
    WeeklyReportBuilder.cs        # CHANGED: order needs-review signals by recency before Take

tests/Radar.Application.Tests/
  Reporting/
    WeeklyReportBuilderTests.cs   # CHANGED: add a recency-ordering / no-drop-recent test
```

---

## Implementation details

In `WeeklyReportBuilder` (around the "Signals needing review" selection, ~lines 183–193), replace the
`Id`-based ordering with the established **recency-first** key, applied **before** `Take`:

```csharp
// BEFORE
.OrderBy(s => s.Id)
.Take(_options.MaxItems)

// AFTER — most-recent-first, Id as deterministic tiebreaker (AD-3), then cap
.OrderByDescending(s => s.ObservedAtUtc)
.ThenBy(s => s.Id)
.Take(_options.MaxItems)
```

- Keep the existing exclusive-start window filter (`ObservedAtUtc > periodStartUtc`, inclusive end)
  exactly as-is — only the ordering changes.
- Ordering must be deterministic: `ObservedAtUtc` descending with `Id` as the tiebreaker (two signals
  at the same instant resolve by `Id`).
- Do not change the ranking of the main report entries (those are correctly ranked by
  `OpportunityScore` desc then `CompanyId`); this fix is only the needs-review list.

---

## Tests

In `WeeklyReportBuilderTests`, add a case proving the cap keeps the **most recent** needs-review
signals (use the shared `Radar.TestSupport` builders):

- **Recency-first under the cap.** Seed `MaxItems + N` needs-review signals (all `Pending` /
  needs-review status, in-window) with distinct `ObservedAtUtc` values whose recency order is
  deliberately the *opposite* of their `Id` order. Generate the report and assert the surfaced
  needs-review refs are exactly the `MaxItems` most-recent signals, in descending `ObservedAtUtc`
  order (so the test fails under the old `OrderBy(Id)` behaviour).
- **Deterministic tiebreak.** Two needs-review signals sharing the same `ObservedAtUtc` are ordered by
  `Id` (ascending) — assert stable output across two builds.

Keep the existing needs-review tests passing (status filtering, on-boundary exclusion, etc.).

---

## Constraints

- Target .NET 10. Application-only; no new types, no DI/domain/formula/policy changes.
- Preserve AD-3 (deterministic ordering) — the new key is `ObservedAtUtc desc, Id asc`.
- Preserve the `(periodStartUtc, periodEndUtc]` window semantics and all provenance/output-language
  behaviour.

---

## Acceptance criteria

- [ ] `WeeklyReportBuilder` orders needs-review signals `OrderByDescending(ObservedAtUtc).ThenBy(Id)`
      **before** `Take(MaxItems)`.
- [ ] A test proves the cap retains the most-recent needs-review signals (fails under the old
      `OrderBy(Id)` ordering) and that same-instant signals tiebreak deterministically by `Id`.
- [ ] No other ordering (main report ranking) changed; no new types or DI changes.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release` are green.

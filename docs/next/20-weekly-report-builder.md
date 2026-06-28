# Task: Weekly Report Builder — assemble, persist, and trace the Stage 7 report

## Overview

This slice completes Stage 7 (Weekly Report) by adding the orchestration that assembles a complete,
provenance-complete weekly report from stored data and persists it. The `WeeklyReportBuilder` gathers
each company's recent `CompanyScoreSnapshot`, decides its label via `IReportActionPolicy` (spec 18),
loads the evidence behind each snapshot via the stored `ScoreEvidenceLink`s, builds a
`WeeklyReportModel` (spec 19), renders the markdown via `IWeeklyReportRenderer`, and writes a
`RadarReport` plus one `RadarReportItem` per company through `IReportRepository`.

This is **deterministic orchestration**: it reads repositories, applies a windowing/ranking rule, and
delegates all label logic to the policy and all formatting to the renderer. It contains **no scoring
math and no label thresholds**. After this slice the MVP acceptance criteria "Weekly markdown report is
generated" and "Every reported company includes evidence references" are satisfied.

> **PROVENANCE IS SACRED.** Every `RadarReportItem` carries the `ScoreSnapshotId` it was derived from,
> and the rendered markdown lists the evidence behind that snapshot (resolved from stored
> `ScoreEvidenceLink` → `EvidenceItem`). A report item is therefore reproducible from stored data:
> report → snapshot → signals/evidence. The builder never mutates snapshots, signals, or evidence and
> never fabricates evidence.

---

## Assignment

Worktree: pending
Dependencies: 18-weekly-report-action-policy (uses `IReportActionPolicy`), 19-weekly-markdown-report-renderer (uses `IWeeklyReportRenderer` + view models), 15-scoring-engine-windowing-and-persistence (consumes `IScoreRepository` snapshots/links), 03-repository-abstractions-and-inmemory
Conflicts with: 18 and 19 — all three edit `AddRadarApplicationServices`; **sequence 18 → 19 → 20, do not parallelize**
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/
  Reporting/
    WeeklyReportOptions.cs   # NEW: operational knobs (period length, max items) — NOT label thresholds
    WeeklyReportResult.cs    # NEW: persisted report + its items
    IWeeklyReportBuilder.cs  # NEW
    WeeklyReportBuilder.cs   # NEW: orchestration + persistence
  DependencyInjection/ ... (InfrastructureServiceCollectionExtensions)
                           # MODIFIED: register builder + options

tests/Radar.TestSupport/
  CompanyBuilder.cs          # NEW: fluent builder for Company

tests/Radar.Application.Tests/
  Reporting/
    WeeklyReportBuilderTests.cs   # NEW
```

Namespace: `Radar.Application.Reporting`.

---

## Implementation details

### Options (operational, not label thresholds)

```csharp
namespace Radar.Application.Reporting;

/// <summary>
/// Operational weekly-report parameters (NOT label thresholds — those live in IReportActionPolicy).
/// </summary>
public sealed class WeeklyReportOptions
{
    /// <summary>Reporting period length. Default 7 days per the pipeline spec ("weekly").</summary>
    public TimeSpan Period { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Report type label stored on the RadarReport. Default "Weekly".</summary>
    public string ReportType { get; init; } = "Weekly";

    /// <summary>Maximum number of company entries to include (highest opportunity first). Default 25.</summary>
    public int MaxItems { get; init; } = 25;
}
```

### Result and interface

```csharp
namespace Radar.Application.Reporting;

using Radar.Domain.Reports;

/// <summary>The persisted report plus the items that trace it to score snapshots.</summary>
public sealed record WeeklyReportResult(RadarReport Report, IReadOnlyList<RadarReportItem> Items);

/// <summary>
/// Stage 7 builder: assembles and persists a weekly RadarReport for the period ending at
/// <paramref name="periodEndUtc"/>, with one item per surfaced company tracing back to its score
/// snapshot and evidence.
/// </summary>
public interface IWeeklyReportBuilder
{
    Task<WeeklyReportResult> GenerateAsync(DateTimeOffset periodEndUtc, CancellationToken ct);
}
```

### `WeeklyReportBuilder : IWeeklyReportBuilder`

Constructor dependencies (`ArgumentNullException.ThrowIfNull` each): `ICompanyRepository`,
`IScoreRepository`, `IEvidenceRepository`, `ISignalRepository`, `IReportActionPolicy`,
`IWeeklyReportRenderer`, `IReportRepository`, `WeeklyReportOptions`, `TimeProvider`,
`ILogger<WeeklyReportBuilder>`.

`GenerateAsync` logic (deterministic given fixed clock + repository state):

1. `ct.ThrowIfCancellationRequested()`. Compute `periodStartUtc = periodEndUtc - _options.Period`.
2. Load all companies: `ICompanyRepository.GetAllAsync(ct)`.
3. For each company:
   - `snapshots = IScoreRepository.GetSnapshotsForCompanyAsync(companyId, ct)` (already ordered by
     `CreatedAtUtc` per AD-3).
   - **Current** = the latest snapshot with `CreatedAtUtc` in `(periodStartUtc, periodEndUtc]`
     (exclusive start, inclusive end — document this, matching the scoring window convention). If none,
     **skip the company** (nothing scored this period).
   - **Previous** = the latest snapshot with `CreatedAtUtc < Current.CreatedAtUtc` (any time), or
     `null`. Used only to feed the policy.
   - `action = _policy.Decide(new ReportActionContext(current, previous))`.
   - Load evidence refs: `links = IScoreRepository.GetLinksForSnapshotAsync(current.Id, ct)`; for each
     link load `IEvidenceRepository.GetByIdAsync(link.EvidenceId, ct)`. Build a `ReportEvidenceRef`
     (`SourceName`/`SourceUrl`/`Title` from the evidence; `ContributionReason` from the link). If the
     evidence is missing, `LogWarning` and render the ref with the link's reason but a placeholder title
     (`"(evidence unavailable)"`, no url) — do not drop provenance silently. Order refs by
     `link.ContributionWeight` descending, then `SignalId` (deterministic).
   - Collect an interim entry (company + current snapshot + action + evidence refs).
4. **Rank** the interim entries by `Snapshot.OpportunityScore` descending, then `CompanyId` ascending
   (deterministic, AD-3 spirit). Assign `Rank = 1..n`. Take at most `_options.MaxItems`.
5. Build `WeeklyReportEntry` list (name/ticker from `Company`, `ScoreSnapshotId = current.Id`,
   `Rationale = action.Rationale`).
6. **Signals needing review**: `ISignalRepository.GetObservedBetweenAsync(periodStartUtc, periodEndUtc, ct)`,
   keep `ReviewStatus` in { `Pending`, `NeedsHumanReview` }, map to `NeedsReviewSignalRef`
   (`Summary = signal.Reason`), ordered by `SignalId` for determinism, cap at `_options.MaxItems`.
7. `generatedAt = _timeProvider.GetUtcNow()`. Build `WeeklyReportModel` (title e.g.
   `$"Radar Weekly — {periodStartUtc:yyyy-MM-dd} to {periodEndUtc:yyyy-MM-dd}"`, invariant culture).
8. `markdown = _renderer.Render(model)`.
9. Build the domain report:
   - `RadarReport(Id = Guid.NewGuid(), ReportType = _options.ReportType, Title, PeriodStartUtc,
     PeriodEndUtc, MarkdownContent = markdown, CreatedAtUtc = generatedAt)`.
   - one `RadarReportItem` per entry: `Id = Guid.NewGuid()`, `ReportId = report.Id`,
     `CompanyId`, `ScoreSnapshotId = entry.ScoreSnapshotId`, `SuggestedAction = entry.Action`,
     `Summary = entry.Rationale`, `Rank = entry.Rank`.
10. Persist: `await _reportRepository.AddAsync(report, items, ct)`.
11. `LogInformation` a one-line summary (item count, period); return `WeeklyReportResult(report, items)`.

Notes:
- The builder contains **no thresholds and no scoring math** — labels come from `_policy`, layout from
  `_renderer`. The only knobs it owns (period, max items, ordering) are operational and documented.
- Empty period (no company has an in-period snapshot) → a valid persisted `RadarReport` with empty
  items and a markdown that still renders heading + disclaimers. Do not special-case beyond letting the
  renderer run on an empty model.
- Per AD-2 the in-memory repos ignore `ct`; the builder still threads `ct` through for the future Dapper
  repositories.
- `IScoreRepository` has no cross-company query; iterating companies + `GetSnapshotsForCompanyAsync` is
  the MVP approach. Note in a code comment that a future `GetSnapshotsBetween` could optimise this; do
  **not** add a repository method in this slice (keeps the interface/AD surface untouched).

### TestSupport: `CompanyBuilder`

Add a fluent builder mirroring the others, `Build()` returning a `Company` with sensible defaults
(`Status = CompanyStatus.Active`, fixed UTC timestamps, a name + ticker) and `With*` setters for `Id`,
`Name`, `Ticker`, `Status`.

### DI

In `AddRadarApplicationServices` add:

```csharp
services.TryAddSingleton(new WeeklyReportOptions());
services.AddSingleton<IWeeklyReportBuilder, WeeklyReportBuilder>();
```

`TryAddSingleton` for the options so a host can override period/max items. Leave all existing
registrations untouched.

---

## Tests

`WeeklyReportBuilderTests.cs` (xUnit). Per AD-4 the Application test project may reference
`Radar.Infrastructure`; seed real in-memory repositories (`InMemoryCompanyRepository`,
`InMemoryScoreRepository`, `InMemoryEvidenceRepository`, `InMemorySignalRepository`,
`InMemoryReportRepository`). Use `CompanyBuilder`, `ScoreSnapshotBuilder`, `SignalBuilder`,
`EvidenceBuilder`, a fixed `TimeProvider`, the real `WeeklyReportActionPolicyV1` +
`MarkdownWeeklyReportRenderer`, and `NullLogger<WeeklyReportBuilder>`.

- **In-period selection.** A company with a snapshot in `(periodEnd - Period, periodEnd]` is included;
  a company whose only snapshot is before the period is excluded.
- **Current vs previous.** With two in-window-or-earlier snapshots, the latest in-period one is used as
  current and the prior one feeds the policy (assert via the resulting label, e.g. a clear improvement
  yields `ThesisImproving`).
- **Ranking and cap.** Multiple companies are ranked by `OpportunityScore` descending (then CompanyId);
  `Rank` is 1..n; `MaxItems` caps the count.
- **Provenance / traceability.** Each `RadarReportItem.ScoreSnapshotId` equals the company's current
  snapshot id; the rendered markdown contains each surfaced company's evidence `SourceUrl` (link) and
  the `Score snapshot:` id. Seed a snapshot with `ScoreEvidenceLink`s pointing at seeded evidence.
- **Allowed labels only.** Every item's `SuggestedAction` is one of the five allowed values; `Ignore`
  never appears; the markdown contains the three disclaimers.
- **Signals needing review.** A `NeedsHumanReview`/`Pending` signal observed in the period appears in
  the rendered "Signals needing review" section.
- **Persistence.** After the call, `IReportRepository.GetByIdAsync(report.Id)` returns the report and
  `GetItemsAsync(report.Id)` returns the items (ordered by rank).
- **Empty period.** No in-period snapshots → a valid persisted report with zero items whose markdown
  still contains the heading and disclaimers; the call does not throw.
- **Reproducibility.** Running twice over the same repository state with the same fixed clock yields
  equal `MarkdownContent`, equal item count, and equal per-item tuples
  (`CompanyId`/`ScoreSnapshotId`/`SuggestedAction`/`Rank`) — ignoring freshly-generated `Id`s.

Optionally one wiring test: `AddInMemoryRadarPersistence()` + `AddRadarApplicationServices()`, resolve
`IWeeklyReportBuilder`, and generate a report over a seeded company.

---

## Constraints

- Target .NET 10. Application orchestration depends only on abstractions (repository interfaces,
  `IReportActionPolicy`, `IWeeklyReportRenderer`, `TimeProvider`, `ILogger<T>` per AD-5) — **no
  provider SDKs**.
- **No scoring math and no label thresholds here** — labels come from `IReportActionPolicy`, formatting
  from `IWeeklyReportRenderer`. Tests assert windowing/ranking/provenance, never label or score
  thresholds.
- Preserve provenance/replayability: every item names its `ScoreSnapshotId`; the markdown lists the
  evidence behind each snapshot; the builder produces new records and never mutates snapshots, signals,
  or evidence.
- Respect AD-1 (report upsert-by-Id via `IReportRepository.AddAsync`), AD-2 (thread `ct`; in-memory
  repos ignore it), AD-3 (deterministic ordering).
- Reuse existing domain `RadarReport`/`RadarReportItem`/`RadarReportAction`; add no domain types and no
  repository methods. `AddInMemoryRadarPersistence` and `AddLocalFileCollector` unchanged.

---

## Acceptance criteria

- [ ] `WeeklyReportOptions`, `WeeklyReportResult`, `IWeeklyReportBuilder`, and `WeeklyReportBuilder`
      exist under `Radar.Application.Reporting`; the builder + options are registered in
      `AddRadarApplicationServices` (options via `TryAddSingleton`).
- [ ] The builder windows snapshots by `CreatedAtUtc` in `(periodStart, periodEnd]`, picks current +
      previous per company, decides labels via `IReportActionPolicy`, ranks by `OpportunityScore`, and
      caps at `MaxItems`.
- [ ] Each `RadarReportItem` carries its `ScoreSnapshotId`; the rendered markdown lists the evidence
      behind each snapshot (resolved from `ScoreEvidenceLink` → `EvidenceItem`) — provenance is
      reproducible from stored data.
- [ ] Only the five allowed labels appear; the markdown always contains the three disclaimers.
- [ ] Report + items are persisted via `IReportRepository` and retrievable afterward; empty period
      still yields a valid report with heading + disclaimers and zero items.
- [ ] `CompanyBuilder` is added to `Radar.TestSupport`.
- [ ] Tests cover in-period selection, current/previous, ranking + cap, provenance/traceability,
      allowed-labels + disclaimers, needs-review section, persistence, empty period, and
      reproducibility — with **no** label/score-threshold assertions.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release` are green.

# Task: Weekly Markdown Report Renderer (pure) + report view models

## Overview

Stage 7 of the pipeline produces a **markdown weekly report**. This slice adds the pure, deterministic
rendering half: a set of Application-layer view models that carry a fully-assembled, provenance-complete
report, plus an `IWeeklyReportRenderer` and its `MarkdownWeeklyReportRenderer` implementation that turns
that model into markdown.

Keeping rendering separate from data-gathering means the renderer is a pure function
(`WeeklyReportModel → string`) with no repositories, no clock, and no I/O — trivially testable and
fully deterministic. The next slice (20) assembles the model from stored snapshots/signals/evidence and
persists the result.

The renderer is where Radar's **output-language hard rule** is enforced: it emits only the five allowed
labels, includes the required disclaimers (not financial advice / research only / human review
required), and renders evidence as attributed source links so every reported company is reproducible
from stored data.

> **PROVENANCE.** Each rendered company entry must show its score-snapshot id and a list of the
> evidence behind it (source name + title + link, with the contribution reason). The renderer never
> invents data — it formats only what the model carries. Evidence excerpts/titles are rendered as
> attributed quotations (source's words), not Radar's own voice.

---

## Assignment

Worktree: pending
Dependencies: 18-weekly-report-action-policy (uses `RadarReportAction`; sequenced after it on the shared DI file)
Conflicts with: 18-weekly-report-action-policy, 20-weekly-report-builder — all three edit `AddRadarApplicationServices`; **sequence 18 → 19 → 20, do not parallelize**
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/
  Reporting/
    ReportEvidenceRef.cs        # NEW: one evidence reference for display (provenance)
    WeeklyReportEntry.cs        # NEW: one company's report row
    NeedsReviewSignalRef.cs     # NEW: a signal flagged for human review
    WeeklyReportModel.cs        # NEW: the whole report as data
    IWeeklyReportRenderer.cs    # NEW: model -> markdown
    MarkdownWeeklyReportRenderer.cs  # NEW
  DependencyInjection/ ... (InfrastructureServiceCollectionExtensions)
                              # MODIFIED: register IWeeklyReportRenderer

tests/Radar.Application.Tests/
  Reporting/
    MarkdownWeeklyReportRendererTests.cs   # NEW
```

Namespace: `Radar.Application.Reporting`.

---

## Implementation details

### View models

```csharp
namespace Radar.Application.Reporting;

using Radar.Domain.Reports;
using Radar.Domain.Scoring;

/// <summary>One piece of evidence behind a company entry (provenance for display).</summary>
public sealed record ReportEvidenceRef(
    Guid EvidenceId,
    Guid SignalId,
    string SourceName,
    string? SourceUrl,
    string Title,
    string ContributionReason);

/// <summary>A signal surfaced in the "needs review" section.</summary>
public sealed record NeedsReviewSignalRef(
    Guid SignalId,
    Guid EvidenceId,
    string CompanyMention,
    string Summary);

/// <summary>One company's row in the weekly report, carrying its snapshot id and evidence.</summary>
public sealed record WeeklyReportEntry(
    Guid CompanyId,
    string CompanyName,
    string? Ticker,
    Guid ScoreSnapshotId,
    CompanyScoreSnapshot Snapshot,
    RadarReportAction Action,
    string Rationale,
    int Rank,
    IReadOnlyList<ReportEvidenceRef> Evidence);

/// <summary>The complete weekly report as data; the renderer formats it deterministically.</summary>
public sealed record WeeklyReportModel(
    string Title,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<WeeklyReportEntry> Entries,
    IReadOnlyList<NeedsReviewSignalRef> SignalsNeedingReview);
```

### Renderer seam

```csharp
namespace Radar.Application.Reporting;

public interface IWeeklyReportRenderer
{
    /// <summary>Renders the model to deterministic markdown. Pure: no clock, no I/O.</summary>
    string Render(WeeklyReportModel model);
}
```

### `MarkdownWeeklyReportRenderer : IWeeklyReportRenderer`

Pure/deterministic. `ArgumentNullException.ThrowIfNull(model)`. Build the markdown with a
`StringBuilder`; use `\n` line endings and `CultureInfo.InvariantCulture` for all number/date
formatting so output is byte-stable across machines.

Allowed-label guard (enforce the hard rule):

```csharp
private static readonly IReadOnlySet<RadarReportAction> Allowed = new HashSet<RadarReportAction>
{
    RadarReportAction.Investigate,
    RadarReportAction.Watch,
    RadarReportAction.NeedsMoreEvidence,
    RadarReportAction.ThesisImproving,
    RadarReportAction.ThesisDeteriorating,
};
```

If any entry's `Action` is not in `Allowed` (e.g. `Ignore`), throw `InvalidOperationException` — the
weekly report must never surface a disallowed label. Map enum → display string:
`Investigate`, `Watch`, `Needs more evidence`, `Thesis improving`, `Thesis deteriorating`.

Document structure (fixed, deterministic order):

1. **Heading + period.** `# {Title}` then a line with `Period: {PeriodStartUtc:yyyy-MM-dd} → {PeriodEndUtc:yyyy-MM-dd} (UTC)` and `Generated: {GeneratedAtUtc:yyyy-MM-dd HH:mm}Z`.
2. **Disclaimer block** (required, verbatim, as a markdown blockquote):
   - `Not financial advice.`
   - `For research only.`
   - `Human review required.`
3. **Highest opportunity** — all entries, already ordered by the model (assume input order = rank
   order; do not re-sort, but you may assert `Rank` is ascending). For each entry render a subsection:
   - `## {Rank}. {CompanyName}{ (TICKER) if present }` then a bullet line `Label: {display label}`,
     a line `Opportunity {Opp} · Trajectory {Traj} · Attention {Att} · Evidence {EC} · Velocity {Vel}`
     (from `Snapshot`), a line `Why: {Rationale}`, and a line `Score snapshot: {ScoreSnapshotId}`.
   - **Evidence** sub-list: for each `ReportEvidenceRef`, a bullet:
     `- [{Title}]({SourceUrl}) — {SourceName}: {ContributionReason}` when `SourceUrl` is non-empty,
     otherwise `- {Title} — {SourceName}: {ContributionReason}` (no broken link). If an entry has no
     evidence, render `- (no linked evidence)`.
4. **Thesis improving** / **Thesis deteriorating** — short lists naming companies whose `Action` is the
   corresponding label (name + ticker + rank reference). Omit the section header if empty.
5. **Signals needing review** — from `model.SignalsNeedingReview`: a bullet per signal
   `- {CompanyMention}: {Summary} (signal {SignalId})`. Omit the section if empty.

The renderer's own generated scaffolding (headers, labels, disclaimers, connective text) must contain
none of: `buy`, `sell`, `guaranteed`, `safe bet`. Evidence titles/reasons are model-supplied
quotations and are rendered as-is (attributed to their source) — they are provenance, not Radar's
recommendation.

### DI

In `AddRadarApplicationServices` add:

```csharp
services.TryAddSingleton<IWeeklyReportRenderer, MarkdownWeeklyReportRenderer>();
```

Leave all existing registrations untouched.

---

## Tests

`MarkdownWeeklyReportRendererTests.cs` (xUnit). Construct `WeeklyReportModel` instances **directly**
(inline records; use `ScoreSnapshotBuilder` from spec 18 for the `Snapshot`). No repositories.

- **Disclaimers present.** Output contains all three required disclaimer lines.
- **Heading and period.** Output contains the title and the formatted UTC period.
- **All allowed labels render** with their display strings (`Needs more evidence`, `Thesis improving`,
  etc.).
- **Disallowed label rejected.** An entry with `RadarReportAction.Ignore` causes `Render` to throw
  `InvalidOperationException`.
- **Evidence links and provenance.** An entry with evidence renders a markdown link
  `[Title](url)` with the source name and contribution reason, and the entry shows its
  `Score snapshot: {id}`. An evidence ref with null/empty `SourceUrl` renders no broken `()` link.
- **No-evidence entry.** Renders the `(no linked evidence)` placeholder rather than an empty bullet.
- **Needs-review section.** Present when the list is non-empty (lists the signal), omitted when empty.
- **Empty report.** A model with no entries and no review signals still renders the heading and
  disclaimers and does not throw.
- **No advice language in scaffolding.** The rendered output, excluding model-supplied
  titles/reasons/summaries, contains none of the banned phrases (assert the disclaimer/label/header
  text is clean; a simple approach: assert the generated section headers and label strings exactly).
- **Determinism.** Rendering the same model twice yields byte-identical strings.

---

## Constraints

- Target .NET 10. Application-only, **pure** (no repositories, no clock, no I/O), BCL only
  (`System.Text`, `System.Globalization`). Deterministic: invariant culture, `\n` endings.
- Enforce the output-language hard rule: only the five allowed labels; required disclaimers always
  present; no advice words in generated scaffolding.
- Preserve provenance: every entry renders its score-snapshot id and its evidence references with
  links where available.
- Reuse domain `RadarReportAction`/`CompanyScoreSnapshot`; add no domain types. View models live in
  `Radar.Application.Reporting`.
- Respect AD-5 and centralised DI; register via `TryAddSingleton`.

---

## Acceptance criteria

- [ ] `ReportEvidenceRef`, `NeedsReviewSignalRef`, `WeeklyReportEntry`, `WeeklyReportModel`,
      `IWeeklyReportRenderer`, and `MarkdownWeeklyReportRenderer` exist under
      `Radar.Application.Reporting`.
- [ ] The renderer is pure and deterministic (invariant culture, stable ordering, `\n` endings).
- [ ] Output always contains the three disclaimers; only the five allowed labels render; a disallowed
      label (e.g. `Ignore`) throws.
- [ ] Each entry renders its `ScoreSnapshotId` and attributed evidence links (provenance).
- [ ] Registered via `TryAddSingleton` in `AddRadarApplicationServices`; existing registrations
      unchanged.
- [ ] Tests cover disclaimers, heading/period, all labels, disallowed-label rejection, evidence
      links + provenance, no-evidence placeholder, needs-review section, empty report, advice-free
      scaffolding, and determinism.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release` are green.

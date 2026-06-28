# Task: Weekly Report Action Policy (seam + deterministic `WeeklyReportActionPolicyV1`)

## Overview

Stage 7 (Weekly Report) turns persisted `CompanyScoreSnapshot`s into a human-readable report. Before
the report can be assembled, Radar must decide, for each company, which **allowed label** to surface:
`Investigate`, `Watch`, `Needs more evidence`, `Thesis improving`, `Thesis deteriorating`. That
mapping — score thresholds → label — is a **product decision**, the most advice-adjacent output Radar
produces, and must live behind its own visible, versioned, owned seam (exactly as scoring weights live
behind `IScoreFormula`), never baked into report-assembly orchestration.

This slice adds the `IReportActionPolicy` seam and a deterministic first implementation,
`WeeklyReportActionPolicyV1`. It maps a company's current score snapshot (and its immediately-prior
snapshot, for improving/deteriorating) onto one of the five allowed labels plus a plain-English
rationale. It is pure, deterministic, BCL-only, and emits **only** the five allowed labels — never
`Ignore`, never financial-advice language.

> **HUMAN-OWNED / OUTPUT-LABEL BOUNDARY.** The thresholds below are a product decision. They are
> written out exactly as named `private const` fields; the coder **transcribes them faithfully and
> invents nothing** (same rule as `RadarScoreFormulaV1` in spec 17). The policy is registered via
> `TryAddSingleton` so the maintainer can swap a revised policy without touching the report builder.
> To change thresholds, bump `Version` and the constants — existing reports remain explainable.

This slice also adds a `ScoreSnapshotBuilder` to `Radar.TestSupport` (first needed here; reused by the
later Stage 7 slices).

---

## Assignment

Worktree: pending
Dependencies: 17-radar-score-formula-v1 (consumes the produced `CompanyScoreSnapshot`), 13-shared-test-data-builders (extends `Radar.TestSupport`)
Conflicts with: 19-weekly-markdown-report-renderer, 20-weekly-report-builder — all three edit `AddRadarApplicationServices`; **sequence 18 → 19 → 20, do not parallelize**
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/
  Reporting/
    IReportActionPolicy.cs        # NEW: the seam
    ReportActionContext.cs        # NEW: input (current + optional previous snapshot)
    ReportActionResult.cs         # NEW: chosen action + rationale
    WeeklyReportActionPolicyV1.cs # NEW: deterministic v1
  DependencyInjection/ ... (InfrastructureServiceCollectionExtensions)
                                  # MODIFIED: TryAddSingleton<IReportActionPolicy, WeeklyReportActionPolicyV1>

tests/Radar.TestSupport/
  ScoreSnapshotBuilder.cs         # NEW: fluent builder for CompanyScoreSnapshot

tests/Radar.Application.Tests/
  Reporting/
    WeeklyReportActionPolicyV1Tests.cs  # NEW
```

Namespace: `Radar.Application.Reporting`.

---

## Implementation details

### Records and seam

```csharp
namespace Radar.Application.Reporting;

using Radar.Domain.Reports;
using Radar.Domain.Scoring;

/// <summary>
/// Inputs for deciding a company's weekly report label. <paramref name="Current"/> is the snapshot for
/// the reporting period; <paramref name="Previous"/> is the immediately-preceding snapshot for the same
/// company (null if none), used to detect an improving or deteriorating thesis.
/// </summary>
public sealed record ReportActionContext(
    CompanyScoreSnapshot Current,
    CompanyScoreSnapshot? Previous);

/// <summary>A chosen allowed label plus a deterministic, advice-free rationale.</summary>
public sealed record ReportActionResult(RadarReportAction Action, string Rationale);

/// <summary>
/// Maps a company's score snapshot onto one of the five ALLOWED weekly-report labels. The mapping is a
/// versioned product decision; implementations must emit only Investigate / Watch / NeedsMoreEvidence /
/// ThesisImproving / ThesisDeteriorating and must never produce financial-advice language.
/// </summary>
public interface IReportActionPolicy
{
    string Version { get; }
    ReportActionResult Decide(ReportActionContext context);
}
```

### `WeeklyReportActionPolicyV1 : IReportActionPolicy`

`Version => "weekly-report-action-v1"`. `Decide` is pure/deterministic: no clock, no randomness, no
I/O. `ArgumentNullException.ThrowIfNull(context)` and `ThrowIfNull(context.Current)`.

Named constants (put these at the top, exactly — do not retune):

```csharp
// Evidence-confidence floor: below this, the thesis is "needs more evidence" regardless of score.
private const int EvidenceConfidenceFloor = 35;
// Trajectory midpoint (50 = neutral, matches radar-formula-v1).
private const int NeutralTrajectory = 50;
// Minimum trajectory change vs the previous snapshot to call a thesis improving/deteriorating.
private const int ThesisDelta = 5;
// Opportunity thresholds for the steady-state labels.
private const int InvestigateOpportunity = 60;
private const int WatchOpportunity = 40;
```

Decision rules — **evaluate in this exact order, first match wins** (document the precedence in a code
comment):

1. **Thin evidence overrides everything.** If `Current.EvidenceConfidenceScore < EvidenceConfidenceFloor`
   → `NeedsMoreEvidence`. Rationale:
   `$"Evidence confidence {Current.EvidenceConfidenceScore} is below {EvidenceConfidenceFloor}; needs more evidence."`
2. **Deterioration (surfaced before opportunity, to stay honest).** If `Previous is not null` and
   `delta = Current.TrajectoryScore - Previous.TrajectoryScore <= -ThesisDelta` → `ThesisDeteriorating`.
   Rationale: `$"Trajectory fell {Previous.TrajectoryScore}→{Current.TrajectoryScore} ({delta}) versus the prior snapshot."`
3. **Improvement.** If `Previous is not null` and `delta >= ThesisDelta` **and**
   `Current.TrajectoryScore >= NeutralTrajectory` → `ThesisImproving`. Rationale:
   `$"Trajectory rose {Previous.TrajectoryScore}→{Current.TrajectoryScore} (+{delta}) versus the prior snapshot."`
4. **Steady-state by opportunity.**
   - `Current.OpportunityScore >= InvestigateOpportunity` → `Investigate`. Rationale:
     `$"Opportunity {Current.OpportunityScore} (>= {InvestigateOpportunity}); worth investigating."`
   - else `Current.OpportunityScore >= WatchOpportunity` → `Watch`. Rationale:
     `$"Opportunity {Current.OpportunityScore} (>= {WatchOpportunity}); watch for further signals."`
   - else → `NeedsMoreEvidence`. Rationale:
     `$"Opportunity {Current.OpportunityScore} below {WatchOpportunity}; needs more evidence."`

The policy **never** returns `RadarReportAction.Ignore` and never emits the words buy/sell/guaranteed/
safe bet. Rationales reference only the score numbers and the documented thresholds.

### DI

In `AddRadarApplicationServices` add (after the scoring registrations):

```csharp
services.TryAddSingleton<IReportActionPolicy, WeeklyReportActionPolicyV1>();
```

`TryAddSingleton` so a maintainer can pre-register a revised policy and have it win. Leave all existing
registrations untouched.

### TestSupport: `ScoreSnapshotBuilder`

Add a fluent builder mirroring `SignalBuilder`/`EvidenceBuilder` (sensible defaults, `With*` setters,
`Build()` returning a `CompanyScoreSnapshot`). Defaults: new `Id`/`CompanyId`,
`ScoringVersion = "radar-formula-v1"`, the five component scores = `50`, `Explanation = "test"`,
`ComponentJson = "{}"`, a 30-day UTC window, fixed `CreatedAtUtc`. Provide `WithCompanyId`,
`WithTrajectoryScore`, `WithOpportunityScore`, `WithAttentionScore`, `WithEvidenceConfidenceScore`,
`WithSignalVelocityScore`, `WithScoringVersion`, `WithWindow(start,end)`, `WithCreatedAtUtc`.

---

## Tests

`WeeklyReportActionPolicyV1Tests.cs` (xUnit), using `ScoreSnapshotBuilder`:

- **Version.** `Version == "weekly-report-action-v1"`.
- **Allowed labels only.** Across a representative matrix of snapshots, every returned `Action` is one
  of the five allowed values; `Ignore` is never returned.
- **Thin evidence overrides opportunity.** A snapshot with high `OpportunityScore` but
  `EvidenceConfidenceScore` below the floor → `NeedsMoreEvidence`.
- **Investigate / Watch boundaries.** Opportunity at/above `InvestigateOpportunity` → `Investigate`;
  between `WatchOpportunity` and `InvestigateOpportunity` → `Watch`; below `WatchOpportunity` →
  `NeedsMoreEvidence` (with sufficient evidence confidence).
- **Improving / deteriorating via previous.** Previous trajectory low, current higher by `>= ThesisDelta`
  (current `>= NeutralTrajectory`) → `ThesisImproving`; current lower by `>= ThesisDelta` →
  `ThesisDeteriorating`; a sub-threshold change does not trigger either.
- **No previous snapshot.** `Previous == null` → never improving/deteriorating; falls through to the
  opportunity/evidence rules.
- **Rationale content.** Rationale is non-empty, contains the relevant score number(s), and contains
  none of: `buy`, `sell`, `guaranteed`, `safe bet` (case-insensitive).
- **Determinism.** Two `Decide` calls on the same context return equal results.
- **Null guards.** `Decide(null!)` and a context with a null `Current` throw `ArgumentNullException`.

---

## Constraints

- Target .NET 10. Application-only, pure/deterministic, BCL only.
- **Transcribe the specified thresholds exactly** — every constant as named. No retuning. If a rule is
  ambiguous or appears wrong, stop and flag rather than guessing.
- Emit only the five allowed `RadarReportAction` values; never `Ignore`; never advice language
  (hard rule in CLAUDE.md).
- Reuse domain `CompanyScoreSnapshot`/`RadarReportAction`; add no domain types.
- Respect AD-5 (Application may use `Microsoft.Extensions.*` abstractions only; no provider SDKs) and
  centralised DI; register via `TryAddSingleton`.

---

## Acceptance criteria

- [ ] `IReportActionPolicy`, `ReportActionContext`, `ReportActionResult`, and
      `WeeklyReportActionPolicyV1` exist under `Radar.Application.Reporting`.
- [ ] `WeeklyReportActionPolicyV1.Version == "weekly-report-action-v1"`, all thresholds are named
      `private const` fields, and decision precedence matches the spec exactly.
- [ ] The policy emits only the five allowed labels (never `Ignore`) and advice-free rationales.
- [ ] Registered via `TryAddSingleton` in `AddRadarApplicationServices`; existing registrations
      unchanged.
- [ ] `ScoreSnapshotBuilder` is added to `Radar.TestSupport`.
- [ ] Tests cover version, allowed-labels, evidence-floor override, opportunity boundaries,
      improving/deteriorating, no-previous, rationale safety, determinism, and null guards.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release` are green.

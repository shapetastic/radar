# Task: Pipeline Runner — orchestrate Stages 1–7 end-to-end behind one Application service

## Overview

Every pipeline stage now exists behind an Application interface and is unit-tested, but **nothing wires
them together**: there is no code that collects evidence, persists it, extracts/resolves/reviews
signals, scores companies, and builds the report as a single run. This slice adds that missing glue —
`RadarPipelineRunner` in the Application layer — so Radar can actually run end-to-end.

The runner is **provider-independent deterministic orchestration**. It depends only on existing
Application interfaces (`IEvidenceCollector`, `IEvidenceRepository`, `ISignalExtractor`,
`ICompanyResolver`, `ISignalReviewer`, `ISignalRepository`, `ICompanyRepository`, `IScoringEngine`,
`IWeeklyReportBuilder`), the static `ExtractedSignalMapper`, an injected `TimeProvider`, and
`ILogger<T>` (AD-5). It contains **no scoring math, no label thresholds, no resolution/extraction
logic** — every stage's behaviour stays behind its own interface. The runner only sequences the stages
and threads provenance through them.

This slice deliberately does **not** seed the company watch-universe (that is spec 23) and does **not**
touch the Worker host (that is spec 24). It runs against whatever company universe already exists in
`ICompanyRepository`; with an empty universe every mention resolves to *unresolved* and the run still
completes (signals land as `NeedsHumanReview`, the report is valid but empty) — that is correct,
conservative behaviour, not an error.

> **PROVENANCE IS SACRED.** The runner never fabricates or mutates evidence. Each signal keeps the
> `EvidenceId` the mapper assigned from its source evidence; resolution only *adds* a `CompanyId`;
> review may only lower confidence; scoring builds `ScoreEvidenceLink`s from current-window signals; the
> report references score snapshots and their evidence. The chain evidence → signal → score → report is
> never broken by the runner.

---

## Assignment

Worktree: pending
Dependencies: 05-local-file-evidence-collector (`IEvidenceCollector`), 06/08-company-alias-resolver
(`ICompanyResolver`), 10-deterministic-keyword-signal-extractor (`ISignalExtractor` +
`ExtractedSignalMapper`), 11-deterministic-signal-review (`ISignalReviewer`),
15/16/17-scoring (`IScoringEngine`), 20-weekly-report-builder (`IWeeklyReportBuilder`),
03-repository-abstractions-and-inmemory
Conflicts with: 23-company-watch-universe-seed (both edit `InfrastructureServiceCollectionExtensions`);
**sequence 22 → 23, do not parallelize**
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/
  Pipeline/
    PipelineOptions.cs        # NEW: operational knobs (whether to build the report)
    RadarPipelineResult.cs    # NEW: typed run summary (counts + optional report id)
    IRadarPipeline.cs         # NEW
    RadarPipelineRunner.cs    # NEW: stage orchestration

src/Radar.Infrastructure/
  DependencyInjection/
    InfrastructureServiceCollectionExtensions.cs   # MODIFIED: add AddRadarPipeline()

tests/Radar.Application.Tests/
  Pipeline/
    RadarPipelineRunnerTests.cs   # NEW
```

Namespace: `Radar.Application.Pipeline`.

---

## Implementation details

### Options (operational, not formula/label knobs)

```csharp
namespace Radar.Application.Pipeline;

/// <summary>
/// Operational pipeline parameters (NOT scoring weights or label thresholds). The scoring window and
/// report period live on ScoringOptions / WeeklyReportOptions respectively; this only toggles whether a
/// run finishes by building the Stage 7 report.
/// </summary>
public sealed class PipelineOptions
{
    /// <summary>When true (default) the run ends by generating the weekly report (Stage 7).</summary>
    public bool GenerateReport { get; init; } = true;
}
```

### Result and interface

```csharp
namespace Radar.Application.Pipeline;

/// <summary>
/// Deterministic summary of one pipeline run. Counts are observational only — provenance lives in the
/// persisted evidence/signals/snapshots/report, not here.
/// </summary>
public sealed record RadarPipelineResult(
    int EvidenceCollected,
    int EvidenceNew,
    int SignalsExtracted,
    int SignalsValid,
    int SignalsApproved,
    int SignalsNeedingReview,
    int CompaniesScored,
    Guid? ReportId);

/// <summary>
/// Runs the Radar pipeline once: collect → store evidence → extract → resolve → review → store signals
/// → score companies → (optionally) build the weekly report. Provider-independent; deterministic given
/// a fixed clock and fixed repository/source state.
/// </summary>
public interface IRadarPipeline
{
    Task<RadarPipelineResult> RunAsync(CancellationToken ct);
}
```

### `RadarPipelineRunner : IRadarPipeline`

Constructor dependencies (`ArgumentNullException.ThrowIfNull` each): `IEvidenceCollector`,
`IEvidenceRepository`, `ISignalExtractor`, `ICompanyResolver`, `ISignalReviewer`, `ISignalRepository`,
`ICompanyRepository`, `IScoringEngine`, `IWeeklyReportBuilder`, `PipelineOptions`, `TimeProvider`,
`ILogger<RadarPipelineRunner>`.

`RunAsync` logic (deterministic given a fixed clock + fixed source/repository state):

1. `ct.ThrowIfCancellationRequested()`. Take a **single** `asOfUtc = _timeProvider.GetUtcNow()` and use
   the same instant for the mapper's `createdAtUtc`, the scoring `windowEndUtc`, and the report
   `periodEndUtc`, so the whole run is internally consistent. `TimeProvider.GetUtcNow()` already returns
   a zero-offset `DateTimeOffset` (the report builder requires zero offset — do not reconstruct it).

2. **Stage 1 + 2 (collect + dedupe-store).** `collected = await _collector.CollectAsync(ct)` (the
   collector already normalized text and computed the content hash). For each item, call
   `await _evidenceRepository.AddIfNewAsync(item, ct)`; keep the items where it returned `true` in a
   `newEvidence` list. **Only newly-stored evidence is extracted** — re-collected duplicates must not
   produce duplicate signals. Iterate `collected` in the order returned (the local-file collector is
   already filename-ordered → deterministic).

3. **Stage 4 + 3 + 5 (extract → resolve → review → store), per new evidence, in order.**
   For each `evidence` in `newEvidence`:
   - `output = await _extractor.ExtractAsync(evidence, ct)`.
   - For each `extracted` in `output.Signals` (in order):
     - `mapping = ExtractedSignalMapper.ToSignal(extracted, evidence, asOfUtc)`. If `!mapping.IsValid`,
       `LogDebug` the join of `mapping.Errors` and **skip** (count toward `SignalsExtracted` but not
       `SignalsValid`). The mapper owns the provenance check (excerpt must be found in the evidence) and
       validation — the runner does not re-validate.
     - `var signal = mapping.Signal!;` (`SignalsValid++`).
     - **Resolve:** `resolution = await _resolver.ResolveAsync(signal.CompanyMention, ct)`. If
       `resolution.CompanyId is { } companyId`, `signal = signal with { CompanyId = companyId };`. If
       unresolved, leave `CompanyId == null` (the reviewer will route it to human review) — never guess.
     - **Review:** `outcome = await _reviewer.ReviewAsync(signal, evidence, ct)`.
     - **Store:** `await _signalRepository.AddAsync(outcome.ReviewedSignal, ct)`.
     - Tally by `outcome.ReviewedSignal.ReviewStatus`: `Approved → SignalsApproved++`;
       `NeedsHumanReview` (or `Pending`) → `SignalsNeedingReview++`.
   - Note (do **not** add a repository in this slice): the reviewer also returns an audit
     `SignalReview` record. There is currently **no `ISignalReviewRepository`**, so that audit record is
     not persisted — only the reviewed signal's `ReviewStatus`/`Confidence` are. Record this as a known
     MVP gap in an XML/code comment; persisting the audit trail is a future slice, not this one.

4. **Stage 6 (score every company at `asOfUtc`).**
   `companies = await _companyRepository.GetAllAsync(ct)` (already AD-3 ordered). For each company,
   `await _scoringEngine.ScoreCompanyAsync(company.Id, asOfUtc, ct)` and `CompaniesScored++`. The engine
   itself applies the window/Approved-only filter and writes the snapshot + links; the runner does not
   pre-filter which companies have signals (scoring a company with no in-window signals yields a valid
   neutral snapshot — that is the engine's contract, not the runner's concern).

5. **Stage 7 (optional report).** If `_options.GenerateReport`,
   `report = await _reportBuilder.GenerateAsync(asOfUtc, ct)` and set `reportId = report.Report.Id`;
   otherwise `reportId = null`.

6. `LogInformation` a one-line summary (evidence new/collected, signals approved/needs-review,
   companies scored, report id-or-"none"); return the populated `RadarPipelineResult`.

Notes:
- Per AD-2 the in-memory repos ignore `ct`; the runner still threads `ct` through every call and checks
  it at the top for the future Dapper repositories.
- The runner adds **no** new domain types, **no** new repository methods, and **no** scoring/label
  logic. It is pure sequencing.

### DI

Add a new extension method to `InfrastructureServiceCollectionExtensions` (do not fold this into
`AddRadarApplicationServices`, because the pipeline also needs `IEvidenceCollector`, which is registered
separately by `AddLocalFileCollector`):

```csharp
/// <summary>
/// Registers the end-to-end pipeline runner. Requires the persistence registration
/// (<see cref="AddInMemoryRadarPersistence"/>), the application services
/// (<see cref="AddRadarApplicationServices"/>), and an evidence collector
/// (e.g. <see cref="AddLocalFileCollector"/>) to also be registered.
/// </summary>
public static IServiceCollection AddRadarPipeline(this IServiceCollection services)
{
    services.TryAddSingleton(new PipelineOptions());
    services.AddSingleton<IRadarPipeline, RadarPipelineRunner>();
    return services;
}
```

`TryAddSingleton` for `PipelineOptions` so a host (spec 24) can override `GenerateReport`. Leave every
existing registration untouched.

---

## Tests

`RadarPipelineRunnerTests.cs` (xUnit). Per AD-4 the Application test project may reference
`Radar.Infrastructure`; seed real in-memory repositories (`InMemoryEvidenceRepository`,
`InMemoryCompanyRepository`, `InMemorySignalRepository`, `InMemoryScoreRepository`,
`InMemoryReportRepository`). Use the **real** `CompanyResolver`, `DeterministicSignalReviewer`,
`ScoringEngine` (with `RadarScoreFormulaV1` + `ScoringOptions`), and `WeeklyReportBuilder`
(with `WeeklyReportActionPolicyV1` + `MarkdownWeeklyReportRenderer`), a fixed
`TimeProvider` (`FakeTimeProvider` or a small stub), and `NullLogger<T>`. Use `CompanyBuilder` /
`EvidenceBuilder` from `Radar.TestSupport`.

For the evidence collector and signal extractor, prefer **in-test fakes** so the cases stay
deterministic and decoupled from the keyword rules:
- a fake `IEvidenceCollector` returning a fixed `IReadOnlyList<EvidenceItem>` (built via
  `EvidenceBuilder`), and
- a fake `ISignalExtractor` returning a fixed `ExtractSignalsOutput` whose `SupportingExcerpt` is a
  substring of the matching evidence's `RawText` (so the mapper's provenance check passes).
(At least one case may instead use the real `KeywordSignalExtractor` for a true integration smoke test.)

Cases:
- **Happy path / full chain.** Seed one company whose `Name` exactly matches the extracted signal's
  `CompanyMention`; the fake extractor emits one material signal (strength ≥ 3, novelty ≥ 3,
  confidence ≥ 0.4) with a valid excerpt. After `RunAsync`: the evidence is persisted; exactly one
  signal is persisted with `CompanyId == company.Id` and `ReviewStatus == Approved`; a
  `CompanyScoreSnapshot` exists for the company; with `GenerateReport = true` the report is persisted
  and contains the company as a ranked entry. **Provenance:** the report item's `ScoreSnapshotId`
  equals the snapshot id and a `ScoreEvidenceLink` for that snapshot points at the persisted evidence.
- **Unresolved mention stays conservative.** With an empty company universe (or a non-matching mention),
  the persisted signal has `CompanyId == null` and `ReviewStatus == NeedsHumanReview`; it is counted in
  `SignalsNeedingReview`; it never becomes an Approved scoring contribution.
- **Dedup / no double-extract.** Running `RunAsync` twice over the same fake collector output leaves the
  evidence count unchanged on the second run (`AddIfNewAsync` returns false) and produces **no
  additional signals** from that evidence (assert signal count stable). (A second scoring snapshot per
  company is expected and fine.)
- **Invalid extracted signal is dropped.** An `ExtractedSignal` with an unknown type or an excerpt not
  present in the evidence is counted in `SignalsExtracted` but not `SignalsValid`, and no signal is
  persisted for it.
- **GenerateReport = false.** `ReportId` is null and `IReportRepository` has no report afterward.
- **Injected clock is honoured.** The persisted snapshot's `CreatedAtUtc` and (when generated) the
  report's `CreatedAtUtc` equal the fixed `TimeProvider` instant — no `DateTimeOffset.UtcNow` leaks.
- **Determinism.** Two runs over identical fresh state with the same fixed clock return equal
  `RadarPipelineResult` counts.

Optionally one wiring test: compose `AddInMemoryRadarPersistence()` + `AddRadarApplicationServices()` +
`AddLocalFileCollector(tempDir)` + `AddRadarPipeline()`, resolve `IRadarPipeline`, and run it over a
temp directory containing one evidence JSON file plus a seeded company.

---

## Constraints

- Target .NET 10. The runner depends only on Application abstractions + `ExtractedSignalMapper` +
  `TimeProvider` + `ILogger<T>` (AD-5) — **no provider SDKs**, no `Microsoft.AspNetCore.*`/hosting
  packages (those belong to the Worker, spec 24).
- **No scoring math, no label thresholds, no resolution/extraction logic in the runner** — it only
  sequences stages. Tests assert orchestration/provenance/determinism, never score or label thresholds.
- Preserve provenance/replayability: a signal keeps its `EvidenceId`; resolution only adds `CompanyId`;
  review only lowers confidence; scoring links trace to evidence; the report references snapshots. The
  runner mutates nothing it did not create.
- Respect AD-1 (evidence insert-only via `AddIfNewAsync`; signals/scores/report upsert-by-Id), AD-2
  (thread `ct`; in-memory repos ignore it), AD-3 (rely on the repositories' deterministic ordering).
- Add **no** new domain types and **no** new repository methods. `AddRadarApplicationServices`,
  `AddInMemoryRadarPersistence`, and `AddLocalFileCollector` are unchanged.
- Use the established central DI convention: registration lives in
  `InfrastructureServiceCollectionExtensions`.

---

## Acceptance criteria

- [ ] `PipelineOptions`, `RadarPipelineResult`, `IRadarPipeline`, and `RadarPipelineRunner` exist under
      `Radar.Application.Pipeline`; `AddRadarPipeline()` registers the runner (options via
      `TryAddSingleton`).
- [ ] `RunAsync` collects evidence, stores only new evidence via `AddIfNewAsync`, extracts signals from
      new evidence, resolves each mention (adding `CompanyId` only when matched), reviews, persists the
      reviewed signal, scores every company at the run instant, and optionally builds the report.
- [ ] A single run instant from the injected `TimeProvider` feeds the mapper, scoring window end, and
      report period end; no `DateTimeOffset.UtcNow` is used.
- [ ] Unresolved mentions stay `CompanyId == null` / `NeedsHumanReview`; re-collected evidence does not
      produce duplicate signals; invalid extracted signals are dropped (not persisted).
- [ ] Provenance is intact end-to-end: report item → score snapshot → score-evidence link → persisted
      evidence, with the same evidence the signal referenced.
- [ ] Tests cover the full happy-path chain, unresolved mention, dedup/no-double-extract, invalid-signal
      drop, `GenerateReport = false`, injected-clock, and determinism — with no score/label-threshold
      assertions.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` are green.

# Task: Add a "Collection summary" section to the weekly report

## Overview

The weekly markdown report is the MVP user interface, but it currently shows **only interpreted output**
(opportunity entries, thesis roll-ups, watch, ignore, signals needing review). It gives Dean no
indication of **collection coverage or health**: a thin report could mean a genuinely quiet week or it
could mean half the feeds were unreadable that run. Without that context the report is harder to trust —
exactly the trade the master spec warns against (surface evidence transparently; do not hide why output
is sparse).

Slices 41–42 made collection health a structured `CollectionSummary` carried on the pipeline run. This
slice threads that summary into report generation and has `MarkdownWeeklyReportRenderer` emit a small,
deterministic **`## Collection summary`** footer: how many sources Radar checked this run, how many were
unreadable, and (when any failed) a list of the failed sources with their reason. It is a transparency
footer only — observational metadata, no scoring, no labels, no advice language.

---

## Assignment

Worktree: pending
Dependencies: **Slice 42** (provides `CollectionSummary` and the runner holding it). Sequence 42 → 43.
Conflicts with: Slice 42 (shared file: `RadarPipelineRunner.cs`). Sequence; do not parallelize.
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/Reporting/WeeklyReportModel.cs                 # MODIFIED: add optional Collection summary
src/Radar.Application/Reporting/IWeeklyReportBuilder.cs              # MODIFIED: GenerateAsync takes the summary
src/Radar.Application/Reporting/WeeklyReportBuilder.cs               # MODIFIED: set model.Collection
src/Radar.Application/Reporting/MarkdownWeeklyReportRenderer.cs      # MODIFIED: render the footer
src/Radar.Application/Pipeline/RadarPipelineRunner.cs               # MODIFIED: pass run summary to the builder

tests/Radar.Application.Tests/Reporting/WeeklyReportBuilderTests.cs            # MODIFIED
tests/Radar.Application.Tests/Reporting/MarkdownWeeklyReportRendererTests.cs   # MODIFIED/extended
tests/Radar.Application.Tests/Pipeline/RadarPipelineRunnerTests.cs            # MODIFIED if it asserts builder args
```

No domain changes, no DI changes, no new files required.

---

## Implementation details

### Pass the summary into report generation

`IWeeklyReportBuilder.GenerateAsync` currently takes `(DateTimeOffset periodEndUtc, CancellationToken)`.
Add the run's collection summary:

```csharp
Task<WeeklyReportResult> GenerateAsync(
    DateTimeOffset periodEndUtc, CollectionSummary collection, CancellationToken ct);
```

`RadarPipelineRunner` already holds `collected.Summary` (slice 42) — pass it when it calls
`_reportBuilder.GenerateAsync(asOfUtc, collectionSummary, ct)`. The builder reads scores/signals from
repositories exactly as today and simply attaches the summary to the model.

### Carry it on the model

Add `CollectionSummary? Collection` to `WeeklyReportModel` (nullable so existing tests that build a model
directly without a summary still compile and render — a `null` summary renders no footer). The builder
sets it from the passed-in summary.

### Render the footer

In `MarkdownWeeklyReportRenderer.Render`, after the existing sections (it is a footer — place it **last**,
after `## Signals needing review`), emit when `model.Collection` is non-null:

```text
## Collection summary

Radar checked {SourcesChecked} source(s) this run; {SourcesFailed} could not be read.
```

When `SourcesFailed > 0`, follow with one bullet per `SourceFailure` in the summary's (already
deterministic) order:

```text
- {SourceName}: {Reason}
```

Include `SourceUrl` in the bullet only when present. Render the summary even when `SourcesFailed == 0`
(the "all sources read" line is itself the trust signal). Omit the whole section only when
`model.Collection` is null.

### Keep invariants intact

- The output-language gate and the snapshot-id/company-id consistency checks run first and are
  unchanged; the footer adds **no** labels and no advice language.
- Determinism unchanged: same model → byte-identical markdown; invariant culture; `\n` endings;
  model-supplied ordering (the failure list is already deterministically ordered upstream).
- No clock, no I/O, no repositories in the renderer.

### Scope guard

Do not restructure existing sections, change ranking/labels, or alter scoring or the action policy. This
slice only adds the footer and the plumbing to feed it.

---

## Tests

### `MarkdownWeeklyReportRendererTests` (extended)

- **All sources read:** a model whose `Collection` reports `SourcesFailed == 0` renders a
  `## Collection summary` section with the "checked N … 0 could not be read" line and **no** failure
  bullets.
- **Some sources failed:** a `Collection` with two `SourceFailure`s renders the count line plus one
  bullet per failure, in summary order, each showing the reason (and URL when present).
- **Omitted when null:** a model with `Collection == null` renders **no** `## Collection summary` header
  (back-compat for direct-model tests).
- **Footer placement:** `## Collection summary` appears **after** `## Signals needing review`.
- **Determinism:** rendering the same model twice yields byte-identical output.

### `WeeklyReportBuilderTests`

- The builder attaches the passed-in `CollectionSummary` to `model.Collection` unchanged; existing
  entry/score/period assertions still hold. Add the new `GenerateAsync` argument to existing calls
  (pass `CollectionSummary.Empty` where the test doesn't care).

### `RadarPipelineRunnerTests`

- The runner passes its collection summary into `GenerateAsync` (assert via a fake/recording builder, or
  via the rendered report if the integration path is exercised).

---

## Constraints

- Target .NET 10; C# 14.
- Pure, deterministic renderer: no clock, no I/O, no repositories; invariant culture, `\n` endings.
- The footer is observational transparency metadata: no AD-9 labels, no advice language; the existing
  output-language and provenance gates are unchanged.
- Keep changes scoped to report generation + the runner's builder call. Do not touch scoring, the action
  policy, collectors, DI, the domain, or add AI/DB.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Acceptance criteria

- [ ] `IWeeklyReportBuilder.GenerateAsync` accepts the run's `CollectionSummary`; the builder attaches it
      to `WeeklyReportModel.Collection`; the runner passes its `collected.Summary`.
- [ ] The renderer emits a `## Collection summary` footer (checked/failed line, plus one bullet per
      failure in summary order when any failed), placed after `## Signals needing review`, and omits the
      section entirely when `Collection` is null.
- [ ] No advice language or labels in the footer; all existing renderer invariants (label gate,
      snapshot/company-id checks, determinism, ordering) are preserved.
- [ ] Renderer, builder, and runner tests cover all-read, some-failed, null-omitted, placement, and
      determinism; build/test green.

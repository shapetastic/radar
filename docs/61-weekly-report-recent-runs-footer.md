# Task: Weekly report — a "Recent runs" footer from the persisted run history

## Overview

Spec 59 persists a `PipelineRunRecord` per run under `data/runs/`. This slice surfaces that history in the
weekly report as a compact **"Recent runs"** footer: the last few runs with their date, collectors, new
evidence, approved signals, and companies scored. It turns the run log into something the maintainer actually
reads, and answers "has anything moved over the last few runs?" at a glance — the observability payoff of the
run history. It complements spec 60's per-company deltas (which show movement for surfaced companies) with a
run-level pipeline overview.

Scope is **reading the recent run records and rendering a footer section** — deterministic, presentation-only.
It does NOT change scoring, extraction, resolution, the run store, or which companies surface.

---

## Assignment

Worktree: any
Dependencies: 59 (run store / `IPipelineRunStore.ReadRecentAsync`) merged. Best sequenced after 60 (both edit
the renderer + report model) to avoid conflicts.
Conflicts with: 60 (both touch `MarkdownWeeklyReportRenderer` + `WeeklyReportModel`/builder — sequence 60 →
61). Depends on 59.
Estimated time: ~1–1.5 h

---

## Project structure changes

```text
src/Radar.Application/Reporting/
  WeeklyReportModel.cs                 # MODIFIED: carry an optional recent-runs list (nullable, back-compat like Collection)
  RecentRunSummary.cs                  # NEW: the presentation projection of one PipelineRunRecord for the footer
  WeeklyReportBuilder.cs               # MODIFIED: read recent runs via IPipelineRunStore, project into the model
  MarkdownWeeklyReportRenderer.cs      # MODIFIED: append a "## Recent runs" section (omitted when absent/empty)

tests/Radar.Application.Tests/Reporting/
  MarkdownWeeklyReportRendererTests.cs # MODIFIED: footer renders deterministically; omitted when empty/null
  WeeklyReportBuilderTests.cs          # MODIFIED: recent runs flow from the store into the model
```

---

## Implementation details

### Projection (`RecentRunSummary`)
- A small immutable `sealed record` the renderer consumes, projected from `PipelineRunRecord`:
  `DateTimeOffset CreatedAtUtc`, `IReadOnlyList<string> Collectors`, `int EvidenceNew`, `int SignalsApproved`,
  `int CompaniesScored`, `int SourcesChecked`, `int SourcesFailed`. (Keep it minimal — the footer is a
  glance, not the full record; the full record stays on disk.)
- Keep it in `Radar.Application/Reporting`.

### Builder wiring
- Inject `IPipelineRunStore` into `WeeklyReportBuilder` (constructor + `ArgumentNullException.ThrowIfNull`).
- Add a bounded count to `WeeklyReportOptions` (e.g. `int RecentRunsInReport { get; init; } = 5`) so the
  footer size is configurable and defaulted; surface it from `RadarWorkerOptions` if trivial, else default it.
- In `GenerateAsync`, call `await _runStore.ReadRecentAsync(options.RecentRunsInReport, ct)` and project the
  records into `RecentRunSummary` (newest-first, as the store returns them). Put the list on
  `WeeklyReportModel` as a **nullable** property (mirror how `Collection` is optional so direct-model callers
  and existing tests stay valid). A read failure/empty history → null-or-empty (section omitted), never a
  throw.
- Note the ordering nuance: the run currently being generated is written by the runner **after** the report is
  built (spec 59 writes at the end of `RunAsync`), so this footer shows the **prior** runs — that is correct
  and worth a one-line comment. Do not try to include the in-flight run.

### Renderer
- Add `AppendRecentRuns(sb, model)` called near the end of `Render` (after the collection summary, before
  returning), mirroring the omit-when-null pattern of `AppendCollectionSummary`:
  - Header `## Recent runs`.
  - One bullet per run, newest-first, invariant-culture, ASCII-only, e.g.:
    `- 2026-06-28 14:00Z — collectors: rss, sec — new evidence 12 · approved 7 · companies 6 · sources 14/1 failed`.
  - Omit the whole section when the model carries no recent runs (null or empty).
- Renderer stays pure/deterministic (no clock, no I/O); same model → byte-identical markdown. No advice
  language, no labels — this is observational metadata like the collection summary.

---

## Tests

- `MarkdownWeeklyReportRendererTests`: a model with two `RecentRunSummary` rows renders a `## Recent runs`
  section with both bullets newest-first in the exact format; a model with null/empty recent runs omits the
  section entirely (assert the header string is absent). Deterministic exact-string assertions.
- `WeeklyReportBuilderTests`: with a fake `IPipelineRunStore` returning N records, the built model's recent-run
  list matches (projected, newest-first, capped at `RecentRunsInReport`); an empty store → no footer; a store
  read that yields empty degrades cleanly. Existing report tests stay green (constructor gains the dependency —
  update setup/fakes).

---

## Constraints

- Target `net10.0`. Presentation/observability only — no scoring, extraction, resolution, or run-store change.
  Renderer pure/deterministic; invariant culture, `\n`, ASCII only (no emoji).
- The footer shows prior runs (the in-flight run is persisted after the report is built — do not include it).
  A history read failure never aborts the report.
- No advice language, no labels in the footer. Provenance and the six AD-9 labels unchanged.
- Scope: model + projection + builder read + renderer footer + tests. `dotnet build`/`dotnet test` on
  `Radar.sln -c Release` green.

---

## Acceptance criteria

- [ ] `WeeklyReportBuilder` reads the last `RecentRunsInReport` records via `IPipelineRunStore.ReadRecentAsync`
      and projects them into a nullable recent-runs list on `WeeklyReportModel`; a read failure/empty history
      degrades to an omitted section, never a throw.
- [ ] The renderer emits a deterministic, invariant-culture, ASCII-only `## Recent runs` footer (newest-first)
      and omits it entirely when there are no recent runs.
- [ ] The footer shows prior runs only (the in-flight run is written after the report is built); no
      scoring/extraction/run-store change; renderer stays pure.
- [ ] Tests cover footer rendering, omit-when-empty, and the store→model flow (capped, newest-first). No advice
      language. `build`/`test` green.

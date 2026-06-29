# Task: Add a "Watch" roll-up section to the weekly report

## Overview

The master spec's weekly-report layout has four distinct sections: detailed lead entries, **Watch**,
**Ignore / Low signal**, and **Signals needing review**. `MarkdownWeeklyReportRenderer` today renders a
detailed "## Highest opportunity" block for **every** entry (regardless of label), then named roll-up
sections for `Thesis improving`, `Thesis deteriorating`, and `Ignore / Low signal`, then
`Signals needing review`. There is a roll-up for `Ignore` but **no equivalent for `Watch`** — so a
`Watch`-labelled company appears only buried inside the detailed lead block, with no at-a-glance
"what's on the watch list" section the master layout calls for.

This slice adds a `## Watch` roll-up section, mirroring the existing `Ignore / Low signal` roll-up
exactly in shape (the renderer already has a shared `AppendNamedActionSection` helper for precisely
this). It is a small, pure renderer change that brings the report in line with the master layout and
makes the watch list scannable, without altering scoring, labelling, or provenance.

---

## Assignment

Worktree: any
Dependencies: None. (Reads better once slices 38–39 clean and enrich evidence text, but shares no files
with them and can ship independently.)
Conflicts with: None. Edits only `MarkdownWeeklyReportRenderer.cs` and its tests.
Estimated time: ~1 hour

---

## Project structure changes

```text
src/Radar.Application/Reporting/MarkdownWeeklyReportRenderer.cs                  # MODIFIED: add Watch roll-up

tests/Radar.Application.Tests/Reporting/MarkdownWeeklyReportRendererTests.cs     # MODIFIED/extended
```

No new files, no interface changes, no DI changes, no domain changes. `RadarReportAction.Watch` and
its `DisplayLabels`/`Allowed` entries already exist.

---

## Implementation details

### Add the Watch section

In `Render`, add one call to the existing `AppendNamedActionSection` helper for the `Watch` action.
Place it to match the master layout's ordering — the watch list belongs after the detailed lead block
and the thesis roll-ups, and before `Ignore / Low signal`:

```text
AppendHighestOpportunity(...)            // detailed entries (unchanged)
AppendThesisSection(... ThesisImproving ...)
AppendThesisSection(... ThesisDeteriorating ...)
AppendNamedActionSection(sb, model, RadarReportAction.Watch, "Watch")   // NEW
AppendNamedActionSection(sb, model, RadarReportAction.Ignore, "Ignore / Low signal")
AppendSignalsNeedingReview(...)
```

`AppendNamedActionSection` already: filters `model.Entries` by action in model order, omits the section
entirely when no entry matches, emits `## <header>` then one `- CompanyName (Ticker) (#rank)` line per
match. Reuse it verbatim — do **not** duplicate the formatting logic.

### Keep invariants intact

- The output-language gate (only the six AD-9 labels render; snapshot-id/company-id consistency checks)
  is unchanged and still runs first.
- Determinism is unchanged: same model → byte-identical markdown, invariant culture, `\n` endings,
  model-supplied ordering.
- A `Watch` entry still also appears in the detailed "## Highest opportunity" block (every entry does);
  the new section is an additional at-a-glance roll-up, consistent with how `Ignore` already behaves.
- No clock, no I/O, no repositories in the renderer.

### Scope guard

Do not restructure the existing "Highest opportunity" block, rename existing sections, change ranking,
or alter the builder/policy. This slice only inserts the missing `Watch` roll-up.

---

## Tests

### `MarkdownWeeklyReportRendererTests` (extended)

- **Watch section present:** a model containing at least one `Watch`-labelled entry renders a `## Watch`
  section listing that company as `- Name (TICKER) (#rank)` in model order.
- **Omitted when empty:** a model with no `Watch` entries renders **no** `## Watch` header (section
  omitted, mirroring the Ignore-section behaviour).
- **Ordering:** when both `Watch` and `Ignore` entries exist, `## Watch` appears **before**
  `## Ignore / Low signal` and after the thesis sections.
- **Roll-up + detail coexist:** a `Watch` entry appears both in the detailed "## Highest opportunity"
  block and in the new `## Watch` roll-up (regression on the detailed block).
- **Determinism:** rendering the same model twice yields byte-identical output (extend/keep existing
  determinism assertion).

---

## Constraints

- Target .NET 10; C# 14.
- Pure, deterministic renderer: no clock, no I/O, no repositories; invariant culture, `\n` endings.
- Only the six AD-9 labels may render; the existing output-language and provenance gates are unchanged.
- Reuse `AppendNamedActionSection`; do not duplicate section-formatting logic.
- Keep changes scoped to the renderer and its tests. Do not touch the builder, action policy, scoring,
  or DI. Do not add AI.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Acceptance criteria

- [ ] The renderer emits a `## Watch` roll-up (one `- Name (Ticker) (#rank)` line per `Watch` entry, in
      model order) using the shared `AppendNamedActionSection` helper.
- [ ] The `## Watch` section is omitted when no entry is labelled `Watch`.
- [ ] `## Watch` is ordered after the thesis sections and before `## Ignore / Low signal`.
- [ ] All existing invariants (label gate, snapshot/company-id checks, detailed block, determinism) are
      preserved.
- [ ] New tests cover presence, omission, ordering, roll-up/detail coexistence, and determinism;
      build/test green.

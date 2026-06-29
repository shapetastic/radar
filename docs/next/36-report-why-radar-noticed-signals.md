# Task: Surface "Why Radar noticed" signal types in the weekly report

## Overview

The weekly report is the MVP user interface, and the master spec's report example puts a
**"Why Radar noticed"** block under each surfaced company — a short list of the contributing signal
*types* and what each was about:

```markdown
Why Radar noticed:
- CustomerWin: Multi-launch agreement announced.
- GovernmentContract: NASA-related contract evidence found.
```

Today `MarkdownWeeklyReportRenderer` emits a single policy `Why:` rationale line plus raw evidence
links. The contributing signal *types* — the most legible "what changed" summary — are not surfaced.
With slices 34–35 producing richer, more varied signals, this slice turns that richer output into a
clearer report by listing the contributing signals (type + direction + reason) behind each company,
each carrying its signal id for provenance.

This is a report-content slice only: no scoring math, no label thresholds, no resolution changes. It
reads provenance that is already persisted — every `CompanyScoreSnapshot` already has
`ScoreEvidenceLink`s that name the contributing `SignalId`s.

---

## Assignment

Worktree: any
Dependencies: None technically (independent files); benefits from 34–35's richer signals
Conflicts with: None (does not touch the extractor; edits Reporting only)
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/Reporting/ReportSignalRef.cs        # NEW: one contributing signal (provenance)
src/Radar.Application/Reporting/WeeklyReportEntry.cs       # MODIFIED: add Signals collection
src/Radar.Application/Reporting/WeeklyReportBuilder.cs     # MODIFIED: load contributing signals
src/Radar.Application/Reporting/MarkdownWeeklyReportRenderer.cs   # MODIFIED: render "Why noticed"

tests/Radar.Application.Tests/Reporting/WeeklyReportBuilderTests.cs              # MODIFIED/extended
tests/Radar.Application.Tests/Reporting/MarkdownWeeklyReportRendererTests.cs     # MODIFIED/extended
```

No domain or persistence changes; no new repository methods (use existing
`ISignalRepository.GetByIdAsync`).

---

## Implementation details

### New `ReportSignalRef` (Application/Reporting)

```csharp
namespace Radar.Application.Reporting;

using Radar.Domain.Signals;

/// <summary>One contributing signal behind a company entry (provenance for the "why noticed" block).</summary>
public sealed record ReportSignalRef(
    Guid SignalId,
    SignalType Type,
    SignalDirection Direction,
    string Reason);
```

### `WeeklyReportEntry` (MODIFIED)

Add `IReadOnlyList<ReportSignalRef> Signals` to the record. Keep `Evidence` as-is (the evidence-link
display is unchanged). Update the single construction site in `WeeklyReportBuilder`.

### `WeeklyReportBuilder` (MODIFIED)

In the per-entry build (where `BuildEvidenceRefsAsync` is already called), build the contributing
signals from the **same** score-evidence links so the two displays stay consistent:

- From the snapshot's `ScoreEvidenceLink`s, take the **distinct** `SignalId`s.
- Load each via `_signalRepository.GetByIdAsync(signalId, ct)`.
- For a signal that loads, emit `ReportSignalRef(s.Id, s.Type, s.Direction, s.Reason)`. If a signal id
  is missing (should not happen — provenance), **do not drop it silently**: log a warning (mirroring
  the existing missing-evidence handling) and skip it; the evidence-link block still carries the id.
- Deterministic order: by `Type` (enum order), then `Direction`, then `SignalId` (AD-3 spirit). Order
  before emitting.
- This is one extra `GetByIdAsync` per distinct contributing signal for entries that survive
  ranking/capping only (built inside the same post-`Take` loop) — acceptable for the in-memory MVP.

### `MarkdownWeeklyReportRenderer` (MODIFIED)

In `AppendEntry`, after the existing `- Why:` rationale line and before (or after) the evidence list,
add a **"Why noticed"** block when `entry.Signals` is non-empty:

```text
- Why noticed:
  - CustomerWin (Positive): Matched phrase 'multi-year deal'.
  - GovernmentContract (Positive): Matched phrase 'nasa'.
```

- One bullet per `ReportSignalRef`, in the model-supplied order (renderer stays a pure formatter — no
  re-sorting).
- Format: `  - {Type} ({Direction}): {Reason}`. Trim the reason; if empty, render the type/direction
  only.
- When `entry.Signals` is empty, omit the "Why noticed" block entirely (mirrors the evidence block's
  empty handling, but no placeholder line).
- All existing invariants stay: only the six AD-9 labels render, disclaimers always present, snapshot
  id and evidence links unchanged, `\n` line endings, invariant culture, byte-identical for a given
  model.

The output-language hard rule is unaffected: signal *type* names (CustomerWin, GovernmentContract, …)
and directions (Positive/Negative/Neutral/Mixed) are not advice labels.

---

## Tests

### `WeeklyReportBuilderTests` (extended)
- A company with score-evidence links pointing at stored signals produces an entry whose `Signals`
  contains one `ReportSignalRef` per **distinct** contributing signal, with correct `Type`/`Direction`/
  `Reason`, ordered by type then direction then id.
- Duplicate `SignalId`s across links collapse to one `ReportSignalRef`.
- A link whose signal is missing logs a warning and is skipped without throwing; other signals still
  surface.
- A company with no contributing signals yields an empty `Signals` list.

### `MarkdownWeeklyReportRendererTests` (extended)
- An entry with signals renders a `- Why noticed:` block with one `  - {Type} ({Direction}): {Reason}`
  bullet per signal, in model order.
- An entry with no signals renders no "Why noticed" block.
- Regression: existing heading/disclaimer/label/evidence/snapshot assertions still hold; output is
  byte-identical for a fixed model; a disallowed label still throws.

---

## Constraints

- Target .NET 10; C# 14.
- Preserve provenance: every "why noticed" line traces to a stored signal id behind the cited score
  snapshot; nothing is fabricated, nothing is dropped silently.
- Renderer stays a pure, deterministic formatter (no clock, no I/O, no re-ordering).
- Output-language hard rule intact (only the six AD-9 labels as actions; signal types are not advice).
- Keep changes scoped to Reporting; no scoring, resolution, extraction, or persistence changes.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Acceptance criteria

- [ ] `ReportSignalRef` exists and `WeeklyReportEntry` carries an ordered, de-duplicated `Signals` list
      built from the snapshot's score-evidence links.
- [ ] The renderer emits a "Why noticed" block listing contributing signal type, direction, and reason;
      omitted when there are no signals.
- [ ] A missing contributing signal is logged and skipped (never dropped silently, never throws).
- [ ] Existing report invariants (labels, disclaimers, snapshot id, evidence links, determinism) all
      hold.
- [ ] Tests cover builder signal-ref assembly (incl. dedupe + missing signal) and renderer output;
      build/test green.

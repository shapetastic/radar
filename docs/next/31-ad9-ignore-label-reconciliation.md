# Task: AD-9 label reconciliation — admit `Ignore` in renderer + policy

## Overview

AD-9 widened the allowed report labels to the **union of six**: `Investigate`, `Watch`, `Ignore`,
`Needs more evidence`, `Thesis improving`, `Thesis deteriorating`. CLAUDE.md and
`radar-philosophy.md` are already updated, but the **code is not**:

- `MarkdownWeeklyReportRenderer` still has a five-item `Allowed` set (no `Ignore`) and **throws** if an
  entry's action is `Ignore`.
- `WeeklyReportActionPolicyV1` never emits `Ignore` (its XML doc explicitly says so) — low-signal
  companies currently fall to `NeedsMoreEvidence`.

This slice updates both so low-signal companies can be labelled `Ignore`, matching the master spec's
"Ignore / Low Signal" report section. The advice-language ban is unchanged (no buy/sell/price-target).

---

## Assignment

Worktree: any
Dependencies: 18-weekly-report-action-policy, 19-weekly-markdown-report-renderer (existing trunk)
Conflicts with: None — touches only the renderer + policy (and their tests). **May run in parallel
with the collector chain (26–30, 32).**
Estimated time: ~1 hour

---

## Project structure changes

```text
src/Radar.Application/Reporting/
  MarkdownWeeklyReportRenderer.cs    # MODIFIED: allow + display Ignore; add "Ignore / Low signal" section
  WeeklyReportActionPolicyV1.cs      # MODIFIED: may emit Ignore for low-signal companies

tests/Radar.Application.Tests/Reporting/
  MarkdownWeeklyReportRendererTests.cs   # MODIFIED
  WeeklyReportActionPolicyV1Tests.cs     # MODIFIED
```

---

## Implementation details

### `MarkdownWeeklyReportRenderer`

- Add `RadarReportAction.Ignore` to the static `Allowed` set.
- Add `[RadarReportAction.Ignore] = "Ignore"` to `DisplayLabels`.
- The forbidden-label guard stays (any action not in the six-item set still throws); update the class
  summary comment from "five ALLOWED labels" to the six AD-9 labels.
- Add an **"## Ignore / Low signal"** section (mirroring `AppendThesisSection`) listing `Ignore`
  entries by name/ticker/rank. Place it after the thesis sections and before "Signals needing review".
  Entries already render in the "Highest opportunity" list ranked by opportunity (lowest at the bottom);
  the new section is just the named low-signal roll-up the master spec shows. Keep output deterministic
  (invariant culture, `\n`, model order).

### `WeeklyReportActionPolicyV1`

- Update the class summary: it may now return `Ignore` (remove "Never returns ... Ignore").
- Add a low-signal rule at the **end** of the steady-state branch, replacing the current
  bottom `NeedsMoreEvidence` fallthrough *only when evidence is adequate*. Precedence stays:
  1. Thin evidence (`EvidenceConfidenceScore < EvidenceConfidenceFloor`) → `NeedsMoreEvidence`
     (unchanged — thin evidence must not be silently ignored).
  2. Deterioration → `ThesisDeteriorating` (unchanged).
  3. Improvement → `ThesisImproving` (unchanged).
  4. `OpportunityScore >= InvestigateOpportunity` → `Investigate` (unchanged).
  5. `OpportunityScore >= WatchOpportunity` → `Watch` (unchanged).
  6. **NEW**: otherwise (evidence is adequate but opportunity is below `WatchOpportunity`) →
     `Ignore` with rationale e.g. `$"Opportunity {score} below {WatchOpportunity} with adequate
     evidence; low signal."`.
- Add a constant comment clarifying the boundary so the distinction between `NeedsMoreEvidence`
  (insufficient evidence) and `Ignore` (sufficient evidence, low opportunity) is explicit. `Version`
  stays `"weekly-report-action-v1"` — this is the first emission of `Ignore`, not a formula change to an
  already-shipped scoring snapshot; the policy is a labelling layer, not a scored artifact. (If the
  reviewer prefers a version bump for the behaviour change, bump to `"weekly-report-action-v2"` and note
  it — either is acceptable; pick one and be consistent in tests.)

---

## Tests

### `MarkdownWeeklyReportRendererTests` (MODIFIED)
- An entry with `RadarReportAction.Ignore` now renders (no throw), shows `- Label: Ignore`, and appears
  under "## Ignore / Low signal".
- A truly out-of-set action (cast an undefined enum value) still throws.
- Existing label/section assertions still pass.

### `WeeklyReportActionPolicyV1Tests` (MODIFIED)
- **Low signal → Ignore**: adequate `EvidenceConfidenceScore` (>= floor), no prior-snapshot delta, and
  `OpportunityScore` below `WatchOpportunity` returns `Ignore`.
- **Thin evidence still wins**: below the evidence floor returns `NeedsMoreEvidence` even when
  opportunity is low (Ignore must not mask thin evidence).
- Existing Investigate/Watch/Improving/Deteriorating cases unchanged.

---

## Constraints

- Target .NET 10. Pure, deterministic renderer/policy (no clock, no I/O).
- Honour AD-9: the six allowed labels exactly; never emit advice language
  (`buy`/`sell`/`strong buy`/`price target`/`guaranteed`/`safe bet`).
- `Ignore` means "adequate evidence, low opportunity"; `NeedsMoreEvidence` means "insufficient
  evidence" — keep them distinct.
- Scope strictly to the renderer + policy + their tests; do not touch scoring, the builder, or the
  pipeline.
- `dotnet build`/`dotnet test` on `Radar.sln -c Release` green.

---

## Acceptance criteria

- [ ] `MarkdownWeeklyReportRenderer` allows + displays `Ignore` and renders an "Ignore / Low signal"
      section; non-allowed actions still throw.
- [ ] `WeeklyReportActionPolicyV1` emits `Ignore` for adequate-evidence/low-opportunity companies while
      thin evidence still maps to `NeedsMoreEvidence`.
- [ ] Tests cover Ignore rendering, Ignore vs NeedsMoreEvidence policy boundary, and existing labels;
      `build`/`test` green.

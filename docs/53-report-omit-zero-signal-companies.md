# Task: Weekly report should not surface zero-signal companies as empty "Highest opportunity" rows

## Overview

The live runs exposed a report-quality issue: `WeeklyReportBuilder` creates a "Highest opportunity" entry for
**every** company with an in-period score snapshot — including companies scored from **zero** in-window signals.
Those appear as noise rows:

```
### 5. Mercury Systems, Inc. (MRCY)
- Label: Needs more evidence
- Opportunity 0 · Trajectory 0 · Attention 0 · Evidence 0 · Velocity 0
- Why: Evidence confidence 0 is below 35; needs more evidence.
- Evidence:
  - (no linked evidence)
```

A company with no signals and no linked evidence is an **absence of data**, not an opportunity — listing it under
"Highest opportunity" with all-zero scores and "(no linked evidence)" is misleading and clutters the digest. (The
zero-signal snapshot is still legitimately computed and persisted for provenance/completeness; this is purely
about what the *report* surfaces.)

This slice stops the weekly report from surfacing zero-signal companies in the "Highest opportunity" list.

---

## Assignment

Worktree: any
Dependencies: existing trunk. Independent of spec 52 (52 widens the window, which makes empties rarer but does
not eliminate them — a company with genuinely no recent news still scores zero). No file overlap.
Conflicts with: None.
Estimated time: ~45 min

---

## Project structure changes

```text
src/Radar.Application/Reporting/
  WeeklyReportBuilder.cs            # MODIFIED: skip zero-signal snapshots when building entries

tests/Radar.Application.Tests/Reporting/
  WeeklyReportBuilderTests.cs       # MODIFIED: zero-signal company is not surfaced; signal-bearing ones are
tests/Radar.IntegrationTests/
  PipelineEndToEndTests.cs          # MODIFIED only if its assertions count surfaced entries
```

## Implementation details

- In `WeeklyReportBuilder.GenerateAsync`, exclude a company from the "Highest opportunity" entries when its
  current snapshot has **no contributing signals** — i.e. zero `ScoreEvidenceLink`s for that snapshot (equivalently
  the all-zero snapshot a 0-signal company receives). Prefer testing the actual provenance signal (no links)
  rather than `OpportunityScore == 0`, so the rule is "no evidence behind it", not a magic score value.
  - The builder already fetches `GetLinksForSnapshotAsync(snapshot.Id)` for surviving entries; to decide
    inclusion before that, either move the links fetch earlier for the candidate set, or use the snapshot's
    signal/contribution count. Keep it deterministic and avoid an extra repository round-trip per company where
    practical (a snapshot with zero links can be detected from the links already needed for the evidence/signal
    refs).
- Do **not** change scoring, the policy, or the renderer's section logic. Companies with ≥1 signal continue to
  surface exactly as today (same ranking, labels, evidence, "Why noticed").
- Keep determinism (AD-3): the surviving entries' ranking/order is unchanged.
- Edge case: if **no** company has any signal this run, the "Highest opportunity" section is empty (as today when
  nothing scored) — that is correct; the "Collection summary" footer still shows what was checked.

## Tests

- `WeeklyReportBuilderTests`: a company whose snapshot has zero score-evidence links is NOT emitted as an entry;
  companies with ≥1 link are emitted and ranked as before. Assert the surfaced count excludes the zero-signal one.
- Confirm an all-zero-signal run produces an empty "Highest opportunity" section without error.
- Existing report tests (labels, sections, "Why noticed", determinism) stay green.

## Constraints

- Target `net10.0`. Deterministic; report-builder scope only (+ tests). No scoring/policy/renderer change.
- Provenance intact: omitted companies are still scored and persisted; only the report display is filtered.
- `dotnet build`/`dotnet test` on `Radar.sln -c Release` green.

## Acceptance criteria

- [ ] Companies scored from zero in-window signals (no score-evidence links) do not appear in the report's
      "Highest opportunity" list.
- [ ] Signal-bearing companies surface unchanged (ranking, labels, evidence, rationale).
- [ ] An all-zero run yields an empty "Highest opportunity" section gracefully; `build`/`test` green.

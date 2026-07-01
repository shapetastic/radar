# Task: Weekly report — show each company's score movement vs its previous snapshot

## Overview

The report already labels companies `Thesis improving` / `Thesis deteriorating` when a company's latest
snapshot beats or trails its prior one — the `WeeklyReportBuilder` already loads each candidate's `Previous`
snapshot (`CandidateEntry.Previous`) and hands it to the action policy. But the rendered markdown shows only
the **current** absolute scores; the *movement* that earned the thesis label is invisible. A reader sees
"Trajectory 61" with no idea it fell from 80 last run. This is exactly the week-over-week story the weekly
report exists to tell, and it makes the corroboration payoff from `radar-formula-v2` legible run-over-run.

This slice surfaces the **delta of each company's Opportunity and Trajectory scores versus its previous
snapshot** in the report entry, deterministically. It reuses data the builder already fetches — no new
repository query, no new store, no scoring change. It is independent of spec 59 (it reads the previous
*snapshot*, not the run log).

Scope is **the report entry model + builder wiring of the already-loaded previous snapshot + the renderer
line + tests**. It does NOT change scoring, the action policy, ranking, or which companies surface.

---

## Assignment

Worktree: any
Dependencies: existing trunk (report builder + renderer merged). Independent of 59.
Conflicts with: 61 (both touch `MarkdownWeeklyReportRenderer` + `WeeklyReportBuilder`/entry model — sequence
60 → 61). No conflict with 59.
Estimated time: ~1–1.5 h

---

## Project structure changes

```text
src/Radar.Application/Reporting/
  WeeklyReportEntry.cs                 # MODIFIED: carry the previous snapshot's Opportunity/Trajectory (or the deltas) for the entry
  WeeklyReportBuilder.cs               # MODIFIED: pass c.Previous's scores into the entry (already computed, currently unused for rendering)
  MarkdownWeeklyReportRenderer.cs      # MODIFIED: render a "vs last snapshot" movement on the score line

tests/Radar.Application.Tests/Reporting/
  MarkdownWeeklyReportRendererTests.cs # MODIFIED: delta renders (up/down/flat/first-time) deterministically
  WeeklyReportBuilderTests.cs          # MODIFIED: the previous snapshot's scores flow into the entry
```

---

## Implementation details

### Carry the previous scores onto the entry
- Extend `WeeklyReportEntry` with the previous snapshot's comparison scores. Prefer carrying the **previous
  values** (nullable) rather than pre-computed deltas, so the renderer owns formatting and the model stays
  data-not-presentation:
  - `int? PreviousOpportunityScore`
  - `int? PreviousTrajectoryScore`
  (Null when the company has no prior snapshot — a first-time surface.)
- In `WeeklyReportBuilder`, the rank-ordered loop already has `c.Previous` (the latest snapshot strictly
  before `current`). Populate the new entry fields from `c.Previous?.OpportunityScore` /
  `c.Previous?.TrajectoryScore`. No new repository call — `Previous` is already computed for the policy.
- Keep the entry an immutable record; add the fields to the constructor (update all call sites/tests).

### Render the movement
- In `MarkdownWeeklyReportRenderer.AppendEntry`, on the existing score line (the
  `- Opportunity … · Trajectory … · …` bullet), append a deterministic movement suffix computed purely from
  the entry's current vs previous values. Suggested, ASCII-only (no emoji — house rule), invariant-culture:
  - Both present and current > previous: ` (Opportunity +N, Trajectory +M vs last run)`.
  - Current < previous: negative deltas, e.g. ` (Opportunity -19, Trajectory -19 vs last run)`.
  - Equal: ` (no change vs last run)`.
  - No previous snapshot: ` (first snapshot)`.
- Use a plain signed integer delta (`current - previous`), formatted with an explicit sign for non-negative
  (`+N`) and the natural `-N` for negatives, via `CultureInfo.InvariantCulture`. Keep it a single appended
  clause on the existing line so existing golden-string assertions change in one predictable place.
- Do NOT introduce advice language and do NOT re-order or re-label anything — this is presentation only. The
  renderer stays pure/deterministic (no clock, no I/O); identical models still render byte-identically.

### Keep it presentation-only
- No change to `IReportActionPolicy`, ranking, the zero-link skip (spec 53), the six allowed labels, or the
  provenance guards in the renderer. The delta is descriptive metadata alongside the absolute scores, never a
  new label or recommendation.

---

## Tests

- `MarkdownWeeklyReportRendererTests`: an entry whose current Opportunity/Trajectory exceed the previous
  renders the `+N`/`+M … vs last run` clause; a lower current renders negative deltas; equal renders
  `no change vs last run`; a null-previous entry renders `first snapshot`. Assert exact strings (deterministic,
  invariant culture, `\n`). Confirm no advice language appears.
- `WeeklyReportBuilderTests`: a company with an earlier and a later in/prior snapshot yields an entry whose
  `PreviousOpportunityScore`/`PreviousTrajectoryScore` equal the earlier snapshot's values; a company with only
  one snapshot yields null previous values. Existing ranking/skip/label assertions stay green.
- Update any other test that constructs a `WeeklyReportEntry` for the new constructor parameters.

---

## Constraints

- Target `net10.0`. Renderer stays pure and deterministic (no clock, no I/O); same model → byte-identical
  markdown. Invariant culture, `\n` endings, ASCII only (no emoji).
- Reuse the already-loaded `Previous` snapshot — no new repository query, no new store, no scoring/policy
  change. Provenance and the six AD-9 labels unchanged.
- No advice language; the delta is descriptive, never a recommendation.
- Scope: entry model + builder wiring + renderer line + tests. `dotnet build`/`dotnet test` on
  `Radar.sln -c Release` green.

---

## Acceptance criteria

- [ ] `WeeklyReportEntry` carries the previous snapshot's Opportunity/Trajectory (nullable), populated by the
      builder from the already-computed `Previous` snapshot with no new repository call.
- [ ] The renderer appends a deterministic, invariant-culture, ASCII-only movement clause to the score line:
      signed deltas when a previous snapshot exists (up and down), `no change` when equal, `first snapshot`
      when absent.
- [ ] No change to labels, ranking, the zero-link skip, the action policy, or provenance guards; renderer
      stays pure/deterministic.
- [ ] Tests cover up/down/flat/first-time rendering and the previous-score flow through the builder; no advice
      language. `build`/`test` green.

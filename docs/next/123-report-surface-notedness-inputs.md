# Task: Surface the notedness inputs (Attention + FollowingTier) per surfaced company in the weekly report

> **REPORT TRANSPARENCY (renderer layer).** Makes the `radar-formula-v7` notedness discount **legible** in the
> weekly report: for each surfaced company it shows the two inputs that drove its Opportunity discount — measured
> **Attention** and the curated **FollowingTier** (AD-14 non-price). Read-only over data already on the snapshot
> and the company; **no scoring change, no fingerprint move, no new persisted field.** Directly de-risks the
> OPEN-DECISION (b) discussion (spec 122) by letting the maintainer *see* why e.g. JNJ was discounted (Mega,
> Attention 21) versus AEHR (Small, Attention 19) in the rendered report rather than only in logs.

## Overview

Since `radar-formula-v7` (spec 117), a company's Opportunity is discounted by BOTH its measured `AttentionScore`
and its curated `FollowingTier` (`Small`/`Mid`/`Large`/`Mega`). Today the weekly report shows Opportunity /
Trajectory / Evidence-confidence but **not** the two notedness inputs, so a reader cannot tell *why* a
plausibly-improving mega-cap was pushed below the actionable surface — the mechanism is invisible. This slice adds
a one-line **"Notedness"** annotation per surfaced company:

```
Notedness: Attention 21 · Following: Mega (already broadly followed)
```

It is a **research statistic** (AD-9 clean — no advice, no performance claim), rendered from values already
present: `CompanyScoreSnapshot.AttentionScore` and `Company.FollowingTier`. No score recomputation, no discount
re-derivation (surface the inputs, not a recomputed multiplier), no persistence change.

---

## Assignment

Worktree: any. Edits the report entry/model + markdown renderer (+ the builder line that populates the entry).
Dependencies: **spec 121** (both edit `WeeklyReportBuilder` and the report-entry plumbing — **sequence after
121**, do not parallelize). No scoring/fingerprint dependency.
Conflicts with: spec 121 (shared `WeeklyReportBuilder` / report-entry surface) and any slice touching
`MarkdownWeeklyReportRenderer` / `WeeklyReportEntry` / `WeeklyReportModel`.
Estimated time: ~1–1.5 hours

---

## Grounding facts (verified against the code, 2026-07-21)

- **`CompanyScoreSnapshot`** (`src/Radar.Domain/Scoring/CompanyScoreSnapshot.cs`) already carries `AttentionScore`
  (int). The entry already holds the whole `Snapshot`.
- **`Company.FollowingTier`** (`src/Radar.Domain/Companies/FollowingTier.cs`): `Small | Mid | Large | Mega`. The
  builder holds `c.Company` when it constructs the entry.
- **`WeeklyReportBuilder`** (`src/Radar.Application/Reporting/WeeklyReportBuilder.cs`): builds
  `WeeklyReportEntry` (the private/record model consumed by the renderer). `FollowingTier` must be threaded onto
  the entry (from `c.Company.FollowingTier`); `AttentionScore` is already reachable via `entry.Snapshot`.
- **`MarkdownWeeklyReportRenderer`** (`src/Radar.Application/Reporting/MarkdownWeeklyReportRenderer.cs`) renders
  each surfaced company block (the "Opportunity / Trajectory / Evidence confidence / Why Radar noticed / Evidence"
  layout). Add the Notedness line there, near the existing score lines.
- The `WeeklyReportModel` / `WeeklyReportEntry` shape lives alongside the builder (whichever file defines them);
  add the `FollowingTier` field there.
- Labels/report content are **not** hashed into `ScoringConfigVersion`; efficacy reads snapshots, not the report.
  **Zero** fingerprint / efficacy impact.

---

## Implementation details

1. Add `FollowingTier FollowingTier` to `WeeklyReportEntry` (default `FollowingTier.Small` for construction-site
   safety); the builder sets it from `c.Company.FollowingTier`.
2. In `MarkdownWeeklyReportRenderer`, for each surfaced company render one line, e.g.:
   `- **Notedness:** Attention {AttentionScore} · Following: {FollowingTier}` — optionally with a short,
   advice-free parenthetical per tier (`Small` = "under-followed", `Mega` = "already broadly followed", etc.).
   Keep wording factual; no "buy/sell/upside/undervalued" language (AD-9).
3. A short explanatory sentence under the report's existing methodology/disclaimer note is optional but nice:
   "Notedness (Attention + curated following tier) discounts a company's Opportunity so already-followed names
   surface lower — a research signal, not a valuation."

Keep it minimal: surface the two **inputs** already on the snapshot/company. Do **not** recompute the v7
`followingDiscount` multiplier (that would duplicate formula logic in the renderer — if a numeric discount is ever
wanted, it should be persisted by the engine in a later slice, out of scope here).

---

## Tests

- **`MarkdownWeeklyReportRendererTests`** (extend): a surfaced company renders a `Notedness` line containing its
  `AttentionScore` and `FollowingTier`; a `Mega`/`Small` tier renders the correct token; no advice language
  appears (extend the existing forbidden-language assertion if present).
- **`WeeklyReportBuilderTests`** (adjust): the entry carries the company's `FollowingTier`.
- Existing renderer/builder tests stay green. Full gate: `dotnet build Radar.sln -c Release` and
  `dotnet test Radar.sln -c Release --no-build` green.

---

## Constraints

- Target `net10.0`, C# 14. Read-only over existing snapshot/company data; **no scoring recomputation**, no new
  persisted field, **no `ScoringConfigVersion` fingerprint move**, no `_formula.Version` / `RuleSetVersion` bump.
- **AD-9:** the Notedness line is a research statistic — factual, no advice / valuation language.
- Preserve provenance/layout; keep the change scoped to the render + entry plumbing. Do not implement unrelated
  features.

---

## Acceptance criteria

- [ ] Each surfaced company in the weekly report shows a `Notedness` line with its measured `AttentionScore` and
      curated `FollowingTier`.
- [ ] The value is read from the existing snapshot/company (no discount recomputation, no new persisted field).
- [ ] No advice language (AD-9); allowed labels/wording only.
- [ ] No scoring / fingerprint change (AI-OFF `8f4b59efd288` / AI-ON `2ef5ef96cce2` unchanged).
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.

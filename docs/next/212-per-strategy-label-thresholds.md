# Task: Per-strategy label thresholds — the Investigate/Watch lines are calibrated per arm from its measured distribution, stated in the report, and no longer two constants tuned for one formula

## Overview

`WeeklyReportActionPolicyV1` labels a company **Investigate** at Opportunity ≥ 60 and **Watch** at ≥ 40
(`InvestigateOpportunity` / `WatchOpportunity`, private constants, L61–62). Those lines were set when
`radar-formula-v8`'s multi-channel composite was the only formula. Since spec 184 the narrative sections
and labels follow the **Lead** arm (`disclosure-led-v11`, `radar-formula-v11`), whose Opportunity is
`100 × S × P × notedness` over the FILINGS channel alone, with `S = M/(M+3)` and `P = (M⁺−M⁻)/(M+10)`
(`RadarScoreFormulaV11`, `ScoreSignalMath.Saturate` / `.Preponderance`, `TrajectoryCorroborationK = 10`).
Both saturating terms were tuned for all-channel mass; multiplied on one channel's mass the composite
collapses (a perfect, unanimous filings record of mass 6 scores 0.25; the best company in the universe
carries about that). Reaching 40 needs mass ≈ 16 with zero dissent, 60 needs ≈ 48 — EDGAR supplies 2–4
directional reads per 60-day window. The lines never moved when the formula did.

**Measured (2026-09-07, read-only pass over every accrued snapshot under `data/scores/` and
`data/scores/strategies/`, as-of dates 2026-07-29 → 2026-09-06; the script is §5):**

| arm | formula | n | dates | max | p99 | p90 | p50 | ≥ 60 | ≥ 40 | share ≥ 40 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| disclosure-led-v11 (**Lead**) | v11 | 3,471 | 35 | 21 | 18 | 12 | 0 | 0 | 0 | 0.0% |
| disclosure-led-v10-control | v10 | 3,471 | 35 | 21 | 18 | 13 | 0 | 0 | 0 | 0.0% |
| filings-led-v2 / -halfnoted / -nonoted | v9 | ≈3,650 | 37–38 | 28 / 31 / 39 | 25 / 30 / 37 | 22 / 27 / 32 | 12 / 17 / 21 | 0 | 0 | 0.0% |
| narrative-led-v2 | v9 | 3,557 | 36 | 39 | 27 | 21 | 17 | 0 | 0 | 0.0% |
| default (storage primary) | v8 | 3,545 | 36 | 44 | 42 | 31 | 18 | 0 | 74 | 2.1% |
| default-noattn | v8, no discount | 878 | 7 | 76 | 71 | 52 | 30 | 21 | 174 | 19.8% |
| baseline-earnings-only | comparator | 3,471 | 35 | 52 | 50 | 42 | 15 | 0 | 602 | 17.3% |
| baseline-media-only | comparator | 3,471 | 35 | 50 | 43 | 39 | 31 | 0 | 203 | 5.8% |
| baseline-activity-only | comparator | 3,471 | 35 | 41 | 33 | 24 | 17 | 0 | 5 | 0.1% |

**Consequences, from the 60 accrued weekly reports:** `Investigate` has rendered **4 times ever** (all
pre-2026-07-29, single-strategy era); the last score-based `Watch` is in the 2026-08-14 report; since the
Lead took the narrative (2026-08-23) **zero** labels have come from a score — every one of the 307 accrued
`Watch` labels since then is the spec-187 corroboration floor (last night: 18 of 18). Ranking is governed
by the score; labels are governed entirely by a rule that was designed as a floor. Even on v8 the
Investigate line has not fired in 36 dates (max 44). This is the CLAUDE.md live-distribution defect exactly:
a near-constant measure against a fixed threshold, provably correct and discriminating nothing.

This slice makes the two Opportunity lines **per-strategy config**, defaulting to today's values so
every arm that does not set them is byte-identical; states the lines in effect on the report; and seeds
the Lead's values from its measured distribution. It is report-layer only — no score, weight, formula,
fingerprint or accrued file changes. The formula constants (`3`, `10`) are NOT touched: retuning them is a
composition change (v12 / `CompositionRevision`) and a different, later decision.

## Assignment

Worktree: any. Dependencies: spec 211 merged (`e4f330d`). Use `run-next.ps1 -Spec 212`.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. `Labels` on the strategy definition — config, validated, NOT identity

- New immutable value `LabelThresholds(int Investigate, int Watch)` in `Radar.Application.Reporting` with
  `LabelThresholds.Default = (60, 40)` (the ONLY place those two numbers are defined from now on; the
  policy constants are deleted). Invariant, enforced in the constructor: `0 < Watch < Investigate ≤ 100`.
- `ScoringStrategyDefinition` gains `LabelThresholds Labels { get; init; } = LabelThresholds.Default`.
- Config: an optional `Labels` object on each `Radar:Strategies[i]` entry, `{ "Investigate": n, "Watch": m }`,
  BOTH required when the object is present (a half-set pair is a startup failure naming the path, not a
  silent default — the spec-176 fail-closed shape). Add `"Labels"` to `StrategyEntryKeys` AND to the
  "Valid keys:" text of `RejectUnknownStrategyEntryKeys`
  (`InfrastructureServiceCollectionExtensions.cs` ~L752/774); resolve it beside `ResolvePurpose`. Reject
  unknown child keys inside `Labels` the same way.
- **Not a fingerprint input.** `ScoringConfigFingerprint`, `FormulaIdentity`, `StrategyIdentityGuard` and
  the strategy-config files are untouched; a label line is a presentation decision, not a scoring one
  (AD-10 as amended: the fingerprint stamps what changes a SCORE). `ScoringConfigFingerprintTests` must
  pass unchanged — the PR body states "spec 212 moved nothing", scoped to this slice.

## 2. The policy reads the labelled arm's lines; version v4 → v5

- `ReportActionContext` gains a trailing, defaulted `LabelThresholds? Thresholds = null` (the spec-210
  additive pattern); `null` means `Default`, never "no thresholds". `WeeklyReportBuilder` passes the
  narrative arm's `Definition.Labels` — the Lead's when a Lead call exists (`leadRuntime`, ~L199), else the
  primary's — at the single `Decide` call site (~L309).
- `WeeklyReportActionPolicyV1` replaces `InvestigateOpportunity` / `WatchOpportunity` with
  `context.Thresholds.Investigate` / `.Watch`. Rule order is unchanged; the rationale keeps interpolating
  the line it actually applied (`Opportunity 12 (>= 12)`), so a reader sees the arm's line, not 40. The
  corroboration floor (rule 4b) still floors to Watch and never above; its condition
  (`OpportunityScore < Watch`) now uses the arm's line, so a name that clears the arm's Watch line labels
  by SCORE with the score rationale, and the floor rationale only appears below it. `NeutralTrajectory`,
  `EvidenceConfidenceFloor`, `ThesisDelta`, `MinCorroboratingSignalTypes` stay constants — trajectory and
  evidence confidence are computed by the shared `ScoreSignalMath` on the same scale for every formula;
  only Opportunity changed scale.
- Bump `Version` `weekly-report-action-v4 → v5` (the mapping CONTRACT changes: the lines are inputs), pin
  updated in `WeeklyReportActionPolicyV1Tests`. Nothing hashes it (spec 211 verified); say so.
- **Tests:** (a) `Default` thresholds ⇒ every existing policy test byte-identical (the 400-case sweep from
  spec 210 re-asserts against `Default`); (b) a v11-shaped case: thresholds (20, 15), opportunity 16 ⇒
  `Watch` BY SCORE with rationale `(>= 15)`, no floor text; opportunity 12 with two corroborating positive
  types ⇒ floor rationale; opportunity 21 ⇒ `Investigate`; (c) invariant violations (`Watch >= Investigate`,
  zero, > 100) throw with the path; (d) a half-set `Labels` object fails startup naming
  `Radar:Strategies:{i}:Labels`; (e) the builder passes the LEAD's lines, not the primary's, when they
  differ (fixture with a Lead call on a non-primary arm).

## 3. The report states the lines in effect

- In `AppendDisclaimers` (or immediately under `## Live strategy leaders`' operating-call banner — pick the
  one place a reader hits first, and only one): one line, e.g.
  `> Labels in this report follow disclosure-led-v11 at Investigate ≥ 20 / Watch ≥ 15 (weekly-report-action-v5); a label is comparable only across reports labelled on the same arm and lines.`
  Rendered from the model (the builder puts the arm name + `LabelThresholds` on `WeeklyReportModel`), never
  from a constant. Per-strategy ranking sections stay label-free (they are today).
- `docs/reading-radar-output.md` "The labels": rules 4–5 become "the arm's Investigate line (default 60)" /
  "the arm's Watch line (default 40)", plus a short table of the live profile's configured lines per arm and
  a sentence that a Lead call must come with calibrated lines (below). The mapping version there → v5.
- `docs/strategy-lifecycle.md` header: one sentence — a `call-made … Lead` for an arm whose formula is not
  v8 must set `Labels` in the live profile in the same change, citing the distribution it was calibrated
  on; the journal line records the values. (The journal is audit-only; this is a documented obligation,
  not runtime validation.)

## 4. Live profile values (`scripts/run-profiles/default.json`) — the DATA decision, proposed from measurement

Calibration principle: **quantile-match at calibration time, then FIX.** The line's meaning is "the
share of snapshots the v8 primary labels at that line"; the arm's line is the Opportunity value at the
same share of ITS accrued distribution. Fixed numbers, not daily quantiles — a label must mean the same
thing on every report, and a relative threshold would mint Investigate every day regardless of evidence.

- `disclosure-led-v11` (Lead): **Watch 15** — v8 default labels 2.1% of snapshots ≥ 40; the v11 value at
  the top 2.1% is 15 (measured). **Investigate 20** — v8 has NO ≥ 60 share to match (0.0%, max 44), so
  this is a JUDGEMENT, stated as one: 20 is the top 0.3% of v11's accrued distribution (observed max 21),
  i.e. the line fires only for the very top of the arm. On last night's report those lines would have
  labelled AGX `Investigate` (20) and ESQ/JOUT/DGII `Watch` by score (17/16/15) with the remaining floors
  unchanged. The implementer re-measures on the store at implementation time (§5 script) and, if the
  quantile-matched Watch value has moved by more than 1, uses the re-measured value and says so.
- `disclosure-led-v10-control`: same lines as v11 (identical distributions: max 21 / p99 18 / p90 13) — it
  is the v11 control and must be labelled on the same scale if it is ever compared by label.
- v9 arms (`filings-led-*`, `narrative-led-v2`), `default-noattn`, comparators: **no `Labels` set** —
  defaults apply and are never rendered (only the narrative arm is labelled). Recorded in the operator
  guide as "uncalibrated — calibrate before any Lead call".
- `default`: no `Labels` — (60, 40) is its own calibration by definition, and the 2.1% ≥ 40 / 0.0% ≥ 60
  shares are recorded as what those lines currently MEAN on v8.

## 5. Measurement is reproducible: `scripts/audit-label-thresholds.ps1`

Read-only (no store mutation, no Worker), in the style of `scripts/audit-signal-directions.ps1`: scans
`data/scores/**` (primary + `strategies/`), prints per arm `n / dates / max / p99 / p95 / p90 / p50 /
share ≥ each configured line` and, for a `-Strategy` + `-MatchShareOf default` argument, the value at the
matched share. The PR body carries its output at implementation time (the "after" for the table above)
plus: for the first post-merge report, the count of labels by score vs by floor vs Investigate on the Lead.
If no post-merge run exists at PR time, the "after" column is UNMEASURED, not predicted.

## 6. Docs

- `docs/architecture-history.md`: a spec-212 bullet with the measured table, the calibration principle,
  and the values chosen; amend the spec-187 floor bullet IN PLACE to note that since 2026-08-23 the floor
  had been the ONLY source of Watch labels (the number it floors against is now per-arm).
- `CLAUDE.md` standing facts: one bullet — label lines are per-strategy config (`Radar:Strategies[i].Labels`,
  default 60/40, report-layer, not a fingerprint input; spec 212). Nothing else in CLAUDE.md.

## Non-goals

- Retuning `Saturation` / `TrajectoryCorroborationK` or v11's composition — a formula change (v12 or
  `CompositionRevision`), and the Lead is under a precommitted claim until 2026-09-29.
- Daily/relative (quantile) thresholds; labels on non-narrative arms; efficacy (labels never feed it).
- Changing the floor's rule (`MinCorroboratingSignalTypes`, tiers) or the other three constants.

## Acceptance criteria

- [ ] `LabelThresholds` is the single owner of 60/40; the policy has no threshold constants; invariant
      `0 < Watch < Investigate ≤ 100` enforced and tested.
- [ ] `Radar:Strategies[i].Labels` binds, is validated (both-or-neither, unknown child keys, path in the
      error), and `"Labels"` is in `StrategyEntryKeys` and its error text.
- [ ] `ReportActionContext.Thresholds` is nullable-defaulted; the builder passes the LEAD's lines (else the
      primary's); every existing policy/renderer test is byte-identical under `Default`.
- [ ] Policy `weekly-report-action-v5`; rationale interpolates the applied line; the floor floors against
      the arm's line and never above Watch; the v11-shaped cases (§2 b) pass.
- [ ] The report states arm + lines + version in exactly one place, rendered from the model.
- [ ] `default.json`: v11 and v10-control carry `Labels` (Watch quantile-matched and re-measured at
      implementation; Investigate stated as a judgement); no other arm does.
- [ ] `ScoringConfigFingerprintTests` unchanged; strategy-config files untouched; PR body says "spec 212
      moved nothing".
- [ ] `scripts/audit-label-thresholds.ps1` exists, is read-only, and its output is in the PR body.
- [ ] Operator guide, lifecycle header, architecture history (incl. the spec-187 in-place amendment) and
      the CLAUDE.md bullet updated.
- [ ] `dotnet build` / full suite / `git diff --check` clean; actual dispatch→PR time in the PR body.

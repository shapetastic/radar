# Task: Per-Lead operating thresholds — the Investigate/Watch lines are explicit, fixed, required for every declared Lead, stated on the report, and no longer two constants tuned for one formula

## Overview

`WeeklyReportActionPolicyV1` labels a company **Investigate** at Opportunity ≥ 60 and **Watch** at ≥ 40
(`InvestigateOpportunity` / `WatchOpportunity`, private constants, L61–62). Those lines were set when
`radar-formula-v8`'s multi-channel composite was the only formula. Since spec 184 the narrative sections
and labels follow the **Lead** arm (`disclosure-led-v11`, `radar-formula-v11`), whose Opportunity is
`100 × S × P × notedness` over the FILINGS channel alone, with `S = M/(M+3)` and `P = (M⁺−M⁻)/(M+10)`
(`RadarScoreFormulaV11`, `ScoreSignalMath.Saturate` / `.Preponderance`, `TrajectoryCorroborationK = 10`).
Both saturating terms were tuned for all-channel mass; multiplied on one channel's mass the composite
collapses. With every filing read positive, `S × P = M²/((M+3)(M+10))`, so the mass a company needs
depends on its notedness discount: **Opportunity 40 needs M ≈ 10.6 with no discount and ≈ 16 at a discount
of 0.75; Opportunity 60 needs ≈ 21.6 and ≈ 48.** There is no universal mass requirement — but EDGAR supplies
2–4 directional reads per 60-day window, and the best company in the universe carries mass ≈ 6 (AGX,
composite 0.259). The lines never moved when the formula did.

**The scales are not comparable.** Every formula emits one headline `OpportunityScore` on 0–100, but 20
under v11 does not mean what 20 means under v8, and two v8 arms differ as much as two formulas do
(`default-noattn`, v8 without the discount: 19.8% of snapshots ≥ 40 against `default`'s 2.1%). A label
line is therefore a property of the ARM, not of the formula version.

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

**What this slice is and is not.** It is a report-usability fix: the labels decide what a reader inspects,
so the lines that mint them must be explicit per Lead and stated where the reader sees them. It does NOT
improve Radar's business logic or its statistical evidence, and must not claim to: the lines below are
**fixed operating (triage) thresholds** chosen by prevalence — how many names a morning's report should put
in front of a human — and are connected to no outcome (returns, attention arrival, thesis survival). The raw
`OpportunityScore` is preserved untouched; a universal cross-arm score is a non-goal (see there). No score,
weight, formula, fingerprint or accrued file changes. The formula constants (`3`, `10`) are NOT touched:
retuning them is a composition change (v12 / `CompositionRevision`) and a different, later decision.

## Assignment

Worktree: any. Dependencies: spec 211 merged (`e4f330d`). Use `run-next.ps1 -Spec 212`.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. `Labels` on the strategy definition — config, validated, nullable, NOT identity

- New immutable value `LabelThresholds(int Investigate, int Watch)` in **`Radar.Application.Scoring`**,
  beside `StrategyPurpose` (the analogous report-only strategy metadata) — NOT in `Reporting`, which would
  make Scoring depend back on Reporting. `LabelThresholds.Default = (60, 40)` is the ONLY place those two
  numbers are defined from now on; the policy constants are deleted. Invariant, enforced in the
  constructor: `0 < Watch < Investigate ≤ 100`.
- `ScoringStrategyDefinition` gains **`LabelThresholds? Labels { get; init; } = null`**. Nullable is the
  point: `null` means "omitted", and an explicit `{ 60, 40 }` means "chose the defaults" — the two are
  distinguishable, and only the second satisfies §2's Lead requirement.
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

## 2. Every declared Lead REQUIRES explicit lines — at runtime, not by documentation

The Lead is known only when `data/strategy-operating-calls.json` is read (report time, `WeeklyReportBuilder`
~L199, `lifecycle.Calls.LeadStrategyName`), so the requirement is enforced THERE, fail-closed: if the
resolved Lead's `Definition.Labels is null`, the builder throws `InvalidOperationException` naming the arm
and the exact config path (`Radar:Strategies:{i}:Labels`) and stating why (a Lead's labels decide what a
human inspects; defaulting them silently is the fail-open shape). This is independent of formula version —
`default-noattn` (v8) shows a formula version says nothing about an arm's scale. It mirrors
`StrategyIdentityGuard`'s stance: a halt with a named remedy is correct; a report labelled on lines nobody
chose is not.

When NO Lead call exists the narrative follows the storage primary (pre-184 path) and uses
`Labels ?? LabelThresholds.Default`; the report banner (§4) then says "defaults (no Lead declared)".
Non-Lead research arms and comparators may omit `Labels`; comparators cannot carry a call
(`OperatingCallReducer`), so configuring them is dead config and is NOT done.

## 3. The policy reads the labelled arm's lines; version v4 → v5

- `ReportActionContext` gains a trailing, defaulted `LabelThresholds? Thresholds = null` (the spec-210
  additive pattern); `null` resolves to `Default` inside the policy. `WeeklyReportBuilder` passes the
  narrative arm's lines (the Lead's, required non-null by §2; else the primary's or `Default`) at the single
  `Decide` call site (~L309).
- `WeeklyReportActionPolicyV1` replaces `InvestigateOpportunity` / `WatchOpportunity` with the context's
  lines. Rule order is unchanged; the rationale keeps interpolating the line it actually applied
  (`Opportunity 16 (>= 15)`), so a reader sees the arm's line, not 40. The corroboration floor (rule 4b)
  still floors to Watch and never above; its condition (`OpportunityScore < Watch`) now uses the arm's line,
  so a name that clears the arm's Watch line labels BY SCORE with the score rationale, and the floor
  rationale appears only below it. `NeutralTrajectory`, `EvidenceConfidenceFloor`, `ThesisDelta`,
  `MinCorroboratingSignalTypes` stay constants — trajectory and evidence confidence are computed by the
  shared `ScoreSignalMath` on the same scale for every formula; only Opportunity changed scale.
- Bump `Version` `weekly-report-action-v4 → v5` (the mapping CONTRACT changes: the lines are inputs), pin
  updated in `WeeklyReportActionPolicyV1Tests`. Nothing hashes it (spec 211 verified); say so.
- **Tests:** (a) under `Default` every existing POLICY test is byte-identical (the spec-210 400-case sweep
  re-asserts against `Default`) — policy RESULTS, not renderer output (§4 adds a banner line); (b) v11-shaped
  cases with lines (20, 15): opportunity 16 ⇒ `Watch` BY SCORE, rationale `(>= 15)`, no floor text;
  opportunity 12 with two corroborating positive types ⇒ floor rationale; opportunity 21 ⇒ `Investigate`;
  (c) invariant violations (`Watch >= Investigate`, zero, > 100) throw naming the path; (d) a half-set
  `Labels` object fails startup naming `Radar:Strategies:{i}:Labels`; (e) the builder passes the LEAD's
  lines when they differ from the primary's (fixture: Lead call on a non-primary arm); (f) a Lead whose
  `Labels` is null makes the builder throw with the arm name and config path in the message; (g) a
  full-report renderer fixture under `Default` with no Lead whose ONLY diff from the pre-212 pin is the
  §4 banner line.

## 4. The report states the lines in effect — once

- In `AppendDisclaimers` (or immediately under `## Live strategy leaders`' operating-call banner — pick the
  one place a reader hits first, and only one): one line, e.g.
  `> Labels in this report follow disclosure-led-v11 at Investigate ≥ 20 / Watch ≥ 15 (weekly-report-action-v5). These are fixed operating thresholds set by prevalence, not validated evidence of opportunity; a label is comparable only across reports labelled on the same arm and lines.`
  Rendered from the model (the builder puts the arm name, the `LabelThresholds`, and whether they were
  explicit or defaulted on `WeeklyReportModel`), never from a constant. Per-strategy ranking sections stay
  label-free (they are today).
- `docs/reading-radar-output.md` "The labels": rules 4–5 become "the Lead's Investigate line" / "the Lead's
  Watch line" (defaults 60/40 only when no Lead is declared), a short table of the live profile's configured
  lines per arm, the runtime requirement from §2, and the sentence that the lines are triage prevalence, not
  outcome-calibrated. The mapping version there → v5.
- `docs/strategy-lifecycle.md` header: one sentence — a `call-made … Lead` must land in the same change as
  that arm's `Labels` in the live profile (the Worker will refuse to build the report otherwise), and the
  journal line records the values and the prevalence they were chosen at.

## 5. Live profile values (`scripts/run-profiles/default.json`) — PINNED

Principle: **prevalence-match once, then FIX.** A line's operational meaning is "the share of snapshots it
puts in front of a reader"; the Lead's line is the Opportunity value at a chosen share of ITS accrued
distribution. Fixed numbers, not daily quantiles — a label must mean the same thing on every report, and a
relative threshold would mint Investigate every day regardless of evidence. This is a workload decision
and is labelled as one; it says nothing about whether 20 is a strong opportunity.

- `disclosure-led-v11` (Lead): **`Labels: { "Investigate": 20, "Watch": 15 }`** — pinned by this spec.
  Watch 15 reproduces the v8 primary's historical 2.1% ≥ 40 prevalence on v11's accrued distribution
  (measured value 15). Investigate 20 is the top 0.3% of that distribution (observed max 21) — v8 has no
  ≥ 60 prevalence to match (0.0%, max 44), so 20 is a JUDGEMENT about how many names a morning should
  escalate, stated as one. On the 2026-09-06 report those lines would have labelled AGX `Investigate` (20)
  and ESQ/JOUT/DGII `Watch` by score (17/16/15), with the remaining floors unchanged — noted, not a
  justification: the line was not chosen to promote AGX.
- **No other arm sets `Labels`.** `default` is its own 60/40 by definition (2.1% / 0.0% recorded as what
  those lines currently MEAN on v8); `disclosure-led-v10-control` is a comparator and cannot lead (dead
  config — do not add); v9 arms, `default-noattn` and the baselines are documented in the operator guide as
  "no lines set — a Lead call on this arm must add them".
- **The coder does not retune.** The §6 script is run at implementation time and its output goes in the PR
  body. If the prevalence-matched Watch value for v11 differs from 15 by more than 1, the coder STOPS and
  reports; the maintainer amends this section before implementation resumes. A new data decision is not
  delegated to the implementer.

## 6. Measurement is reproducible: `scripts/audit-label-thresholds.ps1`

Read-only (no store mutation, no Worker), in the style of `scripts/audit-signal-directions.ps1`: scans
`data/scores/**` (primary + `strategies/`), prints per arm `n / dates / max / p99 / p95 / p90 / p50 /
share ≥ each configured line (or ≥ 60/40 when none)` and, for `-Strategy X -MatchPrevalenceOf default`, the
value of X at default's ≥ 40 share. The PR body carries its output at implementation time (the "after" for
the table above) plus, for the first post-merge report, the Lead's label counts by score vs by floor vs
Investigate. If no post-merge run exists at PR time, the "after" column is UNMEASURED, not predicted.

## 7. Docs

- `docs/architecture-history.md`: a spec-212 bullet with the measured table, the prevalence principle, the
  runtime Lead requirement, and the pinned values; amend the spec-187 floor bullet IN PLACE to note that
  from 2026-08-23 to this slice the floor had been the ONLY source of Watch labels (the number it floors
  against is now per-Lead).
- `CLAUDE.md` standing facts: one bullet — label lines are per-strategy config (`Radar:Strategies[i].Labels`,
  nullable; REQUIRED for a declared Lead, enforced at report build; report-layer, not a fingerprint input;
  fixed triage prevalence, not outcome-calibrated; spec 212). Nothing else in CLAUDE.md.

## Non-goals

- Retuning `Saturation` / `TrajectoryCorroborationK` or v11's composition — a formula change (v12 or
  `CompositionRevision`), and the Lead is under a precommitted claim until 2026-09-29.
- **A universal cross-arm score, or rescaling any arm's Opportunity.** Rescaling v11 so 20 displays as 60
  is arithmetically the same as lowering its lines and merely hides the conversion; averaging arms would let
  failed arms contaminate better ones. A genuinely comparable score needs ONE external meaning (e.g. the
  estimated probability of a positive 21-day excess return) and prospective calibration on adequate
  outcome data — revisit only after the efficacy evidence identifies a surviving formula.
- Daily/relative (quantile) thresholds; labels on non-narrative arms; efficacy (labels never feed it).
- Changing the floor's rule (`MinCorroboratingSignalTypes`, tiers) or the other three constants.

## Acceptance criteria

- [ ] `LabelThresholds` lives in `Radar.Application.Scoring`, is the single owner of 60/40; the policy has
      no threshold constants; invariant `0 < Watch < Investigate ≤ 100` enforced and tested.
- [ ] `ScoringStrategyDefinition.Labels` is nullable (omitted ≠ explicit 60/40);
      `Radar:Strategies[i].Labels` binds, is validated (both-or-neither, unknown child keys, path in the
      error), and `"Labels"` is in `StrategyEntryKeys` and its error text.
- [ ] A declared Lead with null `Labels` fails the report build with the arm and config path named; no Lead
      ⇒ primary's lines or `Default`, and the banner says so.
- [ ] `ReportActionContext.Thresholds` is nullable-defaulted; the builder passes the LEAD's lines; every
      existing policy test is byte-identical under `Default`; the renderer fixture differs from its pre-212
      pin by the banner line only.
- [ ] Policy `weekly-report-action-v5`; rationale interpolates the applied line; the floor floors against
      the arm's line and never above Watch; the v11-shaped cases pass.
- [ ] The report states arm + lines + explicit/defaulted + version + the "operating thresholds, not
      validated evidence" sentence in exactly one place, rendered from the model.
- [ ] `default.json`: `disclosure-led-v11` carries `Labels { 20, 15 }` exactly; NO other arm carries
      `Labels`; if the audit's prevalence-matched Watch differs from 15 by > 1 the coder stopped and said so.
- [ ] `ScoringConfigFingerprintTests` unchanged; strategy-config files untouched; PR body says "spec 212
      moved nothing".
- [ ] `scripts/audit-label-thresholds.ps1` exists, is read-only, and its output is in the PR body.
- [ ] Operator guide, lifecycle header, architecture history (incl. the spec-187 in-place amendment) and
      the CLAUDE.md bullet updated; no wording anywhere calls the lines "calibrated evidence".
- [ ] `dotnet build` / full suite / `git diff --check` clean; actual dispatch→PR time in the PR body.

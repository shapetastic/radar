# Task: Completeness gates the "all clear" — capped coverage no longer silences the news-risk read

## Overview

The first live spec-179 shadow run (2026-08-22) produced **36 of 36 assessments as `IncompleteCoverage` and
made zero model calls.** Cause: the news collector caps at `Radar:News:MaxRecordsPerCompany` (25) per
company, actively-covered companies hit that cap routinely, spec-169 coverage records the cap contact, and
spec 179 §4 made "coverage complete/not capped" an admission gate on the whole input bundle. The sharpest
case: EOSE's supplied bundle held twelve point-in-time texts including "EOSE Stock Slides As Loss Widens And
Legal Scrutiny Mounts", "Legal Probes And Losses Rattle Traders" and "Down 11.8% After Tightening 2026
Revenue Outlook And Wider Q2 Loss" — and both readers refused to look, because the list might be missing
something.

The logic error is precise, and it is the recorded omission-bias doctrine's first concrete fix:
**completeness is required for absence claims, never for presence claims.** A going-concern story found in a
truncated list is valid evidence regardless of what was truncated; only "no risk found" needs the full list.
A risk screen that is silent about a company *because* it has too much news is inverted.

This slice changes admission, labelling and cohort bookkeeping only. No prompt change, no schema change, no
score/label/fingerprint change, no collector-cap change.

## Assignment

Worktree: any
Dependencies: spec 179 merged.
Estimated time: ~half a day.

## 1. Coverage becomes a recorded property of the assessment, not an admission gate

Amend the §4 input-bundle rule of spec 179:

- The bundle is built from every qualifying point-in-time observation that EXISTS for the candidate
  (time-window, cutoff and dedupe rules unchanged). Coverage/archive status no longer blocks bundle
  construction or the model call.
- The assessment record gains an explicit coverage classification, derived from the same spec-169/177 facts
  the old gate read:

```text
CoverageStatus   Complete | Truncated | CaptureUnproven
```

  `Truncated` = provider cap contact or result-limit-reached on the company's `newssearch` coverage;
  `CaptureUnproven` = the run's archive batch recorded failures or is missing. When both apply, record
  `CaptureUnproven` (the stronger caveat). The classification is persisted on every attempt, rendered in the
  live artifact, and carried into evaluator rows.
- A candidate with ZERO qualifying observations remains `InsufficientContent` — nothing here manufactures
  content.

## 2. The verdict rules — asymmetric by claim type, stated plainly

- **`ThesisChallenged` renders whenever validated claims survive, at every coverage status.** Under
  `Truncated`/`CaptureUnproven` the rendered section and the JSON carry the marker (e.g. "assessed over
  possibly-truncated coverage (provider cap contact)") beside the verdict — a caveat, never a suppression.
- **`NoRiskFoundInSuppliedText` renders as the clean all-clear ONLY under `CoverageStatus=Complete`** —
  spec 179 §7's rule, unchanged and now the ONLY thing completeness gates. When the model returns
  no-risk over `Truncated`/`CaptureUnproven` coverage, the stored verdict is kept but rendered as its own
  distinct state, e.g. **`NoRiskFoundInTruncatedCoverage`**, with wording that it is not an all-clear:

  > No risk was supported by the supplied text, but the supplied text is known to be incomplete. This is
  > not an all-clear.

  It must be visually and programmatically distinct from both the clean no-risk state and from
  `IncompleteCoverage` (which remains only for the genuinely-unassessable states: zero observations after
  the §1 rules, or a bundle-construction failure).
- `InsufficientContent`, `ValidationFailed` and provider-failure semantics are unchanged.

## 3. Evaluator: coverage is a cohort dimension, never a silent pool

- Evaluator rows carry `CoverageStatus`. The existing never-pool discipline extends to it: complete-coverage
  and truncated-coverage assessments are reported in separate tables/columns (like capture modes and
  readers), so the backtest can answer "does the truncated cohort predict as well as the complete one?"
  instead of hiding the distinction or re-muting the truncated cohort.
- The clean prospective table ADMITS truncated-coverage `ThesisChallenged`/`RiskScore` rows (they are frozen
  predictions like any other), segmented by the flag; `NoRiskFoundInTruncatedCoverage` rows are excluded
  from any "correctly found nothing" accounting — that claim was never made.

## 4. Out of scope, recorded not built

- Raising `Radar:News:MaxRecordsPerCompany` (25 → N). Deliberately separate: more collected articles means
  more MediaAttention signals, which moves Attention/Velocity component values on the live series — an
  evidence-volume decision for the maintainer, not a rider on a labelling fix. It also would not fix hot
  names, which hit any cap.
- Prompt/schema/reader changes; collector changes; any scoring or report-rank change.

## Files to inspect

- `src/Radar.Application/NewsRisk/` (bundle builder, assessment records, statuses)
- `src/Radar.Infrastructure/NewsRisk/` (renderers)
- the spec-179 evaluator and its cohort splits
- `src/Radar.Worker/RadarWorkerOptions.cs` (no new keys expected)
- the spec-179 tests for coverage gating (they pin the OLD behaviour and must be updated to pin the new)

## Tests

- EOSE-shaped fixture: capped coverage + risk-laden supplied texts ⇒ the model IS called; validated claims
  render as `ThesisChallenged` with the truncation marker; the assessment record carries
  `CoverageStatus=Truncated`.
- Capped coverage + model returns no-risk ⇒ renders `NoRiskFoundInTruncatedCoverage` with the not-an-all-clear
  wording; never the clean state.
- Complete coverage ⇒ behaviour byte-identical to pre-182 for all verdicts (the clean no-risk rule is
  untouched).
- Archive batch failure ⇒ `CaptureUnproven` classification, assessment still runs over persisted
  observations.
- Zero observations ⇒ `InsufficientContent`, unchanged.
- Evaluator: coverage cohorts never pool; truncated no-risk rows never count as correct absence.
- Both readers assess independently under the same coverage classification.

Do not run tests concurrently with another agent's solution-wide test run. At handoff,
`dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass in one
coordinated session.

## Acceptance criteria

- [ ] A company with capped news coverage receives a real model assessment; found risks render with the
      truncation caveat.
- [ ] The clean all-clear still requires complete coverage; no-risk over truncated coverage is a distinct,
      explicitly-not-clean state.
- [ ] Coverage status is persisted on every attempt and is a never-pooled evaluator cohort dimension.
- [ ] Zero-observation and validation-failure semantics unchanged; no score, fingerprint, prompt, schema,
      collector or cap change.
- [ ] Build and coordinated tests green.

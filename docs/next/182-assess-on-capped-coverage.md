# Task: Presence-claim unlock — readers run on any supplied text; no state is ever an "all-clear"

## Overview

The first live spec-179 shadow run (2026-08-22) produced **36 of 36 assessments as `IncompleteCoverage` and
made zero model calls**: the news collector's 25-article cap is routinely hit, spec-169 coverage records the
cap contact, and spec 179 §4 made completeness an admission gate on the whole input bundle. EOSE's supplied
bundle held twelve point-in-time texts including "Legal Scrutiny Mounts", "Legal Probes And Losses Rattle
Traders" and "Down 11.8% After Tightening 2026 Revenue Outlook And Wider Q2 Loss" — and both readers refused
to look, because the list might be missing something.

The doctrine fix (recorded omission-bias feedback): **completeness is required for absence claims, never for
presence claims.** And a second review finding sharpens it: even "complete" provider coverage does not mean
complete MODEL input, because the bundle is independently capped at `MaxArticlesPerCompany` (12) — in the
live run 17 of 18 companies supplied exactly 12 articles. Collection completeness, archive integrity and
model-input completeness are three separate dimensions; a single `CoverageStatus` enum conflating them would
let a 12-of-20 bundle render as "complete". Consequently **no state in this system is ever rendered as an
"all-clear"** — the absence verdict is permanently narrow: *nothing found in the supplied text*, with the
supplied text's known limitations stated beside it.

This slice changes admission, bookkeeping and rendering. No prompt change, no model-result-schema change, no
score/label/fingerprint change, no collector-cap change. The persisted assessment record and live artifact
schemas DO change and are honestly version-bumped.

## Assignment

Worktree: any
Dependencies: spec 179 merged.
Estimated time: ~half a day.

## 1. Readers run whenever at least one qualifying article exists

Amend spec 179 §4: the input bundle is built from every qualifying point-in-time observation that exists
(time-window, cutoff, dedupe and `MaxArticlesPerCompany` rules unchanged), and the readers are invoked
whenever the bundle is non-empty. Coverage and archive status no longer block bundle construction, the model
call, **or live body attachment** — `NewsRiskShadowGenerator`'s body-fetch path is currently gated on
`coverageComplete` (line ~244) and that gate is removed with the same justification: a fetched body for a
supplied article is more information, and more information never requires completeness.

Zero qualifying observations remains exactly the current **`NoContent`** status — this spec's earlier draft
misnamed it `InsufficientContent`/`IncompleteCoverage`; the implemented token is `NoContent` and it is
unchanged. `IncompleteCoverage` as a reader-blocking status disappears; the states it covered are recorded
as the §2 dimensions instead.

## 2. Three independent completeness dimensions, recorded — never collapsed, never given precedence

Each assessment attempt persists three orthogonal facts, derived from what `EvaluateCoverage` and the
spec-177 batch manifest already know:

```text
archiveCapture      Proven | Unproven
searchEnumeration   Complete | Truncated | Failed | Unproven
assessmentBundle    Complete | Capped
```

Rules:

- `searchEnumeration` must be TOTAL over the states the existing coverage evaluation distinguishes — missing
  coverage rows, missing feeds, feed failures, source failures and run/health mismatches all map to
  `Failed` or `Unproven` explicitly (enumerate the mapping in code beside `EvaluateCoverage`, with a test
  per input state). Provider cap contact / result-limit-reached maps to `Truncated`.
- `assessmentBundle` is `Capped` when qualifying observations were dropped by `MaxArticlesPerCompany` (or
  any future bundle bound); a bundle that holds every qualifying observation is `Complete`.
- The three dimensions are INDEPENDENT record fields. No precedence, no summary enum, no discarding one
  because another is worse. Renderers may summarise for layout, but the record keeps all three.
- All three render in the live artifact per assessment, and all three are evaluator row fields.

## 3. Verdict rules — the absence claim is permanently narrow

- **`ThesisChallenged` renders whenever validated claims survive, under every combination of the three
  dimensions**, with the dimensions stated beside it. A caveat, never a suppression.
- **`NoRiskFoundInSuppliedText` means exactly its name and nothing more, always.** The "all-clear" framing
  is removed from the spec, the renderer and the artifact wording entirely — there is no state in which
  Radar asserts a company is clean. When all three dimensions are at their best (`Proven` + `Complete` +
  `Complete`), the rendered wording is still scoped to the supplied text; when any dimension is degraded,
  the degradation is stated:

  > No risk was supported by the supplied text. Supplied text is known to be incomplete
  > (search truncated; bundle capped at 12) — this is a statement about 12 articles, not about the company.

- **The raw model verdict and the coverage-derived presentation are stored separately.** The cache key
  remains model/prompt/schema + ordered input-bundle hash and caches ONLY the raw verdict; the presentation
  state is derived per run from that run's three dimensions, so a cached verdict replayed under different
  coverage circumstances cannot carry a stale derived status.
- `ValidationFailed` and provider-failure semantics are unchanged.

## 4. Evaluator: presence and absence claims are separate cohorts; nothing named "clean" admits caveats

- Evaluator rows carry the three dimensions as never-pooled cohort columns (joining capture mode, reader and
  the existing splits).
- The former "clean prospective" table is split and renamed: a **presence-claim cohort** (validated
  `ThesisChallenged`/`RiskScore` rows — admitted at any completeness, segmented by the dimensions) and an
  **absence-claim cohort** (`NoRiskFoundInSuppliedText` rows — admitted ONLY when all three dimensions are
  at their best, because only there does "found nothing" carry evidential weight). No table whose name
  implies cleanliness may contain caveated rows.
- Absence rows with any degraded dimension are excluded from every "correctly found nothing" accounting —
  that claim was never made.

## 5. Out of scope, recorded not built

- Raising `Radar:News:MaxRecordsPerCompany` or `MaxArticlesPerCompany` — evidence-volume decisions that move
  Attention/Velocity components and model cost; each is a separate deliberate maintainer choice, and no cap
  fixes hot names anyway (a hot name saturates any cap; the dimensions record it honestly instead).
- Strategy lifecycle / outcome governance (validation states, promotion/retirement rules) — its own spec.
- The AD benchmark-adjusted forward-return repair (`ForwardReturn` computes raw returns; the architecture
  decision at docs/architecture-decisions.md:1409 requires benchmark adjustment) — its own spec, needed
  before the 2026-09-29 claim boundary admits observations.
- Prompt/schema/reader changes; collector changes; any scoring or report-rank change.

## Files to inspect

- `src/Radar.Application/NewsRisk/NewsRiskShadowGenerator.cs` (EvaluateCoverage, the line-~244 body gate)
- `src/Radar.Application/NewsRisk/NewsRiskInputBundle.cs` (the MaxArticlesPerCompany cap — the bundle must
  report whether it capped)
- `src/Radar.Application/NewsRisk/NewsRiskAssessmentRecord.cs` (schema version bump + the three dimensions)
- the spec-179 live renderer and evaluator cohort splits
- the spec-179 tests pinning the OLD gate (they must be updated to pin the new behaviour)

## Tests

- EOSE-shaped fixture: truncated search + capped bundle + risk-laden texts ⇒ readers ARE called; validated
  claims render `ThesisChallenged` with all three dimensions stated; record carries
  `Truncated`/`Capped`/`Proven` independently.
- Every `EvaluateCoverage` input state maps to exactly one `searchEnumeration` value (total mapping test).
- Bundle with more qualifying observations than the cap ⇒ `assessmentBundle=Capped` even when search
  enumeration is `Complete` — the two dimensions are demonstrably independent.
- No-risk verdict renders the narrow wording at every dimension combination; the literal string "all clear"
  (any casing) appears nowhere in renderer output — asserted.
- Cached raw verdict replayed under different dimensions derives that run's presentation, not the cached
  run's.
- Body attachment proceeds under degraded coverage; fetch outcomes recorded as before.
- `NoContent` for zero observations — byte-identical to current behaviour.
- Evaluator: presence and absence cohorts never pool; absence rows require best-state dimensions; no
  cleanliness-named table contains caveated rows.
- Both readers assess independently under identical recorded dimensions.

Do not run tests concurrently with another agent's solution-wide test run. At handoff,
`dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass in one
coordinated session.

## Acceptance criteria

- [ ] Any company with at least one qualifying article receives a real assessment from every reader; found
      risks render with the three completeness dimensions stated.
- [ ] Archive capture, search enumeration and bundle completeness are recorded independently on every
      attempt; no precedence collapsing; the search-enumeration mapping is total over the existing coverage
      states.
- [ ] No rendered state anywhere reads as an all-clear; the absence verdict is permanently scoped to the
      supplied text.
- [ ] Raw verdicts and derived presentation are stored separately; cache reuse cannot carry a stale derived
      status.
- [ ] Presence-claim and absence-claim evaluator cohorts are separate; absence claims require best-state
      dimensions.
- [ ] `NoContent`, validation-failure and provider-failure semantics unchanged; assessment/artifact schemas
      version-bumped; no score, fingerprint, prompt, model-schema, collector or cap change.
- [ ] Build and coordinated tests green.

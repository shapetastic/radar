# Task: In-process news-risk shadow read and frozen-assessment backtest

## Overview

Spec 177 preserves immutable point-in-time news text; spec 176 exposes every strategy's current live leaders.
This slice joins those two completed facts into a bounded shadow diagnostic:

> For companies surfaced by a live Research strategy, does contemporaneous third-party reporting support a
> financing, dilution, solvency, execution or credibility risk that Radar's company-disclosure score missed?

EOSE is the motivating failure case: company disclosures and filing activity can look constructive while the
external story contains financing, dilution, cash-runway or execution risks. CASS and MSEX are related known
development examples. All three were examined before this spec, so they may exercise the feature but cannot
serve as validation evidence.

This slice does not change a score or label. It creates a frozen, cited assessment visible immediately and a
separate read-only evaluator that can later join that frozen predictor to forward prices.

## Assignment

Worktree: any  
Dependencies: spec 176 merged (purpose + live strategy sections); spec 177 merged (observation archive and
safe content reader).  
Estimated time: ~1–2 days.

## 1. Three states remain distinct

| State | Future price required? | Meaning |
| --- | --- | --- |
| Live strategy selection | No | Exact company/strategy/rank rows already built for the weekly report. |
| Shadow news-risk assessment | No | Model-supported diagnostic over cited text known by its cutoff. |
| Exploratory outcome row | Yes | Later relation between the frozen assessment and a forward outcome. |

The live artifact carries:

> News-risk assessments are shadow diagnostics over the cited text available at the stated cutoff. They do
> not alter Radar scores or labels, and absence of a detected risk is not evidence that a company is safe.

The evaluator carries:

> This is exploratory development evidence, not an AD-15 or AD-16 result. Retrospectively retrieved content
> is reported separately and is never treated as point-in-time content at the article's publication date.

No score, price, future return, eventual Radar label or efficacy result enters candidate selection, article
selection, model input, cache identity or assessment.

## 2. Execution model and exact structured row source

The shadow read is an **in-process Worker step after `IRadarPipeline.RunAsync` returns**, following the
efficacy-generator architecture: outside `IRadarPipeline`, but still in the same DI scope/process. At that
point the company report and durable `PipelineRunRecord` have been written, while the exact spec-176 strategy
sections are still available.

Do not parse Markdown and do not reopen/re-rank strategy score files.

Thread the already-built result explicitly:

1. Add a trailing optional `StrategySections` to `WeeklyReportResult`; `WeeklyReportBuilder.GenerateAsync`
   returns the exact `strategySections` it already built and rendered.
2. Add trailing optional `RunId` and `StrategySections` to `RadarPipelineResult`. `RadarPipelineRunner`
   generates the run id once, writes that exact id to `PipelineRunRecord`, and returns it with the report's
   exact section instances after the run record is durable.
3. Change the Worker's private `RunPipelineAsync` helper to return `RadarPipelineResult`.
4. Invoke optional `INewsRiskShadowGenerator.GenerateAsync(runId, strategySections, ct)` before the existing
   efficacy step. It is registered only for unfiltered full mode when Shadow is enabled and AI exists.

This is an additive transport change only: no second strategy read, no second rank construction and no report
output change. `StrategyReportSection.Rows` remains the one candidate source. Shadow enabled with
`GenerateReport=false` fails startup naming that the structured sections are produced by report construction;
single-strategy/null sections yield a named `NoLiveStrategySections` diagnostic rather than invented rows.

Persist the selected candidates—including every selecting strategy, within-strategy rank, snapshot id and
selection cutoff—inside the shadow assessment output. The later evaluator reads that frozen selection and
never derives candidates again.

## 3. Select live candidates without manufacturing consensus

Candidate traversal is deterministic:

1. `Purpose=Research` sections only; Comparators are excluded.
2. Primary first, then remaining Research sections in their existing configured order.
3. Take the first five existing evidence-linked rows per section, in existing rank order.
4. Deduplicate by company id while retaining every strategy/rank/snapshot that selected it.
5. Stop at `MaxCompaniesPerRun` (default 30) in that traversal order.

This is a cost budget, not a merged rank. Repeated selection by related arms is displayed as shared ancestry,
not independent corroboration. No consensus count, average rank, Borda score or cross-strategy score comparison
is created.

`selectionAsOfUtc` is the exact `PipelineRunRecord.CreatedAtUtc`/snapshot cutoff of the completed run. A
candidate cannot be selected from a report row belonging to another run.

## 4. Build a point-in-time input bundle

For candidate C selected at D, start from spec-177 observations satisfying:

- `companyId == C`;
- `firstObservedAtUtc <= D` and `retrievedAtUtc <= D`;
- `publishedAtUtc` is null or `publishedAtUtc <= D`;
- observation time falls in `(D - LookbackDays, D]` (default 30 days); and
- the exact run's spec-169 company `newssearch` coverage and spec-177 archive batch are complete/not capped.

Remove only the already-defined Google publisher suffix for headline comparison, collapse exact duplicate
normalized headlines, order newest first then observation id, and take at most `MaxArticlesPerCompany`
(default 12). Keep publisher diversity and exact ids visible.

When article fetching is enabled under spec 177, attempt at most `MaxFetchedArticlesPerCompany` (default 3),
newest first, through its allowlisted safe reader. A fetched body is known only at its actual retrieval time E.
If supplied to the model:

- `assessmentCutoffUtc = max(D, every supplied input retrievedAtUtc)`;
- the assessment may render immediately after E; and
- every later price outcome anchors at E, never at selection time D.

RSS-only input known by D retains D as the assessment cutoff. Never move a cutoff backward or attach a newly
fetched page to the article's older publication/collection time.

## 5. Shadow thesis-breaker schema

Add provider-neutral `INewsRiskAnalyzer` over the existing `IChatClient` seam. It receives company name/ticker
plus ordered, id-labelled input text. It receives no Radar score/rank/label, price, future outcome or uncited
company background.

Closed result:

```text
Assessment     ThesisChallenged | NoRiskFoundInSuppliedText | InsufficientContent
RiskScore      integer 0..100 when sufficient, else null
Categories[]   LiquidityOrGoingConcern | DilutionOrFinancingDependence | DebtOrCovenant |
               DelistingOrReverseSplit | ExecutionOrMissedMilestone | GuidanceCredibility |
               UnitEconomicsOrMargin | RegulatoryOrLegalSetback |
               CustomerOrRevenueConcentration | GovernanceOrRelatedParty | OtherSpecifiedRisk
Claims[]       category, severity, confidence, observationIds, excerpts
Rationale
```

Prompt requirements:

- assess only risks supported by supplied text;
- distinguish a financing facility from actual issuance/dilution and a historical loss from a current
  going-concern statement;
- management/company statements are claims, not verified facts;
- ordinary negative words are not automatically thesis-breaking;
- conflicting evidence remains visible;
- insufficient text returns `InsufficientContent`, not a low score; and
- never recommend buying, selling, shorting or holding.

## 6. Mechanical validation and exact meaning of archived text

For an assessment, **archived text** means the union of the exact fields actually supplied to the model for
that observation:

- headline;
- `descriptionText`, when supplied; and
- permitted extracted publisher body, when supplied.

It excludes raw HTML, metadata, URLs and any field omitted from that call. Validate:

- every cited observation id was supplied;
- every excerpt is an exact ordinal substring of at least one supplied text field for that observation;
- enum values, score, severity and confidences are in range;
- advice-language guard passes; and
- `ThesisChallenged` retains at least one supported category.

Exact substring validation is intentionally strict; model whitespace normalization may cause real claims to
drop. Record total/accepted/dropped claim counts and drop reasons so that rate is measurable. Do not normalize
an excerpt until it matches. If all claims fail, store `ValidationFailed`/`InsufficientContent`, never
`NoRiskFoundInSuppliedText`.

Persist every attempt—including no content, incomplete coverage, provider error, parse error and validation
failure—with:

- durable run id, selection and assessment cutoffs;
- selecting strategy/rank/snapshot provenance;
- ordered observation ids and payload/body hashes;
- coverage/archive completeness;
- provider/exact model id;
- prompt/schema/fetch/extractor versions;
- raw bounded-response hash;
- status and validated result; and
- creation time.

Cache by model/prompt/schema identity plus ordered input-bundle hash. A policy or model change creates a new
cohort and never overwrites/reuses an incompatible assessment.

## 7. Live artifact and fail-closed absence

Write `data/news-risk/live/news-risk-{asOfDate}.md` and `.json`. Each selected company shows:

- selecting strategy/rank(s) and snapshot ids;
- selection and assessment cutoffs;
- assessment/risk score or exact non-result status;
- categories and validated supporting excerpts;
- headline, publisher, URL and whether input was headline, RSS description or permitted publisher body; and
- coverage/archive/fetch/model/validation warnings.

Render `NoRiskFoundInSuppliedText` only when company `newssearch` coverage and archive batch are complete,
input is sufficient, and the analyzer/validator completed successfully. Everything else is unknown/failure.

The step runs after the normal report and cannot rewrite its labels/ranks. A shadow failure writes a named
failed artifact and does not roll back or relabel the already-durable Radar run. A later report may link to the
artifact; it must never wait for the model call.

## 8. Known development examples

Commit `docs/cohorts/news-risk-development.json` naming at least EOSE, CASS and MSEX with the date/reason each
was inspected. They still appear in live diagnostics, but evaluator rows are marked `KnownDevelopmentExample`
and excluded from its clean prospective table. The exclusion file is read directly; git history is not the
declaration mechanism.

This prevents a successful EOSE warning from being presented as evidence for a feature designed because EOSE
was already known.

## 9. Read-only frozen-assessment evaluator

Add a separate read-only audit command/generator over persisted assessments, development declarations and the
existing price store. It does not select companies, fetch URLs or invoke AI.

One row is one frozen company/run assessment and contains:

- run/selection/assessment/model/prompt/input identities;
- selecting strategies/ranks;
- capture mode and completeness;
- assessment, `RiskScore` and categories;
- entry price resolved at **`assessmentCutoffUtc`** (never `selectionAsOfUtc`);
- 21-day forward return using existing `ForwardReturn.TryCompute`/spec-152 tolerance semantics; and
- 21-day maximum adverse close move from that same resolved entry close, requiring a complete window.

The clean prospective table requires:

- assessment at/after spec-177 `boundary.json`;
- successful `ProspectiveRss` batch and complete company coverage;
- completed/validated assessment;
- not a known development example; and
- fully resolved forward window.

Report company/date counts, every exclusion reason, per-date/date-block associations between `RiskScore` and
adverse move, flagged/non-flagged descriptive returns/drawdowns, and tie/constant-predictor frequency.
`LegacyHeadlineOnly` and `RetrospectiveUrlFetch` stay in separate development tables and never pool with the
clean cohort.

No pass/fail threshold, promotion rule or alpha claim is declared here. If useful, a later spec declares a
new strategy, one predictor/outcome/threshold and first eligible date before outcomes exist.

## 10. AD-14 and architecture boundary

The live shadow generator reads no price. Only the §9 evaluator may reference price/outcome abstractions, and
it lives alongside other read-side audit/efficacy code—not under Scoring or Pipeline.

Add an architecture test mirroring `EfficacyReadOnlyGuardrailTests`:

- `Radar.Application.Scoring`, evidence/signal pipeline and score-formula dependency closures contain no
  NewsRisk namespace/type;
- the live NewsRisk generator contains no price repository/reader/resolver dependency; and
- the evaluator may depend on frozen assessments + price, but no scoring/pipeline type may depend back on it.

## 11. Configuration and shipped posture

Extend spec 177's fail-closed block:

```json
{
  "CaptureRss": true,
  "ObservationDirectory": "data/news-observations",
  "Shadow": {
    "Enabled": true,
    "OutputDirectory": "data/news-risk",
    "LookbackDays": 30,
    "MaxCompaniesPerRun": 30,
    "MaxArticlesPerCompany": 12,
    "MaxFetchedArticlesPerCompany": 3
  },
  "ArticleFetch": {
    "Enabled": false,
    "AllowedDomains": []
  }
}
```

Unknown keys/invalid limits fail startup. Enable shadow in live `default.json`; register it only when the
existing AI provider is configured. Article fetching remains off until an explicit domain/storage decision.
`run-radar.ps1 -WhatIf` prints the gates and paths.

These limits are cost/safety controls, recorded in assessments and hashed into no scoring fingerprint.

## Files to inspect

- `src/Radar.Application/Reporting/WeeklyReportResult.cs`
- `src/Radar.Application/Reporting/WeeklyReportBuilder.cs`
- `src/Radar.Application/Reporting/StrategyReportSection.cs`
- `src/Radar.Application/Pipeline/RadarPipelineResult.cs`
- `src/Radar.Application/Pipeline/RadarPipelineRunner.cs`
- `src/Radar.Worker/Worker.cs`
- spec-177 archive/content-reader/store primitives
- `src/Radar.Application/Ai/IChatClientFactory.cs`
- `src/Radar.Infrastructure/Filings/AdviceLanguageGuard.cs`
- `src/Radar.Application/Efficacy/Comparison/ForwardReturn.cs`
- `src/Radar.Application/Efficacy/` date-block/price-resolution primitives
- `src/Radar.Worker/RadarWorkerOptions.cs`
- `src/Radar.Worker/RadarWorkerServices.cs`
- `scripts/run-radar.ps1`
- `scripts/run-profiles/default.json`

## Tests

- `WeeklyReportResult`/`RadarPipelineResult` carry the exact section instances and exact durable run id;
  shadow makes no score repository/file-store read and performs no ranking.
- Candidate traversal is primary then Research order, five per arm, deduped/capped; Comparators never enter.
- Candidate selection provenance is persisted and the evaluator never re-derives it.
- Shadow enabled without report sections fails/skips exactly as specified; filtered/replay/score/collect modes
  never run it accidentally.
- Model input contains no score, rank, price, future outcome or post-cutoff text (rank remains output
  provenance only, not prompt content).
- Fetched-body cutoff becomes actual retrieval; evaluator entry anchors that cutoff, not run time.
- Exact-substring validation accepts headline/description/body excerpts actually supplied and rejects
  fabricated/omitted-field citations; drop rates persist.
- Incomplete coverage/archive/model/validation states cannot render clean/no-risk.
- Model/prompt/schema/input changes create distinct records/cache entries.
- Development, legacy, retrospective and prospective evaluator cohorts never pool.
- Partial forward windows and unresolved prices fail closed through reused efficacy primitives.
- AD-14 architecture test pins that live analysis cannot read price and Scoring/Pipeline cannot reference it.

Do not run tests concurrently with another agent's solution-wide test run. At handoff,
`dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass in one coordinated
session.

## Acceptance criteria

- [ ] Shadow analysis consumes the exact spec-176 structured rows in-process after the report/run record,
      without parsing Markdown, reopening score stores or reconstructing ranks.
- [ ] Candidate selection and every strategy/rank/snapshot provenance fact are frozen in the assessment.
- [ ] Only point-in-time text at/before assessment cutoff enters the model; fetched-body and outcome anchors
      use actual retrieval/assessment time.
- [ ] Every risk claim is mechanically tied to exact text actually supplied to the model.
- [ ] Missing coverage/content/model output never becomes a low/no-risk result.
- [ ] EOSE/CASS/MSEX remain visible development examples but cannot support the clean prospective table.
- [ ] The evaluator reads frozen assessments + prices only and never reruns selection, fetching or AI.
- [ ] No score, label, strategy, fingerprint or AD-15/AD-16 claim changes.
- [ ] Build and coordinated tests green.

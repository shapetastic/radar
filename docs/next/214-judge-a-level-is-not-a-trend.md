# Task: The judge stops reading a level as a trend — deterministic comparison-basis on every supplied fact, a prompt rule, and a fail-closed materializer gate

## Overview

On the 2026-09-07 run the stage-2 judge read Argan (AGX) as `Improving` with the rationale "Backlog reached
$2.5B, indicating strong future demand." Backlog was $2.93B on 2026-01-31 and ≈$2.8B on 2026-04-30 — it has
fallen 14% this year (`docs/cohorts/skeptic-review-agx-2026-09-08.md`). The judge was not wrong about what
it was given; it was given a LEVEL and treated it as a TREND. Verified against the store:

- The judgment record (`data/news-risk/judgments/…/928eb9f8-380b-7838-e80d-ad9283cbf066.json`) cites four
  trajectory facts; the backlog one is the typed statement "Power projects lift Argan (NYSE: AGX) as backlog
  hits $2.5B" (EventTypes `ContractOrCustomerWin, OtherSpecified`, TemporalScope "not stated"). That is the
  ONLY backlog fact Radar holds for AGX: the typing store begins 2026-08-26 and AGX's 67 typing records carry
  no earlier backlog value. `familyBundle: Capped`, `searchEnumeration: Failed`.
- Nothing in Radar could have supplied the comparison. The 8-K's raw evidence is a 145-character stub
  (`data/evidence/raw/filing/2026/09/b31c119a….json`, `rawText` = "8-K filing accession … Items: Results of
  Operations…"); the EX-99.1 body is read live by `ChatFilingAnalyzer` and never persisted (the ai-debug
  record keeps a 2,000-char `inputHead`). Spec 215 addresses the supply side; THIS spec makes the judge
  honest about what a level can and cannot establish, whether or not a comparison is available.
- The judge prompt (`ChatNewsJudgmentAnalyzer`, rules 1–10) says "make the best directional call the
  supplied BUSINESS facts support" and "never infer a direction from what the facts fail to mention" (rule
  3) — but no rule says that a stock quantity stated without a comparison establishes no direction. Rule 3
  is about absence of facts; this is about the presence of a number that carries no direction.

**Measured (2026-09-08, read-only over `data/news-typing/typings/**` and `data/news-risk/judgments/**`):**
5,263 typed facts; 1,171 (22.2%) carry a comparison phrase under a crude regex (record / up from / vs /
%, grew, fell, beat, …); 269 Judged judgments, 182 directional; **27 of 182 (14.8%) cite ONLY facts with no
comparison phrase** — an UPPER bound for this defect, because the crude regex also flags genuinely
directional EVENT facts ("Aehr receives $22M follow-on order", "Middlesex Water lifts revenue and profit on
rate hikes") that need no comparison to carry direction. The rule below is therefore scoped to STOCK
quantities (balances), not to events, and §3 requires the scoped classifier's live distribution before it
ships — the 14.8% is what it must be measured against, not the number it will produce.

## Assignment

Worktree: any. Dependencies: main at `d8794f3` or later. Use `run-next.ps1 -Spec 214`. **Ship with spec 215
back-to-back (merge both before the next baseline) so the AI-ON identity boundary moves ONCE** — see §5.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. `ComparisonBasis` — a deterministic, hashed classification of every supplied fact

New `Radar.Application.NewsRisk.Judgment.StatementComparisonClassifier` (`comparison-basis-v1`), pure and
static, applied at judge-INPUT time to each family's representative statement (never at typing time — the
stage-1 cohort key is untouched, so NO re-typing of the ~2,000 in-window observations is triggered; the
350-call/run typing budget would take days to drain a re-type):

- Output enum `NewsFactComparisonBasis { Event, StatedComparison, LevelOnly, NotQuantified }`:
  - `NotQuantified` — no number/currency/percentage in the statement.
  - `StatedComparison` — a number AND a comparison marker (a closed phrase table in the classifier:
    record / all-time / up from / down from / compared / versus / vs / increase / decrease / grew / rose /
    fell / declined / higher / lower / year-over-year / sequential / from X to Y / beat / missed / above /
    below / doubled / halved / raised / cut / wider / narrower / percentages, …). The table is the
    classifier's identity — changing it is `comparison-basis-v2`.
  - `LevelOnly` — a number attached to a STOCK-QUANTITY noun (backlog / order book / pipeline / cash /
    cash and investments / debt / headcount / employees / market cap / shares outstanding / assets /
    book value / capacity / fleet / stores / subscribers / users — a closed noun table, likewise identity)
    with NO comparison marker.
  - `Event` — a number with neither: an order, award, contract, financing, acquisition, launch — the
    quantity sizes an event that is directional in itself. `EventTypes` is a tie-breaker only
    (`EarningsOrGuidance` with a bare number and no stock noun → `LevelOnly`, because a bare earnings
    figure is a level).
- Rendered to the judge per family as one line: `ComparisonBasis: LevelOnly — a stated level, not a
  trend` / `StatedComparison` / `Event` / `NotQuantified`. Persisted on the judgment record per consumed
  family (additive field, nullable = written pre-214, never defaulted).
- The classifier's version token joins the judgment cohort key beside `families=` (it is an input the model
  sees), so it is hashed into `ScoringConfigVersion` through the spec-194 §2 `news=` segment.

## 2. Prompt rule 11 and the trajectory-basis validator; prompt v3 → v4, materializer v2 → v3

- **Prompt rule (11)**, `news-judgment-prompt-v3 → v4`: "A quantity stated as a LEVEL — a balance such as
  backlog, cash, debt, headcount, capacity — establishes NO direction by itself, however large. Only a
  supplied fact that states the comparison (prior value, change, record, beat/miss) or an EVENT fact
  (an order, award, contract, launch, financing) can be cited in TrajectoryFactIds. A LevelOnly fact may be
  cited in a finding's FactIds as context, never as trajectory support. If the only quantified facts are
  levels, say so in the Rationale and answer Unknown." The response schema (`news-judgment-schema-v3`) is
  unchanged by this spec.
- **Validator** (`NewsJudgmentValidator`): after fact-id resolution, compute `trajectoryBasis` over the
  cited `TrajectoryFactIds`: `Supported` when ≥ 1 cited fact is `StatedComparison` or `Event`; `LevelOnly`
  when every cited fact is `LevelOnly`/`NotQuantified` and the trajectory is Improving/Deteriorating.
  `LevelOnly` is NOT a validation failure (the judgment is persisted, `status: Judged`, the model's read is
  kept verbatim — a wrong call recorded beats a call rewritten) — it is a persisted marker
  (`trajectoryBasis`, additive, nullable pre-214) plus a per-run aggregated count.
- **Materializer gate** (`NewsJudgmentSignalMaterializer`, `news-judgment-signal-v2 → v3`): a judgment
  with `trajectoryBasis == LevelOnly` mints NO signal, counted under a new
  `NewsJudgmentSignalSkipReason.LevelOnlyTrajectory` (rendered by `DescribeSkips` in the daily news
  report's accounting and in the live artifact, beside `not-judged` / `non-directional-trajectory`).
  Fail-closed: a direction with no directional basis produces no scoring input. v2 stays in
  `SupportedJudgmentSignalVersions` (accrued v2 signals remain valid, exactly the spec-197 v1→v2 pattern;
  an existing valid v2 id is prior-version occupancy and mints no v3 duplicate).
- **Nothing is rewritten.** Accrued judgments keep their trajectory; accrued v2 signals stay on disk and in
  scoring (AD-8). The AGX judgment of 2026-09-07 is history; the first post-214 run re-judges AGX once
  (the cohort key forks) and that re-judgment either cites the record-revenue facts (StatedComparison →
  Supported) or not — the outcome is recorded in the PR body, not predicted.

## 3. Live distribution BEFORE it ships (the CLAUDE.md rule)

A read-only harness (`tests/…/ComparisonBasisLiveMeasurementTests`, env-gated like
`NewsRecencyWindowLiveMeasurementTests`) over the store at implementation time, reported in the PR body:

| measure | value |
| --- | ---: |
| typed facts by `ComparisonBasis` (Event / StatedComparison / LevelOnly / NotQuantified) | counts + shares |
| directional judgments whose cited trajectory facts are ALL LevelOnly/NotQuantified (would-be `LevelOnly`) | n of 182 (crude upper bound 27) |
| of those, how many currently HAVE a materialized v2 signal (i.e. would have minted nothing under v3) | n |
| the AGX 2026-09-07 judgment's four cited facts, classified | 4 rows |

**A classifier that puts > 30% of quantified facts in `LevelOnly`, or < 5% in `StatedComparison`, is a
defect in the noun/phrase tables, not a finding** — retune the tables (still v1, it has not shipped) and
re-measure. The `LevelOnly` share of judgments is expected to be well under the crude 14.8%; if it is
ABOVE it, the tables are over-broad.

## 4. Report and docs

- Weekly report, judgment provenance appendix: each judgment row gains `basis: Supported | LevelOnly |
  (pre-214)`. Daily news report: the new skip reason in the accounting line.
- `docs/reading-radar-output.md`: one paragraph under the semantic-read marker — a level is not a trend;
  what `LevelOnly` means on the appendix; that such judgments mint no signal.
- `docs/architecture-history.md`: spec-214 bullet (the AGX misread, the classifier, the rule, the gate, the
  measured distribution); amend the spec-194 §1.2 bullet IN PLACE to add the `LevelOnlyTrajectory` skip
  beside "Mixed/Unknown mint nothing".
- CLAUDE.md "News is a two-stage read" bullet: amend in place — `news-judgment-signal-v3`, and one clause
  "a level-only trajectory mints nothing (spec 214)".

## 5. Identity boundary and operator step — ONCE, with spec 215

Prompt v4, materializer v3 and `comparison-basis-v1` all enter the `news=` segment, so the **AI-ON pins move**
(60d live / 120d / 30d unit); the AI-OFF three MUST NOT move (the disabled descriptor carries none of these —
spec 197's proof pattern; an AI-OFF move is scope leakage). Spec 215 moves the same pins again (prompt v5,
schema v4). **Merge 214 and 215 back-to-back before the next baseline** so the operator step — consciously
delete/re-record `data/scoring-configs/strategies/{name}.json` (git-ignored, never fabricated) and verify the
first run's stamp against `ScoringConfigFingerprintTests` — happens ONCE and the accrued series takes ONE
discontinuity, not two. Expected one-time cost: every candidate company is re-judged once (~19 calls).
`ScoringConfigFingerprintTests` pins move for the AI-ON side only; the PR body states the six values by
citing the test, never by transcribing them into prose.

## Non-goals

- Supplying prior-period values (that is spec 215). This spec makes the judge honest WITHOUT them.
- Changing stage-1 typing (schema, prompt, cohort) — deliberately avoided; see §1.
- Re-judging or re-materializing history; touching accrued v2 signals; any scoring weight or formula.

## Acceptance criteria

- [ ] `StatementComparisonClassifier` (`comparison-basis-v1`) is pure, closed-table, unit-tested on each
      of the four classes with the AGX backlog statement → `LevelOnly`, "record revenue of $384 million" →
      `StatedComparison`, "$22 Million Follow-On Order" → `Event`, a bare-number earnings statement →
      `LevelOnly`; its token is in the judgment cohort key and thus the `news=` segment.
- [ ] Every supplied family renders its `ComparisonBasis` line; prompt v4 carries rule 11 verbatim in
      spirit; schema v3 unchanged.
- [ ] `trajectoryBasis` persisted (nullable pre-214) and computed only over resolved cited facts; a
      `LevelOnly` judgment stays `Judged` and mints nothing under `news-judgment-signal-v3`, counted as
      `LevelOnlyTrajectory` in the run summary, the daily report accounting and the live artifact; v2 stays
      supported and re-mints nothing.
- [ ] §3 live distribution in the PR body with the AGX four-fact classification; the sanity bounds hold.
- [ ] Appendix `basis` column, operator-guide paragraph, history bullets (new + the 194 in-place amendment),
      CLAUDE.md bullet amended in place.
- [ ] AI-ON pins moved and asserted by `ScoringConfigFingerprintTests`; AI-OFF pins proven unchanged; PR body
      carries the operator step and the back-to-back-with-215 instruction.
- [ ] `dotnet build` / full suite / `git diff --check` clean; actual dispatch→PR time in the PR body.

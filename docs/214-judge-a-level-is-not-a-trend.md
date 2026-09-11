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

- Output enum `NewsFactComparisonBasis { StatedComparison, LevelOnly, Event, NotQuantified }`, decided by
  PRECEDENCE — the first rule that matches wins, and a number is NOT required for the two directional
  classes (review round 1: a numberless statement can state a comparison or an event, and a bare
  percentage is a level, not a comparison):
  1. **`StatedComparison`** — explicit comparison/trend language, with or without a number: a closed
     phrase table (record / all-time / up from / down from / compared / versus / vs / increase /
     decrease / grew / rose / fell / declined / higher / lower / wider / narrower / year-over-year /
     sequential / from X to Y / beat / missed / above / below / doubled / halved / raised / cut /
     reaffirmed / …). "Backlog declined during the quarter" → StatedComparison. **A bare percentage
     ("gross margin was 24%") is NOT a comparison marker** — a percent sign never qualifies on its own;
     only the phrase table does.
  2. **`LevelOnly`** — a quantified STOCK or FLOW metric with no comparison marker: a number attached to
     a metric noun from a closed table (stock: backlog / order book / pipeline / cash / cash and
     investments / debt / headcount / employees / market cap / shares outstanding / assets / book value /
     capacity / fleet / stores / subscribers / users; flow: revenue / sales / net income / earnings / EPS /
     margin / operating income / cash flow / bookings), and a bare number or percentage on an
     `EarningsOrGuidance` statement. "Backlog hits $2.5B" and "gross margin was 24%" → LevelOnly.
  3. **`Event`** — a recognised event, with or without a number: an order / award / contract / customer
     win / approval / clearance / launch / acquisition / financing / listing / delisting / recall /
     lawsuit filed or settled, from a closed event-verb table with `EventTypes` as a tie-breaker
     (`ContractOrCustomerWin`, `RegulatoryOrLegal`, `ProductOrTechnology`, `MergerAcquisitionOrStake`,
     `FinancingOrDilution` statements that name such an event). "The company won a major government
     contract" and "The FDA approved the product" → Event; "$22 Million Follow-On Order" → Event.
  4. **`NotQuantified`** — none of the above.
  Every table matches WHOLE words/phrases only (token-boundary, case-insensitive, no substring hits): `vs`
  must not match "investors", `record` must not match "recorded", `cut` must not match "cutting-edge",
  `rose` must not match "Rosetta" — pinned by negative tests. The three tables and the boundary rule are
  the classifier's identity — changing any of them is `comparison-basis-v2`.
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
  cited in a finding's FactIds as context, never as trajectory support. Answer Unknown ONLY when no
  supplied fact is StatedComparison or Event — a numberless comparison ("backlog declined") or a
  numberless event ("the FDA approved the product") beside a quantified level still establishes
  direction; when that is the case, say in the Rationale which levels you set aside." The response
  schema (`news-judgment-schema-v3`) is unchanged by this spec. **(Spec 221's prompt v7 withdrew the
  "Answer Unknown ONLY when no supplied fact is StatedComparison or Event" clause: when no BUSINESS
  StatedComparison/Event fact is supplied the judge answers `NoBusinessSignal` or — only when a supplied
  business fact bears on a direction it cannot resolve — `Unknown`, per rule 2; and a basis label describes
  wording, not subject. See the spec-221 bullet in `docs/architecture-history.md`.)**
- **Validator** (`NewsJudgmentValidator`): after fact-id resolution, compute `trajectoryBasis` ONLY for a
  current, `Judged`, DIRECTIONAL record (BusinessTrajectory Improving or Deteriorating), over the cited
  `TrajectoryFactIds`: `Supported` when ≥ 1 cited fact is `StatedComparison` or `Event`; `LevelOnly` when
  every cited fact is `LevelOnly`/`NotQuantified`. For every other record — Mixed, Unknown, a validation
  failure, an insufficient-facts or provider-failure attempt — the field is null, meaning "not
  applicable"; it is also null on every pre-214 record. The two nulls are distinguishable by what the
  materializer checks first: status and direction gate BEFORE the basis gate (as today), so a null that
  REACHES the basis gate is, by construction, a pre-214 directional record.
  `LevelOnly` is NOT a validation failure (the judgment is persisted, `status: Judged`, the model's read is
  kept verbatim — a wrong call recorded beats a call rewritten) — it is a persisted marker
  (`trajectoryBasis`, additive, nullable pre-214) plus a per-run aggregated count.
- **Materializer gate — an ALLOWLIST, not a denylist** (`NewsJudgmentSignalMaterializer`,
  `news-judgment-signal-v2 → v3`): a judgment materializes ONLY when its `trajectoryBasis` is an explicitly
  allowlisted value — under this spec **`Supported` alone**; spec 215 adds `ReferenceSupported`. Everything
  else mints nothing, each under its OWN named skip reason: `LevelOnlyTrajectory` (basis LevelOnly),
  `TrajectoryBasisNotRecorded` (basis null — a pre-214 record reached the v3 materializer; it cannot be
  re-derived without re-judging, so it is counted, never assumed Supported), `TrajectoryBasisNotAllowlisted`
  (a DEFINED enum value that is not on the allowlist — a future basis nobody allowlisted; an unknown or
  malformed token ON DISK never reaches the materializer at all, because the strict JSON enum converter
  rejects the record as unreadable, which is counted on the existing unreadable-record axis). All three render through `DescribeSkips` in the
  daily news report's accounting and the live artifact beside `not-judged` / `non-directional-trajectory`.
  Fail-closed means the default outcome is "no signal": a direction whose basis is absent, unknown or
  level-only produces no scoring input.
- **Durable record schema `news-judgment-v4 → v5`** (`NewsJudgmentRecord.CurrentSchemaVersion`, L193 —
  distinct from the MODEL-RESPONSE schema `news-judgment-schema-v3`, which this spec does not change):
  `trajectoryBasis` and the per-family `ComparisonBasis` change what a persisted judgment MEANS — whether
  it can become a scoring signal — which is the same precedent that moved the record tag for
  `TrajectoryFactIds`. v4 records stay readable (basis null ⇒ `TrajectoryBasisNotRecorded` above).
- **v3 signals and accrued v2 signals — what actually happens on the first run.** A materialized signal's
  id is `DeterministicGuid("radar:news-judgment-signal:" + version + ":" + judgmentId)`
  (`SignalIdFor`, `NewsJudgmentSignalMaterializer.cs` L147–149). The prompt/cohort fork gives every
  re-judged company a NEW judgment id, so a v3 signal can never collide with, or be found by a
  prior-version lookup for, the v2 signal of the OLD judgment — prior-version occupancy does NOT prevent
  a second signal here (review round 1 corrected the earlier claim). Therefore: v1 and v2 stay in
  `SupportedJudgmentSignalVersions` as accepted historical versions; new-cohort v3 judgments MAY mint new
  signals; where the old v2 signal and the new v3 signal cite the SAME evidence, the existing
  latest-judgment supersede (`NewsJudgmentSignalSupersede`, `news-judgment-supersede-v1`) resolves them —
  the later judgment's signal replaces the earlier over that evidence in both windows; where they cite
  DIFFERENT evidence anchors they may coexist, and that is measured, not assumed. **The first post-214 run
  reports, per company: v2 signals in window, v3 signals minted, same-evidence pairs resolved by
  supersede, and different-evidence coexistences** — descriptive, in the PR body's post-merge follow-up,
  no gate.
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

**Sanity bounds, to INVESTIGATE if breached — not targets to tune to:** > 30% of quantified facts in
`LevelOnly`, < 5% in `StatedComparison`, or a `LevelOnly` judgment share ABOVE the crude 14.8% each mean
the implementer inspects a sample of the classified statements and reports what the tables did — a
mis-tabled noun or a substring hit is a defect to fix; a genuinely level-heavy corpus is a finding to
record. The share is not itself proof the classifier is wrong, and it must never be tuned to preserve
signal volume — that would weaken the very fix this spec exists for.

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

- [ ] `StatementComparisonClassifier` (`comparison-basis-v1`) is pure, closed-table, PRECEDENCE-ordered,
      unit-tested on: the AGX backlog statement → `LevelOnly`; "record revenue of $384 million" →
      `StatedComparison`; "Backlog declined during the quarter" (no number) → `StatedComparison`; "The
      company won a major government contract" (no number) → `Event`; "The FDA approved the product" →
      `Event`; "$22 Million Follow-On Order" → `Event`; "gross margin was 24%" → `LevelOnly` (a bare
      percentage is never a comparison); a bare-number earnings statement → `LevelOnly`; its token is in
      the judgment cohort key and thus the `news=` segment.
- [ ] Every supplied family renders its `ComparisonBasis` line; prompt v4 carries rule 11 verbatim in
      spirit; schema v3 unchanged.
- [ ] `trajectoryBasis` persisted (nullable pre-214) and computed only over resolved cited facts; record
      schema `news-judgment-v5` with v4 readable; the materializer ALLOWLISTS `Supported` only — `LevelOnly`,
      null and defined-but-not-allowlisted each mint nothing under their own skip reason
      (`LevelOnlyTrajectory`, `TrajectoryBasisNotRecorded`, `TrajectoryBasisNotAllowlisted`) in the run
      summary, the daily report
      accounting and the live artifact; v1/v2 stay accepted historical versions; the first-run
      v2/v3 overlap is measured and reported, not claimed away.
- [ ] §3 live distribution in the PR body with the AGX four-fact classification; the sanity bounds hold.
- [ ] Appendix `basis` column, operator-guide paragraph, history bullets (new + the 194 in-place amendment),
      CLAUDE.md bullet amended in place.
- [ ] AI-ON pins moved and asserted by `ScoringConfigFingerprintTests`; AI-OFF pins proven unchanged; PR body
      carries the operator step and the back-to-back-with-215 instruction.
- [ ] `dotnet build` / full suite / `git diff --check` clean; actual dispatch→PR time in the PR body.

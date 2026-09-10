# Task: The judge reads 19 companies out of 102 because a risk-assessment selector was reused to feed the scorer — judge every company, cap the DEPTH not the COVERAGE

## Overview

Radar's only source of "is this news good or bad" is the stage-2 judge. It runs on ~19 companies a day.
The other ~83 reach scoring as a **count of articles with no direction**, and that count actively pushes
them DOWN the ranking. Reconstructed from the store on 2026-09-09
(`data/news-risk/judgments`, `data/news-typing/typings`, `data/scores`, `data/evidence/raw/news`):

- **`KeywordSignalExtractor` emits exactly one Neutral `MediaAttention` per news article, unconditionally**
  (the `EvidenceSourceType.NewsArticle` branch; directional keyword rules are deliberately suppressed for
  news — spec 70). Direction reaches scoring ONLY via `NewsJudgmentSignalMaterializer`, i.e. only for a
  company the judge actually judged.
- **The judge covers 32 of 102 companies, ever.** 322 judgment records exist (67 reused), first one
  2026-08-24. Fresh judgments per day run 12–23; the `MaxCompaniesPerRun` budget (default 30) was reached
  on **1 of 13 active days**. The cost budget is NOT the binding constraint.
- **What binds is `NewsRiskCandidateSelector.RowsPerSection = 5`** — the first five ranked rows per Research
  section, deduped across arms. That is a traversal constant, not a cost decision, and raising
  `MaxCompaniesPerRun` would change nothing.
- **The bias is measurable and it runs the wrong way.** Over the whole judged era, of the 15 rows where
  score and forward price disagreed most (`scripts/audit-score-vs-price-misses.ps1`):
  the six MISSES (scored low, then rose) have been judged **0, 0, 0, 0, 0, 0** times; the nine FALSE
  POSITIVES (scored high, then fell) **17, 17, 16, 12, 10, 7, 1, 0, 0**. Judgment spend concentrates on
  names already ranked high — the ones producing false positives — while the misses get nothing.
- **The loop closes on itself.** `RadarScoreFormulaV8` discounts Opportunity by Attention
  (`Opportunity = Trajectory · (EvidenceConfidence/100) · clamp(followingDiscount, floor, 1)`), which is
  correct for the product thesis. But unread coverage is still counted, so heavy coverage LOWERS the rank,
  and a lowered rank falls outside the top five, so the coverage is never read. Worked example — MarineMax
  (HZO) 2026-07-31, the largest miss in the store (+51.4% over 21 sessions): `Trajectory 56, Opportunity 16
  (Attention 75, …)`, rank ~43, **never judged, not once**. Its twelve contributing links include
  `(collapsed 31 same-event media items)` and `(collapsed 28 …)` of Blackstone/Donerail bid coverage,
  every one classified `MediaAttention (Neutral)`. Radar's response to the most informative event in its
  universe was to conclude the company was already noticed and look elsewhere.

**Why it is like this, and it was nobody's decision.** The selector is `NewsRiskCandidateSelector`, in
`Radar.Application.NewsRisk`, built by spec 179 §3 to pick candidates for **risk assessment** — a skeptic
pass over the names about to be shown to a human. For THAT job "top five per section" is correct: you audit
what you are about to recommend. Spec 194 then repurposed judgments into the source of directional
`MediaAttention` **for scoring**. The job changed from "audit the top of the list" to "supply an input to
the ranking that produces the list", and the selection rule was never re-derived. This is
`radar-constraints-arent-rules` exactly: a constraint correct at its origin, still obeyed where it is wrong.

**Measured: the work is already paid for.** Stage-1 typing already covers **all 102 companies** — 5,500
typing artifacts, median 44 per company, minimum 4. The facts are extracted and on disk for every name. The
only missing step is asking the judge to read them.

**Measured: cost is not a reason.** Total DeepInfra spend is **$0.24 (Sept 1–9), $0.26 (Aug), ~$0.18 (Jul)**
— about a quarter of a dollar a month for typing, judging and the directional filing read combined. The
judge's arrival is visible in output tokens (Jul 60,329 → Aug 414,985 → Sept 487,964), putting its marginal
cost near a cent a day at 19 companies. Judging all 102 is ~5.4× that: **single dollars per month.** Judgment
latency is median ~10s / p90 ~28.5s, so 102 companies is ~17 minutes sequential and parallelisable.

This spec makes coverage universal and caps DEPTH instead. It adds no new AI stage, no new provider call
type, and no new identity machinery.

## Assignment

Worktree: any. Dependencies: main at `19c0699` or later. Use `run-next.ps1 -Spec 219`.

## 1. Coverage becomes universal: `news-judgment-coverage-v2`

Replace rank-gated selection with **every company that has typed facts in the window**. Concretely, in
`NewsRiskCandidateSelector` (or a sibling that leaves the spec-179 risk path intact — see §3):

- The breadth pass enumerates the **company universe**, not `StrategyReportSection.Rows`. Order is
  deterministic: ticker ascending, then company id, so two runs over one store produce one order.
- A company with **no typed facts in the window is SKIPPED AND COUNTED**, never silently absent. It is not
  an error — it is a company with nothing to read — and the count is the honest measure of how much of the
  universe the judge could not reach.
- `MaxCompaniesPerRun` stays as a **safety valve**, not a policy: raise the shipped baseline to cover the
  seeded universe. If it ever binds, the run says so on a counter AND names how many companies it dropped,
  because a silently truncated universe is the defect this spec exists to remove.
- Selection is **not** a merged rank, a consensus, or a score comparison. There is no ordering by
  desirability at all in the breadth pass — that is the whole point.

## 2. Depth is what gets capped: `MaxFamiliesPerBreadthJudgment`

The current shape is a DEEP read on a few (`MaxFamiliesPerJudgment`, default 50). Invert it: a bounded read
on everyone.

- New option `Radar:NewsResearch:Judgment:MaxFamiliesPerBreadthJudgment`, shipped default **5**. The
  arithmetic is measured, not projected: across the 322 accrued judgments the families supplied per judgment
  run min 0 / median 36 / mean 32.3 / p90 50 / max 50, so a run of ~19 companies supplies **~610
  family-units**. The breadth pass supplies 102 × 5 = **510**. **Universal coverage for LESS model work than
  today** — the depth cohort in §3 adds its own budget back on top, and the §5 measurement is what settles
  the true delta.
- **The families supplied must be the most DISTINCT ones, not the first five.** The de-duplication already
  exists and must be reused, not reimplemented: `MediaAttentionCollapse` (spec 109) and `fact-family-v2`
  (`normalization=statement-normalization-v1|similarity=token-set-jaccard|threshold=0.6|temporalWindowDays=7`)
  already collapse same-event coverage to one representative — this is why HZO's twelve links stand for ~75
  articles. `NewsJudgmentInput` already orders families by `MemberCount` descending; the breadth pass takes
  the first N of that EXISTING order, tie-broken by `distinctPublisherCount` then `familyId` so the choice
  is total and reproducible. **SUPERSEDED by spec 220 (`family-ordering-v2`):** member count is syndication
  volume, and at a five-family budget it handed the judge boilerplate (46 of 83 breadth reads came back
  `Unknown` on 2026-09-09). Families are now ordered by comparison basis first (`StatedComparison` =
  `Event`, then `LevelOnly`, then `NotQuantified`), with this `MemberCount` → `distinctPublisherCount` →
  `familyId` order retained unchanged as the tie-break within a basis class.
- **Every family not supplied is counted per judgment** (`familiesAvailable`, `familiesSupplied`,
  `familiesWithheldByBudget`). A judgment that saw 5 of 60 families must not read as a judgment that saw
  everything: the record states its own coverage.
- A breadth judgment carries a **`ReadDepth` marker** (`Breadth` | `Full`) on the record, so a downstream
  consumer can tell a bounded read from a complete one and never has to infer it.

## 3. Depth is retained where it was designed for

Spec 179's purpose — auditing the names about to be shown to a human — is still valid and is NOT removed.

- The existing top-five-per-Research-section traversal continues to select a **depth cohort**, which is
  judged with the full `MaxFamiliesPerJudgment` budget and `ReadDepth = Full`.
- A company in both cohorts is judged **once**, at Full depth; the breadth pass must not duplicate it. The
  dedupe is by company id and the retained selection ancestry rule from spec 179 §3 is unchanged.
- Net model calls: ~102 breadth (5 families) + ~19 depth (50 families), against today's ~19 × 50.

## 4. What must be counted

Nothing may be discarded without being counted (CLAUDE.md). At minimum, per run and as ONE aggregated log
line each — never one per company:

- `CompaniesInUniverse`, `CompaniesWithTypedFactsInWindow`, `CompaniesJudgedBreadth`, `CompaniesJudgedFull`
- `CompaniesSkippedNoTypedFacts`, `CompaniesSkippedByCapacityValve` (with the cap that bound)
- `FamiliesAvailable`, `FamiliesSupplied`, `FamiliesWithheldByBudget`
- `JudgmentsReused` (existing) split by `ReadDepth`
- Judgment failures and `ValidationFailed` counts split by `ReadDepth` — a breadth read that fails
  validation more often than a full read is a finding, and it must be visible without a re-run.

## 5. Live distribution — REQUIRED, and the reason this spec exists

"No measure ships without its live distribution" (CLAUDE.md). This slice materially changes a scoring
input's coverage, so the PR body MUST report, from a real full run over the live universe:

- **Coverage:** companies judged (breadth / full / total) out of 102, and the skip counts from §4.
- **The distribution of `BusinessTrajectory` across the breadth cohort** — Improving / Deteriorating /
  Stable / not-recorded, as counts. **A near-constant result is a DEFECT even if the code is perfect.** If
  the breadth read returns ~"Improving" for almost everyone, it discriminates nothing and is worth no more
  than the Neutral it replaced. That failure has already happened twice here (`MediaAttention` 98.4%
  Neutral; `AttentionScore` 73.4 ± a few for every company) and both shipped green.
- **How many directional `MediaAttention` signals were minted**, and how many companies moved from "no
  directional news signal at all" to having one. This is the number the spec is buying.
- **Breadth vs Full disagreement on the overlap:** for any company judged at both depths across runs, does
  the 5-family read reach the same trajectory as the 50-family read? Report agreement as a count. This is
  the honest measure of what depth costs, and it is descriptive — no gate, no removal.
- **Cost and time, measured not projected:** the DeepInfra spend delta and wall-clock judge duration for the
  run. The store records no per-call token accounting today, so quote the provider page and say so.

## 6. Identity: a chore, not a gate

Judging ~102 companies instead of ~19 mints far more directional signals, and the judgment configuration is
hashed into the `news=` segment of `ScoringConfigVersion` (spec 194 §2). Therefore:

- The AI-ON pins move. `ScoringConfigFingerprintTests` is the only authority for the new values — do not
  quote a pin in prose, here or anywhere.
- `StrategyIdentityGuard` will halt before collection on the first run after merge. **This is correct and it
  is a thirty-second operator step**, not a design question: consciously delete
  `data/scoring-configs/strategies/{name}.json` (git-ignored — NEVER fabricate one) and let the next run
  re-record it. Owed ONCE, by this spec, and separate from any step owed by 214–216.
- Record the change date in `docs/architecture-history.md` — **not for the fingerprint**, but so that a step
  change in the efficacy chart can be read as "Radar gained a sense organ" rather than "Radar got better".
  One line. This is the only bookkeeping this spec asks for.

## Non-goals

- **No change to the attention discount.** `RadarScoreFormulaV8` keeps discounting Opportunity by Attention;
  that is the product thesis and is not in question here. This spec changes what the judge READS, not how
  the formula weighs it. No new `radar-formula-vN`, no `CompositionRevision` bump.
- **No change to stage-1 typing budgets or the backlog drain** (`MaxNewTypingsPerRun`). If the breadth pass
  finds companies whose typed facts are stale, that is a FINDING to report under §5, not a fix to make here.
- **No new AI stage, no new provider, no new identity machinery.** Existing prompt, schema and cohort keys.
- **No re-ranking, no consensus, no merged score across arms.** The breadth pass has no notion of "better".
- **No backfill.** Accrued history heals forward only (AD-8/AD-1); past scores are not re-judged.
- **Not a fix for spec 217's premise.** That doc's claim that "nothing predictive existed before the Reuters
  exclusive on the morning of the 10th" is false on the record (the exclusive is dated **2026-07-24** in the
  store, captured 6×, and 13 takeover-shaped items predate the 07-31 score). It needs correcting IN PLACE
  per the REVERSAL rule, but it is a separate change and must not be bundled here.

## Acceptance criteria

- [ ] The breadth pass enumerates the company universe deterministically; a company with no typed facts in
      the window is skipped and counted, and the ordering is stable across two runs on one store.
- [ ] `MaxFamiliesPerBreadthJudgment` defaults to 5; families supplied come from the EXISTING
      `NewsJudgmentInput` ordering, tie-broken to a total order; withheld families are counted per judgment.
- [ ] `ReadDepth` (`Breadth` | `Full`) is persisted on every judgment record; a company selected by both
      cohorts is judged once, at Full.
- [ ] The spec-179 risk path still selects its top-five-per-section depth cohort, unchanged in ancestry.
- [ ] Every counter in §4 is emitted as one aggregated line per run; a bound capacity valve names its cap
      and its dropped count.
- [ ] `MediaAttention` direction still reaches scoring ONLY through `NewsJudgmentSignalMaterializer`; the
      unconditional Neutral extractor branch (spec 70) is untouched.
- [ ] A regression test pins the MarineMax 2026-07-31 shape: a company with high Attention, ~rank 43 and
      typed facts in the window IS a breadth candidate (under the old traversal it was not).
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` both green.
- [ ] The §5 live distribution is reported in the PR body from a real full run — coverage, trajectory
      distribution, signals minted, breadth-vs-full agreement, measured cost and wall-clock.
- [ ] The §6 operator step is stated in the PR body as owed, and the one-line date note lands in
      `docs/architecture-history.md`.

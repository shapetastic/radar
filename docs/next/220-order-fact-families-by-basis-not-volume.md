# Task: The judge's five fact families are chosen by SYNDICATION VOLUME, so 41% of trajectory-bearing facts are invisible to it — order by comparison BASIS instead

## Overview

Spec 219 gave the judge universal coverage: the 2026-09-09 run (`run-20260909T234658242Z-5c6644f6`,
stamp `radar-scoring-fp-09c9db128480`) planned 102 of 102 companies, dropped **0** to the capacity valve,
and judged 86 (68 breadth + 18 full). Coverage is solved. What it bought is not, and the §5 live
distribution says why.

**The breadth cohort returned `Unknown` 46 times out of 83 (55.4%) and failed validation 15 times (18.1%),
against 5.3% and 5.3% for the full cohort.** So of 83 newly-covered companies only 22 produced a usable
directional verdict.

**The `Unknown`s are not failures — they are correct abstentions, and they name the cause.** All 46
received their FULL 5-family budget (min 4, median 5, max 5), wrote a substantial rationale (median 665
characters), recorded 0 findings and left `trajectoryFactIds` empty. Verbatim:

> "All six supplied fact families are NotQuantified (level-only or event mentions without a comparison or
> reference value). Fact (CEO sells shares) reports a transaction but no direction on business trajectory.
> Fact (investor day) is an event. Fact (Q1 results announcement) and Fact (Q2 earnings report) report
> earnings were released but provide no figures." — KOP

> "Director appointment, CFO/CEO stock sales, and conference presentations are management/governance or
> promotional events with no stated trajectory such as backlog change, revenue comparison, or financial
> beat/miss." — ANIP

The judge was not starved of quantity. It was handed earnings-call notices, investor days, conference
appearances and director stock awards, and correctly declined to invent a trajectory from them.

**Root cause, and it is a selection defect, not a budget one.** Spec 219 §2 told the breadth pass to take
the first N of the EXISTING `NewsJudgmentInput` ordering, which is `OrderByDescending(f => f.MemberCount)`.
Member count is SYNDICATION VOLUME. What gets syndicated is boilerplate — "Q2 results released", "CEO to
present at conference" — while the trajectory-bearing fact (a revenue comparison, a record backlog) often
sits in a family with one or two members. Selecting the most-covered family selects the least informative.

**Measured, on the full cohort that succeeded** (the only place both the top-5 and the tail were visible):

| | |
|---|---|
| Full judgments citing a trajectory fact resolvable to a family | 17 |
| …whose cited fact was inside the top-5-by-memberCount | **10 (59%)** |
| …whose cited fact was OUTSIDE it | **7 (41%)** at ranks 5, 5, 10, 14, 14, 20, 28 |

`AEHR Improving` cited a fact at rank **28 of 50**; `MSEX Improving` rank 20 of 43; `STRL Improving` rank
14 of 50. That 41% is the OPTIMISTIC bound: it takes the BEST rank among each judgment's cited facts and
counts only judgments that succeeded.

This is the same category error spec 219 fixed one layer up — selecting on a popularity proxy instead of
the property actually needed. 219 stopped ranking companies by desirability; it left the families ranked by
desirability.

**The fix already exists and is deterministic.** `StatementComparisonClassifier`
(`src/Radar.Application/NewsRisk/Judgment/StatementComparisonClassifier.cs`) already classifies every
statement as `StatedComparison`, `LevelOnly`, `Event` or `NotQuantified` — a closed-phrase-table read,
no model call, `comparison-basis-v1`, already computed for every supplied fact since spec 214. It simply
runs AFTER selection. Running it BEFORE, and ordering on it, costs one classifier pass over families that
were going to be classified anyway.

## Assignment

Worktree: any. Dependencies: main at `225b915` or later (spec 219 must be present). Use `run-next.ps1 -Spec 220`.

## 1. Order families by comparison basis, then volume — `family-ordering-v2`

In `NewsJudgmentInput` (the single existing ordering both cohorts read — do NOT add a second one):

- Classify each family's representative fact with the EXISTING `StatementComparisonClassifier`. Reuse it;
  do not reimplement the phrase tables, and do not add a model call.
- Order by **basis class first**, then the existing `MemberCount` descending, then `familyId` ordinal so
  the order is TOTAL and two runs over one store produce one order.
- The basis rank is **`StatedComparison` and `Event` first, then `LevelOnly`, then `NotQuantified`**, and
  it follows the enum's own documented semantics rather than intuition: `StatedComparison` "states a
  comparison or trend" and `Event` "carries direction without needing a comparison" — both establish
  direction, so both lead. `LevelOnly` explicitly "establishes no direction by itself" (it only becomes
  usable beside a cited reference value — spec 215's `ReferenceSupported`), so it ranks BELOW `Event`
  despite being quantified. `NotQuantified` filled all 46 `Unknown` bundles and belongs at the back.
  Ranking `LevelOnly` above `Event` would promote exactly the facts spec 214 says mint nothing.
- **Within a basis class the existing MemberCount order is unchanged**, so this is a re-ordering ACROSS
  classes only. Syndication volume stops being the primary key and becomes the tie-break it should always
  have been.
- The full cohort reads the same ordering. At a 50-family budget it usually sees the whole set anyway, so
  its verdicts should be largely unchanged — that stability is a CHECK on this change, reported in §4, not
  an assumption.

## 2. A blank `challengeStrength` must not discard a judgment that has surviving findings

Six of the sixteen validation failures were one shape:
`challenge-strength-out-of-range: '' with N surviving finding`. The model returned an EMPTY value, not an
out-of-range one, and the whole judgment was discarded — including its findings and its rationale. That is
the spec-192 defect again (an over-long rationale binning unexamined findings), in a new field.

- A blank/absent `challengeStrength` is **not recorded**, never coerced to `0` or to a midpoint. Persist it
  as null and COUNT it (`ChallengeStrengthNotStated`), by cohort.
- A judgment that is otherwise valid — cited trajectory, grounded findings, non-blank rationale, no advice
  language — is **accepted** with `challengeStrength` null. An out-of-RANGE value (a number outside the
  permitted band) still fails, unchanged: this relaxes ABSENT, not INVALID.
- The other rejections stay exactly as they are. `rationale-advice-language` (2),
  `trajectory-non-business-context-only` (3+1), `rationale-missing` (2), `trajectory-evidence-missing` (1)
  and `reference-not-projected` (1) are the guards working correctly and MUST NOT be loosened.
- **If this proves to be a scoring-fingerprint input, split it into its own spec** rather than bundling an
  identity move with §1 — §1 alone is expected to move the pin and one boundary is enough.

## 3. What must be counted

Per run, one aggregated line each, never one per company:

- `FamiliesByBasisAvailable` and `FamiliesByBasisSupplied`, each broken down `StatedComparison` /
  `Event` / `LevelOnly` / `NotQuantified`. This is the number that says whether the reordering
  actually changed what the judge saw.
- `FamiliesWithheldByBudget` split by basis class — withholding 3,557 `NotQuantified` families is a
  different fact from withholding 3,557 `StatedComparison` ones.
- `ChallengeStrengthNotStated`, by cohort (§2).
- The existing spec-219 coverage counters are unchanged and still required.

## 4. Live verification — the before/after is precommitted

The 2026-09-09 run is the baseline. Report from the first full run after merge, in the PR body:

| measure | baseline (2026-09-09) | after |
|---|---|---|
| breadth `Unknown` | 46 / 83 = **55.4%** | ? |
| breadth `ValidationFailed` | 15 / 83 = **18.1%** | ? |
| full `Unknown` | 1 / 19 = **5.3%** | ? (expected ~flat — see §1) |
| companies with a usable directional verdict | **22** of 83 breadth | ? |
| families supplied by basis class | not recorded | ? |

- **A near-constant result is still a defect.** If `Unknown` merely moves to `Improving` for everyone, the
  measure discriminates nothing and this change failed even with the rate improved.
- **Report the full cohort's stability explicitly.** If full-cohort verdicts move materially, the
  reordering did something unintended at a budget where it should barely matter.
- Report cost and wall-clock, measured. §1 adds a classifier pass over families already being classified;
  it should be free. Say so from the measurement, not from the reasoning.

## Non-goals

- **No budget increase.** `MaxFamiliesPerBreadthJudgment` stays at 5. Raising it would ALSO reduce
  `Unknown` and would confound the measurement — the whole point is to learn whether ordering alone is
  enough. If §4 shows ordering helps but does not suffice, raising the budget is the NEXT spec, measured
  separately.
- **No change to the classifier's phrase tables**, to `comparison-basis-v1`, or to the spec-214
  `TrajectoryBasis` allowlist.
- **No new AI stage, prompt, schema or provider call.** The prompt is untouched by §1.
- **No loosening of any other validation rule** (see §2).
- **No re-judgment of accrued records.** Heal forward only (AD-8/AD-1); the 2026-09-09 judgments stay as
  the baseline they now are.

## Acceptance criteria

- [ ] Families are ordered basis-first, then `MemberCount` descending, then `familyId`; the order is total
      and reproducible across two runs on one store.
- [ ] `StatementComparisonClassifier` is REUSED, not reimplemented, and no model call is added.
- [ ] Within a basis class the pre-existing MemberCount order is byte-identical to before.
- [ ] A blank `challengeStrength` persists as null (never `0`), is counted, and does not by itself discard
      an otherwise-valid judgment; an out-of-range value still fails.
- [ ] Every counter in §3 is emitted as one aggregated line per run.
- [ ] A regression test pins the AEHR shape: a trajectory-bearing family that ranked 28th by member count
      is inside the top 5 under the new ordering.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` both green.
- [ ] The §4 table is completed in the PR body from a real full run, including the full-cohort stability
      check and the near-constant check.
- [ ] If §1 moves the scoring fingerprint, the PR states the operator step as owed (delete/re-record
      `data/scoring-configs/strategies/{name}.json`) and does not fabricate an identity file.

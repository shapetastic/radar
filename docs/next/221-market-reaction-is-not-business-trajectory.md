# Task: A stock-price move is not a business trajectory — stop admitting MarketReaction facts as directional basis, and split "we were handed nothing" from "we could not tell"

## Overview

Spec 219's live run (`run-20260909T234658242Z-5c6644f6`) returned `Unknown` for 46 of 83 breadth
judgments. Spec 220 attributes that to family ordering by syndication volume and reorders by comparison
basis. Reading the 47 `Unknown` rationales against the **persisted per-family `comparisonBasis`** shows
spec 220 is necessary but not sufficient, and surfaces a second, sharper defect.

**The `Unknown`s are two different states, and they are already separable from accrued data:**

| | count | what it means |
|---|---|---|
| **No directional basis supplied** | **19 (40%)** | every supplied family was `NotQuantified`/`LevelOnly` — nobody could have answered |
| **Had directional basis, still `Unknown`** | **28 (60%)** | ≥1 `StatedComparison`/`Event` family was supplied and the judge still declined |

Across all `Unknown` judgments the supplied families were 218 `NotQuantified`, 33 `StatedComparison`,
24 `Event`, 5 `LevelOnly` — only 20% carried any direction.

**The 28 are the finding, and they are NOT flat companies.** The judge is explicitly REJECTING what the
classifier called directional, and the pattern is consistent:

> "The supplied fact families are dominated by market-reaction items (stock price movements, all-time high,
> 12-month high) which do not, on their own, establish recent business trajectory. The only supplied fact
> with a StatedComparison is the 3.9% price increase and the all-time high." — SENEA

> "No supplied fact provides a StatedComparison of a business metric (e.g., past vs. present FFO,
> occupancy, NOI)... The three unique claims are: a financing event (new $10.2M Fannie Mae mortgage at
> 6.03%)" — UMH

> "The single Event fact (acquisition of View Point for $140M) is an acquisition event, but without
> accompanying statements about..." — MMSI

> "an earnings outperformer label and a Q2 review that compares winners and losers, but neither provides a
> quantitative comparison or event establishing a recent business trajectory" — GHM

`StatementComparisonClassifier` is answering a LINGUISTIC question correctly — does this statement contain a
comparison marker or an event term — and that is not the question a trajectory read needs, which is
SEMANTIC: is the thing being compared a *business* metric? "The stock hit an all-time high" contains a
comparison. It says nothing about the business.

**This is an AD-14 problem, not only a quality one.** A `MarketReaction` fact reaching the judge as
directional basis invites the verdict "Improving" *because the price rose*. That verdict mints a directional
`MediaAttention` signal, which feeds `OpportunityScore`. Price is VALIDATION-ONLY and must never be a
scoring input — this is a path by which it becomes one, laundered through a news fact.

**Stage-1 typing already labels every fact**, so the fix needs no new classifier and no model call. Over 612
sampled facts: `MarketReaction` **80 (13%)**, `AnalystOrRatingAction` 52, `ManagementOrGovernance` 66,
`PromotionalOrListicle` 8, `IndexOrTradingMechanics` 2 — precisely the categories the four rationales above
reject.

**Consequence for spec 220, stated plainly:** 220 orders families by `comparisonBasis`, which counts a
stock-price move as `StatedComparison`. Without this spec, 220 will promote market-reaction facts INTO the
top five and partly spend the improvement it was written to deliver. 220 remains correct and should ship
first — its §4 is a clean single-variable test — but its measured gain should be read as a floor, not a
ceiling.

## Assignment

Worktree: any. Dependencies: **spec 220 must be merged first** (this extends `family-ordering-v2`).
Use `run-next.ps1 -Spec 221`.

## 1. Market-reaction facts are demoted in SELECTION — `family-ordering-v3`

Extend spec 220's ordering. **Do not touch `StatementComparisonClassifier` or `comparison-basis-v1`**: that
classifier answers its own question correctly, it is spec 214's contract, and the `ComparisonBasis` LINE
SUPPLIED TO THE JUDGE stays byte-identical. This spec changes only WHICH families are picked.

- A family is **non-business** for ordering purposes when its representative fact's stage-1 `eventTypes`
  are drawn ONLY from the closed non-business set: `MarketReaction`, `AnalystOrRatingAction`,
  `PromotionalOrListicle`, `IndexOrTradingMechanics`. The set is a declared constant with a version token,
  reviewed as a table, not an inline predicate.
- A fact carrying a non-business type ALONGSIDE a business type (e.g. `MarketReaction` +
  `EarningsOrGuidance`) is **business** — the exclusion requires the eventTypes to be non-business
  EXCLUSIVELY. A partial-overlap fact is where the real content usually is.
- A fact with EMPTY or absent `eventTypes` is **not** demoted and is **counted**
  (`FamiliesWithNoEventTypes`). Absent is not evidence of noise; treating it as such would silently drop
  facts stage-1 failed to label.
- `ManagementOrGovernance` is deliberately **NOT** in the demotion set. It carries insider transactions,
  which are a real Radar signal type (`InsiderBuying`, spec 93) — demoting it would suppress evidence the
  pipeline already scores. Named here so a later reader does not "complete" the list.
- Ordering becomes: business-and-directional (`StatedComparison`/`Event`) → business `LevelOnly` →
  business `NotQuantified` → **non-business, whatever its basis** → within every class, spec 220's
  existing `MemberCount` then `distinctPublisherCount` then `familyId`. Demoted, never dropped: a company
  whose entire supply is market reaction still gets its five families and the judge still sees them.

## 2. Split "handed nothing" from "could not tell" — BOTH a derived fact and a model verdict

Two different questions, two different answerers, and **their DISAGREEMENT is the primary diagnostic**.

**2a. The derived, supply-side fact (Radar answers).** Add a persisted `SuppliedBasisProfile` on the
judgment record: supplied families counted by `comparisonBasis` and by business/non-business (§1). Derive
`NoDirectionalBasisSupplied` = no supplied family was business-and-directional. This audits RADAR's own
behaviour — did we hand the judge anything to work with — and cannot be gamed by the model. It is computable
over ALREADY-ACCRUED judgments (this spec's Overview numbers were derived that way): no backfill, no
re-judgment, the reporting simply starts distinguishing them.

**2b. The model verdict (the judge answers).** Add `NoBusinessSignal` to the judgment vocabulary — the
judge's explicit finding that what it READ carries no business trajectory, as distinct from `Unknown`
("there is signal here and I cannot resolve it"). New wire token, new schema version, same validation path
as every other value; a `NoBusinessSignal` verdict still requires a non-blank rationale and still fails on
advice language.

**Why the model must own 2b, reversing this spec's first draft.** The original said a derived flag was
enough and a model token would be "an easy exit from a hard call". Both halves were wrong. `Unknown` IS
already that exit — it absorbed 47 judgments in one run — so this splits an existing escape hatch rather
than opening a new one. And more decisively, **the judge OUTPERFORMED the deterministic classifier on
exactly this question**: SENEA, UMH, MMSI and GHM were all cases where `comparisonBasis` said directional
and the judge correctly said the fact was a stock move, a mortgage, or an analyst label. A "noise" flag
derived from `comparisonBasis` would inherit the very defect §1 exists to fix.

**2c. The disagreement is the metric.** Count and report, per run:
`ClassifierSaidDirectionalJudgeSaidNoBusinessSignal` — judgments where ≥1 supplied family was
business-and-directional by `comparisonBasis` yet the judge returned `NoBusinessSignal`. Every such case is
a candidate classifier defect, localized automatically. The four rationales in the Overview were found by
hand-reading; this surfaces all of them. A RISING count is not a regression — it is the instrument working.

**2d. Guard against the real risk.** The genuine hazard is `NoBusinessSignal` absorbing bad news the judge
would rather not call. §4 therefore checks that `Deteriorating` does not fall as `NoBusinessSignal` rises;
if it does, the token is being used to dodge and must be reconsidered.

## 3. What must be counted

Per run, one aggregated line each:

- `FamiliesNonBusinessAvailable` / `…Supplied` / `…DemotedBySelection`, split breadth/full.
- `FamiliesWithNoEventTypes` (§1) — stage-1 labelling gaps, not silently treated as business.
- `JudgmentsWithNoDirectionalBasisSupplied` (derived, 2a), split breadth/full.
- `JudgmentsNoBusinessSignal` (model verdict, 2b), split breadth/full.
- `ClassifierSaidDirectionalJudgeSaidNoBusinessSignal` (2c) — the disagreement count.
- Spec 219's coverage line and spec 220's basis counters are unchanged and still required.

## 4. Live verification

Baseline is the 2026-09-09 run; spec 220's post-merge run is the intermediate point. Report all three:

| measure | 2026-09-09 (219) | after 220 | after 221 |
|---|---|---|---|
| breadth `Unknown` | **55.4%** | ? | ? |
| …of which no-directional-basis-supplied | **19 (40%)** | ? | ? |
| …of which had basis and still Unknown | **28 (60%)** | ? | ? |
| non-business families supplied | not recorded | ? | ? |
| judge returned `NoBusinessSignal` | n/a (token did not exist) | n/a | ? |
| classifier-said-directional vs judge-said-noise | **28 (found by hand)** | ? | ? |
| `Deteriorating` (must not fall as noise rises) | **2 / 83** | ? | ? |
| companies with a usable directional verdict | **22 / 83** | ? | ? |

- **The 28 are the target.** If they do not fall, the diagnosis in the Overview is wrong and this spec
  should be reconsidered rather than extended.
- **If the 19 do not fall, that is a COLLECTION finding, not a judgment one** — those companies have no
  directional news in supply at all, and no selection rule can conjure it. Say so plainly rather than
  reaching for another ordering change.
- **A near-constant result remains a defect.** `Unknown` converting wholesale to `Improving` is a failure
  even with the rate improved.
- Report the full cohort's stability, and cost/wall-clock measured (§1 adds no model call).

## Non-goals

- **NOT deriving the noise verdict from `comparisonBasis` alone.** See §2b: that classifier is the thing
  §1 is correcting, so a flag derived from it would be wrong in precisely the SENEA/UMH/MMSI cases.
- **NOT adding a `Stable` trajectory token.** It was considered and the evidence rejects it: the 28
  has-basis `Unknown`s are not flat companies, they are companies whose directional facts were market
  reactions, financing events and analyst labels. Adding `Stable` would let the judge label those "flat"
  and would BURY this defect under a friendlier token. Revisit only if, after §1, a residue of `Unknown`
  judgments cites genuinely quantified business facts showing no change.
- **No change to `StatementComparisonClassifier`, `comparison-basis-v1`, or the `ComparisonBasis` line
  supplied to the judge.** Selection only.
- **No change to the stage-1 typing taxonomy** (`news-event-taxonomy-v1`) or to any prompt or schema.
- **No new model call, no new AI stage.**
- **No re-judgment or backfill** — heal forward (AD-8/AD-1); the 2026-09-09 and post-220 runs stay as the
  baselines they are.

## Acceptance criteria

- [ ] The non-business eventType set is a declared, versioned constant; `ManagementOrGovernance` is NOT in
      it; a fact mixing a non-business and a business type is treated as business.
- [ ] Families with absent/empty `eventTypes` are NOT demoted and ARE counted.
- [ ] Non-business families are DEMOTED, never dropped: a company supplied only market-reaction families
      still receives its full budget and is still judged.
- [ ] Within every class spec 220's ordering is byte-identical to before.
- [ ] `SuppliedBasisProfile` is persisted and derived (2a) and is NOT the source of the noise verdict.
- [ ] `NoBusinessSignal` is a first-class model verdict (2b) with a bumped schema version, requiring a
      non-blank rationale and failing on advice language exactly as the other values do.
- [ ] The 2c disagreement counter is emitted, and §4 reports whether `Deteriorating` fell as
      `NoBusinessSignal` rose (2d).
- [ ] A regression test pins the SENEA shape: a family whose only directional basis is a stock-price move
      ranks below a business `NotQuantified` family.
- [ ] Every counter in §3 is emitted as one aggregated line per run.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` both green.
- [ ] The §4 three-column table is completed in the PR body from a real full run.
- [ ] If the ordering change moves the scoring fingerprint, the PR states the operator step as owed and
      fabricates no identity file.

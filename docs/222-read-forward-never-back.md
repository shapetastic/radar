# Spec 222 — Withdrawn: read forward, never back

**Status: WITHDRAWN, 2026-09-11. Not implemented.** Removed from `docs/next/` so it is no longer a
pending assignment. The original proposal is preserved below as historical text; its implementation,
archive, strategy-reset and claim-boundary instructions are withdrawn in full.

## Decision and evidence boundary

Do not implement 222. Its proposed mechanism is not supported by the reported family re-supply counts,
and its proposed per-family judgments require scoring and context semantics that the design did not
provide. **No first-read latency verdict is established.** Neither "latency is fixed" nor "the backlog
will correct organically" is a finding of this record.

The measurements below were reported by the maintainer during the 2026-09-11 review. They are recorded
with their limitations, not presented as independently reproduced by this documentation change.

| Reported observation | What it establishes |
|---|---|
| Original supplied-fact median age: 12–13 days | Age at supply, not time to first judgment. |
| Per-family supply frequency: median 2, maximum 3 in the reported sample | The original DGII count of 18 company judgments in 13 days is not 18 reads per family; it overstates the evidence for repeated family reading as the main problem. |
| Last run: 1,300 families supplied of 5,311 available; 4,011 withheld by budget (about 75.5%) | A per-run selection gap under the breadth cap of 5 and full cap of 50. It does not establish that those 4,011 families have never been supplied in an earlier run. |
| Supplied-family counts grew from 45 to 1,300 across the period as 219 and 220 landed | Coverage grew. This alone establishes neither first-read latency nor the causal contribution of each change. |
| Consecutive-run family-ID overlap of 84–98%, with IDs persisting from August | Family identity continuity was reported; this does not by itself prove an earlier successful judgment. |

Company coverage is not family coverage. A family that continually loses the selection ranking can
remain unread until it leaves the available window. The withheld count reopens the latency question
through selection coverage; it does not answer that question or prove that indefinite waiting occurred.

## Retracted latency analysis

The maintainer explicitly withdrew the reported median first-read latency of 12 days, the 5% within one
day figure, and the apparent weekly improvement from 19 days to 1 day (including the recent 83% within
two days figure). They must not be used as findings or as evidence that spec 219 fixed latency.

The second script labelled all 1,692 families as first judged on 2026-09-10, and its latency distribution
reproduced the overall age distribution. Together with the earlier history/overlap result, this raised an
unresolved measurement inconsistency. Persistent family IDs alone do not logically establish prior
judgment, but the available analysis is insufficient to support either script's latency conclusion.
The script defect or reconciliation has not been established here.

The original inference of a hidden 19-day window ceiling is also unestablished: the maximum age in a
sample does not prove a configured ceiling. Likewise, score dispersion does not establish that attention
correctly measures whether the market has already noticed a business development.

## Constraints retained for any future incremental-judgment design

1. **Define company-level meaning and cross-family context.** Independent family directions are not
   equivalent to a company judgment that can resolve conflicting evidence as `Mixed` (reported as 47.6%
   of the full cohort in the review). A future design must state how it preserves or deliberately changes
   that meaning, with the appropriate scoring identity; it cannot call this only a cache change.
2. **Define aggregation and revision replacement explicitly.**
   `NewsJudgmentSignalSupersede` chooses `winners[signal.EvidenceId]`. Two family verdicts anchored to
   the same article can lose one signal, while revisions anchored to different articles need not replace
   one another. The existing materializer and supersede rule do not provide the family aggregation
   promised by original section 2. See
   [NewsJudgmentSignalSupersede](../src/Radar.Application/Scoring/NewsJudgmentSignalSupersede.cs).
3. **Distinguish new information from projection changes.** `FactFamilyBuilder` projects
   `windowMembers`; expiry can change membership and the representative without any new news.
   "Members changed" is therefore not a sufficient invalidation rule. Reference inputs can also change
   without new members, and late earlier facts can move the identity anchor. Specify input revision,
   identity continuity and original observation time rather than treating all membership changes as
   new corroboration. See [FactFamilyBuilder](../src/Radar.Application/NewsTyping/FactFamilyBuilder.cs)
   and [NewsJudgmentInputBuilder](../src/Radar.Application/NewsRisk/Judgment/NewsJudgmentInput.cs).
4. **Preserve the state needed for reuse.** Original section 1 required reuse while section 4 archived
   `news-risk/` and `news-typing/`, removing the active history needed to support that reuse. These
   instructions contradict each other. A strategy rename alone does not isolate shared signal inputs.
   No store reset, archive move, new strategy or retirement of the prospective claim follows from this
   withdrawal.

## What a separate latency measurement must establish

Build the family denominator from typed facts using the production `FactFamilyBuilder` logic and the
appropriate extractor cohorts, rather than deriving the universe only from judgments. Reconstruct
historical availability with point-in-time typing knowledge; do not let facts typed later silently enter
an earlier run's universe. Preserve full-history identity semantics and account for anchor changes.

For each family, distinguish first observation, first availability as typed judge input, first actual
model supply, first successful judgment, and signal materialization. Reused records are not fresh model
calls. Join the complete applicable judgment history without pooling incompatible cohorts. Report both
observed first-read delays and the ages/counts of still-unsupplied families, including budget exclusions
and families that aged out without supply. Recent unjudged families must remain visible in the
denominator; a distribution over successful judgments alone cannot establish prompt coverage.

This is a separate measurement task, not an implementation assignment under 222. Its results may
justify a future selection or scheduling change; no such outcome is preselected here.

## Predictive value remains a separate question

The review reports no measurement of the effects of 219/220/221 against subsequent price outcomes.
Neither coverage growth nor a future latency result establishes predictive value. Evaluating accrued
scores remains a priority, respecting existing series identities, prospective claim boundaries and
outcome maturity. Price remains validation-only (AD-14).

---

## Original proposal — withdrawn in full, retained as history

**Everything below records the superseded proposal, including unsupported diagnoses and inactive
acceptance criteria. It is not an instruction to run or implement spec 222.**

### Original title: Read forward, never back — judge a fact ONCE when it arrives, accrue the signal, and stop re-reading a fortnight of history every night

## Overview

Radar's news read is a rolling recomputation over a sliding window, not a forward reader. Every night it
re-reads roughly two weeks of accrued facts and re-derives a verdict from them. Measured on the live store
(runs `5c6644f6` 2026-09-09 and `b640146f` 2026-09-10):

- **The median supplied fact is 12–13 days old**; p90 is 19 days; only **24–26% are within 7 days**. Three
  quarters of what the judge reads is more than a week old.
- **The directional verdicts are driven by the OLDEST material.** `Improving` has a median fact age of 14
  days and `Mixed` 14–15, while `Unknown` — the verdict that declines to call a direction — sits at 9–12.
  The fresher the input, the less likely a directional read.
- **The root cause is the cache key.** `NewsJudgmentGenerator` keys a completed judgment on
  `attemptKey = (cohortKey, CompanyId, bundle.FamilySetHash)` — the hash of the ENTIRE family set. One new
  article changes the set, so the cached verdict is invalidated and **every** fact is re-judged, including
  the ones judged identically the night before. DGII accrued **18 judgments across 13 days** this way.
- **What that produces.** DGII on 2026-09-10 was judged `Improving` from facts dated 2026-08-22 → 09-08,
  describing a quarter the market priced on 2026-08-06 (+19% on the day, peak 08-12, fully retraced by
  09-04, now below the pre-earnings price). The verdict is TRUE about the business and STALE as a signal.
  DGII is also the false positive `scripts/audit-score-vs-price-misses.ps1` flagged: 91st percentile on
  2026-08-06, then −16.4%.

**The premise this violates.** Radar surfaces companies whose trajectory may be improving **before the
market notices**. A verdict computed a fortnight after the information arrived is not that read, however
correct it is about the business.

**What this spec does NOT claim.** Repeat coverage of one event is NOT the defect and is deliberately left
alone (§ Non-goals): a story raised again a week later genuinely means more readers are seeing it, Radar
values each wave when it arrives, and the attention component already carries that. Attention was verified
to discriminate on the live universe — 19..92, sd 13.4, comparable to trajectory's 20..85 / 13.0 — so the
mechanism that discounts "already noticed" is working and is not price.

**An unlocated effective ceiling, to be found before anything is tuned.** `Typing:LookbackDays` is
configured at 30, yet no supplied fact in either run exceeded **19 days**. Something between the typing
lookback and the family projection is bounding the window and it is not written down. Find it and name it;
do not tune a number whose real value is unknown.

## Assignment

Worktree: any. Dependencies: main at `b683263` or later; spec 221 may land before or after (no overlap —
221 changes WHICH families are selected, this changes WHEN they are judged). Use `run-next.ps1 -Spec 222`.

## 1. The unit of judgment becomes the FAMILY, not the family SET — `news-judgment-incremental-v1`

- A judgment's durable identity is keyed on **the family**, not on `FamilySetHash`. A family already has a
  durable identity anchor (`FactFamilyBuilder.IdentityAnchor` = `first-member-utc-date+event-types`,
  segmented over full history), so this key already exists and is stable across runs.
- **A family that has already been judged under the same cohort key is NOT re-judged.** Its verdict is read
  from the store. Only families never judged before consume a model call.
- **A family whose MEMBERS changed since it was judged IS re-judged** — new corroboration can legitimately
  change a verdict — but the re-judgment is scoped to that family, not to every family the company has.
- The judge is therefore handed, on a normal night, the handful of families that are new since last night.
  On a company with no new news it is handed nothing and **no call is made and no verdict is recorded** —
  "no news today" is the correct answer to most days for most companies, not an `Unknown`.
- `FamilySetHash` remains on the record as provenance (it describes the bundle assembled), but it stops
  being the reuse key.

## 2. What the company-level trajectory becomes

Today one verdict is derived per company per night over everything in the window. That is what forces the
re-read. Instead:

- **A per-family verdict is the durable unit.** It is computed once, at arrival, and never recomputed
  unless its family changes.
- **The company-level directional signal is composed from accrued family verdicts** inside the existing
  60-day scoring window, by the existing materializer — no new scoring concept, no new formula, no
  `radar-formula-vN`.
- **Decay is the scoring window's job, not the reader's.** A family verdict ages out of the score the same
  way every other signal does. This spec does NOT add a decay curve; if one is wanted later it is its own
  slice, argued from measurement.
- The existing supersede rule (a judgment-derived `MediaAttention` replaces the ordinary attention event
  for its evidence) is unchanged.

## 3. Windows: locate the real ones, then set them to "since last read"

- **First, find and NAME the effective ceiling** that caps supplied facts at 19 days despite
  `Typing:LookbackDays=30`. Record it in the spec's PR body. A window nobody can point at cannot be tuned.
- The typing pass keeps a lookback — it is a BACKLOG DRAIN for facts never typed, and that is legitimate
  work. It is not the judgment window and must not be conflated with it.
- **The judgment's horizon becomes "families not yet judged"**, which is a state question, not a date
  question. A stalled run, a failed night or a backlog therefore heals forward automatically: the unjudged
  families are simply judged when the run next succeeds, however old they are — with their ORIGINAL
  observed dates intact, never restamped as today's news.
- Nothing here changes collection. `Radar:News:RecencyWindowDays=7` (spec 198) stays exactly as it is.

## 4. The clean start: a NEW SERIES, not a deleted store

The maintainer wants to start accruing afresh. That is achieved by starting a new series, NOT by deleting
data, and the distinction is load-bearing:

- **`data/evidence/` MUST NOT be deleted.** It holds **18,528 news articles back to 2010**. Since spec 198
  the news query carries `when:7d`; a first collection is unfiltered but returns only what the feed holds
  today (~100 items/company). Deleting the archive **permanently destroys** the historical corpus for a
  gain this spec does not need — forward-reading is a state change, not a data deletion. The same applies
  to `data/prices/` (cheap to refetch, but pointless to lose).
- **Start a NEW STRATEGY NAME** (e.g. `default` → `default-v2`) for the fresh accrual. This is the
  mechanism `StrategyIdentityGuard` itself recommends, it is spec 141's immutable-by-convention rule
  applied deliberately, and it orphans the old series cleanly WITHOUT destroying it. `Radar:PrimaryStrategy`
  moves with it.
- Derived stores that the maintainer wants clear (`scores/`, `signals/`, `efficacy/`, `news-typing/`,
  `news-risk/`) should be **ARCHIVED, not deleted** — move to `data-archive-{date}/`, roughly 1.1 GB,
  reversible. Accrued history is never rewritten (AD-8/AD-1); archiving is not rewriting.
- The old series stays readable as development data. Nothing is pooled across the boundary.

## 5. The precommitted claim boundary must be re-declared PROSPECTIVELY

`Radar:Efficacy:Comparison:PairedFirstEligibleAsOfUtc = 2026-09-29` is immutable by convention. A fresh
series cannot meet it, and `default.json` already states the only honest route:

> "if a different boundary is ever genuinely needed, declare a new one prospectively and treat pre-boundary
> dates as development data"

- Declare the NEW boundary **before any outcome under the new series exists** — that is the entire source
  of its value, and it must be recorded with its declaration date.
- The existing 2026-09-29 boundary and `PairedPrimaryStrategy=disclosure-led-v11` are **not edited or
  deleted**: they are marked as belonging to the retired series, in place, per the REVERSAL rule.
- State plainly in the PR that the old claim family will not be met and why. That is an honest outcome, not
  a failure to hide.

## 6. What must be counted

Per run, one aggregated line each — never one per company:

- `FamiliesNew`, `FamiliesAlreadyJudged` (verdict reused from store), `FamiliesRejudgedMembersChanged`.
- `CompaniesWithNoNewFamilies` — the companies that consumed NO model call because nothing arrived. This
  should be the majority on a normal night, and it is the number that proves the re-read stopped.
- `ModelCallsSaved` = calls that would have been made under the family-SET key. State the baseline: DGII
  alone took 18 judgments in 13 days.
- `FamiliesJudgedLateAndTheirAge` — families judged for the first time more than N days after their first
  observation (a stalled/backlogged read). Their ORIGINAL age must be visible, never restamped.
- Every verdict record keeps its family's first-observed instant, so "how old was this when we judged it"
  is answerable from the record alone.

## 7. Live verification

From the first full run after the new series starts, in the PR body:

| measure | baseline (2026-09-09 / 09-10) | after |
|---|---|---|
| median age of facts behind a verdict | **12–13 days** | ? (expect ≤2) |
| share of supplied facts within 7 days | **24–26%** | ? |
| median fact age behind `Improving` | **14 days** | ? |
| judgments per company per night | DGII **18 in 13 days** | ? |
| companies consuming no model call | not recorded | ? |

- **A near-constant result is still a defect.** If every company reads `NoBusinessSignal`/nothing because
  daily volume is too thin to judge, the read discriminates nothing — report the verdict distribution, not
  just the call count.
- **Report cost, measured.** Fewer calls should mean lower spend; quote the provider page, since the store
  records no per-call token accounting.
- Report the located effective ceiling from §3.

## Non-goals

- **NOT deleting `data/evidence/` or `data/prices/`.** See §4 — the news corpus is unrecoverable under
  `when:7d`.
- **NOT changing `FactFamilyBuilder.TemporalWindowDays` (7).** The maintainer's call, on the record: one
  event echoing a week later means more readers are seeing it, Radar valued the first wave when it arrived,
  and the attention component carries the spread. Widening it is a separate question and is NOT bundled.
- **NOT introducing price into scoring.** AD-14 stands as written. "Already noticed" is carried by
  attention, which was verified to discriminate live (19..92, sd 13.4). No price lookup, no absorption
  discount, no same-day move check — the latency budget is days, not seconds.
- **NOT adding a decay curve** to signals. Decay is the existing 60-day scoring window's job (§2).
- **NOT backfilling or re-judging accrued history.** Heal forward (AD-8/AD-1). The old series is archived
  and readable, never rewritten.
- **NOT changing collection.** `RecencyWindowDays=7` is untouched.
- **NOT a new formula.** No `radar-formula-vN`, no `CompositionRevision` bump.

## Acceptance criteria

- [ ] The judgment reuse key is the FAMILY identity, not `FamilySetHash`; `FamilySetHash` survives on the
      record as provenance only.
- [ ] A family already judged under the same cohort key consumes NO model call; one whose members changed
      is re-judged, and only that family.
- [ ] A company with no new families produces no call and NO recorded verdict — absence is not `Unknown`.
- [ ] A family judged late carries its ORIGINAL first-observed instant; nothing is restamped as today.
- [ ] The company-level directional signal composes accrued family verdicts through the EXISTING
      materializer and the existing 60-day window; no new scoring concept is added.
- [ ] The effective ceiling capping facts at 19 days is located and NAMED in the PR body.
- [ ] Every counter in §6 is emitted as one aggregated line per run.
- [ ] A regression test pins the DGII shape: given a family judged yesterday and one new family today, only
      the new one is sent to the model.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` both green.
- [ ] The §7 table is completed from a real full run, including the verdict distribution.
- [ ] The new series name, the archive move, and the NEW prospective claim boundary (with its declaration
      date) are all stated in the PR; the 2026-09-29 boundary is marked as retired-series IN PLACE, not
      edited away.

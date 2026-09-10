# Task: Read forward, never back — judge a fact ONCE when it arrives, accrue the signal, and stop re-reading a fortnight of history every night

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

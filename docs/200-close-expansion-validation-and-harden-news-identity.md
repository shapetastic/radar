# Task: Harden new-company news identity and close the spec-199 validation debt

## Overview

Spec 199 expanded the universe from 74 to 94 companies. The additions are useful, but the implementation
record contains three things that must be repaired before the expansion is treated as validated:

1. **Three news feeds are not precise enough for the collector's actual relevance rule.**
   `NewsAttentionCollector.IsRelevant` performs an unanchored, case-insensitive substring match against the
   feed's query phrase or ticker. That makes `query=Utah Medical&ticker=UTMD` capable of accepting unrelated
   "University of Utah medical ..." headlines, makes ticker `ESQ` collide with the ordinary word "Esquire",
   and leaves `query=Investors Title` broader than the issuer's full name. False-positive articles inflate
   Attention and the inverse-attention discount, so this is scoring-input integrity, not cosmetic search
   quality.
2. **The capacity ship condition was never measured on the code that shipped.** Spec 199 required a live
   post-198 baseline before sizing the batch, but no post-198 run existed. Its recorded section correctly says
   that, then incorrectly concludes that the ship condition was met from a projection. A projection may
   justify taking a reversible operational risk; it cannot satisfy a live-measurement condition after the
   fact.
3. **The efficacy wording conflates three different products.** The 20 additions receive live strategy scores
   immediately. After a complete forward-price window they may also appear in raw diagnostic returns. They do
   **not** enter the frozen benchmark-v1 leaderboard or the paired AD-15 claim: `UniverseBenchmark` correctly
   returns `NotInBenchmarkUniverse` until a prospectively declared benchmark-universe-v2 exists.

This spec fixes the three feed identities before their first collection if that boundary is still available,
corrects the durable documentation, and records the live post-expansion measurements after three successful
full runs. It changes no score formula, strategy, efficacy boundary or benchmark membership.

## Assignment

Worktree: any

Dependencies: specs 198 and 199 merged.

Delivery is deliberately **two-phase** because the repair must reach `main` before collection, while the
measurements do not exist until later:

- **Phase A — merge immediately, before the first post-199 full run:** §1–§4 and their tests. Keep this spec in
  `docs/next/`; do not call the validation complete. **The Phase A PR must NOT promote this spec to `docs/`** —
  CLAUDE.md Step 4 promotes by default; skip that step here and say so in the PR body.
- **Phase B — after three successful post-199 full runs:** append the §5–§6 measured record and the §4 mature
  read date in a follow-up PR, then promote this spec to `docs/`.

Do not hold Phase A unmerged while waiting for Phase B: doing so would allow the ambiguous queries to accrue
the history this slice is intended to prevent.

Estimated implementation time: Phase A ~2–3 hours; Phase B ~1–2 hours after the runs exist.

## 1. Repair the three feed identities — exact changes

Change only these three `newssearch` URLs in `data/companies.json`:

| ticker | current | required |
| --- | --- | --- |
| UTMD | `query=Utah Medical&ticker=UTMD` | `query=Utah Medical Products&ticker=UTMD` |
| ITIC | `query=Investors Title` | `query=Investors Title Company` |
| ESQ | `query=Esquire Financial&ticker=ESQ` | `query=Esquire Financial` |

Why these exact forms:

- UTMD keeps its non-colliding ticker but the phrase must identify the issuer, not a university plus the word
  "medical".
- ITIC already omits its colliding ticker (`critic`, `political`); use the issuer's full public name as the
  phrase.
- ESQ must join the existing colliding-ticker allowlist. `ESQ` is not a useful issuer token because an
  ordinary headline containing "Esquire" satisfies the current substring predicate. The issuer phrase alone
  is sufficiently specific.

Do **not** change aliases, company identity, CIKs, tiers or any other feed. Do not add quoted-query syntax: the
collector's local relevance decision is the load-bearing filter, and these exact phrases are what it evaluates.

### Deliberate narrowness: do not redesign ticker matching here

A boundary-aware ticker predicate may eventually be worthwhile, but applying it globally would change the
accepted history for every ticker-bearing feed and requires a corpus-wide impact audit of its own. This slice
fixes the three known, not-yet-accrued identities at the seed. Record a generic predicate change as deferred
only if further collisions are measured; do not smuggle it into this repair.

## 2. Establish whether the clean pre-collection boundary still exists

Before editing the queries, inspect the durable run, evidence, news-observation, news-typing and score stores
for the three company ids. **Do not flat-grep `data/` recursively** (the observation store alone holds thousands
of files per month and the grep exceeds two minutes): check the per-company directories under `data/scores`,
`data/signals`, `data/prices` and `data/news-typing`, and search `companyId` in `data/news-observations` and the
run records under `data/runs`. Note that a headline ending `" - Esquire"` (Esquire as PUBLISHER, e.g. the live
IMAX observation `911018cf-4da2-b3e9-7df9-57f86fc98fcc`) belongs to another company and is not ESQ history.

| ticker | company id |
| --- | --- |
| UTMD | `28243c9e-eb18-4a85-acec-8f93aeb8cdef` |
| ITIC | `2ae6e6da-b714-416f-9d90-b6432f6eac2b` |
| ESQ | `971ea074-e524-4d6d-baf2-ead26449a0dc` |

Record the latest durable run id/time/universe count and the per-company counts found. A recent global run date
is not evidence that a particular collector succeeded; use the run's collector/source provenance as well as
the stores.

Two outcomes are possible:

- **No post-199 history exists:** state that the correction landed before first collection. No migration or
  cleanup is required.
- **Any history exists:** the correction is prospective from the first run after merge. Record the affected
  run ids and counts, and mark the three-run attention read for that company as potentially contaminated.
  Inspecting titles/URLs to quantify the problem is allowed; changing history is not.

In either outcome, **never delete, rewrite, re-hash, reassign or backfill** evidence, observations, typing
records, signals, snapshots or efficacy artifacts. Insert-only history remains insert-only. A general
supersede-by-reference correction mechanism is outside this slice.

## 3. Pin the feed and relevance behavior in tests

Update `ProductionCompanySeedTests` so the intended seed cannot regress:

- add `ESQ` to `TickersWithoutTickerToken`, with the collision reason in the existing documentation and theory;
- pin the exact UTMD, ITIC and ESQ `newssearch` URLs above, not merely the presence/absence of `ticker=`;
- retain the existing ITIC collision assertion and all spec-199 CIK/feed pins; and
- continue to assert 94 companies and byte-stable membership — this spec adds no company.

Exercise the collector through its public collection surface in `NewsAttentionCollectorTests`; do not make
`IsRelevant` public for the test. At minimum pin these adversarial pairs:

| feed | must reject | must accept |
| --- | --- | --- |
| UTMD | `University of Utah Medical School opens a new centre` | `Utah Medical Products reports quarterly results` |
| ITIC | `Investors title technology as their top theme` | `Investors Title Company declares a dividend` |
| ESQ | `Esquire names its people of the year` | `Esquire Financial expands litigation banking` |

The accepted cases must produce evidence for the intended company; the rejected cases must produce none. Keep
URL dedupe, the spec-198 recency filter, the 25-item retained prefix (`Radar:News:MaxRecordsPerCompany`), the
100-item parse ceiling (spec 190) and evidence identity unchanged.

## 4. Correct the durable boundary descriptions

Amend `docs/199-expand-universe-small-caps.md`, its spec-199 `CLAUDE.md` record, and
`docs/cohorts/under-covered-2026-08.md` without rewriting the historical prediction.

### Capacity wording

Preserve the historical projected numbers, but label them **PROJECTED, NOT MEASURED**. The recorded statement
that the ship condition was met must be marked superseded by spec 200: the batch shipped while the required
post-198 measurement was still owed. Do not manufacture a pre-expansion post-198 baseline after the expansion
has already landed; that counterfactual no longer exists.

### Efficacy wording

Replace "they enter the efficacy series" with this explicit three-way boundary:

1. **Live strategy/report scoring:** all 94 companies score and rank on every ordinary run as soon as evidence
   is available. This is the surface used to inspect an EOSE-like bad leader now; no price horizon gates it.
2. **Raw forward-return diagnostics:** a new company can acquire a complete price observation only after its
   forward horizon resolves. This remains diagnostic and does not grant benchmark membership.
3. **Official benchmark-adjusted leaderboard and paired AD-15 claim:** the 20 additions remain
   `NotInBenchmarkUniverse` under frozen benchmark-v1. They are excluded until a future
   `benchmark-universe-v2` is prospectively declared. This spec does not create v2 and does not move the
   2026-09-29 AD-15 first-eligible boundary.

### Cold-start wording

Keep the promised three-run attention retrospective, but state what it can and cannot mean. The stored
`AttentionScore` uses a 60-day window; after three daily runs the additions have only a few days of locally
captured history. Therefore:

- the three-run read tests query relevance, capture shape and early calibration;
- it is **not proof** that the companies are durably under-covered; and
- no company may be removed, re-tiered or have its feed tuned because of that cold-start read.

The mature descriptive read is the first successful run whose 60-day attention window starts no earlier than
the first post-199 collection instant. Record that date once the first run exists. It is an operational
follow-up, not a new efficacy gate and not a reason to delay the Phase B capacity verdict.

## 5. Measure capacity after the one-time seed burst

Use the first **three successful full runs after spec 199** on the merged 94-company seed; because spec 198 was
already merged, these are also the missing post-198 measurements. A successful run is an ordinary complete
default-profile run, not `-WhatIf`, a targeted replay or an interrupted process. It must have durable run
provenance and scoring output. Record failures and partial runs for operations, but do not count them toward
the three.

**Precondition, owed since spec 198 — VERIFIED 2026-08-29 20:25Z: `data/scoring-configs/strategies/` is
EMPTY (cleared 12:35 local), so no deletion is required.** The first-run fingerprint verification was
performed on run 1 (`70f256e3`, 2026-08-29): the first identity recorded for `default` was
`radar-scoring-fp-11240da5aeb0` (log line cited in the §5 record below), so nothing remains owed on this
precondition. (Had records been present: delete or re-record them — git-ignored, never fabricate — or
`StrategyIdentityGuard` halts run 1 before collection; that halt is CORRECT, and a halted run does not count
toward the three.)

The first run is a deliberate one-time seed burst: the 20 additions (21 new newssearch feeds because NWPX has
two, plus GHM's `rss` feed) have no cross-run history, and price history backfills one year per new ticker on
that run (AD-14, outside the pipeline), so its wall-clock and inflow must not be presented as steady-state.
Report it separately. Use runs 2 and 3 to test whether the backlog drains after that burst.

For all three runs, append a table containing:

- run id, UTC start/end, completion status, universe count and total wall-clock duration;
- observations attempted/stored and cross-run-deduped;
- admitted recent-window count and count admitted under first-collection/unfiltered behavior;
- typing calls attempted, completed outcomes, validation/provider failures and `untypedRemaining` at run end;
- collector/source failures and coverage warnings; and
- the live 60-day AI-ON scoring fingerprint, which must be
  `radar-scoring-fp-11240da5aeb0` unless an independently approved later identity boundary has superseded it.

Compute the two steady-state backlog deltas (`run2 - run1`, `run3 - run2`) and the aggregate (`run3 - run1`).
The validation result is:

- **DRAINING:** `untypedRemaining` at run 3 is below run 1, with the per-run deltas shown even if one is noisy;
- **NOT DRAINING:** run 3 is equal to or above run 1; or
- **UNRESOLVED:** a run cannot be proved full and successful, its typing reader/cohort cannot be identified,
  or `untypedRemaining` is absent for any of the three checkpoints. Missing data is never treated as zero.

The other requested columns are diagnostics: render an absent value as `not recorded` and name the alternate
source used if it did not come from the durable run/typing artifacts. An unavailable wall-clock duration, for
example, must be visible but does not fabricate an UNRESOLVED backlog verdict when all verdict-bearing fields
are durable.

If NOT DRAINING or UNRESOLVED, freeze further universe expansion and report it. Do not remove the 20 companies,
raise the typing budget, narrow capture or retune scoring in this spec; each would change the thing being
measured and needs its own measured decision.

## 6. Perform the predeclared three-run attention read honestly

After the third successful run, use the `default` primary-strategy snapshot produced by that run and its exact
`WindowEndUtc`. Do not choose a strategy or run by whichever gives the best fit. Append to
`docs/cohorts/under-covered-2026-08.md` all 20 rows with:

- predicted band (unchanged);
- measured `AttentionScore` after runs 1, 2 and 3;
- run-3 measured band using the predeclared boundaries low `<55`, mid `55–70`, high `>70`;
- hit/miss; and
- a short mechanical explanation for a material miss, without revising the prediction.

Report low-band hit rate, mid-band hit rate, overall hit rate, the exact count above 70 and EPM separately
(EPM is the cohort's declared RISK CASE — `docs/cohorts/under-covered-2026-08.md` line 60: a non-operated,
dividend-paying E&P whose yield attracts investor-platform write-ups, so it may measure as covered for reasons
that have nothing to do with under-coverage; the precommitted reason is investor-platform/dividend coverage,
not commodity sensitivity). A
missing company/snapshot is **unresolved**, remains in the denominator and is not silently dropped. If §2 found
pre-correction history, label the affected row potentially contaminated and report it both in the full table
and in a sensitivity total excluding contaminated rows; never erase it from the primary total.

This is a **cold-start descriptive result**. It can falsify a seed query or expose an unexpectedly noisy name;
it cannot validate the under-coverage business thesis on a 60-day measure with only a few days of capture.
Record the first fully accrued 60-day read date from §4 beside the result. No scoring or universe action follows
automatically from either read.

## 7. Explicit non-goals and invariants

- No company additions, removals, renames or tier changes; universe remains 94.
- No `benchmark-universe-v2`; benchmark-v1 remains byte-identical.
- No score formula, component, weight, strategy, channel, model prompt, typing budget or recency-window change.
- No movement of the 2026-09-29 AD-15 boundary or any AD-15/AD-16 precommitment.
- No use of price, efficacy outcomes or the three-run attention result as a scoring input.
- No evidence/observation/signal/snapshot migration, deletion, replay or backfill.
- No global change to `NewsAttentionCollector.IsRelevant`.
- The seed-only query edits move no scoring fingerprint; all six pins remain unchanged unless an independently
  merged later spec has prospectively moved them.

## Tests and verification

Phase A:

- the three exact seed URL tests and six accept/reject relevance cases pass;
- all existing production-seed, feed-inventory and company-identity tests pass;
- `benchmark-universe-v1.json` is byte-unchanged and no v2 exists;
- the 20 spec-199 ids remain absent from benchmark-v1 and resolve as `NotInBenchmarkUniverse`;
- all six scoring fingerprint pins are unchanged by this diff;
- `dotnet build Radar.sln -c Release` and the full suite pass;
- `git diff --check` is clean; and
- `run-radar.ps1 -Profile default -WhatIf` still resolves 94 companies and the expected live fingerprint.

Phase B:

- every reported metric is derived from durable run/snapshot records, with file/run provenance named;
- the first-run seed burst is separated from the two steady-state deltas;
- the capacity verdict follows §5 exactly and missing data fails closed to UNRESOLVED;
- all 20 attention predictions are retained and the run-3 snapshot choice is fixed as specified; and
- documentation says live scores are immediate while official benchmark-v1 efficacy excludes the additions.

## Acceptance criteria

- [ ] Phase A merges before the first post-199 collection if the boundary is still available; otherwise the
      existing history is quantified and retained, and the correction is explicitly prospective.
- [ ] UTMD, ITIC and ESQ use the exact required query forms, with ESQ added to the colliding-ticker guard and
      public-surface relevance tests covering the adversarial headlines.
- [ ] Spec 199 and `CLAUDE.md` no longer claim that a projected capacity check was measured or satisfied.
- [ ] The docs clearly distinguish immediate live scoring, raw forward-return diagnostics and exclusion from
      benchmark-v1/AD-15 efficacy; no v2 or claim-boundary change is introduced.
- [ ] Three successful full post-expansion runs are recorded; the seed burst is separated; capacity resolves
      DRAINING, NOT DRAINING or UNRESOLVED from durable fields without invented zeroes.
- [ ] The promised 20-row attention retrospective is appended without changing predictions, is labelled
      cold-start, and names the first fully accrued 60-day descriptive read date.
- [ ] No history is rewritten, no scoring identity moves, benchmark-v1 is byte-identical, and the full suite
      and dry-run verification pass.

## §2 record (Phase A, 2026-08-29)

Inspected the durable stores read-only (per-company directories under `data/scores`, `data/signals`,
`data/prices`, `data/news-typing`; `companyId` search in `data/news-observations`; the run records under
`data/runs`; no recursive flat grep) BEFORE editing the three queries.

- **Latest durable run:** `run-20260828T214044920Z-fa50b516-19a6-4129-ab02-151c1260e290.json` — run
  `fa50b516-19a6-4129-ab02-151c1260e290`, created **2026-08-28T21:40:44Z**, `companiesScored: 74`, primary
  `default`, 10 strategies. A **PRE-199** run (spec 199 merged after it).
- **Per-company counts, all three ids:**

  | ticker | company id | score dirs | signal dirs | news-typing dirs | news-observation files | run-record mentions | evidence mentions | price files |
  | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
  | UTMD | `28243c9e-eb18-4a85-acec-8f93aeb8cdef` | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
  | ITIC | `2ae6e6da-b714-416f-9d90-b6432f6eac2b` | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
  | ESQ | `971ea074-e524-4d6d-baf2-ead26449a0dc` | 0 | 0 | 0 | 0 | 0 | 0 | 0 |

- **Outcome: no post-199 history exists — the correction landed before first collection. No migration or
  cleanup is required.** Nothing was deleted, rewritten, re-hashed, reassigned or backfilled. No row of
  `docs/cohorts/under-covered-2026-08.md` is contaminated, and no three-run attention read needs a
  contamination label.

## Phase A status (2026-08-29)

Phase A (§1–§4) is implemented: the three seed urls are corrected, `ESQ` is on the colliding-ticker
allowlist, the exact urls and six adversarial accept/reject headlines are pinned, and the spec-199 doc /
CLAUDE.md bullet / cohort file are amended in place. **§5 (three-run capacity measurement) and §6 (the
20-row attention retrospective) were RECORDED BY PHASE B on 2026-09-03** — see "§5 record (Phase B,
2026-09-03)" and "§6 record (Phase B, 2026-09-03)" below — after the three successful post-199 full runs of
2026-08-29, 2026-08-30 and 2026-09-01; until then this spec stayed in `docs/next/` and was NOT promoted
(the Phase B PR promotes it). The spec-198 operator precondition (clear/re-record
`data/scoring-configs/strategies/{name}.json`, verify `radar-scoring-fp-11240da5aeb0`) was CLOSED by run 1
on 2026-08-29: `logs/baseline-20260829T213002Z.log` carries the line "Recorded first identity for scoring
strategy default: radar-scoring-fp-11240da5aeb0." and `data/scoring-configs/strategies/default.json` holds
that fingerprint (see the §5 record).

## §5 record (Phase B, 2026-09-03)

Recorded 2026-09-03 from the live durable stores under `data/` and the scheduled-run logs under `logs/`,
read only; nothing was rewritten. Every value names its source; a value the sources do not hold is rendered
`not recorded`, never 0. Run 1 is the deliberate ONE-TIME SEED BURST and is reported separately from the two
steady-state runs.

### The three successful post-199 full runs

Qualification, identical for all three: default profile, `RunMode=full`, exit 0, `companiesScored` 94, all
11 strategies executed (run-record `strategies`; `primaryStrategy` `default`), durable run record + report +
news-observation batch present, `collectionWarnings` empty, every `*NotPersisted` counter 0. No failed or
partial run occurred between them, so nothing was excluded from the count.

| field (source) | run 1 — SEED BURST | run 2 | run 3 |
| --- | --- | --- | --- |
| run id (run record `id`) | `70f256e3-1589-44c2-aa7a-85c235bf77b2` | `b6d52f64-0521-4b3a-9f9b-0ae8c5705c32` | `7d4dbce3-f24d-4eff-bd5f-1ebccd5cfc93` |
| run record | `data/runs/2026/08/run-20260829T214452540Z-70f256e3-1589-44c2-aa7a-85c235bf77b2.json` | `data/runs/2026/08/run-20260830T214625510Z-b6d52f64-0521-4b3a-9f9b-0ae8c5705c32.json` | `data/runs/2026/09/run-20260901T025016551Z-7d4dbce3-f24d-4eff-bd5f-1ebccd5cfc93.json` |
| scheduled-run log | `logs/baseline-20260829T213002Z.log` | `logs/baseline-20260830T213001Z.log` | `logs/baseline-20260901T021014Z.log` |
| script start UTC (log `Started:`) | 2026-08-29 21:30:02 | 2026-08-30 21:30:01 | 2026-09-01 02:10:14 |
| run as-of instant (run record `createdAtUtc` = every snapshot's `WindowEndUtc`) | 2026-08-29T21:44:52.5407443Z | 2026-08-30T21:46:25.5105658Z | 2026-09-01T02:50:16.5514898Z |
| pipeline complete (log "Radar pipeline run completed at") | 2026-08-29T22:44:00Z | 2026-08-30T21:47:39Z | 2026-09-01T03:28:15Z |
| script end UTC (log `Ended  :`) | 2026-08-29 23:18:49 | 2026-08-30 22:22:48 | 2026-09-01 08:41:00 |
| total wall-clock (log `Elapsed:`) | 01:48:47 (exit 0) — seed burst, not steady state | 00:52:46 (exit 0) | 06:30:46 (exit 0) — NOT steady-state comparable, see the note below |
| completion status | full, exit 0, report `541ccc3a-2132-40bc-ac1f-8346dce1055e` | full, exit 0, report `72ccffa1-920c-4bb4-8926-2229f32d4a33` | full, exit 0, report `16c6d7ea-e85b-447c-b135-5e36247fd7e6` |
| universe (`companiesScored`) | 94 | 94 | 94 |
| strategies executed (`strategies`) | 11 (`default` primary) | 11 | 11 |
| `evidenceCollected` / `evidenceNew` | 7,252 / 1,903 | 6,870 / 127 | 6,833 / 254 |
| `signalsExtracted` / `signalsApproved` / `signalsNeedingReview` | 1,646 / 1,619 / 27 | 127 / 127 / 0 | 247 / 247 / 0 |
| `sourcesChecked` / `sourcesFailed` | 425 / 3 | 425 / 6 | 425 / 8 |
| collector failures (run record `collectorRuns[].failures[]`: `sourceName` / `reason`) | rss: ServisFirst, Helios, ProPetro (transport error) | rss: Graham Corporation (= GHM, a spec-199 addition), ServisFirst, Helios, ProPetro, Select Water, Cryoport (all transport error) | rss: Graham Corporation (GHM, transport error), Aehr Test Systems (request timed out), ATN International (transport error); newssearch: The Bancorp (TBBK), First Industrial Realty, Hormel (HRL), Winmark (WINA), Mercury Systems (MRCY) (all transport error) — 91/96 newssearch feeds succeeded |
| `collectionWarnings` | none (empty array) | none | none |
| `signalsNotPersisted` / `scoreSnapshotsNotPersisted` / `reportsNotPersisted` / `scoringConfigsNotPersisted` | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| `strategiesSkippedForUnpersistedConfig` | not recorded (field absent on this record) | null | null |
| `hydrationElapsed` / `scoringElapsed` | not recorded (fields absent on this record) | 00:05:15 / 00:00:27 | 00:36:22 / 00:34:05 |
| price acquisition (AD-14, outside the pipeline; log "Price history acquisition complete") | 94/94 tickers, 23,594 bars — this is where the one-year backfill for the 20 new tickers happened | 94/94, 23,594 bars | 93/94, 23,343 bars, 1 source unreadable (JNJ, transport error) |
| news-judgment pass (log "News-judgment pass complete") | 22 judgment records | 21 | 18 |
| live 60-day AI-ON fingerprint for `default` (every `default` snapshot's `scoringConfigVersion`; `data/scoring-configs/strategies/default.json`) | `radar-scoring-fp-11240da5aeb0` — FIRST identity recorded on this run (log "Recorded first identity for scoring strategy default: radar-scoring-fp-11240da5aeb0.") | `radar-scoring-fp-11240da5aeb0` | `radar-scoring-fp-11240da5aeb0` |

**Run 3's wall-clock is not steady-state comparable.** The log's own caveat says a large excess over the
awake-rate expectation means the host was suspended mid-run: the typing stage shows one provider call of
5,214,325 ms (87 min) and stage elapsed 17,779,051 ms, and the run record shows `hydrationElapsed` 00:36:22 /
`scoringElapsed` 00:34:05 against 00:05:15 / 00:00:27 on run 2. The run itself is complete and successful
(exit 0, 94 scored, all durable counters 0), so it counts toward the three; only its duration is excluded
from any steady-state reading.

**The spec-198 operator precondition is CLOSED.** `data/scoring-configs/strategies/` was empty before run 1
(verified 2026-08-29 20:25Z, above), and run 1 recorded the first identity for `default` as
`radar-scoring-fp-11240da5aeb0` (`logs/baseline-20260829T213002Z.log`, "Recorded first identity for scoring
strategy default: radar-scoring-fp-11240da5aeb0."). Every `default` snapshot of runs 1–3 (and of the later
runs 4–5) carries `scoringConfigVersion` `radar-scoring-fp-11240da5aeb0`, the expected live 60-day AI-ON
value. No independently approved later identity boundary superseded it in this window.

### Observations (news-observation batch records)

| field (source: `data/news-observations/batches/{file}` unless stated) | run 1 — SEED BURST | run 2 | run 3 |
| --- | --- | --- | --- |
| batch file / `batchId` | `20260829T214452Z.json` / `cfad07d0-91e6-43d9-b4eb-ff5c73e9a575` | `20260830T214625Z.json` / `462024ca-579c-4997-9294-6765d3292c3d` | `20260901T025016Z.json` / `69f0133c-6f34-4ba4-aa1a-31a41f5f1bc3` |
| `observationsAttempted` / `observationsWritten` / `observationsCrossRunDeduped` / `observationsFailed` | 1,240 / 693 / 547 / 0 | 888 / 114 / 774 / 0 | 821 / 215 / 606 / 0 |
| `captureProven` / `fullUniverse` | true / true | true / true | true / true |
| newssearch feeds expected / succeeded (sum over `collectors[0].companyCoverage[]` of `expectedFeedCount` / `successfulFeedCount`) | 96 / 96 | 96 / 96 | 96 / 91 |
| companies with `confirmedLocalTruncation` / sum of `unadmittedRelevantTailItemCount` (same `companyCoverage`) | 50 / 1,326 | 35 / 512 | 31 / 372 |
| recency window (log "News search recency-window audit"; configured window `when:7d`) | 75 feeds issued the windowed query, **21 issued the unfiltered first-collection query**; 20 companies on first collection | 96 windowed / 0 unfiltered / 0 companies on first collection | 96 windowed / 0 / 0 |
| admitted under the windowed (recent-window) query vs under first-collection/unfiltered behaviour (ALTERNATE SOURCE — see below) | **239 windowed (the 74 incumbents) / 454 unfiltered first-collection (the 20 additions)** — total 693 | 114 windowed (29 for the 20 additions + 85 incumbents) / 0 unfiltered | 215 windowed (40 additions + 175 incumbents) / 0 unfiltered |

**Alternate source for the admitted split.** The batch record does not split admitted observations by query
mode. The split was derived from the observation store `data/news-observations/observations/2026/{08,09}/*.json`
by counting files whose `firstObservedAtUtc` falls inside each run's script window (run 1 2026-08-29
21:30–23:20Z; run 2 2026-08-30 21:30–22:23Z; run 3 2026-09-01 02:10–08:42Z), split by whether `companyId` is
one of the 20 spec-199 ids. The per-run totals (693 / 114 / 215) reconcile EXACTLY to `observationsWritten`,
which validates the method. Of the 454 run-1 observations for the additions, 332 carried a `publishedAtUtc`
at least 7 days before observation — items the 7-day window would have excluded, admitted only because first
collection is unfiltered (spec 198 §2). All 20 companies' first-collection feeds hit the effective result
limit of 25 on run 1 (per-company `companyCoverage` rows, `unfilteredFirstCollectionFeedCount` = 1 per
company, 2 for NWPX — NWPX's two feeds counted jointly, because the coverage rows and observation files are
per-company).

### Typing (the verdict-bearing field)

Typing reader on every run: `deepinfra-deepseek` (openai:deepseek-ai/DeepSeek-V4-Flash), cohort key
`openai:deepseek-ai/DeepSeek-V4-Flash|news-typing-prompt-v1|news-typing-schema-v1|news-event-taxonomy-v1`;
budget 350 new typings per run (`Radar:NewsResearch:Typing:MaxNewTypingsPerRun=350`, unchanged by this
spec). The pre-199 reference column is the last pre-expansion run, for the seed-burst comparison only.

| field | pre-199 reference (2026-08-28, run `fa50b516`) | run 1 — SEED BURST | run 2 | run 3 |
| --- | --- | --- | --- | --- |
| source | `data/news-typing/live/attention-decomposition-2026-08-28.json` (`readerSummaries[0]`) | `data/news-typing/live/attention-decomposition-2026-08-29.json` (`runId` 70f256e3, `readerSummaries[0]`) | `data/news-typing/live/attention-decomposition-2026-08-30.json` (`runId` b6d52f64, `readerSummaries[0]`) | **`logs/baseline-20260901T021014Z.log`** (ALTERNATE SOURCE — the durable artifact was overwritten, see the defect note): the lines "350 new typing(s) this pass — lanes: 5 retry, 120 judgment-candidate priority, 225 general (1816 untyped observation(s) remain)", "350/350 call(s) attempted, 340 persisted completed typing(s), failures 0 provider / 0 parse / 10 validation" and "2 observation(s) have spent all 3 typing attempt(s)" |
| `providerCallsAttempted` | 350 | 350 | 350 | 350 |
| `completedOutcomesPersisted` | 340 | 341 | 344 | 340 |
| provider / parse / validation failures | 0 / 0 / 10 | 0 / 0 / 9 | 0 / 0 / 6 | 0 / 0 / 10 |
| `retryExhausted` | 0 | 1 | 2 | 2 |
| `reservationsRefused` / `outcomeWritesFailed` / `reservedWithoutOutcome` | 0 / 0 / 0 | 0 / 0 / 0 | 0 / 0 / 0 | not recorded (the log does not print these three, and the artifact that would have is overwritten) |
| lanes retry / judgment-candidate priority / general (`retrySelected` / `candidatePrioritySelected` / `generalSelected`; log "lanes:") | 13 / 150 / 187 | 10 / 150 / 190 | 8 / 150 / 192 | 5 / 120 / 225 |
| **`untypedRemaining` at run end** | 1,821 | **2,172** | **1,941** | **1,816** |

The log value and the artifact value are the SAME quantity from the same code path (`NewsTypingGenerator`):
on runs 1 and 2 the log lines read "2172 untyped observation(s) remain" and "1941 untyped observation(s)
remain" while the artifacts say 2172 and 1941 — identical. So run 3's log figure is the same measure, read
from its only surviving source.

### Backlog deltas and verdict

- **Seed burst, reported separately:** run 1 RAISED the backlog from 1,821 (2026-08-28, pre-199) to
  **2,172 (+351)** — the one-time effect of 454 unfiltered first-collection observations for the 20
  additions (plus the 20 tickers' one-year price backfill on the same run, outside the pipeline). This is
  not steady state and is not part of the two steady-state deltas.
- **Steady-state deltas:** `run2 − run1` = 1,941 − 2,172 = **−231**; `run3 − run2` = 1,816 − 1,941 =
  **−125**; aggregate `run3 − run1` = 1,816 − 2,172 = **−356**.
- **Verdict: DRAINING.** `untypedRemaining` at run 3 (1,816) is below run 1 (2,172), with both per-run
  deltas negative. All three verdict-bearing checkpoints are present: runs 1 and 2 from the durable typing
  artifacts, run 3 from its scheduled-run log (named above). Had that log not existed the verdict would have
  been **UNRESOLVED** — missing data is never treated as zero — and that dependence is stated plainly here.
- **Against the projection:** the steady-state drain measured **125–231 per run** against spec 199's
  PROJECTED ~54/run (the projection was pessimistic); steady-state admitted inflow measured 114–215/run
  against the projected ~286 upper bound.
- **Supplementary, NOT part of the verdict:** the next two successful runs have DURABLE artifacts and
  continue the drain — run 4 `35b57cfd-defc-48d5-bf8c-751248d701de` (as-of 2026-09-01T21:46:09Z,
  `data/news-typing/live/attention-decomposition-2026-09-01.json`) **1,688**; run 5
  `fd2575b7-b60a-451e-8cc9-1f3a004ece57` (as-of 2026-09-02T21:50:03Z,
  `attention-decomposition-2026-09-02.json`) **1,585**.
- **Consequence:** universe expansion is NOT frozen by this verdict (spec 207's gate reads this verdict).
  No typing-budget, capture, scoring or universe change follows from this spec.

### Defect FOUND, recorded, NOT fixed here (out of Phase B scope; needs its own spec)

`FileNewsTypingArtifactStore` writes the decomposition artifact to
`{root}/live/attention-decomposition-{asOfDate}.md|.json`, keyed by as-of DATE. Two successful full runs
happened on 2026-09-01 (run 3 at 02:50Z and run 4 at 21:46Z), so run 4 silently OVERWROTE run 3's artifact:
`attention-decomposition-2026-09-01.json` carries `runId` `35b57cfd`, and run 3's durable typing accounting
exists only in its log. This violates "nothing may be discarded without being counted". Every other §5
field for run 3 IS durable (run record, batch record, snapshots); only the typing accounting fell back to
the log. The fix is owed to a follow-up spec (see Phase B status).

## §6 record (Phase B, 2026-09-03)

**Fixed snapshot, chosen in advance:** the run-3 `default` snapshot, `WindowEndUtc`
2026-09-01T02:50:16.5514898Z (= run 3 `createdAtUtc`), `scoringConfigVersion` `radar-scoring-fp-11240da5aeb0`,
read from `data/scores/{companyId}/{snapshotId}.json` with `strategyName` `default`; `AttentionScore` is the
snapshot's `attentionScore`. Run-1 and run-2 values are the same company's `default` snapshots at
`WindowEndUtc` 2026-08-29T21:44:52.5407443Z and 2026-08-30T21:46:25.5105658Z. Bands are the predeclared low
< 55 / mid 55–70 / high > 70. All 20 companies have a run-1, run-2 and run-3 `default` snapshot — **0
unresolved**; §2 found zero pre-correction history, so **0 contaminated** and the sensitivity total
excluding contaminated rows equals the primary total. No predicted band was revised.

| ticker | companyId | predicted | run 1 | run 2 | **run 3** | run-3 snapshot id (prefix) | run-3 band | hit? |
| --- | --- | --- | ---: | ---: | ---: | --- | --- | --- |
| GHM | `03d83435-a25a-46d9-8c0b-ccd6ae2f6370` | mid | 32 | 32 | **36** | `ac505f40` | low | MISS |
| CLMB | `3ec8fa0f-0be7-425b-9d18-d3f2cf9c7cce` | mid | 38 | 38 | **38** | `62803935` | low | MISS |
| UTMD | `28243c9e-eb18-4a85-acec-8f93aeb8cdef` | low | 35 | 35 | **42** | `d3d55444` | low | HIT |
| MLAB | `6546421b-76ce-40cf-ad5a-67850e8a6a14` | mid | 32 | 32 | **35** | `1ce63fcd` | low | MISS |
| JOUT | `345739cf-7b3b-4a5e-9282-9a08d00dbf95` | mid | 41 | 43 | **43** | `6d932235` | low | MISS |
| FLXS | `977ab7ef-84c4-4415-914e-225445f5bf77` | low | 33 | 39 | **42** | `7e981812` | low | HIT |
| ITIC | `2ae6e6da-b714-416f-9d90-b6432f6eac2b` | low | 29 | 29 | **29** | `6635aad9` | low | HIT |
| ESQ | `971ea074-e524-4d6d-baf2-ead26449a0dc` | mid | 28 | 31 | **33** | `8b059925` | low | MISS |
| SGA | `b9114ba4-4a9e-444a-b295-e5ee4d6e75c3` | low | 31 | 31 | **33** | `9cf28cd6` | low | HIT |
| OOMA | `6abb319e-7404-4771-a7e4-1392afcdd106` | mid | 39 | 40 | **42** | `2ce874ff` | low | MISS |
| JBSS | `9033c658-5451-4eb6-a75d-6ee934e0d0ae` | mid | 42 | 45 | **46** | `f67751cd` | low | MISS |
| SENEA | `28a12288-2bef-4e98-9c20-2243eb8c7a3a` | low | 46 | 52 | **57** | `ac18f802` | mid | MISS |
| NWPX | `5a2b18f8-a612-4d50-9987-4aa182205a5f` | mid | 45 | 46 | **46** | `161fec2b` | low | MISS |
| KOP | `0ca2bef7-aebf-4e2b-acdc-d5383c5e9acf` | mid | 35 | 37 | **39** | `33a0419a` | low | MISS |
| GEOS | `77b0bd01-7b66-4b56-b192-954476183ec6` | low | 26 | 26 | **26** | `c27176ce` | low | HIT |
| EPM | `a86391fc-476a-41ef-8fae-b259b052eec9` | mid (declared RISK CASE) | 36 | 39 | **40** | `6391adf7` | low | MISS |
| CTO | `2f6db469-4729-4c7b-af2c-89ec30b7285b` | mid | 33 | 33 | **34** | `4437e7ea` | low | MISS |
| OLP | `de02b7db-8c25-4252-96a1-64093e3a5e3a` | low | 36 | 36 | **33** | `04896c1a` | low | HIT |
| UTL | `db2f28fc-75f0-42c7-9480-04dd4b5e4326` | low | 35 | 35 | **37** | `f1955a46` | low | HIT |
| RGCO | `1f2c8b48-1daa-41fb-bd08-1cbdc30dcb3b` | low | 36 | 36 | **36** | `304b86d5` | low | HIT |

**Totals (run-3 band against predicted band):**

- **low-band hit rate 8/9** — SENEA is the miss (measured 57, mid).
- **mid-band hit rate 0/11** — every mid prediction measured low (range 33–46).
- **overall 8/20 = 40 %.**
- **count above 70: 0 of 20** — the "clustered above 70 ⇒ heuristic FAILED" test is NOT triggered.
- **EPM (declared RISK CASE): 40, low** — predicted mid. The precommitted risk (investor-platform/dividend
  coverage making it measure as COVERED) did NOT materialise in this cold-start window; the miss is in the
  opposite direction (less attention than predicted) and is the same cohort-wide cold-start effect as the
  other ten mid misses.
- **unresolved rows: 0; contaminated rows: 0**; sensitivity total excluding contaminated rows = 8/20
  (identical to the primary total).

**Supplementary, later durable snapshots (context only, NOT in the hit/miss):** run 4 (as-of
2026-09-01T21:46:09Z) / run 5 (as-of 2026-09-02T21:50:03Z) — GHM 36/36, CLMB 38/39, UTMD 42/42, MLAB 35/36,
JOUT 46/46, FLXS 43/47, ITIC 29/33, ESQ 34/34, SGA 33/33, OOMA 43/50, JBSS 49/49, SENEA 57/57, NWPX 48/51,
KOP 41/41, GEOS 26/26, EPM 42/44, CTO 34/34, OLP 35/38, UTL 38/38, RGCO 36/39.

**Mechanical explanation of the material miss (the whole mid band measuring low) — no prediction is
revised.** `AttentionScore` is computed over a 60-day window (run-3 snapshot `windowStartUtc` 2026-07-03 →
`windowEndUtc` 2026-09-01), but the 20 additions were first collected at 2026-08-29T21:44Z, so by run 3 they
held roughly three days of capture: one unfiltered first-collection pull capped at the 25-item retained
prefix per feed, then two 7-day-windowed pulls admitting 29 and 40 observations across all 20. Incumbents
carry a full 60 days of accrual (spec 199 measured the `small` tier at mean 62.0). The cohort is therefore
mechanically depressed as a whole (26–57), and the within-cohort mid/low separation cannot be tested yet —
which is exactly the §4 cold-start caveat. Within the cohort the ordering tracks capture volume (per-company
`companyCoverage` rows in the run-2 and run-3 batch records): SENEA, the only row to reach mid (57),
saturated the 25-item retained prefix on runs 2 AND 3 (`maxValidItemsObserved` 49 with 24 unadmitted
relevant tail items each run) — the noisiest name in the cohort by relevant volume within a 7-day window;
OOMA (63 → 48 valid items, 37 → 22 tail) and SGA (69 → 67 valid items, but 0 relevant tail) also hit the
limit. GEOS (26, the lowest) returned 0 and 1 valid items on runs 2 and 3. GHM's rss IR feed failed with a
transport error on runs 2 and 3 (recorded in `collectorRuns`), so its attention rests on newssearch alone.

**What this read is and is not.** It tested query relevance (every one of the 20 companies returned
relevant items on run 1; no company returned zero relevant items in all three runs), capture shape and early
calibration. It does NOT validate the under-coverage thesis, and **NO company is removed, re-tiered or
feed-tuned** as a result. No scoring or universe action follows from it.

## §4 mature read date (Phase B, 2026-09-03)

First post-199 collection instant = run 1 as-of **2026-08-29T21:44:52Z**. The mature descriptive read is the
first successful run whose `WindowEndUtc` ≥ **2026-10-28T21:44:52Z** (that instant + 60 days), i.e. the
first run whose 60-day attention window starts no earlier than first collection. Operationally: the
2026-10-28 nightly slot IF its as-of instant falls at or after 21:44:52Z (the slot's as-of has ranged
21:44:52Z–21:50:03Z across the nightly-slot runs 1, 2, 4 and 5; run 3 was the 02:50Z off-slot run),
otherwise the 2026-10-29 slot. It is an operational follow-up,
descriptive only — not an efficacy gate.

## Phase B status (2026-09-03)

Phase B is complete: the §5 three-run capacity record, the §6 20-row cold-start attention read and the §4
mature read date are recorded above from the named durable sources. **Capacity verdict: DRAINING** (2,172 →
1,941 → 1,816; seed burst +351 reported separately; steady-state deltas −231 and −125). This PR promotes the
spec to `docs/`. Phase B changed no code, test, seed, config or data; spec 200 moved no scoring fingerprint
in either phase.

Owed follow-ups:

1. **The mature 60-day attention read** of the 20 additions — first successful run with `WindowEndUtc` ≥
   2026-10-28T21:44:52Z; descriptive only, no gate.
2. **A spec for the artifact-overwrite defect**: stop `FileNewsTypingArtifactStore`'s date-keyed
   `attention-decomposition-{asOfDate}` artifact silently overwriting an earlier same-day run (run 3 of §5
   lost its durable typing accounting this way).
3. **Spec 207** may now read the DRAINING verdict at its gate.

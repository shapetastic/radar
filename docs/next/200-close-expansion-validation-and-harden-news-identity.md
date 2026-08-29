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

**Precondition, owed since spec 198 and still not done:** before run 1, consciously delete or re-record every
configured `data/scoring-configs/strategies/{name}.json` (git-ignored — never fabricate them), otherwise
`StrategyIdentityGuard` halts run 1 before collection. That halt is CORRECT and must not be bypassed; a halted
run does not count toward the three.

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
(EPM is the one addition whose obscurity case in `docs/cohorts/under-covered-2026-08.md` rests on a
commodity-price-sensitive business, so its attention may move with the sector rather than with coverage). A
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

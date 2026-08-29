# Task: Expand the watch universe with genuinely small caps — 74 → ~94

## Overview

The universe has sat at 74 companies since spec 166 (2026-07-31). Two things have changed that make expansion
the right call now rather than a scaling risk.

**Capacity stopped being the binding constraint.** Spec 198's recency filter cut the retained news prefix from
~100 items per company to ~19, and the measured projection moved new observations per run from 152 to 210
against a typing budget of **350/run**. The backlog therefore drains ~140/run instead of ~58 — roughly 13 runs
to clear 1,821 rather than 31. Adding ~20 companies (+27 %) takes inflow to ~267/run, still draining ~83/run
(~22 runs). **Expansion is affordable now in a way it demonstrably was not a week ago.** §5 requires this to be
re-measured against the first post-198 run rather than taken on the projection.

**And the universe itself may be why nothing validates.** The Lead arm's evidence status reads "no evidence of
discrimination yet"; a media-count baseline currently out-performs every research arm out of sample. The
current leaderboard top is two water utilities and a payments processor. **A universe too small or too tame to
contain a genuine early-stage improver cannot demonstrate the method works, however carefully it is measured.**
Waiting for validation before expanding is circular when the universe is a plausible cause of the null result.

The composition argues the same way. Of 74 companies only **35 are `followingTier: small`**; 32 are `mid`, and
7 are `large`/`mega` benchmark controls. So barely half the universe is the thing Radar exists to look at.

## Assignment

Worktree: any

Dependencies: spec 198 merged. Every existing company, evidence item, observation, signal, snapshot and
efficacy artifact remains immutable; **no existing company is removed, renamed or re-tiered.**

Estimated time: ~1–1.5 days, dominated by seed research and feed verification, not code.

## 1. What to add: ~20 genuinely UNDER-COVERED companies

Target **~94 companies total**. At least three quarters `followingTier: small`, the remainder `mid` only where
genuine obscurity is argued (see below). This shifts the balance from 35/74 small today to roughly 50–55/94, so
the majority of the universe becomes the under-covered names Radar exists for.

### The selection variable is COVERAGE, not market cap — decided, with the evidence

`followingTier` is curated from **following/coverage evidence only** and is never derived from price, market
cap or volume (AD-14). So "small cap" and `followingTier: small` are different things, and the live data shows
the curated tier is only a weak proxy for what Radar actually scores:

| tier | n | attention min | mean | max |
| --- | ---: | ---: | ---: | ---: |
| small | 35 | 46 | **62.0** | 79 |
| mid | 32 | 56 | **66.0** | 90 |
| large | 2 | 67 | 74.5 | 82 |
| mega | 5 | 58 | 71.6 | 90 |

**Four points of mean separation and near-total overlap** — a `small` company reaches 79 while a `mega` sits at
58. Selecting purely on market cap would therefore optimise the wrong variable.

**The rule: select on being UNDER-COVERED. Small cap is the prior, not the test.** In practice most additions
will be small caps because they dominate the under-covered end, but an unloved mid-cap industrial nobody
writes about is a better Radar target than a small cap with a retail following. **Up to a quarter of the batch
may be `followingTier: mid`** where the case for genuine obscurity is explicit; the rest are `small`.

⚠ **This is a HYPOTHESIS, and it must be recorded as one.** Coverage cannot be measured for a company that is
not yet in the universe — it has no observations. So seed-time selection is a prediction, and §5 makes it
falsifiable: record a **predicted attention band** (low < 55 / mid 55–70 / high > 70) per addition, and check
it retrospectively. If the additions cluster at the high-attention end, **the selection heuristic was wrong
and that is a finding to report**, not something to quietly absorb.

Selection rules, applied in order:

- **US-listed operating companies** (NASDAQ/NYSE), consistent with the existing seed.
- **Genuinely under-covered — the primary criterion.** The practical test is Radar's own: would this company's
  news volume be dominated by aggregators rather than editorial outlets? Do NOT add a name because it is
  interesting — add it because it is plausibly un-noticed. **Record the reason AND the predicted attention band
  per company.** A useful sanity check: would an engaged private investor plausibly have heard of it? If yes,
  it is probably already noticed.
- **Not already represented.** No duplicate ticker, CIK or company identity.
- **Must have a working SEC submissions feed** (`data.sec.gov/submissions/CIK…json`) — this is the load-bearing
  one. Filings are the highest-quality evidence source and the arms under test are disclosure/filings-led.
- **Prefer sector spread.** The current spread is 10 Industrials / 9 Technology / 8 Healthcare / 8 Consumer
  Cyclical / 7 Financial Services / 7 Communication Services / 6 Consumer Defensive / 5 each Basic Materials,
  Energy, Real Estate. Do not concentrate the additions in one sector; a sector-correlated batch would confound
  the efficacy read with a sector bet.
- **No thematic clustering** for the same reason — spread across the existing `themes` vocabulary rather than
  adding twenty names from one story.

Each addition needs the full seed shape already used: `id` (a fresh `Guid`), `name`, `legalName`, `ticker`,
`exchange`, `countryCode`, `sector`, `industry`, `followingTier` (`"small"`, or `"mid"` for the minority with
an argued case), `aliases`, `themes`, and `sourceFeeds`.

## 2. Feeds must be VERIFIED, not assumed

For every added company, verify each feed resolves before committing it:

- **`sec`** — `data.sec.gov/submissions/CIK{10-digit}.json` returns 200 and the CIK matches the company.
  **Required**; a company without a working SEC feed is not added.
- **`secform4`** and **`sec13dg`** — the same submissions document drives these; add them consistently with
  existing entries.
- **`rss`** — a company press-release feed where one exists and returns valid RSS. **Optional**: a name with no
  press-release feed is still worth having, and a broken feed is worse than an absent one.
- **`newssearch`** — the Google News query phrase. Choose it deliberately: a phrase that is ambiguous with
  another company, a common word, or a place name will poison relevance. The spec-196 audit found three local
  newsrooms covering **Otter Tail County, Minnesota** matched to OTTR by name alone — do not add a company whose
  query phrase has that shape without disambiguating it.

⚠ **Respect SEC fair access.** Verification is a burst of requests against `data.sec.gov`. Pace it, use the
configured `Radar:Sec:UserAgent`, and stop on the first 403 rather than retrying — an unpaced burst has
previously self-blocked `www.sec.gov` for this repo.

## 3. What must NOT change

- **The benchmark universe stays frozen.** `benchmark-universe-v1.json` has 74 members and a pinned content
  hash. Spec 183's rule is explicit: expansion is a **prospective `benchmark-universe-v2`**, never an edit.
  New companies will correctly report `NotInBenchmarkUniverse` on the pooled leaderboard until such a v2 is
  declared, and that is the honest behaviour — **do not create v2 in this slice.**
- **No scoring change.** No formula, weight, tier map, rule set, strategy, channel budget or config default.
  The universe is not a hashed input, so **no fingerprint moves and no identity records need clearing** — the
  first slice in weeks where that is true. Assert it: the pins are unchanged.
- **No new collector, feed kind or provider.**
- **No existing company modified.** Additions only; `followingTier` on existing names is untouched.
- **No typing/judgment budget change.** §5 measures the effect; a budget change is a separate decision.

## 4. Expected operational consequences, recorded not discovered

State these in the PR so the first post-199 run is read correctly and nothing reads as a regression:

- **Per-run scorings rise from 740 to ~940** (94 companies × 10 strategies).
- **~4 additional feeds per company** (≈80 more), so collection wall-clock rises. The 2026-08-28 baseline ran
  1h49m; expect proportionally longer, and confirm it stays inside the scheduled window.
- **Price history backfills one year per new ticker on the first run** — a one-off cost outside the pipeline.
- **New companies have NO accrued evidence**, so their first scores are thin and their attention is low. They
  will look artificially attractive on an inverse-attention discount before their evidence accrues. **Do not
  read an early high rank for a new company as a finding**; note it in the report period's interpretation.
- **They enter the efficacy series with zero history**, so they contribute no in-sample observations for ~21+
  days and will initially show as partial forward windows.

## 5. Measure the capacity claim against reality

Per CLAUDE.md's live-distribution rule, this spec's central premise — that capacity now permits expansion — is
a projection and must be checked against the **first post-198 baseline** before the additions land, and again
after. Report in the PR:

- observations captured per run, and the new-vs-cross-run-deduped split, post-198 versus the 2026-08-28
  baseline of 234 new / 1,370 deduped;
- typing: in-window observations, typed, untyped remaining, and the resulting drain per run; and
- the projected post-199 drain at ~94 companies.

**Record the selection hypothesis so it can be judged later.** Commit the per-company predicted attention band
alongside the seed (a short table in the PR and in the spec's own record). After **three** post-199 runs, compare
predicted against measured `AttentionScore` and report the hit rate. If the additions cluster ABOVE 70, the
under-covered heuristic did not work and that is a reportable finding — it would mean seed-time judgement cannot
identify under-covered names, which is worth knowing before any further expansion.

**The ship condition is that the typing backlog is still draining after expansion.** If the measured post-198
drain is materially below the projected ~140/run, say so and reduce the batch size rather than shipping the
full ~20 — the point is to expand as far as capacity genuinely allows, not to a round number.

## 6. Tests

- The seed loads: 94 companies, all new entries `followingTier: small`, no duplicate `id`, ticker or CIK.
- Every added company has a resolvable `sec` feed; the feed-inventory validator's declared-vs-reached counts
  reconcile and the shrinkage warning does not trip.
- `benchmark-universe-v1.json` is byte-unchanged and its content hash still verifies; a new company resolves as
  `NotInBenchmarkUniverse` on the pooled path rather than being silently admitted.
- **No fingerprint moves** — all six pins unchanged, `ScoringConfigFingerprintTests` untouched.
- Existing companies are byte-identical in the seed.

## Acceptance criteria

- [ ] ~20 US-listed companies added, selected on being UNDER-COVERED rather than on market cap, at least three
      quarters `followingTier: small` and any `mid` carrying an explicit obscurity case; spread across sectors
      and themes; each with a recorded reason, a **predicted attention band**, and a verified SEC feed.
- [ ] The selection hypothesis is committed and scheduled for a three-run retrospective against measured
      attention, with a clustering-above-70 result reported as a failed heuristic rather than absorbed.
- [ ] No existing company is modified, removed or re-tiered; no duplicate identity.
- [ ] `benchmark-universe-v1` is untouched and new companies report `NotInBenchmarkUniverse`; no v2 is created.
- [ ] No scoring, formula, weight, tier-map, strategy or config change; **all six pins unchanged** and no
      identity-record clear required.
- [ ] The capacity premise is measured against the first post-198 run, and the batch is sized to what the
      measured drain supports.
- [ ] Expected consequences (≈940 scorings/run, longer collection, price backfill, thin early scores) are
      stated in the PR and the lineage.
- [ ] `dotnet build Radar.sln -c Release` and the full suite pass; `git diff --check` clean; on Windows
      `run-radar.ps1 -Profile default -WhatIf` still resolves.

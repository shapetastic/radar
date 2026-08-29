# Task: Filter the news feed by recency — stop spending the collection budget on year-old articles

## Overview

Radar queries Google News per company with **no time filter**, retains the first 25 items in document order,
and only *then* dedupes against what it already has. Measured against the live endpoint on 2026-08-28 for
one company phrase:

| query | items | median age | ≤1 day | ≤7 days | >30 days | oldest |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| unfiltered (today's behaviour) | **100** | **71 days** | 3 | 10 | **63** | **303 days** |
| `…+when:2d` | **3** | — | 3 | 3 | 0 | 2 days |

**The feed is overwhelmingly historical.** Two thirds of every response is over a month old, the median item
is ten weeks old, and only three items are recent. Radar's 25-slot window is therefore filled almost
entirely with aged articles it has already seen and correctly discarded on previous nights.

The 2026-08-28 baseline shows the cost: **1,604 items retained → 234 new, 1,370 cross-run deduped.**
**85 % of the collection budget is spent re-reading known articles**, leaving roughly 3–4 slots per company
per night for genuinely new material.

**This also retires spec 190's open question.** That audit reported *"4,312 additional unique
company-relevant tail items observed but not admitted"*, which reads like missed coverage. Given the
measured median age of 71 days, that tail is overwhelmingly **old** news. An earlier draft of this spec
proposed selectively admitting it by publisher tier; that was **wrong** and is abandoned — it would have
carefully imported more stale articles while leaving the actual waste untouched.

What Radar captures today is genuinely fresh (of last night's 234 new observations, 159 were ≤1 day old and
194 ≤7 days), so this is **not** a "scoring on stale news" defect. It is a capacity defect: the budget is
consumed before it can be spent on anything that matters, and headroom is only ~3–4 new articles per company
per night.

## Assignment

Worktree: any

Dependencies: specs 190, 196, 197 merged. Every existing observation, evidence item, typing, family,
judgment, signal, snapshot and efficacy artifact remains immutable. Nothing is backfilled.

Estimated time: ~1.5–2 days.

## 1. Add a recency window to the query

Add `Radar:News:RecencyWindowDays` (int, default **7**), applied by `HttpNewsSearchReader` as a
`when:{n}d` term appended to the search phrase, URL-encoded through the existing builder. `0` or absent
disables the filter and reproduces today's unfiltered query **byte-for-byte** — that is the compatibility
proof, and it is what every test asserting the current URL shape continues to exercise.

**Why 7 and not 1 or 2.** The baseline runs daily, so a 1–2 day window has no margin: the 2026-08-26 run
fired 23 minutes late and a missed night would open a permanent gap, because a skipped article never
reappears in a narrower window. Seven days tolerates several consecutive failures while still cutting a
100-item response to a handful. The redundancy is free — cross-run dedupe already discards it, and that is
precisely what it is for.

Do **not** make the window adaptive, derive it from the last successful run, or vary it per company. A fixed
declared window is reproducible; a clock-derived one is not (AD-3).

**Verify the operator against the live endpoint during implementation**, since it is undocumented: confirm
`when:{n}d` still bounds the response and does not silently degrade to unfiltered. If it ever stops
filtering, the collector must behave exactly as it does today — the failure mode is "no improvement", never
"no results".

## 2. Preserve history for a company Radar has not seen before

The unfiltered query is currently the only way a newly-seeded company acquires any back history. Under a
7-day window a new company would start empty, which silently changes what seeding means.

On a company's **first** collection — no prior observation exists for it in the archive — issue the
**unfiltered** query once, exactly as today, and apply the window on every subsequent run. Decide it from
persisted state, never from a timestamp comparison. Record which mode each feed used in the existing
per-company coverage diagnostic so the split is visible rather than inferred.

## 3. Make the change comparable

The feed query determines which evidence exists, so it changes `AttentionReach`, `OpportunityScore` and
every rank — and the query is **not** currently a hashed scoring input, so today this would move silently.
That is the same comparability hole spec 194 §2 closed for judgment configuration.

Add the effective recency window to `ISignalSourceDescriptor.CanonicalDescriptor()` — the identity side, not
`CollectionProvenance` — composed through `DescriptorEscaping`. Assert that the disabled/`0` configuration
reproduces the **current post-197 pins exactly** (so the segment is additive, not a silent re-stamp), and
that the shipped default of 7 moves all six pins **once**, deliberately. Both AI-off and AI-on pins move:
this segment is not judgment-gated, so an unchanged AI-off pin means it is not actually hashed.

Recompute all six 30/60/120-day pins, update `ScoringConfigFingerprintTests`, the operator-facing
`scripts/run-profiles/default.json` record and the `CLAUDE.md` lineage.

**Operator action, which cannot ride in the PR:** `data/scoring-configs/` is gitignored, so every
`data/scoring-configs/strategies/{name}.json` must be deleted after merge and before the next baseline or
`StrategyIdentityGuard` halts before collection. That halt is correct. This is the sixth identity boundary
in three weeks; name it in the lineage.

## 4. Report the live distribution

Per CLAUDE.md's **"no measure ships without its live distribution"**, measure the query change directly
against the live endpoint — read-only, no admission, nothing persisted — across the full 74-company feed
set, and report both arms:

- items returned per company, unfiltered versus windowed: min / median / max;
- **age distribution of returned items** in both arms, which is the measurement that motivated this spec;
- projected retained-slot usage: how many of the 25 slots the windowed query would consume, and the
  projected new-versus-deduped split against last night's 234 / 1,370 baseline; and
- companies for which the windowed query returns **zero** items, which is expected and not a fault — it
  means nothing was published about them this week.

Then, from a read-only paired counterfactual at one fixed as-of instant over the 74-company universe and the
primary `default` strategy, varying only the recency window and persisting nothing: distinct-publisher
breadth actually consumed by `AttentionReach`, and the `AttentionScore`/`OpportunityScore` distributions,
before and after.

**State the direction honestly.** Fewer stale articles means less redundant `MediaAttention`, so attention
may fall and opportunity may rise. That is expected. What must not happen is *coverage* falling — the count
of genuinely recent articles admitted must be **at least** today's. If the windowed arm admits fewer recent
items than the unfiltered arm, the window is too narrow and the PR must say so rather than ship it.

## 5. Explicit non-goals

- **No change to `Radar:News:MaxRecordsPerCompany`** (stays 25), the absolute 100-item ceiling, request
  count, pacing, or the number of requests per company.
- **No selective tail admission by publisher tier.** The earlier draft's approach is abandoned: the tail is
  old, not missed.
- No change to the relevance rule, URL dedupe, evidence identity (spec 145) or the spec-197 join.
- No typing-budget change. If freeing slots materially increases genuine inflow, that is a measured
  follow-up against spec 189's budget, not an assumption to bake in here.
- No attention weight re-tuning (spec 196 left that open on its own evidence).
- No backfill and no re-collection of history; the change is forward-only.
- No new collector, feed, provider or query beyond the appended term.

## 6. Tests

- The disabled/`0` window produces a **byte-identical** URL to today's, pinned against the current literal.
- A configured window appends exactly `when:{n}d`, correctly encoded, with the phrase otherwise unchanged.
- First-collection detection: a company with no prior observation gets the unfiltered query; one with prior
  observations gets the windowed query; the decision comes from persisted state, not a clock.
- A response that ignores the filter still yields today's behaviour — degraded to "no improvement", never to
  dropped results.
- Fingerprint: window `0` reproduces the post-197 pins exactly; a changed window moves the stamp; both AI-off
  and AI-on move under the shipped default.
- Retained-prefix mechanics, the diagnostic tail, dedupe, mapping and observation capture are otherwise
  unchanged, pinned against pre-198 output.

## Acceptance criteria

- [ ] A configurable recency window (default 7 days) is applied to the news query; `0`/absent reproduces
      today's URL byte-for-byte.
- [ ] A company's first collection remains unfiltered so seeding still acquires history; the mode used is
      recorded per feed.
- [ ] The recency window is a hashed `ScoringConfigVersion` input; window `0` reproduces the post-197 pins;
      the shipped default moves all six once, with lineage and the operator step stated.
- [ ] The live measurement reports item counts and **age distributions** for both arms, projected slot usage
      and the new-versus-deduped split against the 234 / 1,370 baseline.
- [ ] The paired counterfactual reports distinct-publisher breadth and both score distributions before and
      after, and confirms the count of genuinely recent articles admitted does **not** fall.
- [ ] `MaxRecordsPerCompany`, the 100-item ceiling, request count and pacing are unchanged; no tail item is
      admitted.
- [ ] `dotnet build Radar.sln -c Release` and the full suite pass; `git diff --check` clean; on Windows
      `run-radar.ps1 -Profile default -WhatIf` still resolves.

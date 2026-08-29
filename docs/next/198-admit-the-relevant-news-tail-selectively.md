# Task: Admit the relevant news tail selectively — by curated publisher tier, capped, and comparably

## Overview

Spec 190 built a read-only audit of what Radar sees beyond its local retention limit and deliberately
deferred the decision. The first full post-197 baseline (2026-08-28) reported it:

> 74 company/companies reached the effective LOCAL retention limit of **25**; **74 confirmed a response tail
> beyond it**; **4,312 additional unique company-relevant tail item(s) observed but not admitted**; observed
> valid response size across 75 successful feeds: **max 100, median 100**; admitted under the retained
> prefix: 1,604 evidence items, 1,604 observation candidates.

So every company hits the cap, every company has more behind it, and Radar discards roughly **three
quarters of every response** — 4,312 items per run that its own relevance rule already judged
company-relevant.

**Naive admission is not available, and the arithmetic says so.** Admitting the tail takes observation
inflow from 1,604 to ~5,916 per run — **3.7×** — against a spec-189 typing budget of **350 per run**, with
**1,821 already untyped** and only 1,528 of 3,428 in-window observations typed (44.6 %). The backlog would
grow by roughly 4,250 every run and typing coverage would collapse toward zero, taking the grounded
judgment path down with it. Raising `MaxRecordsPerCompany` alone would therefore *reduce* what Radar
understands while increasing what it stores.

**Two things are not yet known, and this spec must not pretend otherwise:**

1. **What the tail is made of.** Spec 190 counts tail items; it does not characterize them. We cannot say
   whether the tail brings genuinely different publishers or simply more output from the same aggregators
   that spec 196 measured at ~50 % of volume. That determines whether admitting it changes `AttentionReach`
   at all, since reach counts **tier-weighted distinct publishers per company**, not article volume.
2. ⚠ **Whether the change would even be comparable.** `Radar:News:MaxRecordsPerCompany` is **not a hashed
   scoring input**, and `CollectionProvenance` records only the collector CSV. Changing the retention limit
   today would move attention, opportunity and every rank **under an identical `ScoringConfigVersion`** —
   the exact comparability hole spec 194 §2 closed for judgment configuration. Any admission change must
   close it in the same slice or it is silently un-auditable.

This spec therefore decides a **bounded, curated admission** rather than a limit raise, characterizes the
tail in the same pass so the rule can be judged, and makes the retention policy comparable.

## Assignment

Worktree: any

Dependencies: specs 190, 196 and 197 merged. Every existing observation, evidence item, typing, family,
judgment, signal, snapshot and efficacy artifact remains immutable.

Estimated time: ~2 days.

## 1. Characterize the tail before and while admitting it

Extend spec 190's diagnostic so the tail is described, not merely counted. For each run record, per company
and in aggregate:

- tail items by **resolved publisher tier** (`Genuine` / `Platform` / `Mill` / `Wire` / unclassified), using
  the spec-196 typed resolver — **one resolver, no second copy**;
- how many tail items come from a publisher **already present in that company's retained prefix** versus a
  publisher **new to that company this run**; and
- the top unclassified tail publishers by volume, top 10, deterministically ordered (spec 196's ordinally
  smallest-variant fold).

This answers the open question directly: if the tail is overwhelmingly `Mill` and from publishers already
in the prefix, admitting it adds storage and typing load without adding breadth or perspective. If it
carries `Genuine`/`Platform` sources the prefix truncated away, it is exactly what Radar is missing.

Record it beside spec 196's existing capture-flow summary, as a trailing nullable extension with its own
version token; a pre-198 batch hydrates it as **null = not recorded, never zero**. It remains a
capture-flow diagnostic and is not the `AttentionScore` input.

## 2. The admission rule: curated tier, capped, provably bounded

Admit a tail item **only** when all of the following hold. Everything else stays observed-and-counted,
exactly as today.

- its publisher resolves to **`Genuine` or `Platform`** under the spec-196 tier map — the two tiers whose
  definitions require a human to have chosen to write about *this* company. `Mill`, `Wire` and unclassified
  publishers are **never** admitted from the tail, because spec 196 established that those publish on every
  ticker by construction and admitting them buys volume, not selection;
- it passes the **existing** company-relevance rule and URL dedupe unchanged, against both the retained
  prefix and earlier tail items;
- it is within a **per-company cap** (`Radar:News:MaxAdmittedTailItemsPerCompany`, default **5**); and
- it is within a **per-run global cap** (`Radar:News:MaxAdmittedTailItemsPerRun`, default **200**),
  applied deterministically — companies in ascending `CompanyId`, items in feed order — so a run that hits
  the global cap admits the same set every time (AD-3).

**The caps are the load-bearing safety property.** 200 additional observations per run against a 350-per-run
typing budget is absorbable; 4,312 is not. State the expected steady state explicitly in the PR: admitted
tail volume, resulting total inflow, and the projected effect on the untyped backlog. **If the measured
`Genuine`+`Platform` tail is larger than the global cap, report the shortfall rather than silently raising
the cap** — that becomes the evidence for a later capacity decision, exactly as spec 189's drain prediction
did.

An admitted tail item becomes an ordinary observation **and** ordinary `NewsArticle` evidence through the
existing mapper — no second admission path, no special-casing downstream. It is indistinguishable from a
prefix item once admitted, except for one trailing nullable provenance marker recording that it arrived
from the tail, so the cohort can be measured later.

`Radar:News:MaxRecordsPerCompany` **stays at 25**. This spec does not raise the retention limit; it admits a
small, curated set from what the reader already loaded.

## 3. Make the retention/admission policy comparable

Because §2 changes which evidence exists, it changes `AttentionReach`, `OpportunityScore` and every rank —
and today that would happen under an unchanged `ScoringConfigVersion`.

Add a **news-admission policy segment** to `ISignalSourceDescriptor.CanonicalDescriptor()` — the identity
side, not `CollectionProvenance` — carrying the effective retention limit, the two caps and the admitted
tier set. Compose it through `DescriptorEscaping`; do not hand-roll delimiters.

Assert:

- changing either cap, the retention limit or the admitted tier set produces a **different**
  `ScoringConfigVersion`;
- the pre-198 configuration reproduces the **current** post-197 pins exactly, so the segment is additive
  rather than a silent re-stamp of unchanged behaviour; and
- the shipped default configuration moves the pins **once**, deliberately.

Recompute all six 30/60/120-day AI-off/AI-on pins, update `ScoringConfigFingerprintTests`, the
`scripts/run-profiles/default.json` operator record and the `CLAUDE.md` lineage. **Both AI-off and AI-on
pins move here** — unlike spec 197, this segment is not judgment-gated, so an unchanged AI-off pin would
indicate the segment is not actually hashed.

**Operator action, which cannot ride in the PR:** `data/scoring-configs/` is gitignored, so every
`data/scoring-configs/strategies/{name}.json` must be deleted after merge and before the next baseline, or
`StrategyIdentityGuard` halts before collection. That halt is correct. This is the sixth identity boundary
in three weeks; name it in the lineage rather than burying it.

## 4. Report the effect, per the live-distribution rule

Per CLAUDE.md's **"no measure ships without its live distribution"**, report from a read-only paired
counterfactual at one fixed as-of instant over the current 74-company universe and the primary `default`
strategy, varying **only** the admission policy and persisting nothing:

- tail composition by tier, and how many items the rule would admit;
- distinct-publisher breadth per company actually consumed by `AttentionReach`, before and after — this is
  the scoring unit, not article volume; and
- the `AttentionScore` and `OpportunityScore` distributions, before and after: min, max, mean, decade
  histogram.

**State the direction honestly.** Admitting `Genuine`/`Platform` publishers *raises* breadth, which raises
attention, which — because attention is an inverse discount — *lowers* `OpportunityScore`. That is expected
and is not a defect: it means Radar has learned the company is better covered than it thought. What would
be a defect is admitting `Mill` volume and calling the resulting attention "notice", which §2's tier gate
exists to prevent.

If the measured effect is negligible because the tail is almost entirely `Mill`, **say so plainly**. That is
a real and useful result: it closes the "4,312 items we are throwing away" question with evidence, and the
correct follow-up would be a collection-source decision rather than a limit decision.

## 5. Explicit non-goals

- **No raise of `Radar:News:MaxRecordsPerCompany`**, no extra request, page, redirect or change to pacing;
  the tail comes from the response the reader already loaded, under spec 190's unchanged absolute ceiling.
- No new collector, feed or provider.
- No change to the relevance rule, URL dedupe, evidence identity (spec 145) or the observation↔evidence join
  (spec 197).
- No typing-budget change; if the caps prove too small, that is a separate measured decision.
- No attention weight re-tuning — spec 196 left that open on its own evidence and this spec does not
  pre-empt it.
- No backfill: the historical tail is gone and is not reconstructed. Admission is forward-only.

## 6. Tests

- A feed whose response exceeds the retention limit admits exactly the `Genuine`/`Platform` tail items
  within both caps; `Mill`, `Wire` and unclassified tail items are never admitted, and a mutation making
  the gate tier-blind turns the test red.
- Caps bind deterministically: the same corpus admits the same set regardless of feed or company iteration
  order, and a global-cap-exceeded run reports the shortfall.
- An admitted tail item is byte-identical to an equivalent prefix item downstream apart from its tail
  provenance marker; evidence identity and the spec-197 join are unaffected.
- The retained prefix, request count, pacing and collection order are **unchanged** — pinned against
  pre-198 output.
- Fingerprint: the pre-198 configuration reproduces the post-197 pins exactly; each of the retention limit,
  both caps and the admitted tier set moves the stamp; both AI-off and AI-on pins move under the shipped
  default.
- The tail-composition diagnostic conserves — tier counts sum to the observed tail count — and hydrates
  null on a pre-198 batch.

## Acceptance criteria

- [ ] The tail is characterized by publisher tier and by prefix-overlap, using the spec-196 typed resolver,
      recorded durably with its own version token and null-on-legacy semantics.
- [ ] Only `Genuine`/`Platform` tail items are admitted, within a per-company cap of 5 and a per-run cap of
      200, deterministically; everything else remains observed-and-counted.
- [ ] `Radar:News:MaxRecordsPerCompany` remains 25; no additional request, page or pacing change.
- [ ] The admission policy is a hashed `ScoringConfigVersion` input; the pre-198 configuration reproduces
      the post-197 pins; the shipped default moves all six pins once, with lineage and the operator step
      stated.
- [ ] The paired counterfactual reports tail composition, distinct-publisher breadth actually consumed, and
      both score distributions before and after — with the honest direction stated, or a plain statement
      that the effect is negligible because the tail is predominantly `Mill`.
- [ ] Typing impact is quantified: admitted volume, resulting inflow and projected backlog effect against
      the 350-per-run budget.
- [ ] `dotnet build Radar.sln -c Release` and the full suite pass; `git diff --check` clean; on Windows
      `run-radar.ps1 -Profile default -WhatIf` still resolves.

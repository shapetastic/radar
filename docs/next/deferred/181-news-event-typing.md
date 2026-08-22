# Task: News event typing — answer "what kind of it" for every observed article

> ⚠️ **DEFERRED — DO NOT DISPATCH until the entry condition in §1 is met.** This spec is written now so
> the fix is scheduled with a measured entry gate, not rediscovered later. Move it to `docs/next/` only
> after §1's numbers exist and §3's taxonomy is finalized from them.

## Overview

Radar's news pipeline answers "how many outlets covered it" (spec 109/122 same-event collapse, tier-weighted
breadth) but never "what kind of *it* was covered." A patent grant, a CEO indictment, an index inclusion, a
short-seller report and a meme squeeze are indistinguishable: one Neutral `MediaAttention` tick each. Measured
against the store on 2026-08-22: **13,027 of 18,471 raw evidence files (70.5%) are news articles, and none
was ever read.** The 2026-08 EOSE case made the cost concrete — ~43% of its `filings-led-v2` composite was
unread coverage of its own collapse.

This slice types every point-in-time news observation in the spec-177 archive against a closed, versioned
event-type taxonomy, with cited excerpts and mechanical validation — the spec-179 task shape applied to the
general case instead of the risk subset. The payoff is **attention decomposition**: "EOSE: 24 observations
this window — 14 FinancingOrDilution, 6 IndexOrTradingMechanics, 4 ProductOrTechnology" instead of
"attention 76."

It is read-side and shadow: no score, label, strategy, fingerprint or report rank changes. Typed attention as
a scoring input is explicitly a later, separately named strategy spec.

## Assignment

Worktree: any
Dependencies: spec 177 merged (archive); spec 179 merged **and its first cohort measured** (§1).
Estimated time: ~1.5–2 days once unblocked.

## 1. Entry condition — the numbers this spec is blocked on

Spec 179 is the pilot for this spec: same task shape (closed taxonomy, cited excerpts, fail-closed
validation) on the highest-stakes slice of event types. Before this spec is dispatched, record here, from at
least two full 179 runs:

- per-reader citation-validation drop rate (total/accepted/dropped claims and reasons);
- per-reader assessment completion rate (completed vs `InsufficientContent`/`ValidationFailed`/provider
  failure);
- category agreement between readers, if a second reader cohort exists (the DeepSeek-vs-Ollama question —
  this spec inherits 179's `Readers` seam, and the pilot decides whether the backfill can run on the local
  model);
- observed per-assessment cost/latency, to size the §6 backfill honestly.

If 179's validation shows the model cannot reliably classify short news text with exact citations, this spec
does not proceed on hope: the remedy is a prompt/schema revision in 179's cohort machinery first.

## 2. What "typing" is and is not

One observation → one primary event type (plus optional secondaries), with relevance and cited support. It
is **not** sentiment: valence stays where it already lives (179's risk read; the earnings AI read). Typing an
article as `FinancingOrDilution` records what the coverage is about, not whether it is bad — 179 already
answers the bad. The two reads are complementary and their outputs are stored separately.

Closed per-observation schema:

```text
Relevance        CompanySpecific | SectorOrMacroContext | NotAboutThisCompany | InsufficientContent
PrimaryType      one taxonomy entry (§3)
SecondaryTypes[] zero or more taxonomy entries
Confidence       0..1
SupportingExcerpts[]   exact substrings of supplied text
```

Mechanical validation mirrors spec 179 §6, including its definition of archived text (the union of fields
actually supplied). Invalid or uncited claims are dropped and counted; all-invalid results are
`ValidationFailed`, never a silent default type.

## 3. Taxonomy v1 — PROVISIONAL until finalized from data

The taxonomy is versioned (`news-event-taxonomy-v1`), hashed into every typing record's cohort identity, and
immutable by convention (change ⇒ v2, cohorts never pool across versions). The candidate set below is a
**strawman recorded for review, not a decision**:

```text
EarningsOrGuidance | MergerAcquisitionOrStake | FinancingOrDilution | ProductOrTechnology |
ContractOrCustomerWin | RegulatoryOrLegal | ManagementOrGovernance | AnalystOrRatingAction |
IndexOrTradingMechanics | ShortSellerOrCritique | DividendOrBuyback | PromotionalOrListicle |
OtherSpecified
```

Finalization procedure (part of this spec's implementation, before any prospective claim):

1. Stratified sample of ≥200 archived observations (across companies, publishers, capture modes).
2. Type them with the pilot reader(s); a human audits the sample — merge types the model confuses, split
   types that hide distinct behaviour, drop types with near-zero support.
3. Declare `news-event-taxonomy-v1` with its hash; record the audit sample and decisions beside it.

`PromotionalOrListicle` and `IndexOrTradingMechanics` are deliberately present in the strawman: they are the
"coverage that says nothing about the business" buckets whose *identification* is half this spec's value.

## 4. Execution, cohorts and storage

- Reuses spec 179's `Readers` seam verbatim: each configured reader types independently; cohort identity is
  provider + model + prompt/schema/taxonomy version; cohorts never pool; no merged verdict.
- Runs as a post-run step beside the 179 shadow generator for new observations, plus a standalone catch-up
  command for backlog. Both are bounded by `MaxNewTypingsPerRun` (mirroring 179's cap precedent); cache by
  input + cohort identity so nothing is typed twice.
- Persist every attempt under `data/news-typing/{model-policy}/...` with the full 179-style provenance
  (observation ids, hashes, status, raw-response hash, created-at).
- Capture-mode cohorts stay separate: `ProspectiveRss` (headline + description), `LegacyHeadlineOnly`
  (headline only — expect lower confidence and more `InsufficientContent`; that is honest, not a defect),
  `RetrospectiveUrlFetch` (never presented as point-in-time).

## 5. Output: attention, decomposed

Write `data/news-typing/live/attention-decomposition-{asOfDate}.md` and `.json`: per company, the window's
observation count broken down by event type and reader, with publisher breadth per type, the same-event
family count beside the raw count (so 40 syndicated copies of one financing story render as one family), and
per-company completeness (unproven capture/typing backlog marks the company incomplete, never silently
partial).

The artifact carries:

> Event typing describes what coverage was about. It is not a sentiment, a risk assessment or a score input,
> and a type distribution is not a recommendation.

## 6. Backfill posture

The 13,027-article legacy backlog is typed as `LegacyHeadlineOnly`, incrementally under the per-run cap —
sized from §1's measured cost, never in one unbounded pass. Headline-only typing is expected to be weaker;
its cohort separation is what keeps that honesty visible. No evidence file is rewritten; typing records are
a parallel store keyed by observation id.

## 7. Out of scope, recorded not built

- Typed attention as a scoring input (an event-type-aware breadth channel, filtering
  `PromotionalOrListicle`/`IndexOrTradingMechanics` from attention, a typed-coverage strategy): each is a
  NEW named strategy/formula spec with its own fingerprint story, declared prospectively.
- Sentiment/valence for general news (179 owns risk; anything broader is its own spec).
- New providers or fetching beyond the spec-177 archive.

## Acceptance criteria

- [ ] §1's entry numbers are recorded in this spec before dispatch; taxonomy v1 declared via §3's procedure.
- [ ] Every typing claim is citation-validated against the exact stored text; failures are explicit.
- [ ] Reader/capture-mode/taxonomy cohorts never pool; no merged verdict.
- [ ] Attention decomposition renders per company with family counts beside raw counts.
- [ ] No score, label, strategy, fingerprint, report rank or AD-15/AD-16 claim changes.
- [ ] Build and coordinated tests green.

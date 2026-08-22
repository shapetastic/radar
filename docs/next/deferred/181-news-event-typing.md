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

**This spec is STAGE 1 of a two-stage read architecture (maintainer decision, 2026-08-22).** The MOTIVATING
misreads in this repo were *judgment* failures — llama saw EOSE's −70% gross margin and said "Improving";
DeepSeek saw CASS's prior-year securities loss and called the doubling Positive; and the fix that worked
(spec 160's comparability scan) is precisely a fact-extraction stage mechanically constraining a judgment.
That is motivation, not proof that all failures are judgment failures: spec 162's measured 36.7%
false-OMISSION rate shows extraction/recall failure exists too (or at least that a combined read cannot
localize which stage failed) — which is itself an argument for the split, since only separated stages make
recall and judgment separately measurable. So extraction and direction-weighing are SEPARATED: this spec is the fact/event
layer ("what kind of it, and what did it say"), receiving NO directional question; the stage-2 spec
(**spec 185, drafted alongside this amendment so the consumer contract shapes the layer it consumes**) adds
the direction judge, which consumes ONLY the typed, citation-validated fact layer — never the raw persuasive
prose — and cites fact ids, extending the provenance chain to judgment → fact → excerpt → observation →
archive. Benefits this structure is chosen for: the judge never sees engineered headline framing; facts are
extracted once and re-judged many ways (rubric changes re-run stage 2 only — decisive for the 13k backfill);
extraction recall and judgment quality become separately measurable (spec 162 measured a 36.7%
false-omission rate and could not localize it); and an asymmetric reader split (cheap local extractor,
stronger judge) becomes testable.

**Omission-bias guard at the new interface:** stage 1 decides what is "pertinent", and a fact it drops is a
fact stage 2 can never see — the omission failure mode reborn at a new seam. Therefore stage 1 EXTRACTS
LIBERALLY and stage 2 filters, never the reverse; the raw text stays archived (177) so nothing is ever
unrecoverable; and stage-1 recall is measured against the §3 audited sample as a first-class number.

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

**Facts are the typed unit; the observation-level type is derived.** One headline routinely carries several
events — a real EOSE example holds `EarningsOrGuidance` (wider Q2 loss), `RegulatoryOrLegal` (legal
scrutiny) and `MarketReaction` (down 11.8%) in one sentence — so each extracted fact carries its OWN event
type(s), temporal scope, confidence and citations, and the observation's primary type is derived from its
facts for display, never authored separately.

**The extractor withholds the VERDICT, not the factual direction.** The contract, exactly: *preserve actors,
quantities, periods, comparisons, negation, modality and attribution exactly; do not assign investment
direction, severity or materiality.* "Loss widened from X to Y", "shares fell 11.8%", "guidance changed from
X–Y to A–B", "a plaintiff law firm announced an investigation" are FACTS — stripping "widened"/"fell"/
"lowered", negation, modality or numerical baselines would neuter the layer and recreate the original
blindness at a new boundary. What stage 1 never emits: Positive/Negative, severity, materiality,
ThesisChallenged.

**Epistemic status and attribution are first-class**, because stage 2 never sees the prose: an SEC
investigation vs a plaintiff-firm shareholder solicitation, a confirmed filing vs a publisher assertion,
"may face" vs "was charged" must be distinguishable from the fact record alone — otherwise the fact layer
launders headline framing into apparent certainty.

It is **not** sentiment and receives no directional question: valence stays where it already lives (179's
risk read; the earnings AI read) until spec 185's judge consumes this layer. The two reads are complementary
and their outputs are stored separately.

Closed per-observation schema:

```text
Relevance        CompanySpecific | SectorOrMacroContext | NotAboutThisCompany | InsufficientContent
DerivedPrimaryType   derived from the facts below for display, never authored
Facts[]          extracted LIBERALLY (stage 2 filters; stage 1 never pre-judges materiality), each:
  FactId
  EventTypes[]         one or more taxonomy entries (§3)
  Statement            preserving actors/quantities/periods/comparisons/negation/modality/attribution
  TemporalScope
  Attribution          who asserts it (company | regulator | plaintiff firm | publisher | analyst | ...)
  AssertionStatus      confirmed-filing | reported | alleged | solicited | speculative | ...
  Confidence           0..1
  Citations[]          exact substrings of supplied text
```

Facts carry NO family id: the extractor works one observation at a time and must not invent
cross-observation identifiers. Families are SEPARATE RECORDS built by §4's deterministic post-extraction
pass, which reference member FactIds — stage 2 consumes those canonical families, never N syndicated copies
of one claim.

The fact shape and the `Attribution`/`AssertionStatus` vocabularies are finalized through the same §3 audit
procedure as the taxonomy. Stage-1 recall over the audited sample (facts a human judged pertinent that the
extractor missed) is recorded as a headline number beside the citation-drop rate. Same-event family
collapse happens BEFORE judgment: the two near-identical "StocksToTrade" legal-scrutiny headlines in the
live EOSE bundle are one family, and syndication volume must never reach the judge as repetition — the
40-outlets problem must not be reborn at the judgment seam.

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
MarketReaction | IndexOrTradingMechanics | ShortSellerOrCritique | DividendOrBuyback |
PromotionalOrListicle | OtherSpecified
```

(`MarketReaction` added on review: a stock falling after earnings is a price-move report, not
`IndexOrTradingMechanics` — conflating them would misfile the most common headline kind there is.)

Finalization procedure (part of this spec's implementation, before any prospective claim):

1. Stratified sample of ≥200 archived observations (across companies, publishers, capture modes).
2. Type them with the pilot reader(s); a human audits the sample — merge types the model confuses, split
   types that hide distinct behaviour, drop types with near-zero support.
3. Declare `news-event-taxonomy-v1` with its hash; record the audit sample and decisions beside it.

`PromotionalOrListicle` and `IndexOrTradingMechanics` are deliberately present in the strawman: they are the
"coverage that says nothing about the business" buckets whose *identification* is half this spec's value.

## 4. Execution, cohorts and storage

### The family builder — versioned, deterministic, post-extraction

`fact-family-v1` is a separate deterministic pass over the run's extracted facts (never a model call, never
the extractor's job — AD-3): it groups facts asserting the SAME claim about the same company, so syndication
reaches the judge as one family with size metadata. Rules:

- **Key**: companyId + overlapping `EventTypes` + normalized-statement similarity (the versioned
  normalization is part of the family-builder identity) + temporal proximity. The existing media collapse is
  NOT reused — it is a time-window bucket that can merge unrelated same-day stories; family membership here
  means *same claim*, not *same day*.
- **Conflict handling**: facts with contradictory statements (different quantities for the same measure,
  negated vs asserted) never share a family, however similar their text — a family is one claim, and
  merging a contradiction would erase exactly the information stage 2 needs to see.
- **Representative selection is deterministic**: earliest `firstObservedAtUtc`, then lowest FactId; family
  metadata records member count, distinct publishers and the full FactId membership list.
- **Lifecycle: checkpoint SNAPSHOTS, not incremental accretion.** At each checkpoint the builder runs over
  ALL qualifying validated facts in that window for exactly ONE extractor cohort — never only the newly
  extracted facts (which would miss duplicates from earlier runs). The output is a persisted checkpoint
  family SET with each family's complete member list; a later run writes a new snapshot, never edits an old
  one.
- **Family ids are stable under changing membership**: derived from builder version + company + the stable
  canonical-claim key — NOT from the member list — so a later-arriving member joins the same family id at
  the next checkpoint instead of minting a sibling and leaving the old family active.
- **The full builder definition enters the cohort identity**: builder version, statement normalization,
  similarity metric AND threshold, and the temporal window are all part of `fact-family-v1`'s identity —
  changing any of them is `fact-family-v2`, a new cohort dimension, never an edit.
- **Pinned fixtures, minimum set**: syndicated duplicate variants collapse to one family; unrelated same-day
  stories do NOT merge; contradictory claims (different quantities for one measure) do NOT merge; a rerun
  over identical facts is byte-deterministic; a later-arriving member joins the existing family id in the
  next snapshot with the prior snapshot unchanged.

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

- **The stage-2 direction judge** — **spec 185** (deferred beside this one, drafted FIRST so this layer is
  built against a real consumer contract), consuming ONLY canonical fact/event families by FactId, citing
  them in every claim, and A/B-measured as a new cohort against the spec-179 single-call read
  (citation-drop, category agreement, and the localized extraction-vs-judgment error split) rather than
  asserted better. The asymmetric split (local extractor + stronger judge) is one of its cohorts. Its
  end-to-end acceptance case is the EOSE chain: facts extracted and attributed → duplicated legal stories
  collapse to one family → the judge records a thesis challenge → EOSE cannot render as an unqualified
  leader.
- Typed attention as a scoring input (an event-type-aware breadth channel, filtering
  `PromotionalOrListicle`/`IndexOrTradingMechanics` from attention, a typed-coverage strategy): each is a
  NEW named strategy/formula spec with its own fingerprint story, declared prospectively.
- Sentiment/valence for general news (179 owns risk until stage 2 lands; anything broader is its own spec).
- New providers or fetching beyond the spec-177 archive.

## Acceptance criteria

- [ ] §1's entry numbers are recorded in this spec before dispatch; taxonomy v1 declared via §3's procedure.
- [ ] Every typing claim is citation-validated against the exact stored text; failures are explicit.
- [ ] Reader/capture-mode/taxonomy cohorts never pool; no merged verdict.
- [ ] Attention decomposition renders per company with family counts beside raw counts.
- [ ] No score, label, strategy, fingerprint, report rank or AD-15/AD-16 claim changes.
- [ ] Build and coordinated tests green.

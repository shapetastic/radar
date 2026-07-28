# Task: Characterize which channels can carry usable variation — INPUT ONLY, before the live arm is finalised

> **Spec 157 predeclares `filings-led-v11` / `filings-led-v10-control` with an insider .50 / institutional
> .30 / breadth .20 budget. There is real doubt that budget can produce a usable ranking — and equal doubt
> about every proposed replacement.** Spec 153 measured, on the live window, that of 32 companies with an
> active `sec-form4` channel **13 were all-Neutral, 18 net-negative and 1 positive**; spec 156 found
> `InstitutionalOwnership` is **98.79 % Neutral by design** (spec 99) and `InsiderBuying` **77.89 %**.
>
> A channel that scores 0 for nearly every company contributes nothing to a ranking, and under the
> **never-renormalise** rule its weight is simply lost. Three weeks of accrual would confirm a feasibility
> problem that is measurable **today**, from inputs alone.

## ⚠️ Three claims that must NOT be assumed — this spec exists to test them

An earlier analysis asserted all three without adequate evidence. Do not carry them in:

1. **"Insiders are net selling 11.7:1, so the insider channel scores 0."** That ratio is over the **whole
   accrued store** (`observedAt` spans 2006–2026). Scoring uses a **60-day window**. An aggregate says
   nothing about any individual company's windowed channel. Spec 153's per-company live-window measurement
   is the relevant evidence, and this spec must re-derive it rather than cite it.
2. **"~75 % of positive signal comes from press releases/news."** That was inferred from *signal type*, not
   from producing collector. Actual attribution requires evidence resolution, which stands at **11.44 %**
   (spec 156). **Use real collector attribution and report its coverage as its own number.**
3. **"Breadth is the channel left standing."** Probably backwards. Spec 157 §3 narrows breadth to publishers
   carrying a **Positive** signal — but `NewsArticle` evidence always becomes **Neutral** `MediaAttention`
   (spec 70), so no news publisher can ever qualify, and RSS is **first-party**, so it is not a third-party
   publisher. **Breadth under §3 may be structurally zero for every company.** This is the single most
   important question here, because it would invalidate the §3 decision itself.

## Design

### 1. INPUT ONLY — this is what keeps AD-16 intact

**No forward outcome is computed, read or inspected. No price. No attention outcome. No efficacy statistic.**
AD-16's pre-commitment clause binds the *outcome*; characterizing *inputs* before declaring an arm is
legitimate design work and does not consume the pre-commitment. Looking at any forward quantity here would.

If a reader could learn from this document how any strategy performed, it has gone too far.

### 2. Fixed, declared window

The as-of instant is pinned **here, before implementation**:

> **D = `2026-07-28T08:04:27.7605621Z`**, the common `WindowEndUtc` of the latest completed full-collection
> baseline run present when this spec was accepted (`PipelineRunRecord`
> `120c99e2-2b8d-4831-99aa-1f02a0d58896`).

Use exactly the 60-day observation window `(2026-05-29T08:04:27.7605621Z, D]`, plus the spec-136 knowledge
predicate `CreatedAtUtc <= D`. Do not substitute the audit execution time, the newest signal timestamp, or a
later pipeline run. Do not sweep several windows and pick one. Repeat the instant and the run-record id in
the findings so the result is reproducible.

### 3. Per candidate channel, per company

Use the concrete, exact `IEvidenceCollector.CollectorName` values that scoring matches ordinally — not the
`Radar:Collectors` kind tokens. The candidate collector channels are `sec-form4`, `sec-13dg`,
`RssPressReleaseCollector`, `newssearch`, `usaspending`, `sec-edgar` and `fda`. The display may add the
friendly label `rss`, but the computation must use `RssPressReleaseCollector`.

First report the scoring-input eligibility funnel, globally and per company:

1. Approved signals satisfying the observation-window and known-at predicates;
2. signals dropped because their evidence id does not resolve;
3. resolved `ScoringSignal`s before and after `GuidanceChangeSupersede` and `MediaAttentionCollapse`;
4. among the resolved scoring inputs, collector attribution resolved as **recorded**, **inferred** or
   **unattributed**.

These are different states. `ScoringEngine` drops missing-evidence signals **before** collector attribution,
so they must be reported as `evidence-unresolvable`, never relabelled `unattributed`. A resolved-but-
unattributed signal is consumed by no collector channel; consequently a collector channel's consumed set can
contain only recorded or inferred attribution. Report the unattributed pool globally/per-company rather than
inventing a collector for it.

Then, for each candidate collector channel and for the **breadth** channel under spec 157 §3's positive-only
rule, report over the 43 companies:

- **directional activity mass** (the v11 rule: Neutral excluded), and how many companies have any at all;
- **companies whose channel score would be > 0** — the headline feasibility number, since
  `max(0, preponderance)` floors a net-negative channel at 0;
- the count of distinct `(directional activity mass, preponderance)` pairs and each term's cross-company
  variance. These are the saturation-independent structural inputs; do not fabricate a channel score for a
  collector that has no declared saturation;
- **preponderance sign distribution** (all-neutral / balanced / net-positive / net-negative).

**Collector attribution must be real, not inferred from signal type.** Use the spec-146 recorded `collector`
metadata, with spec-151's `Radar:Scoring:InferLegacyCollectorAttribution` for the legacy cohort — and report
**recorded vs inferred vs unattributed counts separately**, because 151's inference is validated only for
`newssearch`, `sec-form4` and RSS, while `sec-edgar`, `sec-13dg`, `usaspending` and GDELT are reasoned, not
ground-truth validated. An unattributed signal is consumed by no collector channel and must be counted in
the resolved-unattributed pool, not silently dropped.

### 4. One production math path — prospective primitives are allowed, a copied audit formula is not

The characterization must be a small **C# audit path** referencing the production projects. A PowerShell
script may launch it and format/check its output, but must not contain a second implementation of scoring.
It must reuse the durable stores and the production window/known-at/review filtering, evidence join,
`GuidanceChangeSupersede`, `MediaAttentionCollapse`, collector-attribution resolver, recency/quality factors,
`ScoreSignalMath` and `ScoringChannelComposition`.

Two prospective v11 terms do not exist as production primitives yet. It is in scope for this slice to extract
them once into shared, pure scoring helpers/seams, with all existing formula call sites retaining their
current delegates and arithmetic:

- collector directional activity is exactly `DirectionalMasses(...).Total`;
- positive-only breadth applies `Direction == Positive` to both the post-collapse and pre-collapse inputs
  **before** the existing third-party publisher, tier-weight, collapsed-publisher-credit and media-count
  terms are evaluated. Thus a Neutral `MediaAttention` signal contributes zero to this prospective breadth
  term, even when the same publisher has some other Positive signal. **Publisher inclusion stays BINARY and
  DISTINCT** — a publisher qualifies on **at least one** Positive signal and is counted **once**, never
  earning extra reach for carrying several; and **Negative signals are excluded alongside Neutral**, from
  the media-count term as well as from publisher reach. `AttentionScore` is **not** filtered and stays over
  the full gated set. Spec 157 §3 states the same rule normatively; the two must not drift.

If `ScoringChannelComposition` needs a breadth-reach delegate so the audit can call the same composition
path, add that seam and make v9/v10 pass the existing `AttentionReach` explicitly. Their outputs and
floating-point order must remain byte-identical. Spec 157 must reuse these exact helpers; it may not add a
second positive-reach implementation later.

This does **not** ship or register `radar-formula-v11`, configure a strategy, write a snapshot, or move a
fingerprint. It establishes shared prospective primitives while keeping every currently shipped formula
behaviourally unchanged.

### 5. Answer the breadth question explicitly

Report, as its own result: **how many distinct publishers carry at least one Positive signal, per company
and both as a cross-company sum and a globally de-duplicated total**, and how many companies would have
non-zero §3-narrowed breadth. Use the exact prospective term in §4, including the post/pre-collapse split,
tier weights, collapsed-publisher credit and media-count result. State plainly whether a first-party RSS
feed is counted as a publisher by the existing reach computation, since that determines whether the answer
is "small" or "zero".

If §3-narrowed breadth is structurally zero, say so — that is a finding about spec 157's design decision and
must be reported as such, not worked around here.

### 6. Separate the measured facts from the arm decision

First evaluate the currently predeclared `filings-led-v11` budget exactly as written in spec 157
(insider `sec-form4` .50 / institutional `sec-13dg` .30 / breadth .20; saturations 2 / 3 / 3), using the
prospective v11 terms but without persisting a strategy or snapshot. Report the distribution of its **final
stored-shape integer `OpportunityScore`**, after the declared weights, current-at-D `AttentionScore`
notedness discount, following-tier discount and normal `[0,100]` rounding:

- companies with `OpportunityScore > 0`;
- distinct integer score count;
- largest tie-group size;
- cross-company variance.

The current-at-D `AttentionScore` is a predictor-side diagnostic needed by the existing composition and is
allowed. No attention after D may be read. Raw double channel variation is not enough: AD-16 correlates the
stored integer `OpportunityScore`, and rounding can turn many distinct channel values into one tie.

Then conclude with a **clearly labelled design recommendation**, not a measured finding, for the smallest
candidate input set worth considering — or state that none is ready. “Smallest” means the fewest declared
channels first, then the fewest collectors. The implementer does not amend AD-16, configure the arm, or
declare that a ranking is “usable”; the maintainer makes that decision after reviewing the fixed-window
findings.

If a replacement is recommended, its exact proposed collectors, weights and saturations must be written in
the findings and run through the same in-memory final-`OpportunityScore` distribution before it can be
adopted by a later amendment to spec 157 §7 and AD-16 §7. A marginal per-collector table alone is not enough
to validate a pooled channel: pooling recomputes saturation and preponderance non-linearly.

Constraints on the recommendation:

- **Exclude `newssearch` from inputs.** Under AD-16 third-party news is the *outcome*; scoring on it
  confounds input with outcome.
- **Omit breadth unless §5 proves it can carry positive mass.**
- **Do not propose adding four strategies.** One matched pair. Every arm costs 43 scorings per run and makes
  a chance winner likelier (AD-15).
- **"No budget is viable yet" is a legitimate and complete conclusion** — and if so, the honest next step is
  the collector mix (spec 156's finding), not a formula.

## Files (verify against the tree before planning)

`scripts/audit-signal-directions.ps1` (spec 156's read-only/output precedent only — do not copy scoring math
into it), a small C# audit entry point referencing the production projects, `ScoringChannelComposition.cs`
and `ScoreSignalMath.cs` for the shared prospective seams, `LegacyCollectorAttributionInference` (spec 151),
and `docs/` for the findings.

## Constraints

- **Read-only.** Nothing in `data/` is modified; no backfill, no re-extraction (specs 142/145 — heal forward
  only).
- **No shipped scoring-behaviour change, no new strategy configured, no fingerprint input, no pin move.**
  The shared pure seams permitted by §4 must leave v8/v9/v10 byte-identical.
- **Mirror the real rules rather than re-implementing them** — the numbers must be what `ScoringEngine`
  would actually produce, or the characterization is fiction.
- Deterministic (AD-3): same store + same declared as-of ⇒ same output.
- AD-15's positive-claim suspension is untouched; nothing here licenses any claim about efficacy.

## Out of scope (record, do not build)

- **Any forward outcome** — price, attention, or otherwise. That is the whole point of §1.
- **Shipping/registering v11, or configuring any strategy.** The shared prospective primitives in §4 are
  explicitly in scope; a dispatchable formula and live arm are not.
- **Changing the collector mix** — spec 156 recommended it; acting on it is its own spec.
- **Re-tuning weights or saturation constants.**

## Acceptance criteria

- [ ] The characterization computes **no** forward outcome of any kind — asserted by inspection, and stated
      in the findings doc.
- [ ] The one window is exactly `(2026-05-29T08:04:27.7605621Z,
      2026-07-28T08:04:27.7605621Z]`, known-at the latter instant; that instant and run-record id appear in
      the output.
- [ ] The eligibility funnel reports evidence-unresolvable separately from resolved-unattributed; no missing
      evidence is silently converted into collector attribution.
- [ ] Per candidate channel: directional-mass coverage/variance, distinct structural-input count,
      positive-score company count and preponderance sign distribution, over the 43 companies.
- [ ] Collector attribution is real, with **recorded / inferred / unattributed reported separately** over
      resolved inputs and 151's validated-vs-reasoned split stated.
- [ ] The audit uses shared C# production primitives; no PowerShell or findings-only copy of the scoring math
      exists, and v8/v9/v10 golden pins pass unmodified.
- [ ] §5's breadth question is answered explicitly, including whether a first-party feed counts as a
      publisher and every existing reach sub-term.
- [ ] The exact predeclared filings arm's final integer-score distribution is reported.
- [ ] A clearly labelled recommendation for the smallest candidate input set — with an exact, similarly
      characterized proposed budget if one is offered — or an explicit "none is viable yet". Adoption remains
      a later maintainer decision.
- [ ] Nothing in the accrued store is modified; no strategy is configured.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.

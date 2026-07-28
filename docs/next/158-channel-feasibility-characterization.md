# Task: Characterize which channels can carry usable variation — INPUT ONLY, before any arm is predeclared

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

One 60-day scoring window ending at a **stated** as-of instant, matching `Radar:ScoringWindowDays` = 60 and
the spec-136 point-in-time read predicate. Declare the date in the output; do not sweep several and pick.

### 3. Per candidate channel, per company

For each candidate collector channel — `sec-form4`, `sec-13dg`, `rss`, `newssearch`, `usaspending`,
`sec-edgar`, `fda` — and for the **breadth** channel under §3's positive-only rule, report over the 43
companies:

- **directional activity mass** (the v11 rule: Neutral excluded), and how many companies have any at all;
- **companies whose channel score would be > 0** — the headline feasibility number, since
  `max(0, preponderance)` floors a net-negative channel at 0;
- **variance of the channel score** across companies, and the **count of distinct channel-score values** —
  a channel that is technically nonzero but takes three distinct values cannot rank 43 companies;
- **preponderance sign distribution** (all-neutral / balanced / net-positive / net-negative).

**Collector attribution must be real, not inferred from signal type.** Use the spec-146 recorded `collector`
metadata, with spec-151's `Radar:Scoring:InferLegacyCollectorAttribution` for the legacy cohort — and report
**recorded vs inferred vs unattributed counts separately**, because 151's inference is validated only for
`newssearch`, `sec-form4` and RSS, while `sec-edgar`, `sec-13dg`, `usaspending` and GDELT are reasoned, not
ground-truth validated. An unattributed signal is consumed by no collector channel and must be counted as
such, not silently dropped.

### 4. Answer the breadth question explicitly

Report, as its own result: **how many distinct publishers carry at least one Positive signal, per company
and in total**, and how many companies would have non-zero §3-narrowed breadth. State plainly whether a
first-party RSS feed is counted as a publisher by the existing reach computation, since that determines
whether the answer is "small" or "zero".

If §3-narrowed breadth is structurally zero, say so — that is a finding about spec 157's design decision and
must be reported as such, not worked around here.

### 5. Recommend the smallest viable matched pair — or none

Conclude with a recommendation for the **smallest** budget that the measurements show can produce usable
variation, to be predeclared by amendment to spec 157 §7 and AD-16 §7. Constraints on the recommendation:

- **Exclude `newssearch` from inputs.** Under AD-16 third-party news is the *outcome*; scoring on it
  confounds input with outcome.
- **Omit breadth unless §4 proves it can carry positive mass.**
- **Do not propose adding four strategies.** One matched pair. Every arm costs 43 scorings per run and makes
  a chance winner likelier (AD-15).
- **"No budget is viable yet" is a legitimate and complete conclusion** — and if so, the honest next step is
  the collector mix (spec 156's finding), not a formula.

## Files (verify against the tree before planning)

`scripts/audit-signal-directions.ps1` (spec 156's precedent — read-only, refuses to write inside the data
root; extend or sibling it), `ScoringChannelComposition.cs` and `ScoreSignalMath.cs` for the exact
activity/preponderance rules to mirror, `LegacyCollectorAttributionInference` (spec 151), and `docs/` for
the findings.

## Constraints

- **Read-only.** Nothing in `data/` is modified; no backfill, no re-extraction (specs 142/145 — heal forward
  only).
- **No scoring change, no new strategy configured, no fingerprint input, no pin move.** No formula file is
  touched.
- **Mirror the real rules rather than re-implementing them** — the numbers must be what `ScoringEngine`
  would actually produce, or the characterization is fiction.
- Deterministic (AD-3): same store + same declared as-of ⇒ same output.
- AD-15's positive-claim suspension is untouched; nothing here licenses any claim about efficacy.

## Out of scope (record, do not build)

- **Any forward outcome** — price, attention, or otherwise. That is the whole point of §1.
- **Implementing v11, or configuring any strategy.** Spec 157 is paused pending this result.
- **Changing the collector mix** — spec 156 recommended it; acting on it is its own spec.
- **Re-tuning weights or saturation constants.**

## Acceptance criteria

- [ ] The characterization computes **no** forward outcome of any kind — asserted by inspection, and stated
      in the findings doc.
- [ ] One declared 60-day window; the as-of instant appears in the output.
- [ ] Per candidate channel: nonzero-company count, score variance, distinct-score count and preponderance
      sign distribution, over the 43 companies.
- [ ] Collector attribution is real, with **recorded / inferred / unattributed reported separately** and
      151's validated-vs-reasoned split stated.
- [ ] §4's breadth question is answered explicitly, including whether a first-party feed counts as a
      publisher.
- [ ] A recommendation for the smallest viable matched pair — or an explicit "none is viable yet".
- [ ] Nothing in the accrued store is modified; no strategy is configured.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.

# Task: Measure how much score rests on mis-typed `GuidanceChange` signals

## Overview

`DirectionalFilingSignalSource` hardcodes every passing AI read to `SignalType: "GuidanceChange"`. Spec 168
recorded the misnomer and specified the fix, then **deferred** it behind an un-defer gate. Two live
skeptic reviews have since confirmed it is not cosmetic:

- **UFPT**, 2026-08-03 8-K (items 2.02, 9.01) — `GuidanceChange (Positive)`, strength 8, confidence 0.95.
  The release **issues no guidance**; UFPT provided none. The signal carried the company's entire +17.
- **IOSP**, 2026-08-05 8-K (items 2.02, 9.01) — `GuidanceChange (Positive)`, strength 8, confidence 0.90.
  No quantified forward target anywhere. **Aggravating**: the qualitative outlook that does exist is
  *negative* — management guided to further Q3 gross-margin compression in its largest segment and
  disclosed supply constraints unrelieved until "Q4 at the earliest, more likely Q1 next year". Typed
  honestly this was **Mixed**, and Mixed removes most of the +21 that made IOSP the largest mover of 74.

Spec 168's own evidence was 49 of 145 spec-162 *calibration* rationales mentioning neither guidance nor
outlook. What nobody has is the number that actually decides anything: **how much live Opportunity across
the universe is currently carried by signals whose type is wrong.** Spec 168 cannot be scheduled rationally
without it — if the answer is "two companies" it waits behind the gate; if it is "most directional signals
in the store" the gate itself needs revisiting.

**This spec measures. It changes no signal, no type, no score.**

## Assignment

Worktree: any
Dependencies: none. Read-only over the accrued store; spec 142 (durable hydration) is the prerequisite and
is merged. Feeds — does not satisfy — spec 168's un-defer gate.
Estimated time: ~1–2 hours.

## Shape — a script, following the spec-164 precedent

Implement under **`scripts/calibration-audit/`** alongside the existing spec-164 audit scripts
(`analyze-labels.ps1`, `analyze-shadow-read.ps1`, `categorize-comparability.ps1`), writing to
`data/guidance-typing-audit/`. **No production code, no new Application type, no DI wiring, nothing that
runs nightly.** A one-shot read-only measurement over JSON on disk is exactly what that directory exists
for, and the nightly baseline run is currently unattended.

## The hard limitation — state it, do not engineer around it

**Radar does not store the filing text.** For an 8-K, the persisted evidence `rawText` is the EDGAR index
summary — item codes and item titles — not the exhibit. Verified on both cases above; IOSP's reads in full:

```
8-K filing accession 0001193125-26-333724 filed 2026-08-05: 8-K. 8-K item codes: 2.02,9.01.
Items: Results of Operations and Financial Condition.
```

So this audit **cannot** answer "does the filing contain a quantified forward target" from the store. It can
only classify the **AI rationale** (`Signal.Reason`) and, where present, the cached `AnalyzedFilingRecord`.
That is a proxy: a rationale that never mentions guidance is strong evidence the read was not about
guidance, but it is not proof about the underlying document.

**Say this plainly in the rendered output.** A reader who takes the result as a filing-level measurement
will over-claim. Fetching exhibits to do it properly is a separate, network-bound, SEC-fair-access-exposed
piece of work and is explicitly out of scope.

## Changes

### 1. Classify every `GuidanceChange` signal in the accrued store

Split by **origin**, because the two are different defects with different fixes:

- **AI-read** (`DirectionalFilingSignalSource`) — the hardcoded type; this is spec 168's target.
- **Deterministic extractor** (`KeywordSignalExtractor`) — a matched phrase; spec 168 §5 repartitions these
  and some are legitimately guidance actions.

Distinguish them by the `Reason` shape (the extractor writes `Matched phrase '…'`; the AI writes prose) and
record the rule used, so the split is reproducible rather than eyeballed.

Then classify each rationale as:

- **`guidance-action`** — names an explicit guidance/outlook action (raise / cut / lower / withdraw /
  introduce / reaffirm of guidance or outlook)
- **`results-only`** — describes reported results with no guidance action named
- **`ambiguous`** — mentions outlook language without an action

Use the **same wording rule spec 168 §5 defines** ("a `GuidanceChange` phrase must state a guidance/outlook
ACTION — not merely mention results near the word"), so this audit and that spec cannot drift. Print the
rule and the token list into the output.

### 2. Weight it by score, which is the decision-relevant part

Counting signals is not enough — one mis-typed signal on a thin company (IOSP, 9 links) matters more than
one on a well-covered company. For the most recent as-of date, report:

- Companies whose **highest-contribution directional signal** is a `results-only` `GuidanceChange`
- Their `OpportunityScore`, and their rank in the primary strategy's ordering
- **How many of the current top 10 by Opportunity depend on one** — the single number that decides whether
  spec 168 waits

Read scores through the existing score-file layout only; do not recompute anything.

### 3. Cross-check the two known cases

UFPT (`3ba9066c-…`, 8-K accession 0001628280-26-051846) and IOSP (`1779a777-…`, accession
0001193125-26-333724) must both classify as **`results-only`**. If either does not, the classifier is wrong
and the aggregate is worthless — pin them as fixtures.

## Output

`data/guidance-typing-audit/guidance-typing.{csv,md}`:

- Counts and percentages by origin × classification
- The score-weighted section from §2
- The classification rule verbatim, the rationale-not-filing limitation, and the total signals examined
- Deterministic ordering (AD-3) so two runs over the same store agree byte for byte

## Constraints

- **Read-only.** Nothing is written to `data/signals/`, `data/evidence/`, `data/scores/` or
  `data/scoring-configs/`. No signal is retyped, deleted or superseded. No production code changes, so no
  fingerprint input moves, no `_formula.Version` bump, no `RuleSetVersion` bump; all four spec-148 pins
  stand and `ScoringConfigFingerprintTests` is untouched.
- Append-only (AD-8) is preserved trivially: this writes only under its own new directory.
- No network. No SEC fetch, no AI call.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` must still pass —
  unaffected, since no C# changes.

## Out of scope — recorded, NOT built

- **The fix.** Retyping to `EarningsTrajectory`, the `GuidanceAction` diagnostic, the extractor
  repartition, the cache epoch and the `baseline-earnings-only-v2` re-key are all **spec 168**, deferred
  behind its own gate. This spec produces the evidence that decides when that gate opens; it must not
  pre-empt any of it.
- Fetching 8-K exhibits to classify the filing rather than the rationale.
- Re-reading or reprocessing accrued filings, and any backfill or supersede of existing signals.
- The confidence-calibration question the IOSP review raised separately — that a rationale which enumerates
  its own contra-indicators and then discounts them ("adjusted EPS roughly flat", "gross margins slightly
  down in two segments", concluding "not deeply negative enough to shift to Mixed") emitted **0.90**. That
  is a real finding about elimination-shaped reasoning earning affirmation-level confidence, and it needs
  its own spec.

## Acceptance criteria

- [ ] Every `GuidanceChange` signal in the accrued store classified, split by AI-read vs deterministic
      origin, with the origin rule recorded.
- [ ] Classification uses spec 168 §5's wording rule verbatim; the rule and token list appear in the output.
- [ ] UFPT and IOSP both classify as `results-only`, pinned as fixtures.
- [ ] Score-weighted section reports how many of the current top 10 by Opportunity rest on a `results-only`
      `GuidanceChange`.
- [ ] The rationale-not-filing limitation is stated in the rendered output, not only in this spec.
- [ ] Deterministic: identical store ⇒ byte-identical artifacts.
- [ ] Nothing written outside `data/guidance-typing-audit/`; no signal or score mutated; no pin moves.
- [ ] Build and full test suite pass, unaffected.

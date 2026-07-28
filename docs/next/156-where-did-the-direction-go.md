# Task: Where did the direction go? Measure the 87.6 % Neutral corpus before tuning anything that consumes it

> **Radar scores trajectory, and 87.6 % of its 49,793 signals carry no trajectory.** Positive 8.1 %,
> Negative 4.3 %. Spec 153 measured the consequence: of 32 companies with an active `sec-form4` channel,
> **13 were all-Neutral and 18 net-negative — 31 of 32 lost their entire channel contribution** under v10,
> `sec-13dg` was dark for all 43, and `newssearch` was all-Neutral for all 43.
>
> Every open measurement question — the paired comparison (155), benchmark adjustment, the attention-arrival
> outcome — is downstream of evidence that mostly has no direction in it. **A slower, more patient score
> computed over directionless evidence will accumulate nothing, more reliably.**

## This is an INVESTIGATION spec

The deliverable is a **measured breakdown and a written recommendation**, not a formula. Ship at most the
smallest fix the measurement unambiguously justifies. If the finding is "the neutrality is correct and the
collector mix is wrong", that is a complete and successful outcome — say so and stop.

## What is already known (verify each; do not re-derive from scratch)

Much of the neutrality is **deliberate design, not extractor failure**, and the spec must not treat it as a
defect to be fixed:

- **`NewsArticle` evidence emits exactly one Neutral signal** (`KeywordSignalExtractor.cs:21`). With
  MediaAttention at ~15.3 k signals this is a large share of the corpus **by construction**.
- **13G and amendments are Neutral by design** (spec 99), specifically so passive stakes "never misfire
  bullish". InstitutionalOwnership is ~12.8 k signals.
- Several `CapitalRaise` and `ExecutiveHire` phrases are Neutral on the explicit grounds that the code
  reveals no directional read (`KeywordSignalExtractor.cs:111-156`) — a convertible note may be accretive or
  a death spiral, and the filing does not say which.

⚠️ **Under AD-16 (proposed), Neutral MediaAttention is not merely acceptable — it is CORRECT.** News is the
attention arriving; it is the thing the stealth thesis wants to *predict*, not an input to the prediction.
A recommendation that makes news directional would work against the thesis and must be argued explicitly if
proposed at all.

## The specific hypothesis to test first

**Two independent paths produce insider signals, and the directionless one may be swamping the directional
one.**

- `HttpSecForm4Reader` reads the Form 4 XML and **does** classify direction: transaction code `P` →
  `Positive`, `S` → `Negative`, a mixed same-filing buy+sell → `Neutral` deliberately (not net-signed), and
  **any 10b5-1 planned transaction → `Neutral`** because a planned sale is not a discretionary signal
  (`HttpSecForm4Reader.cs:286-341`).
- `KeywordSignalExtractor` separately matches a routine-insider phrase and emits **Neutral `InsiderBuying`**
  (spec 153 quotes it: "matched phrase 'insider stock transaction (routine)'").

If both fire on the same filings, the Neutral copies dilute preponderance in the same channel the
directional ones are trying to move — and `sec-form4`'s 9.0 k `InsiderBuying` signals being overwhelmingly
Neutral would be an artefact of the keyword path, not a fact about insiders.

**Measure it, do not assume it.** Report the `InsiderBuying` population split by which path produced it, and
the direction distribution within each. The 10b5-1 share is a genuine confound and must be reported
separately: if most insider activity in this universe is planned-sale, the neutrality is **honest** and the
finding is about the universe, not the code.

## What to produce

A written finding in `docs/`, backed by a table over the live store, covering at minimum:

1. **Neutral signals by `SignalType` × source × producing rule/path**, so "neutral by design" and "neutral by
   default" are separated with numbers. This is the core deliverable.
2. **The insider split above**, including the 10b5-1 share.
3. **Which sources could carry direction and do not** — and for each, whether that is a design decision
   (cite it), a data limitation, or an unfilled gap.
4. **A recommendation**, which may legitimately be "collect different evidence rather than extract harder".
   Radar's directional reads come from a narrow base; if 13G is neutral by design and news is neutral by
   design and planned sales are neutral by design, then the honest conclusion may be that the current
   collector mix cannot support a directional thesis — which is a finding about **[[radar-collector-expansion-direction]]**,
   not about the extractor.

## Constraints

- **Read-only over the live store by default.** No backfill, no rewrite, no re-extraction of accrued
  evidence — the standing rule since spec 142/145 is heal forward only, and 89.5 % of signals have
  unresolvable evidence that must not be retro-healed.
- **No scoring change, no fingerprint input, no pin move** unless the measurement justifies one, and then it
  is a separate slice with its own `RuleSetVersion` decision (a rule-STRUCTURE change bumps
  `KeywordSignalExtractor.RuleSetVersion`; a magnitude change does not).
- **Do not "fix" neutrality by making uncertain things directional.** Spec 99 made 13G Neutral so it would
  never misfire bullish; that reasoning still holds. A Neutral that honestly reflects "the code does not say"
  is correct behaviour and must survive this slice.
- Price is never an input (AD-14).

## Out of scope (record, do not build)

- **Re-extracting or backfilling accrued signals.**
- **Changing v10's neutral amplification** — settled by AD-16 and its own slice; this spec only supplies the
  evidence for how much it matters.
- **Adding a collector.** If the recommendation is "collect differently", that is the *next* spec, and per
  [[radar-collector-expansion-direction]] it must be efficacy-motivated rather than added on enthusiasm.
- Any AI/LLM re-read of historical filings to recover direction — a large, separate, and expensive question.

## Acceptance criteria

- [ ] A table over the live store splits Neutral signals by type × source × producing path, distinguishing
      **neutral-by-design** (with the citation) from **neutral-by-default**.
- [ ] The `InsiderBuying` two-path hypothesis is measured and answered either way, with the 10b5-1 share
      reported separately.
- [ ] Each directionless source is classified as design decision / data limitation / gap.
- [ ] A written recommendation lands in `docs/`, explicitly allowed to conclude that the collector mix — not
      the extractor — is the constraint.
- [ ] Nothing in the accrued store is modified.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.

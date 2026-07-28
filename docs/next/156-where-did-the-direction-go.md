# Task: Audit WHY each signal has the direction it has — with honest Unknown buckets and denominators

> **Radar scores trajectory, and 87.6 % of its 49,793 signals carry no trajectory.** Positive 8.1 %,
> Negative 4.3 %. Spec 153 measured the consequence: of 32 companies with an active `sec-form4` channel,
> **13 were all-Neutral and 18 net-negative — 31 of 32 lost their entire channel contribution** under v10,
> `sec-13dg` was dark for all 43, and `newssearch` was all-Neutral for all 43.
>
> Under the now-accepted **AD-16**, a stealth thesis needs directional evidence to accumulate a slope from.
> A slower, more patient score computed over directionless evidence will accumulate nothing, more reliably.
> **Before tuning anything that consumes direction, find out where the direction went.**

## This is an INVESTIGATION spec

The deliverable is a **measured audit and a written recommendation**, not a formula. Ship at most the
smallest fix the audit unambiguously justifies (see §4). If the finding is "the neutrality is correct and
the collector mix is the constraint", that is a complete and successful outcome — say so and stop.

> ### ⚠️ What this audit does and does NOT establish
>
> It establishes **data provenance and extraction coverage**: what Radar collected, what direction it
> assigned, and how much of that is explainable. It establishes **nothing whatsoever about business
> efficacy** — not whether the signals predict anything, not whether a strategy works. No number produced
> here may be cited in support of a strategy claim, and AD-15's positive-claim suspension (amended
> 2026-07-28) is unaffected by anything this audit finds.

## ⚠️ A hypothesis this spec previously carried was WRONG — do not re-derive it

An earlier draft claimed `HttpSecForm4Reader` and `KeywordSignalExtractor` were two independent
signal-producing paths, and that a keyword-emitted Neutral was diluting the reader's directional read.
**There is no second path.** The pipeline is strictly linear and deterministic:

1. `HttpSecForm4Reader` classifies the filing's transaction codes → `SecForm4Filing.Direction`
   (`P` → Positive, `S` → Negative, mixed same-filing buy+sell → Neutral deliberately, any 10b5-1 planned
   transaction → Neutral).
2. `SecForm4Collector.MapToEvidence` (`SecForm4Collector.cs:146`) synthesizes **exactly one** fixed phrase
   from that `Direction` into the evidence Title/RawText.
3. `KeywordSignalExtractor` (`KeywordSignalExtractor.cs:194`) maps that phrase **back** to one signal — its
   own comment states "the extractor only maps phrase -> direction".

So the ~9.0 k Neutral `InsiderBuying` signals are **the reader genuinely classifying those filings Neutral**,
not an artefact. That is a fact about the filings (or about the classification rules), and it is what this
audit must explain. The same one-phrase pattern holds for 13D/13G (spec 100) and GovernmentContract.

## What is already known — verify, but do not treat as defects

Much of the neutrality is **deliberate design**, and the audit must classify it as such rather than as loss:

- **`NewsArticle` evidence emits exactly one Neutral signal** (`KeywordSignalExtractor.cs:21`). MediaAttention
  is ~15.3 k signals — a large share of the corpus **by construction**.
- **13G and amendments are Neutral by design** (spec 99), specifically so passive stakes "never misfire
  bullish". InstitutionalOwnership is ~12.8 k signals.
- **Several `CapitalRaise`/`ExecutiveHire` phrases are Neutral** on the explicit grounds that the filing
  reveals no directional read (`KeywordSignalExtractor.cs:111-156`) — a convertible note may be accretive or
  a death spiral.
- **10b5-1 planned transactions are forced Neutral** (`HttpSecForm4Reader.cs:286`): a planned sale is not a
  discretionary signal.

⚠️ **Neutral MediaAttention is THESIS-CONSISTENT and implemented as designed — which is not the same as
empirically validated.** Under AD-16 news is the attention *arriving*, i.e. the thing the stealth thesis
exists to predict rather than an input to the prediction; that makes its neutrality coherent with the
accepted thesis, and nothing more. It has not been shown to be the right choice by measurement. A
recommendation that makes news directional works against the accepted thesis and must be argued explicitly,
at length, if proposed at all.

## Design

### 1. Audit the REASON, not just the direction — and admit where the reason is gone

For each signal, the question is *why* it carries the direction it does. Group into at least:

- **Directional** — and by which rule/branch.
- **Neutral by design** — with the citation (spec 99, the 10b5-1 rule, the news rule, the
  no-directional-read `CapitalRaise` phrases).
- **Neutral by default** — matched a rule that simply has no directional reading.
- **Unknown / unresolvable** — the reason was never persisted (see §2). This bucket is a first-class
  outcome, not a rounding error.

### THREE INDEPENDENT DIMENSIONS — do not make one conditional on another

An earlier draft treated reason-recovery as conditional on source-recovery. **It is not.**
`FileSignalStore.Serialize` persists `Reason` on the **signal record itself** (`:470-484`), beside
`Direction` and `Type` — so the extraction rule that fired is recoverable for essentially **every** signal,
including the 89.5 % whose *evidence* does not resolve on disk. Audit these separately, each with its own
denominator:

1. **Evidence source** — does the signal's `EvidenceId` resolve to a stored raw item? Only ~**10.5 %** do
   (spec 142's measurement; cause diagnosed in spec 145; healed forward only). An unresolvable source is a
   provenance gap.
2. **Persisted signal / extraction reason** — the `Reason` on the signal (for keyword extraction, the
   matched phrase). Expected to be near-total coverage, and it is what makes the by-design vs by-default
   split answerable at all.
3. **Upstream producer / classification reason** — the branch the *collector* took before synthesizing its
   phrase: Form 4's 10b5-1 flag and its `NeutralExcluded`-vs-mixed distinction. **Not persisted** (§2), so
   permanently Unknown for accrued data.

Dimension 2 being recoverable where dimension 1 is not is the single most useful fact for this audit — it
means most of the corpus *can* be explained at rule level even though its evidence is unresolvable.

**Every figure carries the denominator it was computed over**, and these are three different denominators.
A percentage over an unstated base is exactly the kind of number specs 152 and 153 were written to stop
producing, and here there are three bases to confuse.

### 2. The reason for a Form 4 classification is NOT recoverable from the store — report it as Unknown

`HttpSecForm4Reader` computes `Is10b5Plan` (`:253`, returned at `:362`) and distinguishes
`NeutralExcluded` codes from a mixed buy+sell, **but `SecForm4Collector` persists only `insiderDirection`
and `insiderNetValue`** (`:63`, `:182`, `:189`). The plan flag and the reason branch never reach disk.

So: the **direction** of every historical insider signal IS recoverable (`insiderDirection` is persisted);
the **reason** is not. Under this spec's read-only/no-refetch constraint, historical reason attribution must
be reported as **Unknown** — not estimated, not inferred from the phrase, and not backfilled by re-fetching
filings. Do not attempt to reconstruct it.

### 3. Classify each directionless source

For every source producing predominantly Neutral signals, state which it is: a **design decision** (cite
it), a **data limitation** (the source genuinely does not carry valence), or an **unfilled gap** (direction
is available and simply not read). Only the third is a defect.

### 4. The one prospective fix this audit may justify

If — and only if — the audit shows the missing reason is what blocks the analysis, persist it going forward:
a reason/classification token on Form 4 evidence metadata (e.g. `insider10b5Plan`, or better a single
`insiderClassificationReason`), so the same audit becomes answerable next time.

Constraints on that fix: **forward only** (AD-8/AD-1 — no backfill, no rewrite); additive metadata; and it
must not become an evidence-identity or `ContentHash` input (spec 145: identity is the normalized
title+body hash alone), so no evidence id moves and no `AddIfNewAsync` decision changes.

## Files (verify against the tree before planning)

`HttpSecForm4Reader.cs`, `SecForm4Collector.cs`, `KeywordSignalExtractor.cs`, the durable stores
(`FileSignalStore` / `FileRawEvidenceStore`) for the read side, and `docs/` for the written finding.

## Constraints

- **Read-only over the live store.** No backfill, no rewrite, no re-extraction, no re-fetching of filings.
  Heal forward only (specs 142/145); 89.5 % of signals have unresolvable evidence that must not be
  retro-healed.
- **Do not "fix" neutrality by making uncertain things directional.** Spec 99 made 13G Neutral so it would
  never misfire bullish; a Neutral that honestly reflects "the filing does not say" is CORRECT behaviour and
  must survive this slice.
- **No scoring change, no fingerprint input, no pin move.** A rule-STRUCTURE change would bump
  `KeywordSignalExtractor.RuleSetVersion` — out of scope here.
- Price is never an input (AD-14).

## Out of scope (record, do not build)

- **Re-extracting or backfilling accrued signals**, and any refetch of historical filings.
- **Changing v10's neutral amplification** — its own spec, already required by AD-16.
- **Adding a collector.** If the recommendation is "collect differently", that is the *next* spec, and per
  [[radar-collector-expansion-direction]] it must be efficacy-motivated rather than added on enthusiasm.
- **Any AI/LLM re-read of historical filings** to recover direction — large, separate, expensive.

## Acceptance criteria

- [ ] A table over the live store classifies signals by direction **and reason**, with
      design / default / **Unknown** buckets and an explicit denominator for each.
- [ ] The Form 4 reason gap is reported as Unknown, with the persistence gap named — not estimated or
      inferred.
- [ ] Each directionless source is classified as design decision / data limitation / unfilled gap.
- [ ] The written finding lands in `docs/` and is explicitly permitted to conclude that the collector mix,
      not the extractor, is the constraint.
- [ ] Any prospective metadata fix is additive, forward-only, and moves no evidence id.
- [ ] Nothing in the accrued store is modified.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.

# Task: Recover collector attribution for accrued evidence, so replay can backtest v9 strategies

> **This converts "wait until 17 August" into "answer tonight."** Spec 139 shipped replay and spec 142 made
> it able to see accrued history, but a v9 channel strategy matches on the **collector recorded against each
> signal's evidence** — and that stamp only started being written by spec 146, today. Measured on the live
> store: **6,047 of 6,388 raw evidence files (94.7%) carry no collector attribution.** Only 341 do (337
> `newssearch`, 2 `sec-form4`, 2 `RssPressReleaseCollector`).
>
> So replaying the v9 strategies across the accrued 27 days today would score every channel against ~5%
> attribution and produce a full series of near-zero scores. That is **worse than no series**: it would
> populate the spec-140 leaderboard with numbers that measure the missing attribution rather than the
> strategy, and they would look like data.

## Why this is recovery, not fabrication

The attribution was **deterministic at collection time** and simply wasn't persisted. Every evidence record
already carries `sourceType`, `sourceName` and `metadata`, and those discriminate cleanly for the enabled
collector set. Measured distribution of the 6,047 unattributed records:

| `sourceType` | count | discriminator |
|---|---:|---|
| `news_article` | 3,028 | `sourceName` (Yahoo Finance, MarketBeat, simplywall.st, …) |
| `filing` | 2,681 | `metadata.form` — `4` → Form 4; `SC 13G`/`SC 13G/A`/`13D` → 13D/G; `8-K`/`10-Q`/`10-K` → EDGAR |
| `press_release` | 317 | IR RSS feeds |
| `government_contract` | 21 | unambiguous by `sourceType` |

This is re-deriving a fact that was known and dropped — categorically different from synthesising evidence,
which the standing rule (and spec 145) forbids. **But it is still an inference, and it must never be
indistinguishable from a recorded fact.**

## Pre-validated before this spec was dispatched (2026-07-27)

The mapping below was run over the live store **before** queueing this slice, against the 341 records that
carry recorded attribution, ignoring their recorded value:

- **341 / 341 agree — 100%, zero disagreements.**
- **Zero ambiguous:** all 6,388 files resolve. `newssearch` 3,365 (52.7%) / `sec-edgar` 1,160 (18.2%) /
  `sec-13dg` 850 (13.3%) / `sec-form4` 673 (10.5%) / `RssPressReleaseCollector` 319 (5.0%) /
  `usaspending` 21 (0.3%).

Rules used: `government_contract` → `usaspending`; `press_release` → `RssPressReleaseCollector`;
`news_article` → `newssearch`; `filing` + `form=4` → `sec-form4`; `filing` + `form` starting `SC 13` →
`sec-13dg`; any other `filing` with a form → `sec-edgar`.

⚠️ **Re-run this yourself — do not take it on trust — and note WHAT IT DOES NOT PROVE.** The 341 ground-truth
records are 337 `newssearch`, 2 `sec-form4`, 2 `RssPressReleaseCollector`. **`sec-edgar` (1,160) and
`sec-13dg` (850) are reasoned, not validated** — and `filings-led`'s two channels are exactly `sec-form4` and
`sec-13dg`, so the least-validated mappings carry the experiment. **Specifically check whether the general
SEC/EDGAR collector also fetches Form 4 and 13D/G filings**; if it does, `form` alone does not identify the
producing collector and the mapping needs a stronger discriminator (e.g. `metadata.secFeedUrl`) or those
records must stay unattributed. Since nothing is currently ambiguous, an over-confident mapping is the main
risk this slice carries.

## Design

### 1. Validate the mapping against the 341 records that DO have recorded attribution

**Do this first, and let the result decide whether the slice proceeds.** Those 341 records are a natural
ground-truth holdout: run the inference over them *ignoring* their recorded value, then compare.

- **Report the exact agreement rate.** If the mapping disagrees with recorded truth on any record, the
  mapping is wrong — fix it or narrow it, do not average over the error.
- 341 is a small and skewed sample (mostly `newssearch`), so **say so**: agreement there does not prove the
  `filing` mapping, which is the one carrying 2,681 records. State which collectors are genuinely validated
  and which are only reasoned.

### 2. Inferred attribution is marked, always

Attribution must carry its provenance: `recorded` (the collector stamped it, spec 146 onward) vs `inferred`
(this slice derived it). A consumer must be able to tell them apart, and any artifact built on inferred
attribution must be able to say so.

**Ambiguous cases stay unattributed.** Never assign a best guess. Report the unattributed fraction — it
bounds what any backtest over this data can claim.

### 3. Do not rewrite accrued evidence in place

The raw-evidence store is insert-only (AD-8/AD-1) and evidence identity is content-derived (spec 145).
Prefer a **side index keyed by `contentHash`** over mutating 6k historical files — it keeps history
untouched, is trivially reversible, and cannot corrupt the identity that spec 145 established. **Verify the
read path can consult it** before committing to this shape; if a trailing nullable field on the record turns
out to be genuinely better, argue it explicitly rather than defaulting to it.

### 4. Forward behaviour is unchanged

New evidence keeps recording the real collector at collection time. This slice fills historical gaps only;
it must not become a fallback that masks a future failure to stamp attribution properly.

### 5. Then prove the backtest is worth trusting

With attribution recovered, a replay across the accrued window becomes meaningful. **Report, in the
hand-back:**

- The attributed / inferred / still-ambiguous split, as counts and percentages.
- Whether `replay ⊆ forward` still holds (spec 139's invariant) — inferred attribution must not change any
  score that a forward run already produced.
- What the v9 strategies actually score over the replayed window, and **how much of their channel mass comes
  from inferred rather than recorded attribution.** If a strategy's series is 95% inferred, that is the
  headline caveat, not a footnote.

## Files (verify against the tree before planning)

`FileRawEvidenceStore` (+ its options and the persisted record shape), the `IEvidenceRepository`
implementation from spec 142, `CollectionProvenanceMetadata` / the stamping site in `CollectionPass`
(spec 146 — reuse its vocabulary, do not invent a second collector-name list), `EnabledCollectorVocabulary`
(spec 147), and the v9 channel gate in `RadarScoreFormulaV9` / `ScoringChannel.Consumes`.

## Constraints

- **No scoring change for already-attributed data.** Same inputs ⇒ same scores; `replay ⊆ forward` holds.
- **Append-only (AD-8).** Nothing deleted; historical evidence not rewritten if a side index will do.
- **Inferred ≠ recorded**, structurally, not by convention.
- **No fingerprint move** — attribution is data, not scoring configuration. Verify rather than assume, since
  the v9 channel set *is* a fingerprint input while the data it matches on is not.
- **Reuse the spec-146/147 collector vocabulary** — one source of collector names (CLAUDE.md: reuse over copy).
- Price is not read (AD-14).

## Out of scope (record, do not build)

- **Backfilling missing *evidence*.** 89.5% of accrued signals have no resolvable evidence record at all
  (spec 142's finding); that stays healed-forward-only and must not be conflated with this.
- **Auto-running the replay or promoting its output** into the live series — replay stays a hypothesis
  (spec 139 §4).
- **Changing what a v9 channel means.**

## Acceptance criteria

- [ ] The inference is validated against the 341 recorded-attribution records with the agreement rate
      reported; disagreements are fixed or the mapping narrowed, never averaged over.
- [ ] Which collectors are genuinely validated vs only reasoned is stated explicitly.
- [ ] Inferred attribution is distinguishable from recorded attribution by any consumer.
- [ ] Ambiguous records stay unattributed, and the unattributed fraction is reported.
- [ ] Accrued evidence files are not rewritten (or, if they are, the case is argued explicitly).
- [ ] Forward collection still records real attribution; the inference is not a silent fallback.
- [ ] `replay ⊆ forward` still holds; no already-produced score changes.
- [ ] No fingerprint move.
- [ ] The hand-back reports the attributed/inferred/ambiguous split and what fraction of each v9 strategy's
      channel mass is inferred.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.

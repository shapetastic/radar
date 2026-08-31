# Task: A Mixed earnings read is a READ — persist it with its provenance, and name why a filing produced no direction

## Overview

Measured 2026-08-30 over the live store (read-only): of the **88 Item-2.02 earnings 8-Ks observed since
2026-06-30, 34 (39 %) carry only the keyword fallback** `GuidanceChange Neutral, strength 3, novelty 4,
confidence 0.4, "Matched phrase 'results of operations'"` (`KeywordSignalExtractor.cs:201`), across **34
of 94 companies** — 28 of them original-74 names (HRL, JJSF, LZB, BKE, EOSE ×2, AEHR, MNRO, …), so this is
not a seeding artefact. **36 of those 37 filings WERE analysed by the model**: the cache
(`data/filings-cache/…/{accession}.json`) holds `outcome: NoDirectionalSignal` for them, and the
`data/ai-debug/filings` store shows what the model actually said — **28 of 30 with a record are `Mixed` at
confidence ≥ 0.6** (mostly 0.75–0.90), with genuinely two-sided rationales:

- JBSS `0001193125-26-356911`, Mixed 0.90: record net sales, but gross profit −9.5 % and diluted EPS −38.3 %.
- HRL `0000048465-26-000053`, Mixed 0.85: organic sales −2 %, $56m divestiture loss, $48m impairment — and
  adjusted EPS guidance raised.
- EOSE `0001628280-26-052903`, Mixed 0.85: revenue +351 %, gross margin −71 %, net loss widened.

The reads are correct. The accounting is not: `DirectionalFilingSignalSource.cs:296` maps
Mixed / Unknown / below-`MinConfidence` onto ONE cached token and emits **nothing**, so the company keeps
the spec-57 keyword Neutral — the exact signal an **unread** filing gets. A confident "materially
two-sided quarter" and "Radar never looked" are scored identically and are indistinguishable on disk (the
cache record carries no direction, confidence, rationale or cause). Across the whole cache that is **224
`NoDirectionalSignal` records against 222 `DirectionalSignalProduced`** — half of every analysed filing is
discarded uncounted. This is CLAUDE.md's "nothing may be discarded without being counted" and spec 191's
98 %-Neutral finding, one layer down.

**Explicitly NOT the fix: turning Mixed into a direction, or lowering `MinConfidence`.** A two-sided
quarter is not "improving"; changing what Mixed *scores* is a separate, measured decision. This slice
changes what is **persisted and visible**, and proves the score is byte-identical.

## Assignment

Worktree: any. Dependencies: spec 203 merged (it is not touched here, but both edit the scoring read path
and should not race). Independent of spec 200 Phase B.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. Persist the non-directional read as a signal — Mixed IS a direction the domain already has

`SignalDirection.Mixed` exists (`Radar.Domain`), `ScoreSignalMath.DirectionSign` maps it to **0** and every
component treats Neutral and Mixed identically (masses, activity, counts — the doc comments at
`ScoreSignalMath.cs:66,162,212,466,534` say so), and `GuidanceChangeSupersede` already treats Mixed as
"directional beats Neutral" (`:19-21`) — yet **nothing has ever produced a Mixed signal**, so that path is
dead code today. Use it:

| model read | emitted `ExtractedSignal` |
| --- | --- |
| `Mixed`, confidence ≥ `MinConfidence` | `GuidanceChange`, **Direction `Mixed`**, Strength **3**, Novelty **4**, Confidence **0.4** — i.e. the keyword fallback's exact magnitudes |
| `Unknown` (any confidence) or a directional read **below** `MinConfidence` | `GuidanceChange`, **Direction `Neutral`**, same magnitudes |
| `EmptyBodySkipped` (no model call) | nothing — unchanged |

`Reason` = the model rationale, prefixed `AI earnings read: Mixed 0.85 —` (or `Unknown` / `Improving 0.45
(below MinConfidence 0.6)`), advice-scrubbed as today. `MetadataJson` (spec 191's trailing nullable field,
composed through the shared `EvidenceMetadata.Compose`, ONE new key-set declared as consts beside the
directional-filing descriptor): `filingReadOutcome` ∈ {`mixed`, `unknown`, `below-confidence`},
`filingReadDirection` (the model's own token), `filingReadConfidence` (invariant `G29`), `filingReadModel`.

**Why the magnitudes are the keyword fallback's**: the point is provenance without a score move. With
Strength/Novelty/Confidence/EvidenceId/ObservedAt identical and a direction that scores as 0 exactly like
Neutral, every v8/v9/v10/v11 component is byte-identical — asserted, not argued (see §4). The model's real
confidence rides in metadata, never in `Confidence`, per the standing rule "if AI confidence is low, persist
the evidence but do not create high-confidence signals" — and, symmetrically, a high-confidence *Mixed* must
not become a high-strength anything.

## 2. The supersede prefers the READ over the keyword copy, deterministically

Today a Neutral AI read would tie with the keyword Neutral on the same `EvidenceId` and fall to the
`ObservedAtUtc`/`Id` tie-break — provenance chosen by GUID order. Extend `GuidanceChangeSupersede`'s
winner rule by ONE step ahead of the existing ones: a signal whose metadata carries `filingReadOutcome`
beats one that does not; then the existing "directional beats Neutral"; then the existing stable order.
Mixed already wins over the keyword Neutral under the existing rule, so the new step matters only for the
`Neutral` row of §1. The spec-193 counts and contribution-reason note are unchanged in shape (the superseded
keyword copy is still counted, still named on the survivor). The supersede's doc says it is deliberately
not a fingerprint input; keep that true and assert the pins.

## 3. The cache record names the cause

`AnalyzedFilingRecord` gains trailing nullable `NoSignalCause` (`Mixed` / `Unknown` / `BelowConfidence` /
`EmptyBody`), `ReadDirection`, `ReadConfidence`, `Rationale`, and `CurrentCacheVersion` 2 → 3.

**Post-merge correction (spec 205): this section's original accrued-migration claim was false and is
withdrawn.** `CollectionPass` passes only `newEvidence` to `DirectionalFilingSignalSource`; an already
durable filing is eliminated by content-derived evidence identity before the cache is consulted. Therefore
making a v2 `NoDirectionalSignal` record a cache MISS cannot cause the 224 accrued filings measured above
to be swept or re-read. The MISS remains useful in its only reachable case: the same accession is genuinely
re-admitted as new evidence (for example because its old raw record never landed, or its content identity
changed), at which point a fresh read is current point-in-time work, not a backfill. A successful read writes
v3 through the cache's ordinary per-accession replacement path. A v2 `DirectionalSignalProduced` record
remains an accepted hit and replays its intact signal. Version 3 otherwise heals forward for genuinely new
evidence. No historical evidence or signal is rewritten, no cache enumeration/bulk retry is added, and no
model call is spent merely because an old v2 record exists.

The debug store (`FileFilingReadDebugStore`) is unchanged — it stays a diagnostic, not a source.

## 4. Proof the score does not move

- `ScoringOutputStabilityTests` (v8), the v9/v10/v11 composition guards and all six pins in
  `ScoringConfigFingerprintTests` UNTOUCHED and green. The directional-filing descriptor
  (`directional-filing:str=…;nov=…;minconf=…;model=…;cmpscan=…;cmpcap=…`) gains **no** segment: no
  magnitude or gate changed.
- A new engine-level pin: a company with one earnings filing scored (a) with the keyword Neutral only,
  (b) with the keyword Neutral + the §1 Mixed read, (c) with the keyword Neutral + the §1 Neutral read.
  All five components, the explanation and `ComponentJson` are byte-identical across (a)/(b)/(c); only the
  surviving link's contribution reason and metadata differ. Mutation: give the Mixed read Strength 8 and
  (b) goes red — the proof is that the magnitudes, not the direction, are what keep it identical.
- Spec-139 read-only replay over the live store at one as-of on `main` and on the branch: every snapshot
  field-for-field identical excluding minted GUIDs (the spec-145 precedent). Note: the replay never
  re-extracts, so it proves the supersede + Mixed handling on ACCRUED signals; the first live run after
  merge proves the extraction side (§6).

## 5. Investigate, do not assume: two `Improving ≥ 0.6` debug records with a no-signal cache outcome

`data/ai-debug/filings` holds 2 records for silent 8-Ks whose recorded direction is `Improving` at
confidence ≥ 0.6, yet the cache says `NoDirectionalSignal`. Most likely a re-analysis under a later
comparability policy (spec 160) that landed Mixed, with the debug store keeping the FIRST read. Find out;
if the debug store keeps only the first read, say so on its class doc; if it is anything else, it is a
finding for the PR body. Do not change the debug store's semantics in this slice.

## 6. Live distribution — the deliverable, not the harness

The read-only post-merge audit on **2026-08-31** found **451 version-2 cache records: 225
`NoDirectionalSignal`, 226 `DirectionalSignalProduced`, and 0 version-3 records**. That is the measurement
which falsified the migration premise above: the first run did not turn cache misses into a historical
re-read cohort because those filings never re-entered the candidate list.

After the first post-205 baseline that actually observes at least one genuinely new earnings filing, report
(read-only, from the cache + signals): the legacy v2 totals separately; the count of new v3 records by
`NoSignalCause`; how many companies carry a persisted Mixed/Neutral read instead of a keyword-only
GuidanceChange in the current window; and — the CLAUDE.md rule — the resulting **distribution** of
`GuidanceChange` directions across the live universe (Positive / Negative / Mixed / Neutral-read /
Neutral-keyword). Reconcile any v2 decrease one-for-one to a genuinely re-admitted accession's v3
replacement; it is not evidence of an accrued migration. A run with no new filing records **not observed**,
not a fabricated all-zero distribution. If Mixed turns out to be > 50 % of confident new reads, that is a
fact about the prompt or the rubric worth its own spec; record it, do not tune here.

## Non-goals

No change to `MinConfidence`, `Strength`, `MaxFilingsPerRun`, the comparability scan/cap, the analyzer
prompt, the reading model, or what Mixed/Neutral CONTRIBUTE to any score; no fingerprint move; no rewrite
or deletion of any signal or evidence file; no enumerated/bulk historical re-analysis of either v2 outcome;
no cache mutation except the existing per-accession replacement after a genuinely re-admitted no-signal
record is successfully read; no change to the news-judgment path; no re-reading of accrued
`EmptyBodySkipped` filings merely because they are accrued.

## Acceptance criteria

- [ ] A confident Mixed read persists as a `GuidanceChange` `Mixed` signal with the keyword magnitudes and
      the model's direction/confidence/rationale in metadata; Unknown/below-gate reads persist as Neutral
      with the same envelope; `EmptyBodySkipped` unchanged.
- [ ] The supersede prefers the read over the keyword copy deterministically; counts and reasons intact.
- [ ] Cache records name the cause for new v3 reads; v2 produced records remain hits, while a v2 no-signal
      MISS can run only for genuinely re-admitted evidence and never enumerates the accrued cache (spec 205
      correction).
- [ ] Engine pin (a)/(b)/(c) byte-identical, mutation-proven; all pins unchanged; live replay identical.
- [ ] §5 answered in the PR body; §6 reported after the first live run (or in a follow-up note).

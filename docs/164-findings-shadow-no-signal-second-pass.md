# Spec 164 findings — shadow-mode forced-choice second pass over the no-signal cohort

**Status: complete. Verdict (precommitted rule, frozen in spec 164 before any read ran):**

```
SHADOW: NOT-SUPPORTED (τ=0.80, strict recovery 16/33, false alarms 6/57, inversions 0/145, flips 24/145; τ=0.90, strict recovery 2/33, false alarms 1/57, inversions 0/145, flips 82/145)
```

**Consequence:** the production recall fix is NOT a prompt/threshold change alone. The spec-162 misses
need a different mechanism — a second model or a pre-screen, each its own spec. Every number outside
the decision block below is descriptive only and grounds no production recommendation.

## Run provenance

- Run date: 2026-07-31 (single pass; **298/298 reads `ok` on the first attempt** — 0 call-failed,
  0 parse-failed — so the completeness gate was satisfied without reruns).
- Reader under test: `openai:deepseek-ai/DeepSeek-V4-Flash` (the spec-119 production baseline), via the
  production `FilingAnalyzerPrompt` assembly with the committed `cal-shadow-v1` instruction as the
  COMPLETE system instruction (replacement, never append).
- Prompt: `scripts/calibration-audit/shadow-prompt.md`, LF-normalized SHA-256
  `463f7b8b78661d597b1be876900eae38a5a1d7f0f6a064d2f164c993191877c6` — recomputed by the analyzer and
  carried by all 298 records, so every read below ran under the one committed instruction.
- Inputs: the 298 archived production-parity model inputs, each SHA-256 + length verified against
  `docs/162-exhibit-manifest.csv` before any model call. Zero SEC requests. Cohort re-assertions HELD:
  145 directional + 153 no-signal, and both outcome-conflicting legacy accessions
  (`0001628280-26-048253`, `0001654954-26-006655`).
- Outputs: one JSON per accession + `shadow-summary.csv` under the shadow output root only (untracked
  study data, outside the exhibit archive); the production `data/filings-cache/` is untouched.

## Reading the result (descriptive, not a recommendation)

- **The abstention hypothesis is half-right.** Forced to choose, the reader recovered 23/33 provisional
  misses (69.7%, Wilson 52.7–82.6%) with NO confidence threshold — and every one of the 23 had the
  RIGHT direction (loose = strict at every τ; zero wrong-direction recoveries anywhere). The
  information is in the text and the model can read it.
- **But the confidence channel does not separate recovered misses from degraded reads.** The frozen
  τ=0.80 point failed by one on strict recovery (16/33 vs ≥17) and clearly on flips (24/145 vs ≤15):
  the same confidence region that admits the recovered misses also admits the directional-cohort
  degradation. Loosening τ raises both together (see the descriptive sweep); the fallback τ=0.90
  collapses recovery to 2/33 with 82/145 flips.
- **Zero inversions at every τ is the one unambiguously clean property.** Across all 145 sealed
  directional reads the forced prompt never produced the opposite direction at any confidence. The
  failure mode is under-confidence/hedging (drift to `Mixed`), never contradiction.
- **False alarms were never the binding constraint** (6/57 at τ=0.80, bound ≤9; 13/57 unthresholded,
  bound applied at τ). The reader does not invent directional stories on genuinely-quiet filings at
  high confidence.
- The unlabeled 63 no-signal rows skew heavily `Mixed` (37/63), consistent with the hedging pattern.

## Standing caveats (spec 162's, unchanged)

Single-shot non-deterministic reads (this is a record of one pass, not an average — rerunning may move
individual rows); exploratory reference labels (ratified same-family verdicts, not ground truth);
filings cluster within tickers, so Wilson intervals are somewhat narrower than the truth.

---

# Full analyzer report (verbatim, `analyze-shadow-read.ps1`)

# Spec 164 - shadow-mode forced-choice second pass

Generated (UTC): 2026-07-31T22:59:15.8769758Z
Shadow records: 298 under `C:\Users\scm9d\source\repos\radar\data\calibration-audit-164\shadow`.
Worksheet: 298 rows (145 directional, 153 no-signal). Labels: 235 raw, 235 effective.
Labeled rows: 90 no-signal + 145 directional = 235. Provisional misses: 33; provisional non-misses: 57.

## Vocabulary mapping (defined once, in this script)

| shadow direction | worksheet / label direction |
| --- | --- |
| `Improving` | `Positive` |
| `Deteriorating` | `Negative` |
| `Mixed` | `Mixed` |
| `Neutral` | `Neutral` |

No other equivalence is permitted. `Mixed` is NEVER "directional" and never agrees with a
directional label in the strict recovery rate.

## Standing caveats (they apply to EVERY rate below)

1. The reference labels are EXPLORATORY ratified same-family verdicts (spec 162), not ground truth.
2. Filings cluster within tickers, so observations are not independent and the Wilson intervals are
   somewhat narrower than the truth.
3. The reads are SINGLE-SHOT against a non-deterministic model. This is a RECORD of one pass, not an
   average - re-running may move individual rows.

## 0. Provenance and status coverage

Committed prompt: `C:\Users\scm9d\source\repos\radar-claude-1\scripts\calibration-audit\shadow-prompt.md`
Recomputed LF-normalized SHA-256: `463f7b8b78661d597b1be876900eae38a5a1d7f0f6a064d2f164c993191877c6`

| recorded field | distinct values (count) |
| --- | --- |
| promptVersion | `cal-shadow-v1` (298) |
| promptSha256 | `463f7b8b78661d597b1be876900eae38a5a1d7f0f6a064d2f164c993191877c6` (298) |
| modelIdentity | `openai:deepseek-ai/DeepSeek-V4-Flash` (298) |

Every record carries the committed prompt hash: the reads below were all taken under one instruction.

| cohort | n | ok | call-failed | parse-failed | no record | other |
| --- | --- | --- | --- | --- | --- | --- |
| labeled no-signal | 90 | 90 | 0 | 0 | 0 | 0 |
| labeled directional | 145 | 145 | 0 | 0 | 0 | 0 |
| UNLABELED no-signal | 63 | 63 | 0 | 0 | 0 | 0 |

A non-`ok` status is recorded SEPARATELY from the result: an infrastructure failure is never counted
as a `Neutral` (or any other) read. Failed rows are re-runnable - rerun the console, which retries
anything that is not `ok`.

## 1. Recovery table - the 90 labeled no-signal rows (HEADLINE)

Does the forced-choice prompt recover the provisional misses without flooding the non-misses?
Rates here use NO confidence threshold (any directional read counts); the frozen decision below
applies τ = 0.80.

- STRICT recovery P(forced directional AND direction agrees with the adjudicated finalDirection | provisional miss): 23/33 = 69.7% (Wilson 95%: 52.7%-82.6%)
- LOOSE recovery P(forced directional | provisional miss): 23/33 = 69.7% (Wilson 95%: 52.7%-82.6%)
- FALSE ALARM P(forced Improving/Deteriorating | provisional NON-miss): 13/57 = 22.8% (Wilson 95%: 13.8%-35.2%)

A miss recovered with the WRONG direction counts in the LOOSE rate and NOT in the STRICT one.
`Mixed` and `Neutral` are not directional and count in neither.

Broken out by FORCED-read confidence bin (the operating point a production spec would need):

| forced confidence bin | misses in bin | strict | loose | non-misses in bin | false alarms |
| --- | --- | --- | --- | --- | --- |
| [0.00,0.60) | 0 | 0 | 0 | 0 | 0 |
| [0.60,0.70) | 5 | 2 | 2 | 17 | 5 |
| [0.70,0.80) | 10 | 5 | 5 | 18 | 2 |
| [0.80,0.90) | 16 | 14 | 14 | 19 | 5 |
| [0.90,0.95) | 2 | 2 | 2 | 1 | 1 |
| [0.95,1.00] | 0 | 0 | 0 | 2 | 0 |

Rows with no `ok` record fall in no bin and are listed in section 0.

## 2. Stability table - the 145 labeled directional rows

Forced direction vs the SEALED production direction. A forced prompt that degrades the directional
cohort is disqualifying evidence, and inversions are worse than abstentions.

- Agrees with the sealed direction: 132/145 = 91.0% (Wilson 95%: 85.3%-94.7%)
- Flipped to `Mixed`/`Neutral` (or unresolved): 13/145 = 9.0% (Wilson 95%: 5.3%-14.7%)
- INVERTED (directional, opposite the sealed direction): 0/145 = 0.0% (Wilson 95%: 0.0%-2.6%)

| sealed confidence bin | n | agree | flipped to Mixed/Neutral | inverted | no `ok` record |
| --- | --- | --- | --- | --- | --- |
| [0.00,0.60) | 0 | 0 | 0 | 0 | 0 |
| [0.60,0.70) | 1 | 0 | 1 | 0 | 0 |
| [0.70,0.80) | 2 | 2 | 0 | 0 | 0 |
| [0.80,0.90) | 39 | 31 | 8 | 0 | 0 |
| [0.90,0.95) | 33 | 29 | 4 | 0 | 0 |
| [0.95,1.00] | 70 | 70 | 0 | 0 | 0 |

Bins are the SEALED reader confidence. At τ = 0 an "inverted" row is any directional read opposite
the sealed direction; the decision block re-evaluates inversions and flips at τ = 0.80.

## 3. Unlabeled no-signal rows - DISTRIBUTION ONLY (63 rows)

**No reference labels exist for these rows: this is a DISTRIBUTION, not accuracy.** They enter no
recovery, false-alarm or stability rate anywhere in this report, and failures among them do not block
the decision below.

| forced direction | n |
| --- | --- |
| Improving | 13 |
| Deteriorating | 8 |
| Mixed | 37 |
| Neutral | 5 |
| (no `ok` record) | 0 |

| forced confidence bin | n |
| --- | --- |
| [0.00,0.60) | 1 |
| [0.60,0.70) | 16 |
| [0.70,0.80) | 19 |
| [0.80,0.90) | 24 |
| [0.90,0.95) | 1 |
| [0.95,1.00] | 2 |

## 4. PRECOMMITTED DECISION BLOCK (frozen in spec 164 before any read ran)

Threshold semantics: a forced read is DIRECTIONAL at τ iff its direction is `Improving` or
`Deteriorating` AND its confidence >= τ. Anything else - `Mixed`, `Neutral`, or a directional read
below τ - is NONDIRECTIONAL at τ. An INVERSION is a directional-at-τ read OPPOSITE the sealed
direction; a FLIP is any nondirectional-at-τ result on a sealed-directional row.

SUPPORTED requires ALL of: strict recovery >= 17 of 33; false alarms <= 9 of 57; inversions = 0; flips <= 15 of 145.
Primary τ = 0.80; ONE-SHOT fallback τ = 0.90 if the primary fails. No further threshold shopping.

| criterion | bound | at primary | at fallback |
| --- | --- | --- | --- |
| strict recovery | >= 17/33 | 16/33 | 2/33 |
| false alarms | <= 9/57 | 6/57 | 1/57 |
| inversions | = 0 | 0/145 | 0/145 |
| flips | <= 15/145 | 24/145 | 82/145 |

```
SHADOW: NOT-SUPPORTED (τ=0.80, strict recovery 16/33, false alarms 6/57, inversions 0/145, flips 24/145; τ=0.90, strict recovery 2/33, false alarms 1/57, inversions 0/145, flips 82/145)
```

SUPPORTED means the production recall spec may proceed citing this rule. NOT-SUPPORTED means the
misses need a different mechanism (second model / pre-screen - their own specs).

**EVERY OTHER NUMBER IN THIS REPORT IS DESCRIPTIVE ONLY AND GROUNDS NO PRODUCTION RECOMMENDATION.**

### Descriptive threshold sweep - DESCRIPTIVE ONLY

This sweep exists to show the shape of the trade-off. It is NOT a menu: the decision above was
evaluated at the two precommitted points (τ = 0.80, then once at 0.90) and nowhere else.

| τ | strict recovery | loose recovery | false alarms | inversions | flips |
| --- | --- | --- | --- | --- | --- |
| 0.00 | 23/33 | 23/33 | 13/57 | 0/145 | 13/145 |
| 0.50 | 23/33 | 23/33 | 13/57 | 0/145 | 13/145 |
| 0.60 | 23/33 | 23/33 | 13/57 | 0/145 | 13/145 |
| 0.70 | 21/33 | 21/33 | 8/57 | 0/145 | 16/145 |
| 0.80 | 16/33 | 16/33 | 6/57 | 0/145 | 24/145 |
| 0.90 | 2/33 | 2/33 | 1/57 | 0/145 | 82/145 |
| 0.95 | 0/33 | 0/33 | 0/57 | 0/145 | 109/145 |


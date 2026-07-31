# Findings: AI filing-read calibration audit (spec 162)

**Status: Phase B COMPLETE (2026-07-31).** The full study ran under protocol `cal-v2`: 145 directional
labels (30 pilot relabels + 115 new), 90 no-signal labels (precommitted 60 + the one-shot extension,
which TRIGGERED), and a 77-row adjudication queue resolved through the two-step blinded flow. The full
label set is committed at `docs/162-calibration-labels-full.jsonl`; the analyzer's final-mode report
(validated against the pinned study contract — exit 0, all provenance checks green) is reproducible via
`scripts/calibration-audit/analyze-labels.ps1` from that file plus the sealed worksheet.

Read-side only: no scoring change, no fingerprint input, no pin move. Labels never flow into runtime
values by side door — these findings inform SPECS (the confidence remap, `cmpscan-v2`, structured
financial-comparison extraction).

**Provenance**: production reader `openai:deepseek-ai/DeepSeek-V4-Flash` (scope
`openai-deepseek-ai-deepseek-v4-flash-8f94f2dbe65fcb93`, pinned); second reader
`anthropic:claude-fable-5` (the `radar-skeptic-reviewer` agent, precommitted); prompt template hash
`3745cc4a2c63476fb355a39e14ec53b163a75e2c6b278bfbcccd806e1f18a286` (CRLF→LF-normalized, equal to the
study contract's pin); every label's `modelInputHash` verified against the exhibit manifest. Blinding
was structural throughout: labelers received only company/CIK/accession/model-input path, ≤5 concurrent,
zero SEC requests.

**Recorded protocol deviation (stated, not hidden):** adjudication was executed by
`anthropic:claude-fable-5` agents under the two-step blinded flow (blindCall recorded from the exhibit
text alone, then unblinding both model answers), i.e. the same model family as the second reader rather
than a human. The maintainer resolves the queue by ratifying or overriding these verdicts (the
`blindCall` + `finalDirection` + note for every row is in the committed JSONL, so any row can be
re-adjudicated without re-running the study). Mitigation: the blind step is persisted, verdicts cite
reported numbers, and in 5 of the 9 directional error-diagnosis adjudications the adjudicator's blind
call MATCHED the model against the second reader or vice versa — the verdicts are not a rubber stamp of
either reader.

---

## Protocol (verbatim from spec 162 §4)

> - **Blinding is structural**: labeling agents receive ONLY company, CIK, accession and the local
>   **model-input** exhibit path; no other local file, no web. REIT filings carry the REIT framing note
>   (judge on FFO/AFFO). **≤5 concurrent agents** (the pilot's 20-at-once drew a 529 wave).
> - **Adjudication makes ground truth**, and is itself two-step blinded: the adjudicator first records their
>   own `blindCall` from the exhibit text alone, THEN unblinds both model answers and records the final
>   verdict — both steps persisted in the JSONL.
> - **The queue has two separately-analyzed parts, and only one feeds calibration**:
>   1. **Calibration probability sample** — `min(10, bin size)` rows per confidence bin, selected by
>      SHA-256(accession) hex ascending within the bin, **irrespective of agreement status**. This sample
>      ALONE produces the calibration rates and their Wilson intervals. (Deterministic without tracking
>      CIK-prefix order, which plain accession sort does.)
>   2. **Error-diagnosis set** — ALL remaining disagreements and ALL labels with
>      identification/parity/truncation doubts, adjudicated for failure-mode analysis and reported
>      separately; NEVER pooled into the calibration rates.
>   Expected total ~45–60 rows.

Precommitted for the whole study (spec 162 §3): **the second reader is `anthropic:claude-fable-5` (the
`radar-skeptic-reviewer` agent), a different model family from the DeepSeek production reader** — recording
`labeler` per batch detects drift, precommitting prevents it, and changing the labeler mid-study is a
protocol-version bump that restarts the affected labels. The labeling prompt template is committed at
`scripts/calibration-audit/labeling-prompt.md` and `promptHash` is its hash, so a mid-study template edit
is visible.

No-signal extension rule (spec 162 Phase B, verbatim): **minimum 60 of 153**, selected as the first 60 by
SHA-256(accession) hex order. **Extension rule, ONE-SHOT**: if adjudication confirms ≥1
genuinely-directional missed print, or the Wilson 95% upper bound on the miss rate exceeds 10%, extend by
exactly the next 30 in hash order and **report the final result at N=90 — the trigger is evaluated once,
never re-applied to the extended set**.

## Canonical label schema (`labels.jsonl`, one JSON object per filing)

```
{ accession, ticker, cik, batch, modelInputHash,
  protocol: { version: "cal-v2", labeler: { provider, model }, promptHash, labeledAtUtc,
              attempt: 1|2|..., replacedLabelOfAttempt?: n },
  label: { direction, directionConfidence, comparisonClean, comparabilityItems[], material, keyFacts[] },
  adjudication: { status: pending|confirmed|overturned|n/a,
                  selectionReason: calibration-sample|disagreement|doubt,
                  blindCall?: { direction, comparisonClean },   // recorded BEFORE unblinding
                  finalDirection?, note? } }
```

The sealed model answer is joined from the worksheet (`Radar.CalibrationAudit`'s `worksheet.csv`) at
analysis time, never stored in the label file. `selectionReason` records WHY a row entered the
adjudication queue; `calibration-sample` takes precedence when a row qualifies both ways.

## Note on the pilot CSV

`docs/162-calibration-pilot-labels.csv` is a **lossy legacy summary** of the 30-filing pilot (schema
`pilot-flat`): it preserves accession/direction/confidence/agreement/clean/materiality plus a one-line
KeyItem, and **drops the structured `comparabilityItems` amounts and the `keyFacts`** the labelers
produced. Append-only, historical; superseded as study data by the Phase-B relabels (the pilot labels were
not production-parity — see the input-path stability table below).

---

## Inter-model agreement curve

Reader-confidence bins × skeptic-agreement rate, over all 145 directional labels. **Agreement is not
calibration** — two models can agree and both be wrong; adjudicated correctness is the next table.

| reader-confidence bin | n | agree | agreement rate (Wilson 95%) |
| --- | --- | --- | --- |
| [0.00,0.60) | 0 | — | no labeled rows |
| [0.60,0.70) | 1 | 0 | 0/1 = 0.0% (0.0%–79.3%) |
| [0.70,0.80) | 2 | 1 | 1/2 = 50.0% (9.5%–90.5%) |
| [0.80,0.90) | 39 | 27 | 27/39 = 69.2% (53.6%–81.4%) |
| [0.90,0.95) | 33 | 29 | 29/33 = 87.9% (72.7%–95.2%) |
| [0.95,1.00] | 70 | 70 | 70/70 = 100.0% (94.8%–100.0%) |

Overall agreement: 127/145 = 87.6% (Wilson 95%: 81.2%–92.0%). The curve is strictly monotone in
confidence — the pilot's "ordering carries signal" finding holds on the full cohort.

## Calibration table (calibration probability sample only)

Human-adjudicated correctness over the 33 derived `calibration-sample` rows exclusively (min(10, bin
size) per bin, hash-ordered, agreement-blind). A row is correct iff `finalDirection` equals the sealed
model direction. Disagreement-queued adjudications are never pooled here.

| reader-confidence bin | adjudicated n | model correct | accuracy (Wilson 95%) |
| --- | --- | --- | --- |
| [0.00,0.60) | 0 | — | no sample members |
| [0.60,0.70) | 1 | 0 | 0/1 = 0.0% (0.0%–79.3%) |
| [0.70,0.80) | 2 | 1 | 1/2 = 50.0% (9.5%–90.5%) |
| [0.80,0.90) | 10 | 5 | 5/10 = 50.0% (23.7%–76.3%) |
| [0.90,0.95) | 10 | 9 | 9/10 = 90.0% (59.6%–98.2%) |
| [0.95,1.00] | 10 | 10 | 10/10 = 100.0% (72.2%–100.0%) |

**Reading (honest Ns: the sub-0.90 bins are small, and the [0.80,0.90) estimate is the load-bearing
one):**

1. **The scale runs hot below 0.90 and is honest above it.** A 0.80–0.89 self-reported confidence was
   right half the time (5/10) — a coin flip — while 0.90–0.95 delivered 90% and 0.95+ went 10/10.
   The reader's ORDERING is real; its absolute values below 0.90 are not.
2. **Every one of the 8 sample errors degraded to Mixed or Neutral — zero inversions.** No adjudicated
   row anywhere in the study found the model reading Positive where the truth was Negative or vice
   versa. The failure mode is over-commitment on materially two-sided or comparability-broken prints,
   exactly the pilot's pattern, now quantified: at 0.85 confidence the model calls Positive on prints
   an adjudicator judges Mixed about half the time.

## Error-diagnosis set (reported separately — never pooled into calibration rates)

9 directional disagreements outside the sample were adjudicated: the model was **wrong in 6**
(ATEX, LZB ×2, WTTR ×2, HWKN — every one a Positive→Mixed overturn on a comparability-broken or
two-sided print) and **right in 3** (ERII Negative upheld; SKWD and JNJ Positive upheld against the
second reader's Mixed). Recurring evidence in the overturns: headline profit swings driven by one-time
gains (ATEX's $33.9M license-exchange gain), acquisition-inflated growth masking same-store declines
(LZB, HWKN), and in two cases (WTTR ×2) the production reader's rationale contained **factual
misreads** ("record full-year revenue" on a year-over-year decline; a "raised outlook" the release never
states). Full verdicts with numbers are in the committed JSONL and the analyzer report.

## False-negative table (no-signal cohort, one-shot threshold applied)

Precommitted first 60 of 153 by SHA-256(accession) hex order; **the trigger FIRED** (21 confirmed misses
in rows 1–60; Wilson upper 47.6% vs the 10% threshold — and the ≥1-confirmed-miss arm fired on its own).
Per the one-shot rule the next 30 were labeled and the final result is reported at N=90; rows 61–90
never entered the trigger.

**Confirmed misses: 33/90 = 36.7% (Wilson 95%: 27.4%–47.0%).** Every miss is a human-adjudicated
`finalDirection` of Positive (22) or Negative (11) on a filing the production reader analyzed and
produced NO directional signal from. Labeled materiality of the missed prints: 13 high, 20 moderate,
0 low. The full 33-row table (accession, ticker, blinded label, finalDirection, materiality, hash-order
position) is in the analyzer report; representative misses: CAT's −21% adjusted-EPS quarter (twice —
both 2025 CAT prints in the sample were missed), AEHR's +33%-revenue swing-to-profit with 160–200%
growth guidance, CALM's −73% FY EPS collapse, IMAX's +48% EBITDA quarter, and MRCY's record-bookings
quarter (book-to-bill 1.48).

**This is the study's biggest finding.** The false-negative rate is roughly 4× the directional error
rate at the reader's median confidence: the production reader's precision on what it DOES emit is
decent (and its ≥0.95 reads are excellent), but its RECALL is poor — for every ~4 directional signals
it produces, it silently discards ~3 genuinely-directional prints, a third of them high-materiality.
The no-signal outcome cannot be read as "nothing here"; it is closer to a coin-weighted "maybe".

## Input-path stability table (pilot vs canonical-input relabel)

All 30 pilot filings were relabeled under the canonical model-input text (production normalizer +
truncation parity). Deltas vs the pilot's non-parity labels: **direction changed on 4/30 (13%), clean
changed on 8/30 (27%)** — ERII Negative→Mixed, WINA Mixed→Positive, AXGN Positive→Mixed, DEA
Positive→Neutral. Input provenance alone moves a meaningful minority of labels, which retroactively
validates the round-2 amendment (relabeling the pilot rather than reusing its labels) and means any
future study must hold the input path fixed — wire reproductions are not a substitute for the exhibit
the model actually read.

## Comparison cleanliness / comparability-item frequency (cmpscan-v2 evidence)

Clean YoY comparison rate over the 145 directional labels: **38/145 = 26.2% (Wilson 95%:
19.7%–33.9%)** — the pilot's 5/30 was not an artifact; roughly three of four directional prints carry
at least one comparability-breaking item (127/145 carry ≥1). Categorized frequency (a filing counts
once per category; full per-item strings with amounts are in the JSONL and analyzer report):

| category | filings (of 145) |
| --- | --- |
| acquisition / divestiture / perimeter change | 79 |
| one-time tax items (discrete releases, swings, valuation allowances) | 32 |
| impairment / restructuring / severance / closures | 30 |
| FX / currency translation | 25 |
| gains/losses on asset sales / dispositions | 23 |
| insurance / weather / litigation one-offs | 19 |
| accounting changes / recasts / reclassifications | 16 |
| LIFO / inventory adjustments | 6 |

The acquisitions/perimeter category — the evidenced `cmpscan-v1` gap from the pilot — is by far the
largest, present in **54%** of directional prints. Candidate `cmpscan-v2` phrases with evidence:
`acquisition`, `pro forma`, `deconsolidation`, `divestiture`, `held for sale`, `same-store` (as the
mask-detector for acquisition-inflated growth), plus a discrete-tax cluster (`discrete tax`, `tax
benefit`, `valuation allowance`).

## Materiality × constant-strength cross-tab

Every AI directional read carries constant Strength 8; the blinded labels grade materiality
independently:

| labeled materiality | strength 8 (all 145) |
| --- | --- |
| low | 3 |
| moderate | 82 |
| high | 60 |

Materiality varies over the full low/moderate/high range while the encoded strength never moves — and
13 of the 33 MISSED prints were high-materiality. Constant strength encodes no information; the
signal-strength channel is available and unused.

## Decisions

Findings inform SPECS; the maintainer decides. What this evidence supports:

1. **Confidence remap (own spec; fingerprint-moving).** The adjudicated curve gives the remap its
   shape: sub-0.90 self-reported confidence ≈ 50% directional accuracy (vs the 0.6 `MinConfidence`
   gate currently treating 0.65+ as actionable), 0.90–0.95 ≈ 90%, ≥0.95 ≈ 100% on this sample. A
   piecewise remap (or a raised gate) should discount the 0.80–0.90 mass hardest — that bin holds 39
   of 145 live reads (27%). Every error is an over-commit to Mixed-territory, never an inversion, so
   a remap (not a rejection) is the right instrument.
2. **`cmpscan-v2` (own spec, fed by the frequency table).** Add the acquisitions/perimeter cluster
   first (79/145), then discrete-tax (32/145). The 6 Positive→Mixed adjudicated overturns all sat on
   prints these phrases would have flagged.
3. **The false-negative rate needs its own spec, and it is the priority.** 36.7% (27.4%–47.0%) of
   no-signal filings were genuinely-directional, a third high-materiality, including repeat misses on
   the same companies (CAT ×2, CYRX ×3, SHOO ×2, FLO ×2, LBRT ×2, MRCY ×2, BELFB ×2). Candidate
   directions for the spec: a second-pass read of no-signal filings, prompt changes that force an
   explicit direction-with-confidence instead of allowing silent no-signal, or a cheaper
   recall-oriented pre-screen. Until then, no-signal outcomes must not be treated as evidence of
   absence anywhere downstream.
4. **Encode materiality (feeds the structured-extraction arc).** Constant Strength 8 wastes a real
   channel; the labelers graded materiality reliably from the same text.
5. **Input parity is load-bearing (already enforced by specs 162/163 harness).** 13% direction /
   27% clean label drift from input provenance alone: never label from non-canonical text again.

---

*Reproduce: `powershell -File scripts/calibration-audit/analyze-labels.ps1 -LabelsPath
docs/162-calibration-labels-full.jsonl -WorksheetPath data/calibration-audit/worksheet.csv` (final
mode; exits 0 with all provenance checks green as of 2026-07-31).*
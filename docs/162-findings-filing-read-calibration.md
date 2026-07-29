# Findings: AI filing-read calibration audit (spec 162)

**Status: Phase B pending.** This is the Phase-A skeleton: the protocol and schema are fixed here before
any study label exists; Phase B (the study itself — executed in-session with agents after Phase A merges,
NOT run-next work) fills the empty sections and commits `docs/162-calibration-labels-full.jsonl`. Until
that commit lands, spec 162 is NOT done and the spec stays in `docs/next/`.

Read-side only: no scoring change, no fingerprint input, no pin move. Labels never flow into runtime
values by side door — these findings inform SPECS (the confidence remap, `cmpscan-v2`, structured
financial-comparison extraction).

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

*(Phase B: reader-confidence bins × skeptic-agreement rate, honest Ns, Wilson 95% intervals. Agreement is
NOT calibration — rendered by `scripts/calibration-audit/analyze-labels.ps1`.)*

_Pending Phase B._

## Calibration table (calibration probability sample only)

*(Phase B: bins × human-adjudicated correctness over the `calibration-sample` rows EXCLUSIVELY; empty bins
say "no adjudicated labels". Disagreement/doubt adjudications reported in the error-diagnosis section,
never pooled here.)*

_Pending Phase B._

## Error-diagnosis set

_Pending Phase B._

## False-negative table (no-signal cohort, one-shot threshold applied)

_Pending Phase B._

## Input-path stability table (pilot vs canonical-input relabel)

_Pending Phase B._

## Comparability-item frequency (cmpscan-v2 evidence)

_Pending Phase B._

## Materiality × constant-strength cross-tab

_Pending Phase B._

## Decisions

*(Phase B: what these findings imply for the confidence-remap spec, the `cmpscan-v2` phrase-table spec,
and the structured financial-comparison extraction arc. Decisions are made by the maintainer on this
evidence — findings inform SPECS, never runtime values directly.)*

_Pending Phase B._

# Task: cmpscan-v2 candidate rules — measure raw-input hit rates BEFORE any production change

> **MEASUREMENT SPEC (deterministic, no AI, no production-scanner change).** Spec 162 Phase B's final review
> was explicit: the comparability category counts (acquisition/perimeter in 79/145 directional filings,
> discrete-tax in 25/145) come from regexes over the second reader's CURATED `comparabilityItems` strings —
> they establish candidate CONCEPTS, not the precision of any phrase on raw filing text. **"Evaluate first":
> this spec measures each candidate rule's hit rate and label-referenced precision/recall against the
> archived raw exhibits, so the eventual cmpscan-v2 production spec adds only rules with evidence.** The
> production scanner (`EarningsComparabilityScan`, `cmpscan-v1`) is NOT touched — its phrase table is hashed
> into the AI-ON fingerprint (spec 160), so touching it moves pins; this spec moves nothing.

## Overview

Spec 160's `cmpscan-v1` has 15 cap-triggering + 4 diagnostic-only phrases; the pilot and Phase B both showed
its biggest gap is acquisitions/perimeter changes (54% of directional prints carry one per the exploratory
labels), with discrete-tax second. The candidate phrases named in the findings — `acquisition`, `pro forma`,
`deconsolidation`, `divestiture`, `held for sale`, `same-store`, `discrete tax`, `tax benefit`, `valuation
allowance` — have never been run against a filing body. Some will be precise (`deconsolidation`), some are
plausibly noisy (`acquisition` matches "acquisition of customers"; `tax benefit` matches routine
stock-compensation lines). Measure, then decide.

Inputs all exist and are hash-pinned: 298 archived **full** normalized exhibit texts
(`data/calibration-audit/exhibits-full/`, verified against `docs/162-exhibit-manifest.csv` —
`cmpscan-v1` scans the full body, so candidates are measured on the full text, not the truncated model
input) and the labels (`docs/162-calibration-labels-full.jsonl`, 235 labeled filings).

⚠ **The committed concept mapping does NOT cover the reference population — regenerate it first.**
`docs/162-comparability-item-mapping.csv` holds ONLY the 145 production-directional filings
(`categorize-comparability.ps1` filters `outcome -eq 'DirectionalSignalProduced'`); using it as the
reference over all 235 labeled filings would silently treat every no-signal filing as concept-negative and
fabricate false positives. First step of this task: give the generator a cohort switch (e.g.
`-Cohort directional|all-labeled`, default `directional` so the spec-162 artifact reproduces byte-identical)
and generate `docs/165-comparability-item-mapping-all235.csv` over ALL 235 labeled filings (no-signal labels
carry `comparabilityItems` too). The spec-162 artifact and its 145-row numbers are NOT touched. All
concept-reference metrics below use the 235-filing reference; the 63 never-labeled filings contribute hit rates
only.

## Assignment

Worktree: any — the exhibits live ONLY in the main repo's untracked `data/calibration-audit/`; pass absolute
paths (read-only) and hash-verify against the committed manifest before measuring. No network, no keys.
Dependencies: current main (post #167).
Estimated time: ~1–2 hours.

## Changes

### 1. `scripts/calibration-audit/measure-cmpscan-candidates.ps1`

Deterministic PowerShell 5.1-compatible script (the `analyze-labels.ps1` conventions: byte-level UTF-8 reads,
StrictMode, fail loudly):

- **The candidate list is FROZEN in this spec, with production semantics, before any measurement runs**
  (the reviewer's precommitment point — a list edited after seeing results is tuning). The PRIMARY rows are
  **literal case-insensitive substring matches** — `cmpscan-v1`'s own matching semantics, so a promoted
  candidate behaves in production exactly as measured. The frozen primary list:
  `acquisition` · `acquisitions` · `completed acquisition` · `recent acquisition` · `pro forma` ·
  `deconsolidation` · `divestiture` · `divestitures` · `held for sale` · `same-store` · `same store` →
  target concept `acquisition-divestiture-perimeter`; `discrete tax` · `tax benefit` · `valuation
  allowance` · `uncertain tax position` → target concept `discrete-tax`. The script implements exactly this
  table (id, literal, concept, one-line rationale). **Regex variants are permitted only as clearly-marked
  EXPLORATORY rows** (e.g. word-boundary-anchored forms) — reported in a separate table section, never
  eligible for the promotion rule below.
- **Hash-verify every exhibit** against `docs/162-exhibit-manifest.csv` (`fullTextSha256`) before reading;
  mismatch ⇒ fail naming the file.
- Per candidate, over ALL 298 filings: filings hit + hit rate. Over the 235 LABELED filings, against the
  concept reference derived from the regenerated 235-filing mapping (the mapping stays one row per
  comparability ITEM; the reference is per FILING — a filing "has the concept" iff any of its items mapped
  to the candidate's target category): **precision, recall, F1 at the filing level**, with Wilson intervals
  and honest Ns. Additionally, against the **ANY-BREAK reference** (a filing "has a break"
  iff its label records `comparisonClean = false`): precision only — a candidate whose hits routinely land
  on clean-labeled filings is noise regardless of concept.
- **False positives and false negatives are LISTED, not just counted** (accession + the matched line's
  ±80-char context for FPs; the label item the rule missed for FNs) — the review's point: a "false positive"
  may be a label omission, and only examples let a human tell. Cap the listing at 15 per candidate with the
  overflow counted.
- **`cmpscan-v1` baseline row — hit rate, overlap and ANY-BREAK precision ONLY.** v1's 15 cap-triggering
  phrases legitimately detect impairments, litigation, settlements and asset-sale effects — concepts the
  two candidate references do NOT cover — so scoring v1 against the acquisition/tax references would count
  its legitimate hits as false positives and make the baseline artificially poor. v1 gets: hit rate over the
  298, precision against the ANY-BREAK reference (`comparisonClean = false` covers every break kind, so it
  IS a valid v1 target), and the per-candidate overlap column (candidate fires ∧ v1 already fired) — a
  candidate that only fires where v1 already capped adds nothing. **No concept precision/recall is reported
  for v1.**
- Emits `docs/165-cmpscan-candidate-hits.csv` (candidate × accession hit matrix, long form) and prints the
  summary tables.

### 2. Findings doc — `docs/165-findings-cmpscan-v2-candidates.md`

Committed with: the per-candidate table (hit rate / precision / recall / F1 / any-break precision /
v1-overlap), the FP/FN example listings, and a decisions section applying the **precommitted promotion
rule, frozen here before the run**: a primary (literal) candidate is RECOMMENDED for the production
cmpscan-v2 spec iff **concept precision ≥ 0.80 AND concept recall ≥ 0.30 AND it fires on ≥ 5 labeled
filings where v1 did not fire** (novel coverage — all three over the 235-filing reference). Candidates failing
the rule are NOT recommended, full stop; the findings may note a narrower exploratory variant as "re-measure
in a future round", but no production recommendation may cite exploratory rows or post-hoc thresholds —
every number outside the rule is descriptive. Standing caveats stated: the concept reference is derived
from EXPLORATORY ratified labels (spec 162 status), the taxonomy is regex-coded with a long uncategorized
tail, and 63 of the 298 filings have no labels at all (hit rates only).

## Tests

Script-level fixture tests in the `AnalyzeLabelsScriptTests` style (temp tree, tiny fixture exhibits +
mapping):

- A candidate matching a fixture exhibit is counted, and its FP listing carries the matched context.
- Precision/recall computed correctly against a fixture reference (including a filing with the concept but
  no phrase hit ⇒ FN listed).
- Tampered exhibit (hash mismatch) fails naming the file.
- Unlabeled filings contribute to hit rate but never to precision/recall.
- The v1 baseline row renders hit rate / any-break precision / overlap and NO concept precision/recall.
- The generator's cohort switch: default output byte-identical to the committed spec-162 artifact; `all-labeled`
  includes fixture no-signal rows.
- Any-break precision computed against `comparisonClean = false` (a hit on a clean-labeled fixture filing
  lowers it).

## Constraints

- **`EarningsComparabilityScan` and its phrase tables are untouched** — no production code change at all; no
  fingerprint input; every spec-160 pin stands. This is a script + two docs artifacts.
- No AI, no network. Deterministic: same inputs ⇒ byte-identical outputs.
- `data/calibration-audit/` is read-only for this task.

## Out of scope, recorded not built

- The production cmpscan-v2 change (own spec, fed by these findings; it moves the AI-ON pins via the
  `cmpscan=` descriptor segment and must say so).
- Re-labeling or extending the concept reference; adjudicating label omissions surfaced by FP examples
  (list them for the maintainer instead).
- Scanning filings outside the 298-exhibit study corpus.

## Acceptance criteria

- [ ] `docs/165-comparability-item-mapping-all235.csv` regenerated over all 235 labeled filings via the
      generator's cohort switch; the spec-162 145-row artifact byte-untouched (default cohort asserted).
- [ ] `measure-cmpscan-candidates.ps1` implementing EXACTLY the frozen literal candidate list (regex rows
      exploratory-only), manifest hash verification, per-candidate hit/precision/recall/F1 + any-break
      precision + Wilson, FP/FN example listings, v1 baseline (hit rate / any-break precision / overlap
      only), and the hit-matrix CSV.
- [ ] Measurement executed over the 298 exhibits; `docs/165-findings-cmpscan-v2-candidates.md` +
      `docs/165-cmpscan-candidate-hits.csv` committed with the PRECOMMITTED promotion rule applied verbatim
      and the caveats.
- [ ] No production file touched; no pin move; `dotnet build Radar.sln -c Release` /
      `dotnet test Radar.sln -c Release --no-build` green (new script tests included).
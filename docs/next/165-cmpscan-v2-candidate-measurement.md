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
input), the committed per-item concept mapping (`docs/162-comparability-item-mapping.csv`), and the labels
(`docs/162-calibration-labels-full.jsonl`, 235 labeled filings).

## Assignment

Worktree: any — the exhibits live ONLY in the main repo's untracked `data/calibration-audit/`; pass absolute
paths (read-only) and hash-verify against the committed manifest before measuring. No network, no keys.
Dependencies: current main (post #167).
Estimated time: ~1–2 hours.

## Changes

### 1. `scripts/calibration-audit/measure-cmpscan-candidates.ps1`

Deterministic PowerShell 5.1-compatible script (the `analyze-labels.ps1` conventions: byte-level UTF-8 reads,
StrictMode, fail loudly):

- **The candidate table is committed IN the script** (one place to edit and re-run): each row = candidate id,
  regex, target concept (`acquisition-divestiture-perimeter` | `discrete-tax`), and a one-line rationale.
  Seed set: the nine phrases above, plus near-variants worth testing side-by-side (e.g. `acquisition of` vs
  `acquisition`, `same.store` with both hyphen forms, `completed acquisition|recent acquisition`). Matching
  is case-insensitive over the full normalized exhibit text.
- **Hash-verify every exhibit** against `docs/162-exhibit-manifest.csv` (`fullTextSha256`) before reading;
  mismatch ⇒ fail naming the file.
- Per candidate, over ALL 298 filings: filings hit + hit rate. Over the 235 LABELED filings, against the
  concept reference derived from the committed mapping CSV (a filing "has the concept" iff any of its items
  mapped to the candidate's target category): **precision, recall, F1 at the filing level**, with Wilson
  intervals and honest Ns.
- **False positives and false negatives are LISTED, not just counted** (accession + the matched line's
  ±80-char context for FPs; the label item the rule missed for FNs) — the review's point: a "false positive"
  may be a label omission, and only examples let a human tell. Cap the listing at 15 per candidate with the
  overflow counted.
- **`cmpscan-v1` baseline row**: the same measurement for the existing 15 cap-triggering phrases as a set
  (hit rate + concept precision/recall where a concept applies), so v2 candidates are judged against what the
  scanner already catches, and overlap (candidate fires ∧ v1 already fired) is reported — a candidate that
  only fires where v1 already capped adds nothing.
- Emits `docs/165-cmpscan-candidate-hits.csv` (candidate × accession hit matrix, long form) and prints the
  summary tables.

### 2. Findings doc — `docs/165-findings-cmpscan-v2-candidates.md`

Committed with: the per-candidate table (hit rate / precision / recall / F1 / v1-overlap), the FP/FN example
listings, and a decisions section recommending which candidates clear the bar for the production cmpscan-v2
spec (out of scope here), which need narrowing (with the tested narrower variant beside them), and which are
rejected with the example that killed them. Standing caveats stated: the concept reference is derived from
EXPLORATORY ratified labels (spec 162 status), the mapping taxonomy is regex-coded with 246/497 items
uncategorized, and 63 of the 298 filings have no labels at all (they contribute hit rates only).

## Tests

Script-level fixture tests in the `AnalyzeLabelsScriptTests` style (temp tree, tiny fixture exhibits +
mapping):

- A candidate matching a fixture exhibit is counted, and its FP listing carries the matched context.
- Precision/recall computed correctly against a fixture reference (including a filing with the concept but
  no phrase hit ⇒ FN listed).
- Tampered exhibit (hash mismatch) fails naming the file.
- Unlabeled filings contribute to hit rate but never to precision/recall.
- The v1 baseline row and overlap column render.

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

- [ ] `measure-cmpscan-candidates.ps1` with the committed candidate table, manifest hash verification,
      per-candidate hit/precision/recall/F1 + Wilson, FP/FN example listings, v1 baseline + overlap, and the
      hit-matrix CSV.
- [ ] Measurement executed over the 298 exhibits; `docs/165-findings-cmpscan-v2-candidates.md` +
      `docs/165-cmpscan-candidate-hits.csv` committed with the decisions section and caveats.
- [ ] No production file touched; no pin move; `dotnet build Radar.sln -c Release` /
      `dotnet test Radar.sln -c Release --no-build` green (new script tests included).
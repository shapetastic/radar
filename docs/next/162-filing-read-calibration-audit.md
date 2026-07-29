# Task: AI filing-read calibration audit — blinded second-reader protocol, harness, and the full-cohort study

> **RESEARCH SPEC (spec-156/158 genre): read-side only, no scoring change, no fingerprint input, no pin
> move.** Motivated by the external plausibility review (2026-07-29): the reader's self-reported confidence
> has never been empirically calibrated. Spec 160 (the comparability cap) is containment; this is the
> measurement underneath it. **A 30-filing blinded pilot was run 2026-07-29 before this spec was written**
> — summary labels committed at `docs/162-calibration-pilot-labels.csv`.
>
> **Amended 2026-07-29 before dispatch** after review, which found the draft could have produced a
> convincing-looking but invalid calibration: (1) the cohort must be read from the MODEL-SCOPED cache
> segment, not the directory root — verified: the active scope
> `openai-deepseek-ai-deepseek-v4-flash-8f94f2dbe65fcb93` holds 145 directional + 153 no-signal records,
> the legacy root holds 5 stale files duplicating active accessions, **two with conflicting outcomes** —
> so the remaining directional cohort is **115**, not 119; (2) the no-signal CIK-recovery path is
> `data/evidence/raw/filing/**` (singular — the drafted plural path matches nothing); (3) exhibit
> selection/normalization must REUSE the production reader, not a regex reimplementation; (4) the headline
> curve is renamed to what it measures (inter-model agreement) and true calibration is defined against
> human-adjudicated labels sampled in every confidence bin; (5) study execution is now split into an
> explicitly-accepted Phase B rather than dangling outside acceptance; (6) one canonical label schema is
> declared and the pilot CSV is marked as a lossy legacy view; (7) sampling, uncertainty and the
> false-negative threshold are precommitted.

## Pilot findings (n=30) — pilot DESCRIPTIONS, not population estimates

The pilot deliberately oversampled (all 4 Negative reads in the corpus, the CASS control, a
confidence-spread of Positives) and then added 20 population-representative Positives, so its rates
describe the labeled set, not the population; the full study produces the population numbers. With that
caveat:

1. **Direction was never inverted; 3/30 directional reads should have been Mixed** — all three
   dirty-comparison prints read at face value. The failure mode is precisely characterized: the reader does
   not hallucinate direction; it over-commits on comparability-broken headlines.
2. **Confidence ran hot and compressed**: reader mean 0.885 vs blinded-skeptic mean 0.762 (higher in
   26/30), clustering at 0.85–0.95 regardless of print quality. The three disagreements sat at reader
   confidence 0.75–0.85 — its 0.95s all agreed — so the ordering carries some signal; the scale does not.
3. **Clean YoY comparisons were the exception: 5/30.**
4. **An evidenced `cmpscan-v2` gap: acquisitions/perimeter changes** (HWKN ×2, DGII, AGYS, MMSI, STRL,
   PLUS) — matched by no `cmpscan-v1` phrase. Candidate v2 markers now with evidence: `acquisition`,
   `pro forma`, `deconsolidation`. (Not changed here — that is a cmpscan-v2 slice fed by this study.)
5. **Materiality is unencoded**: skeptic graded low/moderate/high (ERII's strength-8 Negative graded *low*
   — pre-communicated timing in a seasonally tiny quarter) while every AI read carries constant Strength 8.

**Pilot method caveats, recorded**: the first 10 labels were produced from wire/IR reproductions after SEC
403'd the agents' fetcher (verbatim copies, but not the EDGAR bytes); the 20 batch-2 exhibits were
pre-fetched with an ad-hoc tag-stripper, NOT the production normalizer — so pilot labels carry a small
input-parity caveat the full study eliminates. Neither affects the committed numbers' arithmetic; both are
why the harness below exists.

## What this spec builds — Phase A (dispatchable via run-next)

### 1. `Radar.CalibrationAudit` — a small read-only console, `Radar.ChannelFeasibilityAudit` pattern

Reuse over copy is the point of building this in .NET rather than PowerShell — three production seams the
draft harness had reimplemented (wrongly) are consumed directly:

- **Cohort**: resolve the cache through the real model-scoped path logic (`FileAnalyzedFilingCache`'s
  scoping — the same `provider:model` identity the Worker config produces), pinning the exact scope segment
  in the output. Legacy-root files are EXCLUDED and listed with a `legacy-scope` reason (including the two
  outcome-conflicting accessions, named). Duplicate accessions inside the worksheet are an error, not a
  dedupe.
- **Exhibit text**: fetch through `ISecEarningsReleaseReader` (`HttpSecEarningsReleaseReader`) — the
  production index-table parse, the production EX-99.1-preferred/largest-EX-99.* selection, the shared
  normalizer — so the skeptic reads the SAME normalized text the DeepSeek reader judged, by construction.
  Per exhibit, record: selected filename, exhibit type, URL, normalized-content hash, normalized length,
  and the analyzer `MaxInputLength` in force (so input-truncation-explainable misreads are distinguishable
  at adjudication). Requires `RADAR_SEC_UA`; paced by the existing `SecRequestPacer`; re-runnable
  (skip-if-present keyed on content hash presence, refetch on the short-body tripwire).
- **No-signal CIK recovery**: from `data/evidence/raw/filing/**` (singular), matching the accession in the
  persisted index `SourceUrl`. Unrecoverable accessions are listed, never silently dropped.

Outputs: `worksheet.csv` (sealed model columns clearly marked), `exhibits/{ticker}-{accession}.txt`,
`exhibit-manifest.csv` (the parity record above).

The console lives beside `src/Radar.ChannelFeasibilityAudit` with the same read-only discipline: nothing
under the production projects changes; no store is written.

### 2. `scripts/calibration-audit/analyze-labels.ps1`

Joins `labels.jsonl` to the sealed worksheet and emits, with honest Ns:

- **Inter-model agreement curve** — reader-confidence bins × skeptic agreement. **Named exactly that**:
  two models agreeing is not evidence a 0.90 read was 90% correct.
- **Calibration table (adjudicated rows only)** — reader-confidence bins × human-adjudicated correctness.
  Empty bins render as "no adjudicated labels", never interpolated.
- Clean-rate, comparability-item frequency (the cmpscan-v2 evidence table), materiality ×
  constant-strength cross-tab, false-negative table for the no-signal sample, and the adjudication queue.
- Wilson 95% intervals on every headline rate.

### 3. Canonical label schema — ONE definition, and the pilot CSV's relationship to it stated

`labels.jsonl`, one JSON object per filing:
`{ accession, ticker, cik, batch, exhibitContentHash, label: { direction, directionConfidence,
comparisonClean, comparabilityItems[], material, keyFacts[] }, adjudication: { status:
pending|confirmed|overturned|n/a, adjudicatedDirection?, note? } }` — the sealed model answer is joined
from the worksheet at analysis time, never stored in the label file (blinding survives the file format).

`docs/162-calibration-pilot-labels.csv` is declared a **lossy legacy summary** of the pilot
(schema `pilot-flat`): it preserves accession/direction/confidence/agreement/clean/materiality plus a
one-line KeyItem, and drops the structured `comparabilityItems` amounts and `keyFacts` (preserved in the
session transcript). Pilot rows join the study by accession; they are append-only and are NOT relabeled —
except any pilot row later adjudicated, whose adjudication is recorded in `labels.jsonl` like every other.

### 4. Protocol (documented in the spec + script headers)

- **Blinding is structural**: labeling agents receive ONLY company, CIK, accession and the LOCAL exhibit
  path; no other local file (repo files contain sealed answers), no web. REIT filings carry the REIT
  framing note (judge on FFO/AFFO). ≤5 concurrent agents (the pilot's 20-at-once drew a 529 wave).
- **Adjudication makes ground truth**; labels alone are a second AI opinion. The queue is: ALL
  disagreements, ALL labels with identification/parity doubts, **and a deterministic stratified sample of
  AGREEMENTS — 5 per reader-confidence bin** (bins ≤0.6, 0.7, 0.8, 0.9, ≥0.95; first 5 by ascending
  accession in each bin) — because a calibration claim requires adjudicated rows in every bin, not just
  where the models fought. Adjudication is the maintainer's; expected total ~25–35 rows.

## Phase B — the study itself (executed in-session with agents after Phase A merges; NOT run-next work)

Deterministic, precommitted:

- **Directional cohort**: all 115 unlabeled active-scope directional reads (145 − 30), ordered by
  accession, batches of ≤5.
- **No-signal sample**: 30 of 153, selected as every 5th record of the accession-ordered list starting at
  index 0 (deterministic, no RNG — AD-3 discipline). **Extension rule, precommitted**: if adjudication
  confirms ≥1 genuinely-directional missed print, or the Wilson 95% upper bound on the miss rate exceeds
  10%, extend by a further 30 (the next offset) before writing conclusions.
- **Completion gate**: `docs/162-findings-filing-read-calibration.md` (findings + both curves + decisions
  section feeding the confidence-remap / cmpscan-v2 / structured-extraction specs) and
  `docs/162-calibration-labels-full.jsonl` committed; adjudication queue resolved by the maintainer.
  **Until that commit lands, spec 162 is NOT done** — Phase A merging is scaffolding, not completion, and
  the spec is promoted to `docs/` only with Phase B's artifacts.

## Constraints

- Read-side only: no production project changes, no scoring behaviour, descriptor, fingerprint or store
  touched. The pins do not move.
- All EDGAR traffic goes through the console (paced, `RADAR_SEC_UA`, sequential); labeling agents make zero
  SEC requests.
- Labels never flow into runtime values by side door: findings inform SPECS.
- `docs/162-calibration-pilot-labels.csv` is append-only.

## Out of scope, recorded not built

- The confidence remap (needs the adjudicated curve; fingerprint-moving; own spec).
- `cmpscan-v2` (fed by the comparability-item frequency table; own spec).
- Structured financial-comparison extraction (requirements come from these findings; own arc).
- Labeling the entire no-signal cohort (sampled at 30 + the precommitted extension rule).

## Acceptance criteria — Phase A (the run-next PR)

- [ ] `Radar.CalibrationAudit` console: model-scoped cohort (scope segment pinned; legacy root excluded and
      listed; the two outcome-conflicting accessions named), production reader/normalizer reuse, exhibit
      manifest with filename/type/URL/content-hash/length/MaxInputLength, singular `raw/filing` CIK
      recovery with unrecoverables listed.
- [ ] `analyze-labels.ps1` emitting the inter-model agreement curve (named as such), the
      adjudicated-calibration table, Wilson intervals, and the queue including the per-bin agreement
      sample.
- [ ] Canonical `labels.jsonl` schema documented; pilot CSV declared lossy-legacy with the exact dropped
      fields named.
- [ ] Protocol section verbatim in the findings-doc skeleton (blinding, ≤5 concurrency, REIT note,
      adjudication-makes-ground-truth).
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release --no-build` green; nothing
      under existing production projects changed.

## Acceptance criteria — Phase B (the study; blocks promotion of this spec to docs/)

- [ ] 115 directional + 30 no-signal labels produced under the protocol, committed as
      `docs/162-calibration-labels-full.jsonl`.
- [ ] Adjudication queue (disagreements + doubts + per-bin agreement sample) resolved by the maintainer and
      recorded in the JSONL.
- [ ] `docs/162-findings-filing-read-calibration.md` committed with both curves (honest Ns, Wilson
      intervals), the false-negative table with the precommitted threshold applied, and the decisions
      section.

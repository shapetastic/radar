# Task: AI filing-read calibration audit — blinded second-reader protocol, harness, and the full-cohort study

> **RESEARCH SPEC (spec-156/158 genre): read-side only, no scoring change, no fingerprint input, no pin
> move.** Motivated by the external plausibility review (2026-07-29): the reader's self-reported confidence
> has never been empirically calibrated. Spec 160 (the comparability cap) is containment; this is the
> measurement underneath it. **A 30-filing blinded pilot was run 2026-07-29 before this spec was written**
> — summary labels committed at `docs/162-calibration-pilot-labels.csv`.
>
> **Amended twice before dispatch (2026-07-29).** Round 1 fixed: model-scoped cohort (active scope
> `openai-deepseek-ai-deepseek-v4-flash-8f94f2dbe65fcb93` = 145 directional + 153 no-signal; 5 stale
> legacy-root files duplicate active accessions, two with conflicting outcomes), the singular
> `data/evidence/raw/filing/**` recovery path, production-reader reuse, agreement-vs-calibration naming,
> Phase A/B acceptance split, canonical schema, precommitted sampling. Round 2 fixed four blockers:
> (1) the parity study would have contained **zero Negative reads** (all four live in the pilot, whose
> inputs were not production-parity) — the 30 pilot filings are RELABELED under canonical input as primary
> study rows; (2) `ISecEarningsReleaseReader` is `internal`, so the console gets a narrow
> `InternalsVisibleTo` (existing Infrastructure precedent), never reflection or copied parsing; (3) the
> blinded labeler now receives the **exact truncated model input** (`ChatFilingAnalyzer` truncates FIRST,
> leading substring of `MaxInputLength`), with the full normalized text kept for comparability analysis
> only; (4) the no-signal minimum rises to 60 because 0/30 has a Wilson 95% upper bound of ~11.4% — above
> the 10% threshold, so the old rule extended on every possible outcome (and every-5th-of-153 selected 31
> rows, not 30). Bins are now exact half-open intervals with hash-ordered sampling, and labels carry full
> provenance. **Round 3 fixed the last statistical blocker**: the calibration estimate is now computed on a
> PROBABILITY SAMPLE selected irrespective of agreement (the round-2 queue adjudicated every disagreement
> but only 5 agreements per bin — over-representing failures and biasing estimated accuracy downward, which
> Wilson intervals cannot repair), the second-reader model and exact prompt template are precommitted
> before Phase B, and the no-signal extension is explicitly one-shot.

## Pilot findings (n=30) — pilot DESCRIPTIONS, not population estimates

The pilot deliberately oversampled (all 4 Negative reads in the corpus, the CASS control, a
confidence-spread of Positives) and then added 20 population-representative Positives, so its rates
describe the labeled set, not the population. With that caveat:

1. **Direction was never inverted; 3/30 directional reads should have been Mixed** — all three
   dirty-comparison prints read at face value. The reader does not hallucinate direction; it over-commits
   on comparability-broken headlines.
2. **Confidence ran hot and compressed**: reader mean 0.885 vs blinded-skeptic mean 0.762 (higher in
   26/30), clustered at 0.85–0.95 regardless of print quality. The three disagreements sat at reader
   confidence 0.75–0.85; its 0.95s all agreed — ordering carries signal, the scale does not.
3. **Clean YoY comparisons were the exception: 5/30.**
4. **An evidenced `cmpscan-v2` gap: acquisitions/perimeter changes** (HWKN ×2, DGII, AGYS, MMSI, STRL,
   PLUS) — matched by no `cmpscan-v1` phrase. Candidates with evidence: `acquisition`, `pro forma`,
   `deconsolidation`. (Not changed here — a cmpscan-v2 slice fed by this study.)
5. **Materiality is unencoded**: skeptic graded low/moderate/high (ERII's strength-8 Negative graded *low*)
   while every AI read carries constant Strength 8.

**Pilot method caveats, recorded**: the first 10 labels came from wire/IR reproductions (SEC 403'd the
agents' fetcher); the 20 batch-2 exhibits used an ad-hoc tag-stripper, not the production normalizer; and
no pilot label saw the model's truncated input. **Consequence (round-2 finding): the pilot labels are NOT
production-parity rows, and since all four active-scope Negative reads are pilot rows, a study of only the
remaining 115 would contain zero Negatives.** Therefore the pilot's 30 filings are **relabeled in Phase B
under the canonical input** and those relabels are the primary study rows; the pilot CSV stays as the
historical record, and the pilot-vs-relabel delta is itself reported as an **input-path stability**
finding (a free measurement of how sensitive labels are to input provenance).

## What this spec builds — Phase A (dispatchable via run-next)

### 1. `Radar.CalibrationAudit` — a small read-only console, `Radar.ChannelFeasibilityAudit` pattern

Reuse over copy: three production seams are consumed directly, never reimplemented.

- **Access**: `Radar.Infrastructure` gains `InternalsVisibleTo("Radar.CalibrationAudit")` — the ONE
  permitted change to an existing production project, non-behavioural, following the existing
  `InternalsVisibleTo("Radar.Infrastructure.Tests")` precedent. No reflection, no copied parsing logic.
- **Cohort**: resolved through the real model-scoped cache path logic (`FileAnalyzedFilingCache`'s
  scoping), pinning the exact scope segment in the output. Legacy-root files EXCLUDED and listed with a
  `legacy-scope` reason (the two outcome-conflicting accessions named). Duplicate accessions in the
  worksheet are an error.
- **Exhibit text**: fetched through the real `HttpSecEarningsReleaseReader` (production index-table parse,
  EX-99.1-preferred/largest-EX-99.* selection, shared normalizer). Per filing the console writes TWO
  texts + hashes:
  - `exhibits-full/{ticker}-{accession}.txt` — full normalized text (+ hash): for comparability analysis
    and adjudication only.
  - `exhibits-model-input/{ticker}-{accession}.txt` — the **exact model input**: the leading
    `MaxInputLength`-character substring, exactly as `ChatFilingAnalyzer` truncates before the model call
    (+ hash, + a `truncated: true|false` flag). **This is the only text a blinded labeler receives** —
    calibration asks "was the read right given its input"; whether the input was sufficient is a separate,
    adjudicator-visible question.
  The `exhibit-manifest.csv` records: accession, selected filename, exhibit type, URL, full-text hash and
  length, model-input hash and length, truncated flag, `MaxInputLength` in force. Requires `RADAR_SEC_UA`;
  paced by the existing `SecRequestPacer`; re-runnable (skip keyed on manifest hash presence; short-body
  tripwire refetches).
- **No-signal CIK recovery**: from `data/evidence/raw/filing/**` (singular), matching the accession in the
  persisted index `SourceUrl`. Unrecoverable accessions listed, never silently dropped.

### 2. `scripts/calibration-audit/analyze-labels.ps1`

Joins `labels.jsonl` to the sealed worksheet and emits, with honest Ns and Wilson 95% intervals on every
headline rate:

- **Inter-model agreement curve** (named exactly that) — reader-confidence bins × skeptic agreement.
- **Calibration table (calibration probability sample ONLY)** — bins × human-adjudicated correctness,
  computed exclusively over the `calibration-sample` rows (selected irrespective of agreement, below);
  disagreement/doubt-queued adjudications are NEVER pooled into these rates — conditioning on a set that
  contains every failure but only a slice of successes biases accuracy downward, and Wilson intervals do
  not repair selection bias. Empty bins render "no adjudicated labels", never interpolated.
- Clean-rate, comparability-item frequency (the cmpscan-v2 evidence table), materiality ×
  constant-strength cross-tab, false-negative table, **input-path stability table** (pilot vs relabel
  direction/clean deltas), and the adjudication queue.

**Confidence bins, exact half-open intervals**: `[0,0.60)`, `[0.60,0.70)`, `[0.70,0.80)`, `[0.80,0.90)`,
`[0.90,0.95)`, `[0.95,1.00]`. (The store contains 0.65/0.75/0.85/0.91/0.92 — every value maps
unambiguously.)

### 3. Canonical label schema — ONE definition, with full provenance

`labels.jsonl`, one JSON object per filing:

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

The sealed model answer is joined from the worksheet at analysis time, never stored in the label file.
`selectionReason` records WHY a row entered the adjudication queue; `calibration-sample` takes precedence
when a row qualifies both ways (a disagreeing row inside the probability sample is still a sample row —
that is the point of sampling irrespective of agreement). **The labeling prompt template is committed in
Phase A** (`scripts/calibration-audit/labeling-prompt.md`) and `promptHash` is its hash, so a mid-study
template edit is visible; **the second reader is precommitted for the whole study: `anthropic:claude-fable-5`
(the `radar-skeptic-reviewer` agent), a different model family from the DeepSeek reader** — recording
`labeler` per batch detects drift, precommitting prevents it, and changing the labeler mid-study is a
protocol-version bump that restarts the affected labels. Retries record which attempt they replaced.

`docs/162-calibration-pilot-labels.csv` is a **lossy legacy summary** of the pilot (schema `pilot-flat`):
preserves accession/direction/confidence/agreement/clean/materiality + one-line KeyItem; drops structured
`comparabilityItems` amounts and `keyFacts`. Append-only, historical; superseded as study data by the
Phase-B relabels.

### 4. Protocol (documented in the spec + script headers)

- **Blinding is structural**: labeling agents receive ONLY company, CIK, accession and the local
  **model-input** exhibit path; no other local file, no web. REIT filings carry the REIT framing note
  (judge on FFO/AFFO). **≤5 concurrent agents** (the pilot's 20-at-once drew a 529 wave).
- **Adjudication makes ground truth**, and is itself two-step blinded: the adjudicator first records their
  own `blindCall` from the exhibit text alone, THEN unblinds both model answers and records the final
  verdict — both steps persisted in the JSONL.
- **The queue has two separately-analyzed parts, and only one feeds calibration**:
  1. **Calibration probability sample** — `min(10, bin size)` rows per confidence bin, selected by
     SHA-256(accession) hex ascending within the bin, **irrespective of agreement status**. This sample
     ALONE produces the calibration rates and their Wilson intervals. (Deterministic without tracking
     CIK-prefix order, which plain accession sort does.)
  2. **Error-diagnosis set** — ALL remaining disagreements and ALL labels with
     identification/parity/truncation doubts, adjudicated for failure-mode analysis and reported
     separately; NEVER pooled into the calibration rates.
  Expected total ~45–60 rows.

## Phase B — the study itself (executed in-session with agents after Phase A merges; NOT run-next work)

Deterministic, precommitted:

- **Directional cohort: all 145 active-scope directional reads** = the 30 pilot filings RELABELED under
  canonical model input + the 115 unlabeled, ordered by SHA-256(accession) hex, batches of ≤5.
- **No-signal sample: minimum 60 of 153**, selected as the first 60 by SHA-256(accession) hex order.
  **Extension rule, ONE-SHOT**: if adjudication confirms ≥1 genuinely-directional missed print, or the
  Wilson 95% upper bound on the miss rate exceeds 10%, extend by exactly the next 30 in hash order and
  **report the final result at N=90 — the trigger is evaluated once, never re-applied to the extended
  set** (otherwise a single confirmed miss would demand extension forever). (At 0/60 the upper bound is
  ~6.0%, so a fully-clean result does NOT auto-extend — the rule
  can actually pass, unlike the 30-row version whose 0-miss bound of ~11.4% extended on every outcome.)
- **Completion gate**: `docs/162-findings-filing-read-calibration.md` (both curves with honest Ns +
  Wilson intervals, false-negative table with the threshold applied, input-path stability table, decisions
  section feeding the confidence-remap / cmpscan-v2 / structured-extraction specs) and
  `docs/162-calibration-labels-full.jsonl` committed; adjudication queue resolved by the maintainer.
  **Until that commit lands, spec 162 is NOT done** — Phase A merging is scaffolding, and the spec is
  promoted to `docs/` only with Phase B's artifacts.

## Constraints

- Read-side only: no behavioural change to any production project (`InternalsVisibleTo` is the one
  permitted attribute addition); no scoring behaviour, descriptor, fingerprint or store touched. The pins
  do not move.
- All EDGAR traffic goes through the console (paced, `RADAR_SEC_UA`, sequential); labeling agents make
  zero SEC requests.
- Labels never flow into runtime values by side door: findings inform SPECS.
- `docs/162-calibration-pilot-labels.csv` is append-only.

## Out of scope, recorded not built

- The confidence remap (needs the adjudicated curve; fingerprint-moving; own spec).
- `cmpscan-v2` (fed by the comparability-item frequency table; own spec).
- Structured financial-comparison extraction (requirements come from these findings; own arc).
- Labeling the entire no-signal cohort (minimum 60 + the precommitted extension rule).

## Acceptance criteria — Phase A (the run-next PR)

- [ ] `Radar.CalibrationAudit` console: model-scoped cohort (scope pinned; legacy root excluded and
      listed; the two outcome-conflicting accessions named), production reader/normalizer reuse via
      `InternalsVisibleTo("Radar.CalibrationAudit")` (no reflection, no copied parsing), dual
      full/model-input exhibit outputs with hashes and truncated flags in the manifest, singular
      `raw/filing` CIK recovery with unrecoverables listed.
- [ ] `analyze-labels.ps1`: inter-model agreement curve (named as such), calibration table computed over
      the `calibration-sample` rows ONLY (min(10, bin size) per bin, hash-ordered, agreement-blind),
      error-diagnosis set reported separately, Wilson intervals, exact half-open bins as specified,
      input-path stability table.
- [ ] Canonical `labels.jsonl` schema with the provenance block (protocol version, labeler
      provider/model, prompt hash, timestamp, attempt/replacement) and `selectionReason`
      (`calibration-sample` precedence); pilot CSV declared lossy-legacy with dropped fields named.
- [ ] **The labeling prompt template committed** at `scripts/calibration-audit/labeling-prompt.md` (the
      `promptHash` source) and the second reader precommitted in the protocol section as
      `anthropic:claude-fable-5` (`radar-skeptic-reviewer`).
- [ ] Protocol section verbatim in the findings-doc skeleton (blinding on model-input text, ≤5
      concurrency, REIT note, two-step blinded adjudication, two-part queue).
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release --no-build` green; no
      behavioural change to existing production projects.

## Acceptance criteria — Phase B (the study; blocks promotion of this spec to docs/)

- [ ] **145 directional labels** (30 relabels + 115 new) and **no-signal labels at N=60, or N=90 if the
      one-shot extension triggered**, produced under the protocol on canonical model-input text by the
      precommitted labeler, committed as `docs/162-calibration-labels-full.jsonl` with full provenance
      blocks.
- [ ] Both queue parts resolved via the two-step blinded flow and recorded with `selectionReason`: the
      calibration probability sample (feeding the calibration table) and the error-diagnosis set
      (reported separately, never pooled).
- [ ] `docs/162-findings-filing-read-calibration.md` committed with both curves (honest Ns, Wilson
      intervals — calibration from the probability sample only), the false-negative table with the
      one-shot threshold applied, the input-path stability table, and the decisions section.

# Task: Shadow-mode forced-choice second pass over the no-signal cohort — measure the recoverable miss rate

> **RESEARCH SPEC (spec-156/158/162 genre): read-side only, shadow-mode, no scoring change, no fingerprint
> input, no pin move.** Motivated by spec 162 Phase B (merged 2026-07-31, PR #167): the exploratory
> false-omission rate is **33/90 = 36.7%** (Wilson 27.4–47.0%) — a third of sampled no-signal filings were
> genuinely-directional prints, 13 of them high-materiality. Both reviewers converged on the same next step:
> **a shadow-mode second pass over no-signal filings is the priority**, and it must be shadow-mode — its own
> scoped output, never live signals — until validated. This spec measures whether the misses are RECOVERABLE
> by prompting (forced choice instead of silent abstention) before anyone touches the production reader.

## Overview

The production reader (`openai:deepseek-ai/DeepSeek-V4-Flash` via `ChatFilingAnalyzer`) may return "no
directional signal", and spec 162 showed that outcome is unreliable as an absence claim. The cheapest
attackable hypothesis: **the reader abstains on prints it could read** — i.e. a forced-choice prompt (must
emit a direction + confidence on every filing, no abstain) recovers a large share of the 33 provisional
misses without flooding the 57 provisional non-misses with false alarms. If true, the production fix is a
prompt/threshold change (its own spec, fingerprint-moving via the `ai=` descriptor); if false, the misses
need a different mechanism (second model, pre-screen). Either way the decision needs this measurement.

Inputs already exist: the 298 archived production-parity model inputs
(`data/calibration-audit/exhibits-model-input/`, hash-pinned by `docs/162-exhibit-manifest.csv`), the sealed
worksheet (`docs/162-study-worksheet.csv`), and the Phase B labels (`docs/162-calibration-labels-full.jsonl`,
33 provisional-miss / 57 provisional-non-miss over the labeled 90). **Zero SEC traffic.** ~298 DeepInfra
reads (run over BOTH cohorts: recovery on no-signal, stability on directional) — negligible cost.

## Assignment

Worktree: any — but the exhibits live ONLY in the main repo's untracked `data/calibration-audit/`; pass
absolute paths to it (read-only) and hash-verify every exhibit against the committed manifest before use.
Dependencies: current main (post #167). Requires `DEEPINFRA_API_KEY` at run time (load from the canonical
key file the baseline task uses; never print or commit the value). No SEC UA needed (`--skip-fetch` semantics
throughout — no fetching).
Estimated time: ~1–2 hours code + one console run.

## Changes

### 1. `Radar.CalibrationAudit --shadow-read` — a new mode on the EXISTING console (reuse over copy)

- The console already owns cohort resolution, the pinned scope, the manifest and the exhibit archive; the
  shadow pass is a new mode, not a new project. It reads each archived **model-input** text (after verifying
  its SHA-256 + length against the manifest — spec-163 discipline; mismatch ⇒ fail naming the file, never
  read a tampered study input) and calls the model through the production plumbing.
- **Production analyzer reuse via an internal seam, byte-identical by default — and the shadow instruction
  REPLACES the production one, never appends to it.** The production prompt explicitly permits and
  instructs `Unknown` for ambiguous text; appending "no abstain" would send the model contradictory
  instructions and measure the contradiction, not the prompt. `ChatFilingAnalyzer` (or the narrowest seam
  the implementer verifies is equivalent — verify, don't assume) gains an internal instruction-override
  hook whose default is the EXACT current prompt text, so production behaviour is byte-identical when the
  hook is unused (asserted by test); when the console sets it, the committed shadow prompt is the COMPLETE
  system instruction sent. **The recorded hash covers exactly the instruction bytes sent to the model** —
  asserted by a test that the assembled instruction equals the committed `shadow-prompt.md` content under
  the LF canonicalization, so the hash in each record is a hash of what actually ran. No copied
  prompt-assembly or response-parsing logic.
- **The forced-choice prompt is committed** at `scripts/calibration-audit/shadow-prompt.md` (version
  `cal-shadow-v1`), and every shadow record carries its LF-normalized SHA-256 (the spec-163 canonicalization)
  plus the model identity — same provenance discipline as the study labels. Content: same task framing as the
  production read, but the model MUST return a direction (`Improving`/`Deteriorating`/`Mixed`/`Neutral` — the
  wider vocabulary is deliberate, it is what the calibration study judged) and a confidence in [0,1]; no
  abstain path.
- ⚠ **The shadow response gets its OWN contract — the production one cannot carry it.** Production's
  `FilingSentiment`/`FilingDirection` supports only `Unknown`/`Improving`/`Deteriorating`/`Mixed`
  (`Radar.Domain/Filings/FilingSentiment.cs`), and `ChatFilingAnalyzer`'s validation degrades an
  unrecognised direction to `Unknown`/confidence 0 — a shadow `Neutral` fed through it would silently
  become `Unknown/0` and every rate below would be garbage. Define a console-local `ShadowFilingSentiment`
  (direction ∈ the four shadow tokens, confidence, rationale, raw response) parsed by the console — reuse
  the production plumbing up to the chat call, never its response-validation path for the shadow vocabulary.
  **The vocabulary mapping is defined ONCE, in the analyzer script, and stated in the findings doc:**
  shadow `Improving` ↔ worksheet/label `Positive`, `Deteriorating` ↔ `Negative`, `Mixed` ↔ `Mixed`,
  `Neutral` ↔ `Neutral`. No other equivalence is permitted (in particular, `Mixed` never counts as agreeing
  with a directional label in the STRICT recovery rate).
- **Call/parse status is recorded per accession, separately from the result**: `ok` | `call-failed` |
  `parse-failed` (with the error and the raw response when present). An infrastructure failure must never
  be counted as a `Neutral` (or any other) read. Failed rows are re-runnable (the skip rule skips only `ok`
  records), and — see the decision block — **the precommitted decision is not evaluable until every LABELED
  row has an `ok` record**: the frozen criteria are absolute counts over fixed denominators (33/57/145),
  and excluding failures would silently change what 17/33 means or conceal instability behind missingness.
- **Outputs land ONLY under `{output-root}/shadow/`** — one JSON per accession (accession, cohort, forced
  direction, confidence, model's brief rationale, raw response, prompt hash, model identity, timestamp) plus
  a `shadow-summary.csv`. Re-runnable: skip an accession whose shadow record already exists (a `--fresh` flag
  may overwrite).
- ⚠ **The shadow pass MUST NOT write to `data/filings-cache/`** — the production model-scoped cache. A shadow
  read that landed there would be served to the next LIVE baseline run as a cached production read. Assert:
  the shadow mode's composition does not register the production cache writer (or the console's cache root
  points inside the shadow output root — whichever the implementer verifies is airtight; test it either way).
  Likewise no signal/evidence/score/report writes — the console has no such writers; keep it that way.

### 2. `scripts/calibration-audit/analyze-shadow-read.ps1` — deterministic measurement

Joins the shadow records to the sealed worksheet and the Phase B labels. Wilson 95% intervals and honest Ns
everywhere; every rate carries the standing caveats (labels are EXPLORATORY ratified same-family verdicts —
spec 162's status section; filings cluster within tickers so intervals are somewhat narrow). Sections:

1. **Recovery table (the headline)** — over the 90 labeled no-signal rows: forced direction vs
   provisional miss/non-miss. Recovery rate = P(forced read directional AND direction agrees with the
   adjudicated `finalDirection` | provisional miss), reported alongside the looser
   P(forced read directional | miss). False-alarm rate = P(forced read Positive/Negative | provisional
   non-miss). Broken out by forced-confidence bin (the spec-162 half-open bins) — the operating point the
   production spec would need.
2. **Stability table** — over the 145 directional rows: forced direction vs the sealed production direction
   (agree / flipped-to-Mixed-or-Neutral / inverted), by sealed confidence bin. A forced prompt that degrades
   the directional cohort is disqualifying evidence, and inversions are worse than abstentions.
3. **Unlabeled distribution** — the 63 no-signal rows outside the labeled 90: direction/confidence
   distribution only, explicitly marked "no reference labels; distribution, not accuracy".
4. **Precommitted decision block — the rule is FROZEN HERE, before any read runs.** The reviewer's point
   stands: an "acceptable" cost chosen after seeing the results is the tuning failure these specs exist to
   prevent. The rule:
   - **Completeness gate first**: the decision is evaluable ONLY when all 235 labeled rows (90 no-signal +
     145 directional) have status `ok`. Otherwise the block emits
     `SHADOW: INCONCLUSIVE (n rows unresolved: …accessions…)` and the remedy is to rerun the failures —
     never to decide on a subset. The frozen criteria below are absolute counts over the FIXED denominators
     33 / 57 / 145; excluding failures would silently redefine them or hide instability as missingness.
     (The 63 unlabeled rows are outside the decision and may carry failures without blocking it.)
   - **Threshold semantics, defined once (τ is the evaluation threshold):** a forced read counts as
     DIRECTIONAL at τ iff its direction is `Improving` or `Deteriorating` AND its confidence ≥ τ; any other
     result — `Mixed`, `Neutral`, or a directional read with confidence < τ — counts as NONDIRECTIONAL at
     τ. On the no-signal cohort: strict recovery = directional-at-τ with direction agreeing with the
     adjudicated `finalDirection`; false alarm = directional-at-τ on a provisional non-miss. On the
     directional cohort: INVERSION = directional-at-τ with the direction OPPOSITE the sealed one
     (below-threshold opposite reads are flips, not inversions); FLIP = any nondirectional-at-τ result on a
     sealed-directional row.
   - **Primary evaluation, at τ = 0.80 exactly:** the forced-choice approach is SUPPORTED iff ALL of —
     strict recovery ≥ 50% (≥ 17 of 33), false alarms ≤ 15% (≤ 9 of 57, counting 9 as within the bound),
     ZERO inversions (one disqualifies outright), and flips ≤ 10% (≤ 15 of 145, counting 15 as within).
   - **One-shot fallback (162's extension-rule pattern):** if the primary fails at τ = 0.80, evaluate the
     SAME four criteria once at τ = 0.90. No further threshold shopping — the rule is evaluated at most
     twice, at these two precommitted points.
   - **Outcome is machine-readable**: `SHADOW: SUPPORTED (τ=0.80|0.90, …numbers…)` /
     `SHADOW: NOT-SUPPORTED (…numbers at both τ…)` / `SHADOW: INCONCLUSIVE (…)`. SUPPORTED ⇒ the production
     recall spec may proceed citing this rule; NOT-SUPPORTED ⇒ the misses need a different mechanism
     (second model / pre-screen — their own specs). **Every other number in the report — the full threshold
     sweep included — is DESCRIPTIVE ONLY and grounds no production recommendation.**

### 3. Findings doc — `docs/164-findings-shadow-no-signal-second-pass.md`

Committed with the run's tables, the decision section (does forced-choice recover the misses at an acceptable
false-alarm cost — feeding the PRODUCTION recall spec, which is out of scope here), and the honest caveats:
single-shot non-deterministic reads (record, don't average — rerunning may move individual rows), exploratory
reference labels, ticker clustering.

## Tests

- Instruction-seam default: production prompt byte-identical when the hook is unused (pin the assembled
  prompt for a fixture filing against the pre-change value).
- Shadow mode writes only under `{output-root}/shadow/`; the production filings-cache root is untouched by a
  shadow run (assert on a temp tree).
- Manifest verification: a tampered model-input file fails naming the file before any model call.
- Analyzer script: recovery/false-alarm/stability tables computed correctly from fixture shadow records +
  fixture labels (including: a miss recovered with the WRONG direction counts in the loose rate but not the
  strict one); unlabeled rows never enter any accuracy rate; re-run skip semantics.
- Contract: a fixture response with direction `Neutral` round-trips as `Neutral` (never `Unknown`/0); an
  unparseable response yields `parse-failed` and is listed; the vocabulary map is applied exactly (shadow
  `Mixed` vs label `Positive` is NOT a strict recovery); the assembled shadow instruction equals the
  committed `shadow-prompt.md` bytes (LF-canonicalized) and its hash is what the records carry.
- Decision block: a single failed labeled row ⇒ `SHADOW: INCONCLUSIVE` naming it (never a decision on a
  subset); an unlabeled-row failure does NOT block the decision; threshold semantics fixtures — an
  opposite-direction read at confidence < τ counts as a FLIP not an inversion, an opposite-direction read
  at ≥ τ disqualifies, a directional read below τ is nondirectional-at-τ; all three outcome forms
  (SUPPORTED / NOT-SUPPORTED / INCONCLUSIVE) render with their numbers from fixtures.

## Constraints

- Read-side only: no production behaviour change (the instruction seam's default is byte-identical,
  asserted); no scoring/descriptor/fingerprint/store change — all spec-160 pins stand; labels and shadow
  results never flow into runtime values by any side door. Findings inform SPECS.
- All model traffic is DeepInfra (paced ≤5 concurrent, sequential is fine); zero SEC requests.
- `data/calibration-audit/` inputs are read-only; the two outcome-conflicting legacy accessions and the
  cohort counts (145/153) are re-asserted from the worksheet before running, not assumed.

## Out of scope, recorded not built

- The production recall change itself (prompt/threshold/second-model — needs this measurement first;
  fingerprint-moving via the `ai=` descriptor segment when it comes).
- Multi-sample reads / self-consistency voting (cost is trivial but it changes the question; single-shot
  matches how production would run).
- Re-adjudicating the exploratory labels (spec 162's status stands).

## Acceptance criteria

- [ ] `--shadow-read` mode: manifest-verified inputs, production-plumbing reuse via the byte-identical
      instruction seam, committed hashed `cal-shadow-v1` prompt, outputs only under `shadow/`, production
      cache provably untouched.
- [ ] `analyze-shadow-read.ps1`: recovery (strict + loose), false-alarm, stability and unlabeled-distribution
      tables with Wilson intervals, honest Ns, threshold trade-off decision block.
- [ ] Shadow run executed over all 298 inputs; `docs/164-findings-shadow-no-signal-second-pass.md` committed
      with the tables, decisions and caveats.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release --no-build` green; no
      behavioural change to production projects; no pin move.
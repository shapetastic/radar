# Task: Comparability-aware confidence cap on the AI directional filing read

> **Motivating live failure (2026-07-29, CASS — the worked example).** The first 66-company run put Cass
> Information Systems #1 in the weekly report on ONE signal: the DeepSeek read of its Q2-2026 8-K at
> **confidence 0.90**, Positive `GuidanceChange`, anchored on "record net income" $5.2M → $10.6M. The skeptic
> pass against the primary sources found that doubling is a **two-sided artifact**: the prior-year quarter was
> depressed by a $3.6M investment-securities loss, the current quarter is boosted by a $1.8M bad-debt recovery
> that is the *second annual payment of a litigation settlement*, and the YoY perimeter changed (the TEM
> business was sold to Asignet 2025-06-30 — hence "continuing operations"). The company's own adjusted figure
> is +18.1%, and adjusted EPS **missed** consensus. A positive read was defensible; **0.90 confidence was
> not.** This is the same failure class as the llama3.1 EOSE misread that motivated spec 119 — but it survives
> the model swap, because any reader anchoring on the headline GAAP line inherits the flattery. The fix is
> deterministic, not a better prompt: when the release itself declares comparability breaks (one-offs,
> divestitures), cap the confidence Radar is willing to persist.

## Overview

`DirectionalFilingSignalSource` turns an earnings 8-K's EX-99.1 into at most one confidence-gated directional
`GuidanceChange` signal. Nothing in that path reacts to the release *declaring its own comparability breaks* —
the phrases "discontinued operations", "litigation settlement", "impairment", "gain on sale" are strong,
deterministic, in-band evidence that the YoY headline is not clean, and Radar currently ignores them while
trusting the model's self-reported confidence built on that headline.

This slice adds a deterministic **comparability scan** of the same release body the analyzer reads, and a
config **cap** applied to the AI read's confidence when the scan finds markers:

```
persistedConfidence = min(readConfidence, ComparabilityConfidenceCap)   // only when markers matched
```

Design principles, in house terms:

- **Deterministic code before AI.** The scan is a fixed phrase table (like `KeywordSignalExtractor`'s), not a
  second model opinion. The model's read is kept; only the *weight Radar assigns it* is bounded by what the
  release itself discloses.
- **The cap dampens; it does not veto.** CASS's quarter genuinely improved (+18.1% adjusted) — the right
  output is a *weaker positive*, not silence. At the default cap the CASS signal survives the `MinConfidence`
  gate (0.65 ≥ 0.6) but its scoring weight drops ~28%, taking strength×confidence from 7.2 to 5.2 — below a
  strong keyword match instead of dominating everything. A cap below `MinConfidence` (operator's choice)
  suppresses capped signals entirely; the gate applies **after** the cap.
- **Provenance visible everywhere.** A capped signal's `Reason` says so, naming the matched marker(s), so the
  weekly report's "Why noticed" shows the cap the same way it shows everything else.

## Assignment

Worktree: any
Dependencies: current main (post 159).
Estimated time: ~1–2 hours.

## Changes

### 1. `EarningsComparabilityScan` (new, `Radar.Infrastructure/Filings/`)

A static, deterministic scanner over the **full stripped EX-99.1 body** (the same text handed to the
analyzer, scanned BEFORE any `MaxInputLength` truncation — a marker past the truncation point is still a
marker). Case-insensitive, whitespace-normalised verbatim phrase containment, two marker groups:

- **Perimeter change:** `discontinued operations`, `continuing operations`, `divestiture`, `divested`,
  `sale of its`, `sold its`, `sale of the` + `business` proximity is NOT required — keep it verbatim-simple.
- **One-off items:** `impairment`, `litigation settlement`, `legal settlement`, `one-time`, `one time`,
  `non-recurring`, `nonrecurring`, `gain on sale`, `loss on sale`, `securities loss`, `securities losses`,
  `bad debt recovery`.

Returns the ordered distinct list of matched phrases (empty = clean). The scanner has a
`public const string Version = "cmpscan-v1"` declared beside the table with the obligation stated: **change
the table ⇒ bump the version** (parallel to `KeywordSignalExtractor.RuleSetVersion` — it is a rule-STRUCTURE
identity and it is hashed, below).

**Deliberately excluded from the table:** `non-GAAP` / `adjusted` — essentially every earnings release
contains reconciliation boilerplate, so those phrases would cap everything and turn the cap into a constant
(a constant re-scaling of every AI read is a `Strength` edit wearing a costume). The table targets phrases
that declare a *specific* comparability break. If measurement later shows the table over- or under-fires,
that is a `cmpscan-v2` with its own evidence.

### 2. `DirectionalFilingSignalOptions.ComparabilityConfidenceCap`

New `decimal`, default **0.65**, validated in `[0,1]` at registration (same place `MinConfidence` is
validated). Bound from `Radar:Ai:Filings:ComparabilityConfidenceCap` through `RadarWorkerOptions.Ai` exactly
like `MaxFilingsPerRun`. Semantics: **1.0 is the exact off-switch** — `min(conf, 1.0)` is the identity, so a
composition that sets 1.0 is byte-identical to pre-160 behaviour (asserted). Default 0.65 is chosen so a
capped read still clears the default `MinConfidence` 0.6 (dampen, don't veto) while landing a capped
strength-8 read (8 × 0.65 = 5.2) below a confident keyword match (6 × 0.85+) instead of above everything.

### 3. Apply the cap in `DirectionalFilingSignalSource` — at analysis time, recorded in the cache

In `AnalyzeFilingAsync`, after a structurally successful, authoritative read (body ≥ `MinPlausibleBodyLength`)
and BEFORE the `MinConfidence` gate and signal construction:

1. Run `EarningsComparabilityScan` on the body.
2. If markers matched: `confidence = min(analyzerConfidence, ComparabilityConfidenceCap)`; append to the
   signal `Reason`: `" (comparability cap: matched '<m1>', '<m2>')"`.
3. Apply the existing `MinConfidence` gate to the **capped** value. Capped-below-gate ⇒ the existing
   no-directional-signal path (cached as `NoDirectionalSignal`, debug record emitted — same as any
   below-confidence read today).

**Cache:** `AnalyzedFilingRecord` gains a trailing, nullable `ComparabilityMarkers` (list of matched
phrases; `null` = written pre-160, "not scanned" — never a false claim of a clean scan; empty list = scanned
clean). The cached `ExtractedSignal` already carries the capped confidence and annotated reason, so pass-1
replay needs **no** change to behave consistently. **Do NOT bump `AnalyzedFilingRecord.CurrentCacheVersion`**
— heal forward (the spec-142/145 rule): legacy cached reads replay exactly as recorded and age out of the
60-day scoring window naturally (fully converged by ~late September). Re-reading the whole cache would
re-spend AI on every filing and re-roll reads that were fine. **Bounded manual remedy for a known-bad legacy
read** (e.g. CASS's 0.90): delete that accession's file under `data/filings-cache/` — the next run re-reads
it under the scan. Document this in the source's XML doc; the coder does not delete anything.

### 4. Descriptor: fold the scan version + cap into the `ai=` segment — the AI-ON pins MOVE, deliberately

The cap changes the **confidence of emitted signals** — a comparability input exactly like `MinConfidence`
(hashed) and the reading model (hashed). Append to `_scoringDescriptor`, **after** `model=` (spec 119's
precedent: new fields LAST so the existing prefix is byte-stable):

```
;cmpscan=cmpscan-v1;cmpcap=<G29 of ComparabilityConfidenceCap>
```

`cmpscan` references the scanner's `Version` const (structure identity); `cmpcap` is the magnitude by value
(InvariantCulture `G29`, injective over [0,1] — same discipline as `minconf`).

Consequences, stated so nobody discovers them in a failed run:

- **Every AI-ON fingerprint moves once**: the 30-day unit pin (`radar-scoring-fp-28226897f97b` → new), the
  60-day live baseline stamp (`radar-scoring-fp-4da4b5ff6ec9` → new), and the 120-day long-window stamp
  (`radar-scoring-fp-81e9fab711f8` → new). Compute all three on the branch, update
  `ScoringConfigFingerprintTests` and the `scripts/run-profiles/default.json` `_comment` lineage (and the
  long-window comment if it names its stamp). **AI-OFF pins do not move** (the descriptor is folded only when
  the AI source is registered) — assert that.
- **`StrategyIdentityGuard` will trip once per strategy NAME on the first post-merge run** — all 10
  strategies share the `ai=` segment. That is the guard doing its job on a deliberate identity change (the
  spec-148 precedent). The operator remedy — delete the per-name records under
  `data/scoring-configs/strategies/` to acknowledge — must be stated in the PR body and appended to the
  `default.json` `_comment`. Series continuity is safe: `ScoreSeriesKey` keys on the NAME (spec 141), so no
  score series forks; the efficacy chart just draws its fingerprint-boundary tick.
- **No `_formula.Version` bump, no `KeywordSignalExtractor.RuleSetVersion` bump** — no formula or keyword
  rule changed. `cmpscan-v1` is its own parallel structure token.

### 5. Diagnostics (spec 115) — additive

`FilingReadDebugRecord` gains trailing nullable `ComparabilityMarkers` and `CappedConfidence` (null when no
cap applied), so `data/ai-debug/filings/{accession}.json` shows what the scan found and what the cap did.
Additive fields, null for legacy records; the sink stays best-effort and NOT a fingerprint input.

## Tests

- **CASS-shaped fixture** (excerpt-level text containing "continuing operations", "litigation settlement",
  a securities-loss mention, and bullish headline language): analyzer stub returns Positive 0.90 ⇒ emitted
  signal has confidence **0.65**, reason carries the cap annotation naming the markers, cache record carries
  the markers.
- **Clean fixture** (AGYS-shaped: record revenue, raised guidance, no marker phrases): 0.90 stays 0.90,
  reason unannotated, cache `ComparabilityMarkers` empty (scanned-clean ≠ not-scanned).
- **Cap-below-gate**: cap 0.5 (< MinConfidence 0.6) + markers ⇒ no signal, cached `NoDirectionalSignal`,
  debug record emitted.
- **Off-switch identity**: cap 1.0 ⇒ output byte-identical to pre-160 (assert the full signal, not just
  confidence).
- **Gate ordering**: read at 0.62 with markers and cap 0.65 ⇒ confidence stays 0.62 (cap is a ceiling, not a
  floor) — and read 0.90, cap 0.65, gate 0.7 ⇒ suppressed (gate applies after cap).
- **Replay honesty**: a legacy cache record (null markers, conf 0.90) replays at 0.90 unchanged.
- **Descriptor**: pinned string asserts field order `str;nov;minconf;model;cmpscan;cmpcap`; two options
  differing only in cap produce different descriptors; AI-OFF composition's fingerprint unchanged.
- **Fingerprint pins**: all three AI-ON pins updated with a lineage note; AI-OFF pins asserted unmoved.
- Truncation: a marker placed beyond `MaxInputLength` still caps (scan runs on the full body).

## Constraints

- Layering: the scan lives in Infrastructure beside the source; nothing new crosses into Application/Domain;
  no provider SDK anywhere near it (AD-5 untouched).
- The scan runs on text already in memory — **zero** new SEC requests, zero new AI spend, no cache
  invalidation.
- Phrase table is code (structure, versioned); cap magnitude is config (hashed by value). Don't blur that
  line — it is the AD-10 split.
- Do not touch the keyword extractor, any formula, or any scoring weight.
- Deleting/regenerating any existing cache, signal, score or evidence file is out of scope (heal forward).

## Out of scope, recorded not built

- Feeding the non-GAAP reconciliation to the model (a richer read is a different, bigger slice).
- Re-reading legacy cached filings under the scan (converges naturally within the 60-day window; manual
  per-accession deletion is the documented remedy for a known-bad read).
- Any change to `GuidanceChange` semantics or a new signal type for one-off-driven results.
- Marker-table tuning beyond v1 — measure first: the spec-115 debug records now capture the scan outcome per
  read, so after a few weeks the hit rate is measurable from `data/ai-debug/` before anyone argues about
  phrases.

## Acceptance criteria

- [ ] `EarningsComparabilityScan` with `Version = "cmpscan-v1"`, the two marker groups above, scanned on the
      full body before truncation.
- [ ] `ComparabilityConfidenceCap` (default 0.65, validated [0,1], bound from config; 1.0 = exact off-switch,
      asserted byte-identical).
- [ ] Cap applied before the gate; capped signals' `Reason` names the markers; cache and debug records carry
      the scan outcome (trailing nullable, null = pre-160).
- [ ] No cache version bump; legacy records replay unchanged (asserted).
- [ ] Descriptor extended `;cmpscan=…;cmpcap=…` after `model=`; all three AI-ON pins recomputed with lineage
      notes; AI-OFF pins unmoved; `default.json` `_comment` updated including the StrategyIdentityGuard
      acknowledge step.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.

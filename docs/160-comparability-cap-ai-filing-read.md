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
>
> **Amended 2026-07-29 before dispatch** after review: (1) the originally-proposed manual remedy for CASS's
> existing 0.90 signal was WRONG and is withdrawn — see "What this slice does NOT fix"; (2) the 0.65 rationale
> no longer claims a re-ranking below keyword matches that is arithmetically impossible; (3) the marker table
> is split into cap-triggering and diagnostic-only groups (three over-broad phrases demoted); (4) the cache
> records the scan POLICY, and a policy mismatch is a cache miss; (5) the cap lives beside `MinConfidence`
> under `Radar:Ai`, not in the diagnostics-only `Radar:Ai:Filings` section; (6) AD-16's precommitted
> primary-screen boundary must move, because capped and uncapped reads would otherwise mix inside its first
> eligible window; (7) `PersistReadDebug` is enabled in the live profile so the marker hit rate is actually
> measurable.

## Overview

`DirectionalFilingSignalSource` turns an earnings 8-K's EX-99.1 into at most one confidence-gated directional
`GuidanceChange` signal. Nothing in that path reacts to the release *declaring its own comparability breaks* —
the phrases "discontinued operations", "litigation settlement", "impairment", "gain on sale" are strong,
deterministic, in-band evidence that the YoY headline is not clean, and Radar currently ignores them while
trusting the model's self-reported confidence built on that headline.

This slice adds a deterministic **comparability scan** of the same release body the analyzer reads, and a
config **cap** applied to the AI read's confidence when the scan finds cap-triggering markers:

```
persistedConfidence = min(readConfidence, ComparabilityConfidenceCap)   // only when cap-triggering markers matched
```

Design principles, in house terms:

- **Deterministic code before AI.** The scan is a fixed phrase table (like `KeywordSignalExtractor`'s), not a
  second model opinion. The model's read is kept; only the *weight Radar assigns it* is bounded by what the
  release itself discloses.
- **The cap dampens; it does not veto.** CASS's quarter genuinely improved (+18.1% adjusted) — the right
  output is a *weaker positive*, not silence. At the default cap the CASS signal survives the `MinConfidence`
  gate (0.65 ≥ 0.6) and its scoring weight drops ~28% (strength×confidence 7.2 → 5.2). **Stated precisely,
  because the original draft got this wrong:** a capped AI read still outranks a deterministic keyword
  `GuidanceChange` match (6 × 0.65 = **3.9**) — and necessarily so: any cap that survives the 0.6 gate weighs
  at least 8 × 0.6 = 4.8, so "rank below the keyword signal" is not achievable by a surviving cap and is NOT
  a goal of this design. The goal is solely to bound overconfidence on self-declared-dirty comparisons. A cap
  set below `MinConfidence` (operator's choice) suppresses capped signals entirely; the gate applies
  **after** the cap.
- **Provenance visible everywhere.** A capped signal's `Reason` says so, naming the matched marker(s), so the
  weekly report's "Why noticed" shows the cap the same way it shows everything else.

## ⚠ What this slice does NOT fix — CASS's existing 0.90 signal (decided, not overlooked)

The original draft claimed deleting CASS's `data/filings-cache/` entry would force a corrected re-read. That
is wrong twice over, verified against the code:

1. `CollectionPass` hands the directional source **`newEvidence` only** (`CollectionPass.cs`, the
   `candidates = newEvidence.Where(...)` projection). CASS's 8-K evidence is already durable, so
   `AddIfNewAsync` returns false on every future run, the filing never re-enters the candidate list, and a
   deleted cache entry is simply never consulted again for it.
2. Even if a capped signal WERE re-produced, `FileSignalStore`'s cross-run collapse keeps the
   **earliest-created** copy per `(CompanyId, EvidenceId, Type, Direction)` — the 2026-07-29 0.90 signal wins
   the dedupe against any later capped twin, by design (the spec-142 known-at honesty rule).

So: **the accrued 0.90 signal stands and ages out naturally** — observed 2026-07-23, it leaves the 60-day
scoring window after as-of ≈ 2026-09-21. That is the standing heal-forward rule (specs 142/145: never rewrite
accrued history) applied to ourselves. An explicit correction mechanism for already-persisted signals
(supersede-by-reference, never deletion) is **out of scope, recorded not built** — if the need recurs it
deserves its own spec with provenance semantics, not a side door in this one.

## Assignment

Worktree: any
Dependencies: current main (post 159).
Estimated time: ~1–2 hours.

## Changes

### 1. `EarningsComparabilityScan` (new, `Radar.Infrastructure/Filings/`)

A static, deterministic scanner over the **full stripped EX-99.1 body** (the same text handed to the
analyzer, scanned BEFORE any `MaxInputLength` truncation — a marker past the truncation point is still a
marker). Case-insensitive, whitespace-normalised verbatim phrase containment, returning TWO ordered distinct
lists:

**Cap-triggering (v1)** — phrases that specifically declare a comparability break:

- Perimeter change: `discontinued operations`, `divestiture`, `divested`
- One-off items: `impairment`, `litigation settlement`, `legal settlement`, `one-time`, `one time`,
  `non-recurring`, `nonrecurring`, `gain on sale`, `loss on sale`, `securities loss`, `securities losses`,
  `bad debt recovery`

**Diagnostic-only (recorded, never caps)** — phrases that correlate with perimeter changes but over-match
ordinary prose, demoted per review: `continuing operations` (standard GAAP presentation language whenever a
discontinued segment exists in ANY comparative period, and common loose prose), `sale of its`, `sale of the`,
`sold its` (match product/stock/asset-sale prose unrelated to a perimeter change). They are persisted in the
cache/debug records so their true hit rate and co-occurrence with cap-triggering markers is measurable from
live data; promoting any of them into the cap-triggering set is a `cmpscan-v2` decision made on that
evidence, not on argument.

CASS's release still caps under this split — `litigation settlement` and the securities-loss language are
cap-triggering; its `continuing operations` phrasing is recorded diagnostically.

The scanner has a `public const string Version = "cmpscan-v1"` declared beside the table with the obligation
stated: **change either table ⇒ bump the version** (parallel to `KeywordSignalExtractor.RuleSetVersion` — a
rule-STRUCTURE identity, and it is hashed, below).

**Deliberately excluded entirely:** `non-GAAP` / `adjusted` — essentially every earnings release contains
reconciliation boilerplate, so those phrases would cap everything and turn the cap into a constant (a
constant re-scaling of every AI read is a `Strength` edit wearing a costume).

### 2. `ComparabilityConfidenceCap` — beside `MinConfidence`, NOT under `Radar:Ai:Filings`

New `decimal` on `DirectionalFilingSignalOptions`, default **0.65**, validated in `[0,1]` at registration
(same place `MinConfidence` is validated). Bound from **`Radar:Ai:ComparabilityConfidenceCap`** on
`RadarWorkerOptions.Ai` — directly beside `MinConfidence`/`Strength`/`Novelty`, because it is a
scoring-affecting magnitude like them. It must NOT go under `Radar:Ai:Filings`
(`AiFilingsWorkerOptions`), which is documented and wired as **diagnostics-only** ("never an
evidence/signal/scoring/report input"); parking a scoring knob there would falsify that contract. Semantics:
**1.0 is the exact off-switch** — `min(conf, 1.0)` is the identity, so a composition that sets 1.0 is
byte-identical to pre-160 behaviour (asserted). Default 0.65 keeps a capped read above the default
`MinConfidence` 0.6 (dampen, don't veto) while cutting its weight ~28%.

### 3. Apply the cap in `DirectionalFilingSignalSource` — at analysis time, with the POLICY recorded in the cache

In `AnalyzeFilingAsync`, after a structurally successful, authoritative read (body ≥ `MinPlausibleBodyLength`)
and BEFORE the `MinConfidence` gate and signal construction:

1. Run `EarningsComparabilityScan` on the body.
2. If cap-triggering markers matched: `confidence = min(analyzerConfidence, ComparabilityConfidenceCap)`;
   append to the signal `Reason`: `" (comparability cap: matched '<m1>', '<m2>')"`.
3. Apply the existing `MinConfidence` gate to the **capped** value. Capped-below-gate ⇒ the existing
   no-directional-signal path (cached as `NoDirectionalSignal`, debug record emitted — same as any
   below-confidence read today).

**Cache — record the policy, and treat a policy mismatch as a miss.** `AnalyzedFilingRecord` gains two
trailing nullable fields:

- `ComparabilityPolicy` (string): the policy the record was produced under, canonically
  `"cmpscan-v1;cap=<G29>"`. `null` = written pre-160 ("not scanned" — never a false claim of a clean scan).
- `ComparabilityMarkers` (two lists, or one structured record): the cap-triggering and diagnostic-only
  matches. Empty lists under a non-null policy = **scanned clean**, distinct from not-scanned.

Lookup rule in pass 1: a record whose `ComparabilityPolicy` is **null is a HIT** (heal forward — the accrued
cache is never mass-invalidated, and legacy reads age out of the 60-day window naturally, fully converged by
≈ merge + 60 days). A record whose policy is **non-null but ≠ the current policy string is a MISS** — it is
re-fetched and re-analyzed under the current policy, bounded like any miss by `MaxFilingsPerRun` and the 429
breaker. This makes policy staleness *visible and self-healing* instead of silently baked in: an operator who
tunes the cap or the coder who ships `cmpscan-v2` gets a bounded, automatic migration of post-160 records
(NOTE: this is deliberately **stronger** than the existing `MinConfidence` semantics, whose gate outcome is
baked into cached records — that asymmetry is accepted; extending policy-miss semantics to `MinConfidence` is
out of scope).

No `CurrentCacheVersion` bump — the null-policy hit rule IS the migration story.

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

### 5. AD-16: the precommitted v11 primary-screen boundary MUST move (append an amendment, do not edit in place)

AD-16 precommits the `disclosure-led-v11` primary screen's first eligible as-of date (2026-09-26). The AI
`GuidanceChange` signals attach to `sec-edgar` evidence — exactly the collector v11's single channel
consumes — so this slice changes that predictor's input regime. Because legacy (uncapped) cached reads keep
replaying for filings still in-window, a 60-day window mixes capped and uncapped reads until **(first
post-160 baseline run) + 60 days** — later than 2026-09-26 for any merge after 2026-07-27.

The coder appends a dated amendment to AD-16 in `docs/architecture-decisions.md`: the first eligible
primary-screen as-of date becomes **the later of 2026-09-26 and (first post-160 baseline run date + 60
days)**, with the mixed-regime rationale stated and the concrete date left for the operator to record after
that first run (expected ≈ 2026-09-28/29 for a ~2026-07-30 merge). Moving a precommitted boundary is itself a
precommitment-integrity act — it must be written down BEFORE the data exists, which is now.

### 6. Diagnostics (spec 115) — additive, and actually turned on

- `FilingReadDebugRecord` gains trailing nullable `ComparabilityMarkers` (both groups) and
  `CappedConfidence` (null when no cap applied). Additive fields, null for legacy records; the sink stays
  best-effort and NOT a fingerprint input.
- **`scripts/run-profiles/default.json` gains `"Ai": { "Filings": { "PersistReadDebug": true } }`** (merged
  into the existing `Ai` block) with a `_comment` note. Without this the marker hit-rate measurement this
  spec's out-of-scope section relies on cannot happen — `data/ai-debug/` is only written when the flag is on.
  Diagnostics-only, gitignored output, not a fingerprint input; the run cost is one small JSON per analysis
  attempt.

## Tests

- **CASS-shaped fixture** (excerpt-level text containing "litigation settlement", a securities-loss mention,
  "continuing operations", and bullish headline language): analyzer stub returns Positive 0.90 ⇒ emitted
  signal has confidence **0.65**, reason names the cap-triggering markers (and NOT "continuing operations"),
  cache record carries policy `cmpscan-v1;cap=0.65`, cap-triggering markers, and "continuing operations"
  in the diagnostic-only list.
- **Diagnostic-only fixture**: text whose ONLY matches are diagnostic-group phrases ⇒ confidence unchanged,
  reason unannotated, markers recorded in the diagnostic list (measurable but inert).
- **Clean fixture** (AGYS-shaped): 0.90 stays 0.90; cache policy non-null with both lists empty
  (scanned-clean ≠ not-scanned).
- **Cap-below-gate**: cap 0.5 (< MinConfidence 0.6) + cap-triggering markers ⇒ no signal, cached
  `NoDirectionalSignal` with policy + markers, debug record emitted.
- **Off-switch identity**: cap 1.0 ⇒ output byte-identical to pre-160 (assert the full signal, not just
  confidence).
- **Gate ordering**: read 0.62, markers, cap 0.65 ⇒ stays 0.62 (ceiling, not floor); read 0.90, cap 0.65,
  gate 0.7 ⇒ suppressed (gate after cap).
- **Cache policy rules — the full outcome × cause matrix, not just the produced-signal path**: null-policy
  record ⇒ HIT, replays unchanged (heal forward); matching policy ⇒ HIT; non-null mismatched policy ⇒ MISS,
  re-analyzed under the current policy — asserted for **all four** combinations: a cached
  `DirectionalSignalProduced` record and a cached `NoDirectionalSignal` record, each invalidated by a cap
  change AND by a scanner-version change. The `NoDirectionalSignal` × cap-change cell is the one a
  produced-signal-only test suite would silently miss: a read suppressed under an old lower cap must be
  re-analyzed (and may now emit) when the cap rises.
- **Descriptor**: pinned string asserts field order `str;nov;minconf;model;cmpscan;cmpcap`; two options
  differing only in cap produce different descriptors; AI-OFF composition's fingerprint unchanged.
- **Fingerprint pins**: all three AI-ON pins updated with a lineage note; AI-OFF pins asserted unmoved.
- Truncation: a cap-triggering marker placed beyond `MaxInputLength` still caps (scan runs on the full body).

## Constraints

- Layering: the scan lives in Infrastructure beside the source; nothing new crosses into Application/Domain;
  no provider SDK anywhere near it (AD-5 untouched).
- The scan runs on text already in memory — **zero** new SEC requests or AI spend on the default path (the
  only new fetches are policy-mismatch misses, which only exist after an operator/coder changes the policy,
  and are bounded by `MaxFilingsPerRun` + the 429 breaker like every miss).
- Phrase tables are code (structure, versioned); cap magnitude is config (hashed by value). Don't blur that
  line — it is the AD-10 split.
- Do not touch the keyword extractor, any formula, or any scoring weight.
- Deleting/regenerating any existing cache, signal, score or evidence file is out of scope (heal forward) —
  and per the "What this slice does NOT fix" section, no manual-deletion remedy is documented anywhere,
  because it does not work.

## Out of scope, recorded not built

- **An explicit correction mechanism for already-persisted signals** (supersede-by-reference with
  provenance, never deletion) — the only honest way to retire CASS's accrued 0.90 before it ages out; its
  own spec if the need recurs.
- Feeding the non-GAAP reconciliation to the model (a richer read is a different, bigger slice).
- Extending policy-miss semantics to `MinConfidence`'s baked-in gate outcome.
- Any change to `GuidanceChange` semantics or a new signal type for one-off-driven results.
- Marker-table tuning beyond v1 — measure first: with `PersistReadDebug` now on in the live profile, the
  per-read scan outcome (both groups) accrues in `data/ai-debug/`, so `cmpscan-v2` arguments can be had over
  hit-rate data instead of intuitions.

## Acceptance criteria

- [ ] `EarningsComparabilityScan` with `Version = "cmpscan-v1"`, the cap-triggering and diagnostic-only
      groups exactly as listed, scanned on the full body before truncation.
- [ ] `ComparabilityConfidenceCap` (default 0.65, validated [0,1]) bound from
      `Radar:Ai:ComparabilityConfidenceCap` beside `MinConfidence` — NOT under `Radar:Ai:Filings`; 1.0
      asserted byte-identical to pre-160.
- [ ] Cap applied before the gate from cap-triggering markers only; `Reason` names them; cache and debug
      records carry policy + both marker lists (trailing nullable, null = pre-160).
- [ ] Cache: null policy ⇒ hit (asserted replay-unchanged); non-null mismatch ⇒ miss, asserted over the full
      {DirectionalSignalProduced, NoDirectionalSignal} × {cap change, scanner-version change} matrix; no
      `CurrentCacheVersion` bump.
- [ ] Descriptor extended `;cmpscan=…;cmpcap=…` after `model=`; all three AI-ON pins recomputed with lineage
      notes; AI-OFF pins unmoved; `default.json` `_comment` updated including the StrategyIdentityGuard
      acknowledge step.
- [ ] AD-16 amendment appended: first eligible primary-screen as-of = later of 2026-09-26 and first
      post-160 baseline run + 60 days, rationale stated.
- [ ] `default.json` enables `Radar:Ai:Filings:PersistReadDebug`.
- [ ] The spec's "What this slice does NOT fix" holds: no code path, test, or doc claims the accrued CASS
      signal is corrected.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.

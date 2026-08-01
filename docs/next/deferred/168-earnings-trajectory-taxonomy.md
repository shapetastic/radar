# Task: Signal-type taxonomy — `EarningsTrajectory` with a structured basis; reserve `GuidanceChange` for explicit guidance actions

> ⚠️ **DEFERRED — do not dispatch before the un-defer gate at the bottom is met.** This spec moves the AI-ON
> fingerprint pins, re-keys a control strategy, and starts a new filing-read cache epoch. Doing that in the
> middle of the current strategy-comparison accrual window (v9 arms rankable ≈ 2026-08-17, baselines ≈
> 08-19+, batch-4 names ≈ 08-22, AD-16 first eligible primary screen 2026-09-26) would reset exactly the
> series the window exists to rank. Documented now so the decision is not lost; implemented later.

## Overview

Fix the spec-75 taxonomy misnomer at its root. Today `ChatFilingAnalyzer` is asked to classify the business
trajectory **as reported** — it is never asked about guidance — and `DirectionalFilingSignalSource`
hardcodes every passing directional read to `SignalType: "GuidanceChange"` with constant Strength 8.
Measured consequences (2026-08-01, MSEX skeptic review + spec-162 corpus): all 145 calibration directional
reads carry the token while 49 rationales mention neither guidance nor outlook, and the weekly report's #2
company was labelled with a guidance change it never issued. The label is false; the underlying
trajectory read is legitimate.

After this spec:

- The trajectory concept is named what it is: **`EarningsTrajectory`**.
- The read carries a **structured basis** — what the model grounded its direction in:
  `ReportedResults` | `GuidanceChange` | `Both`.
- The literal **`GuidanceChange` signal type is reserved** for an explicit guidance action (raised, cut,
  introduced, withdrawn). A read whose basis includes a guidance action emits `GuidanceChange`; otherwise
  it emits `EarningsTrajectory`.
- Still **exactly one scored signal per filing** — the basis selects the type; it never fans out into two
  independently scored signals (double-counting one filing must remain a deliberate, separately-specced
  design decision, not a side effect here).

## Assignment

Worktree: any
Dependencies: spec 167 (display relabel) merged; un-defer gate met.
Estimated time: ~2 hours.

## Changes

### 1. Analyzer contract — structured basis

- The typed analyzer result (`FilingSentiment` or successor) gains a required `Basis` field with the closed
  set `ReportedResults` / `GuidanceChange` / `Both`; the system instruction is extended to ask for it, with
  the definition: a guidance basis requires an EXPLICIT forward guidance action (raise / cut / introduce /
  withdraw) stated in the release — outlook commentary, vision statements, and "we remain confident"
  language are `ReportedResults`. Validated as structured output before persistence (existing rule).
- An unparseable/missing basis degrades the READ (no directional signal, not cached as authoritative), never
  guesses.

### 2. Signal typing

- `DirectionalFilingSignalSource` maps basis → type: `GuidanceChange` (basis `GuidanceChange` or `Both`),
  else `EarningsTrajectory`. One signal per filing, same confidence gate, same cap (spec 160) applied
  before the gate.
- `SignalType` (Domain) gains the `EarningsTrajectory` member. The deterministic spec-57 Neutral earnings
  signal moves to `EarningsTrajectory` (it states trajectory, not guidance) — verify every consumer of the
  old token (extractor tables, review rules, report policy) and update deliberately, not mechanically.

### 3. Identity and epoch consequences — state them, move them ONCE

- **AI-ON pins move** (the analyzer contract is part of the `ai=` descriptor segment). Update
  `ScoringConfigFingerprintTests` pins at every window (30d/60d/120d) with a lineage note, once.
- **The filing-read cache starts a new epoch**: a changed contract must MISS the old cache scope (verify the
  scope hash covers the prompt/contract; if it does not, that is a defect to fix here). Consequence: in-window
  8-Ks are re-read over ~1-2 runs (`MaxFilingsPerRun` backlog behaviour) — cost accepted, note it in the PR.
- **`baseline-earnings-only` is re-keyed, not edited.** Its `SignalTypes: [GuidanceChange]` filter is folded
  into its fingerprint and its hypothesis ("earnings-read signals only") now means BOTH new types. Per the
  spec-141 immutable-by-convention rule it gets a NEW NAME (e.g. `baseline-earnings-only-v2`) declaring
  `SignalTypes: [EarningsTrajectory, GuidanceChange]`; the old name's accrued series is left intact and
  simply stops accruing. `StrategyIdentityGuard` must not trip on any OTHER strategy.
- **Accrued signals are NOT rewritten** (AD-8). Legacy `GuidanceChange` rows keep their type; the efficacy
  join is unaffected (constant type and strength cannot differentiate observations). The spec-167 report
  legend already explains the historical token.

### 4. Strength stays constant — explicitly out of scope

- Constant Strength 8 is untouched. Spec 162 established materiality varies (3 low / 82 moderate / 60 high
  over the labeled corpus) but also that grade reliability is unmeasured — encoding materiality needs its
  own validation pass first. Record, don't build.

## Tests

- Basis mapping: explicit raise/cut/introduce/withdraw fixture bodies → `GuidanceChange`; results-only
  bodies (including outlook/vision boilerplate) → `EarningsTrajectory`; exactly one signal per filing in
  every case.
- Contract guard: the system instruction's basis definition is pinned as a string (mirroring the existing
  `SystemInstruction` guard).
- Cache epoch: a pre-168 cache record is a MISS under the new contract; a post-168 record round-trips.
- Identity: the new baseline's fingerprint differs from the old; no other strategy's stamp moves EXCEPT the
  strategies whose `ai=` segment moved (all AI-ON) — enumerate and pin.
- Report: `EarningsTrajectory` renders via the spec-167 mapping (which becomes an identity mapping for the
  new member); policy corroboration counts treat the two types as distinct axes only if genuinely distinct —
  decide and test explicitly (recommendation: they are ONE axis for the corroboration floor; two types from
  one reader must not self-corroborate).

## Constraints

- One reader invocation per filing (no second AI call for basis).
- Provider isolation (AD-5), structured-output validation before persistence, append-only stores, and the
  advice-language ban all hold.
- No change to cmpscan (spec 160) semantics; the cap applies identically to both types.

## Un-defer gate (ALL must hold before dispatch)

1. The current strategy-comparison window has produced its first real multi-strategy ranking (v9 arms and
   baselines present on the leaderboard), OR the maintainer explicitly waives waiting.
2. Spec 167 is merged (so the report is honest in the interim).
3. Maintainer sign-off recorded in this file (replace this line with the date + decision).

## Acceptance criteria

- [ ] Basis field: closed set, validated, degrade-don't-guess.
- [ ] Type mapping: explicit guidance action ⇒ `GuidanceChange`; otherwise `EarningsTrajectory`; one signal
      per filing (asserted).
- [ ] Deterministic spec-57 signal moved to `EarningsTrajectory`; every consumer of the old token reviewed
      and updated deliberately (list them in the PR).
- [ ] AI-ON pins updated once with lineage note; cache epoch verified; `baseline-earnings-only-v2` added
      under a new name with the widened filter; no accrued data rewritten.
- [ ] Corroboration-floor treatment of the two types decided and tested.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.

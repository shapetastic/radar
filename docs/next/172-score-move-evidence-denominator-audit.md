# Task: Measure whether a thin evidence base amplifies score moves

## Overview

Two consecutive live runs produced a largest-mover whose move rested on **one** directional signal, and the
thinner evidence base moved **further**:

| Run | Company | ΔOpportunity | Linked evidence | Directional signals |
| --- | --- | ---: | ---: | ---: |
| 2026-08-04 | UFPT | +17 (joint largest of 74) | 28 | **1** positive (+1 contradicting negative) |
| 2026-08-05 | IOSP | **+21 (largest of 74)** | **9** (thinnest in the top ten) | **1** positive |

Both were skeptic-reviewed and both came back `THESIS_CHALLENGED`. The IOSP review named the mechanism
directly: under `radar-formula-v8` a single strength-8 / confidence-0.90 signal moves Trajectory hard on a
company with almost no other directional mass **precisely because the denominator is small** — so the score
may be reporting the shape of the evidence set as much as the shape of the business. Neutral
`MediaAttention` links cannot corroborate, because they carry no direction.

**This spec measures whether that is true across the universe. It changes no score.** If the largest movers
systematically have the fewest directional signals, that is a formula property worth its own remediation
spec; if they do not, the two cases were coincidence and this closes the question with a number instead of
an anecdote.

The same structural complaint is already recorded for v9 collector channels in spec 153 ("volume alone
produces score"). This asks the mirror question of v8: does *scarcity* alone produce movement?

## Assignment

Worktree: any
Dependencies: none. Spec 142 (durable history hydration) is the load-bearing prerequisite and is merged.
Estimated time: ~1–2 hours.

## Changes

### 1. A read-only audit under `Radar.Application/Efficacy/`

Reads accrued snapshots through the existing `IScoreSnapshotFileStore` / `IScoreRepositoryFactory` — the
same route spec 150's report and spec 140's comparison use. **No second path to the score files.**

For each company, walk its snapshots in as-of order and emit one observation per **consecutive pair**:

- `Delta` — `OpportunityScore(t) − OpportunityScore(t−1)`, and the same for `TrajectoryScore`
- `LinkCount` — evidence links on the later snapshot
- `DirectionalCount` — links on the later snapshot whose `ContributionReason` is **not** Neutral
- `AsOfDate`, `CompanyId`, `StrategyName`

`DirectionalCount` is the denominator the hypothesis is actually about; `LinkCount` is reported alongside
because it is the number a reader sees in the weekly report and the two must not be conflated.

### 2. The statistic — REUSE, do not reimplement

Use **`RankCorrelation.ComputeRho`** (`Radar.Application/Efficacy/Comparison/RankCorrelation.cs`, added by
spec 169) for Spearman ρ between `|Delta|` and `DirectionalCount`, and again against `LinkCount`. It already
carries the average-rank convention and the named degeneracies (`TooFewObservations`, constant vector,
|ρ| = 1). Reimplementing Spearman in a script or a new class is a reuse-over-copy violation and the
architecture reviewer has flagged exactly this pattern repeatedly.

A negative ρ is the hypothesis: fewer directional signals, larger moves.

**Report a binned distribution as well as ρ**, because a single coefficient can hide the shape that matters:
median and 90th-percentile `|Delta|` grouped by `DirectionalCount` (0, 1, 2, 3, 4+). If the effect is real it
should be visible as a monotone fall across those bins, and the bin table is what makes the finding
actionable rather than merely significant.

### 3. Honesty requirements, non-negotiable

- **Observations are NOT independent** — pooled across companies and dates, same as spec 140. Say so in the
  rendered output, in those words, and treat any interval as dispersion rather than significance.
- **Name every degeneracy rather than emitting NaN**, following `RankCorrelation`'s existing vocabulary.
- **State the confound explicitly**: a company with few directional signals also tends to be smaller and
  less covered, so `DirectionalCount` is partly a proxy for company size. This audit **cannot** separate
  "thin evidence amplifies moves" from "small companies move more". Recording that limitation is part of
  the deliverable — a reader who takes the ρ as causal will draw the wrong remediation.
- **AD-3**: fully deterministic. No sampling, no bootstrap, no `Math.Random`, no wall-clock in the output
  path.
- **AD-14**: price is not an input and must not become one. This measures score mechanics, not efficacy.
  The existing `EfficacyReadOnlyGuardrailTests` type-graph walk must keep passing.

### 4. Wiring — default OFF

`Radar:Efficacy:DenominatorAudit:Enabled`, **default `false`**, inside the already-opt-in `Radar:Efficacy`
gate. Runs in `Worker` alongside the other efficacy artifacts, skipped entirely by a replay run.

Default-off is deliberate: this is a one-shot diagnostic, not a nightly artifact, and the nightly baseline
run is currently unattended. Writes to `data/audits/score-move-denominator.{csv,md}` — a **new** directory,
so no existing efficacy artifact can be overwritten. (Spec 161 found the sibling hazard: an efficacy
artifact being rewritten from a run whose universe was not what the reader assumes.)

## Tests

- Per-pair delta arithmetic, including a company with a single snapshot (contributes no pair, is not an
  error) and a company with a gap in its as-of dates (consecutive **snapshots**, not consecutive calendar
  days — state which and pin it).
- `DirectionalCount` excludes Neutral and counts nothing else; a snapshot of only Neutral links yields 0.
- Degeneracy passthrough: fewer than 4 observations, a constant `DirectionalCount` vector, |ρ| = 1 — each
  produces its named reason, never NaN.
- Binned output: bins are stable and ordered, an empty bin renders as empty rather than being dropped.
- Determinism: two runs over identical input produce byte-identical CSV and markdown.
- Default-off: with no config key present, no file is written and no directory is created.
- The AD-14 guardrail test still passes unchanged.

## Constraints

- **Read-only.** No scoring change, no new fingerprint input, no `_formula.Version` bump, no
  `RuleSetVersion` bump. Nothing under `Scoring/`, `Domain/` or `Pipeline/` is touched. All four spec-148
  pins stand.
- Ranking or promoting anything on the result is out of scope. This produces a number; a human reads it.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.

## Out of scope — recorded, NOT built

- **Any remediation.** If the effect is real, the fix (a corroboration floor, a denominator term, a
  confidence cap on single-signal moves) is a scoring change and earns its own `radar-formula-vN` under
  AD-6 — never an in-place edit to v8, whose stability `ScoringOutputStabilityTests` pins.
- Suppressing or flagging low-evidence movers in the weekly report.
- The same question for v9/v10 channel strategies — related to spec 153 but a different composition, and
  worth its own pass once this establishes the method.

## Acceptance criteria

- [ ] Per-company consecutive-snapshot deltas built through the existing score-store route, no second path.
- [ ] Spearman via `RankCorrelation.ComputeRho` — not reimplemented.
- [ ] ρ reported for `|Delta|` vs `DirectionalCount` AND vs `LinkCount`, with the binned distribution table.
- [ ] Non-independence, every degeneracy, and the size/coverage confound all stated in the rendered output.
- [ ] Deterministic: identical input ⇒ byte-identical artifacts.
- [ ] Default OFF; writes only under `data/audits/`.
- [ ] No scoring change and no pin move; AD-14 guardrail unchanged and passing.
- [ ] Build and full test suite pass.

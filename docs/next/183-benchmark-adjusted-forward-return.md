# Task: Benchmark-adjusted forward returns — close the AD contradiction before the claim boundary

## Overview

The accepted architecture demands it and the running code contradicts it: AD-16's amendment states **"Price
stays validation-only (AD-14) and must be benchmark-adjusted. A raw share return conflates 'this company
improved' with 'the market went up'; under a thesis about re-rating, the market component is pure
contamination"** (docs/architecture-decisions.md:1409) — while `ForwardReturn.TryCompute` computes a raw
share return, consumed by both the spec-140 strategy leaderboard (`StrategyObservationBuilder.cs:138`) and
the spec-179 news-risk evaluator (`NewsRiskEvaluationGenerator.cs:184`), and feeding the spec-155 AD-15
paired comparison.

**The deadline is structural, not preferential.** AD-16's pre-commitment clause makes any change to a
declared outcome variable "an amendment to this AD, recorded with its reason", and the claim families'
first eligible date is 2026-09-29. Amending the outcome BEFORE that boundary is the legitimate window this
clause exists to protect; amending after observations are admitted is the unfalsifiability failure it
names. This spec is that amendment, made in time.

No score, signal, fingerprint, strategy identity or report rank changes: price remains strictly downstream
of scoring (AD-14), and the efficacy read-side guardrail tests continue to hold.

## Assignment

Worktree: any — but do NOT dispatch while spec 182's run is active (shared worktree).
Dependencies: none on 182's content; sequencing only.
Estimated time: ~1 day.

## 1. The benchmark is the equal-weight seeded universe, precommitted — not a config knob

The adjusted outcome is **arithmetic excess over the equal-weight mean forward return of the seeded
universe**, computed over the identical window from the same price store:

```text
excessReturn(c, D, h) = forwardReturn(c, D, h) − mean over m in universe, m ≠ c of forwardReturn(m, D, h)
```

Why this benchmark and not an index ETF:

- **It removes the contamination the AD names, and more of it.** The universe mean strips the market
  component plus the small/mid-cap and pond-composition drift an external large-cap index would leave in.
  The question Radar's ranking claims to answer is "did it pick the right names *within its pond*" — the
  pond's own mean is that question's natural zero.
- **No new collection, no new external dependency, no new failure mode.** Every input already exists in
  `data/prices/`; AD-3 determinism is preserved because the computation is a pure function of the stored
  bars. An SPY/IWM series would add a collector, a symbol-availability failure mode, and a benchmark whose
  composition Radar does not control.
- **Self-exclusion (`m ≠ c`) is deliberate and recorded**: a company is not measured against itself; the
  per-company benchmark difference this creates is deterministic and tiny (1/73), and the honest form of
  "versus peers".

The definition — arithmetic excess, equal-weight, self-excluded, member-window rules below — is **code
constants beside the computation, deliberately NOT configurable** (the Efficacy precedent: "a declared
threshold an operator can tune between runs is not declared at all"). Changing any of it is a new AD
amendment, only legitimate before a claim family admits observations.

## 2. Member-window mechanics — reuse the spec-152 rules, per member, exactly

For each observation (company c, as-of D, horizon h):

- `forwardReturn(c, …)` is the existing `ForwardReturn.TryCompute` result, unchanged — entry strictly after
  D, exit within tolerance, `PartialWindow`/`SingleForwardBar`/price-check exclusions all intact.
- Each universe member m resolves its own entry/exit through the SAME rules (same horizon, same tolerance,
  same `bar.Date > D` admission — the poison-bar guarantee applies to members too). A member that fails its
  own window rules is omitted from that observation's benchmark, deterministically.
- The benchmark requires at least **`MinBenchmarkMembers = 20`** (code constant) resolved members; fewer ⇒
  the observation is excluded with the new named reason `BenchmarkUnavailable` and counted like every other
  exclusion — an outcome that cannot be honestly adjusted is not reported raw instead.
- Every adjusted observation records its benchmark provenance: member count and a deterministic hash of the
  contributing member set, so two rows computed against different ponds are distinguishable.

Implement once, share twice (reuse-over-copy): one `UniverseBenchmark`/`ExcessForwardReturn` helper in
`Radar.Application/Efficacy/Comparison/`, consumed by `StrategyObservationBuilder` AND
`NewsRiskEvaluationGenerator`. `ForwardReturn.TryCompute` itself is untouched — raw per-ticker resolution
stays one primitive; adjustment composes on top.

## 3. What each consumer reports after this change

- **Spec-140 leaderboard**: Spearman ρ, intervals and the hold-out are computed over EXCESS returns. This
  materially changes pooled-across-dates correlations (per-date market moves were common noise across the
  pool). Columns are renamed to say what they now are (`excess-vs-universe`), the benchmark definition and
  member counts render in the artifact preamble, and the CSV schema version bumps — a reader must not
  mistake new numbers for old semantics.
- **Spec-155 / AD-15 paired comparison**: the purged median paired delta is computed over excess returns.
  Recorded plainly: this is an amendment to the AD-15 outcome made BEFORE `PairedFirstEligibleAsOfUtc`
  (2026-09-29) while eligible support is zero — the precommitment is being completed, not moved after
  results.
- **Spec-179 news-risk evaluator**: rows carry BOTH raw and excess forward return (additive fields; the
  excess is the claim-bearing one). The 21-day maximum adverse close move stays RAW and is explicitly
  labelled raw — a drawdown is a descriptive path statistic, not the outcome variable, and
  benchmark-adjusting a running minimum is a different (undeclared) quantity.
- **AD-16 attention-arrival screen**: untouched — its outcome is publisher counts, no price.

## 4. The AD amendment itself

Append a dated amendment to the AD-16 section of `docs/architecture-decisions.md`: outcome variable for
every price-outcome claim family (140 leaderboard, 155/AD-15 paired comparison, 179 evaluator's
claim-bearing column) is the equal-weight-universe excess forward return defined here; reason: the AD
already required benchmark adjustment and the implementation predated it; effective before any claim family
admits observations (boundary 2026-09-29); per the AD's own clause, comparisons across the change are
invalidated — which is why it must land now, while the leaderboard is still honestly reporting "cannot be
ranked" and the paired-support count is zero.

## 5. Out of scope, recorded not built

- An external index/ETF benchmark series (SPY/IWM) as a secondary comparator — possible later; needs its
  own collection decision and would be descriptive-only beside the precommitted primary.
- Any change to `ForwardReturn.TryCompute`, the horizon, the exit tolerance, or spec-152's partial-window
  semantics.
- Sector/size-bucketed benchmarks, factor models, risk-adjustment (Sharpe/vol scaling) — each is a new
  outcome definition requiring its own prospective declaration.
- Any scoring change. Price remains outside `IRadarPipeline`; the type-graph guardrail is untouched.

## Files to inspect

- `docs/architecture-decisions.md` (the AD-16 amendment site, line ~1409 context)
- `src/Radar.Application/Efficacy/Comparison/ForwardReturn.cs` (untouched primitive; read for rules)
- `src/Radar.Application/Efficacy/Comparison/StrategyObservationBuilder.cs`
- `src/Radar.Application/Efficacy/Comparison/StrategyLeaderboardRenderer.cs` (+ CSV renderer)
- the spec-155 paired-comparison observation path
- `src/Radar.Application/NewsRisk/Evaluation/NewsRiskEvaluationGenerator.cs`
- `data/efficacy/strategy-leaderboard.md` / `strategy-paired-comparison.md` (current raw-return artifacts)

## Tests

- Excess arithmetic: fixture with known bars — company return, per-member returns, self-exclusion, and the
  mean verified by hand-computed values.
- Determinism: identical inputs ⇒ byte-identical benchmark hash and excess values; member iteration order
  cannot change the mean (sum ordering pinned or tolerance-asserted).
- Member-window reuse: a member with only at-or-before-D bars contributes nothing (poison-bar test at the
  member level); a member failing tolerance is omitted; below `MinBenchmarkMembers` ⇒ `BenchmarkUnavailable`
  exclusion, counted and rendered.
- Per-date invariance sanity: within one date-block cross-section, company RANKS by excess equal ranks by
  raw return (the benchmark is a common shift) — pinned so the adjustment is demonstrably about pooled and
  absolute claims, not within-date reordering.
- Leaderboard/paired artifacts: renamed columns, benchmark preamble, schema-version bump; golden updates.
- News-risk evaluator: raw and excess both present; max-adverse labelled raw; existing exclusion reasons
  byte-identical.
- Guardrail: the scoring type-graph tests still pass; nothing under `Scoring/`/`Domain/`/`Pipeline/`
  touched.

Do not run tests concurrently with another agent's solution-wide test run. At handoff,
`dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass in one
coordinated session.

## Acceptance criteria

- [ ] Every price-outcome claim path (leaderboard, paired comparison, news-risk claim column) computes
      equal-weight-universe excess returns with self-exclusion and the member-window rules; the raw
      primitive is unchanged.
- [ ] `BenchmarkUnavailable` is a named, counted exclusion; no observation silently falls back to raw.
- [ ] Artifacts say what they measure: renamed columns, benchmark provenance, schema-version bumps.
- [ ] The AD-16 amendment is recorded with its reason, dated before 2026-09-29, while paired support is
      zero.
- [ ] AD-16 attention screen untouched; no scoring/fingerprint/pipeline change; guardrails green.
- [ ] Build and coordinated tests green.

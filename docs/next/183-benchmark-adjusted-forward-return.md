# Task: Benchmark-adjusted forward returns — frozen universe, right claims, no lost support

## Overview

The accepted architecture demands it and the running code contradicts it: AD-16 states **"Price stays
validation-only (AD-14) and must be benchmark-adjusted"** (docs/architecture-decisions.md:1409) while
`ForwardReturn.TryCompute` computes a raw share return, consumed by the spec-140 leaderboard
(`StrategyObservationBuilder.cs:138`), the spec-155/AD-15 paired comparison, and the spec-179 news-risk
evaluator (`NewsRiskEvaluationGenerator.cs:184`).

**The need differs by consumer, and this spec is scoped accordingly (review finding, adopted):**

- **Pooled leaderboard: adjustment genuinely matters.** Pooling observations across dates lets market-wide
  moves contaminate the pooled correlation; demeaning by the contemporaneous universe removes that common
  factor.
- **News-risk evaluation: useful, and descriptive only.** Comparing flagged companies across different
  dates benefits from removing the pond average — but spec 179 declares no threshold or alpha claim, and
  its actual association is RiskScore vs raw maximum adverse move. Both raw and excess forward returns are
  attached as DESCRIPTIVE fields; nothing here is claim-bearing.
- **AD-15 paired comparison: adjustment is mathematically REDUNDANT and must not cost support.** For a date
  with N resolved companies, `excessᵢ = rᵢ − mean(rⱼ, j≠i) = N/(N−1) × (rᵢ − mean(all))` — a strictly
  increasing per-date transformation, so every per-date cross-sectional rank, every per-date ρ and every
  paired delta is IDENTICAL. Therefore the confirmatory outcome is (re)defined as what the statistic
  actually consumes — **the per-date cross-sectional rank of the 21-day adjusted-close forward return** —
  excess values are attached for audit consistency only, and NO benchmark-availability gate applies to the
  paired path: an exclusion there could only discard otherwise-valid support while improving nothing.

Timing, stated precisely (not "before any claim"): the CONFIRMATORY family (AD-15 paired) has zero eligible
support before 2026-09-29, so defining its semantics now is the legitimate window. The DESCRIPTIVE
leaderboard already publishes raw-return results (current default OOS ρ −0.0501); those artifacts are
retained as a distinct raw semantic version and declared incomparable with the new excess series — the old
numbers are not rewritten, renamed or disowned.

No score, signal, fingerprint, strategy identity or report rank changes; price remains strictly downstream
of scoring (AD-14) and the type-graph guardrails hold.

## Assignment

Worktree: any — do NOT dispatch while another run holds the worktree.
Dependencies: sequencing after spec 182's merge only.
Estimated time: ~1 day.

## 1. The benchmark universe is FROZEN and VERSIONED — never today's seed list

The watch universe has changed repeatedly (8 → 19 → 29 → 43 → 66 → 74). Benchmarking historical dates
against the CURRENT `companies.json` would retroactively insert later-selected companies and make reruns
change whenever the seed changes — mutable-universe leakage. Therefore:

- **`benchmark-universe-v1` is a committed, frozen artifact** (`data/efficacy/benchmark-universe-v1.json`):
  the exact company ids at freeze time, plus a content hash. It is used for ALL dates — one fixed pond,
  applied uniformly, so reruns are byte-stable regardless of later seed edits. Members whose price history
  starts after an early date simply fail their member-window rules on that date and are excluded per
  observation, recorded (that is honest per-date coverage, not universe mutation).
- **Future universe expansions do NOT touch v1.** A future `benchmark-universe-v2` is declared
  prospectively, is a new cohort dimension, and never restates v1-era results.
- **The benchmark is computed CENTRALLY, once per (universeVersion, D, horizon)** — independent of any
  strategy, shared by every consumer — so two arms can never derive different outcomes from different
  member sets. One `UniverseBenchmark` computation in `Radar.Application/Efficacy/Comparison/`, reused by
  the leaderboard builder and the news-risk evaluator (reuse-over-copy).

The excess definition itself: **arithmetic excess over the equal-weight mean forward return of the other
resolved v1 members** (`m ≠ c`, self-excluded, recorded), members resolving entry/exit through the SAME
spec-152 rules (same horizon, tolerance, `bar.Date > D` admission — poison-bar guarantee applies at the
member level). Definition and thresholds are code constants beside the computation, deliberately NOT
configurable.

## 2. Coverage rule — proportion plus floor, with full provenance

`MinBenchmarkMembers = 20` alone was unjustified (a 20-member pond is a radically different pond than a
74-member one, and self-exclusion stops being a ~1/73 effect). Replaced by a predeclared two-part rule
(code constants): a benchmark is usable when **resolved members ≥ 90% of the eligible frozen universe AND
≥ 40 absolute**. Below either bound, the POOLED/descriptive observation is excluded with the named, counted
reason `BenchmarkUnavailable` (never a silent fallback to raw) — and, per the Overview, this gate NEVER
applies to the paired path.

Sampled against the live store (2026-06-30, 07-25, 08-04): 74/74 members resolve, so the strong rule is
feasible, and tripping it signals a real data problem rather than routine attrition.

Every adjusted observation records: universe version + hash, eligible member count, resolved count,
coverage percentage, and every excluded member with its reason.

## 3. What each consumer reports

- **Spec-140 leaderboard**: pooled Spearman ρ, intervals and hold-out over EXCESS returns. Columns renamed
  (`excess-vs-universe-v1`), benchmark provenance in the artifact preamble, CSV schema version bumped. The
  pre-change raw artifacts are preserved as the raw semantic version; the artifact states the two series
  are not comparable.
- **Spec-155 / AD-15 paired comparison**: confirmatory outcome defined as the per-date cross-sectional rank
  of the 21-day adjusted-close forward return (invariance stated in the AD amendment); excess values
  attached for audit; no new gate; support counts unchanged by construction (asserted).
- **Spec-179 news-risk evaluator**: rows carry raw AND excess forward return, both labelled DESCRIPTIVE;
  the RiskScore-vs-max-adverse association keeps its raw basis and its raw label; no claim language
  anywhere. A future outcome claim must predeclare predictor, outcome, threshold and boundary in its own
  spec.
- **AD-16 attention-arrival screen**: untouched (publisher counts; no price).

## 4. The AD amendments

Append dated amendments to BOTH decision families in `docs/architecture-decisions.md`:

- **AD-16**: the benchmark-adjustment requirement is satisfied by the equal-weight frozen-universe excess
  defined here for pooled/descriptive price outcomes; universe version v1 named with its hash; reason
  recorded (implementation predated the requirement).
- **AD-15**: the confirmatory outcome is the per-date cross-sectional rank of the 21-day adjusted-close
  forward return — stating the invariance explicitly (per-date statistics are unchanged by any common
  per-date benchmark shift, so the paired result does not materially change and no benchmark gate applies)
  — effective before `PairedFirstEligibleAsOfUtc` (2026-09-29) while eligible support is zero.

## 5. Out of scope, recorded not built

- External index/ETF benchmark series (SPY/IWM) — a possible later descriptive comparator with its own
  collection decision.
- Sector/size-bucketed benchmarks, factor models, vol scaling — each a new outcome definition requiring
  prospective declaration.
- Any change to `ForwardReturn.TryCompute`, the horizon, tolerance or spec-152 semantics.
- Any scoring change; price remains outside `IRadarPipeline`.

## Files to inspect

- `docs/architecture-decisions.md` (AD-15 and AD-16 amendment sites)
- `data/companies.json` (source for the frozen v1 snapshot — read once at freeze, never live)
- `src/Radar.Application/Efficacy/Comparison/ForwardReturn.cs` (untouched primitive)
- `src/Radar.Application/Efficacy/Comparison/StrategyObservationBuilder.cs`
- `src/Radar.Application/Efficacy/Comparison/StrategyLeaderboardRenderer.cs` (+ CSV renderer)
- the spec-155 paired-comparison observation path (must be demonstrably gate-free and support-identical)
- `src/Radar.Application/NewsRisk/Evaluation/NewsRiskEvaluationGenerator.cs`
- `data/efficacy/strategy-leaderboard.md` (current raw artifact — preserved, versioned)

## Tests

- Excess arithmetic: hand-computed fixture (company return, per-member returns, self-exclusion, mean).
- **Universe stability**: adding a company to `companies.json` changes NO benchmark value, hash or excess
  return; reruns over identical stores are byte-deterministic.
- Central computation: two strategies' observations at the same (D, horizon) consume the identical
  benchmark value and provenance.
- Coverage rule: 89%-resolved and 39-member fixtures both yield `BenchmarkUnavailable` with full
  provenance; the paired path processes the same fixtures WITHOUT exclusion and with support counts
  byte-identical to pre-183.
- Invariance: within one date, ranks by excess equal ranks by raw (pinned); the paired statistic's numeric
  outputs are byte-identical before/after this change on a shared fixture.
- Member-window reuse: poison at-or-before-D bars at the member level; tolerance failures omit the member,
  recorded.
- Artifacts: renamed columns, provenance preamble, schema bumps; old raw artifact preserved and marked;
  news-risk rows carry both returns labelled descriptive; max-adverse labelled raw.
- Guardrails: scoring type-graph tests untouched and green.

Do not run tests concurrently with another agent's solution-wide test run. At handoff,
`dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass in one
coordinated session.

## Acceptance criteria

- [ ] `benchmark-universe-v1` is frozen, committed, hashed, applied to all dates, and immune to seed edits;
      benchmark values are computed once per (universeVersion, D, horizon) and shared by all consumers.
- [ ] Pooled/descriptive outputs use excess returns with the proportion+floor coverage rule and full
      provenance; `BenchmarkUnavailable` is named and counted, never a silent raw fallback.
- [ ] The AD-15 confirmatory outcome is the per-date cross-sectional return rank; the paired path has NO
      benchmark gate and byte-identical support; the invariance is stated in the AD amendment.
- [ ] News-risk forward returns (raw and excess) are labelled descriptive; no claim language introduced.
- [ ] Old raw leaderboard artifacts are preserved as a distinct semantic version and declared incomparable.
- [ ] Both AD amendments recorded, dated before 2026-09-29 while paired support is zero; attention screen
      untouched; no scoring/fingerprint change; build and coordinated tests green.

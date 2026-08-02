# Task: Compare strategies pairwise on purged, date-blocked observations

> **AD-15's original decision rule is not a test of difference, and daily date blocks alone do not repair it.**
>
> The shipped rule compares each strategy's marginal Spearman rho and treats the spread between baselines as
> if it were uncertainty on the difference. It is not. The strategies can have different support, and their
> estimates are correlated. Further, adjacent daily blocks reuse most of the same 21-day forward path, so a
> closed-form interval or sign test over every daily delta would still be anti-conservative.

This slice replaces that rule with a deterministic comparison on common support and a predeclared purge that
prevents mechanical forward-window overlap. It is intentionally slower to mature than the daily leaderboard.
At the current horizon, the first six usable blocks plus their outcomes require about four months of forward
accrual. That is the cost of making a claim the current data can support.

## Why this matters

The 2026-07-28 backtest ranked five strategies at rho -0.0849 / -0.0969 / -0.0999 / -0.1000 / -0.1009.
Under the original rule, a strategy at -0.06 could appear to clear the baseline-spread threshold even though
no uncertainty on the paired difference had been calculated.

The strategies also did not share support: `filings-led-v2` had zero in-sample observations while `default`
had 182. Comparing marginal correlations over different (company, date) sets compares different questions,
not two answers to one question.

## Design

### 1. Pair on common support, then form one delta per date

For a descriptive pair of strategies A and B, intersect their admitted observations by
`(CompanyId, AsOfInstant)`. Attach the same already-computed outcome to both scores; an observation may enter
only when both strategies and the common outcome are defined.

An AD-15 claim is stronger: construct one **joint intersection** across the predeclared primary composite and
every predeclared baseline. All primary-versus-baseline deltas must then use the same companies, dates and
outcomes. Pairwise intersections may still be rendered as diagnostics, but they cannot support “beats every
baseline” because each comparison could otherwise answer over a different period or company set.

For every candidate as-of date `d` in the joint intersection:

1. take the companies present for the primary and every baseline on `d`;
2. require the configured minimum number of companies;
3. compute each strategy's cross-sectional Spearman rho against the same outcome ranks; and
4. for each baseline B record `delta_B(d) = rho_primary(d) - rho_B(d)`.

The result must disclose every strategy's marginal support, pairwise-intersection support, joint support,
candidate-date count, every dropped date and its reason, and the admitted block count. A materially smaller
intersection is a result, not a log message.

Date blocking removes the contemporaneous cross-company nuisance: a market-wide move on `d` affects every
company in that block. It does **not** by itself make adjacent dates independent.

### 2. A claim needs a fixed forward boundary, not a moving 70/30 split

The existing global 70/30 date split can remain on the marginal price leaderboard as a descriptive backtest.
It is not a claim boundary: the cutoff moves whenever another date accrues, causing yesterday's holdout dates
to migrate into training.

The paired claim path must instead receive an immutable `FirstEligibleAsOf` recorded before its outcomes
exist. Dates before it may be rendered as development data but never enter the claim interval. A missing
boundary yields `NoPrecommittedEvaluationBoundary` and makes the result exploratory. For AD-16 the evaluator
must take the boundary from the accepted decision; neither spec 155 nor an implementation may derive a more
favourable cutoff from observed deltas.

### 3. Purge overlapping outcome windows before inference

Inference uses a deterministic subset of candidate dates at or after `FirstEligibleAsOf`. Sort dates
ascending and greedily admit the earliest candidate whose nominal outcome interval
`(d, d + ForwardHorizonDays]` does not overlap the last admitted interval. With the current 21-calendar-day
horizon, admitted dates are therefore at least 21 calendar days apart. A date skipped by this rule is counted
as `OverlappingOutcomeWindow`, not silently discarded.

The price return builder never selects an exit after `d + ForwardHorizonDays`; its tolerance permits an
earlier nearby trading bar, not a later one. Consequently the nominal purge is conservative across weekends
and market closures. Preserve actual entry and exit dates in price observations and prove that no admitted
block's observed price interval overlaps the next admitted block. An outcome without price bars supplies its
own exact interval endpoints to the shared purge helper.

The earliest eligible candidate wins deterministically; there is no search over weekday, phase or offset for
the most favourable result. Changing the horizon changes the purge distance automatically.

This removes the known mechanical overlap. It does not prove that macro regimes or company effects 21 days
apart are independent. Render that limitation beside the interval.

### 4. Use an exact, deterministic interval for each paired median

For each baseline, the estimand is the median of its admitted `delta_B(d)` blocks. Under the predeclared
model that the purged blocks are independent draws from a stable distribution, sort the `n` deltas and
report the exact two-sided 95% order-statistic interval for the population median:

- choose the largest integer `k >= 1` for which
  `1 - 2 * BinomialCdf(k - 1; n, 0.5) >= 0.95`; and
- return `[delta_(k), delta_(n-k+1)]`, using one-based order statistics.

This is deterministic, assumes no parametric shape, and makes no difference expressible. It is not
assumption-free: purging removes the known overlap but cannot prove independence or stationarity across
market regimes. State that next to every interval. Ties make the order-statistic interval conservative and
remain data. With fewer blocks than can support a finite 95% interval (six at the current confidence level),
return `InsufficientPurgedBlocks`; do not weaken the confidence level or publish a NaN. Report the exact
two-sided sign-test p-value as a diagnostic only; it must use the same purged blocks, omit zero differences
only from that diagnostic's effective N, and never substitute for the interval gate.

Name every other degeneracy: `EmptyIntersection`, `TooFewCompanies`, `ConstantPrimary`,
`ConstantBaseline`, `ConstantOutcome`, `NoEligibleBlocks`, and
`NoPrecommittedEvaluationBoundary`. Reuse the existing machine-readable drop-reason pattern rather than
encoding failure in logs.

Keep the interval implementation in a small outcome-agnostic statistics helper so the AD-16 attention
evaluator can reuse it without importing the price harness.

### 5. Amend AD-15 narrowly

Amend AD-15 in place. The superseded “more than the spread between baselines” wording was not a test of
difference and licenses no carried-over claim.

A **predeclared primary composite** may be described as adding value only when, against every predeclared
baseline on the joint out-of-sample support:

- the purged median paired difference is positive;
- the exact 95% interval's lower bound is strictly greater than zero; and
- the boundary, support, block-count and strategy-selection disclosures are present.

Requiring the primary to clear every fixed baseline is an intersection-union claim; it does not require a
Bonferroni correction merely because there are several fixed baselines. Choosing the best of several
composite arms after seeing their results is different. Only the arm named primary before its outcomes exist
may use this gate. Other arms remain exploratory until a separately accepted multiplicity rule exists.

For AD-16, this machinery is confirmatory only after its already-precommitted descriptive screen has been
calculated. It must not change AD-16's outcome, horizon, comparator, cohort, eligibility rule or failure rule.
Its confirmatory baseline family is `baseline-attention-persistence` plus the three fixed configured
`baseline-*` scoring arms, all ranked against the AD-16 publisher-count outcome on the same joint support.
The secondary `AttentionScore` and matched v10 control remain diagnostics because AD-16 explicitly does not
screen on the former and the latter isolates formula behaviour rather than representing a dumb baseline.

### 6. Retain the marginal leaderboard

Keep the existing per-strategy marginal rho. It answers whether a strategy tracked its outcome at all, which
is different from whether it beat a comparator. Label it descriptive. The paired, purged comparison is the
only result that can support the amended AD-15 claim.

## Files (verify against the tree before implementation)

- `src/Radar.Application/Efficacy/Comparison/StrategyComparisonHarness.cs`
- `src/Radar.Application/Efficacy/Comparison/ForwardReturn.cs`
- `src/Radar.Application/Efficacy/Comparison/RankCorrelation.cs`
- `src/Radar.Application/Efficacy/Comparison/StrategyComparisonOptions.cs`
- `src/Radar.Application/Efficacy/Comparison/StrategyLeaderboard.cs`
- `src/Radar.Application/Efficacy/Comparison/StrategyLeaderboardRenderer.cs`
- the CSV renderer/artifact writer and their tests
- `docs/architecture-decisions.md`

## Constraints

- **No look-ahead regression.** The entry rule `bar.Date > asOf` and spec 152's `PartialWindow` rule are
  untouched; this slice changes only how admitted observations are compared.
- **Deterministic: no bootstrap, sampling or offset search** (AD-3).
- **No scoring change, fingerprint input or pin move.** Read side only (AD-14); price remains validation-only.
- Do not pool companies across dates for the paired inference.
- Do not call daily date blocks independent; only the purged subset enters the interval.
- No advice vocabulary in rendered output (AD-9).

## Out of scope

- Changing either outcome variable.
- Automatically promoting or retiring a strategy; Radar reports and a human decides (spec 140).
- A general multiple-comparison procedure for selecting among several composite arms.
- Modelling residual dependence after mechanical forward-window overlap has been purged.

## Acceptance criteria

- [ ] A claim family uses one joint intersection of the primary and every baseline; each marginal, pairwise
      and joint support is a result field.
- [ ] Per-date rhos use exactly the same companies and outcome, and every dropped date has a stable reason.
- [ ] A missing immutable `FirstEligibleAsOf` prevents a claim; the moving 70/30 split stays descriptive.
- [ ] Inference admits dates greedily in ascending order with non-overlapping nominal and observed outcome
      intervals; skipped dates are counted as `OverlappingOutcomeWindow`.
- [ ] The headline for each baseline is the purged median paired difference with its exact two-sided 95%
      order-statistic interval and purged-block count.
- [ ] Fewer than six admitted blocks at 95% yields `InsufficientPurgedBlocks`; confidence is not relaxed.
- [ ] The sign-test diagnostic uses only the same purged blocks and handles zero deltas explicitly.
- [ ] Renderers disclose that purging removes known window overlap but not all serial or regime dependence.
- [ ] AD-15 is amended in place and permits a claim only for a predeclared primary clearing every fixed
      baseline on joint support; no prior baseline-spread result carries over.
- [ ] Marginal per-strategy rho remains present and distinctly labelled descriptive.
- [ ] A fixture where the marginal-rho gap exceeds baseline spread but the exact paired interval includes
      zero does not qualify.
- [ ] A fixture with dense daily dates proves that changing unadmitted overlapping dates cannot change the
      interval, while changing an admitted date can.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release` are green.

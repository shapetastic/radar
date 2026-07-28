# Task: Compare strategies PAIRWISE and date-blocked — the current rule does not establish lift

> **AD-15's decision rule is not a test of difference, and it can license a confident claim from noise.**
> The rule as shipped by spec 154 reads:
>
> > A composite strategy may only be described as adding value if it beats **every** baseline
> > **out-of-sample**, on an honest N, by more than the spread between the baselines themselves.
>
> "Beats … by more than the spread" compares each strategy's **marginal** Spearman ρ, each computed over
> observations that `StrategyLeaderboardRenderer.cs:95` itself admits are "pooled across companies and dates
> and are therefore not independent, so the interval is optimistically narrow — treat it as dispersion, not
> significance."
>
> Comparing two such numbers and declaring the gap meaningful is exactly the inference that caveat forbids.
> A gap between two marginal ρ values carries **no** uncertainty estimate of its own, and the "spread between
> the baselines" is a heuristic stand-in for one — it is not a standard error, it has no coverage guarantee,
> and it shrinks when the baselines happen to agree, which is precisely when it should not.

## Why this matters now rather than later

The 2026-07-28 backtest ranked five strategies at ρ −0.0849 / −0.0969 / −0.0999 / −0.1000 / −0.1009. Under
the shipped rule a strategy landing at −0.06 would "beat every baseline by more than the spread (0.016)" and
could be described as adding value. Nothing in the current pipeline would object — and the five strategies
share nearly all their observations, so the differences are between highly correlated quantities where a
pooled marginal interval is at its most misleading.

The strategies also **do not share support**: `filings-led-v2` scored 0 in-sample observations on the live
leaderboard while `default` scored 182. Comparing ρ over different (company, date) sets is comparing
different questions, not two answers to one.

## Design

### 1. Pair on the intersection, block by date

For a pair of strategies A and B, restrict to the (company, as-of date) observations present in **both**,
with the same forward return attached to each — the return depends only on the company and the date, never on
the strategy, so a paired observation differs **only** in the score. An intersection that is materially
smaller than either strategy's own support is itself a finding and must be reported, not silently used.

Then **block by as-of date**: within each date d, compute each strategy's rank statistic over the companies
scored that date, and form the per-date difference `δ(d) = stat_A(d) − stat_B(d)`. The `δ` series is the
paired sample the comparison is actually about.

Blocking by date removes the largest shared nuisance factor — a market-wide move on date d lifts every
company's forward return at once, which is the dominant source of the correlation between observations.
**State plainly what it does NOT remove:** overlapping forward windows mean adjacent dates still share price
path, so the `δ` series is not fully independent either. Say so in the rendered output, as spec 152 said what
its tolerance did and did not cover.

### 2. Report an interval on the DIFFERENCE, and make "no difference" expressible

The headline must be `δ̄` (mean or median paired difference) **with an interval**, plus the number of blocks
it rests on. Determinism is mandatory (AD-3) so a bootstrap is out, exactly as in spec 140 — use a
closed-form paired interval, and where the distributional assumption is uncomfortable report a sign test on
the `δ` series alongside it, which assumes almost nothing.

**Every degeneracy gets a name, never a NaN:** too few blocks, an empty intersection, a constant `δ`, and a
block with too few companies to rank. Spec 140 already established this pattern and its vocabulary
(`DroppedStrategies` with a machine-readable reason) — reuse it rather than inventing a second one.

### 3. Amend AD-15 to require it

The rule becomes, in substance: *a composite may be described as adding value only if, against **every**
baseline, the paired date-blocked difference favours it and the interval on that difference excludes zero.*
Record the amendment in `docs/architecture-decisions.md` under AD-15 itself — amend, do not append a
contradicting AD-17 — and state that the superseded "more than the spread between the baselines" formulation
was **not a test of difference**, so no claim may be carried over from it.

### 4. Do not delete the marginal ρ

The existing per-strategy ρ stays: it answers "did this strategy track price at all", which is a different
and still-useful question from "did it beat that one". The leaderboard should carry both, distinctly
labelled, with the paired comparison identified as **the** basis for an AD-15 claim.

## Files (verify against the tree before planning)

`StrategyComparisonHarness.cs` (`BuildObservations`, the per-strategy loop), `RankCorrelation.cs` (the
Fisher-z interval lives here; the paired interval belongs beside it), `StrategyLeaderboard.cs` (result
fields), `StrategyLeaderboardRenderer.cs` + the CSV renderer, `StrategyComparisonOptions.cs` (any new
minimum-blocks threshold), `docs/architecture-decisions.md`, and their tests.

## Constraints

- **No look-ahead regression.** The entry rule `bar.Date > asOf` and spec 152's `PartialWindow` rule are
  untouched; this slice changes only how already-admitted observations are *compared*.
- **Deterministic — no bootstrap, no sampling** (AD-3). Two runs over identical data must agree exactly.
- **No scoring change, no fingerprint input, no pin move.** Read side only (AD-14); price stays
  validation-only.
- **Nothing may be ranked on an intersection it does not disclose.** The block count and the intersection
  size are result *fields*, like spec 140's `StrategiesCompared`/`DroppedStrategies`, not log lines.
- No advice vocabulary in any rendered output (AD-9).

## Out of scope (record, do not build)

- **Changing the outcome variable** — benchmark adjustment and the attention-arrival measure are their own
  specs, downstream of AD-16.
- **Multiple-comparison correction across many strategies.** Real, and it interacts with this; but decide it
  once there is more than one honest comparison to correct.
- **Auto-promoting or auto-retiring a strategy** on the result. Radar ranks; a human decides (spec 140).
- Non-overlapping observation selection (one per company-week) — still deferred from spec 152.

## Acceptance criteria

- [ ] A strategy pair is compared on the **intersection** of their (company, as-of date) support, with the
      intersection size and per-strategy support reported.
- [ ] The comparison is **blocked by as-of date**, and the headline is a paired difference with a
      deterministic interval and its block count.
- [ ] Every degeneracy is named and counted, never NaN; a comparison that cannot be made says so.
- [ ] The rendered output states what date-blocking does **not** remove (overlapping forward windows).
- [ ] AD-15 is amended in place to require the paired test, recording that the previous formulation was not
      a test of difference.
- [ ] The marginal per-strategy ρ is retained and distinctly labelled.
- [ ] A fixture proves the point: two strategies whose marginal ρ gap exceeds the baseline spread but whose
      paired difference interval **includes zero** must NOT qualify under the amended rule.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.

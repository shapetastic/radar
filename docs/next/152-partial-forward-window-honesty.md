# Task: A partial forward window must not be reported as a full-horizon return

> **Every number the spec-140 leaderboard has printed is mislabelled.** `ForwardReturn.TryCompute`
> (`src/Radar.Application/Efficacy/Comparison/ForwardReturn.cs:59`) selects the **latest bar within**
> `(asOf, asOf + horizonDays]` as the exit, and rejects an observation only when there are **zero** bars
> (`NoForwardBar`) or **one** (`SingleForwardBar`). It never checks that the exit bar is anywhere near the
> horizon end.
>
> So when only four days of price exist inside a 21-day window, it computes a **four-day return and reports
> it as a 21-day forward return.** The `observationsWithoutForwardPrice` column catches only the
> fully-missing case; partial windows pass through silently and are pooled with complete ones.

## How badly this bites right now — measured

Latest price bar across all tickers: **2026-07-27**. The replay's as-of series runs **2026-06-30 → 2026-07-27**.

- Maximum testable horizon for the **earliest** as-of date: **27 days**.
- As-of dates with a complete 21-day forward window: **7** (06-30 → 07-06).
- As-of dates with a complete 63-day window: **0**.

The `backtest-infer-on` leaderboard reported its headline over **8 out-of-sample as-of dates (07-16 → 07-23)**
— **none of which has a full 21-day window.** Those are 4-to-11-day reaction measures presented as a 21-day
statistic, and every strategy's rho was computed from them. The negative signs may be real, but the label is
not.

This is the same class of defect as the silent report truncation spec 125 fixed: the output looked complete,
so nobody asked.

## Design

### 1. A partial window is its own outcome, not a silent success

Add a distinct `ForwardReturnUnavailableReason` — e.g. `PartialWindow` — returned when a forward pair exists
but the exit bar is too far short of the horizon end. Count and **render it as its own column** beside
`observationsWithoutForwardPrice`, so "we had no price" and "we had some price but not the horizon you asked
for" are never conflated.

### 2. Tolerance must account for closed markets, and be explicit

The exit bar can legitimately fall a few days short of `asOf + horizon` — weekends, holidays, a long
Thanksgiving/Easter gap. So the rule is not `exit.Date == exitBound`.

- Require `exit.Date >= asOf.AddDays(horizonDays) - tolerance`, with the tolerance **configurable** and a
  documented default (a value covering a long weekend plus a holiday is the obvious starting point —
  **justify whatever you pick against the actual bar spacing in `data/prices/`, do not guess**).
- **Verify against real data how often a legitimate full window still falls short**, and report it. If a
  sensible tolerance discards a meaningful share of genuinely-complete observations, the rule is wrong.

### 3. Expect this to empty the leaderboard, and say so

With today's data almost every observation becomes `PartialWindow`, so the leaderboard will report
**"No strategy could be ranked"** — for 21 days, until roughly 2026-08-17.

**That is the correct output and the deliverable, not a regression.** The hand-back must state it plainly so
the change is not mistaken for a break. A harness that honestly says "not yet" is worth more than one that
prints a confident number computed from four days of price.

### 4. Re-render and report the delta

After the change, re-render against the existing `backtest-infer-on` replay (6,020 snapshots already on
disk — **do not re-replay**) and report: how many observations were reclassified, and what the previously
published rhos were actually measuring. That is the evidence that the fix matters.

## Files (verify against the tree before planning)

`ForwardReturn.cs` / `ForwardReturnResult` / `ForwardReturnUnavailableReason`, `StrategyComparisonHarness`
(`BuildObservations`, the `WithoutForwardPrice` tally), `StrategyLeaderboardRenderer` + the CSV renderer,
`StrategyComparisonOptions` (the new tolerance), and their tests.

## Constraints

- **No look-ahead regression.** `bar.Date > asOf` remains the single admission filter — this slice tightens
  the *exit* rule only and must not touch the entry rule.
- **No scoring change, no fingerprint input, no pin move.** This is the read/measurement side (AD-14).
- Price stays validation-only and strictly downstream.
- Renderers must not silently change column meaning — if `observationsWithoutForwardPrice` keeps its name,
  its definition must be unchanged, with partials counted separately.

## Out of scope (record, do not build)

- **Changing the horizon or the outcome variable** (fundamental follow-through, sector-adjusted returns) —
  a bigger question, and it should be decided on honest numbers rather than alongside this fix.
- **Non-overlapping observation selection** (one per company-week / per material event) — real, and its own
  slice; this one is about mislabelling, not independence.
- **Acquiring longer price history.**

## Acceptance criteria

- [ ] An observation whose exit bar falls short of the horizon (beyond tolerance) is classified
      `PartialWindow`, excluded from the correlation, and counted separately from `NoForwardBar`.
- [ ] The tolerance is configurable with a documented, data-justified default.
- [ ] Both counts are rendered in the markdown and CSV leaderboards, distinctly labelled.
- [ ] The entry rule (`bar.Date > asOf`) is unchanged — asserted.
- [ ] Re-rendered against the existing replay, with the reclassification counts and the honest verdict
      ("No strategy could be ranked" if that is the truth) reported in the hand-back.
- [ ] No pin move; no scoring source touched.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.

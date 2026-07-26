# Task: Strategy-vs-price comparison — which strategy is the better signal

> **The payoff of the whole strategy-decoupling arc, deliberately sequenced last.** Specs 136–139 built the
> machinery: point-in-time honesty, plural strategies, per-strategy signal-type filters, and historical
> replay. This slice extends the spec-101/108 efficacy read to score **each strategy's** time-series against
> subsequent price movement and rank them — *with hold-out discipline built in from the start*, not bolted on.
>
> **Depends on 137 (strategies), 138 (signal-type filters), and 139 (replay) merged.** Without 139 there is no
> historical series to evaluate; without 138 the strategies are not meaningfully distinct. Reconcile all type
> and path names against what shipped.

## The one rule that governs this entire slice: AD-14

**Price is validation-only. Price is never a scoring input.** Every strategy's score is computed from signals
alone (136/137/139); this slice reads price **afterwards, on the side**, purely to *judge* the already-fixed
scores. If price ever flows back into a score, the comparison is circular and the arc has failed. The design
must keep the price read strictly downstream of scoring, in a separate module, with no path from price back
into any `ScoringEngine` input. Assert this boundary.

## The second rule: multiple-comparisons discipline, from the first commit

Running N strategies against price and picking the winner is **exactly** the setup that manufactures
false-positive "signals" by overfitting. This slice must therefore bake in, not defer:

1. **A hold-out split.** Fit/rank on an in-sample window; report the winner's performance on a **held-out
   out-of-sample window it was not selected on.** The headline number is the out-of-sample one.
2. **Honest N.** Report **how many strategies were compared**. A winner chosen from 20 strategies needs a
   far stronger effect than one chosen from 2 — surface N so the reader can discount accordingly.
3. **No silent strategy pruning.** If strategies are dropped from the ranking (insufficient data, degenerate
   series), `log`/report which and why. A quietly-dropped loser inflates the apparent skill of the rest.
4. **Effect + dispersion, never a point estimate alone.** Report the metric with its spread/confidence, not
   a single seductive number.

These are **design constraints, not a results section** — build the split and the N-reporting into the
comparison harness itself so a caller cannot accidentally produce an overfit headline.

## Design

### 1. Inputs

- Per-strategy historical score series from spec 139's replay output (as-of date → per-company score),
  plus the live forward series for the primary (spec 101/108 location).
- A price series per company. **Reconcile with how price already enters the codebase** — the spec-101/108
  efficacy read already correlates score vs price, so a price source/abstraction likely exists; **reuse it,
  do not add a second price path** (CLAUDE.md reuse-over-copy). If none exists, that is its own slice — stop
  and flag rather than smuggling a price collector in here.

### 2. The comparison metric

Extend the existing spec-101/108 efficacy computation to run **per strategy** rather than once:

- For each strategy, for each as-of date, relate the score (or score *change* — match whatever 101/108
  already uses; do not invent a new definition here) to **subsequent** price movement over a defined forward
  horizon. The forward horizon enforces causality: score at D vs price change over (D, D+h].
- Aggregate into a per-strategy efficacy metric consistent with what 101/108 already reports, so the numbers
  are comparable to the existing single-series read.
- **Never** use price at or before D in the metric for score at D (that is lookahead — the mirror image of
  136's hindsight leak, on the price side).

### 3. Ranking output

- A per-strategy leaderboard: strategy name, N-compared, in-sample metric, **out-of-sample metric
  (headline)**, dispersion, and coverage (how many company-dates it actually scored — a strategy that scored
  3 companies is not comparable to one that scored 43; surface it).
- Rendered where the existing efficacy read surfaces (reconcile with 101/108's output — likely the weekly
  report or a data file). **Output language hard rule still applies:** allowed labels only; this is
  "which strategy tracked price better", **not** "buy the winner's picks". No "buy/sell/safe bet".

### 4. Keep it validation-only and reproducible

- Pure function of (replay series, price series, config). No `Date.now()`/random. Same inputs → same
  leaderboard.
- The price read and the comparison live in their **own** module downstream of scoring; nothing here is
  referenced by `Radar.Application/Scoring`'s engine inputs.

## Assignment

Worktree: any. Files: extend the spec-101/108 efficacy read to iterate strategies; a new comparison/ranking
module (its own type, downstream of scoring); reuse the existing price source; the reporting/output surface;
and tests.
Dependencies: **137 + 138 + 139 merged.** Reuse the existing price path from 101/108 — do **not** add a new
one; if it doesn't exist, flag it as a blocker.
Estimated time: ~4–5 h.

## Tests

- **AD-14 boundary:** a static/architecture assertion that no price value reaches any `ScoringEngine` input;
  the comparison module depends on scoring output, never the reverse.
- **No price lookahead:** score at D is only ever related to price strictly after D; a test with a
  constructed series fails if price at ≤ D is used.
- **Hold-out is real:** the reported headline metric is computed on out-of-sample dates the ranking did not
  select on; a test proves in-sample and out-of-sample windows do not overlap.
- **N and pruning are reported:** with K strategies in and J dropped for thin data, the output states K and
  names the J — asserted, not just present.
- **Per-strategy independence:** two strategies with deliberately different score series produce different
  efficacy numbers; the primary's number matches the existing single-series 101/108 read (no regression).
- **Determinism:** same replay + price inputs → identical leaderboard.
- **Output language:** the rendered leaderboard contains none of the forbidden terms and only allowed labels.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

## Constraints

- **AD-14 absolute:** price is validation-only, read strictly downstream of scoring, never an engine input.
- **Reuse the existing price path** (101/108) — no second price source.
- **Hold-out + honest-N discipline is built into the harness**, not left to the reader.
- **Output-language hard rule:** allowed labels only; comparison ≠ recommendation.
- **No fingerprint move, no scoring change.** This slice only *reads* scores and price; it computes nothing
  that feeds back into a snapshot.
- **Layering:** no `IConfiguration` in `Radar.Application`; price source and horizons arrive resolved.

## Out of scope (record, do not build)

- **Auto-promoting the winning strategy to primary.** A human decides (philosophy: "AI assists. Humans
  decide."). This slice ranks; it does not act.
- **A new price collector** — if none exists, that is a separate slice; flag it, do not build it here.
- **Live/streaming comparison.** Batch over replay output is enough; a company universe of 43 does not need
  incremental.
- **Portfolio/return simulation, position sizing, or anything resembling a backtested trading P&L.** Radar is
  a research assistant, not a trading bot — efficacy = "did the score track subsequent price", nothing more.

## Acceptance criteria

- [ ] The efficacy read runs **per strategy** and emits a ranked leaderboard reusing the 101/108 metric
      definition (primary's number unchanged vs today).
- [ ] Price is read strictly downstream of scoring via the **existing** price path; no price value can reach
      a scoring input — asserted architecturally.
- [ ] No price lookahead: score at D judged only against price after D.
- [ ] Hold-out split is enforced in the harness; the headline metric is out-of-sample; in/out windows proven
      disjoint.
- [ ] N-compared and any dropped strategies are reported, not silent.
- [ ] Deterministic; output uses allowed labels only, no forbidden financial-advice terms.
- [ ] `dotnet build` / `dotnet test` green.

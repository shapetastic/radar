# Task: Add dumb baseline strategies — the composite must beat them before it can claim anything

> **The question no artifact currently answers: does Radar's composite score add anything over a trivial
> heuristic?** The 2026-07-28 replay backtest ranked five deliberately-different strategies and they landed
> within 0.016 of each other. When everything correlates with everything, the useful comparison is not
> strategy-vs-strategy — it is **strategy vs. embarrassingly simple baseline**.
>
> If "count the signals" or "how much media covered it" tracks price as well as the composite does, the
> composite is expensive decoration. That is a cheap thing to find out and an expensive thing to assume.

## Design

### 1. Baselines are strategies, not a new subsystem

They must go through the **same** scoring seam, the same stores, the same fingerprints and the same
leaderboard as any other strategy — otherwise the comparison is not like-for-like. Most are already
expressible in config:

| Baseline | Question it asks | Expressible today? |
|---|---|---|
| `baseline-earnings-only` | does the latest guidance read alone track price? | **Yes** — `SignalTypes: ["GuidanceChange"]` |
| `baseline-media-only` | is Radar just tracking press coverage? | **Yes** — a single channel over the news/RSS collectors |
| `baseline-activity-only` | is score just "something happened"? | **Needs a direction-free scoring path** |
| `baseline-following-tier` | is score just "small company"? | **Not expressible** — the tier is a curated company attribute, not a signal |

**Work out which are genuinely config-only against the shipped binding and which need code.** Ship the
config-only ones in this slice regardless; for the other two, either add the smallest honest affordance or
record precisely why they are deferred. **Do not fake a baseline** — a "following tier" baseline implemented
by some proxy is worse than not having it, because it would look like a control while testing something else.

### 2. `baseline-activity-only` is the important one

It is the direct test of the hypothesis that the composite is dominated by volume — the same hypothesis
spec 153 addresses from the scoring side. Its score should be pure activity: how much evidence arrived,
with **no direction, no notedness, no quality weighting**.

If this slice lands **after** spec 153, `radar-formula-v10` will have made direction explicit and an
activity-only baseline is the natural complement. **Reconcile with whatever 153 actually shipped** rather
than assuming its shape.

### 3. Names must say what they are

Prefix every baseline `baseline-` so nobody reads one as a candidate strategy in a report or leaderboard.
They exist to be **beaten**, and if one wins that is a finding about Radar, not a recommendation.

### 4. The acceptance rule this enables

Record it in `docs/architecture-decisions.md` as the standard the project holds itself to:

> A composite strategy may only be described as adding value if it beats **every** baseline
> **out-of-sample**, on an honest N, by more than the spread between the baselines themselves.

That is a documentation change with teeth: it is the rule that stops a 0.016 spread being reported as a
ranking.

## Files (verify against the tree before planning)

`scripts/run-profiles/default.json` (the strategy list), the strategy binding in
`InfrastructureServiceCollectionExtensions`, `ScoringChannel` / `ScoringChannelSet`, whatever 153 shipped,
and `docs/architecture-decisions.md`.

## Constraints

- **No change to existing strategies' scores or identities**; adding a baseline must move no pin.
- Baselines go through the normal seam — no special-casing in the harness or renderers.
- Each baseline gets its own identity and its own series, like any strategy.
- **Honest cost note:** each added strategy scores all 43 companies every run and adds a table to the weekly
  report. Keep the set small — this is a control group, not a sweep, and every extra arm makes a chance
  winner more likely (the exact trap spec 140's hold-out exists to resist).
- Price never an input (AD-14).

## Out of scope (record, do not build)

- **Auto-tuning or auto-promoting anything** based on which baseline wins.
- **Removing or rewriting existing strategies.**
- **The horizon / outcome-variable question** — spec 152 first; none of these baselines mean anything until
  the leaderboard stops labelling partial windows as full-horizon returns.

## Acceptance criteria

- [ ] The config-only baselines ship, named `baseline-*`, scored through the normal seam with their own
      identities.
- [ ] For any baseline that is not config-only, either the smallest honest affordance is added or the reason
      it is deferred is recorded — and no baseline is approximated by a proxy.
- [ ] Adding them moves no existing strategy's `ScoringConfigVersion`; pins unchanged.
- [ ] The "must beat every baseline out-of-sample" rule is recorded in the decisions ledger.
- [ ] The hand-back states the added per-run cost (companies × strategies) so the control group stays small
      deliberately.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.

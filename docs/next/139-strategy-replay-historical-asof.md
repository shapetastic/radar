# Task: Replay — score a strategy across historical as-of dates from stored signals

> **This is where point-in-time honesty pays off.** Spec 136 made both scoring reads filter
> `CreatedAtUtc <= windowEndUtc`, so a scoring run at a *past* instant provably sees only what Radar knew
> then. This slice uses that to **replay** a strategy across a series of historical as-of dates from the
> already-stored signal history — producing the score time-series a strategy *would have* generated, without
> re-collecting anything.
>
> **Depends on spec 136 (merged) for correctness and spec 137 (multi-strategy) for the strategy abstraction.**
> Ideally also 138 (signal-type filter) so replayed strategies are genuinely distinct. Reconcile all type and
> path names against what 137/138 actually shipped.

## Why replay, and why it is safe now

Today the efficacy series only grows **forward** — one snapshot per daily run — so evaluating a new strategy
means waiting weeks for data to accrue. Replay reconstructs history: given the append-only signal store
(AD-8) and the 136 knowledge-date predicate, scoring "as of 40 days ago" is a pure function of stored
signals with `CreatedAtUtc <= that date`. **This is only honest because 136 is correct** — without the
predicate, a replay would leak signals created after the as-of instant and manufacture hindsight. The replay
harness must therefore treat the 136 predicate as load-bearing and assert it, not trust it.

## The hard invariant: replay ⊆ forward

**A replay at as-of date D must produce the byte-identical score a forward run did produce on date D** (for
any D where a real forward snapshot exists and the signal store has not been rewritten). If replay and the
historical forward snapshot disagree, replay is lying and every downstream comparison (spec 140) is
worthless. This equivalence is the slice's primary acceptance test — build the harness around proving it,
not around generating pretty series.

## Design

### 1. What replay is (and is not)

- **Is:** a read-only, offline scoring pass. For each as-of date D in a requested series, construct the same
  windows the pipeline would (`windowEndUtc = D`), read signals through the 136 predicate, run each
  strategy's `ScoringEngine`, and emit the resulting snapshots into a **replay-scoped, clearly-labelled**
  location — **never** the live scores directory.
- **Is not:** re-collection, re-extraction, re-AI-read, or any mutation of the signal/evidence stores.
  Replay consumes stored signals **exactly as they are**. If a signal was never collected, it does not exist
  for replay — that is the correct, honest behaviour.

### 2. Entry point

A CLI/worker verb (reconcile with how `run-radar.ps1` / the Worker expose commands) e.g.:

```
replay --from 2026-05-01 --to 2026-07-25 --step 1d [--strategy <name>...]
```

- `--from/--to/--step` define the as-of series. Default `--strategy` = all configured strategies.
- Dates are UTC instants (AD-7: one run, one instant); each D is a distinct scoring `windowEndUtc`.
- **`--step` must not silently cap the series.** If the range is large, `log`/report how many as-of points
  will run; do not truncate without saying so.

### 3. Reuse the real scoring path — do not fork it

Replay must call the **same** `ScoringEngine` / factory / read seam the live pipeline uses (post-136/137),
only with a supplied historical `windowEndUtc` instead of `_timeProvider.GetUtcNow()`. Any second copy of the
scoring logic will drift and silently invalidate the replay⊆forward invariant. Inject the as-of instant;
share everything else.

### 4. Storage — isolated and labelled

- Replay snapshots go to a **replay-scoped path** (e.g. `replays/{runLabel}/strategies/{name}/...` —
  reconcile with 137's storage layout) carrying `StrategyName` and the as-of date, and a marker
  distinguishing them from live forward snapshots.
- **Replay must never write into or overwrite the live scores directory** — the spec-101/108 forward
  efficacy series is sacred history and a replay is a hypothesis, not fact. Assert this in tests.

### 5. Idempotence and determinism

Given an unchanged signal store, replaying the same range twice yields **identical** output. No
`Date.now()`/random in the scoring path (already true). Two identical replays are diffable to zero.

## Assignment

Worktree: any. Files: a new replay harness in `Radar.Application` (or `Radar.Worker` for the verb, calling
into Application), reusing the 136/137 scoring seam; a replay-scoped snapshot store target; the CLI/verb
wiring in `Radar.Worker` / `run-radar.ps1`; and tests.
Dependencies: **136 merged** (correctness) and **137 merged** (strategy abstraction); **138 strongly
recommended** so replayed strategies differ meaningfully. Reconcile names against their actual
implementations.
Estimated time: ~4 h.

## Tests

- **replay ⊆ forward (the critical one):** seed a signal store with known `CreatedAtUtc`s, run a forward
  score at D, then replay as-of D — the snapshots are **byte-identical** (same score, same
  `ScoringConfigVersion`, same `StrategyName`, same evidence links).
- **No hindsight leak:** a signal with `CreatedAtUtc = D + 1 day` is present in the store but a replay as-of
  D does **not** see it (directly exercises the 136 predicate through the replay path).
- **Store is untouched:** after a replay, the signal store, evidence store and the **live** scores directory
  are unchanged; replay output lives only under the replay-scoped path.
- **Determinism:** two identical replays of the same range produce identical output.
- **Legacy signals:** a signal with null `CreatedAtUtc` (legacy) is included as "known" per 136's rule and
  replay matches that.
- **Series shape:** a 3-point `--from/--to/--step` run produces exactly 3 as-of snapshots per strategy, each
  stamped with its as-of date.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

## Constraints

- **Read-only over signals/evidence.** Replay mutates nothing except its own replay-scoped output.
- **No second copy of the scoring logic.** Reuse the live engine/factory/read seam with an injected as-of
  instant (CLAUDE.md reuse-over-copy).
- **No fingerprint move.** Replay uses the existing strategy configs; it does not introduce new hashed
  inputs. Pins hold.
- **AD-14 stays intact:** replay scores from signals only — **price is not read here**. Price comparison is
  spec 140.
- **Layering:** no `IConfiguration` in `Radar.Application`; the as-of series and strategy set arrive resolved.

## Out of scope (record, do not build)

- **Strategy-vs-price comparison / efficacy scoring of replays** — spec 140. Replay only *produces* the
  historical score series; judging it against price is the next slice.
- **Re-collection or backfill of missing signals.** If history is thin, replay is honestly thin. Do not
  synthesise signals.
- **Incremental/cached replay.** Correctness first; at 43 companies a full replay is cheap. Optimise only if
  it shows up.

## Acceptance criteria

- [ ] A replay verb scores configured strategies across a `--from/--to/--step` as-of series from stored
      signals, injecting historical `windowEndUtc` into the **live** scoring seam.
- [ ] **replay as-of D == forward snapshot at D**, byte-identical, proven by test.
- [ ] The 136 predicate is exercised through replay: post-D signals never leak into an as-of-D score.
- [ ] Replay writes only to a replay-scoped, labelled location and mutates no live store — asserted.
- [ ] Replay is deterministic and idempotent over an unchanged store.
- [ ] No fingerprint move; price is not read.
- [ ] `dotnet build` / `dotnet test` green.

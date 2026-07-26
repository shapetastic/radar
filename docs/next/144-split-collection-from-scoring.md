# Task: Split collection from scoring into two independently runnable passes

> The target shape: **collection runs daily on its own schedule and writes durable evidence + signals.
> Scoring runs separately, over whatever has accrued, as often as you like.** Adding or re-running a
> strategy then costs a scoring pass — no re-collection, no network, no SEC fair-access exposure, no AI
> spend. That is what makes "run lots of strategies and find one that works" affordable.
>
> **Do not start before spec 142.** Until the scoring read path is durable, a separate scoring pass would
> read an empty in-memory store and score nothing.

## What exists today

`RadarPipelineRunner.RunAsync` runs stages 1–7 in one method — collect → extract/resolve/review/store →
**stage 6: score** → report — and `Worker.cs` calls it as a single unit; `WorkerRunOptions` exposes only
`RunOnce` and `Interval`. Scoring is a *stage of a collection run*. Spec 137 made stage 6 loop N strategies,
but still only ever inside a collection pass. Spec 139 added a replay mode that scores without collecting,
but it is deliberately walled off: replay writes to a replay-scoped path and **must never** write the live
series.

So the machinery for scoring-without-collecting exists. What is missing is making it a first-class
production path rather than a hypothesis-only one.

## Design

### 1. Two verbs

Expose collection and scoring as independently invokable passes — **reconcile with how 139's
`Radar:Replay:Enabled` mode and `run-radar.ps1` already select behaviour**, and follow that precedent rather
than inventing a third mechanism.

- **`collect`** — stages 1–5: collect evidence, extract/resolve/review/store signals. Writes durable stores.
  Does not score, does not report.
- **`score`** — stage 6 (+ optionally 7): read accrued signals through the 136 point-in-time predicate, run
  every configured strategy, write the live series. **No collector is constructed or invoked.** Assert this
  — a stray collection side effect during a scoring pass is the failure mode that would make scheduled runs
  quietly re-hit external APIs.
- The existing combined run must remain available and behave exactly as today, so the daily baseline job is
  not disturbed by this slice.

### 2. Scoring's as-of instant

A scoring pass supplies its own `windowEndUtc` (default: now), reusing the seam 139 already injects the
historical instant through. **Do not fork the scoring path** — one code path, three callers (combined run,
standalone score, replay). A second copy will drift and silently invalidate `replay ⊆ forward`.

### 3. The difference between `score` and `replay`

They share the engine and differ only in *where they write* and *what they claim*:

- `score` writes the **live** series — it is the record of what Radar thinks now.
- `replay` writes the **replay-scoped** series — a hypothesis about what Radar would have thought.

Keep that distinction explicit and asserted. A standalone `score` pass at an as-of instant in the past is a
replay and must not be allowed to write the live series under the name `score`.

### 4. Scheduling

Update `scripts/` so the daily job runs `collect`, and scoring can be invoked separately (and repeatedly)
without re-collecting. `setup-baseline-task.ps1` / `RadarBaselineDaily` currently drive one combined run —
reconcile deliberately and state what the maintainer must re-run, since that task is maintainer-only and
elevated.

## Files (verify against the tree before planning)

`RadarPipelineRunner` (stage split), `IRadarPipeline`, `Worker.cs` / `WorkerRunOptions` / `RadarWorkerOptions`,
the replay-mode wiring from 139, `scripts/run-radar.ps1`, `scripts/run-profiles/*`, and the baseline task
script.

## Constraints

- **One collection pass semantics are unchanged** (137): within a `collect` run, collection, the AI
  directional read, extraction, resolution and review each run exactly once.
- **A `score` pass performs no collection and no AI read** — asserted by test, not by convention.
- **No scoring change.** Scores from `collect`-then-`score` must be byte-identical to the combined run over
  the same inputs and instant. This is the slice's primary acceptance test.
- **Append-only (AD-8)**; **price is not read** (AD-14).
- **Layering:** stage orchestration stays in `Radar.Application`; hosting/verb parsing in `Radar.Worker`.

## Out of scope (record, do not build)

- **Two deployed services / separate processes on separate machines.** This slice delivers two independently
  invokable passes; how they are hosted is an operational decision, not a code one.
- **A queue, scheduler or daemon.** The OS scheduled task remains the scheduler.
- **Durable persistence work** — spec 142, a hard prerequisite.
- **Strategy-vs-price comparison** — spec 140.

## Acceptance criteria

- [ ] `collect` and `score` are independently invokable; the existing combined run still works unchanged.
- [ ] `collect`-then-`score` produces byte-identical scores to the combined run for the same inputs and
      as-of instant — asserted.
- [ ] A `score` pass constructs and invokes **no** collector and performs no AI read — asserted.
- [ ] Scoring, standalone scoring and replay share one scoring code path; `replay ⊆ forward` still holds.
- [ ] A past-dated standalone `score` cannot write the live series.
- [ ] Scripts updated; the maintainer-only baseline-task change is stated explicitly in the hand-back.
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release` green.

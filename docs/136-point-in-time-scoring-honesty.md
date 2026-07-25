# Task: Make point-in-time scoring honest — filter both signal reads by `CreatedAtUtc`

> **FOUNDATION SLICE for strategy backtesting. Provably a no-op for forward runs.** Scoring currently
> windows signals on `ObservedAtUtc` alone — *when the event happened* — with no regard for *when Radar
> learned it*. Running forward that is safe, because a run can only score what it has already collected.
> **Replaying a historical `asOf` is NOT safe**: it pulls in every signal published before that date
> regardless of when it entered the store. This slice adds the missing predicate so any future backtest
> is trustworthy.
>
> **No formula, weight, tier, rule, enum or collector-set change. NO fingerprint move.** Precedent: spec
> 113's `GuidanceChangeSupersede` was likewise "deliberately NOT a fingerprint input (a correctness fix,
> not a scoring-config change — the stamp must not move)". Same reasoning applies here.

## Why this matters now

The direction under discussion is to decouple **data collection** from **scoring**, express strategies as
composable signal-type/weight sets, and evaluate many strategies over the same evidence — then compare each
against price to see which discriminates. The decisive advantage of that design is **replay**: a new
strategy can be scored over all stored history immediately rather than waiting weeks to accrue a series.

Replay is only worth anything if scoring at `asOf = T` sees exactly what Radar knew at `T`. Today it does
not. Every backtest built on the current filter would be silently inflated, and the inflation favours
exactly the signals we most want to evaluate.

## The defect (verified 2026-07-25 against `main` @ `80a37a4`)

`Signal` carries **both** timestamps (`src/Radar.Domain/Signals/Signal.cs:16-17`):

```csharp
DateTimeOffset ObservedAtUtc,
DateTimeOffset CreatedAtUtc);
```

`ObservedAtUtc` is the **event** date (`ExtractedSignalMapper.cs:40`):

```csharp
var observedAtUtc = evidence.PublishedAtUtc ?? evidence.CollectedAtUtc;
```

`CreatedAtUtc` is the **knowledge** date — the run instant, threaded from
`RadarPipelineRunner.cs:444` (`ExtractedSignalMapper.ToSignal(extracted, evidence, asOfUtc)`).

**Both scoring read paths ignore `CreatedAtUtc`:**

1. **Current window** — `ScoringEngine.cs:146-148`
   ```csharp
   var windowedApproved = allSignals
       .Where(s => s.ObservedAtUtc > windowStartUtc && s.ObservedAtUtc <= windowEndUtc)
       .Where(s => s.ReviewStatus == SignalReviewStatus.Approved);
   ```
2. **Previous window (velocity input)** — `ScoringEngine.cs:208-210`
   ```csharp
   var previousSignals = await _signalFileStore
       .ReadApprovedInWindowAsync(companyId, previousWindowStartUtc, windowStartUtc, ct)
   ```

### Real leakage, not hypothetical

- **Enabling a collector backfills history.** `fda` went live on 2026-07-25 (spec 133) with a **365-day**
  lookback. A replay of any date in the past year would now include FDA-derived signals that did not exist
  in the store on that date.
- **Spec 126's filing cap.** Filings are analysed ≤`MaxFilingsPerRun` per run, newest-first. A filing
  published on the 1st may not yield its directional `GuidanceChange` until weeks later; replaying the 5th
  would include it.
- Any collector outage followed by a catch-up run does the same.

The leak is confined to sources carrying a real `PublishedAtUtc` — SEC filings, news, press releases.
Where it is null, `ObservedAtUtc` falls back to `CollectedAtUtc` and is honest by construction. That is
**inverted from what we want**: the leaky sources are the directional ones that drive Trajectory.

## Why the fix cannot change forward-run behaviour

AD-7 gives one run, one instant. `asOfUtc` is captured once (`RadarPipelineRunner.cs:216`) and used for the
mapper's `createdAtUtc`, the scoring `windowEndUtc`, and the report period end. So for every signal created
in a run, `CreatedAtUtc == asOfUtc == windowEndUtc` **exactly**, and `CreatedAtUtc <= windowEndUtc` is
satisfied by equality. Signals from earlier runs have strictly smaller `CreatedAtUtc`.

Therefore the new predicate excludes **nothing** on a forward run. This is the load-bearing safety property
and it must be regression-locked by test — the failure mode to guard against is the predicate silently
dropping the current run's fresh signals and scoring every company from zero.

## Design

### 1. Current-window read (`ScoringEngine`)

Add the knowledge-date predicate alongside the existing window and review filters:

```csharp
.Where(s => s.ObservedAtUtc > windowStartUtc && s.ObservedAtUtc <= windowEndUtc)
.Where(s => s.CreatedAtUtc <= windowEndUtc)   // known-at: what Radar knew by asOf
.Where(s => s.ReviewStatus == SignalReviewStatus.Approved);
```

Comment it as the point-in-time honesty rule, in the same register as the existing window/review comment
block (both are described there as "tunable pipeline scaffolding, NOT formula" — this is the third such
rule, and likewise not formula).

### 2. Previous-window read (`ISignalFileStore.ReadApprovedInWindowAsync`)

This one needs the threshold threaded in — it currently takes only `(companyId, start, end, ct)`. Add a
`knownAsOfUtc` parameter and apply `CreatedAtUtc <= knownAsOfUtc` inside the store's filter.

**The caller passes `windowEndUtc`, NOT `windowStartUtc`.** The previous window's *observation* range ends
at `windowStartUtc`, but the *knowledge* threshold is the scoring instant — we are asking "what did Radar
know at `asOf` about the preceding period", not "what did it know at the start of the current window".
Getting this wrong under-counts previous-window activity and silently shifts velocity.

If the on-disk record does not currently persist `CreatedAtUtc`, persist it (trailing + nullable in the file
DTO, mirroring how `ScoringConfigVersion` was added in `FileScoreSnapshotStore`), and treat a **null as
"unknown → include"** so pre-existing files keep their present behaviour rather than silently vanishing from
the previous window. Record that choice explicitly: it means history written before this slice is not
replay-honest, which is correct and honest — the data genuinely does not carry the fact.

## Tests

- **Forward-run no-op (the critical one):** a signal whose `CreatedAtUtc == windowEndUtc` exactly is
  **included**. Regression-locks the equality boundary so the predicate can never drop the current run's
  own signals.
- **Leakage excluded:** a signal with `ObservedAtUtc` inside the window but `CreatedAtUtc` strictly after
  `windowEndUtc` is **excluded** from the current window.
- **Same for the previous window**, via the file store, including that the caller passes `windowEndUtc` as
  the knowledge threshold (a test that would fail if `windowStartUtc` were passed instead).
- **Null `CreatedAtUtc` on an existing on-disk record is included** (back-compat).
- A replay-shaped test: score the same company at two different `asOf` values over one fixed signal set and
  assert the earlier `asOf` sees strictly fewer signals when some were created later.
- **Fingerprint guard:** `ScoringConfigFingerprintTests` green **unmodified, no pin edit** — AI-OFF
  `radar-scoring-fp-6b2f468041b9` / AI-ON `radar-scoring-fp-57356123e09b` hold.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

## Constraints

- **No formula/weight/tier/rule/enum/collector-set change; no fingerprint move.** If any fingerprint pin
  needs editing, the change has leaked scope and is wrong.
- Provenance untouched — this filters which signals are *read*, and every scored signal still carries its
  `ScoreEvidenceLink` chain.
- AD-7 preserved: one run, one instant. Do not introduce a second clock read in the scoring path.

## Out of scope (record, do not build)

- **The collection/strategy decoupling itself** — running N strategies over one collection pass, strategy
  identity on the snapshot (`(asOf, evidence-set) × strategy`), splitting the fingerprint's *data
  provenance* from its *strategy identity*, per-strategy reports. This slice is only the prerequisite that
  makes replay trustworthy.
- **Backfilling replay-honest history.** Records written before this slice lack the knowledge date; they
  cannot be retro-fixed and must not be faked.
- **Any price-vs-strategy comparison work** (AD-14: price stays validation-only).

## Acceptance criteria

- [ ] Both scoring read paths filter on `CreatedAtUtc <= windowEndUtc` in addition to the existing
      `ObservedAtUtc` window and Approved-only rules.
- [ ] `ISignalFileStore.ReadApprovedInWindowAsync` takes an explicit knowledge threshold, and the caller
      passes **`windowEndUtc`** (not `windowStartUtc`), covered by a test that distinguishes the two.
- [ ] A signal with `CreatedAtUtc == windowEndUtc` is **included** (forward-run no-op regression lock).
- [ ] A signal observed in-window but created after `windowEndUtc` is **excluded**.
- [ ] On-disk records lacking a persisted `CreatedAtUtc` are treated as unknown → included, and the
      limitation is documented.
- [ ] **Fingerprints byte-identical; `ScoringConfigFingerprintTests` green with no pin edit.**
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

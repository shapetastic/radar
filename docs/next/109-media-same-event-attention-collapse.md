# Task: Collapse many-outlet coverage of one event into ~one attention signal

> **CALIBRATION / DE-NOISING REWORK — slice 1 of 4 (failure #2).** Part of the directed
> signal→score de-noising rework diagnosed from the live 2026-07-17 run (AEHR fixture). The
> scoring freeze is deliberately **LIFTED** for this rework (maintainer decision 2026-07-17 —
> Radar surfaces nothing; all 19 companies score `Ignore`). Spec 108 (efficacy
> score-continuity-aware segmentation, PR #111 merged) already protects the efficacy score line
> across the fingerprint re-stamps this rework causes, so re-stamps are acceptable now.
>
> **SEQUENCING.** Dispatch this **FIRST** in the rework. It de-noises the Attention channel and the
> signal set so the trajectory-robustness (spec 111) and materiality (specs 110/112) changes are
> measurable on a clean baseline. All four rework specs re-stamp the default scoring fingerprint and
> touch the pinned fingerprint test + `default.json` comment, so they **must be sequenced, not
> parallelized** (each rebases on the prior's new fingerprint). Do **not** run in parallel with
> 110/111/112.

## Overview

On the live 2026-07-17 run, AEHR's single record-guidance earnings event was covered by ~23 distinct
news outlets ("soars", "skyrockets 40%", "up 29.3%"). The GDELT/news collector emitted **one
`MediaAttention` (Neutral) signal per outlet**, so one real-world event became **~23 signals**. This
is duplication, not breadth: it inflates the media contribution to `AttentionScore`, pads the signal
count that dominates the report's "why Radar noticed" section, and makes a genuinely-improving company
look like it is drowning in undifferentiated media noise.

This slice adds a **principled, deterministic per-company / per-window event-collapse** for
`MediaAttention` signals: many near-simultaneous outlets covering one event collapse to **one
representative attention signal** (retaining provenance to the collapsed set), so one event counts as
~one attention unit — **not** a ticker-specific rule. It is a general de-noising transform; AEHR is the
acceptance fixture, not a special case.

> **NOT in scope / do not overfit.** Do not tune anything to AEHR. Do not touch the Attention formula
> *shape* (the tier-weighted distinct-publisher breadth is genuine market notice and stays — spec 88);
> this slice only changes *how many* `MediaAttention` signals enter scoring, collapsing same-event
> duplicates. Do not change any other signal type's aggregation.

---

## Assignment

Worktree: any (sequence after none; this is the first rework slice)
Dependencies: None
Conflicts with: 110, 111, 112 (all re-stamp the default fingerprint + edit the pinned fingerprint test
  and `default.json` comment; also all read the scoring input assembly). Sequence, do not parallelize.
Estimated time: ~1-2 hours

---

## Project structure changes

Add (Application):
- `src/Radar.Application/Scoring/MediaAttentionCollapse.cs` — the deterministic collapse transform +
  its `CanonicalDescriptor()` (version + window param) for the fingerprint.
- `src/Radar.Application/Scoring/MediaCollapseOptions.cs` — the tunable collapse window
  (`Radar:Scoring:MediaCollapse:*`), with a default and fail-fast validation.

Modify:
- `src/Radar.Application/Scoring/ScoringEngine.cs` — apply the collapse to the assembled
  current-window `ScoringSignal` list **before** it becomes `ScoringInput`; fold the collapse
  descriptor into the fingerprint (see below).
- `src/Radar.Application/Scoring/ScoringConfigFingerprint.cs` +
  `src/Radar.Application/Scoring/EffectiveScoringConfig.cs` — append a `mediaCollapse` descriptor field
  (after `insiderDesc`, existing ordering unchanged) so the change re-stamps automatically and the
  persisted effective config recomputes to the same fingerprint (specs 95/96 precedent).
- DI registration for `MediaCollapseOptions` (mirror the `ScoringWeights` binder).
- `tests/Radar.Application.Tests/Scoring/ScoringConfigFingerprintTests.cs` — update the pinned default
  fingerprint constant (read the current value first; it is order-robust).
- `scripts/run-profiles/default.json` — update the fingerprint note in `_comment`.

---

## Implementation details

- **Collapse rule (deterministic, AD-3).** Operate only on `ScoringSignal`s whose
  `Signal.Type == MediaAttention`. Group them **per company** (the engine already scores one company at
  a time) into **event buckets** by observation-time proximity: order the media signals by
  `ObservedAtUtc`, then greedily bucket signals whose `ObservedAtUtc` falls within
  `MediaCollapseOptions.EventWindow` (default e.g. `TimeSpan.FromDays(3)`) of the bucket's first signal.
  Each bucket yields **one representative** — the earliest-observed signal in the bucket (deterministic;
  `Id` tiebreak). Non-`MediaAttention` signals pass through untouched. Preserve stable ordering (AD-3).
- **Provenance is sacred.** The representative keeps its own evidence link. Collapsed (dropped)
  duplicates must not silently lose their trace: record the collapsed set on the representative's
  contribution reason (e.g. `"… (collapsed N same-event media items)"`) so the report can show one line
  that names the event's coverage breadth rather than N lines. Do **not** fabricate a synthetic signal —
  reuse an existing real signal as the representative.
- **Why per-window collapse and not the formula.** The Attention formula already de-dupes multiple
  articles from the *same* publisher (distinct-publisher breadth) and genuine *distinct* outlets are
  legitimate breadth. The residual noise is one event echoed across many outlets in a tight time window —
  which is a **signal-count** artifact, best removed before scoring (this is the spec 85
  velocity-window-dedup precedent applied to same-event media). Removing the duplicate `MediaAttention`
  signals eases the small `MediaReachWeight·mediaCount` term and, more importantly, de-noises the report
  and the signal count.
- **Fingerprint obligation (AD-10).** This changes scoring output (the media signal set feeding the
  formula), so it MUST re-stamp `ScoringConfigVersion`. Do it the spec 95/96 way: give
  `MediaAttentionCollapse` a `CanonicalDescriptor()` (`media-collapse-v1;window={days}`), inject it into
  `ScoringConfigFingerprint.Compute` as a new trailing field, and carry it verbatim on
  `EffectiveScoringConfig` so recompute-from-stored still equals the filename. **No `_formula.Version`
  bump** — the formula math is untouched; only its input set changes. **No `RuleSetVersion` bump.**
- **Config surface.** `EventWindow` is a tunable magnitude in config (like `ScoringWeights`); the collapse
  *structure* (greedy same-window bucketing, earliest representative) is the versioned part
  (`media-collapse-v1`). Fail-fast on a non-positive window.

---

## Tests

`tests/Radar.Application.Tests/Scoring/MediaAttentionCollapseTests.cs`:
- N `MediaAttention` signals for one company within `EventWindow` collapse to exactly one representative
  (earliest-observed; `Id` tiebreak), and the representative names the collapsed count.
- Media signals **outside** the window form separate buckets (two distinct events → two representatives).
- Non-`MediaAttention` signals pass through unchanged; ordering is stable (AD-3); deterministic across
  repeated runs.
- Empty / single media signal is a no-op.

`ScoringEngine` tests:
- A company with 23 same-event media signals + 5 positive directional signals produces a snapshot whose
  media contribution is collapsed to one, positives unaffected; provenance links still trace every scored
  signal.

`ScoringConfigFingerprintTests`:
- The default fingerprint re-stamps to the new pinned value; recompute-from-`EffectiveScoringConfig`
  equals the stamp.

---

## Constraints

- Target .NET 10 / `net10.0`, C# 14.
- Provenance is sacred: the representative retains its evidence link; collapsed duplicates are named,
  never silently dropped without a trace.
- Deterministic (AD-3): no clock, stable ordering, culture-invariant descriptor.
- Keep changes scoped to media-signal collapse + the fingerprint fold. Do not touch the Attention
  formula shape, other signal types, or the collector.
- No advice language (AD-9).

---

## Acceptance criteria

- [ ] Many outlets covering one event within `EventWindow` collapse to ~one `MediaAttention` signal per
      company; distinct events stay distinct; the transform is general (no ticker-specific logic).
- [ ] Provenance preserved: representative keeps its evidence link; collapsed count is surfaced.
- [ ] `ScoringConfigVersion` re-stamps automatically via a new `mediaCollapse` fingerprint field;
      persisted `EffectiveScoringConfig` recomputes to the same value. No `_formula.Version` /
      `RuleSetVersion` bump.
- [ ] Pinned default-fingerprint test + `default.json` comment updated.
- [ ] AEHR acceptance fixture: its record-guidance event's ~23 duplicate media items no longer swamp the
      signal set — the genuine positive signals are no longer buried under the media mass, and the media
      contribution reflects **one event**, not 23.
- [ ] `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass.

# Task: Default the scoring window to 60 days (small-cap news cadence)

## Overview

The first live run made the case empirically: with the default **30-day** scoring window, three of the
strongest fundamental signals in the watch universe were invisible —
- Agilysys' "17th Consecutive **Record Revenue** Quarter",
- Helios' "Q1 Results that **Exceeded Outlook** (+17% sales)",
- Mercury's "Largest **Production Order**" —

because those press releases were dated weeks before the 30-day cutoff. Re-running with a 60-day window pulled
all three into the report with correctly-matched signals. Small-cap issuers publish material news roughly
monthly, so a 30-day window systematically misses real, recent fundamentals. `RadarScoreFormulaV1` already
recency-weights signals within the window (older signals contribute less), so widening the window adds recall
without over-weighting stale news.

This slice changes the **default** `ScoringWindowDays` from 30 to 60. It is a default-only change — the value is
already a runtime config knob (`Radar:ScoringWindowDays`), so anyone can still override it per run.

---

## Assignment

Worktree: any
Dependencies: existing trunk.
Conflicts with: None.
Estimated time: ~20–30 min

---

## Project structure changes

```text
src/Radar.Worker/
  RadarWorkerOptions.cs        # MODIFIED: ScoringWindowDays default 30 -> 60 (+ updated XML doc)

tests/Radar.Worker.Tests/
  (whichever test asserts the default)   # MODIFIED if a test pins the default to 30
```

---

## Implementation details

- Change `RadarWorkerOptions.ScoringWindowDays` default from `30` to `60`; update its XML doc comment to note the
  rationale (small-cap monthly-ish news cadence; formula already recency-weights within the window).
- **Report period (`ReportPeriodDays`, default 7) — reviewer decision, recommend leaving at 7.** The report is a
  weekly digest; companies scored this run always carry an in-period snapshot, so the 7-day report period does
  NOT gate which companies surface (only the scoring window does). It only bounds the "Signals needing review"
  section and the header's displayed period. Recommended: keep `ReportPeriodDays = 7` (weekly cadence) and,
  optionally, make the report header state that scoring uses a trailing 60-day window so the displayed
  7-day period isn't misread as the signal window. If the reviewer prefers the header period to match the
  scoring window for clarity, that is acceptable — pick one and note it. Do **not** silently couple the two
  options together in code; they remain independent settings.
- No scoring-formula change (`radar-formula-v1` unchanged); this only changes a default window length.

---

## Tests

- Update any test that asserts `ScoringWindowDays == 30` to expect `60`.
- Confirm the worker DI graph still composes and `ScoringOptions.Window == TimeSpan.FromDays(60)` by default.
- Existing scoring tests (which set their own window explicitly) stay green.

---

## Constraints

- Target `net10.0`. Deterministic; no formula change.
- Scope to `RadarWorkerOptions` (+ the affected default-assertion test, + the report header only if the reviewer
  opts to surface the scoring window there).
- `dotnet build`/`dotnet test` on `Radar.sln -c Release` green.

## Acceptance criteria

- [ ] `ScoringWindowDays` defaults to 60; `Radar:ScoringWindowDays` still overrides it per run.
- [ ] Report period remains an independent setting (default 7) — not coupled to the scoring window in code.
- [ ] `build`/`test` green.

# Task: Make the Watch-floor's "corroborating signal types" transparent about single-event echoes

## Overview

`WeeklyReportActionPolicyV1` floors an under-followed company from Ignore up to Watch when it counts
`>= MinCorroboratingSignalTypes` distinct positive signal TYPES among contributing signals
(`WeeklyReportActionPolicyV1.cs`, the `positiveTypeCount` rule). The 2026-09-04 NWPX skeptic review showed
the count can be satisfied by ONE real-world event wearing two extractors' clothes: the Serpentix
acquisition produced a keyword `StrategicPartnership (Positive)` from the 8-K AND a judgment-derived
`MediaAttention (Positive)` from the press release — two "corroborating types", one announcement, ~2.5% of
revenue. The floor's rationale line then presents the count as independent corroboration.

True cross-source event identity (recognizing that an 8-K and a syndicated PR describe the same deal) does
not exist in the codebase and is NOT built here — that would be a large, easily-wrong system. What this
slice does is make the floor honest and measurable: the rationale must SHOW what it counted, and the audit
must say how often the floor rests on plausibly-single-event pairs.

## Assignment

Worktree: any. Dependencies: none beyond current main; independent of spec 209 (different files — dispatch
either order, but not concurrently, both touch the weekly report surface). Use `run-next.ps1 -Spec 210`.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. The rationale names what it counted

When the Watch floor fires, its rationale line changes from the bare count to a named list: each
corroborating type with its evidence date and source class, e.g.

> floored to Watch: StrategicPartnership (filing 2026-09-02) + MediaAttention (news 2026-09-02, judgment)

- derived entirely from the contributing signals already in `ReportActionContext` — no new lookups;
- same-date pairs from different source classes are exactly the suspicious shape; showing the dates is what
  lets a human spot them (the NWPX line would have read as two 2026-09-02 items — self-evidently one event);
- the existing count/threshold logic and every OTHER policy rule stay byte-identical: this changes the
  rationale STRING of one rule only. Update the pinned rationale tests accordingly — pin the new shape, and
  keep a case proving the count itself did not change.

## 2. Measure how often the floor rests on echoes (read-only)

Over the accrued run history (or, minimally, the last 14 daily reports/snapshot sets):

- how many Watch-floor firings occurred; for each, the counted types with evidence dates;
- how many rest entirely on same-day type-pairs (the plausibly-single-event shape) vs types separated by
  distinct dates/events;
- record the table in the PR body. If same-day pairs dominate, the follow-up DECISION (raise
  `MinCorroboratingSignalTypes`, require distinct dates, or build real event identity) belongs to the
  maintainer with that measurement in hand — do not change the threshold or add a date-separation rule in
  this spec, because either would silently change which companies get floored without a human having seen
  the distribution first.

## Non-goals

No cross-source event-identity system; no change to the floor's count, threshold, or any label outcome
(every company gets the same label before and after this spec — pin that with a fixture); no scoring,
fingerprint or signal change; no change to the daily news report.

## Acceptance criteria

- [ ] The Watch-floor rationale names each corroborating type with evidence date and source class; a fixture
      reproducing the NWPX shape renders the same-day pair visibly.
- [ ] Label outcomes are byte-identical for a full report fixture before/after (rationale text is the only
      diff).
- [ ] The §2 echo-frequency table is in the PR body with its follow-up decision left to the maintainer.
- [ ] All six fingerprint pins unchanged; build, full suite and `git diff --check` clean; actual elapsed
      time in the PR body.

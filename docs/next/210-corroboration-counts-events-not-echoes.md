# Task: Make the Watch-floor's "corroborating signal types" transparent about single-event echoes

## Overview

`WeeklyReportActionPolicyV1` floors an under-followed company from Ignore up to Watch when it counts
`>= MinCorroboratingSignalTypes` distinct positive signal TYPES among contributing signals. The concern
(raised by the 2026-09-04 NWPX skeptic review) is that one real-world announcement can wear two extractors'
clothes — a keyword-typed signal from a filing plus a judgment-derived `MediaAttention` from the same
event's press coverage — and satisfy the count without independent corroboration.

**Corrected framing (2026-09-05 pre-spec review): the echo is a HYPOTHESIS to measure, not a demonstrated
live defect.** The motivating NWPX example did not survive inspection: its current floor rests on
`EarningsTrajectory` from the 2026-07-29 earnings 8-K plus `MediaAttention` from September news — separate
dates and apparently separate events, and no `StrategicPartnership` signal exists for the Serpentix
acquisition. The mechanism is still structurally possible (nothing prevents a same-event pair from
counting), so this slice makes the floor's rationale transparent, measures how often the suspicious shape
actually occurs, and pins the behaviour with a **synthetic** same-event fixture unless the audit finds a
genuine live example.

True cross-source event identity is NOT built here.

## Assignment

Worktree: any. Dependencies: none beyond current main; not concurrent with spec 209 (both touch the weekly
report surface). Use `run-next.ps1 -Spec 210`.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. Thread the provenance the rationale needs into the action context

`ReportSignalRef` today carries only `(SignalId, Type, Direction, Reason)` — no date, no evidence source
class, no judgment-derived flag — and `WeeklyReportBuilder` loads evidence only AFTER the policy decides.
Extend additively:

- add to `ReportSignalRef` (trailing, so existing construction sites stay source-compatible): the signal's
  `ObservedAtUtc`, the evidence source class (filing / news / government-contract / press-release), and
  whether the signal is judgment-derived (`NewsDirectionalSignalMetadata.IsJudgmentDerived` — reuse, no
  second parser);
- populate them by REUSING one evidence lookup: restructure the builder so the evidence read that already
  happens for the entry also feeds the action context, rather than adding a second per-company evidence
  pass (the spec-203 lesson: no per-call disk scans).

## 2. The rationale names what it counted

When the Watch floor fires, its rationale changes from the bare count to a named list. Because one TYPE can
have several supporting signals, the rendering must not silently pick one: for each corroborating type,
render every distinct (source class, observed date) support tuple — or, where that exceeds a small cap, the
type's distinct-date count and date range — in a deterministic order (type, then date, then source class).
Example shape:

> floored to Watch: EarningsTrajectory (filing 2026-07-29) + MediaAttention (news 2026-09-02, judgment;
> news 2026-09-03, judgment)

Arbitrarily choosing a first/latest tuple could manufacture or hide an echo; showing them all (or the
honest range summary) is the point. The count, threshold and every label outcome stay byte-identical —
pin with a full-report fixture whose only diff is rationale text.

Because the rationale CONTRACT changes while labels do not, bump the policy's declared version:
`weekly-report-action-v2 → v3` (`WeeklyReportActionPolicyV1.Version`), with its version-consuming
tests/pins updated in the same slice.

## 3. Pin the echo shape synthetically; measure it live

- **Fixture:** a synthetic company whose floor is satisfied by a same-day filing-typed positive plus a
  judgment-derived `MediaAttention` positive citing same-day news — the rendered rationale must make the
  same-day pair visible on one line. This is the guard for the hypothesized shape regardless of whether it
  currently occurs live.
- **Audit (read-only, PR body):** over the accrued reports/snapshots, count Watch-floor firings TWO ways —
  raw report occurrences AND deduplicated support episodes keyed by (company, contributing type + evidence
  set), because fourteen nightly reports can repeat one unchanged floor fourteen times and inflate the
  count. For each distinct episode: the counted types with dates/source classes, and whether it matches the
  same-day cross-extractor shape. If a genuine live echo is found, name it and add it beside the synthetic
  fixture; if none is found, say so — that is the hypothesis measured, not a wasted slice.
- Any follow-up that would change which companies get floored (threshold, distinct-date requirement, real
  event identity) is the maintainer's decision with this table in hand — not this spec's.

## Non-goals

No cross-source event-identity system; no change to the floor's count, threshold, or any label outcome; no
scoring, fingerprint or signal change; no change to the daily news report; no second per-company evidence
pass in the builder.

## Acceptance criteria

- [ ] `ReportSignalRef` carries observed date, source class and judgment-derived provenance, populated from
      one reused evidence lookup; existing construction sites compile unchanged.
- [ ] The Watch-floor rationale renders every corroborating type's distinct support tuples (or capped
      range+count) deterministically; the synthetic same-event fixture makes the same-day pair visible.
- [ ] Label outcomes are byte-identical on a full-report fixture; only rationale text differs; the policy
      version is `weekly-report-action-v3` with its pins updated.
- [ ] The §3 audit reports raw firings AND deduplicated episodes, names a live echo or states none was
      found.
- [ ] All six fingerprint pins unchanged; build, full suite and `git diff --check` clean; actual elapsed
      time in the PR body.

# Task: Strategy evidence status AND operating calls — facts stay uncertain, the call gets made

## Overview

Ten strategies render as "leaders" with no indication of which have outcome evidence, and Radar gives two
incompatible answers about which arm it believes in: `PairedPrimaryStrategy` declares `disclosure-led-v11`
the prospectively chosen arm under confirmatory test, while `default` (out-of-sample ρ −0.05, interval
spanning zero) fronts every report as primary.

The maintainer's guidance, quoted exactly as given (2026-08-23):

> "As a mantra, I would prefer us to take a stab and potentially make a wrong call, rather than sitting on
> the fence and doing nothing."

Uncertain evidence changes the confidence and review date of a call; it does not eliminate the obligation to
make one. **Radar records wrong calls rather than avoiding falsifiable decisions.**

Two layers, deliberately separate:

1. **Evidence status** — computed, factual, never a verdict. Noise is never converted into pass/fail ahead
   of the precommitted gates.
2. **Operating call** — a declared, journaled, falsifiable DECISION that governs ALL user-facing prominence,
   resolved Right/Wrong by an immutable rule declared with the call.

## Assignment

Worktree: any — sequence after spec 183.
Dependencies: specs 176, 183 merged.
Estimated time: ~1.5–2 days (the Lead-governs-narrative choice widens the reporting change).

## 1. Evidence status — computed, closed, only ever descriptive

Per strategy, derived mechanically each run from artifacts that already exist:

```text
Accruing | Ranked | GatePending | GatePassed | GateFailed
```

Rules unchanged from the prior draft: descriptive and confirmatory facts are orthogonal and both rendered;
`Ranked` never renders without its numbers; a CI spanning zero renders "no evidence of discrimination yet"
(a sentence, not a verdict); unreadable evidence degrades the display (`Accruing (evidence unavailable)`),
never hides the arm.

## 2. Operating call — the decision layer

### File, schema, and the single reducer

`data/strategy-operating-calls.json` — committed, the ONLY runtime input to call resolution. The Markdown
journal (§3) is audit-only and never parsed. Schema:

```text
schemaVersion
globalCall?          StopAll                  (present ⇒ no Lead exists; leaders render the diagnostic
                                              view under an explicit "no lead — StopAll" banner)
calls[]:             strategy, call (Lead | Trial | DoNotLead | Stop), asOfUtc, basis,
                     actor (human | gate-default), overridesGate (bool, default false),
                     reviewByUtc (exact UTC), resolutionRule (immutable text, declared with the call),
                     resolution? { outcome: Right | Wrong | Unresolved, resolvedAtUtc, evidenceRef }
```

**One deterministic reducer, in code, tested — implementations cannot disagree about which call wins:**

1. If a persisted gate verdict exists for an arm, and the file's call for that arm either predates the
   verdict or lacks `overridesGate: true` ⇒ the gate default applies (`GatePassed → Lead`,
   `GateFailed → Stop`).
2. Otherwise the file's call applies verbatim.
3. After reduction: if `globalCall: StopAll` is present, no Lead may exist; otherwise EXACTLY ONE Research
   arm must be Lead. **Zero Leads after reduction (e.g. the Lead arm gate-failed) resolves to the
   PREDECLARED fallback: `StopAll`** — if the declared hypothesis fails, no other arm has earned the front
   page by default; a human makes the next Lead call explicitly. This fallback is part of the enum,
   validated, and tested — not a footnote.
4. Validation fails startup on: unknown strategy, unknown token, a call on a Comparator, multiple Leads,
   zero Leads without StopAll, a `resolution` block whose call lacks a `resolutionRule`.

### What a call governs — one privileged strategy, everywhere the reader looks

**`Lead` governs ALL user-facing narrative and action prominence.** The weekly report's narrative — Highest
opportunity, movement, action labels, "why noticed", evidence blocks — is built from the LEAD arm's series;
spec 150 §2's "labels are primary-only" rule is amended to **"labels are Lead-only"** (still exactly one
labelled strategy — the invariant that rule protects). `Radar:PrimaryStrategy` remains ONLY the
storage/series identity (which directory is `data/scores/`, series continuity, `ScoreSeriesKey`) and is
untouched by this spec — immutable score-series identity and reader-facing prominence are now different
things, deliberately. The live-leaders section orders Lead first (under a banner rendering the call, basis,
asOf, reviewBy and resolution rule), then Trial arms in configured order, then DoNotLead with basis stated;
Stop arms move to a complete diagnostic appendix — never hidden, never unlabelled.

A call never touches scores, snapshots, series identity, fingerprints, or news-risk candidate selection
(architecture-tested, as before).

### The initial calls — shipped with the spec, falsifiable by declared rules

Journaled as maintainer-directed 2026-08-23. Review checkpoint: **2026-09-05T00:00:00Z** (first
multi-strategy ranking — a review, not a resolution). Resolution comes from the confirmatory gate, whose
realistic resolution horizon is **≈2027-02-02** (boundary 2026-09-29 + the gate's own accrual requirements)
— the resolution rules reference the GATE EVENT, not a calendar date:

```text
disclosure-led-v11   Lead
  basis:          the prospectively declared AD-15/AD-16 arm under test
  resolutionRule: Right if the AD-15 composite gate PASSES for this arm; Wrong if it FAILS;
                  Unresolved until the gate evaluates. (Interim reviews may re-call on descriptive
                  evidence; doing so journals THIS call as superseded, outcome Unresolved.)

default              DoNotLead   (remains the storage primary / legacy reference series)
  basis:          oos ρ −0.05, CI spans zero at call time
  resolutionRule: Wrong if, at the gate-resolution instant, default's out-of-sample ρ interval excludes
                  zero POSITIVELY on the then-current leaderboard; Right otherwise.

all other Research arms   Trial
  resolutionRule: resolved by supersession — promoted or stopped by a later journaled call.
```

These can be wrong. That is the point: each is falsifiable by a rule fixed at call time, and being wrong
will be recorded with an evidence reference.

## 3. Lifecycle journal

`docs/strategy-lifecycle.md` — committed, append-only, AUDIT-ONLY (never parsed by the Worker): one line per
event (declared / retuned-as-new-name / retired / call-made / call-overridden / call-resolved), with
strategy, date, basis, actor, and for resolutions the outcome + evidence reference. Opening entries
backfilled once from git history. Retirement remains config removal + a journal line; changing
`Radar:PrimaryStrategy` (storage identity) likewise. No automation for either.

## 4. Boundaries, guarded structurally

- Status/call types live outside the scoring closure; the architecture test asserts scoring/pipeline
  reference neither.
- News-risk candidate selection asserted byte-identical across every status/call fixture.
- No fingerprint input, no snapshot field, no formula/rule-set change; the pins do not move.
- **Single-strategy compatibility, stated precisely**: with one configured strategy the call layer and
  status lines are inert (no calls file required, nothing new renders) and the report is byte-identical —
  statuses and calls render only in multi-strategy compositions.

## 5. Out of scope, recorded not built

- Changing `Radar:PrimaryStrategy` / storage identity.
- Automated retirement; status-derived scores; composite strategy-quality metrics; new gates or thresholds.

## Files to inspect

- `src/Radar.Application/Efficacy/Comparison/` (status sources)
- `src/Radar.Application/Reporting/` (builder walks the LEAD series; renderer; section records)
- `src/Radar.Application/Reporting/WeeklyReportActionPolicyV1` call sites (labels: primary-only → Lead-only)
- `data/strategy-operating-calls.json` (new, committed, shipped with initial calls)
- `docs/strategy-lifecycle.md` (new journal)
- `scripts/run-profiles/default.json` (`PairedPrimaryStrategy`)
- spec-150/176 golden tests (multi-strategy goldens change; single-strategy byte-identical)

## Tests

- Reducer: file-only, gate-default-wins, override-wins, and zero-Lead→StopAll fixtures — each deterministic
  and order-independent; StopAll renders the no-lead banner.
- Validation: every §2 failure mode fails startup naming file and rule.
- Prominence: narrative/labels follow Lead (a Lead≠storage-primary fixture shows labels on the Lead arm
  only); Stop→appendix completeness; nothing hidden.
- Resolution: a call resolved Wrong renders its outcome and evidence reference in the journal entry
  fixture; a superseded Trial resolves by supersession.
- Invariance: scores, snapshots, series identity and news-risk nomination byte-identical across all
  status/call fixtures; architecture guard green.
- Single-strategy report byte-identical with no calls file present.

Do not run tests concurrently with another agent's solution-wide test run. At handoff,
`dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass in one
coordinated session.

## Acceptance criteria

- [ ] One deterministic reducer resolves file calls, gate defaults and overrides; exactly one Lead or an
      explicit StopAll — including the predeclared GateFailed fallback — is enforced and tested.
- [ ] Every call carries an immutable resolutionRule and resolves Right/Wrong/Unresolved with an evidence
      reference; the initial three calls ship with their rules and exact UTC review checkpoint.
- [ ] Lead governs all user-facing narrative and action prominence (labels Lead-only); storage identity is
      untouched; scores/nomination provably unaffected.
- [ ] Stop/StopAll arms remain fully visible in the diagnostic appendix; nothing is hidden.
- [ ] The maintainer's guidance appears as quoted; single-strategy output is byte-identical.
- [ ] Build and coordinated tests green.

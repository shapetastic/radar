# Task: Strategy evidence status AND operating calls — facts stay uncertain, the call gets made

## Overview

Ten strategies render as "leaders" with no indication of which have outcome evidence, and — worse — Radar
currently gives two incompatible answers about which arm it believes in: `PairedPrimaryStrategy` declares
`disclosure-led-v11` as the prospectively chosen arm under confirmatory test, while `default` (out-of-sample
ρ −0.05, interval spanning zero) fronts every report as primary.

The maintainer's guidance, verbatim, is this spec's design principle:

> "I would prefer us to try to make a call and be wrong, than sit on the fence as we have and be
> spectacularly wrong."

Uncertain evidence changes the confidence and review date of a call; it does not eliminate the obligation to
make one. **Radar records wrong calls rather than avoiding falsifiable decisions.**

So this spec has TWO layers, deliberately separate:

1. **Evidence status** — computed, factual, never a verdict. Uncertain statistics are displayed as uncertain
   statistics; noise is never converted into pass/fail ahead of the precommitted gates.
2. **Operating call** — a declared, journaled, falsifiable DECISION about prominence, made by a human on the
   evidence and its own record of being right or wrong. Statuses inform; calls decide.

The failure this closes is informed fence-sitting: statuses alone would leave all ten arms operationally
equal, `default` first despite its numbers, `v11` buried despite being the declared hypothesis, and a future
`GateFailed` arm sitting untouched until someone remembered to edit config.

## Assignment

Worktree: any — sequence after spec 183.
Dependencies: specs 176, 183 merged.
Estimated time: ~1–1.5 days.

## 1. Evidence status — computed, closed, only ever descriptive

Per strategy, derived mechanically each run from artifacts that already exist (leaderboard result,
paired-comparison support counts, configured boundaries):

```text
Accruing        not yet rankable; rendered with the projected first-rankable date when computable
Ranked          in the current leaderboard; rendered WITH its numbers (oos ρ, interval, n), never alone
GatePending     named in a precommitted confirmatory gate whose boundary/support is not yet reached
GatePassed      the precommitted gate evaluated and passed (cannot exist before 2026-09-29)
GateFailed      the precommitted gate evaluated and failed
```

Rules: descriptive and confirmatory facts are orthogonal and both rendered; `Ranked` never renders as a bare
badge (the numbers travel with it); an interval spanning zero renders "no evidence of discrimination yet" —
a sentence, not a verdict; a missing/unreadable artifact yields `Accruing (evidence unavailable)`, degrading
the display, never hiding the arm.

## 2. Operating call — the decision layer

A committed file, `data/strategy-operating-calls.json`, read by the Worker, validated fail-fast (unknown
strategy name, unknown token, zero or multiple `Lead`s ⇒ startup failure naming the file and the rule):

```text
OperatingCall     Lead | Trial | DoNotLead | Stop        (Research-purpose strategies only;
                                                          Comparators are inherently diagnostic)
per call:         strategy, call, asOfUtc, basis, actor (human | rule), reviewByUtc
```

Rules:

- **Exactly one active Research strategy is `Lead`** (a deliberate `StopAll` escape token is the only
  exception). "Unknown" and "Mixed" are not calls — only a technical failure may render an arm's call as
  unassessed, and that renders loudly.
- **The call changes REPORT PROMINENCE immediately; it never touches scores, snapshots, series identity, or
  news-risk candidate selection** (asserted by test and by the §4 architecture guard). `Radar:PrimaryStrategy`
  — the storage/series/narrative primary — is NOT changed by this spec: the call governs billing; promoting
  the config primary remains a separate journaled human config act. A `Lead` arm that is not the config
  primary leads the live-leaders section but carries no action labels (spec 150 §2's labels-are-primary-only
  rule stands).
- **Prominence semantics**: the `Lead` arm's table renders first under an explicit call banner (call, basis,
  asOf, review date); `Trial` arms follow in configured order; `DoNotLead` arms render after them with the
  call and basis stated; `Stop` arms move to a diagnostic appendix — still complete, still statused, never
  hidden. "Do not hide evidence" does not mean "give everything equal billing."
- **Gate outcomes create DEFAULT calls, humans may override, overrides are journaled**: `GatePassed` ⇒
  default `Lead`; `GateFailed` ⇒ default `Stop`. The renderer applies the default the run after the gate
  evaluates unless an explicit journaled override exists — the system is allowed to act on its own
  precommitted verdicts; it is never allowed to act on noise.
- **Every call records its outcome eventually**: the journal (§3) carries, for each superseded call, what
  happened at its review date — so Radar accumulates an audit trail of its own wrong calls instead of a
  history of avoided decisions.

**The initial calls ship WITH this spec** — the stab, taken now, journaled as maintainer-directed
2026-08-23, review date = first multi-strategy ranking (~2026-09-05), re-review at the gate boundary
(2026-09-29):

```text
disclosure-led-v11      Lead      basis: the prospectively declared AD-15/AD-16 arm under test —
                                  aligning prominence with Radar's own declared hypothesis
default                 DoNotLead basis: oos ρ −0.05, CI spans zero; remains the config primary /
                                  legacy reference series
all other Research arms Trial
(baseline/control arms  Comparator by Purpose — no call applies)
```

This can be wrong. That is the point: it is falsifiable on a declared date, and being wrong will be recorded.

## 3. Lifecycle journal

`docs/strategy-lifecycle.md` — committed, append-only: one line per event (declared / retuned-as-new-name /
retired / call-made / call-overridden / call-outcome), with strategy, date, basis, actor. Opening entries
backfilled once from git history (the spec-149 renames, spec-154 baselines, and this spec's initial calls).
Retirement remains config removal + a journal line; promotion of the config primary likewise. No automation
for either.

## 4. Boundaries, guarded structurally

- Status and call types live outside the scoring closure; an architecture test mirroring
  `EfficacyReadOnlyGuardrailTests` asserts scoring/pipeline reference neither — outcomes and calls govern
  prominence, never the contemporaneous score.
- News-risk candidate selection is asserted byte-identical across every status/call fixture — calls govern
  trust and billing, not what gets investigated (omission doctrine).
- No fingerprint input, no snapshot field, no formula/rule-set change; the pins do not move.

## 5. Out of scope, recorded not built

- Changing `Radar:PrimaryStrategy` (separate human config act, journaled when it happens).
- Automated retirement; status-derived scores; composite strategy-quality metrics.
- New gates or thresholds (AD-15/AD-16 stand as declared).

## Files to inspect

- `src/Radar.Application/Efficacy/Comparison/` (status sources)
- `src/Radar.Application/Reporting/` (renderer, builder, section records)
- `data/strategy-operating-calls.json` (new, committed, shipped with the initial calls)
- `docs/strategy-lifecycle.md` (new journal)
- `scripts/run-profiles/default.json` (`PairedPrimaryStrategy` — the basis of the Lead call)
- spec-176 golden tests (multi-strategy goldens change; single-strategy stays byte-identical)

## Tests

- Status derivation fixtures per state and orthogonal combination; unreadable artifact degrades display only.
- Call file validation: unknown strategy/token, zero Leads, two Leads, call on a Comparator ⇒ startup
  failure naming file and rule.
- Prominence: Lead-first ordering with banner; DoNotLead/Stop placement; appendix completeness (every arm
  present somewhere with full status — asserted).
- Gate-default behaviour: a GateFailed fixture renders Stop the following run absent an override; a
  journaled override wins and renders as an override.
- Scores/nomination invariance across every status/call fixture; architecture guard green.
- Single-strategy report byte-identical.

Do not run tests concurrently with another agent's solution-wide test run. At handoff,
`dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass in one
coordinated session.

## Acceptance criteria

- [ ] Every rendered strategy carries computed evidence status with its numbers; no computed verdicts, no
      bare badges, nothing hidden.
- [ ] Exactly one Lead exists; the initial calls ship with the spec (v11 Lead, default DoNotLead, others
      Trial), journaled with basis and review dates.
- [ ] Prominence follows the call immediately; scores, series, snapshots and news-risk nomination are
      provably untouched; the config primary is unchanged.
- [ ] Gate verdicts produce default calls; human overrides are journaled; superseded calls record their
      outcomes.
- [ ] The maintainer's guidance and the "records wrong calls" sentence appear verbatim in the rendered
      call banner's linked documentation.
- [ ] Build and coordinated tests green.

# Task: Strategy evidence status — outcomes govern prominence, humans govern lifecycle

## Overview

Ten strategies render as "leaders" with no indication of which have any outcome evidence at all. The current
facts: nine arms have no mature efficacy history; the one rankable arm (`default`) shows out-of-sample
ρ −0.05 with an interval spanning zero; the confirmatory AD-15 gate has zero eligible support until
2026-09-29. Nothing in any report distinguishes an arm with evidence from an arm without — the external
review's P0: "outcomes are archived, not fed back into strategy governance."

This slice closes that honestly, under the constraint set this week's failures taught (the maintainer's
design input, verbatim: "don't do something stupid again"):

- **Computed EVIDENCE STATUS, never computed verdicts.** The system derives and displays what is measured —
  it never converts noisy descriptive numbers into pass/fail labels ahead of the precommitted gates.
  (`default`'s oos ρ moved +0.22 → −0.05 as dates accrued; an automatic "Missed" state would have
  whipsawed on noise.)
- **Declared LIFECYCLE, never automated.** Retiring or promoting an arm is a human act, recorded with date
  and reason. "AI assists. Humans decide."
- **Label, never hide.** No arm is suppressed for lacking evidence (omission doctrine); it renders with its
  status. Candidate nomination for diagnostics (the news-risk read) is deliberately NOT restricted by
  status — restricting what Radar reads based on unproven rankings would recreate the muting this arc just
  removed. Status governs how much TRUST a display invites, not what gets investigated.
- **No look-ahead.** Status derives from outcomes and feeds display/journal only; nothing status-derived
  may reach scoring, snapshot content, or candidate scoring. Guarded architecturally, not by convention.
- **Minimal machinery.** No new identity systems, no fingerprints, no state machines beyond one enum and
  one committed journal file.

## Assignment

Worktree: any — sequence after spec 183 (its excess-return leaderboard is the evidence source).
Dependencies: specs 176, 183 merged.
Estimated time: ~1 day.

## 1. Evidence status — computed, closed, and only ever descriptive

Per strategy, derived mechanically each run from artifacts that already exist (leaderboard result,
paired-comparison support counts, configured boundaries):

```text
Accruing        not yet rankable; rendered with the projected first-rankable date when computable
Ranked          in the current leaderboard; rendered WITH its numbers (oos ρ, interval, n), never alone
GatePending     named in a precommitted confirmatory gate whose boundary/support is not yet reached
GatePassed      the precommitted gate evaluated and passed (cannot exist before 2026-09-29)
GateFailed      the precommitted gate evaluated and failed — rendered plainly; retirement stays human
```

Rules:

- A strategy holds `Accruing` or `Ranked` from the descriptive family AND `GatePending`/`GatePassed`/
  `GateFailed` from the confirmatory family where applicable (the paired gate names one arm today) — two
  orthogonal facts, both rendered, never merged into one verdict.
- `Ranked` NEVER renders as a bare badge: the numbers travel with it ("Ranked: oos ρ −0.05, CI −0.31..0.22,
  n=4 dates" invites exactly the right amount of trust; "Ranked ✓" invites the wrong amount).
- An interval spanning zero is stated as "no evidence of discrimination yet" — a factual sentence, not a
  failure verdict, because the descriptive family declared no pass/fail rule.
- Derivation is deterministic from persisted artifacts; a missing/unreadable artifact yields
  `Accruing (evidence unavailable)` — degraded reads degrade the STATUS display, never the strategy.

## 2. Lifecycle — a committed journal, and config as the actuator

- `docs/strategy-lifecycle.md` is the committed, append-only journal: one line per lifecycle event —
  strategy name, action (`declared | retuned-as-new-name | retired`), date, reason, actor. The existing
  history (the spec-149 renames, the spec-154 baseline additions) is backfilled once from git history as
  the journal's opening entries.
- **Retirement IS config removal** (the mechanism spec 141 already implies): remove the entry from
  `Radar:Strategies`, journal the act. Series, snapshots and identity records stay on disk untouched
  (append-only, AD-8); the reports simply stop rendering the arm. No new "retired" runtime state exists —
  a strategy the config no longer names is retired, and the journal says when and why.
- Promotion (changing `Radar:PrimaryStrategy`) is likewise a config act + journal line. This spec builds
  NO automation for either, and none should be proposed without the maintainer asking (standing feedback).

## 3. Rendering — status everywhere a strategy invites trust

- **Live strategy leaders (spec 176)**: each subsection header gains its arm's status inline —
  `narrative-led-v2 · Accruing (first rankable ≈2026-09-01)` / `default (primary research) · Ranked: oos
  ρ −0.05 (CI spans zero — no evidence of discrimination yet)`. Ordering is UNCHANGED (configured order,
  primary first): reordering by noisy descriptive numbers would be an outcome-derived ranking the gates
  have not earned.
- **Spec-150 per-strategy tables**: same status line under each section header.
- **Efficacy leaderboard**: unchanged — it already IS the evidence; the status vocabulary references it.
- The section's honesty line extends: statuses describe accumulated outcome evidence, not quality; an
  Accruing arm is untested, not bad.
- Composes beside (never replaces) spec 176's Purpose labels and, later, spec 185's semantic-read markers.

## 4. Boundaries, guarded structurally

- Status types live in `Radar.Application.Efficacy` (they are derived FROM outcomes); the reporting layer
  consumes them read-only. An architecture test mirroring `EfficacyReadOnlyGuardrailTests` asserts the
  scoring/pipeline closures reference no status/lifecycle type — outcomes govern prominence, never the
  contemporaneous score (look-ahead stays impossible).
- News-risk candidate selection (spec 179 §3) is asserted UNCHANGED by status — a fixture with an
  `Accruing` and a `GateFailed` arm nominates identically to today.
- No fingerprint input, no snapshot field, no formula/rule-set change; the pins do not move.

## 5. Out of scope, recorded not built

- Automated retirement/promotion, status-driven reordering, composite "strategy quality" scores.
- New confirmatory gates or thresholds (AD-15/AD-16 stand as declared).
- Per-strategy cost accounting or scheduling changes.

## Files to inspect

- `src/Radar.Application/Efficacy/Comparison/` (leaderboard/paired artifacts — the status sources)
- `src/Radar.Application/Reporting/WeeklyReportBuilder.cs` / `MarkdownWeeklyReportRenderer.cs` /
  `StrategyReportSection.cs`
- `scripts/run-profiles/default.json` (`PairedPrimaryStrategy` — the gate-named arm)
- `docs/strategy-lifecycle.md` (new; opening entries from git history)
- the spec-176 golden tests (status lines change multi-strategy goldens; single-strategy stays byte-identical)

## Tests

- Status derivation: fixtures for each state incl. the orthogonal descriptive×confirmatory combinations;
  unreadable artifact ⇒ `Accruing (evidence unavailable)`, never a crash or a hidden arm.
- `Ranked` always renders with its numbers; a CI spanning zero renders the no-evidence sentence; no bare
  badges (asserted on renderer output).
- Ordering unchanged under every status combination; no arm suppressed.
- News-risk nomination byte-identical across status fixtures.
- Architecture guard: scoring/pipeline closures reference no status type.
- Single-strategy report remains byte-identical (statuses render only in the multi-strategy sections).

Do not run tests concurrently with another agent's solution-wide test run. At handoff,
`dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` pass in one
coordinated session.

## Acceptance criteria

- [ ] Every rendered strategy carries a computed evidence status with its numbers; no bare badges, no
      computed verdicts, no reordering, no suppression.
- [ ] Lifecycle actions are config acts recorded in the committed journal; no automation exists.
- [ ] Status cannot reach scoring or candidate selection (architecture-tested); no fingerprint moves.
- [ ] The current honest picture is visible in one glance: one arm Ranked-with-caveats, nine Accruing with
      dates, one GatePending until 2026-09-29.
- [ ] Build and coordinated tests green.

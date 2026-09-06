# Task: Two display-truth defects left by specs 209/210 — a 10b5-1 plan filing has no known direction, and the Watch-floor rationale bypasses the report relabels

## Overview

The 2026-09-05 post-merge review of specs 209 (PR #216) and 210 (PR #217) found two P1 display-truth
defects. Both are confirmed against `main` (`f8bc02b`) and against the live weekly report
`data/reports/weekly/radar-weekly-2026-09-05.md` (run `2b6e2fe9`, generated 2026-09-05 17:15Z):

1. **A `plan-10b5-1` filing is presented as a disposition.** `HttpSecForm4Reader` forces every
   transaction in a 10b5-1 plan filing Neutral and deliberately does not read its transaction codes,
   shares or prices (`HttpSecForm4Reader.cs` ~L287, and the `InsiderActivitySummary` doc-comment says so
   itself: "the reader forces every plan transaction Neutral before reading codes/shares/prices"). Radar
   therefore CANNOT know whether a plan filing contains acquisitions or dispositions — yet the model names
   the bucket `PlannedDisposition*` (`InsiderActivitySummary.cs` L26–34) and the renderer prints
   "N planned-disposition filings" (`MarkdownWeeklyReportRenderer.AppendInsiderActivity`, ~L932). That is
   an inference the store does not carry, stated as a measured fact. **Live: 17 rendered insider lines in
   the 2026-09-05 report say "planned-disposition".**

2. **The spec-210 rationale inserts raw `SignalType` enum names, bypassing the presentation relabels.**
   `WeeklyReportActionPolicyV1` builds the floor rationale with `$"{g.Key} ({DescribeSupport(g)})"`
   (`WeeklyReportActionPolicyV1.cs` L151–153) and the renderer appends `Rationale` verbatim. The two
   renderer-owned relabels — spec 167's `GuidanceChange → EarningsTrajectory` and spec 209's
   `InsiderBuying → InsiderActivity` (`MarkdownWeeklyReportRenderer.DisplaySignalType`, L66–72) — are
   private to the renderer, so the policy never sees them. Consequences, all live in the 2026-09-05 report:
   - `GuidanceChange` reappears in **15 of the 19** floored `- Why:` lines (e.g. AGX, ESQ, JOUT, DGII), the
     exact misnomer spec 167 relabelled ("reads as a guidance event that never happened");
   - `InsiderBuying` reappears in Liberty Energy's floor (`- Why: … InsiderBuying (filing 2026-07-30) + …`),
     the exact inverted label spec 209 rewrote on both provenance paths — and it contains the forbidden
     `buy` substring that `MarkdownWeeklyReportRendererTests`/`WeeklyReportActionPolicyV1Tests` guard
     case-insensitively. The guard did not fire because no fixture floors on `InsiderBuying`; the audit
     `docs/cohorts/watch-floor-episodes-2026-09.md` rows 39–40 show the LBRT shape is real.

This slice fixes both. It changes **presentation only**: no signal, score, weight, fingerprint, stored
JSON or accrued file changes; label computation is untouched (only rationale TEXT changes, as in spec 210).

## Assignment

Worktree: any. Dependencies: specs 209 and 210 merged (they are). Use `run-next.ps1 -Spec 211`.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. Name the plan bucket for what the store knows: a 10b5-1 plan filing

Rename the five `PlannedDisposition*` members of `InsiderActivitySummary` to `Plan10b51*`
(`Plan10b51Count`, `Plan10b51FirstFilingDate`, `Plan10b51LastFilingDate`, `Plan10b51SpanDays`,
`Plan10b51UndatedCount`) — the name of the persisted token they mirror
(`InsiderActivityMetadata.Plan10b51 = "plan-10b5-1"`). Update the doc-comment so it states plainly that
the direction of a plan filing's transactions is NOT captured (codes are not read), so the bucket is a
count and a date span and nothing more.

Render the clause as **"N 10b5-1 plan filing(s)"** — via the existing `Plural` helper, so the NWPX shape
becomes byte-exact:

`- Insider activity (Form 4, this window): 11 filings; 11 10b5-1 plan filings across 29 days; transaction value not captured`

The rest of the line (fixed clause order, "transaction value not captured", span rules, "span not
established: N undated") is unchanged. "10b5-1 plan filing" contains none of the forbidden substrings —
keep the existing forbidden-word assertions in `MarkdownWeeklyReportInsiderActivityTests` in force.

Amend the two comments that carry the same misstatement in place (REVERSAL rule — the original claim,
not a sibling): the renderer's spec-209 relabel comment ("a planned disposition stream renders as Neutral
rows of it") and `HttpSecForm4Reader.cs` ~L287 ("a planned sale is not a discretionary signal" → a planned
transaction, whose direction is not read). No behaviour change in the reader.

Rename the corresponding test members/fixtures (`InsiderActivitySummaryTests`,
`MarkdownWeeklyReportInsiderActivityTests`, `WeeklyReportBuilderTests.InsiderActivity`) and re-pin the
NWPX byte-exact line to the new wording.

## 2. One shared display seam for signal-type labels, used by BOTH the renderer and the policy

Extract a `public static class SignalTypeDisplay` in `Radar.Application.Reporting` (reuse-over-copy,
CLAUDE.md): it becomes THE owner of the presentation mapping, and the renderer's private
`DisplaySignalType` / `DisplayProvenanceText` are deleted in favour of it (not kept as a second copy):

- `string Label(SignalType type)` — `GuidanceChange → "EarningsTrajectory"`,
  `InsiderBuying → "InsiderActivity"`, everything else `type.ToString()`. Move the spec 167/209 comment
  block that explains WHY each relabel exists onto this type, so the reasoning lives with the mapping.
- `string RewriteStoredProvenance(string stored)` — the spec-209 compiled whole-token `\bInsiderBuying\b`
  rewrite, semantics unchanged (`NotInsiderBuyingX` is still not rewritten; `GuidanceChange` inside stored
  text still renders byte-verbatim per spec 167's stance).

`WeeklyReportActionPolicyV1` names each floor-support group with `SignalTypeDisplay.Label(g.Key)`. The
`GroupBy(s => s.Type).OrderBy(g => g.Key)` grouping/ordering and the count stay on the stored enum — only
the printed name changes, so the count and the label are byte-identical to today.

Because the rationale CONTRACT changes again (as spec 210 §2 reasoned when it bumped v2 → v3), bump
`WeeklyReportActionPolicyV1.Version` `weekly-report-action-v3 → v4` and update its pin in
`WeeklyReportActionPolicyV1Tests`. Nothing hashes this token into `ScoringConfigVersion` (verify with a
grep and say so in the PR body) — no fingerprint pin moves.

**Tests (the review asked for an LBRT-shaped one specifically):**
- `WeeklyReportActionPolicyV1Tests`: a `FollowingTier.Small` company, trajectory ≥ neutral, opportunity
  below the Watch line, positives `InsiderBuying` (filing, observed 2026-07-30) + `StrategicPartnership`
  (filing, 2026-06-25) — the LBRT episode-39 shape. Assert the label is Watch, the rationale contains
  `InsiderActivity (filing 2026-07-30)`, does NOT contain `InsiderBuying`, and passes the existing
  `ForbiddenWords` loop. A second case with a `GuidanceChange` positive asserts `EarningsTrajectory`
  appears and `GuidanceChange` does not. Add both shapes to the existing forbidden-language Theory's
  matrix as well, so the guard fires on the exact substring that slipped through.
- `SignalTypeDisplayTests`: the two relabels, the pass-through, and the moved `NotInsiderBuyingX` pin.
- A source guard in `MarkdownWeeklyReportRendererTests` (or a small reflection/`grep` test in the same
  style as `EfficacyReadOnlyGuardrailTests`) that the renderer and the policy contain no
  `SignalType`-to-string site other than `SignalTypeDisplay` — the failure mode here was a second,
  private copy of the mapping; make the next one fail at test time.

## 3. Documentation — amend the original claims in place

- `docs/209-insider-flow-truth-and-aggregate-visibility.md` §3 wording ("planned-disposition",
  L53/83/94/98) and `docs/architecture-history.md` spec-209 bullet (~L2753–2766, the byte-exact NWPX
  line): amend IN PLACE to the new name and wording; do not append a contradicting sibling. Add one
  sentence to the 209 bullet recording that spec 211 corrected the name because direction is not captured.
- `docs/architecture-history.md` spec-210 bullet: note that v3's rationale printed STORED enum names and
  that spec 211 routes it through `SignalTypeDisplay` (v4). Append a spec-211 bullet there (NOT to
  CLAUDE.md).
- `docs/cohorts/insider-flow-audit-2026-09.md` L146/155 and `docs/cohorts/watch-floor-episodes-2026-09.md`:
  these are HISTORY (measured 2026-09-05 under the old wording) — leave the measured rows verbatim, add a
  one-line note at the top that the rendered wording changed in spec 211 (`planned-disposition` →
  `10b5-1 plan filing`; rationale type names now relabelled) so a future re-audit that parses `- Why:`
  lines from `data/reports/weekly/*.md` normalises BOTH spellings before keying episodes. The audit's
  episode key already uses the STORED enum names read from signal files, so the key itself is unaffected.

## 4. Live verification (PR body, from the first post-merge full run — descriptive, no gate)

Report, against the run's weekly report:

| measure | before (2026-09-05 report) | after |
| --- | ---: | ---: |
| floored `- Why:` lines containing `\bGuidanceChange\b` | 15 of 19 | expect 0 |
| floored `- Why:` lines containing `\bInsiderBuying\b` | 1 (LBRT) | expect 0 |
| rendered insider lines containing `planned-disposition` | 17 | expect 0 |
| rendered insider lines containing `10b5-1 plan filing` | 0 | expect 17 ± window drift |

If the first post-merge run has not happened by PR time, state so and label the "after" column
UNMEASURED rather than predicting it; the maintainer runs the check after merge.

## Out of scope

- Forward transaction-code capture for plan filings (spec 209 §4, still DEFERRED) — the honest fix for
  "which direction was it" is to capture the codes going forward, not to guess from the plan flag.
- Any change to the stored `SignalType` enum, the `plan-10b5-1` token, signals, scores or fingerprints.
- Relabelling `GuidanceChange` inside STORED provenance text (spec 167's stance stands).

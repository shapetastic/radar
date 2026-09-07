# Task: `default.json`'s `_comment` becomes standing facts that cite their owners; its per-spec history moves to `docs/architecture-history.md`; a guard stops it regrowing; the report cap fails loudly when the universe outgrows it

## Overview

`scripts/run-profiles/default.json` is the canonical record of how the Worker runs. Its `_comment`
is **74,763 characters** (the four overlay profiles' comments are 1.4–3.3 KB each) and carries **125
`radar-scoring-fp-` literals**, per-spec operator procedures, per-run cost arithmetic and universe counts.
It has become a second architecture-history file — but one whose shape is "state a fact as current, then
append a 'see the SPEC N note at the very end' paragraph when it stops being true". That is the exact
failure the CLAUDE.md REVERSAL rule names: an agent greps, finds the first hit, and acts on it.

Measured on 2026-09-07 (commit `fb1335d` amended them in place as a stop-gap; this spec is the durable fix):
six sentences asserted superseded state as current — the live AI-ON stamp (`5ffa8c9e25f0`, six identity
moves stale), `radar-keyword-rules-v6` (v8 since spec 194), baseline-control indices 5-7 and "indices 1-7
untouched" (6-8 / 9-10 since 2026-08-29), a "seven-key" strategy-entry allowlist (eight since spec 212),
the AD-16 first-eligible date 2026-09-26 (pinned 2026-09-29), and "eight entries / 43 companies / 430
scorings" (11 / 102 / 1,122). Three "verify the first post-19x run reports `<pin>`" operator imperatives
were still live for boundaries long since crossed. And one claim had a live consequence: the sentence
promising `ReportMaxItems` headroom "so the NEXT expansion does not silently truncate" did not hold —
specs 199/207 took the universe to 102 past the 90 cap and every strategy section from 2026-09-03 to
2026-09-06 rendered "showing top 90" of 102 (counted, not silent, but 12 scored companies per arm never
reached the reader). The cap is now 120 by the same headroom logic, which is the same promise again.

CLAUDE.md's mechanical rule 1 is explicit: **never duplicate a value that code defines** — cite where it
lives. CLAUDE.md itself was already cured of this (its per-spec bullets moved verbatim to
`docs/architecture-history.md`, and it keeps only standing rules). This spec applies the same cure to the
profile, and makes it stick with a test.

## Assignment

Worktree: any. Dependencies: `fb1335d` on main. Use `run-next.ps1 -Spec 213`.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. Move the history out — verbatim, then link

- Append a new section to `docs/architecture-history.md`:
  `## default.json _comment history (moved verbatim by spec 213, 2026-09-07)` — and move the ENTIRE
  current `_comment` text into it **verbatim, unedited**, including the in-place amendments `fb1335d` made
  (they are part of the record). Split it into the per-spec notes it already consists of (the
  `SPEC nnn (date) —` paragraphs plus the leading spec-119/-133/-148 lineage prose) as sub-bullets, one
  per note, in the ORDER they appear in the file (which is not chronological — say so in one line at the
  top of the section, and do not reorder: the order is itself evidence of how the drift happened).
- Nothing in the moved text is corrected, trimmed or "tidied". A value that is wrong there is history
  that was wrong; the section header says the text is history and that `ScoringConfigFingerprintTests`,
  `ScoreFormulaVersions.cs`, `KeywordSignalExtractor.RuleSetVersion` and the profile itself are the owners
  of anything it quotes.

## 2. Rewrite `_comment` as standing facts — every value cites its owner, no pin literal survives

The new `_comment` is **at most 4,000 characters** (the overlays' order of magnitude) and contains ONLY
facts that are true by construction of THIS file or that name their owner. Required content, in this
order:

1. What the file is (the baseline the scheduled run and `run-radar.ps1` load; overlays carry deltas only;
   machine-specific values and secrets are supplied at runtime — key env var NAME only).
2. How to find the current values this file used to quote:
   - live/unit fingerprint pins → `ScoringConfigFingerprintTests` (three windows, three correct answers,
     never reconcile; reconcile a run against the pair for the window it used);
   - formula set → `ScoreFormulaVersions.All`; extractor rule set → `KeywordSignalExtractor.RuleSetVersion`;
   - strategy count / order / indices → the `Strategies` array in this file ("count it; never trust an
     index quoted in prose");
   - universe size and per-source feed coverage → `data/companies.json`;
   - operating calls, Lead, review dates → `data/strategy-operating-calls.json` + `docs/strategy-lifecycle.md`;
   - per-spec decisions and every historical pin → `docs/architecture-history.md` (§1's section).
3. The standing operator rules that are NOT derivable from code and would be lost otherwise:
   - a NAMED strategy is immutable by convention — retune = new name; editing in place trips
     `StrategyIdentityGuard`, and a guard halt after a deliberate identity move is CORRECT: consciously
     delete/re-record `data/scoring-configs/strategies/{name}.json` (git-ignored, never fabricated);
   - an overlay that changes WEIGHTS must re-point `Strategies[0].ScoringProfile` — overlaying
     `Radar:Scoring:Profile` alone FAILS OPEN (`DefaultRunProfileTests` is the guard);
   - the three `baseline-` comparators must be IDENTICAL in a baseline run and an experiment;
   - `ReportMaxItems`, `Ai:MaxFilingsPerRun`, `Labels` are operational/display parameters — not
     fingerprint inputs (with §4's rule for the cap);
   - the 2026-09-29 precommitted claim boundary is immutable by convention (owner:
     `Radar:Efficacy:Comparison` below and `appsettings.json`).
4. One line per non-default VALUE set in this file saying WHY it is set (the reason, not its history):
   `ReportMaxItems`, `Ai:MaxFilingsPerRun`, `Prices.Enabled`, `Efficacy.Enabled`,
   `Ai:Filings:PersistReadDebug`, `News:RecencyWindowDays`, the v11 `Labels`, each ≤ 2 sentences, no
   numbers other than the value itself.

Explicitly NOT in the new comment: any `radar-scoring-fp-` literal, any "N companies / N scorings / N
entries / indices a-b" count, any "verify the first run reports …" imperative, any per-spec narrative,
any measured distribution. If a fact needs a number that code owns, cite the owner instead.

## 3. A guard so it cannot regrow

`DefaultRunProfileTests` gains (or a new `RunProfileCommentGuardTests` beside it):

- for EVERY `scripts/run-profiles/*.json`: `_comment` (and any `_comment*` sibling anywhere in the file)
  contains no match of `radar-scoring-fp-[0-9a-f]{12}`, no match of `verify the first .* run reports`,
  and is ≤ 4,000 characters for `default.json` / ≤ 6,000 for an overlay (assert the limit with a message
  that names this spec and says where history goes);
- positive controls: a fixture string containing a pin literal FAILS the same predicate (the guard must
  be proven to bite, not just to pass).

## 4. The report cap fails loudly instead of promising headroom

`WeeklyReportOptions.MaxItems` bounds the rendered entries (`WeeklyReportBuilder` ~L277–280, the
"showing top N" line). Replace the headroom promise with a rule:

- at startup, after `data/companies.json` is loaded, if `ReportMaxItems < companies.Count` the Worker
  FAILS with a message naming both numbers and the config key — the `StrategyIdentityGuard` shape: a
  universe expansion must consciously raise the cap in the same change, and the spec that expands the
  universe gains that as an acceptance line (add it to CLAUDE.md's universe bullet in one clause);
- `ReportMaxItems` stays a config value (120 today) — it is NOT derived from the universe, because a cap
  that silently tracks the universe is no cap; the "showing top N of M" line stays as the counted truth;
- test: a fixture with 5 companies and `ReportMaxItems = 4` fails startup naming `Radar:ReportMaxItems`,
  4 and 5; with 5/5 it starts.

## 5. Docs

- `docs/architecture-history.md`: §1's section, plus a spec-213 bullet (what moved, the guard, the cap
  rule).
- `CLAUDE.md`: amend the "Running the app live" paragraph's `default.json` sentence in place to say the
  profile's comment is standing facts only and history lives in `docs/architecture-history.md`; add the
  cap clause to the universe bullet. Nothing else.
- `README.md` "Key files & directories": if it describes `default.json`'s comment as documentation of the
  run history, amend in place.

## Non-goals

- Changing any configured VALUE other than none — this is a docs/guard slice; `ReportMaxItems` stays 120
  (raised by `fb1335d`); no strategy, weight, formula, fingerprint or pin moves ("spec 213 moved nothing").
- Editing the overlay profiles' comments beyond what §3's guard requires (they already pass it —
  measured 2026-09-07: 0 pin literals each).
- Deriving the cap from the universe (rejected above).

## Acceptance criteria

- [ ] `docs/architecture-history.md` carries the ENTIRE pre-213 `_comment` verbatim under a dated
      section, split per note in file order, with the "history, owners are …" header line.
- [ ] `default.json`'s `_comment` is ≤ 4,000 chars, contains no `radar-scoring-fp-` literal, no count of
      companies/scorings/entries/indices, no "verify the first run" imperative, and every value it names
      cites its owner; the seven non-default values each have a one-line reason.
- [ ] The guard test covers every `scripts/run-profiles/*.json` and every `_comment*` key, with a
      positive control that proves it bites.
- [ ] Startup fails when `ReportMaxItems < companies.Count`, naming key and both numbers; the fixture pair
      (4/5 fails, 5/5 starts) passes; CLAUDE.md's universe bullet carries the clause.
- [ ] `DefaultRunProfileTests`, `BaselineStrategyWiringTests`, `ScoringConfigFingerprintTests` pass
      unchanged; the PR body says "spec 213 moved nothing".
- [ ] `dotnet build` / full suite / `git diff --check` clean; actual dispatch→PR time in the PR body.

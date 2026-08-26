# Task: Record what is discarded — failed writes, superseded signals, collapsed duplicates

## Overview

A codebase scan on 2026-08-26 for silent-discard patterns found the repo is generally disciplined: most
skips carry a named reason and a counter (`NewsRiskClaimValidator`'s eight typed drop reasons,
`AttentionArrivalScreenEvaluator`'s typed exclusions, `FileRawEvidenceStore`'s separate
`duplicatesCollapsed` / `unreadable` counters, `PairedComparisonHarness`'s typed date-drop reasons). Three
genuine gaps remain, all of the same family this repo has been closing all week: **an outcome is discarded
rather than recorded.**

This slice adds accounting only. **It changes no scoring behaviour, no formula, no rule set, no cohort key
and no cache key; no fingerprint pin moves.**

## Assignment

Worktree: any

Dependencies: specs 187–192 merged. All existing artifacts immutable; nothing rewritten or backfilled.

Estimated time: ~1 day.

## 1. A failed durable write must never read as success (the one that can lose real data)

`GracefulFileWriter.TryWriteAllTextAsync` catches `IOException`/`UnauthorizedAccessException`, logs one
Warning and returns `false`. **Every caller uses that return value only to gate a success log.** In
`FileSignalStore.WriteAsync` the code then unconditionally runs `_byId[signal.Id] = signal;` and returns the
path, so a signal that never reached disk is in the in-process index, reports a path, and is counted by the
pipeline as stored — `CollectionPass` explicitly comments that "the store swallows disk errors, so this must
not change any counter". `FileScoreSnapshotStore` has the same shape for score snapshots. The next run's
`ReadApprovedInWindowAsync` (the previous-window velocity input) simply will not see it, and **nothing
anywhere records that it should have.** On Windows, a transient antivirus file lock makes this reachable, not
theoretical.

**Keep the graceful degradation — a disk hiccup must still not crash a run.** Keep the in-memory copy too, so
the current run completes on what it has. Change only the *claim*:

- Give the durable stores a typed write outcome instead of a discarded `bool`, following the shape the
  news-observation archive already uses (`NewsObservationWriteOutcome` + `Written`/`CrossRunDeduped`/`Failed`
  counters + `CaptureProven: failed == 0`). That is the correct pattern and it is already in this repo — reuse
  its shape rather than inventing a second one.
- Thread the failure to the pipeline and **count it**. The run record gains trailing, nullable
  `SignalsNotPersisted` and `ScoreSnapshotsNotPersisted` (null = pre-193, never a fabricated `0`).
- Emit **one aggregated Warning per store per run** carrying the count and what it means, following spec 145's
  aggregation precedent — not one line per failure.
- A run with any non-zero count must say so in its summary log line. Do not fail the run.
- The in-memory index entry stays, but the run must no longer *report* that signal or snapshot as durably
  stored: correct the counters and the `CollectionPass` comment that currently sanctions the false claim.

Explicitly out of scope: retrying a failed write, transactional/atomic write semantics, and any change to
`GracefulFileWriter`'s catch set.

## 2. `GuidanceChangeSupersede` must account for what it removes

`ScoringEngine` calls `GuidanceChangeSupersede.Apply` twice — line ~347 (current window, feeding contributions
and evidence links) and ~387 (previous window, feeding velocity activity). `ApplyCore` returns only the
filtered list: no count, no log, no contribution reason. It is the **only** signal-removal step in that method
with no trace, sitting between two that do account — the dropped-evidence path above it (aggregated Warning
per company) and `MediaAttentionCollapse` below it, whose collapsed count is surfaced on its contribution
reason.

This matters more than it looks: spec 173 measured that **4 of the top 10 companies by Opportunity rest on a
results-only `GuidanceChange`**, so silently superseding `GuidanceChange` signals removes exactly the signal
type the ranking is most sensitive to.

- Return a result carrying the survivors **and** the superseded count, mirroring `MediaCollapseResult`
  (`Survivors` + `CollapsedCounts`) rather than inventing a different shape. Reuse over copy.
- Surface the count where the media collapse surfaces its own: on the contribution reason when non-zero, and
  in the per-company scoring log at the existing level.
- **Which signals are removed must not change.** Assert byte-identical `ScoreComponents`, explanation and
  ordered evidence-link chain against the current behaviour on a fixture exercising a real supersede — the
  spec-153 pinning approach. Accounting is added; the filter is untouched.

## 3. Count the syndication that duplicate-headline collapse discards

`NewsRiskInputBundle` collapses exact duplicate normalized headlines, newest copy surviving. The count of
collapsed copies is **not** recorded, so a company with 40 syndicated copies of one story is indistinguishable
from one with a single article — and syndication breadth is itself a presence measurement. The code comment
claims "publisher diversity and exact ids stay visible on the surviving article", which is **not accurate**:
only the surviving article's own `Publisher` survives. Two lines later the *cap* drop is deliberately counted
(enumeration continues past the cap specifically so it can be reported), so this omission reads as an
oversight rather than a decision.

- Add trailing `SyndicatedDuplicateCount` and `SyndicatedDistinctPublisherCount` to `NewsRiskInputBundle` —
  how many copies collapsed, and across how many distinct publishers.
- **Neither is a `BundleHash` input.** The record's own doc comment already establishes that precedent for
  `QualifyingArticleCount` ("the hash stays over the supplied articles only, so the assessment cache key does
  not move"). The assessment cache key must not move, and no cohort forks.
- `Completeness` keeps its exact current meaning — `Capped` is about the bundle bound, and a dedupe collapse
  is **not** a cap drop (spec 182 §2). Do not fold the new counts into it.
- Correct the inaccurate comment to say what actually survives.

## 4. Tests

- A store write failure (injected via a failing writer) yields: the run completes, the in-memory read still
  returns the item, the run record's not-persisted count is 1, exactly one aggregated Warning is emitted, and
  the run does **not** report it as durably stored.
- A successful run reports zero not-persisted and is otherwise **byte-identical** to today, including the
  summary log line.
- A pre-193 run record hydrates with both new counts null, never 0.
- Supersede: on a fixture with a real supersede, components/explanation/link chain are byte-identical to the
  pre-193 behaviour, and the superseded count appears on the contribution reason; a fixture with no supersede
  emits no count and is byte-unchanged.
- Bundle: N syndicated copies of one headline across M publishers yield `SyndicatedDuplicateCount = N-1` and
  `SyndicatedDistinctPublisherCount = M`; `BundleHash` is **unchanged** versus pre-193 for the same surviving
  articles (pin it); `Completeness` is unaffected by collapses.
- Mutation proof for each of the three: reverting the production change turns the new test red.

## 5. Out of scope

- Retrying, queueing or making durable writes transactional.
- Changing which signals are superseded, which headlines collapse, or any admission rule.
- Any score, rank, label, strategy, formula, rule-set, marker, cohort key or fingerprint change.
- The five lower-ranked scan findings, recorded here for a later slice: MediaAttention on non-`NewsArticle`
  evidence silently skipped from the publisher count; `GdeltNewsCollector` returning no `CompanyCoverage` so
  its confirmed local truncation is invisible (and the legacy `?? false` mirror reads it as "no truncation");
  a crashed typing/judgment step leaving the report permanently `judgment-pending` with no vocabulary token
  for "the step threw"; a null seed entry skipped with no log, silently shrinking the declared feed baseline;
  and `CollectedEvidenceMapper` collapsing an *unrecognised* evidence quality into *missing* (both landing on
  `Unknown`, weight 0.40, persisted forever).

## Acceptance criteria

- [ ] A failed durable write is counted, aggregated into one Warning per store per run, surfaced in the run
      record and the summary line, and is never reported as stored. The run still does not crash.
- [ ] `GuidanceChangeSupersede` returns and surfaces its superseded count via a `MediaCollapseResult`-shaped
      result; the set of removed signals and every scoring output are byte-identical.
- [ ] `NewsRiskInputBundle` records collapsed-duplicate and distinct-publisher counts; `BundleHash` and
      `Completeness` are unchanged and pinned; no cohort forks.
- [ ] All new record/run fields are trailing and nullable; pre-193 artifacts hydrate as "not recorded", never
      as a fabricated zero or false.
- [ ] No score, rank, label, strategy, formula, rule-set, cohort key or fingerprint moves; all four spec-148
      pins stand and `ScoringConfigFingerprintTests` is untouched.
- [ ] `dotnet build Radar.sln -c Release` and the full test suite pass; `git diff --check` clean.

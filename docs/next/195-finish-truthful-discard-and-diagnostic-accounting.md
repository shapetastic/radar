# Task: Finish truthful discard and diagnostic accounting

## Overview

The post-193 code review found three real follow-through defects. None changes a judgment or score, and none
should delay spec 194's correction of the live news-direction scoring seam:

1. spec 193 added pass-level failed-write Warnings but left `GracefulFileWriter`'s per-file Warning in place,
   so a bad disk still produces N detail lines plus the aggregate;
2. spec 193 computes syndication breadth into `NewsRiskInputBundle`, but the bundle is transient and no
   production consumer reads either value; and
3. spec 190 calls `UnadmittedRelevantTailItemCount` company-level unique, but dedupes inside each feed and then
   sums the per-feed counts.

This slice completes those promises without touching scoring. It is deliberately separate from spec 194:
warning plumbing, live-artifact provenance and collector diagnostics have no causal dependency on how a
judgment becomes a signal.

## Assignment

Worktree: any

Dependencies: specs 190 and 193 merged. Spec 194 is not a code dependency; if both are pending, implement 194
first because it corrects live scores.

Estimated time: ~1–2 days.

## 1. Make spec 193's warning aggregation real

`GracefulFileWriter.TryWriteAllTextAsync` catches `IOException`/`UnauthorizedAccessException`, logs a Warning
and returns `false`. `FileSignalStore` and `FileScoreSnapshotStore` call it once per item; their pass owners now
also emit spec 193's aggregated Warning. Therefore N failed writes create N per-file Warnings plus one
aggregate — the aggregate was added, not substituted.

Keep the writer's catch set and graceful return behaviour unchanged. Add an explicit failure-log mode (typed
enum/options, not an ambiguous boolean):

- `Immediate` is the default and preserves every existing caller's behaviour;
- `CallerAggregates` suppresses only the writer's per-file Warning because the caller owns a later aggregate.

`FileSignalStore` and `FileScoreSnapshotStore` use `CallerAggregates` on the pipeline batch paths. Their pass
owners emit the existing one Warning per store per run. Remove the false "see per-write Warnings above"
wording. Other `GracefulFileWriter` consumers remain `Immediate` unless they already have a proven aggregate;
do not silently suppress failures elsewhere.

Detailed attempted paths may be logged at Debug in bounded form. Do not put N exception stack traces back at
Warning. The runner's existing one whole-run shortfall Warning may remain: it summarizes the run across
stores and is not another per-file detail line.

Tests must exercise the **real writer/store logger plus the pass logger**, not only a fake store returning
`Failed`:

- N failed signal writes produce zero per-file Warnings from
  `FileSignalStore`/`GracefulFileWriter` and exactly one aggregated store Warning from `CollectionPass`;
- N failed snapshot writes produce the corresponding zero-plus-one result from
  `FileScoreSnapshotStore`/`ScoringPass`;
- the existing durable-failure counters and current-run in-memory visibility remain exact; and
- an unrelated caller using the default `Immediate` mode still emits its one failure Warning.

## 2. Persist and render the syndication measurement

`NewsRiskInputBundleBuilder` correctly computes:

- `SyndicatedDuplicateCount`; and
- `SyndicatedDistinctPublisherCount`.

But `rg` finds no production reader beyond construction. The bundle is transient, so after the pass nothing
distinguishes forty syndicated copies from one article. Merely carrying the fields on an object that is then
discarded does not satisfy "record what is discarded."

These counts describe the **current run's enumeration before collapse**. Do not put them only on a cached
assessment record: the surviving supplied articles (and therefore `BundleHash`) may remain identical while
syndication breadth changes, and assessment reuse would then display an old run's breadth as current.

Thread both freshly computed values onto `NewsRiskLiveCompany` as trailing nullable fields:

- `SyndicatedDuplicateCount`;
- `SyndicatedDistinctPublisherCount`.

Bump the live document schema `news-risk-live-v3` → `news-risk-live-v4`. Every new company row writes measured
integers, including honest zero; an accrued v3 document hydrates null = not recorded, never zero. The JSON
always carries the measured values. Render:

- a compact per-company syndication line when duplicate count is non-zero; and
- an artifact-level total of collapsed copies plus distinct syndicated publishers, labelled as current-run
  pre-collapse enumeration provenance.

Define the artifact-level distinct-publisher total from the underlying current company bundles, not by
summing per-company counts if the intended question is global uniqueness. If the artifact instead reports a
company-publisher incidence sum, name it that way. Do not label a sum as globally distinct.

Neither value enters `BundleHash`, an assessment id, cohort key, completeness, model request, scoring or
fingerprint. The assessment cache remains byte-identical; current-run enumeration provenance sits beside the
possibly cached reader result and is not a reason to call the model again.

## 3. Make diagnostic-tail uniqueness company-wide

Spec 190 creates a fresh `tailSeenUrls` for each successful feed and then
`CompanyCoverageAccumulator.RecordFeedSuccess` adds that feed's integer to a company total. Two concrete
overcounts follow:

- the same relevant tail URL returned by two feeds counts twice; and
- a URL in feed A's tail counts as unadmitted even when feed B admitted that URL in its retained prefix.

Replace the integer sum with company-scoped sets using the existing case-insensitive URL equality:

- `observedPrefixUrls`: every URL in every successful feed's retained reader prefix;
- `relevantTailUrls`: every company-relevant URL observed in every diagnostic tail.

At `ToCoverage`, `UnadmittedRelevantTailItemCount` is
`relevantTailUrls EXCEPT observedPrefixUrls`, counted once across the company. The result is independent of
feed iteration order.

Preserve spec 190's exact diagnostic population: use the same URL strings/comparer and the same relevance
predicate. Do not introduce URL canonicalization, tracking-query stripping or a wider semantic duplicate
rule in this slice.

This remains diagnostic-only. Neither set may be shared with or mutate the evidence/observation admission
loop. Do not raise a reader/collector cap or admit one additional item. Pin the evidence and observation
outputs record-for-record on fixtures where only the diagnostic tail differs.

## 4. Tests and mutation proofs

At minimum:

1. N real signal-file failures: zero per-file Warnings, one `CollectionPass` store Warning, exact
   `SignalsNotPersisted`, current-run in-memory signal retained.
2. N real snapshot-file failures: zero per-file Warnings, one `ScoringPass` store Warning, exact
   `ScoreSnapshotsNotPersisted`, current-run score retained.
3. A default-`Immediate` `GracefulFileWriter` caller still logs its failure; reverting the new mode at either
   batch store turns the aggregation test red.
4. N syndicated copies of one headline across M publishers produce N−1/M in both the transient bundle and
   the v4 live JSON; Markdown names the same values.
5. A v3 live document hydrates both fields as null. A new measured zero stays zero.
6. Changing only syndication breadth leaves surviving articles, `BundleHash`, assessment cache choice,
   cohort/model request and completeness unchanged while the v4 current-run fields change.
7. One tail URL duplicated across two feeds counts once.
8. A URL in one feed's tail and another feed's retained prefix counts zero regardless of feed order.
9. Tail diagnostics do not change admitted evidence, observation candidates, collection counters or any
   scoring input.

Use constructed fixtures, never mutable live artifacts. Include a mutation proof for each section: restoring
the current per-write Warning, dropping the live fields, or restoring the per-feed integer sum must turn its
new regression red.

## 5. Compatibility and explicit non-goals

- No score, rank, direction, strength, formula, strategy, label, signal, evidence admission, cohort key,
  cache key or fingerprint changes.
- No historical artifact is rewritten or backfilled. v3 live artifacts remain v3/null-on-hydration.
- No write retries, queue, transaction, atomicity or wider exception catch.
- No syndication input to the judge/model and no attempt to interpret syndication as positive or negative.
- No collector limit increase and no URL-normalization policy change.
- No spec-194 judgment-signal, scoring-identity, media-collapse or typing-completeness work. Keep the slices
  independently reviewable.

## Acceptance criteria

- [ ] N failed signal/snapshot writes emit zero per-file Warnings and one aggregate Warning per affected store
      per run; durable-failure counters and graceful current-run behaviour remain exact.
- [ ] Other `GracefulFileWriter` callers retain immediate failure logging by default; the catch set is
      unchanged.
- [ ] Syndication counts survive as measured current-run provenance in `news-risk-live-v4` JSON/Markdown;
      accrued v3 artifacts hydrate null.
- [ ] Syndication does not change supplied articles, hash, cache, cohort, completeness, model input or score.
- [ ] Diagnostic-tail URLs are unique across the whole company; tail-vs-prefix overlap is not reported as
      unadmitted and feed order cannot change the count.
- [ ] Tail measurement remains observational only: evidence, observations, collection limits and scoring
      inputs are unchanged.
- [ ] No historical artifact is deleted, rewritten or backfilled; no scoring fingerprint or strategy identity
      record moves.
- [ ] `dotnet build Radar.sln -c Release`, the full test suite and `git diff --check` pass; on Windows,
      `scripts/run-radar.ps1 -Profile default -WhatIf` remains byte-identical because this spec adds no config.

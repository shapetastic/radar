# Task: Close sibling drift — every store reports its write, every helper has one home, every boundary claim matches its guard

## Overview

The `radar-architecture-reviewer` sweep of `main` @ `cdd718b` (2026-08-29, first since `b9b3f65`/spec 148)
found the core invariants intact — layering, provider containment, `TimeProvider` everywhere, pins matching
`ScoringConfigFingerprintTests`, formula set matching `ScoreFormulaVersions.cs` — and ONE systemic failure
in three costumes: **a fix landed once and its siblings kept the old shape.**

1. Spec 193 closed "a failed durable write reported as stored" for signals and scores. **Eight sibling stores
   still return the path regardless of the write outcome**, and two log an unconditional "artifact written"
   Information line after a write that may have failed.
2. Spec 186 deferred a sweep of the hand-copied canonical-string→SHA-256 idiom (5 copies then). **It is now
   11.** The two news collectors carry a byte-equivalent `IsRelevant` + `NormalizeWhitespace`; spec 200
   hardened and pinned only the newssearch copy, so the GDELT copy will silently miss the next relevance fix.
3. CLAUDE.md states two Application-internal namespace bans in the absolute; the reflection guard checks the
   TYPE GRAPH, which cannot see a `const` reference — and one is crossed today. One of the two doc sentences
   is false outright.

Each is a bounded mechanical change. **Nothing here changes a score, a fingerprint, a cohort key, a
persisted schema or a collection decision**; the deliverable is that the next slice inherits the right
shape instead of re-deciding it.

## Assignment

Worktree: any

Dependencies: spec 200 Phase A merged (it is — PR #205, `cdd718b`). **Do not wait for spec 200 Phase B**;
they are independent. If `run-next.ps1` selects spec 200 first because it still sits in `docs/next/`,
dispatch this one explicitly.

Estimated implementation time: UNMEASURED (the last three specs' estimates ran 5–15× long; see the
spec-200 record). Record the actual dispatch→PR time in the PR body.

## 1. Every durable write reports its outcome; no success line without a success

The established shape is `DurableWriteResult` (spec 193: `ISignalFileStore.WriteAsync`,
`IScoreSnapshotFileStore.WriteAsync`). Bring these to it, or fold the outcome into the record that
already carries the claim:

| store | today | required |
| --- | --- | --- |
| `FilePipelineRunStore.cs:49-54` | returns path unconditionally | return `DurableWriteResult`; the Worker logs the failure ONCE (spec-145 aggregation) |
| `FileReportWriter.cs:45-51` | returns path unconditionally | same; a failed report write must not leave `ReportPath` populated on the run record |
| `FileScoringConfigStore.cs:95-101` and `:162-169` | returns path unconditionally | same — a snapshot must not stamp a fingerprint whose content-addressed file never landed (the hole spec 148 Part B closed for replay), and the `StrategyIdentityGuard` record write must report failure |
| `FilePriceHistoryStore.cs:72-80` | returns path unconditionally | same |
| `FileEfficacyArtifactStore.cs:54-64` | returns path unconditionally | same |
| `FileNewsRiskArtifactStore.cs:45-55` (`:55`, `:85`) | discards the bool, then `LogInformation("… artifact written")` | gate the Information line on the bool; log the failure path instead |
| `FileNewsTypingArtifactStore.cs:48-58` (`:58`) | same | same |

Rules: the graceful degradation, the catch set and `GracefulFileWriter`'s per-instance log mode (spec 195)
are untouched — only the CLAIM changes, exactly as spec 193 put it. A failed write is COUNTED on the run
record where one exists (trailing + nullable, `null` = this pass did no such write, never `0`). The five
`Task<bool>` News/NewsRisk/NewsTyping record stores (`INewsJudgmentStore`, `INewsRiskAssessmentStore`,
`IFactFamilySnapshotStore`, `INewsTypingStore`, `INewsObservationArchive.WriteBatchAsync`) are NOT
converted in this slice — verify every caller CHECKS the bool (spec 187 did this for typing) and list any
that does not as a finding; converting them is the next touch of each store.

Test: for each converted store, a failing writer (the existing `GracefulFileWriter` test double) yields a
non-success result and NO Information success line (assert on a captured logger), and the run record
carries the count. Mutation: revert one store's gate and its test goes red.

## 2. One home per primitive

- **SHA-256**: route all 11 copies through `Radar.Application.Identity.CanonicalHash` — the five spec-186
  deferred sites (`BenchmarkUniverse.cs:74`, `NewsObservationIdentity.cs:72`, `NewsRiskInputBundle.cs:283`,
  `NewsEventTaxonomy.cs:55`, `ScoringConfigFingerprint.cs:135`) and the six newer Infrastructure ones
  (`ChatNewsJudgmentAnalyzer.cs:230`, `ChatNewsRiskAnalyzer.cs:166`, `ChatNewsTypingExtractor.cs:158`,
  `HttpNewsArticleContentReader.cs:106,287`, `InfrastructureServiceCollectionExtensions.cs:3196`).
  Truncations (8/12/16 chars) stay at the callers. **The proof is the existing hash pins** — every pinned
  fingerprint, taxonomy hash, benchmark-universe hash and observation id must be byte-identical, and
  `ScoringConfigFingerprintTests` must be UNTOUCHED. If any pin moves, the refactor is wrong; do not
  update a pin.
- **Feed relevance**: extract `FeedTargetRelevance.IsRelevant(title, target, preNormalize?)` +
  `NormalizeWhitespace` into `Radar.Infrastructure.Sources` (beside `CollectorCompanyHints`), route
  `NewsAttentionCollector.cs:623` and `GdeltNewsCollector.cs:206` through it, with the newssearch
  `StripPublisherSuffix` as the per-caller `preNormalize` hook (CLAUDE.md: share the core, keep the
  divergent edge per caller). Move spec 200's six adversarial headline pins onto the shared type and ADD
  the same six through the GDELT collector's public surface. The third whitespace collapser
  (`EarningsComparabilityScan.cs:117`) routes through the same helper. Behaviour is byte-identical for both
  collectors — assert with the existing collector tests unmodified.

## 3. Boundary claims match their guard

- `LegacyNewsInheritanceNeutralization.cs:171` reads `NewsTrajectorySignalRules.BaseStrength`
  (`internal const`, `Radar.Application.News`) from `Radar.Application.Scoring`. The guard in
  `NewsObservationArchitectureGuardTests.cs:33-72` cannot see it (a `const` is inlined). Decide it rather
  than paper it: move `BaseStrength`, `MaxFindingContribution`, `CompleteTypingBonus`, `Novelty`,
  `Confidence` next to `NewsDirectionalSignalMetadata` in `Radar.Application.SignalExtraction` (which
  Scoring already references legitimately), leaving `NewsTrajectorySignalRules` as the mapping that reads
  them. The `news=…;` identity segment encodes these BY VALUE (spec 194 §2) so relocation moves no pin —
  assert it.
- Add a SOURCE-level check to the guard (a `using Radar.Application.News` / `.NewsRisk` scan over
  `src/Radar.Application/Scoring/**`) so the ban is total rather than type-graph-only.
- Amend IN PLACE (reversal rule): `CLAUDE.md:2246` "`Radar.Application.News` still takes no dependency on
  `Radar.Application.Scoring`" is FALSE (`NewsJudgmentScoringIdentityFactory.cs:2`,
  `NewsRiskCandidateSelector.cs`) — replace with the claim that is true ("the coverage summary type is
  primitive-only"). `CLAUDE.md:1896` becomes "no reference of any kind, source or type-graph — asserted
  both ways".
- `CLAUDE.md:205` "PostgreSQL, Dapper" — already amended in the commit that added this spec; verify it
  stayed true.

## 4. Two `?? 0` sites

`NewsRiskLiveArtifactRenderer.cs:160` (`SyndicatedDistinctPublisherCount ?? 0` renders "across 0 distinct
publisher(s)") and `NewsRiskClaimValidator.cs:248` (`Claims?.Count ?? 0` reads a missing array as zero
claims). Benign today (both construction sites set the pair together) and exactly the shape spec 193's
Copilot catch named. Render "not recorded" / fail validation on the missing array; the `:193-194` sums
are over a `measured` filter and stay.

## 5. Non-goals

No score, weight, formula, rule set, cohort key, prompt, schema, collection rule or fingerprint change; all
six pins unchanged; no store schema change; no split of `InfrastructureServiceCollectionExtensions.cs`
(3,925 lines — noted, its own slice if ever); no test renaming sweep (175 bare names, cosmetic).

## Tests and verification

- Every hash pin and fingerprint pin byte-identical; `ScoringConfigFingerprintTests` untouched (`git diff`).
- Failing-writer tests for all seven converted stores; captured-logger assertion that no success line is
  emitted on failure.
- Six relevance pins pass through BOTH collectors' public surfaces.
- Source-level boundary guard green, and mutation-proven (re-add the `using` → red).
- `dotnet build Radar.sln -c Release`, full suite, `git diff --check`.
- PR body records the actual dispatch→PR wall-clock.

## Acceptance criteria

- [ ] No store in `src/` returns a path or logs "written" after a write it did not confirm.
- [ ] `CanonicalHash` is the only SHA-256 call site in Application and Infrastructure (excluding the
      audit consoles); all pins unchanged.
- [ ] `FeedTargetRelevance` is the one relevance predicate; both collectors pinned on the same headlines.
- [ ] Scoring→News is banned at source level and the ban holds; the three CLAUDE.md sentences are true.
- [ ] The two `?? 0` sites distinguish missing from zero.

# Task: Close the last known durability truth gaps

## Overview

Specs 193/195/201/202 made most graceful file-store failures visible and stopped forward scoring beneath an
unpersisted config. A post-merge sweep found four remaining places where the durable receipt can still say
more than the disk proves:

1. `ReplayRunner` warns when `IScoringConfigStore.WriteIfNewAsync` fails, then writes every replay snapshot
   beneath the undereferenceable stamp anyway — the exact condition spec 148 said replay closed.
2. `ScoringPassResult.Strategies` returns every configured name even when spec 202 skipped one for an
   unpersisted config, so `PipelineRunRecord.Strategies` claims that a strategy scored when it did not.
3. `CollectionPass` discards `IRawEvidenceStore.WriteIfNewAsync`'s boolean. `false` means either healthy
   dedupe or real disk failure, so neither counting it nor ignoring it is truthful. A failed raw write can
   also be followed by a durable signal that references evidence absent from the accrued store.
4. The news-risk shadow counts a failed assessment-store write in memory, but the rendered live artifact's
   `NewsRiskLiveReaderResult` still presents the `AssessmentId` with no per-row durable/not-durable marker.
   A later reader cannot tell whether that id dereferences.

This is a provenance/durability slice only. It changes no business score, strategy, model prompt, price
outcome or efficacy cohort.

## Assignment

Worktree: any. Dependencies: specs 202 and 204 merged; dispatch after spec 205 to avoid overlapping review
on `CollectionPass`/read provenance. Independent of spec 200 Phase B. Use `run-next.ps1 -Spec 206` while
spec 200 is waiting on its measurement.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. Replay uses the same config-durability precondition as forward scoring

Extract or reuse one small predicate for the meaning already established by spec 202:
`Written` and `AlreadyAvailable` mean the effective config is durable; `Failed` means **do not write a
snapshot carrying its fingerprint**.

In `ReplayRunner`, a failed config write skips that strategy's entire as-of × company loop while every other
strategy continues. Emit one aggregated Warning naming every skipped strategy and stating that no replay
snapshot was written for it. Deliberately correct `ReplayResult`'s existing count meaning and extend its
receipt:

- `Strategies` changes from the configured/attempted count to strategies actually executed; update its XML
  contract and every caller/test that reads it rather than presenting this as a purely additive change;
- trailing `StrategiesSkippedForUnpersistedConfig` names the skipped strategies (`null` = none, never an
  empty list pretending to be recorded history);
- `SnapshotsWritten == AsOfPoints × Strategies × companies` remains true for a completed run.

Do not create empty strategy directories before the precondition succeeds. A targeted test fails config B
only across A/B/C and asserts: only A/C factories/stores are resolved, only A/C snapshots exist, counts and
names say A/C executed and B skipped, one Warning. `AlreadyAvailable` must take the success path. Mutation:
restore today's warn-and-continue and the absence/count assertions fail.

## 2. A forward run records the strategies that actually scored

`ScoringPass` already builds `StrategiesSkippedForUnpersistedConfig`; its returned `Strategies` must be the
ordered complement that actually entered the company loop, not
`strategies.Select(s => s.Definition.Name)` after the fact. Thread that exact list unchanged into all three
consumers (`PipelineRunRecord`, report/run logs and any score-only result assertions). Keep
`PrimaryStrategy` as the configured narrative primary: if it was skipped, that fact is explicit in the skip
list and no primary snapshots exist; do not silently nominate another primary.

The existing A/B/C config-failure test must additionally pin `Strategies == [A, C]` on both
`ScoringPassResult` and the durable run record. Healthy runs stay byte-for-byte/field-for-field unchanged.

## 3. Raw evidence gets a typed outcome and becomes a downstream precondition

Change `IRawEvidenceStore.WriteIfNewAsync` from `Task<bool>` to `Task<DurableWriteResult>` using the shared
outcomes:

- `Written`: this call durably created the immutable raw record;
- `AlreadyAvailable`: the same immutable evidence is already present in the hydrated durable index/store;
- `Failed`: the target did not become a trustworthy durable record.

Amend `DurableWriteOutcome`'s comments/tests that currently say only the scoring-config store may return
`AlreadyAvailable`; raw evidence is the second legitimate insert-if-new caller. Do not add a second outcome
enum beside the shared one.

The collection ordering must enforce the invariant, not merely count its breach:

> No extraction, review or signal persistence may occur for evidence whose raw record is not confirmed
> durable, and a failed item must be naturally retryable on a later collection in the same process.

Use the production fact that `FileRawEvidenceStore` is both `IRawEvidenceStore` and `IEvidenceRepository`
without making `CollectionPass` depend on a concrete type. The implementation may reorder admission/write
or add a narrow combined seam, but it must not leave a failed item stranded in the hydrated in-memory index
where `AddIfNewAsync` suppresses every same-process retry. Existing immutable files are never overwritten;
an existing path that cannot be resolved as the same valid evidence is `Failed`, not
`AlreadyAvailable`.

Add trailing nullable `RawEvidenceNotPersisted` to `CollectionPassResult` and `PipelineRunRecord`:

- `null`: this pass attempted no raw write / old record did not record the axis;
- measured `0`: at least one candidate write was attempted and every item was `Written` or
  `AlreadyAvailable`;
- positive: that many items were `Failed` and therefore excluded from `newEvidence` and all downstream
  signal work.

Combined and collect-only runs record the measured value; score-only leaves it null. Emit one pass-level
Warning with the failed count and suppress/demote the per-file Warning so there is one operator signal, not
N+1. `EvidenceNew` means newly durable raw evidence, not merely admitted to memory. Remove the sole
`RecordedExceptions` entry from `DurableWriteSourceGuardTests`; after this slice the guard has **zero**
discarded value-returning store writes and zero allowlisted exceptions.

Tests cover all three outcomes, an insert race, malformed/conflicting existing path, cancellation, and the
load-bearing sequence: first write fails ⇒ no extraction/signal and count 1; the same evidence on the next
pass in the **same process** writes successfully ⇒ it is extracted once and count 0; a third pass dedupes
without extraction. A persisted signal must always resolve its evidence from a freshly hydrated store.

## 4. The news-risk live row says whether its assessment id is durable

Add trailing nullable `bool? DurablyPersisted = null` to `NewsRiskLiveReaderResult`:

- `true`: this pass's assessment record write returned true;
- `false`: the record was rendered from memory but did not persist, so `AssessmentId` may not dereference;
- `null`: legacy artifact, not recorded — never interpret as true.

Thread the checked write result from `AssessWithReaderAsync` alongside the record; do not re-read or infer
durability from file existence later. The markdown reader row renders a compact `assessment persistence`
state when non-null, with failed state unmistakable. `NewsRiskShadowRunResult.AssessmentsNotPersisted`
remains the aggregate and must equal the count of fresh live reader rows marked false; old/null rows are not
included. Cache reuse still writes a new run-linked assessment record, so its current write outcome supplies
the marker rather than the durability of `ReusedFromAssessmentId`.

Schema version moves `news-risk-live-v5 → v6` because the durable artifact contract changed, but this field
enters no bundle hash, assessment cache/cohort key, judgment input, scoring descriptor or efficacy input.
Old v1–v5 JSON deserializes with null and renders as not recorded.

## 5. Proof of non-effect

- All six scoring fingerprints and every scoring-output/composition pin remain untouched.
- A healthy full-run golden is byte-identical except for the additive run-record raw-evidence field and the
  news-risk v6 persistence marker/schema token.
- Existing replay snapshots for successful strategies are field-for-field identical; the only changed
  failure behavior is that an undereferenceable strategy produces none.
- No evidence, signal, snapshot, assessment or cache file is rewritten/backfilled. All new receipt fields
  heal forward.

## Non-goals

No score/formula/weight/rule-set/strategy/prompt/model/cost/budget change; no historical repair or retry of
old missing files; no fail-fast whole-run policy for one file-store failure; no change to news-risk verdicts,
candidate selection or efficacy evaluation.

## Acceptance criteria

- [ ] Replay and forward scoring write snapshots only for strategies whose effective config is confirmed
      durable; executed/skipped names and counts tell the same story on results and run records.
- [ ] Raw evidence writes have a typed three-state outcome; failure blocks all downstream work, is counted
      once, and retries successfully in the same process without rewriting existing evidence.
- [ ] The durable-write source guard has no exceptions and every value-returning store write is checked.
- [ ] Every new news-risk live reader row records true/false assessment durability; legacy rows remain null;
      aggregate and row-level failure counts reconcile.
- [ ] All six fingerprints and numeric scoring outputs remain unchanged; old durable shapes deserialize.
- [ ] Build, full suite and `git diff --check` clean; actual elapsed time in the PR body.

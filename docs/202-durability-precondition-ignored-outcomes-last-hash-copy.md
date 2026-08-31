# Task: Scoring waits for its config record; two ignored write outcomes; the twelfth hash copy

## Overview

Spec 201 (PR #207, `e209de6`) closed most of the sibling-drift audit. A maintainer review of the spec
against the merged code left three items open. Each is small; together they finish the claim spec 201 made
("no store returns an unconfirmed path, and every caller reads the outcome").

## Assignment

Worktree: any. Dependencies: spec 201 merged. Independent of spec 200 Phase B — dispatch with
`run-next.ps1 -Spec 202`.

Estimated implementation time: UNMEASURED. Record the actual dispatch→PR time in the PR body (spec 201
measured 37 minutes).

## 1. A snapshot is not written under a fingerprint whose config record did not land

`ScoringPass.cs` currently counts a failed `IScoringConfigStore.WriteIfNewAsync` (`ScoringConfigsNotPersisted`)
and **continues scoring the strategy**, so snapshots can carry a `ScoringConfigVersion` that dereferences to
nothing — the hole spec 148 Part B closed for replay, now merely *named* on the forward path. Decided:
**durability precondition**. When the config write for a strategy is not durable:

- skip that strategy for this pass (no snapshot is written under it; every OTHER strategy still scores);
- count it in `ScoringConfigsNotPersisted` (unchanged field) AND record the strategy names in a new
  trailing-nullable `StrategiesSkippedForUnpersistedConfig` (`IReadOnlyList<string>?`, `null` = none skipped
  this pass, never an empty list standing in for "not recorded");
- ONE Warning per pass naming the strategies (spec-145 aggregation), stating that no snapshot was written
  under them and that the next run retries naturally (the store is content-addressed and insert-if-new).

`DurableWriteOutcome` gains **`AlreadyAvailable`** — the content-addressed file already exists, so nothing
was written but the record IS on disk. `FileScoringConfigStore` returns it on the existence-check path instead
of `Succeeded`, and `DurableWriteResult.Written` stays `true` for it (the precondition asks "is the record
durable?", to which both answer yes). No other store returns it. `ScoringOutputStabilityTests` and every
pin are untouched — this changes WHEN a snapshot is written, never WHAT.

Test: a config store double that fails for strategy B only ⇒ A's and C's snapshots are written, B's are not,
the run record names B, one Warning. Mutation: revert the skip and the B-snapshot assertion goes red.

## 2. Two `Task<bool>` fixes, plus one explicitly recorded exception

- `NewsRiskShadowGenerator.cs:465` — `await _assessmentStore.WriteAsync(record, ct)` discards the bool. A
  record that did not persist must not be returned as if it had: count it on the shadow run result (trailing
  nullable `AssessmentsNotPersisted`), exclude it from any "persisted" total, one aggregated Warning.
- `NewsTypingGenerator.cs:1200` — `_familyStore.WriteAsync(...)` discards the bool. A family snapshot that
  did not persist is still the pass's in-memory input to the judge (spec 185 consumes the run result, not
  disk), so the honest treatment is: count it (`FamilySnapshotsNotPersisted`, trailing nullable) and Warn
  once, without changing what the judge sees this run.

Both follow spec 187 §3's precedent for the typing store (`WriteAsync`'s boolean is CHECKED). Sweep every
other `Task<bool>` store call under `src/` and guard the result with the spec-201 source-guard shape.

**Post-merge correction (spec 206): the sweep found one further discarded value and the original
"none" claim is withdrawn.** `CollectionPass` discards `IRawEvidenceStore.WriteIfNewAsync` because that
legacy boolean conflates a healthy insert-only dedupe with a caught disk failure. Treating every `false` as
loss would fabricate one failure per duplicate; ignoring it hides a real missing evidence file. The guard
therefore carries exactly one temporary, named exception for this call site. Spec 206 replaces the boolean
with a typed `Written` / `AlreadyAvailable` / `Failed` outcome, removes the exception, and makes evidence
durability a precondition for downstream extraction so no durable signal can point at a raw item that did
not land.

## 3. The twelfth SHA-256 copy

`EvidenceNormalizer.cs:151` (`ComputeHash`) is the evidence `contentHash` — spec 145's identity input.
Route it through `CanonicalHash`. **The proof is the existing contentHash / evidence-id pins**: every pinned
hash in `EvidenceNormalizerTests` and the spec-145 `EvidenceIdentity` tests must be byte-identical and
UNTOUCHED. If any moves, the refactor is wrong. Then the spec-201 acceptance line becomes literally true:
`CanonicalHash` is the only SHA-256 call site in Application and Infrastructure (excluding the audit
consoles) — add the grep-shaped test that asserts it.

## Non-goals

No score, weight, formula, rule set, cohort key, fingerprint, collection or schema-version change; all six
pins unchanged; no change to what the judge or the shadow read consumes in-process.

## Acceptance criteria

- [ ] A strategy whose config record is not durable writes no snapshot this pass, is named on the run record,
      and every other strategy still scores; `AlreadyAvailable` distinguishes "already on disk" from "written".
- [ ] The two named `Task<bool>` writes count and warn; the source guard contains exactly one documented
      temporary raw-evidence exception, closed by spec 206.
- [ ] `CanonicalHash` is the only SHA-256 site; every evidence/contentHash pin unchanged.
- [ ] Build, full suite, `git diff --check` clean; actual elapsed time in the PR body.

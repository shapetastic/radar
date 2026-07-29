# Task: Calibration harness validation hardening — close the fail-open seams before Phase B

> **Follow-up to spec 162 Phase A (PR #165), from the post-merge review (2026-07-29).** The generated
> artifacts are complete and internally consistent (all 298 exhibit hashes verified against the manifest;
> cohort/exclusions/conflicts all correct), and the architecture (production-reader reuse, scoped cohort,
> truncation parity) is sound. What remains are **fail-open validation boundaries around the study
> artifacts**: the analyzer and archiver currently TRUST provenance that they could VERIFY, which means a
> corrupted, stale, or mislabeled input would flow silently into the calibration numbers. Spec 162's whole
> lineage is about not producing a convincing-looking but invalid calibration — these five fixes are the
> last seams. **This must merge before Phase B runs.** Read-side only; no scoring change; no pin move.

## Assignment

Worktree: any
Dependencies: current main (post 162 Phase A, `ca727ac`).
Estimated time: ~1–2 hours.

## Fixes (all verified against the merged code)

### 1. [P1] Calibration-sample membership: derive, then ENFORCE — never trust the claim

`analyze-labels.ps1` computes the per-bin sample from already-labeled rows, but the calibration table
admits any row whose `adjudication.selectionReason` claims `calibration-sample` (the
`Where-Object … -eq 'calibration-sample'` at the table builder). Two defects: arbitrary rows can enter the
probability estimator, and a sample emitted before all labels exist can differ from one emitted after.

Fix:
- The sample is derived **from the complete directional worksheet alone** (all 145 accessions, binned by
  the SEALED reader confidence, `min(10, bin N)` by SHA-256(accession) hex ascending within bin) —
  independent of which labels exist, so `-EmitSample` is stable at any time by construction.
- The calibration table then **cross-checks recorded membership against the derived set exactly**: a row
  claiming `calibration-sample` that is not in the derived set ⇒ FAIL naming the accession; a derived
  member with no adjudicated label ⇒ listed as missing and the table renders INCOMPLETE (never silently
  smaller). The claim in the JSONL becomes a checked assertion, not an input.

### 2. [P1] No-signal sampling and the extension rule: validate membership, compute from adjudication

The false-negative section accepts whatever labeled no-signal rows exist and computes "misses" from the
second reader's raw direction, and never emits an extension decision.

Fix:
- Validate the labeled no-signal set is **exactly the first 60 — or exactly the first 90 when extended —
  by SHA-256(accession) hex order over the 153**; anything else (wrong members, gaps, extras) ⇒ FAIL
  listing the difference.
- A "miss" is counted **only from human adjudication** (`finalDirection` directional on a no-signal row);
  reader-flagged candidates without adjudication ⇒ the section renders INCOMPLETE with the pending list,
  never a rate.
- Emit an explicit, machine-readable **extension decision block**: `EXTENSION: NOT-TRIGGERED (0 confirmed
  misses, Wilson upper X% ≤ 10%)` or `EXTENSION: TRIGGERED — label the next 30 by hash order, report at
  N=90 (one-shot; trigger is not re-evaluated at 90)`, with the numbers shown.

### 3. [P1] Provenance: recompute and verify, don't compare claims to each other

The analyzer only checks that non-empty `promptHash` values agree with one another — a missing or
uniformly wrong hash passes — and `modelInputHash` is never checked against the manifest.

Fix — the analyzer gains `-ManifestPath` and `-PromptTemplatePath` (defaults to the repo paths) and, for
the FINAL report (a distinct `-Interim` switch may relax completeness, never correctness):
- Recompute the prompt template's hash; **every** label's `promptHash` must equal it (missing ⇒ fail,
  naming rows).
- **Every** label's `modelInputHash` must equal the manifest row for its accession (missing manifest row,
  missing label hash, or mismatch ⇒ fail, naming rows).
- **Every** label's `labeler.provider/model` and `protocol.version` must equal the precommitted values
  (passed as parameters with the spec-162 defaults) ⇒ fail on any deviation.

### 4. [P1] `ExhibitArchiver.NeedsFetch`: verify stored artifacts against the manifest, not just existence

`NeedsFetch` checks row success, file existence, and the trimmed full-text tripwire — but not the stored
files' hashes against the manifest, not the model-input file at all beyond existence, and not whether the
manifest row's `MaxInputLength` matches the current run's argument. A rerun with a different input cap —
or a corrupted long file — is silently skipped, preserving a wrong study input.

Fix — `NeedsFetch` additionally refetches (with a named reason) when:
- the stored full-text file's SHA-256 ≠ the manifest's `FullTextSha256`;
- the stored model-input file's SHA-256 or length ≠ the manifest's recorded values;
- the manifest row's recorded `MaxInputLength` ≠ the current `--max-input-length` in force.
Hashing 298 files per rerun is cheap; correctness of the study input is not optional. A refetch replaces
the manifest row.

### 5. [P2] A short fetched body is a typed FAILURE, not a warned success

`FetchAsync` warns below the tripwire but still returns `Outcome: "success"`, so the console counts it as
archived and exits 0 — and Phase B could consume it. Fix: below-tripwire bodies return a typed failure
outcome (`short-body`), carry empty hashes (so they refetch next run, the existing failure semantics),
count in the failed tally, and the summary/exit code reflect them like any other failure.

## Tests

- Sample enforcement: a label claiming `calibration-sample` outside the derived set fails naming it; a
  derived member without an adjudicated label renders the table INCOMPLETE; the derived sample is
  byte-identical whether computed before or after labels exist.
- No-signal: a 59-row, a 61-row, and a right-count-wrong-member set each fail listing the difference;
  misses count only adjudicated rows; both extension-decision blocks render with their numbers.
- Provenance: wrong/missing promptHash fails; label-vs-manifest modelInputHash mismatch fails; wrong
  labeler or protocol version fails; `-Interim` relaxes completeness only.
- `NeedsFetch`: tampered full text ⇒ refetch; tampered/mis-sized model input ⇒ refetch; changed
  `--max-input-length` ⇒ refetch; untouched valid row ⇒ skip (idempotence preserved).
- Short body: typed `short-body` failure row, counted failed, nonzero-failure summary; next run
  re-attempts it.

## Constraints

- Read-side only; nothing under Scoring/Domain/Pipeline; no fingerprint input; the pins do not move.
- No change to the worksheet/exhibit formats already generated — the current artifacts were verified
  hash-consistent, so after this merge a `NeedsFetch` rerun over `data/calibration-audit/` must be a
  **no-op** (0 refetches). That rerun is the operator's post-merge idempotence check.
- The spec-162 protocol itself is unchanged — this hardens enforcement of what 162 already precommitted.

## Acceptance criteria

- [ ] All five fixes implemented as specified, with the tests above.
- [ ] `analyze-labels.ps1` final-report mode fails on: non-derived sample membership, missing derived
      members, wrong no-signal membership/count, unadjudicated miss candidates, any provenance
      mismatch (prompt hash, model-input hash, labeler, protocol version).
- [ ] `Radar.CalibrationAudit` rerun over the existing `data/calibration-audit/` output is a no-op
      (asserted by test with a valid manifest fixture; verified live by the operator post-merge).
- [ ] `dotnet build Radar.sln -c Release` / `dotnet test Radar.sln -c Release --no-build` green; no
      behavioural change to production projects (the console and script are audit tooling).

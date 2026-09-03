# Task: Stop the same-day run from silently overwriting the typing decomposition artifact

## Overview

`FileNewsTypingArtifactStore` names the typing pass's attention-decomposition artifact by **as-of date
alone**: `{root}/live/attention-decomposition-{yyyy-MM-dd}.md|.json` (and `…-FAILED.md` on the failure
path). Two full runs on one UTC date therefore write the same path, and the second silently destroys the
first run's typing accounting.

This is not hypothetical — it has already cost a measurement. On 2026-09-01 the 21:46Z scheduled run
overwrote the 02:50Z run's artifact, and that 02:50Z run was **run 3 of the spec-200 §5 capacity verdict**:
its `untypedRemaining` checkpoint had to be recovered from the scheduled-run wrapper log (named in the §5
record as the alternate source), and had that log not existed the verdict would have been UNRESOLVED. A
verdict-bearing durable artifact must not be erasable by the next run on the same calendar date. This is
"nothing may be discarded without being counted" applied to the run's own accounting.

The weekly/daily reports are genuinely derived views where same-day overwrite is correct; this artifact is
not one of them — it is the only durable record of what one specific typing pass did.

## Assignment

Worktree: any. Dependencies: none beyond current main (spec 207 merged). Use `run-next.ps1 -Spec 208`.

Estimated implementation time: UNMEASURED. Record actual dispatch→PR time in the PR body.

## 1. Run-scoped artifact identity

Change the artifact base name from `attention-decomposition-{asOfDate}` to

```
attention-decomposition-{asOfInstant}-{runId}
```

- `{asOfInstant}` is the run's as-of instant as `yyyyMMdd'T'HHmmss'Z'` (UTC, invariant culture) — the same
  convention the run-record filenames already use, so the two sort and correlate on sight;
- `{runId}` is the pipeline run id (`RadarPipelineResult.RunId`) in `D` format. This makes the
  artifact↔run association EXPLICIT rather than a time join (the spec-177 principle), and makes collision
  impossible rather than unlikely;
- the failure path becomes `attention-decomposition-{asOfInstant}-{runId}-FAILED.md`, same treatment;
- the date remains visible as the leading segment, so human "find that day's artifacts" browsing still
  works — there is simply one PAIR per run instead of one per day.

`NewsTypingGenerator` already holds `asOfUtc` (it derives today's date token at the same call site) and the
Worker invokes typing after the pipeline result exists, so the run id is available to thread through.
Thread it as a parameter; do not reach for ambient state. If the run id is ever absent on this path (it is
not today — typing runs only in unfiltered full mode, which always mints one), fall back to the instant-only
name and log one Warning naming the missing id; never throw and never fabricate a GUID that no run record
carries.

The store's doc comment currently promises the date-keyed path and describes overwrite as acceptable —
amend it in place (REVERSAL rule): the 2026-09-01 loss is the recorded reason the identity widened.

## 2. Accrued artifacts heal forward only

Existing `attention-decomposition-{yyyy-MM-dd}.*` files are left exactly where they are — no rename, no
rewrite, no backfill, no migration. The 02:50Z 2026-09-01 artifact is **permanently lost** and stays lost;
the spec-200 §5 record already names the wrapper log as its alternate source and nothing here changes that
history. New runs simply stop being able to repeat the loss.

## 3. Tests

Update `FileNewsTypingArtifactStoreTests`:

- two writes with the same as-of instant but different run ids produce two distinct artifact pairs, both
  readable afterwards — the mutation proof: revert to the date-keyed name and this test fails on the second
  write clobbering the first;
- two writes on the same DATE at different instants (the 2026-09-01 shape: 02:50Z then 21:46Z) coexist;
- the failed-artifact path carries the same run-scoped name;
- the emitted filename matches the pinned `yyyyMMdd'T'HHmmss'Z'`+`D`-GUID shape exactly (pin the string for
  one known instant/id pair);
- a legacy date-keyed file already on disk is untouched by a new run-scoped write on the same date.

No scoring, fingerprint, report, typing-behaviour or budget change of any kind: this slice renames the
durable identity of one write-only artifact. All six fingerprint pins unchanged.

## Non-goals

- No change to what the decomposition CONTAINS, when it is written, or the typing pass itself.
- No migration/rename of accrued artifacts; no reconstruction of the lost 2026-09-01 02:50Z artifact.
- No new reader: nothing in `src/` reads these artifacts back today, and this spec does not add one.
- No change to the weekly/daily report writers — their same-day overwrite is correct for derived views and
  is explicitly out of scope.

## Acceptance criteria

- [ ] The artifact (and its FAILED variant) is named by as-of instant + run id; two same-day runs produce
      two surviving artifact pairs (mutation-proven).
- [ ] Run-id threading is explicit; the absent-id fallback warns once and still writes.
- [ ] Accrued date-keyed artifacts are untouched; nothing is migrated or reconstructed.
- [ ] The store's doc comment tells the corrected truth, amended in place with the 2026-09-01 loss as the
      reason; CLAUDE.md owed follow-up (ii) is amended in place to DONE by this spec.
- [ ] Build, full suite and `git diff --check` clean; all six fingerprint pins unchanged; actual elapsed
      time in the PR body.

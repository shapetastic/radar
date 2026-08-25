# Task: Pass-truthful judgment telemetry and fail-closed no-id gate parsing

## Overview

Spec 187 shipped the intended live judgment correction and its full merged test gate is green. A post-merge
review found two remaining code defects and one inaccurate operational comment. They are narrow, but they
sit on boundaries where Radar must describe what actually happened rather than infer it from a nearby
durable fact:

1. `NewsJudgmentGenerator` uses a persisted record's non-null `ProviderDurationMs` as proof that the
   **current pass** called the provider. Same-run idempotency correctly returns the already-persisted record
   without a second call, but that record retains the duration and failure status of its original call.
   The outer loop therefore replays old latency as current latency, increments the current attempted-call
   count, and can replay an old provider/parse/validation failure into the current progress totals. The
   provider bill remains bounded, but the observability added to diagnose throttling is false on exactly
   the rerun path it is meant to explain. Independently, the every-fifth-call boundary is evaluated after
   every candidate rather than only after a call, so **any** no-call candidate following call 5/10/etc. —
   same-run reuse, cross-run cache reuse, `InsufficientFacts` or `AttemptsExhausted` — can re-emit the same
   progress line.
2. `StrategyEvidenceStatusCalculator` documents the empty-verdict-id reason parser as fail-closed, but
   `ParseRenderedReasonCodes` silently discards unrecognised segments. A no-id reason list containing one
   recognised merit failure plus one malformed or future reason is reduced to the recognised merit reason
   alone and becomes `GateFailed`. “Partly understood” has therefore become a negative verdict even though
   the contract says it must remain `GatePending`. This fallback is not exclusively historical: every
   current efficacy artifact uses it while the gate has no verdict, including all rows in the current
   `data/efficacy/strategy-paired-comparison.csv`. Those live rows contain only recognised accrual reasons
   and correctly remain pending. For a well-formed artifact from the current writer, a merit-only result
   receives a non-empty verdict id and takes the structured path, so there is no evidence of an active
   false `GateFailed`; the bad shape requires malformed/manual input or vocabulary drift. The defect is a
   latent fail-closed trap on a live path, not a current mis-verdict.
3. `scripts/run-profiles/default.json` says judgment attempts are bounded “the same way” immediately after
   describing typing's durable pre-call reservation ledger. That is not the shipped design. Typing has a
   crash-/write-failure-exact call bound; judgment deliberately has a bound over durably recorded
   call-producing outcomes plus same-run idempotency.

This slice corrects those claims at their source. It changes no model prompt, response schema, persisted
record schema, cohort key, score, label, rank, strategy, fingerprint, score snapshot, marker policy or
AD-15/AD-16 decision rule.

## Assignment

Worktree: any

Dependencies: spec 187 merged (`5fcd7a0`). Preserve all existing typing, judgment, fact-family, assessment,
score and efficacy artifacts as immutable evidence.

Estimated time: ~half a day.

## 1. Separate durable call provenance from current-pass activity

Keep `NewsJudgmentRecord.ProviderDurationMs` unchanged. On a call-producing record it is the duration of
the provider invocation that created that durable attempt; on a genuine no-call record it is null. A
same-run reused attempt must therefore retain its original non-null duration in the store and in
`NewsJudgmentRunResult.Judgments`. Do **not** clone or project that record with a null duration merely to
drive current-pass counters: that would make the in-memory record disagree with the insert-only record on
disk.

Instead, make the private `JudgeOneAsync` boundary return a pass-local outcome containing at least:

- the `NewsJudgmentRecord` to persist/reuse/present; and
- whether this invocation actually called the provider, represented either by an explicit
  `MadeProviderCall` flag plus duration or by a nullable `ProviderCallDurationThisPass` whose non-null value
  is set only around the analyzer invocation made by this call to `JudgeOneAsync`.

This is transient orchestration state, not another persisted record and not a public wire contract. The
provider-call branch writes the same measured duration into the durable record and the pass-local outcome.
Every no-call branch returns no current-pass duration, including:

- same-run attempt reuse;
- completed-judgment cache reuse from an earlier run;
- `InsufficientFacts`;
- `AttemptsExhausted`; and
- any other path that does not invoke `INewsJudgmentAnalyzer` in this invocation.

Drive all spec-187 §7 provider metrics from the pass-local outcome, never from
`NewsJudgmentRecord.ProviderDurationMs` alone:

- increment `attemptedCalls` only for an analyzer invocation made in this pass;
- add only that invocation's duration to `ProviderCallTimings`;
- increment provider/parse/validation failure counters only when the current invocation made a call and
  produced the matching failure;
- count a persisted judged success only when a current provider call produced `Judged` and `WriteAsync`
  reported durable success; reused judgments may still enter the run result and presentation but are not
  newly produced provider successes; and
- evaluate the every-fifth-call progress boundary only immediately after a current provider call. A later
  no-call candidate must not re-emit the same `5/…`, `10/…`, etc. boundary. Emit the final partial batch at
  most once under the existing rule.

An all-reuse pass, including same-run re-entry over a failed attempt with a stored duration, must log the
final timing summary as **zero provider calls**, emit no progress line and contribute no old failure to
current-pass failure totals. The original record and its original duration remain available for audit.

Do not change attempt counting or idempotency. `JudgmentAttemptHistory`, the three-attempt default,
`standalone#N`, per-run exhaustion identity, cache identity and insert-only store semantics remain exactly
as shipped. This section fixes observation of those decisions; it does not create a new retry authority.

### Tests

Extend `NewsJudgmentProviderTimingTests` and the same-run attempt-bound coverage with deterministic fake
time and a counting analyzer:

- create a call-producing same-run record with a non-null duration, clear captured logs, invoke the same
  run again, and assert zero additional analyzer calls, no progress line, a zero-call final summary and no
  replayed provider/parse/validation failure;
- assert that the reused record returned for presentation still carries its original
  `ProviderDurationMs` and remains byte-/value-consistent with the stored record;
- in a mixed pass containing a same-run reuse and a genuinely new call, assert that calls, latency,
  failures and persisted-success totals describe only the new call while both records may appear in the
  run result;
- place a no-call candidate immediately after the fifth genuine call and assert that the `5/…` progress
  boundary is logged exactly once; and
- retain the existing distinct-run completed-cache, `InsufficientFacts`, exhaustion, percentile and
  identity tests. Durations and pass-local activity still affect no identity, selection or ordering.

No wall-clock sleeps and no request/response text, secret or environment-variable value in logs.

## 2. Require complete parsing before a no-id merit failure

The current-artifact path is unchanged: when `GateVerdictId` is non-empty, the structured identity says a
verdict exists and `Qualifies` determines `GatePassed`/`GateFailed`; `GateReasons` is display detail only.

Change only the empty-verdict-id fallback. It serves both pre-186 artifacts, where the column did not
exist, and current artifacts where the writer correctly left the id empty because no verdict exists yet.
All current paired-comparison rows use this path while their gates are pending; do not label it historical
or imply that current artifacts cannot reach it. Replace the lossy list returned by
`ParseRenderedReasonCodes` with a result that preserves both:

- the exact recognised reason codes; and
- whether **every** rendered segment was successfully parsed as one member of the closed
  `Ad15GateReasonCodes.All` vocabulary.

The no-id fallback may return `GateFailed` only when all of the following are true:

- the non-qualifying no-id artifact has a nonblank reason list;
- the list contains at least one segment;
- every segment is nonblank, structurally parseable and recognised exactly after the existing baseline
  prefix/detail removal; and
- every recognised code is a merit-failure code.

Anything else remains `GatePending`: an empty list, a blank segment, malformed baseline syntax, an
unrecognised/future code, prose, any recognised non-merit/accrual/prerequisite code, or any mixture of a
merit code with one of those states. Unknown input is not silently discarded and cannot help produce a
negative verdict.

For a well-formed artifact written by the current writer, `GateVerdictIdentity.VerdictExists` and the
empty/non-empty id are complementary: a fully structured merit-only result receives an id and uses the
structured branch. Therefore this defect is not producing a known live wrong verdict today. Complete
parsing remains necessary because the read boundary must fail closed for pre-186 files, malformed or
manually edited files, and a future reason code read by an older vocabulary.

Keep the existing exact parsing protections:

- comparisons remain ordinal against the closed code vocabulary;
- a code token embedded in a baseline name or free-form detail contributes nothing;
- the last emitted `"': "` delimiter and last optional parenthesised detail suffix continue to be handled
  according to the renderer's established grammar; and
- no verdict id is fabricated for a no-id artifact. A pending no-id result contributes no
  `StrategyGateVerdict` and therefore cannot bind an operating-call override.

Do not change `OperatingCallReducer`, current verdict identity, the writer, the gate thresholds or the
meaning of any existing reason code. This is a conservative empty-id reader correction only.

### Tests

Extend `StrategyEvidenceStatusCalculatorTests` through its public status/verdict surface:

- recognised merit + unrecognised/future segment ⇒ `GatePending`, no gate verdict;
- unrecognised/future segment + recognised merit in the opposite order ⇒ the same result;
- recognised merit + blank/trailing segment ⇒ `GatePending`, no gate verdict;
- malformed baseline prefix beside a recognised merit segment ⇒ `GatePending`;
- multiple fully parsed merit reasons ⇒ `GateFailed` with the empty no-id verdict id, preserving the
  existing compatibility case;
- any fully parsed non-merit reason, alone or mixed with merit, ⇒ `GatePending`; and
- a current artifact with a non-empty verdict id remains governed only by `Qualifies`, even if its display
  reason contains an unknown token. This proves the fix did not leak into the structured path.

Retain the baseline-name and free-form-detail spoof regressions from spec 187.

## 3. Correct the operational record

Replace the misleading sentence in `scripts/run-profiles/default.json` with wording that states the actual
asymmetry, for example:

> Typing's `MaxTypingAttempts 3` is a bound on provider calls because every call wins a durable pre-call
> reservation. Judgment's `MaxJudgmentAttempts 3` is separately derived from durably recorded
> call-producing outcomes plus same-run idempotency; it deliberately has no pre-call ledger, so a crash or
> failed outcome write between call and persistence can spend an unrecorded judgment call.

Keep the existing exhaustion marker statement. Update the stale `NewsJudgmentGenerator` and no-id-parser
comments that currently claim persisted duration alone proves a call happened “this pass”, that dropping
an unknown segment necessarily holds the status pending, or that the empty-id branch is exclusively a
“legacy (pre-186) compatibility path” which nothing current reaches. The corrected comment must state
that current pending artifacts also enter this fallback and that it remains fail-closed because no
structured verdict identity exists. Add a concise spec-188 correction to `CLAUDE.md` beside the spec-187
timing and structured-gate notes; do not duplicate the full spec.

This documentation change does not alter default profile values, strict-key binding, provider scheduling
or the `_comment*` flattener convention. The default profile must remain valid JSON and continue to resolve
through `run-radar.ps1 -Profile default -WhatIf` without exposing comment keys as Worker arguments.

## 4. Migration and out of scope

No migration, reset, replay or cohort fork is required. Existing judgment durations are truthful provenance
for their original calls and stay untouched. Existing no-id efficacy artifacts — historical or current —
stay readable; the reader now refuses to turn a partially understood rendered list into a failed verdict.

Out of scope:

- changing judgment retry limits, adding a judgment pre-call ledger or altering attempt identities;
- changing provider concurrency, throttling, timeout, fallback or scheduling policy;
- changing prompts, schemas, taxonomy, fact families, validation or marker vocabulary;
- feeding telemetry or gate status into score/rank/label/fingerprint calculations;
- rewriting historical judgment or efficacy files; and
- adding Claude or rescheduling Ollama.

## Acceptance criteria

- [ ] Same-run judgment reuse makes no provider call and contributes zero current-pass calls, duration and
      provider/parse/validation failures, while the reused durable record retains its original non-null
      `ProviderDurationMs`.
- [ ] A mixed reuse/new-call pass reports only genuine current-pass invocations and their outcomes;
      current-pass metrics are driven by explicit transient call activity, never inferred solely from a
      persisted record field.
- [ ] Progress fires once per crossed five-call boundary plus at most one final partial batch; no-call
      candidates cannot duplicate a boundary. An all-no-call pass emits no progress and an explicit
      zero-call final summary without percentiles.
- [ ] Judgment record schema, ids, cohorts, attempt bounds, cache/idempotency and insert-only persistence
      remain unchanged; provider duration affects no decision or identity.
- [ ] The empty-verdict-id fallback, reached by both pre-186 artifacts and current artifacts with no verdict,
      returns `GateFailed` only for a completely parsed, nonempty, merit-only reason list. Any unknown,
      malformed, blank or non-merit segment makes the result `GatePending` and produces no gate verdict.
- [ ] Current non-empty `GateVerdictId` artifacts remain governed structurally by `Qualifies`; rendered
      reason text cannot change their pass/fail state.
- [ ] The default profile, code comments and `CLAUDE.md` accurately distinguish typing's pre-call bound
      from judgment's durably-recorded-attempt bound, and describe the empty-id fallback as live for current
      pending artifacts rather than exclusively pre-186.
- [ ] No existing data is deleted or rewritten, no prompt/schema/cohort/fingerprint moves, and no score,
      rank, label, strategy, marker or AD-15/AD-16 policy changes.
- [ ] `dotnet build Radar.sln -c Release` and the full serialized test suite pass; `git diff --check` is
      clean; on Windows, `run-radar.ps1 -Profile default -WhatIf` still resolves successfully.

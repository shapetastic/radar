# Task: Typing capacity and coverage provenance — drain faster and name failures

## Overview

The first nightly baseline on merged spec 187 (`a180298d-0606-483d-9dd9-67a23f5d5266`, selection instant
2026-08-24T21:39:13Z) ran clean in `00:58:10`. Its most important result is semantic: MNRO changed from
the v1 model's forced `Deteriorating` call to a grounded v2 `Unknown`, while EOSE remained
`Deteriorating` with two cited findings and rendered `⚠ challenged`. Radar made a call when business facts
supported one and declined when they did not. The judgment correction worked in the field.

The same run exposed the next limiting layer:

- the 30-day typing window contained 2,411 observations: 377 `Typed`, 17 `InsufficientContent` and 2,017
  still eligible/untyped. Only 15.6% were fully typed (16.3% had any completed typing outcome);
- the 19 judgment candidates were better served by spec 187's candidate lane but remained partial: 151
  `Typed` + 7 `InsufficientContent` out of 626 observations (25.2% completed), with 468 untyped;
- the run captured 252 new observation files while the typing cap allowed 200 calls. If that relationship
  persists, the rolling window will eventually shed old work but the active queue will not drain and the
  strict all-observations `Complete` state will remain exceptional;
- the run actually spent all 200 typing calls: 100 candidate first attempts, 99 general first attempts and
  one successful retry (AXGN attempt 2 after the preceding run's `ValidationFailed`). The v3 decomposition
  lacks a retry-selected field, which made 100 + 99 look like an unused slot;
- the 200 calls consumed 508.6 seconds of serial provider time (mean 2.54s, p95 6.32s, maximum 32.32s).
  At the observed mean, another 150 calls cost about 6m21s — material but acceptable beside a 58-minute
  baseline, and now measurable through spec 187/188's pass-truthful telemetry; and
- five stage-1 typings were `ValidationFailed`: CVLT, NSSC, PLUS, SHEN and KLIC. The first four were
  judgment candidates, which exactly explains the four persisted `TypingCompleteness = Failed` judgments.
  `RetryExhausted = 0` means no permanent hole; `SearchEnumeration = Failed` is a separate dimension. The
  single `Failed` token currently conflates a retryable problem this pass with a permanently exhausted
  observation.

This spec therefore makes an explicit capacity call: increase the live typing budget to **350**, with
**150** candidate slots and **25** retry slots, because measured inflow currently exceeds measured
capacity and the runtime cost is bounded. It also gives typing incompleteness honest, distinct names and
makes all three lane selections plus actual provider calls visible in the decomposition artifact.

The distinct NewsSearch local-limit audit is split into spec 190. It has a different, higher-risk collector
read-path seam and must not delay this capacity slice or smuggle extra evidence into it. No existing data
is deleted or rewritten.

## Assignment

Worktree: any

Dependencies: specs 187 and 188 merged. Use run `a180298d-0606-483d-9dd9-67a23f5d5266` and its immutable
artifacts as the live-before fixture; do not edit or regenerate them.

Estimated time: ~1–1.5 days.

## 1. Raise the baseline typing budget to 350 / 150 / 25

Change the prospectively declared live/profile posture to:

- `MaxNewTypingsPerRun`: **350**;
- `MaxCandidateTypingsPerRun`: **150**;
- `MaxRetryTypingsPerRun`: **25** (unchanged);
- `MaxTypingAttempts`: **3** (unchanged); and
- `LookbackDays`: **30** (unchanged).

Apply those explicit values to `scripts/run-profiles/default.json` and the redundant
`news-typing.json` / `news-judgment.json` overlays so selecting an experiment overlay cannot silently put
the old 200/100 posture back. Update the profile comments and `DefaultRunProfileTests`. Preserve the
existing cross-field invariant: 150 + 25 < 350 guarantees at least 175 general first-attempt slots even
when both earlier lanes are full.

Keep the code-level ambient defaults in `NewsTypingWorkerOptions` / `NewsTypingOptions` at 200/100. The
increase is a measured baseline operating decision for the checked-in scheduled profile, not permission
for an arbitrary caller that merely enables typing to spend 75% more. Persisted `NewsTypingLimitsRecord`
continues to record the actual limits used by each attempt, and limits remain absent from cohort, fact,
family, score and fingerprint identity.

Do not narrow the 30-day window to manufacture a better completeness percentage. The objective is to read
more relevant evidence, not make missing evidence disappear from the denominator. Do not dynamically
change budgets from queue depth, latency or provider failures; this is one prospectively declared posture
whose outcome will be observed.

The first post-189 live run must report the selected/called lane totals and measured provider timing. The
expected runtime addition (~6 minutes at the 2026-08-24 mean) is a planning estimate, not an SLA and not a
reason to hide throttling. A 429 or slow tail stays on the existing named failure/retry path.

The capacity hypothesis is explicit and falsifiable. If capture remains near 252 observations per run and
350 calls continue to produce durable completed outcomes, capacity exceeds inflow by roughly 98
observations per run; a 2,017-observation backlog would then take about 21 runs to clear before allowing
for retries, validation failures, provider failures and changes in inflow. This is a prediction, not a
promise: the three-run review must compare actual completed outcomes and net backlog movement with it and
name the reason for any material miss.

Candidate completeness has a separate moving-denominator limitation. The live candidate set held 626
in-window observations, 468 untyped, and the candidate lane rises only from 100 to 150. Because companies
enter and leave the nominated set each run, newly admitted untyped histories can refill the lane and keep
`Complete` rare even while the global backlog drains. Do not interpret continuing candidate
incompleteness alone as proof that the budget is too low. The operating review must distinguish retained
candidates from entrants and exits, and compare their coverage separately.

Tests pin 350/150/25 in the resolved default and both overlays, the ≥175 general-slot guarantee, the global
350-call cap, and unchanged candidate round-robin/retry-first/general-fallback ordering.

## 2. Replace ambiguous new `Failed` values with retryable vs exhausted states

Preserve the existing enum ordinals and append two values to `NewsTypingCompleteness`:

- `RetryableFailure`: at least one in-window observation for the company had a provider/parse/validation,
  reservation-refusal or outcome-write failure in this pass, but no in-window observation has exhausted
  its attempt budget. The failed observation remains eligible for a later retry; and
- `RetryExhausted`: at least one in-window observation has spent all permitted attempts without a durable
  completed typing. This is a permanent hole for the current `(cohort, observation, payload)`.

Keep `Failed = 0` readable as the degraded legacy/unclassified value for existing records and defensive
default hydration, but do not newly compute it when the generator knows which state occurred. `Backlog`
and `Complete` keep their current meanings and numeric values.

The new computation precedence is total and conservative:

1. any in-window exhausted observation ⇒ `RetryExhausted`;
2. otherwise any failure/refusal/unpersisted outcome in this pass ⇒ `RetryableFailure`;
3. otherwise any eligible untyped observation ⇒ `Backlog`; and
4. otherwise ⇒ `Complete`.

One observation may remain in `UntypedRemaining` while also explaining a company-level
`RetryableFailure`: the former is the disjoint population partition (“work still eligible”), while the
latter is current-pass provenance (“why this company's read degraded today”). An exhausted observation
remains excluded from `UntypedRemaining` exactly as spec 187 requires.

New judgment attempts persist these explicit tokens. Bump only
`NewsJudgmentRecord.CurrentSchemaVersion` from `news-judgment-v2` to **`news-judgment-v3`**, because the
persisted completeness vocabulary changed. Existing v1/v2 records remain readable and untouched.
`NewsJudgmentContract.PromptVersion`, `ResultSchemaVersion`, the stage-2 cohort key and the model request do
not move: typing completeness is run provenance, never an input to the judge. Completed cached verdicts
remain reusable; the cache projection continues to combine the old verdict fields with the **current
run's** newly computed completeness token.

Marker state and wording remain unchanged in this slice: every value other than `Complete` still makes a
zero-finding dot say `(typing incomplete)`. The live judgment appendix already renders the exact
completeness token, which is where retryable versus exhausted becomes visible without turning one failed
article into a fabricated company challenge.

Tests cover every precedence combination, old `Failed` JSON hydration, the current-run cache projection,
and the live failure shape: a candidate with one validation failure plus ordinary backlog becomes
`RetryableFailure`, while an exhausted observation becomes `RetryExhausted` even when other backlog and
retryable failures coexist. A prior-run failure alone must not keep a later run in `RetryableFailure`;
without a new failure, that later run resolves to `Backlog` or `Complete` from its current state.

## 3. `news-typing-decomposition-v4`: show inflow, retries, calls and retryable failures

Fork only the attention-decomposition artifact tag from `news-typing-decomposition-v3` to
**`news-typing-decomposition-v4`**. Existing artifacts stay readable and immutable. Add trailing fields:

At document level:

- `NewsObservationBatchId` (nullable for a standalone/no-run invocation); and
- `ObservationsCapturedThisRun` (nullable when the associated batch cannot be read; otherwise the batch's
  durable `ObservationsWritten`, not a timestamp-derived estimate); and
- one pass-wide reader summary per extractor cohort carrying `RetrySelected`,
  `CandidatePrioritySelected`, `GeneralSelected`, `ProviderCallsAttempted`, completed outcomes,
  provider/parse/validation failures, reservation refusals, failed outcome writes, `RetryExhausted`,
  `ReservedWithoutOutcome` and `UntypedRemaining`. These are the durable artifact equivalent of the
  bounded/final log totals; a reviewer must not have to reconstruct a pass-wide call budget by summing only
  the current window's company rows.

At reader/cohort × company × capture-mode level:

- `RetrySelected`: distinct observations selected through the retry lane this pass;
- `ProviderCallsAttempted`: distinct observations for which this pass actually invoked the provider; and
- `RetryableFailuresThisRun`: distinct in-window observations that ended this pass with a retryable
  failure/refusal/unpersisted outcome and have not exhausted their budget.

The existing candidate/general fields remain **selection** counts, not call counts. Their sum with
`RetrySelected` says how the queue allocated work; `ProviderCallsAttempted` says what was actually spent
after durable-reservation races/refusals. The distinction is intentional. The pass-wide reader summary is
authoritative for the budget. Its totals and the sum of per-company in-window rows may differ only for
explicitly named reasons such as a selected backlog observation outside the checkpoint window or a
company-less observation; never silently call them equal.

Render named incomplete text for retryable failures separately from backlog, for example:

> typing retryable failure this run: 1 observation for deepinfra-deepseek (ProspectiveRss); it remains in
> the eligible backlog

`RetryExhausted` keeps its existing permanent-hole wording. The v4 partition remains
`Typed + InsufficientContent + UntypedRemaining + RetryExhausted = eligible in-window observations`;
retry selections, calls and retryable failures are diagnostics, not extra population buckets.

The `a180298d` shape is a regression fixture: 100 candidate + 99 general + 1 retry explains all 200 calls;
the four judged candidate companies with a stage-1 validation failure render `RetryableFailure`, while
`RetryExhausted` remains zero. Tests use constructed records rather than copying mutable live files.

## 4. Documentation, migration and operating review

Update `CLAUDE.md`, the default/overlay profile commentary and stale code comments with:

- the 350/150/25 baseline posture and its measured 2026-08-24 basis;
- the distinction between retryable typing failure, exhausted typing and ordinary backlog;
- the v4 decomposition diagnostics; and
- the moving-candidate limitation and three-run review method.

No deletion, reset, replay or model/cohort migration is required. Existing observations, evidence,
signals, scores, typings, reservations, families, judgments and efficacy artifacts remain immutable.

After three successful post-189 nightly runs, review rather than auto-tune:

- observation inflow versus actual typing calls and change in `UntypedRemaining`;
- candidate completed-typing coverage, split between retained candidates and newly entering companies;
- candidate-set churn: companies retained, entering and exiting versus the preceding comparable run;
- retryable failures and exhaustion;
- typing p50/p95/max/total and provider-failure rate; and
- the observed net backlog movement versus the predicted roughly 98-observation drain per run.

That review may justify another explicit budget or collection decision. This spec adds no feedback
controller and no silent configuration mutation.

## 5. Out of scope

- Raising `Radar:News:MaxRecordsPerCompany`, admitting additional NewsArticle evidence, changing attention
  scores/ranks/fingerprints, expanding the observation sidecar beyond the scored retained prefix, or
  implementing spec 190's NewsSearch local-limit audit in this slice.
- Narrowing the typing lookback window or deleting/aging data early to improve a percentage.
- Changing typing prompt/schema/taxonomy, fact-family identity or judgment prompt/result schema/cohort.
- Changing marker state or treating incomplete typing as a company challenge.
- Parallel provider calls, dynamic throttling, automatic fallback or provider substitution.
- Adding Claude, rescheduling Ollama or changing the DeepInfra reader/model.
- Rewriting old `Failed` judgments into a guessed retryable/exhausted state.

## Acceptance criteria

- [ ] The resolved baseline and both typing/judgment overlays declare 350 total / 150 candidate / 25 retry
      calls, retain a 30-day window and three-attempt lifetime bound, and reserve at least 175 general slots.
- [ ] The first three-run review tests the declared roughly 98-observation net-drain prediction against
      actual completed outcomes, inflow and `UntypedRemaining`, explaining any material miss rather than
      silently revising the baseline.
- [ ] Candidate coverage reporting distinguishes retained candidates from entrants and exits so rotation
      cannot be mistaken for evidence that the global typing budget is necessarily too low.
- [ ] The 350-call cap and retry → candidate round-robin → general ordering hold under full, partial and
      unused lanes; every call still has a durable pre-call reservation.
- [ ] Newly computed typing completeness distinguishes `RetryableFailure` from `RetryExhausted`; legacy
      `Failed = 0`, `Backlog` and `Complete` remain readable with unchanged ordinals. Exhaustion has
      precedence, and no known new state is collapsed back to `Failed`.
- [ ] New judgments stamp `news-judgment-v3` records with the explicit completeness token while prompt,
      result schema, model request and stage-2 cohort key remain unchanged. Cached verdicts carry current
      completeness provenance, and existing v1/v2 records are untouched.
- [ ] `news-typing-decomposition-v4` records the associated batch/new-observation inflow, authoritative
      pass-wide reader totals, all three per-company lane selections, actual provider calls and retryable
      failures without breaking the population partition.
- [ ] The `a180298d` regression shape is representable as 100 candidate + 99 general + 1 retry = 200 calls,
      five retryable stage-1 validation failures, four affected candidates and zero exhausted observations.
- [ ] No NewsSearch reader/collector path, `MaxRecordsPerCompany`, evidence, score, rank, label, strategy,
      scoring fingerprint, score snapshot, marker policy or AD-15/AD-16 decision rule changes in this spec.
- [ ] No historical artifact is deleted or rewritten. `CLAUDE.md` and profile/code comments describe the
      shipped capacity, completeness and candidate-churn semantics accurately.
- [ ] `dotnet build Radar.sln -c Release` and the full serialized test suite pass; `git diff --check` is
      clean; on Windows, `run-radar.ps1 -Profile default -WhatIf` resolves 350/150/25 and no `_comment*`
      argument reaches the Worker.

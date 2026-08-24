# Task: Live judgment correction — grounded calls, candidate-first typing, durable attempt accounting

## Overview

The first live spec-185/186 baseline run (`976d0f20-cad0-439d-9d6d-f2ba016d72a6`, 2026-08-24)
completed successfully in `01:03:25`. It proved that the two-stage surface is wired end to end: 200 stage-1
typings were persisted, 18 candidate companies were judged, all three semantic-marker states rendered, and
spec 186 correctly stopped a zero-finding `Deteriorating` judgment from showing the reassuring dot.

It also proved that a structurally complete judgment is not yet necessarily a sound judgment:

- every completed judgment was made with `TypingCompleteness = Backlog`;
- EOSE had 31 archived observations but only two completed typings. The three headlines that motivated this
  work — loss widening plus legal scrutiny, legal probes plus losses, and an 11.8% fall after tighter 2026
  revenue guidance plus a wider Q2 loss — were still untyped, so the judge could not see their facts;
- MNRO's persisted rationale said the supplied ESG fact was neutral and did not evidence deterioration,
  then said it labelled the trajectory `Deteriorating` because the instruction required a directional
  choice. This is a v1 prompt-contract defect, not merely a poor call. CASS inferred deterioration from the
  absence of positive context. WDFC defaulted to `Improving` because adverse evidence was absent. KGS
  inferred improvement from one institutional investment. YORW converted a 52-week share-price low into a
  high-confidence business execution finding; and
- EOSE's only challenge finding rested solely on a plaintiff-law-firm solicitation. The attribution caveat
  worked, but the same weak fact also drove the uncited trajectory axis.

That is the distinction this slice closes: **Radar should make a call when supplied business facts support
one, and accept that the call may later prove wrong; it must not manufacture a call from absence, marker
mechanics, price action, or evidence the judge cannot identify.** `Unknown` remains an honest answer when
the supplied facts do not establish business direction. It is not the default escape when relevant
business evidence exists, and `Improving`/`Deteriorating` are not defaults when it does not.

The run's wall clock also corrects an operational assumption. Typing plus judgment occupied roughly five
minutes (about 4m25s for 200 typing calls and about 25s for 18 judgments); most of the 1h03 run was elsewhere
in the pipeline. Provider throttling can still change that materially, but the current records/logs do not
make live latency visible enough to distinguish a slow provider from a slow collector.

A post-run code review found four additional concrete defects:

1. `NewsTypingGenerator` calls the hosted model before any durable attempt fact exists and ignores the
   boolean returned by `INewsTypingStore.WriteAsync`. A process crash or failed outcome write can therefore
   consume a hosted call without advancing the persisted attempt count, so spec 186's nominal maximum is
   not a maximum on calls.
2. The set of exhausted observations is computed before the pass. A failure on the final permitted attempt
   is therefore reported as exhausted only on the next run, and `BuildCompany` currently counts the same
   observation as both typing backlog and retry exhausted.
3. A current paired-gate artifact can carry a semantic `gateVerdictId` while
   `StrategyEvidenceStatusCalculator` still decides pending/failed by substring-searching rendered
   `GateReasons`. A baseline name containing a reason-code token can therefore make the status disagree
   with the structured verdict identity.
4. The production `_comment*` flattener repair in `scripts/run-radar.ps1` is correct but uncommitted, while
   `RunProfileGuardCompatibilityTests` still mirrors exact `_comment` only and binds only the older
   spec-174 guard sites. The complete test suite passed while clean HEAD crashed on `_comment2` before doing
   useful work.

The v2 validation fork introduces one further obligation: stage-2 `ValidationFailed` records currently
retry every later run without a lifetime bound. Stricter validation must not turn better fail-closed rules
into an endless provider bill, so §1 also bounds failed judgment attempts and makes exhaustion visible.

One follow-up slice addresses the live evidence and these defects. It changes no score, label, rank,
strategy, scoring fingerprint, score snapshot, or AD-15/AD-16 claim. Typing and judgment remain read-side
metadata beside the rank; they never feed it.

## Assignment

Worktree: any

Dependencies: specs 181, 184, 185 and 186 merged. Preserve the first-live-run data as immutable evidence;
do not edit, delete or regenerate it.

Estimated time: ~2–3 days. Do not trim §7 merely to fit the estimate; live provider visibility is part of
the correction, not optional polish.

## 1. `news-judgment-v2`: cite what establishes the trajectory

The stage-2 wire and prompt contract both fork:

- `NewsJudgmentContract.PromptVersion`: `news-judgment-prompt-v1` →
  **`news-judgment-prompt-v2`**;
- `NewsJudgmentContract.SchemaVersion`: `news-judgment-schema-v1` →
  **`news-judgment-schema-v2`**; and
- newly written judgment records stamp **`news-judgment-v2`**. Existing v1 records remain readable and
  untouched. The prompt/schema versions already enter the stage-2 cohort key, so v1 and v2 judgments can
  never be reused or pooled.

Add `TrajectoryFactIds` to `NewsJudgmentModelResponse`, the validated projection and the persisted judgment
record. The persisted member is trailing/nullable for old-file hydration; a v2 `Judged` record always writes
a non-null list. These are the representative FactIds that the model says actually establish its
`BusinessTrajectory`, not every fact it happened to read.

Mechanical validation is strict and deterministic:

- every id must parse, be distinct after ordinal-preserving normalization, and belong to the supplied
  family set;
- `Improving`, `Deteriorating` and `Mixed` require at least one trajectory FactId;
- `Unknown` requires an empty trajectory-id list: it means no supplied fact established a directional
  balance, not that the provenance was omitted;
- a non-`Unknown` trajectory must have at least one cited trajectory fact whose assertion status is
  `ConfirmedFiling`, `Reported` or `Announced`. Evidence that is entirely `Alleged`, `Solicited` or
  `Speculative` may still support a caveated challenge finding, but cannot by itself establish the
  company's overall business direction;
- a trajectory evidence set made entirely from families whose event types are confined to
  `AnalystOrRatingAction`, `MarketReaction`, `IndexOrTradingMechanics` and/or `PromotionalOrListicle` is
  invalid. Those facts describe other people's views, price/trading behaviour or content mechanics, not
  the business trajectory. A family containing at least one other event type is not rejected by this
  guard; and
- a challenge finding whose cited evidence is entirely confined to those same context-only event types is
  dropped with a named `non-business-context-only` reason. A share-price fall is not an
  `ExecutionOrMissedMilestone`; the finding must cite a supplied business fact behind the reaction if one
  exists; and
- every `Judged` response requires a non-blank factual rationale of at most 1,000 characters. Advice
  language still fails the shared guard. A missing, over-length or scrubbed-to-empty v2 rationale makes the
  whole response `ValidationFailed`, never a clean-looking zero-finding judgment.

Assertion status and event types are evaluated on the representative fact actually supplied to the judge.
An unprovided family member cannot silently upgrade a `Speculative` representative to `Reported`. That is
conservative by design: if the stronger member should govern, the family projection must select and expose
it rather than asking the validator to reason over hidden evidence.

Do not add a phrase scanner that tries to infer semantic polarity from rationale prose. The deterministic
boundary can verify provenance, evidence class and strength of assertion; it cannot prove that a model
weighed a revenue decline correctly. Bad semantic calls remain possible and visible — that is preferable
to pretending a brittle keyword rule made them impossible.

Challenge findings keep their own cited `FactIds`; they do not have to be a subset of
`TrajectoryFactIds`. This is deliberate: an overall `Improving` or `Unknown` read may still contain a
specific caveated challenge. The existing rule that all-invalid findings fail the response remains. A
zero-finding `Deteriorating` result also remains legitimate, now only when it cites the business facts that
establish the decline; spec 186's warning marker remains unchanged.

The fixed prompt must state all of the following as rules, not examples hidden in commentary:

- make the best directional call supported by the supplied business facts, even when it may be wrong;
- use `Mixed` when the supplied business facts genuinely pull in opposing directions, and `Unknown` only
  when they do not establish direction;
- absence of adverse evidence is not evidence of improvement, and absence of positive evidence is not
  evidence of deterioration;
- never choose a trajectory in order to trigger or suppress a presentation marker; the model neither sees
  nor controls marker policy;
- share-price moves, analyst targets/ratings, index changes, institutional holdings/trades, conference
  attendance and promotional/listicle coverage do not establish recent BUSINESS trajectory on their own;
- syndication breadth is reporting corroboration for one claim, never N independent facts; and
- weakly asserted facts may warrant a caveated challenge but do not establish the overall direction alone.

Pin the complete instruction text (or its canonical hash) so a later wording change cannot silently remain
inside prompt-v2. Extend the architecture guard so `TrajectoryFactIds` carries only supplied fact identity;
no headline, article prose, Radar score/rank/label, price series, marker state, future outcome or prior
judgment enters the request or response.

When a v1 record is rendered historically its missing `TrajectoryFactIds` reads as "not recorded under
v1", not as an empty v2 evidence set and not as proof of invalidity. Current presentation uses the v2
cohort after this change; no old record is rewritten.

Tests must include the first-run failure shapes:

- MNRO-shaped neutral ESG evidence plus a rationale that admits no deterioration cannot pass with omitted
  trajectory ids; the v2 prompt explicitly requires `Unknown` when direction is not established;
- a CASS-shaped response may not infer direction merely from missing positive/negative context;
- a WDFC-shaped "improving by default" response is prevented by the pinned absence rule and cannot pass
  mechanically without cited directional facts;
- a KGS-shaped institutional holding/trade is named by the prompt as context that does not establish the
  investee's business trajectory on its own (taxonomy v1 has no narrower ownership-context token, so this
  is deliberately a prompt-contract test rather than a fabricated text-classification rule);
- a YORW-shaped pure `MarketReaction` family cannot establish `Deteriorating` or support a business
  execution finding as its sole evidence (the finding is dropped by a named
  `non-business-context-only` reason); and
- an EOSE-shaped `Solicited` legal fact may support a caveated legal challenge, but an overall directional
  trajectory based only on that fact fails with a named `trajectory-assertion-too-weak` reason.

The prompt tests establish the contract; they do not claim a unit test can guarantee future model
judgment. The live artifact remains the evidence of actual behaviour.

### Bound failed judgment attempts

The stricter v2 validator makes a persistent `ValidationFailed` response more likely. Do not close endless
typing retries while allowing the same failed stage-2 judgment to call the provider every night forever.

Add `Radar:NewsResearch:Judgment:MaxJudgmentAttempts`, default **3**, validated at startup (≥ 1, strict-key
allowlist) and recorded trailing/nullable in `NewsJudgmentLimitsRecord`. Derive the count per
`(judgmentCohortKey, companyId, familySetHash)` from persisted call-producing judgment attempts. A new
prompt/schema, changed fact-family set or changed upstream stage-1 cohort naturally gets a fresh budget;
renaming a reader does not.

- `Judged`, `ProviderFailure`, `ParseFailure` and `ValidationFailed` are call-producing attempts;
  `InsufficientFacts`, cache reuse and the exhaustion record below are not.
- A persisted attempt for the same non-null `runId` is same-run idempotency: reuse it for presentation and
  make no second call.
- Preserve the typing precedent for the supported null-run path: judgment outcome identity keeps the first
  `standalone` token and uses `standalone#N` for later persisted attempts. The ordinal is derived once from
  the pre-pass store snapshot; existing ids do not move.
- At the limit, make no call and persist a same-run `NewsJudgmentStatus.AttemptsExhausted` record with no
  model result. It does not enter `IsCompletedJudgment`, does not increment the attempt count, and renders
  `? unassessed (retries-exhausted)` through a new closed marker-reason token. Give this no-call record its
  own deterministic identity namespace and run scope: fold the current non-null run id, or the literal
  `standalone` for the null-run path. It therefore cannot collide with the last null-run `standalone#N`
  CALL attempt. A later real run persists one small fresh exhaustion record and satisfies spec 185's
  existing same-run marker rule; it must not dedupe onto a prior-run exhaustion record and render `stale`
  instead. Repeated exhausted null-run invocations may idempotently reuse the one `standalone` exhaustion
  record — both record and current run scope are null, so the marker remains `retries-exhausted` and no call
  occurs.
- Check the boolean outcome of judgment-store `WriteAsync`. A failed write is logged and its unpersisted
  result is not presented as durable judgment. Unlike typing §3, stage 2 does not add a pre-call ledger:
  its guarantee is a bound over durably recorded attempts plus same-run idempotency, not crash-/disk-failure
  exactness across processes. The accepted asymmetry is explicit: judgment is one serial call per company
  per run, while typing can spend hundreds of calls and therefore earns the stronger reservation protocol.

Tests pin provider call count at three across distinct runs, same-run re-entry, null-run `standalone#N`
identity, a fourth-run no-call `AttemptsExhausted` record/marker, a fresh budget after family-set or contract
change, and the judgment-store-false path never reaching presentation. Update the status/marker vocabulary
and its total-function tests; exhaustion must never render a dot or a challenge.

The family-set scope is intentional. While typing backlog drains, a company's `FamilySetHash` may change
from run to run and each materially changed input earns a fresh judgment-attempt budget; the bound becomes
visible once that input stabilizes. This mirrors typing's payload-hash scope: retry limits constrain repeated
calls over the same input, not the evaluation of newly available evidence.

## 2. Type the companies we are about to judge

The global 30-day/backlog queue is useful for eventual coverage, but it is the wrong only queue for a
same-run leader judgment. At the first live run it spent 200 calls while leaving every judged company
incomplete and the motivating EOSE facts unavailable.

Compute the ordered judgment-candidate plan exactly once after the pipeline has produced
`StrategySections`, using the existing `NewsRiskCandidateSelector` rules and the resolved
`MaxCompaniesPerRun`. Use a small application seam (for example `INewsJudgmentCandidatePlanner`) registered
with judgment so the Worker does not duplicate selection policy. Pass that exact immutable plan to both
the typing generator and judgment generator; the latter no longer independently reselects from strategy
sections. A test must prove that the companies prioritized for typing are byte-for-byte the companies
later judged, in the same order.

Add `Radar:NewsResearch:Typing:MaxCandidateTypingsPerRun`, default **100**:

- it is a strict-key member and must be at least 1;
- when judgment is enabled it must satisfy
  **`MaxCandidateTypingsPerRun + MaxRetryTypingsPerRun < MaxNewTypingsPerRun`**. This reserves at least one
  general first-attempt slot under every valid configuration, even when both earlier lanes are full; with
  the defaults, 100 candidate + 25 retry leaves 75 of 200 slots for global progress;
- when judgment is disabled or there is no candidate plan, typing selection is byte-identical to the
  current spec-186 behaviour and the candidate capacity is simply unused; and
- record the limit trailing/nullable in `NewsTypingLimitsRecord`; no scoring identity includes it.

Per reader, selection stays inside the ONE `MaxNewTypingsPerRun` hosted-call budget in this order:

1. **Retry lane:** up to `MaxRetryTypingsPerRun`, using spec 186's global FIFO fairness unchanged.
2. **Candidate first-attempt lane:** from the remaining capacity, up to
   `MaxCandidateTypingsPerRun`. Round-robin over the ordered candidate plan. For each candidate, offer
   unattempted in-window observations newest-first, followed by that candidate's legacy backlog oldest-first.
   One noisy company cannot consume the lane before the others receive an observation.
3. **General first-attempt lane:** every unused slot flows back to the existing global order — in-window
   newest-first, then backlog oldest-first.

An observation selected by an earlier lane is ineligible for later lanes. Candidate status changes only
selection order; it never changes typing content, validation, cohort identity or fact-family membership.
Retries remain globally fair rather than allowing a current leader to pin failing calls forever.

Extend the decomposition/run diagnostics with `CandidatePrioritySelected` and `GeneralSelected` per reader
(retry selections remain separately reported). Bump `news-typing-decomposition-v2` to
**`news-typing-decomposition-v3`** because `UntypedRemaining` also gains the corrected meaning in §4.
Existing v1/v2 artifacts remain readable by name and untouched.

Use a deterministic fixture with 18 candidates, one noisy EOSE-shaped company carrying 31 observations,
the three motivating headline shapes among its recent unattempted inputs, non-candidate backlog, prior
failures and the default 200/100/25 bounds. Pin round-robin fairness, no duplicate selection, the exact
overall call cap, candidate coverage in the same pass, and continued global-backlog movement.

## 3. Reserve every hosted typing attempt before the call

Spec 186 derived attempt count from outcome records. That cannot strictly bound hosted calls because the
call happens before the outcome write. This section explicitly supersedes spec 186 §2's "no new store / no
side index" implementation constraint; a durable pre-call fact is necessary for the guarantee the section
claimed.

Add an insert-only `NewsTypingAttemptReservation` and `INewsTypingAttemptLedger`, implemented in
Infrastructure under the typing output root. Each reservation carries:

- schema `news-typing-attempt-reservation-v1`;
- deterministic `ReservationId`;
- cohort key, observation id and payload hash;
- one-based attempt ordinal;
- nullable run id as provenance only;
- reader provider/model; and
- `ReservedAtUtc`.

The deterministic reservation identity is over
`(cohortKey, observationId, payloadHash, attemptOrdinal)` — **not** run id. `TryReserveAsync` uses an atomic
create-new file operation. Two processes racing for the same ordinal cannot both win. A completed durable
reservation must exist before `AnalyzeAsync` is invoked; failure to create/read the reservation means no
hosted call.

Attempt occupancy is the union of:

- durable reservation ids; and
- legacy typing outcome records with no `AttemptReservationId`.

This prevents old attempts being forgotten while avoiding double-counting a new linked outcome. New
`NewsTypingRecord`s carry trailing nullable `AttemptReservationId` and `AttemptOrdinal`; old records hydrate
as legacy. The next ordinal is derived deterministically from the occupied set.

Keep spec 186's outcome-record identity compatibility: a run-scoped outcome keeps the existing run-id
branch; a null-run outcome uses the reservation ordinal to retain `standalone` for attempt 1 and
`standalone#N` thereafter. The ledger becomes the sole authority for new attempt occupancy. Delete spec
186's outcome-derived selection/counting machinery except for the one migration read that converts legacy
outcomes without a reservation id into occupied attempts; do not leave two competing budget calculators.

The call protocol is exact:

1. completed-cache hit ⇒ no reservation and no call;
2. occupied attempts at `MaxTypingAttempts` ⇒ exhausted, no reservation and no call;
3. atomically reserve the next ordinal;
4. only the winner calls the provider;
5. persist the outcome linked to the reservation; and
6. only a successful `INewsTypingStore.WriteAsync == true` may enter the completed map, increment persisted
   outcome counts, contribute facts/families, or flow into the judge.

If another process already created the candidate reservation, reload/skip that observation for this pass;
do not immediately reserve the following ordinal and create a concurrent duplicate call. A reservation
with no outcome — process crash, cancellation after reservation, or failed outcome write — conservatively
consumes one attempt and is surfaced as `ReservedWithoutOutcome`. This can spend the retry budget early,
but it can never overspend it or present unpersisted facts as durable evidence.

The strict invariant, asserted on provider call count, is:

> For one `(cohortKey, observationId, payloadHash)`, hosted typing calls can never exceed
> `MaxTypingAttempts` across repeated run ids, null-run invocations, concurrent processes, outcome-store
> failure, or a crash after reservation.

Do not silently turn a reservation-store or outcome-store failure into backlog. Count it, log one bounded
cohort summary, mark the affected company's same-run typing completeness `Failed`, and leave the judge
without those unpersisted facts.

Tests require: legacy-outcome migration, same-run replay, repeated null-run calls, an outcome store returning
false, an exception/crash seam after reservation and before outcome, a reservation without outcome carried
into the next run, two concurrent reserve attempts with one winner, final-budget exhaustion, and provider
call-count assertions for every case. File-store tests must use the real `FileMode.CreateNew` path rather
than an in-memory fake alone.

## 4. Exhaustion is immediate and disjoint from backlog

Update the per-reader exhausted set during the pass, after each reservation/outcome result. If the final
permitted attempt leaves no durably completed typing, that observation is exhausted in the **same run**.
No extra run is required to discover it.

For every company/cohort/capture-mode row:

- `UntypedRemaining` counts only observations that are still eligible for a future first attempt or retry;
- `RetryExhausted` counts observations that have spent all permitted attempts without a durable completed
  typing; and
- the two sets are disjoint. `UntypedRemaining + RetryExhausted + completed outcomes` reconciles to the
  eligible observation population (with the existing completed-status split retained).

An exhausted in-window observation still makes `NewsTypingCompleteness.Failed`; ordinary deferred work is
`Backlog`. Render only the matching incomplete reason. Extend the existing retry test to assert the state
on the exact third/final failure run, not by running well past the boundary.

## 5. Let structured gate identity outrank rendered reason text

For a paired artifact carrying a non-empty `GateVerdictId`, the structured fields are authoritative:

- `Qualifies == true` ⇒ `GatePassed`;
- `Qualifies == false` ⇒ `GateFailed`; and
- the non-empty verdict id is the identity carried into `StrategyGateVerdict`.

The writer already emits an id only when the composite gate has reached a merit verdict. Do not
reconstruct that decision by substring-searching human-readable `GateReasons` on the read side.
`GateReasons` remains display detail only for current artifacts.

For a legacy artifact with no id, keep the documented pre-186 compatibility path isolated and fail closed;
it may parse exact reason-code fields/tokens, never arbitrary substring occurrences in baseline names or
prose. Do not fabricate an id. Add a regression where a baseline name contains
`no-eligible-blocks`/another non-merit token while the current artifact has a real failed verdict id: status
must be `GateFailed`, and `GateVerdicts` must carry that same id. Add the symmetric passed case and the
legacy missing-id cases.

## 6. Commit the `_comment*` flattener repair and test the real failure boundary

Keep the already-applied production change in `scripts/run-radar.ps1`:

```powershell
if ($p.Name -like '_comment*') { continue }
```

Its comment establishes the convention: `_comment`, `_comment2`, `_comment3`, and any future `_comment*`
property are profile annotations and must never become Worker configuration arguments.

Repair the test boundary as well:

- `RunProfileGuardCompatibilityTests.Flatten` mirrors PowerShell's case-insensitive `_comment*` behaviour
  with `StartsWith("_comment", StringComparison.OrdinalIgnoreCase)`;
- fixtures pin `_comment`, `_comment2` and `_comment3` at nested and root levels;
- the compatibility test binds the complete `Radar:NewsResearch` typing/judgment/shadow configuration and
  its strict-key guards, not only the older scoring/insider/attention binders; and
- add a Windows script smoke test (or an equivalent checked script harness) that executes
  `run-radar.ps1 -Profile default -WhatIf`, asserts that no resolved `--Radar:*_comment*=` argument exists,
  and asserts that `Typing:Enabled=true`, `Judgment:Enabled=true` and the configured DeepInfra readers do.
  It must execute the production flattener, not another hand-copied implementation. Mark this test
  explicitly Windows-conditional and skip it with a named reason when Windows PowerShell is unavailable;
  the cross-platform mirror and full-config guard tests still run everywhere.

The full test suite passing while clean HEAD crashes is the regression being closed. A mirror-only test is
necessary for config binding but insufficient as proof that the PowerShell implementation matches it.

## 7. Provider-call timing and bounded progress visibility

Measure each typing and judgment provider invocation with the injected `TimeProvider` monotonic timestamp
APIs. Add trailing nullable `ProviderDurationMs` to both persisted attempt records; it is observational
provenance only and enters no record id, cohort key, family id, scoring identity or fingerprint. Provider,
parse and validation failures retain the duration when an outcome can be persisted.

Emit bounded progress at Information level without model text or secrets:

- typing: every 25 attempted calls per reader, plus the final partial batch;
- judgment: every 5 attempted calls per judge/stage-1 cohort, plus the final partial batch; and
- fields: completed/selected, persisted successes, provider/parse/validation failures, elapsed stage time,
  rolling mean call duration and the current maximum.

Each stage's final summary also reports call count, p50, p95, maximum and cumulative provider-call duration
per reader/judge. Percentiles use the current pass's in-memory durations only and a deterministic nearest-rank
definition pinned by tests. Empty-call/cache-only passes render zero calls rather than invented latency.

This is observability, not a new timeout or concurrency policy. Keep calls serial as today, add no automatic
provider fallback, and do not silently lower the per-run budget in response to throttling. A 429/provider
failure follows the existing named failure/retry path and is visible in the progress counters.

Tests use a fake `TimeProvider`; no wall-clock sleeps. Assert that durations never affect identity or
selection and that logs contain no request/response content, API key or environment-variable value.

## 8. Baseline provider posture: stop scheduling Ollama, preserve support

Remove the `ollama-local` entry from **`Radar:NewsResearch:Shadow:Readers` in
`scripts/run-profiles/default.json`**. Keep the explicit DeepInfra DeepSeek entry: a non-empty reader list
replaces the ambient reader, so deleting the whole list would accidentally remove the hosted cohort too.

Update the surrounding default/profile commentary and `DefaultRunProfileTests` to say and assert:

- baseline shadow = one hosted DeepInfra DeepSeek reader;
- baseline typing = one hosted DeepInfra DeepSeek reader;
- baseline judgment = one hosted DeepInfra DeepSeek judge; and
- there is no `Shadow:Readers:1` in the resolved default profile.

This is a scheduling/configuration decision, not a provider deletion:

- retain the Ollama `IChatClient` provider implementation, option binding, manual-profile capability and
  provider tests;
- do not delete or rewrite existing `ollama:llama3.1` assessment/cohort data — it remains historical
  provenance and cohorts never pool;
- do not substitute Ollama into typing or judgment; and
- do not add a Claude CLI/provider, wrapper, cohort or fallback in this spec. Claude may be evaluated later
  under its own explicit provider and cohort identity.

## 9. Documentation and migration record

Update `CLAUDE.md` with a concise spec-187 architecture note covering the judgment-v2 grounding contract,
bounded judgment failures, candidate lane, durable typing-attempt reservation, immediate/disjoint typing
exhaustion, structured gate decision, provider timing and one-reader baseline posture. Update stale code
comments and profile comments that still describe spec 186's outcome-derived count as a strict hosted-call
bound or say the shadow keeps both DeepSeek and Ollama.

No operator deletion/reset is required. The first post-187 run naturally creates the v2 judgment cohort and
re-judges current candidates; stage-1 typing remains in its existing cohort because selection priority and
attempt accounting do not change the extractor prompt/schema/taxonomy. Existing facts, families, typings,
judgments and assessments remain immutable.

## 10. Out of scope, recorded not built

- Any Claude subscription/CLI wrapper, provider adapter, model evaluation or fallback.
- Removing Ollama support from code, deleting its historical cohorts, or reprocessing them.
- Deleting/resetting prior score, signal, typing, family, judgment, assessment or efficacy data.
- Feeding typing, trajectory, findings or markers into score/rank/label/fingerprint calculations.
- A new event taxonomy, semantic NLP rule engine, support-finding taxonomy or investment recommendation.
- Parallel provider calls, a wall-clock stage cutoff, dynamic throttling, automatic reader substitution or
  provider mixing.
- A claim that prompt-v2 makes the model infallible. It makes directional evidence explicit and rejects
  several known invalid bases; live calls remain judgments that can be wrong.

## Acceptance criteria

- [ ] `news-judgment-prompt-v2` / `news-judgment-schema-v2` / `news-judgment-v2` fork a new cohort; every
      non-`Unknown` v2 trajectory cites valid supplied business FactIds including at least one
      at-or-above-`Reported` assertion; context-only/weak-only direction fails by named reason; `Unknown`
      carries no trajectory ids; v1 records remain readable and untouched.
- [ ] The prompt expressly requires a supported call, forbids absence/default/marker-driven direction, and
      separates business facts from price/analyst/ownership/promotional context. The first-run MNRO, CASS,
      WDFC, KGS, YORW and EOSE failure shapes are covered at the strongest deterministic seam available.
- [ ] Judgment calls for one `(cohort, company, family set)` stop after three durably recorded attempts by
      default; same-run and null-run identities are idempotent/distinct as specified; exhaustion persists
      and renders `? unassessed (retries-exhausted)` without becoming a completed judgment. The documented
      judgment-vs-typing durability asymmetry is neither hidden nor overstated.
- [ ] The ordered candidate plan is computed once and shared by typing and judgment. Under the default
      200/100/25 bounds, candidate first attempts advance round-robin, retry fairness remains, unused capacity
      flows to the global queue, non-candidate backlog still advances, no observation is called twice and
      the overall provider-call cap holds. Every valid judgment-enabled configuration reserves at least one
      general first-attempt slot through the three-way cross-field rule.
- [ ] Every hosted typing call has won a durable pre-call reservation. Across reruns, null runs,
      concurrency, failed outcome writes and crash-after-reservation, provider calls for one
      `(cohort, observation, payload)` never exceed `MaxTypingAttempts`; unpersisted outcomes never feed
      families or judgment. Spec 186's `standalone#N` outcome ids remain compatible, while its derived
      counter survives only as the explicit legacy-occupancy migration reader.
- [ ] A final failed attempt is exhausted in that same run. `UntypedRemaining` and `RetryExhausted` are
      disjoint and reconcile with completed outcomes; ordinary backlog and permanent failed coverage render
      different reasons. Decomposition schema is `news-typing-decomposition-v3`.
- [ ] A current non-empty `GateVerdictId` and `Qualifies` determine pass/fail without parsing rendered
      reasons. Crafted baseline names cannot change the result or make status disagree with verdict id;
      the legacy no-id path remains fail-closed.
- [ ] The checked-in PowerShell fix skips every `_comment*` key. The real default `-WhatIf` path and the full
      NewsResearch strict guards are regression-tested; no `_comment*` argument reaches the Worker.
- [ ] Persisted typing/judgment attempts carry nullable provider duration, bounded progress logs show live
      throughput/failures, and deterministic final summaries show calls/p50/p95/max/total without content or
      secrets. Timing affects no identity or decision.
- [ ] The resolved default profile schedules DeepInfra DeepSeek only for shadow, typing and judgment.
      Ollama implementation/tests/history remain; Claude is absent.
- [ ] No score, rank, label, strategy, snapshot, scoring fingerprint or AD-15/AD-16 claim changes;
      `ScoringConfigFingerprintTests` and score golden pins remain untouched.
- [ ] `CLAUDE.md` and stale profile/code comments describe the shipped architecture accurately.
- [ ] `dotnet build Radar.sln -c Release` and the full serialized test suite are green; PowerShell parses and
      `run-radar.ps1 -Profile default -WhatIf` resolves cleanly.

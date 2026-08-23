# Task: Judgment/typing hardening — trajectory-honest markers, bounded typing retries, stable gate-verdict identity, temporal family ids

## Overview

An external review (2026-08-23, after specs 181/184/185 merged and the baseline promotion `3cd7020`) found
four real defects. All four are confirmed against the code; none touches a score, rank, label, fingerprint
or AD-15/AD-16 claim — this is a read-side/display-side hardening slice. Severity, honestly restated:

1. **A `Deteriorating` trajectory can render the reassuring dot.** `NewsJudgmentMarkerPolicy` maps every
   `Judged` + zero-findings record to `NoChallengeFound` without consulting `BusinessTrajectory`
   (`NewsJudgmentMarkerPolicy.cs:40-47`). "No challenge found" is an ABSENCE claim rendered while the same
   record carries contrary presence evidence — the omission-bias failure reborn one seam past where spec 185
   killed it, and the EOSE shape the marker exists to prevent. Live from the first baseline run.
2. **Failed typing records are retried forever inside the hosted-call budget.** The completed-only cache
   (`NewsTypingGenerator.cs:281-305`) means provider/parse/validation failures re-enter selection
   newest-first every run. ~200 persistently failing records would pin the whole `MaxNewTypingsPerRun` cap,
   burn hosted calls, and starve the backlog permanently. Probabilistic today (the pilot measured DeepSeek
   at a 0.0% validation-drop rate), structural nonetheless.
3. **The paired-gate verdict instant is the artifact's filesystem mtime**
   (`FileStrategyEvidenceFactsSource.cs:194`, `ArtifactWrittenAtUtc` ← `File.GetLastWriteTimeUtc`). The
   spec-184 reducer honours a human operating call over a gate verdict only when the call POSTDATES the
   verdict — and the efficacy artifacts are rewritten every run, so the mtime advances daily and a valid
   `overridesGate:true` call silently expires after one run; a copy/restore has the same effect. Dormant
   until gate verdicts can exist (first eligible paired support 2026-09-29, realistically much later), but
   it must be fixed BEFORE then — an override that quietly stops holding is a governance failure, not a bug.
4. **Two temporally separate fact families can share one FamilyId.** `FactFamilyBuilder` splits same-claim
   facts more than 7 days apart into separate families, but derives the id WITHOUT a temporal component
   (`FactFamilyBuilder.cs:177`). Recurring corporate news makes this concrete, not theoretical: quarterly
   dividend/buyback headlines produce near-identical normalized statements months apart — separate
   episodes, colliding ids, corrupted judgment provenance.

One slice, four bounded fixes. Nothing here is hashed into any scoring identity; the spec-148/160 pins do
not move.

## Assignment

Worktree: any
Dependencies: spec 185 merged (`ba8c1c4`); baseline promotion `3cd7020`.
Estimated time: ~1 day.

## 1. Trajectory-honest markers (fix 1)

The marker STATE vocabulary stays the closed 3-state set (challenged / unassessed / no-challenge-found —
spec 185 §4's "absent marker unrepresentable" invariant is untouched). Two changes, both in the pure policy
and its renderer, never in the validator:

- **`Deteriorating` never renders the dot.** `Judged` + zero findings + `BusinessTrajectory ==
  Deteriorating` maps to **`Challenged`** with the deterministic summary token
  `business-trajectory-deteriorating`. No finding is invented — the summary names the trajectory AXIS, and
  its provenance is the persisted judgment record (trajectory, rationale, consumed families).
- **Make the provenance claim TRUE (review round 2): the report does not currently link the judgment** —
  the row renders marker text only. The leaders section's diagnostic appendix therefore gains one line per
  judged row carrying the judgment id (and the judgments-store path root once, not per row), so every
  marker is traceable to its record instead of being asserted traceable. Cheap, additive, display-only.
- **Every `Judged` marker renders the trajectory token.** The rendered marker appends
  `· trajectory <token>` (`improving` / `deteriorating` / `mixed` / `unknown`) in BOTH `Judged` states,
  uniformly — not selectively for bad news, so the display is state-complete and the dot can never
  silently imply health. `Mixed`/`Unknown` + zero findings therefore stay `NoChallengeFound` (the round-1
  "fail validation / render unassessed" remedy stays REJECTED: a validated judgment exists, and
  `Unassessed` would be a false statement about the read — defensible precisely BECAUSE the trajectory
  token renders in the same cell).
- **A `Judged` record with a NULL persisted trajectory is an INVALID state, not an unknown one** (review
  round 2): the validator requires the trajectory token to parse, so null-under-`Judged` can only mean a
  corrupted or hand-edited record — it renders `? unassessed (invalid-record)`, never a dot. The genuine
  `Unknown` enum value remains a valid completed read and keeps the dot + token.

Do NOT change the validator: zero-findings-plus-`Deteriorating` is a legitimate, honest model output — the
spec-179 challenge taxonomy has no bucket for gradual decline, which is exactly why the trajectory axis
exists (spec 185 §2).

**Enum-zero sub-fix, spec-182 precedent:** `NewsJudgmentTrajectory.Improving = 0` makes the BEST state the
default value, inverting the house rule that enum zero is the degraded state ("persisted records can never
hydrate as best-state", spec 182). Reorder so `Unknown = 0` — first VERIFYING that trajectory persists as
tokens everywhere (the `NewsTypingTokens` parse path suggests it does); if any numeric persistence or wire
coupling exists, leave the order and add a hydration guard instead. State which branch was taken in the PR.

Tests: the existing `Mixed`-codifying policy test is updated, plus new cases pinning
`Deteriorating`+0 findings ⇒ `Challenged`, the token rendering in all `Judged` states, null-trajectory
under `Judged` ⇒ `unassessed (invalid-record)` (review round 3 — the round-2 text here still said
"unknown", contradicting the rule above; the genuine `Unknown` ENUM value is the case that renders the
`unknown` trajectory token), and the renderer output for a deteriorating zero-findings leader row.

## 2. Bounded typing retries (fix 2)

Attempt counts are DERIVED per `(cohortKey, observationId, payloadHash)` from the records the insert-only
store already holds and the generator already loads — no new store, no side index.

- **Attempt counting must bound HOSTED CALLS, not stored records (review round 3 — the existing typing
  identity includes `runId`, and a null `runId` maps every invocation onto one "standalone" identity, so
  re-running one run, or repeatedly invoking the supported null-run path, calls the model again while the
  insert-only store deduplicates the record and the derived count NEVER advances).** Two rules, both
  specified here so the identity is deliberate: (a) **same-run idempotency** — within one `runId`, an
  observation that already has a persisted attempt record for this cohort is SKIPPED, no model call; (b)
  **every legitimate standalone (null-run) invocation mints a distinct persisted attempt identity** (a
  per-invocation token folded into the record id), so each real hosted call leaves its own record and the
  derived attempt count advances by exactly one per call. Invariant, stated plainly: hosted calls for one
  `(cohort, observation, payload)` can never exceed `MaxTypingAttempts`, whatever mix of re-runs and
  standalone invocations occurs.
- **`Radar:NewsResearch:Typing:MaxTypingAttempts`**, default **3**, validated at the config boundary like
  its sibling limits (≥ 1; strict-key allowlist gains the one key).
- **A bounded RETRY LANE with FIFO fairness (review rounds 2+3 — round 1's strict ordering starved
  retries behind the 13k backlog; round 2's fewest-attempts-first lane still starved LATER attempts:
  with continuous fresh failures, the replenishing attempt-1 population keeps an attempt-2 record waiting
  indefinitely, so it neither retries nor exhausts).** Per run, per reader: reserve
  `min(MaxRetryTypingsPerRun, pending retries)` slots for retries (new key
  `Radar:NewsResearch:Typing:MaxRetryTypingsPerRun`, default **25**, **≥ 1** — zero would re-permit total
  retry starvation and is rejected — and < `MaxNewTypingsPerRun`); retries fill their lane ordered by
  **oldest last-attempt instant first** (then observation id — AD-3), so newly failed work queues BEHIND
  already-waiting work and the bound `ceil(pendingRetries / MaxRetryTypingsPerRun)` runs-to-reach holds
  for every record in a pending snapshot; UNUSED lane capacity returns to first attempts; first attempts
  fill the remainder window-newest-first then backlog-oldest-first as today. Neither lane can monopolize
  the cap, and no attempt tier can starve another, by construction.
- **Exhausted records (attempts ≥ max) leave selection and become visible, never silent:** a per-cohort
  `RetryExhausted` count on the run result and the decomposition artifact, and a company holding an
  exhausted untyped observation must never read `Complete` typing completeness (map it onto the existing
  degraded state with the fewest new moving parts — `Failed` is acceptable with its doc comment widened;
  a new token is acceptable only if the completeness enum's consumers all handle it — state the choice).
  Log one aggregated warning per cohort naming the count (the spec-145 aggregation precedent).
- **Schema honesty (review round 2 — round 1's "no schema change" was wrong):** `MaxTypingAttempts` and
  `MaxRetryTypingsPerRun` join the persisted limits record (`NewsTypingLimitsRecord`-equivalent — the
  contract that records the safety limits in force for every attempt), trailing + nullable so pre-186
  records hydrate as "not recorded", never as a fabricated limit; `RetryExhausted` joining the
  decomposition JSON is an ADDITIVE field and bumps the decomposition schema tag (follow the existing
  version-token pattern; readers are by-name, so existing consumers are unaffected — assert it). Name both
  bumps in the PR.

Tests: a persistently failing observation is attempted exactly `MaxTypingAttempts` times across simulated
runs and then excluded — **asserted on the counting fake extractor's HOSTED-CALL count, not only on stored
records** (round 3), including repeated invocation with the SAME `runId` (zero extra calls) and repeated
standalone invocations with `runId` null (each calls once, each persists distinctly, exhaustion still
trips at the cap); the retry lane caps retries under a full backlog AND the FIFO ordering guarantees a
pending retry is reached within `ceil(pendingRetries / MaxRetryTypingsPerRun)` runs even while NEW
failures keep arriving (the round-3 starvation case, pinned: an attempt-2 record is reached ahead of a
continuously replenishing attempt-1 population); `MaxRetryTypingsPerRun: 0` fails config validation;
unused lane capacity flows back to first attempts; the exhausted count and completeness degradation are
asserted; a later PAYLOAD change (new `payloadHash`) resets the attempt count (a different input, not a
retry); legacy records without the new limits fields hydrate as null.

## 3. Stable gate-verdict identity (fix 3)

Filesystem metadata leaves the verdict path entirely — and so do TIMESTAMPS (review round 2: round 1's
`verdictAsOf` = latest contributing score as-of date was wrong twice over. Forward outcomes arrive weeks
after the score date, so a human call made after the last as-of but BEFORE the exit prices existed would
falsely count as post-verdict; and the composite gate's AD-16 prerequisite can transition Pending →
Calculated with no paired as-of date changing at all. Time-comparing an override against a verdict is the
wrong primitive; identity-binding is the right one).

- **`gateVerdictId` — a semantic verdict identity, computed by the paired-comparison writer and carried as
  a run-level column in the artifact.** A content hash (shared hashing helper, reuse-over-copy) over, in a
  fixed canonical order: the gate CONTRACT identity (predeclared primary strategy, declared boundary, the
  gate-rule/reason-code vocabulary version), the ADMITTED purged outcome blocks (their dates and per-block
  inputs — the evidence the verdict rests on), the price-gate verdict itself, and the AD-16 prerequisite
  identity + outcome. Properties, each pinned by a test: an identical rewrite (or a file copy/restore)
  yields the SAME id; a new admitted outcome block yields a NEW id; an AD-16 prerequisite transition alone
  (no paired as-of change) yields a NEW id; the id is machine-independent and wall-clock-free (AD-3). When
  no verdict exists (pre-boundary, insufficient support), the column is EMPTY — there is nothing to
  override and the reducer already treats that as `GatePending`/accrual.
- **An override BINDS to the verdict it overrides, by name.** The operating-calls file schema gains
  `overridesVerdictId` (string), REQUIRED whenever `overridesGate: true` — the strict fail-closed
  `FileOperatingCallSource` rejects an override without it, naming file + rule (spec-184 style). The
  reducer honours an override iff its `overridesVerdictId` equals the artifact's current `gateVerdictId`.
  Timestamp comparison is DELETED from the reducer's override rule; `ArtifactWrittenAtUtc` and the
  `File.GetLastWriteTimeUtc` read go away. No live call carries `overridesGate` today, so nothing breaks.
- **This is a VERSIONED schema change, named as such (review round 3):** the reader accepts exactly
  `strategy-operating-calls-v1` and rejects unknown fields, so adding `overridesVerdictId` —
  conditionally required and semantically REPLACING timestamp precedence — is
  **`strategy-operating-calls-v2`**. The committed `data/strategy-operating-calls.json` migrates to v2 in
  this PR (a mechanical version-token bump; no call in it carries an override). Legacy v1 REMAINS
  readable — v1 simply cannot express an override, so a v1 file containing `overridesGate: true` fails
  validation naming the remedy ("migrate to strategy-operating-calls-v2 and bind the override to a
  verdict id"); a v1 file without overrides behaves exactly as today. Both accepted versions and the
  v1-with-override rejection are pinned by tests.
- **A stale override is REPORTED, never silently dropped:** when an override names a verdict id that no
  longer matches, the lifecycle section renders one line naming the call, the id it bound to, and the
  current id — the gate default re-arms (new evidence SHOULD re-open the call), and the maintainer can see
  exactly why and re-declare against the new id.
- **Pre-186 artifact (no `gateVerdictId` column):** verdict identity unknown ⇒ no override can match ⇒
  gate default wins, ONE warning naming the artifact and the remedy ("re-run efficacy to refresh").
  Fail-closed in the safe direction, self-heals next run. AD-8 preserved: unknown never fabricates an id.
- Sweep the facts source and the status calculator for any OTHER consumer of artifact mtime and give each
  the same treatment (this also closes the spec-184 reviewer note that the verdict mtime was
  machine-dependent — say so in the PR).
- CSV schema: one additive run-level column, readers are by-header-name (`TryColumn`) so existing readers
  are unaffected — assert it; bump the CSV schema tag if the artifact carries one (spec-183 precedent).

Tests: identical-content rewrite and copy/restore change nothing; a matching override holds across those;
a new admitted block re-arms the gate and surfaces the stale-override line; an AD-16 prerequisite
transition ALONE re-arms it (the case round 1's date anchor missed); `overridesGate` without
`overridesVerdictId` fails call-file validation; the missing-column path warns once and fails toward the
gate default.

## 4. `fact-family-v2` — temporal anchor in the id (fix 4)

Per spec 181 §4's own rule, ANY change to the family identity inputs is a NEW builder version and a new
cohort dimension — never an edit. So:

- **`fact-family-v2`: the temporal anchor must be DURABLE, so episode assignment runs over the FULL
  accrued fact history, not the checkpoint window (review round 2 — round 1's "earliest member" anchor was
  still mutable in the ordinary case: families rebuild over a rolling window each checkpoint, so when an
  episode's earliest member ages OUT of the window the anchor would advance and the id would churn run
  after run, turning the promised one-time re-judge into repeated cache churn).** Rules:
  - **TWO STAGES, because durable identity and the window representative are DIFFERENT jobs (review
    round 3 — with one stage, the first-ever member doubles as `RepresentativeFactId`, and
    `NewsJudgmentInputBuilder` drops any family whose representative is absent from the current-window
    fact index: once the anchor fact ages out, a family with FRESH news silently disappears from
    judgment — the exact opposite of what this fix is for).** Stage 1, SEGMENTATION over ALL qualifying
    validated facts in the store (bounded and cheap — the hydrated fact store is already in memory),
    **preserving v1's membership algorithm verbatim**: representative-relative similarity within the
    7-day temporal proximity rule, exactly as `FactFamilyBuilder` groups today — NOT exact-canonical-key
    grouping and NOT transitive chaining (round 3: the round-2 phrases "per canonical claim key" and
    "proximity chaining" would each have been a behavioural change to membership; membership semantics do
    not change in this spec, only identity and projection do). Stage 1 yields each episode's DURABLE
    identity anchor: the first-ever member's `firstObservedAtUtc` UTC date plus that member's sorted
    `EventTypes` (two same-statement families with DISJOINT types are different families — round 2's
    point, kept), both immutable under window expiry by construction (facts are append-only).
  - Stage 2, **PROJECTION onto the checkpoint window**: each episode with ≥ 1 in-window member enters the
    snapshot carrying the durable `FamilyId` from stage 1, while `RepresentativeFactId`, member counts,
    distinct-publisher counts and the supplied content are all derived from the IN-WINDOW members only
    (representative = v1's rule applied to the projection: earliest `firstObservedAtUtc`, then lowest
    FactId). What a checkpoint MEANS — window families, window metadata — is unchanged; only the id
    survives from full history.
  - All of this — history-wide segmentation, anchor rule, event-type fold — is part of `fact-family-v2`'s
    identity per spec 181 §4's "the full builder definition enters the cohort identity".
- **Documented, accepted caveat (now the ONLY id-shift case):** a late-arriving member that is temporally
  EARLIER than every member of its episode ever observed shifts the anchor, so the family id moves at the
  next checkpoint (old snapshots immutable and untouched — append-only). Rare (facts arrive roughly in
  time order; the syndication tail lands inside the window and changes nothing) and honest; record it on
  the builder's doc comment.
- v1 checkpoints on disk stay exactly as they are; v2 snapshots build fresh from ALL qualifying facts on
  the first post-186 run. Cohorts never pool across builder versions (spec 181's rule) — the judgment
  cache keys on the family-set content, so affected companies re-judge once under the new identity, bounded
  by `MaxCompaniesPerRun`. State this expected one-time re-judge cost in the PR.
- Fixtures: the five spec-181 §4 pinned fixtures re-pin under v2, PLUS three new ones — (a) two
  byte-identical normalized statements >7 days apart produce two families with two DISTINCT ids; (b)
  **window-expiry stability**: a later checkpoint whose window no longer contains the episode's earliest
  member keeps the SAME family id (the round-2 churn case, pinned); (c) same statement, disjoint event
  types ⇒ distinct ids; (d) **end-to-end through the judge** (round 3): a family whose identity anchor
  fact is OUTSIDE the checkpoint window but which has fresh in-window members is projected with an
  in-window `RepresentativeFactId` and REACHES `NewsJudgmentInputBuilder`'s output — not dropped; (e) a
  membership-parity fixture pinning that v2 groups a v1 fixture set into the SAME member partitions v1
  does (only ids differ). A rerun over identical facts stays byte-deterministic.

## 5. Out of scope, recorded not built

- Taxonomy v2, judge prompt/schema changes, a support taxonomy, any scoring consumption of typing —
  unchanged from specs 181/185's scope lines.
- The standalone typing catch-up command (still deferred; the in-run backlog phase plus §2's retry lane is
  the mechanism).
- The `InsufficientContent`-facts-excluded-from-families call (still standing as shipped; revisit only
  with evidence).
- Retro-editing any persisted v1 family snapshot, typing record or judgment (append-only, AD-8).

## Acceptance criteria

- [ ] A `Judged` record with zero findings and `Deteriorating` trajectory renders `⚠` — never the dot; all
      `Judged` markers carry the trajectory token; null-trajectory-under-`Judged` renders
      `unassessed (invalid-record)`; the validator is untouched; every judged row's judgment id is
      traceable from the report.
- [ ] HOSTED CALLS for one `(cohort, observation, payload)` never exceed `MaxTypingAttempts` under any
      mix of same-run re-invocation and standalone (null-run) invocation — asserted on call counts, not
      stored records; the FIFO retry lane (≥ 1, never 0) guarantees no attempt tier starves another;
      exhaustion degrades completeness and is counted; the limits-record and decomposition schema bumps
      are named.
- [ ] No verdict consumer reads filesystem mtime or compares timestamps; an override binds to a
      `gateVerdictId`, survives identical-content rewrites and copies, and re-arms — visibly, via the
      stale-override line — on any new admitted evidence OR an AD-16 prerequisite transition alone; the
      operating-calls schema is `strategy-operating-calls-v2` with the committed file migrated and
      v1-without-overrides still readable.
- [ ] `fact-family-v2` separates durable identity (full-history anchor) from the window projection
      (representative, counts, publishers, content — in-window only); a fresh-news family with an aged-out
      anchor still reaches the judge; membership semantics are byte-compatible with v1 (parity fixture);
      temporally separate episodes get distinct ids stable under window expiry; disjoint-event-type
      families never share an id; v1 data untouched.
- [ ] No score, label, rank, fingerprint, snapshot field, or AD-15/AD-16 claim changes; the spec-148/160
      pins stand; `ScoringConfigFingerprintTests` untouched.
- [ ] Build and full test suite green.

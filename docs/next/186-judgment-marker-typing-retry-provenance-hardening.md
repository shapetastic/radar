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
`Deteriorating`+0 findings ⇒ `Challenged`, the token rendering in all `Judged` states, null-trajectory ⇒
`unknown`, and the renderer output for a deteriorating zero-findings leader row.

## 2. Bounded typing retries (fix 2)

Attempt counts are DERIVED per `(cohortKey, observationId, payloadHash)` from the records the insert-only
store already holds and the generator already loads — no new store, no side index.

- **`Radar:NewsResearch:Typing:MaxTypingAttempts`**, default **3**, validated at the config boundary like
  its sibling limits (≥ 1; strict-key allowlist gains the one key).
- **A bounded RETRY LANE, not a strict ordering (review round 2 — round 1's "all first-attempts before all
  retries" just inverts the starvation: behind the 13k backlog, a transiently failed CURRENT LEADER would
  wait ~65 runs for its second attempt).** Per run, per reader: reserve
  `min(MaxRetryTypingsPerRun, pending retries)` slots for retries (new key
  `Radar:NewsResearch:Typing:MaxRetryTypingsPerRun`, default **25**, ≥ 0, must be < `MaxNewTypingsPerRun`);
  retries fill their lane ordered fewest-attempts first, then oldest `FirstObservedAtUtc`, then observation
  id (AD-3); UNUSED lane capacity returns to first attempts; first attempts fill the remainder
  window-newest-first then backlog-oldest-first as today. Neither lane can monopolize the cap, by
  construction, in either direction.
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
runs and then excluded; the retry lane caps retries under a full backlog AND guarantees a pending retry is
reached within `ceil(pendingRetries / MaxRetryTypingsPerRun)` runs regardless of fresh volume; unused lane
capacity flows back to first attempts; the exhausted count and completeness degradation are asserted; a
later PAYLOAD change (new `payloadHash`) resets the attempt count (a different input, not a retry); legacy
records without the new limits fields hydrate as null.

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
  - Episode SEGMENTATION (the 7-day proximity chaining) is computed per (company, capture mode, canonical
    claim key) over ALL qualifying validated facts in the store — bounded and cheap, the hydrated fact
    store is already in memory. The id's temporal anchor is the episode's FIRST-EVER member's
    `firstObservedAtUtc` UTC date, which is immutable under window expiry by construction (facts are
    append-only and a fact's first-observed instant never changes).
  - The CHECKPOINT snapshot still contains only families with ≥ 1 member in the checkpoint window — what a
    checkpoint MEANS is unchanged; only where the identity anchor comes from widened.
  - **The id also folds the episode's representative event-type set** (the sorted `EventTypes` of that same
    first-ever member): membership requires overlapping types, so two same-statement families with
    DISJOINT types are different families and must not share an id (review round 2's second point).
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
  types ⇒ distinct ids. A rerun over identical facts stays byte-deterministic.

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
- [ ] A persistently failing typing input stops consuming budget after `MaxTypingAttempts`, visibly;
      the bounded retry lane guarantees NEITHER first attempts nor retries can be starved; exhaustion
      degrades completeness and is counted; the limits-record and decomposition schema bumps are named.
- [ ] No verdict consumer reads filesystem mtime or compares timestamps; an override binds to a
      `gateVerdictId`, survives identical-content rewrites and copies, and re-arms — visibly, via the
      stale-override line — on any new admitted evidence OR an AD-16 prerequisite transition alone.
- [ ] `fact-family-v2` gives temporally separate episodes distinct ids that are STABLE under window
      expiry; disjoint-event-type families never share an id; v1 data untouched; all family fixtures
      pinned under v2 including the collision, window-expiry and disjoint-type cases.
- [ ] No score, label, rank, fingerprint, snapshot field, or AD-15/AD-16 claim changes; the spec-148/160
      pins stand; `ScoringConfigFingerprintTests` untouched.
- [ ] Build and full test suite green.

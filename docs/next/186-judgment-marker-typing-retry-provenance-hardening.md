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
  provenance is the judgment record itself (its trajectory, rationale and cited facts), which the row
  already links.
- **Every `Judged` marker renders the trajectory token.** The rendered marker appends
  `· trajectory <token>` (`improving` / `deteriorating` / `mixed` / `unknown`; a null trajectory renders
  `unknown`) in BOTH `Judged` states, uniformly — not selectively for bad news, so the display is
  state-complete and the dot can never silently imply health. `Mixed`/`Unknown` + zero findings therefore
  stay `NoChallengeFound` (the reviewer's "fail validation / render unassessed" remedy is REJECTED:
  a validated judgment exists, and `Unassessed` would be a false statement about the read — the honest fix
  is showing the axis, not suppressing the verdict).

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

No schema change: the insert-only typing store already holds every failed attempt, so attempt counts are
DERIVED per `(cohortKey, observationId, payloadHash)` from the records the generator already loads.

- **`Radar:NewsResearch:Typing:MaxTypingAttempts`**, default **3**, validated at the config boundary like
  its sibling limits (≥ 1; strict-key allowlist gains the one key).
- **Selection order becomes: first-attempts before retries, structurally.** Window first-attempts (newest
  first) → backlog first-attempts (oldest first) → retries (attempts in [1, max), ordered fewest-attempts
  first, then oldest `FirstObservedAtUtc`, then observation id — AD-3), all under the single existing
  `MaxNewTypingsPerRun` cap. A run full of fresh observations does zero retries; retries can never starve a
  first attempt, in either direction of the old failure.
- **Exhausted records (attempts ≥ max) leave selection and become visible, never silent:** a per-cohort
  `RetryExhausted` count on the run result and the decomposition artifact, and a company holding an
  exhausted untyped observation must never read `Complete` typing completeness (map it onto the existing
  degraded state with the fewest new moving parts — `Failed` is acceptable with its doc comment widened;
  a new token is acceptable only if the completeness enum's consumers all handle it — state the choice).
  Log one aggregated warning per cohort naming the count (the spec-145 aggregation precedent).

Tests: a persistently failing observation is attempted exactly `MaxTypingAttempts` times across simulated
runs and then excluded; retries never displace a first attempt under a tight cap; the exhausted count and
completeness degradation are asserted; a later PAYLOAD change (new `payloadHash`) resets the attempt count
(it is a different input, not a retry).

## 3. Stable gate-verdict identity (fix 3)

Filesystem metadata leaves the verdict path entirely.

- **The paired-comparison artifact carries its own verdict instant.** The writer adds a run-level column
  `verdictAsOf`: the latest as-of DATE among the observations that produced the gate evaluation
  (data-derived and deterministic — byte-stable when the artifact is rewritten from identical data, and it
  moves exactly when new outcome data arrives, which is precisely when a standing override SHOULD come up
  for re-examination). When the gate has no eligible support (every pre-boundary run), the field is empty —
  there is no verdict, and the reducer already treats that as `GatePending`/accrual.
- **`FileStrategyEvidenceFactsSource` reads `verdictAsOf` and stops calling `File.GetLastWriteTimeUtc`.**
  A post-186 artifact missing the column (i.e. any pre-186 artifact): the verdict instant is UNKNOWN — an
  override cannot be proven to postdate it, so the gate default wins, with ONE warning naming the artifact
  and the remedy ("re-run efficacy to refresh the artifact"). This is fail-closed in the safe direction,
  matches today's practical behaviour, and self-heals on the next run since the artifacts rewrite every
  run. AD-8's "cannot tell must not read as changed" is preserved: unknown never fabricates an instant.
- Sweep the facts source and the status calculator for any OTHER consumer of artifact mtime and give each
  the same treatment (the spec-184 reviewer note said the mtime was machine-dependent; this closes that
  note too — say so in the PR).
- CSV schema: the paired-comparison CSV gains one column; per the spec-183 precedent, bump its schema tag
  if it carries one, and keep the column ADDITIVE (existing readers by-name are unaffected — verify the
  facts source reads by header name, which `TryColumn` shows it does).

Tests: an override postdating the verdict holds across an artifact rewrite with identical data (the exact
failure: rewrite must NOT resurrect the gate default); new outcome data (later `verdictAsOf`) correctly
re-arms the gate default; the missing-column path warns once and fails toward the gate default; a
copied/restored artifact (fresh mtime, same content) changes nothing.

## 4. `fact-family-v2` — temporal anchor in the id (fix 4)

Per spec 181 §4's own rule, ANY change to the family identity inputs is a NEW builder version and a new
cohort dimension — never an edit. So:

- **`fact-family-v2`**: the id derivation adds the episode's temporal anchor — the UTC DATE of the family's
  earliest member's `firstObservedAtUtc` — alongside the existing builder-version + company + capture-mode +
  canonical-claim inputs. Two same-claim episodes split by the 7-day rule now get DISTINCT ids by
  construction.
- **Documented, accepted caveat:** a late-arriving member that is temporally EARLIER than every existing
  member of its episode shifts the anchor date, so the family id moves at the next checkpoint (old
  snapshots are immutable and untouched — append-only). This is rare (facts arrive roughly in time order;
  the common late arrival is the syndication tail, which lands inside the window and changes nothing) and
  honest; record it on the builder's doc comment.
- v1 checkpoints on disk stay exactly as they are; v2 snapshots build fresh from ALL qualifying facts on
  the first post-186 run. Cohorts never pool across builder versions (spec 181's rule) — the judgment
  cache keys on the family-set content, so affected companies re-judge once under the new identity, bounded
  by `MaxCompaniesPerRun`. State this expected one-time re-judge cost in the PR.
- Fixtures: the five spec-181 §4 pinned fixtures re-pin under v2, PLUS the new collision fixture — two
  byte-identical normalized statements >7 days apart produce two families with two DISTINCT ids, and a
  rerun is byte-deterministic.

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
      `Judged` markers carry the trajectory token; the validator is untouched.
- [ ] A persistently failing typing input stops consuming budget after `MaxTypingAttempts`, visibly; first
      attempts structurally precede retries; exhaustion degrades completeness and is counted.
- [ ] No verdict-instant consumer reads filesystem mtime; an override survives an identical-content
      artifact rewrite and is re-examined exactly when new outcome data arrives.
- [ ] `fact-family-v2` gives temporally separate episodes distinct ids; v1 data untouched; all family
      fixtures pinned under v2 including the recurring-event collision case.
- [ ] No score, label, rank, fingerprint, snapshot field, or AD-15/AD-16 claim changes; the spec-148/160
      pins stand; `ScoringConfigFingerprintTests` untouched.
- [ ] Build and full test suite green.
